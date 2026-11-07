using Shared.Data.Tables;
using Shared.Simulation.Economy;

namespace EconomySimulator;

/// <summary>
/// The demand loop of the game server (<c>GameServer/Economy/DemandService</c>, R-4.2-03, spec 4.2 M2 implementation) with
/// the same shared math (<see cref="DemandMath"/>, <see cref="PriceQuote"/>): kill counters per (monster type, region) and
/// sales counters per item, in thousandths; a control step decays them, maps each kill counter's ratio for the active
/// players through the <c>spawnDelay</c> and <c>reward</c> curves (clamped, smoothed) and each sales counter's through the
/// <c>price</c> curve (clamped to the floor and the maximum). Counters are walked in key order (rule 6). A test checks it
/// against <c>DemandService</c> step by step.
/// </summary>
public sealed class DemandModel
{
    public const int Neutral = 100;

    private readonly DemandSettings _demand;
    private readonly PriceFloorRecovery _prices;
    private readonly DemandCurveShape _spawnCurve;
    private readonly DemandCurveShape _rewardCurve;
    private readonly DemandCurveShape _priceCurve;
    private readonly SortedDictionary<int, KillCounter> _kills = new();
    private readonly SortedDictionary<int, SalesCounter> _sales = new();
    private readonly SortedDictionary<int, KillCounter> _harvests = new();
    private readonly GatherSettings? _gather;
    private int _activePlayers;

    public DemandModel(SimEconomy economy, int activePlayers)
        : this(economy.Demand, economy.Prices, economy.SpawnCurve, economy.RewardCurve, economy.PriceCurve, activePlayers, economy.Gather)
    {
    }

    /// <param name="gather">The node counters' retention and expected harvests (R-5.2-03, WP-M3-4); null: harvests are not counted.</param>
    public DemandModel(DemandSettings demand, PriceFloorRecovery prices, DemandCurveShape spawnCurve, DemandCurveShape rewardCurve,
        DemandCurveShape priceCurve, int activePlayers, GatherSettings? gather = null)
    {
        _demand = demand;
        _prices = prices;
        _spawnCurve = spawnCurve;
        _rewardCurve = rewardCurve;
        _priceCurve = priceCurve;
        _activePlayers = activePlayers;
        _gather = gather;
    }

    /// <summary>The kill counter key of <c>DemandService</c>: monster net id, then region index.</summary>
    public static int Key(int monsterNetId, byte regionIndex) => (monsterNetId << 8) | regionIndex;

    /// <summary>One monster of the type killed in the region (R-4.2-03).</summary>
    public void AddKill(int monsterNetId, byte regionIndex) => Kill(Key(monsterNetId, regionIndex)).DecayedMilli += DemandMath.Unit;

    /// <summary>One node of the type harvested in the region (R-5.2-03: "the same counters as monsters").</summary>
    public void AddHarvest(int nodeNetId, byte regionIndex)
    {
        int key = Key(nodeNetId, regionIndex);
        if (!_harvests.TryGetValue(key, out var counter))
            _harvests.Add(key, counter = new KillCounter());
        counter.DecayedMilli += DemandMath.Unit;
    }

    public long HarvestsMilli(int nodeNetId, byte regionIndex) =>
        _harvests.TryGetValue(Key(nodeNetId, regionIndex), out var counter) ? counter.DecayedMilli : 0;

    /// <summary>The respawn delay percent of a node type in a region (<c>DemandService.NodeSpawnDelayPercent</c>).</summary>
    public int NodeSpawnDelayPercent(int nodeNetId, byte regionIndex) =>
        _harvests.TryGetValue(Key(nodeNetId, regionIndex), out var counter) ? counter.SpawnPercent : Neutral;

    /// <summary><paramref name="count"/> units of an item sold (R-4.2-02: the price falls per unit).</summary>
    public void AddSale(int itemNetId, int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        Sale(itemNetId).DecayedMilli += count * DemandMath.Unit;
    }

    public long KillsMilli(int monsterNetId, byte regionIndex) =>
        _kills.TryGetValue(Key(monsterNetId, regionIndex), out var counter) ? counter.DecayedMilli : 0;

    public int SpawnDelayPercent(int monsterNetId, byte regionIndex) =>
        _kills.TryGetValue(Key(monsterNetId, regionIndex), out var counter) ? counter.SpawnPercent : Neutral;

    public int RewardPercent(int monsterNetId, byte regionIndex) =>
        _kills.TryGetValue(Key(monsterNetId, regionIndex), out var counter) ? counter.RewardPercent : Neutral;

    public long SalesMilli(int itemNetId) => _sales.TryGetValue(itemNetId, out var counter) ? counter.DecayedMilli : 0;

    /// <summary>The expected decayed sales of one item for the active players of the last step (prices normalize by it).</summary>
    public long ExpectedSalesMilli() => DemandMath.ExpectedMilli(_activePlayers, _demand.MinActivePlayers, _prices.ExpectedSalesPerPlayerMilli);

    /// <summary>The dynamic price percent of an item now (the curve at its sales ratio, clamped; R-4.2-02).</summary>
    public int PricePercent(int itemNetId) =>
        DemandMath.Clamp(_priceCurve.Evaluate(DemandMath.RatioPermille(SalesMilli(itemNetId), ExpectedSalesMilli())),
            _prices.FloorPercent, _prices.MaxPercent);

    /// <summary>The price inputs of a stack now (<see cref="PriceQuote.Quote"/> prices it per unit, sequentially).</summary>
    public PriceInputs PriceInputs(int baseGold, int rarityPercent, int itemNetId) =>
        new(baseGold, rarityPercent, _priceCurve, _prices.FloorPercent, _prices.MaxPercent, SalesMilli(itemNetId), ExpectedSalesMilli());

    /// <summary>One control step, exactly as <c>DemandService.Step</c>: decay, ratio, curve, clamp, smoothing.</summary>
    public void Step(int activePlayers)
    {
        _activePlayers = activePlayers;
        long expectedKills = DemandMath.ExpectedMilli(activePlayers, _demand.MinActivePlayers, _demand.ExpectedKillsPerPlayerMilli);
        foreach (var counter in _kills.Values)
        {
            counter.DecayedMilli = DemandMath.Decay(counter.DecayedMilli, _demand.KillRetentionPermille);
            long ratio = DemandMath.RatioPermille(counter.DecayedMilli, expectedKills);
            counter.SpawnPercent = DemandMath.Step(counter.SpawnPercent, _spawnCurve, ratio, _demand.SpawnDelayMinPercent,
                _demand.SpawnDelayMaxPercent, _demand.MaxStepChangePercent);
            counter.RewardPercent = DemandMath.Step(counter.RewardPercent, _rewardCurve, ratio, _demand.RewardMinPercent,
                _demand.RewardMaxPercent, _demand.MaxStepChangePercent);
        }
        if (_gather != null)
        {
            // R-5.2-03: the node counters through the same spawn delay curve, clamp and smoothing as the kill counters.
            long expectedHarvests = DemandMath.ExpectedMilli(activePlayers, _demand.MinActivePlayers, _gather.ExpectedHarvestsPerPlayerMilli);
            foreach (var counter in _harvests.Values)
            {
                counter.DecayedMilli = DemandMath.Decay(counter.DecayedMilli, _gather.HarvestRetentionPermille);
                long ratio = DemandMath.RatioPermille(counter.DecayedMilli, expectedHarvests);
                counter.SpawnPercent = DemandMath.Step(counter.SpawnPercent, _spawnCurve, ratio, _demand.SpawnDelayMinPercent,
                    _demand.SpawnDelayMaxPercent, _demand.MaxStepChangePercent);
            }
        }
        foreach (var counter in _sales.Values)
            counter.DecayedMilli = DemandMath.Decay(counter.DecayedMilli, _prices.SalesRetentionPermille);
    }

    private KillCounter Kill(int key)
    {
        if (!_kills.TryGetValue(key, out var counter))
            _kills.Add(key, counter = new KillCounter());
        return counter;
    }

    private SalesCounter Sale(int itemNetId)
    {
        if (!_sales.TryGetValue(itemNetId, out var counter))
            _sales.Add(itemNetId, counter = new SalesCounter());
        return counter;
    }

    private sealed class KillCounter
    {
        public long DecayedMilli { get; set; }
        public int SpawnPercent { get; set; } = Neutral;
        public int RewardPercent { get; set; } = Neutral;
    }

    private sealed class SalesCounter
    {
        public long DecayedMilli { get; set; }
    }
}
