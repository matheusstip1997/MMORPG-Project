using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Jobs;
using GameServer.Net;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Gathering;
using Shared.Simulation.Jobs;
using Shared.Util;

namespace GameServer.World.Nodes;

/// <summary>
/// Harvesting a resource node (spec 5.2 R-5.2-01, R-5.2-02, "M3 resource nodes"; WP-M3-5). Harvesting is an action (a
/// channel on the shared action machine, like an item use), so prediction and the cast bar follow it.
/// <list type="number">
/// <item>Phase 1, <see cref="Handle"/>: a <see cref="HarvestRequest"/> is checked through <see cref="HarvestRules.Check"/>
/// (alive, a node, available, within <c>reachCm</c>, the node's job through <see cref="IJobQuery"/>, a tool of its type
/// and tier in the Safe bag's equipment area, room for one unit of every item it can yield, not busy) and becomes the
/// player's <see cref="PendingHarvest"/>; a refusal is answered in the same tick's phase 10.</item>
/// <item>Phase 3, <see cref="RequestedAction"/> and <see cref="AfterStep"/>: the node's harvest action is asked of the
/// machine (<see cref="ActionInput.WithRequest"/>) and starts only from Idle. A movement input or a dodge ends it in the
/// machine; damage since its start (<see cref="Handle(in EventEnvelope, in Damaged)"/>) or the node depleted by someone
/// else ends it here (<see cref="ActionMachine.Break"/>). A broken channel is answered <c>HarvestInterrupted</c> (or
/// <c>NodeDepleted</c>) and harvests nothing.</item>
/// <item>Phase 3, <see cref="OnEffectPoint"/>, when the harvest effect fires: the node, the reach, the job and the tool are
/// checked again; the yield is rolled (<see cref="HarvestMath.Roll"/>) from a per-harvest seed drawn from the world's
/// harvest stream (rule 6); a yield that does not fit is refused <c>BagFull</c> and leaves the node available; otherwise
/// <see cref="InventoryService.TryAdd(long, ItemDef, byte, int, string, InventoryReason, string)"/> adds it (reason
/// <c>Harvest</c>, save class 2, operation key <c>harvest:{runId}:{entityId}:{connection}:{requestId}</c>) and the node
/// depletes now, on the world thread, so of two harvesters in one tick the lower entity id wins (phase 3 runs in entity id
/// order) and the other gets <c>NodeDepleted</c>. A crash before the commit loses the add with the node's state (nodes
/// live in memory and are all available after a restart).</item>
/// <item>Commit (<see cref="OnOpCommitted"/>): <see cref="Harvested"/> is published with the node's region index on the
/// envelope (demand, job XP, achievements, telemetry), whether or not the player is still connected. Phase 10: the
/// harvest is answered once the save holding it committed, right before the <see cref="InventoryContents"/> that shows
/// it.</item>
/// </list>
/// A repeated request id on one connection gets the first answer and harvests nothing more (double submit). World thread
/// only; allocation-free per tick except the operation key and the roll's generator of an accepted harvest.
/// </summary>
internal sealed class HarvestService : IEventConsumer<Damaged>, IJobActivityGate
{
    private readonly InventoryService _inventory;
    private readonly HarvestCatalog _catalog;
    private readonly NodeController _nodes;
    private readonly HarvestSettings _settings;
    private readonly IJobQuery? _jobs;
    private readonly IRng _seeds;
    private readonly ulong _stream;
    private readonly string _runId;
    private readonly Func<uint, bool> _deplete;
    private readonly Func<uint, Entity?> _findEntity;
    private readonly Func<long, Player?> _playerOf;
    private readonly EventBus? _events;
    private readonly byte _toolContainer;
    private readonly Dictionary<string, PendingHarvestCommit> _commits = new(StringComparer.Ordinal);

    /// <param name="seeds">The world's harvest stream: each harvest draws its own 64-bit seed from it (logged with the harvest).</param>
    /// <param name="stream">The stream id of each harvest's generator (the world's harvest stream id).</param>
    /// <param name="deplete">Depletes an available node now (<c>GameWorld.TryDepleteNode</c>).</param>
    /// <param name="runId">Unique per world run, so operation keys never repeat across restarts.</param>
    public HarvestService(InventoryService inventory, HarvestCatalog catalog, NodeController nodes, HarvestSettings settings,
        IJobQuery? jobs, IRng seeds, ulong stream, string runId, Func<uint, bool> deplete, Func<uint, Entity?> findEntity,
        Func<long, Player?> playerOf, EventBus? events)
    {
        _inventory = inventory;
        _catalog = catalog;
        _nodes = nodes;
        _settings = settings;
        _jobs = jobs;
        _seeds = seeds;
        _stream = stream;
        _runId = runId;
        _deplete = deplete;
        _findEntity = findEntity;
        _playerOf = playerOf;
        _events = events;
        _toolContainer = (byte)(inventory.Rules.Containers.FirstOrDefault(c => c.Id == ContainerDef.SafeEquipment)?.Code ?? 0);
    }

    /// <summary>The job tree bonuses (WP-M3-7); none until the job trees land.</summary>
    public IJobBonusQuery Bonuses { get; set; } = NoJobBonuses.Instance;

    public HarvestCatalog Catalog => _catalog;

    /// <summary>Harvests applied and not yet committed (tests).</summary>
    internal int PendingCommits => _commits.Count;

    public string OpKey(Player player, uint requestId) => $"harvest:{_runId}:{player.Id}:{player.Connection}:{requestId}";

    public void Subscribe(EventBus events) => events.Subscribe<Damaged>(this);

    /// <summary>Phase 1: checks one harvest request of <paramref name="player"/> and makes it its pending harvest, or refuses it.</summary>
    public void Handle(Player player, in ClientRequest request)
    {
        var harvests = player.Harvests;
        uint requestId = request.RequestId;
        uint nodeId = request.DropEntityId;
        long characterId = player.CharacterId;
        if (!_inventory.IsAttached(characterId))
        {
            Refuse(player, requestId, nodeId, HarvestReason.InventoryUnavailable);
            return;
        }
        if (harvests.Current is { } current && current.RequestId == requestId)
            return; // a double submit while the first runs: the first one answers
        string opKey = OpKey(player, requestId);
        if (_inventory.WasApplied(characterId, opKey))
        {
            // Rule 5, double submit: the first answer again (after the commit), nothing harvested.
            harvests.Confirmations.Add(new PendingHarvestConfirmation(opKey,
                harvests.Answer(opKey) ?? new HarvestResult { RequestId = requestId, NodeEntityId = nodeId }));
            return;
        }
        var reason = Check(player, nodeId, out var entry);
        if (reason != HarvestReason.None)
        {
            Refuse(player, requestId, nodeId, reason);
            return;
        }
        // Spec 5.1: the job tree's bonuses are read when the activity starts, and a running harvest keeps them.
        string jobId = entry!.Def.JobId;
        harvests.Current = new PendingHarvest(requestId, opKey, nodeId, entry,
            Bonuses.Get(characterId, jobId, JobBonusKind.YieldPercent, JobActivity.Harvest),
            Bonuses.Get(characterId, jobId, JobBonusKind.GemFindPercent, JobActivity.Harvest));
    }

    /// <summary>Phase 3, before the machine steps: the catalog index of the harvest action asked for this tick, or -1.</summary>
    public int RequestedAction(Player player) =>
        player.Harvests.Current is { State: PendingHarvestState.Requested } harvest ? harvest.Entry.ActionIndex : -1;

    /// <summary>
    /// Phase 3, after the machine stepped (before its effects are handed out): a requested harvest started or was refused; a
    /// running harvest whose action is no longer running (a movement input, a dodge, a flinch, a stagger, a stun, death)
    /// harvested nothing; a running harvest whose harvester took damage, or whose node another harvester depleted, is broken
    /// now (spec 5.2), before its effect.
    /// </summary>
    /// <returns>True when the machine's refusal of this step was the harvest's (the caller sends no <c>ActionResult</c>).</returns>
    public bool AfterStep(Player player, in ActionStepResult result)
    {
        var harvests = player.Harvests;
        if (harvests.Current is not { } harvest)
            return false;
        int action = harvest.Entry.ActionIndex;
        if (harvest.State == PendingHarvestState.Requested)
        {
            if (result.StartedIndex == action)
            {
                harvest.State = PendingHarvestState.Running;
                return false;
            }
            bool machineRefused = result.RefusedIndex == action && result.Refused != ActionReason.None;
            Refuse(player, harvest.RequestId, harvest.NodeEntityId, HarvestReason.ActionBusy);
            harvests.Current = null;
            return machineRefused;
        }
        if (player.Actions.ActionIndex != action)
        {
            Refuse(player, harvest.RequestId, harvest.NodeEntityId, HarvestReason.HarvestInterrupted);
            harvests.Current = null;
            return false;
        }
        var broken = harvest.Damaged ? HarvestReason.HarvestInterrupted
            : !_nodes.TryGet(harvest.NodeEntityId, out var node) || !node.IsAvailable ? HarvestReason.NodeDepleted
            : HarvestReason.None;
        if (broken != HarvestReason.None)
        {
            ActionMachine.Break(player.Actions);
            Refuse(player, harvest.RequestId, harvest.NodeEntityId, broken);
            harvests.Current = null;
        }
        return false;
    }

    /// <summary>
    /// Phase 3: the harvest action's harvest effect fired. Rolls and adds the yield and depletes the node (see the class
    /// remarks); a harvest that no longer holds (the node gone, out of reach, no room for the yield) is answered with its
    /// reason, adds nothing and leaves the node as it is.
    /// </summary>
    public void OnEffectPoint(Player player, uint tick)
    {
        var harvests = player.Harvests;
        if (harvests.Current is not { State: PendingHarvestState.Running } harvest)
            return; // the client left (unbind), or the channel was broken this step: nothing is harvested
        harvests.Current = null;
        var def = harvest.Entry.Def;
        long characterId = player.CharacterId;
        uint nodeId = harvest.NodeEntityId;
        if (!_inventory.IsAttached(characterId))
        {
            Refuse(player, harvest.RequestId, nodeId, HarvestReason.InventoryUnavailable);
            return;
        }
        var reason = Check(player, nodeId, out _, atEffect: true);
        if (reason != HarvestReason.None)
        {
            Refuse(player, harvest.RequestId, nodeId, reason);
            return;
        }
        _nodes.TryGet(nodeId, out var node);

        int level = Math.Max(1, _jobs?.JobLevel(characterId, def.JobId) ?? 1);
        int toolBonus = BestTool(characterId, def.ToolType, out var tool) > 0 ? tool!.YieldBonusPercent : 0;
        int bonus = HarvestMath.BonusPercent(level, toolBonus, harvest.TreeYieldPercent, _settings);
        int repeats = _jobs?.Repeats(characterId, def.JobId, JobActivity.Harvest, def.NetId, _settings.YieldRepeatWindow) ?? 0;
        int repeat = HarvestMath.RepeatPercent(repeats, _settings);
        // Rule 6: each harvest rolls on its own generator, seeded from the world's harvest stream; the seed is logged with
        // the harvest (Harvested, telemetry), so the yield can be reproduced from it alone.
        ulong seed = ((ulong)_seeds.NextUInt() << 32) | _seeds.NextUInt();
        var yield = HarvestMath.Roll(harvest.Entry.Table, level, bonus, repeat, new Pcg32Rng(seed, _stream),
            harvest.GemFindPoints);
        if (yield.IsEmpty || !_inventory.Rules.TryGetItem(yield.ItemNetId, out var item))
        {
            Refuse(player, harvest.RequestId, nodeId, HarvestReason.NodeNotFound); // the validator keeps a level-1 entry
            return;
        }
        if (_inventory.SpaceFor(characterId, item, yield.Rarity) < yield.Count)
        {
            Refuse(player, harvest.RequestId, nodeId, HarvestReason.BagFull); // the node stays available
            return;
        }
        var added = _inventory.TryAdd(characterId, item, yield.Rarity, yield.Count, harvest.OpKey, InventoryReason.Harvest, "harvest");
        if (added.Added <= 0)
        {
            Refuse(player, harvest.RequestId, nodeId, HarvestReason.BagFull);
            return;
        }
        _deplete(nodeId);
        _commits[harvest.OpKey] = new PendingHarvestCommit(characterId, player.Id, def.JobId, def.NodeTier, def.NetId,
            node.RegionIndex, (ushort)item.NetId, added.Added, seed);
        var answer = new HarvestResult
        {
            RequestId = harvest.RequestId, NodeEntityId = nodeId, ItemNetId = (ushort)item.NetId,
            Count = (ushort)Math.Min(added.Added, ushort.MaxValue), Reason = HarvestReason.None,
        };
        harvests.RecordAnswer(harvest.OpKey, answer);
        harvests.Confirmations.Add(new PendingHarvestConfirmation(harvest.OpKey, answer));
    }

    /// <summary>
    /// A save committed an inventory operation (<c>InventoryService.OpCommitted</c>): a harvest's is announced now, once,
    /// with the node's region on the envelope (R-4.2-03 demand per region), the player connected or not.
    /// </summary>
    public void OnOpCommitted(long characterId, string opKey, InventoryReason reason)
    {
        if (reason != InventoryReason.Harvest || !_commits.Remove(opKey, out var c))
            return;
        _events?.Publish(new Harvested(characterId, c.EntityId, c.JobId, c.Tier, c.NodeNetId, c.ItemNetId, c.Count, opKey, c.Seed),
            characterId, c.RegionIndex);
    }

    /// <summary>
    /// <see cref="IJobActivityGate"/>: the character has a harvest asked for or channelling, which read the job tree's bonuses
    /// when it was accepted, so a respec waits (decision 10).
    /// </summary>
    public bool InSession(long characterId) => _playerOf(characterId)?.Harvests.Current != null;

    /// <summary>Phase 11: a harvester that lost HP breaks its channel in the next phase 3 (spec 5.2 "breaks on damage").</summary>
    public void Handle(in EventEnvelope envelope, in Damaged payload)
    {
        if (payload.Amount > 0 && _findEntity(payload.TargetId) is Player player
            && player.Harvests.Current is { State: PendingHarvestState.Running } harvest)
            harvest.Damaged = true;
    }

    /// <summary>
    /// The shared gate for <paramref name="player"/> and the node <paramref name="nodeId"/>: in phase 1, and at the effect
    /// point (<paramref name="atEffect"/>) without the busy and room checks (the rolled yield's room is checked then).
    /// </summary>
    private HarvestReason Check(Player player, uint nodeId, out HarvestNodeEntry? entry, bool atEffect = false)
    {
        entry = null;
        long characterId = player.CharacterId;
        bool isNode = _nodes.TryGet(nodeId, out var node) && _catalog.TryGet(node.Def.NetId, out entry);
        var def = entry?.Def;
        int level = def != null ? _jobs?.JobLevel(characterId, def.JobId) ?? 0 : 0;
        var check = new HarvestCheck
        {
            Alive = player.Combatant is { IsAlive: true },
            IsNode = isNode,
            Available = isNode && node.IsAvailable,
            DistanceSqCm = isNode ? HarvestRules.DistanceSq(player.XCm, player.YCm, node.XCm, node.YCm) : long.MaxValue,
            ReachCm = def?.ReachCm ?? 0,
            HasJob = def != null && (_jobs?.HasJob(characterId, def.JobId) ?? false),
            BestToolTier = def != null ? BestTool(characterId, def.ToolType, out _) : 0,
            MinToolTier = def?.MinToolTier ?? 0,
            HasRoom = atEffect || (entry != null && HasRoom(characterId, entry, Math.Max(1, level))),
            Busy = !atEffect && (player.Actions.IsBusy || player.Uses.Current != null || player.Harvests.Current != null),
        };
        return HarvestRules.Check(check);
    }

    /// <summary>The best tier of a tool of <paramref name="type"/> in the Safe bag's equipment area (R-5.2-02), 0 for none.</summary>
    private int BestTool(long characterId, ToolType type, out ToolDef? best)
    {
        best = null;
        var items = _inventory.CurrentInventory(characterId);
        if (!items.TryIndex(_toolContainer, out int index))
            return 0;
        var bag = items.BagAt(index);
        for (int slot = 0; slot < bag.SlotCount; slot++)
        {
            var stack = bag[slot];
            if (!stack.IsEmpty && _inventory.Rules.TryGetTool(stack.ItemNetId, out var tool) && tool.ToolType == type
                && (best == null || tool.Tier > best.Tier))
                best = tool;
        }
        return best?.Tier ?? 0;
    }

    /// <summary>Room for one unit of every item the node can yield at <paramref name="jobLevel"/> (spec 5.2: refused before the channel).</summary>
    private bool HasRoom(long characterId, HarvestNodeEntry entry, int jobLevel)
    {
        foreach (var choice in entry.Table)
        {
            if (choice.MinJobLevel > jobLevel)
                continue;
            if (!_inventory.Rules.TryGetItem(choice.ItemNetId, out var item) || _inventory.SpaceFor(characterId, item, choice.Rarity) < 1)
                return false;
        }
        return true;
    }

    private static void Refuse(Player player, uint requestId, uint nodeEntityId, HarvestReason reason) =>
        player.Harvests.Refusals.Add(new HarvestResult { RequestId = requestId, NodeEntityId = nodeEntityId, Reason = reason });

    /// <summary>An applied harvest waiting for its commit: what <see cref="Harvested"/> carries.</summary>
    private readonly record struct PendingHarvestCommit(long CharacterId, uint EntityId, string JobId, int Tier, int NodeNetId,
        byte RegionIndex, ushort ItemNetId, int Count, ulong Seed);
}
