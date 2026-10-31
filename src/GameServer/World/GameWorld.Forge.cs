using GameServer.Combat;
using GameServer.Crafting;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Jobs;
using GameServer.Net;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Jobs;

namespace GameServer.World;

/// <summary>
/// The forge in the world (spec 5.3, 5.2 R-5.2-04, 9.13; WP-M3-6): <see cref="ForgeService"/> runs every character's session;
/// the world reads the forge requests in phase 1 (the station gate, combat), ends a running session when its character
/// leaves the station or enters combat (phase 8), announces a committed result on the bus (<c>Crafted</c>, <c>Refined</c>,
/// <c>Converted</c>, <c>ForgeCompleted</c>) and to the player, and sends the forge traffic in phase 10. Without the forge
/// tables or the item tables there are no sessions (worlds built for tests).
/// </summary>
public sealed partial class GameWorld
{
    private ForgeService? _forge;
    private ForgeStationInteraction? _station;
    private ForgeSender? _forgeSender;
    private int _forgeCombatStateMs;

    /// <summary>The forge sessions of the world's characters (WP-M3-6); null without the forge or item tables.</summary>
    public ForgeService? Forge => _forge;

    /// <summary>The forge minigame (WP-M3-10 sets the first; until then none).</summary>
    public IForgeChallenge ForgeChallenge { get; set; } = NoForgeChallenge.Instance;

    private void BuildForge(ForgeRules? rules, ulong rngSeed, int combatStateMs)
    {
        _forgeSender = new ForgeSender(_transport);
        if (rules == null || _inventory == null)
            return;
        _forgeCombatStateMs = combatStateMs;
        _forge = new ForgeService(rules, _inventory, _jobs, new LateJobBonuses(this), new LateChallenge(this), () => _clock.UtcNowMs,
            rngSeed)
        {
            OpCommitted = OnForgeOpCommitted,
        };
        JobActivityGates.Add(_forge);
    }

    /// <summary>Registers the stations with the outposts (after <see cref="BuildEconomy"/>, which makes the registry).</summary>
    private void RegisterForgeStations()
    {
        if (_forge == null || Outposts == null)
            return;
        _station = new ForgeStationInteraction(_forge);
        Outposts.Register(NpcInteraction.Forge, _station);
        Outposts.Register(NpcInteraction.JewelerBench, _station);
    }

    /// <summary>Phase 1: one forge request of <paramref name="player"/>; true when <paramref name="request"/> was one.</summary>
    private bool HandleForgeRequest(Player player, in ClientRequest request)
    {
        switch (request.Kind)
        {
            case ReliableMessageId.ForgeStart:
                player.Forge.States.Add(StartForge(player, request));
                return true;
            case ReliableMessageId.ForgeAction:
            {
                var reason = _forge?.Action(player.CharacterId, request.ItemNetId) ?? ForgeReason.ForgeUnavailable;
                player.Forge.States.Add(StateOf(player, request.RequestId, reason));
                return true;
            }
            case ReliableMessageId.ForgeCancel:
            {
                var reason = _forge?.Cancel(player.CharacterId) ?? ForgeReason.ForgeUnavailable;
                var state = StateOf(player, request.RequestId, reason);
                if (reason == ForgeReason.None)
                    state.Phase = ForgePhase.Cancelled;
                player.Forge.States.Add(state);
                return true;
            }
            case ReliableMessageId.ForgeFinish:
                if (_forge == null)
                    player.Forge.Results.Add(new ForgeResult { RequestId = request.RequestId, Reason = ForgeReason.ForgeUnavailable });
                else if (_forge.Finish(player.CharacterId, request.RequestId, player.Connection) is { } answer)
                    player.Forge.Results.Add(answer);
                return true;
            default:
                return false;
        }
    }

    private ForgeState StartForge(Player player, in ClientRequest request)
    {
        if (_forge == null)
            return new ForgeState { RequestId = request.RequestId, Reason = ForgeReason.ForgeUnavailable };
        // Spec 5.3 M3 decision 7: at a station (the outpost gate and range), out of combat and not during an action.
        var station = StationOf(player, request.DropEntityId, out bool inReach);
        bool blocked = CombatState.IsInCombat(player.LastCombatAtMs, _clock.UtcNowMs, _forgeCombatStateMs) || player.Actions.IsBusy;
        var ask = new ForgeStartAsk(request.RequestId, request.DropEntityId, station, (ForgeKind)request.FromSlot, request.ItemNetId,
            request.Locations ?? Array.Empty<Shared.Simulation.Items.InventoryLocation>(), inReach, blocked, player.Connection);
        var reason = _forge.Start(player.CharacterId, ask);
        return _forge.State(player.CharacterId, request.RequestId, reason);
    }

    /// <summary>The station kind of an NPC the player may use now (the outpost gate), and whether it is in reach.</summary>
    private NpcInteraction StationOf(Player player, uint npcEntityId, out bool inReach)
    {
        inReach = false;
        if (Outposts == null)
            return NpcInteraction.None;
        foreach (var kind in Stations)
        {
            var reach = Outposts.CheckReach(player, npcEntityId, kind, out _);
            if (reach == OutpostReason.InteractionUnavailable)
                continue;
            inReach = reach == OutpostReason.None;
            return kind;
        }
        return NpcInteraction.None;
    }

    private static readonly NpcInteraction[] Stations = { NpcInteraction.Forge, NpcInteraction.JewelerBench };

    private ForgeState StateOf(Player player, uint requestId, ForgeReason reason) =>
        _forge?.State(player.CharacterId, requestId, reason) ?? new ForgeState { RequestId = requestId, Reason = reason };

    /// <summary>
    /// Phase 8: a running session ends, its inputs released, when its character left the station's range or entered combat
    /// (spec 5.3 M3 decision 7); the client hears of it with an unasked state. No allocation unless one ends.
    /// </summary>
    private void CheckForgeSessions()
    {
        if (_forge == null || Outposts == null)
            return;
        foreach (var entity in _entities)
        {
            if (entity is not Player player || !_forge.IsRunning(player.CharacterId))
                continue;
            var session = _forge.Session(player.CharacterId)!;
            ForgeReason reason = ForgeReason.None;
            if (Outposts.CheckReach(player, session.StationEntityId, session.Station, out _) != OutpostReason.None)
                reason = ForgeReason.LeftStation;
            else if (CombatState.IsInCombat(player.LastCombatAtMs, _clock.UtcNowMs, _forgeCombatStateMs))
                reason = ForgeReason.InCombat;
            if (reason == ForgeReason.None || !_forge.Abort(player.CharacterId))
                continue;
            var state = _forge.State(player.CharacterId, 0, reason);
            state.Phase = ForgePhase.Cancelled;
            player.Forge.States.Add(state);
        }
    }

    /// <summary>A save committed a forge result (rule 5): the bus, then the answer to the connection that asked.</summary>
    private void OnForgeOpCommitted(long characterId, CommittedForgeOp op)
    {
        _playersByCharacter.TryGetValue(characterId, out var player);
        uint entityId = player?.Id ?? 0;
        int region = player?.RegionIndex ?? 0;
        var record = op.Record;
        var definition = op.Definition;
        int outputNetId = definition.Output?.NetId ?? 0;
        switch (record.Kind)
        {
            case CraftOpKind.Refine:
                _events?.Publish(new Refined(characterId, entityId, definition.JobId, record.Tier, definition.NetId, outputNetId,
                    record.OutputCount, record.OpKey), characterId, region);
                break;
            case CraftOpKind.Convert:
                _events?.Publish(new Converted(characterId, entityId, definition.JobId, record.Tier + 1, definition.NetId, record.Success,
                    record.OpKey), characterId, region);
                break;
            default:
                _events?.Publish(new Crafted(characterId, entityId, definition.JobId, record.Tier, definition.NetId, outputNetId,
                    (byte)record.Rarity, record.Quality, record.RecipientId, record.OpKey), characterId, region);
                break;
        }
        _events?.Publish(new ForgeCompleted(characterId, entityId, record.Kind, definition.NetId, outputNetId, record.OutputCount,
            record.Quality, (byte)record.Rarity, record.Success, record.Seed, record.OpKey), characterId, region);
        if (player == null || player.Connection != op.Connection)
            return;
        player.Forge.Results.Add(op.Answer);
        var state = _forge!.State(characterId, 0, ForgeReason.None);
        state.Phase = ForgePhase.Finished;
        state.Kind = definition.Kind;
        state.DefNetId = (ushort)definition.NetId;
        player.Forge.States.Add(state);
    }

    /// <summary>Reads <see cref="JobBonusQuery"/> when asked, so a later assignment (WP-M3-7, tests) reaches the forge.</summary>
    private sealed class LateJobBonuses(GameWorld world) : IJobBonusQuery
    {
        public int Get(long characterId, string jobId, JobBonusKind kind, JobActivity activity) =>
            world.JobBonusQuery.Get(characterId, jobId, kind, activity);

        public bool MoldTierUnlocked(long characterId, string jobId, int tier) => world.JobBonusQuery.MoldTierUnlocked(characterId, jobId, tier);
    }

    /// <summary>Reads <see cref="ForgeChallenge"/> when asked (WP-M3-10, tests).</summary>
    private sealed class LateChallenge(GameWorld world) : IForgeChallenge
    {
        public int MaxScore(ForgeDefinition definition) => world.ForgeChallenge.MaxScore(definition);

        public int Score(ForgeSession session) => world.ForgeChallenge.Score(session);
    }
}
