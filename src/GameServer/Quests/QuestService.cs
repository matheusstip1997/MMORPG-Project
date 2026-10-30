using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Net;
using GameServer.Outposts;
using GameServer.Progression;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Util;

namespace GameServer.Quests;

/// <summary>
/// Outpost quests and runs (R-5.5-01..03, spec 5.5; WP-M2-6), on the world thread.
/// <list type="bullet">
/// <item>The interaction kind quests on the <see cref="OutpostInteractions"/> registry: its gate (NPC, range, <c>OutOfRange</c>)
/// runs first, then the outpost's cached board (<see cref="QuestBoards"/>) goes to the player.</item>
/// <item>Run requests (phase 1) pass the same gate for kind quests. Accept builds a run from offers of the current board (rewards
/// locked, R-5.5-02); deliver hands in the items of a deliver or return step through <see cref="InventoryService"/> (the only
/// writer of items, rule 4; keys <c>{op}:items:{slot}</c>, reason <c>QuestDelivery</c>, an item sink) and advances the step in
/// the same call, so the removal and the progress ride one save; forfeit gives a step up with the penalty; claim pays the run's
/// exp through <see cref="ExpService.Grant"/> (the only exp entry for other sources), counts "runs claimed" through
/// <see cref="AchievementService.Record"/> and is answered once its save committed (rule 5).</item>
/// <item>Kill steps advance from <see cref="Killed"/> (phase 11, idempotent per event id, <see cref="EventWatermark"/>): the
/// killer's first active kill step of that monster type (one kill counts once, so stacked kill offers of one type add up). A
/// kill also drops the quest item of the active return steps of that type straight into the Quest bag (100 %), until the bag
/// holds what they still need.</item>
/// <item>Operation keys <c>quest:{runId}:{entityId}:{connection}:{requestId}</c>: unique for ever, and the same for a repeated
/// request on one connection, which gets the first answer and changes nothing (double submit).</item>
/// </list>
/// Events: <see cref="QuestStepDone"/>, <see cref="QuestForfeited"/>, <see cref="RunClaimed"/>, <see cref="ItemDelivered"/>.
/// </summary>
public sealed class QuestService : IOutpostInteractionHandler, IEventConsumer<Killed>
{
    private readonly RunStore _runs;
    private readonly QuestBoards _boards;
    private readonly OutpostInteractions _gate;
    private readonly InventoryService _inventory;
    private readonly ExpService _exp;
    private readonly AchievementService? _achievements;
    private readonly EventBus? _events;
    private readonly IClock _clock;
    private readonly string _runId;
    private readonly Func<Guid> _newRunId;
    private readonly Dictionary<string, Waiting> _waiting = new(StringComparer.Ordinal);
    private EventWatermark _watermark;

    /// <param name="runId">Unique per world run, so operation keys never repeat across restarts.</param>
    /// <param name="newRunId">Ids of new runs (database keys); a new GUID each when null.</param>
    public QuestService(RunStore runs, QuestBoards boards, OutpostInteractions gate, InventoryService inventory, ExpService exp,
        AchievementService? achievements, EventBus? events, IClock clock, string runId, Func<Guid>? newRunId = null)
    {
        _runs = runs;
        _boards = boards;
        _gate = gate;
        _inventory = inventory;
        _exp = exp;
        _achievements = achievements;
        _events = events;
        _clock = clock;
        _runId = runId;
        _newRunId = newRunId ?? Guid.NewGuid;
    }

    /// <summary>Claims waiting for their save.</summary>
    public int WaitingCount => _waiting.Count;

    public void Subscribe(EventBus events) => events.Subscribe<Killed>(this);

    public string OpKey(Player player, uint requestId) => $"quest:{_runId}:{player.Id}:{player.Connection}:{requestId}";

    /// <summary>Opening the board: the outpost's current offers go to the player with the yes.</summary>
    public OutpostReason? Interact(Player player, Npc npc, uint requestId)
    {
        long now = _clock.UtcNowMs;
        if (_boards.Current(npc.Id, now) is not { } board)
            return OutpostReason.InteractionUnavailable;
        player.Quests.Boards.Add(_boards.Message(board, now));
        return OutpostReason.None;
    }

    /// <summary>Phase 1: one <see cref="QuestRunRequest"/>; a claim is answered once committed, everything else at once.</summary>
    internal void Handle(Player player, in ClientRequest request)
    {
        long characterId = player.CharacterId;
        string opKey = OpKey(player, request.RequestId);
        if (_runs.TryGetSeen(characterId, opKey, out var first))
        {
            // Double submit: the first answer again, nothing changes.
            if (first.Action != QuestRunAction.Claim || _runs.IsCommitted(characterId, opKey))
                player.Quests.Results.Add(first);
            return;
        }

        var answer = new QuestRunResult { RequestId = request.RequestId, Action = request.QuestAction, StepIndex = request.FromSlot };
        var reason = _gate.CheckReach(player, request.DropEntityId, NpcInteraction.Quests, out var npc);
        if (reason == OutpostReason.None && !_runs.IsAttached(characterId))
            reason = OutpostReason.InteractionUnavailable;
        if (reason == OutpostReason.None)
        {
            reason = request.QuestAction switch
            {
                QuestRunAction.Accept => Accept(player, npc!, request, opKey, answer),
                QuestRunAction.Deliver => Deliver(player, request.FromSlot, opKey, answer),
                QuestRunAction.Forfeit => Forfeit(player, request.FromSlot, opKey, answer),
                QuestRunAction.Claim => Claim(player, opKey, answer),
                _ => OutpostReason.InteractionUnavailable,
            };
        }
        answer.Reason = reason;
        if (reason != OutpostReason.None || request.QuestAction != QuestRunAction.Claim)
            player.Quests.Results.Add(answer);
    }

    private OutpostReason Accept(Player player, Npc npc, in ClientRequest request, string opKey, QuestRunResult answer)
    {
        long characterId = player.CharacterId;
        if (_runs.Active(characterId) != null)
            return OutpostReason.RunAlreadyActive;
        int count = request.Count;
        if (count < 1 || count > _runs.Rules.Caps.MaxStepsPerRun)
            return OutpostReason.TooManySteps;
        long now = _clock.UtcNowMs;
        if (_boards.Current(npc.Id, now) is not { } board)
            return OutpostReason.InteractionUnavailable;
        if (board.Epoch != request.BoardEpoch)
        {
            player.Quests.Boards.Add(_boards.Message(board, now)); // the board that replaced it
            return OutpostReason.BoardExpired;
        }
        var offers = new List<BoardOffer>(count);
        for (int i = 0; i < count; i++)
        {
            byte slot = request.Slot(i);
            for (int j = 0; j < i; j++)
            {
                if (request.Slot(j) == slot)
                    return OutpostReason.TooManySteps; // each offer once
            }
            if (slot >= board.Offers.Count)
                return OutpostReason.OfferNotFound;
            offers.Add(board.Offers[slot]);
        }
        _runs.Accept(characterId, _newRunId(), npc.MarkerId, board.Epoch, offers, now, opKey, answer);
        return OutpostReason.None;
    }

    private OutpostReason Deliver(Player player, int stepIndex, string opKey, QuestRunResult answer)
    {
        long characterId = player.CharacterId;
        var reason = ActiveStep(characterId, stepIndex, out var run, out var step);
        if (reason != OutpostReason.None)
            return reason;
        if (step!.Kind == QuestKind.Kill)
            return OutpostReason.StepNotActive;
        if (!_inventory.IsAttached(characterId))
            return OutpostReason.InventoryUnavailable;
        if (!_inventory.Rules.TryGetItem(step.ItemNetId, out var item) || _inventory.Rules.ReceiverOf(item.Category) is not { } receiver
            || !_inventory.CurrentInventory(characterId).TryIndex((byte)receiver.Code, out int index))
            return OutpostReason.NothingToDeliver;

        // In slot order (rule 6), at most what the step misses. Each slot is read before its removal, so a removal fails only
        // through a bug; then the delivery stops there and counts what was handed in (the removals so far are in the same
        // save as that progress), never an exception on the world thread.
        // Reserved units (a craft holds them, spec 4.1 reservations, WP-M3-2) are never handed in.
        var bag = _inventory.CurrentInventory(characterId).BagAt(index);
        int missing = step.Missing, delivered = 0;
        bool reserved = false;
        string source = $"run:{run!.RunId:N}:{stepIndex}";
        for (int slot = 0; slot < bag.SlotCount && delivered < missing; slot++)
        {
            var stack = bag[slot];
            if (stack.IsEmpty || stack.ItemNetId != item.NetId)
                continue;
            int free = _inventory.Unreserved(characterId, new Shared.Simulation.Items.InventoryLocation((byte)receiver.Code, slot));
            reserved |= free < stack.Count;
            int take = Math.Min(missing - delivered, free);
            if (take <= 0)
                continue;
            if (!_inventory.TryRemove(characterId, receiver.Code, slot, take, $"{opKey}:items:{slot}", InventoryReason.QuestDelivery, source))
                break;
            delivered += take;
        }
        if (delivered == 0)
            return reserved ? OutpostReason.ItemReserved : OutpostReason.NothingToDeliver;
        answer.Count = (ushort)delivered;
        bool done = _runs.Advance(characterId, stepIndex, delivered, opKey, answer);
        _events?.Publish(new ItemDelivered(characterId, run.RunId, stepIndex, (ushort)item.NetId, delivered), characterId,
            player.RegionIndex);
        if (done)
            PublishStepDone(characterId, run, step, player.RegionIndex);
        return OutpostReason.None;
    }

    private OutpostReason Forfeit(Player player, int stepIndex, string opKey, QuestRunResult answer)
    {
        long characterId = player.CharacterId;
        var reason = ActiveStep(characterId, stepIndex, out var run, out _);
        if (reason != OutpostReason.None)
            return reason;
        _runs.Forfeit(characterId, stepIndex, opKey, answer);
        _events?.Publish(new QuestForfeited(characterId, run!.RunId, stepIndex, _runs.Rules.PenaltyPercent(run.ForfeitedSteps)),
            characterId, player.RegionIndex);
        return OutpostReason.None;
    }

    private OutpostReason Claim(Player player, string opKey, QuestRunResult answer)
    {
        long characterId = player.CharacterId;
        if (_runs.Active(characterId) is not { } run)
            return OutpostReason.NoActiveRun;
        if (run.State != Shared.Protocol.RunState.Completed)
            return OutpostReason.RunNotComplete;
        int completed = run.CompletedSteps, forfeited = run.ForfeitedSteps;
        long exp = _runs.Claim(characterId, opKey, answer);
        // The exp and the counter change in this same call, so the claim, its exp and the counter ride the save it asked for.
        _exp.Grant(characterId, player.Id, exp, player.RegionIndex);
        _achievements?.Record(characterId, AchievementCategory.RunsClaimed, run.RunId.ToString("N"));
        _events?.Publish(new RunClaimed(characterId, run.RunId, completed, forfeited, _runs.Rules.BonusPercent(completed),
            _runs.Rules.PenaltyPercent(forfeited), exp), characterId, player.RegionIndex);
        _waiting.TryAdd(opKey, new Waiting(characterId, player.Connection, answer));
        return OutpostReason.None;
    }

    /// <summary>
    /// A save committed the claim <paramref name="opKey"/>: answer it, if the connection that asked is still the player's (a new
    /// connection gets its run in a new <see cref="RunStatus"/>).
    /// </summary>
    public void ClaimCommitted(Player? player, string opKey)
    {
        if (!_waiting.Remove(opKey, out var waiting))
            return;
        if (player != null && player.CharacterId == waiting.CharacterId && player.Connection == waiting.Connection)
            player.Quests.Results.Add(waiting.Answer);
    }

    /// <summary>The character left the world: its unanswered claims have no one to answer.</summary>
    public void Forget(long characterId)
    {
        if (_waiting.Count == 0)
            return;
        foreach (var (key, waiting) in _waiting.ToArray())
        {
            if (waiting.CharacterId == characterId)
                _waiting.Remove(key);
        }
    }

    /// <summary>
    /// Phase 11: one kill counts once. It advances the first active kill step of that monster type (run order), so two "kill 5
    /// demons" steps need ten kills; and it drops one quest item for the return steps of that type while the bag holds less than
    /// they still need together.
    /// </summary>
    public void Handle(in EventEnvelope envelope, in Killed payload)
    {
        if (!_watermark.TryAccept(envelope))
            return; // a re-dispatched kill counts once
        long characterId = envelope.ActorCharacterId;
        if (characterId == 0 || payload.VictimCharacterId != 0 || _runs.Active(characterId) is not { } run
            || run.State != Shared.Protocol.RunState.Active)
            return;
        RunStep? kill = null, returned = null;
        int returnMissing = 0;
        foreach (var step in run.Steps) // index order (rule 6)
        {
            if (step.State != RunStepState.Active || step.MonsterNetId != payload.VictimDefId)
                continue;
            if (step.Kind == QuestKind.Kill)
                kill ??= step;
            else if (step.Kind == QuestKind.ReturnItem && (returned == null || returned.ItemNetId == step.ItemNetId))
            {
                returned ??= step;
                returnMissing += step.Missing;
            }
        }
        if (kill != null && _runs.Advance(characterId, kill.Index, 1))
            PublishStepDone(characterId, run, kill, envelope.RegionId);
        if (returned != null)
            DropQuestItem(characterId, run, returned, returnMissing, envelope.EventId);
    }

    /// <summary>One quest item into the Quest bag (100 %) while the bag holds less than <paramref name="missing"/>.</summary>
    private void DropQuestItem(long characterId, Run run, RunStep step, int missing, long eventId)
    {
        if (!_inventory.IsAttached(characterId) || !_inventory.Rules.TryGetItem(step.ItemNetId, out var item)
            || Carried(characterId, item) >= missing)
            return;
        _inventory.TryAdd(characterId, item, VendorHandler.VendorRarity, 1, $"qdrop:{_runId}:{run.RunId:N}:{step.Index}:{eventId}",
            InventoryReason.QuestDrop, $"monster:{step.MonsterDefId}");
    }

    /// <summary>Units of <paramref name="item"/> in the container that receives its category, as the world sees it now.</summary>
    private int Carried(long characterId, ItemDef item)
    {
        if (_inventory.Rules.ReceiverOf(item.Category) is not { } receiver
            || !_inventory.CurrentInventory(characterId).TryIndex((byte)receiver.Code, out int index))
            return 0;
        var bag = _inventory.CurrentInventory(characterId).BagAt(index);
        int count = 0;
        for (int slot = 0; slot < bag.SlotCount; slot++)
        {
            if (!bag[slot].IsEmpty && bag[slot].ItemNetId == item.NetId)
                count += bag[slot].Count;
        }
        return count;
    }

    private OutpostReason ActiveStep(long characterId, int stepIndex, out Run? run, out RunStep? step)
    {
        step = null;
        run = _runs.Active(characterId);
        if (run == null)
            return OutpostReason.NoActiveRun;
        if (stepIndex >= run.Steps.Count)
            return OutpostReason.StepNotFound;
        step = run.Steps[stepIndex];
        return step.State == RunStepState.Active ? OutpostReason.None : OutpostReason.StepNotActive;
    }

    private void PublishStepDone(long characterId, Run run, RunStep step, int regionId) =>
        _events?.Publish(new QuestStepDone(characterId, run.RunId, step.Index, step.Kind, step.Required), characterId, regionId);

    private readonly record struct Waiting(long CharacterId, int Connection, QuestRunResult Answer);
}
