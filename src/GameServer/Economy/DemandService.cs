using GameServer.Events;
using GameServer.World.Map;
using GameServer.World.Nodes;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Economy;

namespace GameServer.Economy;

/// <summary>What other systems read from the demand loop (R-4.2-03).</summary>
public interface IDemandQuery
{
    /// <summary>Respawn delay scale of a monster type in a region, whole percent (100 = the base delay).</summary>
    int SpawnDelayPercent(int monsterNetId, byte regionIndex);

    /// <summary>Reward multiplier of a monster type in a region, whole percent (for outpost runs, WP-M2-6).</summary>
    int RewardPercent(int monsterNetId, byte regionIndex);
}

/// <summary>One demand counter as saved in <c>demand_state</c> (string ids, so a table edit cannot shift it).</summary>
public readonly record struct DemandStateRow(DemandKeyKind Kind, string DefId, string RegionId, long DecayedMilli,
    int SpawnPercent, int RewardPercent);

/// <summary>
/// The dynamic demand loop (R-4.2-03, spec 4.2 <c>DemandService</c>; WP-M2-4). Decayed counters, in thousandths of a unit:
/// kills per (monster type, region) from <see cref="Killed"/>, and units sold per item from outpost sales
/// (<see cref="RegisterSale"/>, at the moment a sale is applied, so two sales in one tick price one after the other,
/// R-4.2-02), and with the node tables harvests per (node type, region) from <see cref="Harvested"/> (R-5.2-03, WP-M3-4:
/// "the same counters as monsters": their retention and expected harvests per player come from <c>GatherSettings</c>, the
/// curve, clamp and smoothing are the kill counters' spawn delay ones). Once every <c>DemandSettings.controlIntervalMs</c>
/// of world clock time (<see cref="Tick"/>, phase 7; no timers) a control step decays every counter, then maps each kill counter's ratio (decayed over the expected count for
/// the active players, at least <c>minActivePlayers</c>) through the <c>spawnDelay</c> and <c>reward</c> curves, clamps
/// and smooths them (at most <c>maxStepChangePercent</c> per step), and keeps the active player count that normalizes
/// prices until the next step. The sell price itself follows the sales counter directly (<see cref="PriceService"/>).
/// <para>
/// Counters are created on first use and walked in key order (monster or item net id, then region; rule 6). The host
/// saves the state every <c>persistIntervalMs</c> (<see cref="Capture"/>, volatile class) and restores it at start
/// (<see cref="Restore"/>). Simulation thread only.
/// </para>
/// </summary>
public sealed class DemandService : IDemandQuery, INodeDemandQuery, IEventConsumer<Killed>, IEventConsumer<Harvested>
{
    public const int Neutral = 100;

    private readonly EconomyRules _rules;
    private readonly WorldMap _map;
    private readonly EventBus? _events;
    private readonly List<KillCounter> _kills = new();
    private readonly Dictionary<int, KillCounter> _killsByKey = new();
    private readonly List<SalesCounter> _sales = new();
    private readonly Dictionary<int, SalesCounter> _salesByItem = new();
    private readonly NodeRules? _nodes;
    private readonly List<KillCounter> _harvests = new();
    private readonly Dictionary<int, KillCounter> _harvestsByKey = new();
    private EventWatermark _watermark;
    private bool _started;
    private long _nextStepMs;
    private int _activePlayers;

    /// <param name="nodes">The resource node tables (WP-M3-4); without them harvests are not counted.</param>
    public DemandService(EconomyRules rules, WorldMap map, EventBus? events, NodeRules? nodes = null)
    {
        _rules = rules;
        _map = map;
        _events = events;
        _nodes = nodes;
    }

    /// <summary>Control steps run so far.</summary>
    public long Steps { get; private set; }

    /// <summary>The active player count of the last step (what normalizes prices now).</summary>
    public int ActivePlayers => _activePlayers;

    public void Subscribe(EventBus bus)
    {
        bus.Subscribe<Killed>(this);
        if (_nodes != null)
            bus.Subscribe<Harvested>(this);
    }

    /// <summary>Phase 11: a monster kill counts for its type in the region where it died. Idempotent per event id.</summary>
    public void Handle(in EventEnvelope envelope, in Killed payload)
    {
        if (!_watermark.TryAccept(envelope) || payload.VictimCharacterId != 0 || !_rules.TryGetMonster(payload.VictimDefId, out _))
            return;
        byte region = _map.Grid.RegionIndexAt(FixVec2.FromCm(payload.XCm, payload.YCm));
        KillCounterFor(payload.VictimDefId, region).DecayedMilli += DemandMath.Unit;
    }

    /// <summary>
    /// Phase 11: a harvest counts for its node type (<see cref="Harvested.NodeDefNetId"/>) in the node's region, which the
    /// publisher sets as the envelope's <see cref="EventEnvelope.RegionId"/> (R-5.2-03). Idempotent per event id; a node type
    /// or region the tables or the map do not know is ignored.
    /// </summary>
    public void Handle(in EventEnvelope envelope, in Harvested payload)
    {
        if (!_watermark.TryAccept(envelope) || _nodes == null || !_nodes.TryGetNode(payload.NodeDefNetId, out _)
            || envelope.RegionId < 0 || envelope.RegionId > byte.MaxValue || _map.RegionByIndex((byte)envelope.RegionId) == null)
            return;
        CounterFor(_harvests, _harvestsByKey, payload.NodeDefNetId, (byte)envelope.RegionId).DecayedMilli += DemandMath.Unit;
    }

    public int NodeSpawnDelayPercent(int nodeNetId, byte regionIndex) =>
        _harvestsByKey.TryGetValue(Key(nodeNetId, regionIndex), out var counter) ? counter.SpawnPercent : Neutral;

    /// <summary>The harvest counter of a node type in a region now (tests and tools).</summary>
    public long HarvestsMilli(int nodeNetId, byte regionIndex) =>
        _harvestsByKey.TryGetValue(Key(nodeNetId, regionIndex), out var counter) ? counter.DecayedMilli : 0;

    /// <summary>Counts <paramref name="count"/> units of <paramref name="item"/> sold (R-4.2-02: the price falls per unit).</summary>
    public void RegisterSale(ItemDef item, int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        SalesCounterFor(item.NetId).DecayedMilli += count * DemandMath.Unit;
    }

    /// <summary>The item's decayed sales counter now.</summary>
    public long SalesMilli(int itemNetId) => _salesByItem.TryGetValue(itemNetId, out var counter) ? counter.DecayedMilli : 0;

    /// <summary>The expected decayed sales of one item for the active players of the last step.</summary>
    public long ExpectedSalesMilli() =>
        DemandMath.ExpectedMilli(_activePlayers, _rules.Demand.MinActivePlayers, _rules.Prices.ExpectedSalesPerPlayerMilli);

    public int SpawnDelayPercent(int monsterNetId, byte regionIndex) =>
        _killsByKey.TryGetValue(Key(monsterNetId, regionIndex), out var counter) ? counter.SpawnPercent : Neutral;

    public int RewardPercent(int monsterNetId, byte regionIndex) =>
        _killsByKey.TryGetValue(Key(monsterNetId, regionIndex), out var counter) ? counter.RewardPercent : Neutral;

    /// <summary>The kill counter of a monster type in a region now (tests and tools).</summary>
    public long KillsMilli(int monsterNetId, byte regionIndex) =>
        _killsByKey.TryGetValue(Key(monsterNetId, regionIndex), out var counter) ? counter.DecayedMilli : 0;

    /// <summary>
    /// Phase 7: runs a control step when one is due on the world clock. The first call only schedules the first step (and
    /// takes the active players for the prices until then). True when a step ran.
    /// </summary>
    public bool Tick(long nowMs, int activePlayers)
    {
        if (!_started)
        {
            _started = true;
            _activePlayers = activePlayers;
            _nextStepMs = nowMs + _rules.Demand.ControlIntervalMs;
            return false;
        }
        if (nowMs < _nextStepMs)
            return false;
        // One step per due time; after a long stall the next one is a full interval away (no burst of catch-up steps).
        _nextStepMs = Math.Max(_nextStepMs + _rules.Demand.ControlIntervalMs, nowMs + 1);
        Step(activePlayers);
        return true;
    }

    /// <summary>One control step (R-4.2-03): decay, ratio, curve, clamp, smoothing; events for every counter.</summary>
    public void Step(int activePlayers)
    {
        var demand = _rules.Demand;
        _activePlayers = activePlayers;
        long expectedKills = DemandMath.ExpectedMilli(activePlayers, demand.MinActivePlayers, demand.ExpectedKillsPerPlayerMilli);
        var spawnCurve = _rules.SpawnCurve;
        var rewardCurve = _rules.RewardCurve;
        foreach (var counter in _kills)
        {
            counter.DecayedMilli = DemandMath.Decay(counter.DecayedMilli, demand.KillRetentionPermille);
            long ratio = DemandMath.RatioPermille(counter.DecayedMilli, expectedKills);
            counter.SpawnPercent = DemandMath.Step(counter.SpawnPercent, spawnCurve, ratio, demand.SpawnDelayMinPercent,
                demand.SpawnDelayMaxPercent, demand.MaxStepChangePercent);
            counter.RewardPercent = DemandMath.Step(counter.RewardPercent, rewardCurve, ratio, demand.RewardMinPercent,
                demand.RewardMaxPercent, demand.MaxStepChangePercent);
            _events?.Publish(new DemandUpdated(counter.MonsterNetId, counter.RegionIndex, counter.DecayedMilli, ratio,
                counter.SpawnPercent, counter.RewardPercent), regionId: counter.RegionIndex);
        }

        if (_nodes != null)
        {
            // R-5.2-03: the node counters through the same spawn delay curve, clamp and smoothing as the kill counters.
            var gather = _nodes.Settings;
            long expectedHarvests = DemandMath.ExpectedMilli(activePlayers, demand.MinActivePlayers, gather.ExpectedHarvestsPerPlayerMilli);
            foreach (var counter in _harvests)
            {
                counter.DecayedMilli = DemandMath.Decay(counter.DecayedMilli, gather.HarvestRetentionPermille);
                long ratio = DemandMath.RatioPermille(counter.DecayedMilli, expectedHarvests);
                counter.SpawnPercent = DemandMath.Step(counter.SpawnPercent, spawnCurve, ratio, demand.SpawnDelayMinPercent,
                    demand.SpawnDelayMaxPercent, demand.MaxStepChangePercent);
                _events?.Publish(new NodeDemandUpdated(counter.MonsterNetId, counter.RegionIndex, counter.DecayedMilli, ratio,
                    counter.SpawnPercent), regionId: counter.RegionIndex);
            }
        }

        var prices = _rules.Prices;
        long expectedSales = ExpectedSalesMilli();
        foreach (var counter in _sales)
        {
            counter.DecayedMilli = DemandMath.Decay(counter.DecayedMilli, prices.SalesRetentionPermille);
            int percent = DemandMath.Clamp(_rules.PriceCurve.Evaluate(DemandMath.RatioPermille(counter.DecayedMilli, expectedSales)),
                prices.FloorPercent, prices.MaxPercent);
            _events?.Publish(new PriceChanged(counter.ItemNetId, counter.DecayedMilli, percent));
        }
        Steps++;
    }

    /// <summary>Copies the state into <paramref name="rows"/> (cleared first), counters in key order.</summary>
    public void Capture(List<DemandStateRow> rows, Func<int, string?> itemIdOf)
    {
        rows.Clear();
        foreach (var counter in _kills)
        {
            if (!_rules.TryGetMonster(counter.MonsterNetId, out var monster) || _map.RegionByIndex(counter.RegionIndex) is not { } region)
                continue;
            rows.Add(new DemandStateRow(DemandKeyKind.Kills, monster.Id, region.Id, counter.DecayedMilli, counter.SpawnPercent,
                counter.RewardPercent));
        }
        foreach (var counter in _sales)
        {
            if (itemIdOf(counter.ItemNetId) is { } itemId)
                rows.Add(new DemandStateRow(DemandKeyKind.Sales, itemId, string.Empty, counter.DecayedMilli, Neutral, Neutral));
        }
        foreach (var counter in _harvests)
        {
            if (_nodes == null || !_nodes.TryGetNode(counter.MonsterNetId, out var node) || _map.RegionByIndex(counter.RegionIndex) is not { } region)
                continue;
            rows.Add(new DemandStateRow(DemandKeyKind.Harvests, node.Id, region.Id, counter.DecayedMilli, counter.SpawnPercent, Neutral));
        }
    }

    /// <summary>
    /// Restores saved counters before the first tick. Rows naming a monster, item or region the tables or the map no longer
    /// have are skipped; percents are clamped to the current settings.
    /// </summary>
    public void Restore(IReadOnlyList<DemandStateRow> rows, Func<string, int?> itemNetIdOf)
    {
        var demand = _rules.Demand;
        foreach (var row in rows)
        {
            if (row.DecayedMilli < 0)
                continue;
            if (row.Kind == DemandKeyKind.Kills)
            {
                var region = _map.Regions.FirstOrDefault(r => r.Id == row.RegionId);
                if (region == null || !_rules.TryGetMonsterNetId(row.DefId, out var netId))
                    continue;
                var counter = KillCounterFor(netId, region.Index);
                counter.DecayedMilli = row.DecayedMilli;
                counter.SpawnPercent = DemandMath.Clamp(row.SpawnPercent, demand.SpawnDelayMinPercent, demand.SpawnDelayMaxPercent);
                counter.RewardPercent = DemandMath.Clamp(row.RewardPercent, demand.RewardMinPercent, demand.RewardMaxPercent);
            }
            else if (row.Kind == DemandKeyKind.Sales && itemNetIdOf(row.DefId) is { } itemNetId)
            {
                SalesCounterFor(itemNetId).DecayedMilli = row.DecayedMilli;
            }
            else if (row.Kind == DemandKeyKind.Harvests && _nodes != null && _nodes.TryGetNode(row.DefId, out var node))
            {
                var region = _map.Regions.FirstOrDefault(r => r.Id == row.RegionId);
                if (region == null)
                    continue;
                var counter = CounterFor(_harvests, _harvestsByKey, node.NetId, region.Index);
                counter.DecayedMilli = row.DecayedMilli;
                counter.SpawnPercent = DemandMath.Clamp(row.SpawnPercent, demand.SpawnDelayMinPercent, demand.SpawnDelayMaxPercent);
            }
        }
    }

    private static int Key(int defNetId, byte regionIndex) => (defNetId << 8) | regionIndex;

    private KillCounter KillCounterFor(int monsterNetId, byte regionIndex) => CounterFor(_kills, _killsByKey, monsterNetId, regionIndex);

    /// <summary>A kill or harvest counter, created on first use and kept in key order (rule 6).</summary>
    private static KillCounter CounterFor(List<KillCounter> counters, Dictionary<int, KillCounter> byKey, int defNetId, byte regionIndex)
    {
        int key = Key(defNetId, regionIndex);
        if (byKey.TryGetValue(key, out var counter))
            return counter;
        counter = new KillCounter(defNetId, regionIndex);
        byKey.Add(key, counter);
        int index = counters.Count;
        while (index > 0 && Key(counters[index - 1].MonsterNetId, counters[index - 1].RegionIndex) > key)
            index--;
        counters.Insert(index, counter);
        return counter;
    }

    private SalesCounter SalesCounterFor(int itemNetId)
    {
        if (_salesByItem.TryGetValue(itemNetId, out var counter))
            return counter;
        counter = new SalesCounter(itemNetId);
        _salesByItem.Add(itemNetId, counter);
        int index = _sales.Count;
        while (index > 0 && _sales[index - 1].ItemNetId > itemNetId)
            index--;
        _sales.Insert(index, counter);
        return counter;
    }

    /// <summary>
    /// A decayed counter per (definition, region): kills of a monster type, or harvests of a node type (then
    /// <see cref="MonsterNetId"/> is the <c>NodeDef.netId</c> and the reward percent stays neutral).
    /// </summary>
    private sealed class KillCounter(int monsterNetId, byte regionIndex)
    {
        public int MonsterNetId { get; } = monsterNetId;
        public byte RegionIndex { get; } = regionIndex;
        public long DecayedMilli { get; set; }
        public int SpawnPercent { get; set; } = Neutral;
        public int RewardPercent { get; set; } = Neutral;
    }

    private sealed class SalesCounter(int itemNetId)
    {
        public int ItemNetId { get; } = itemNetId;
        public long DecayedMilli { get; set; }
    }
}
