using Shared.Data.Tables;
using Shared.Simulation.Economy;
using Shared.Simulation.Gathering;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;
using Shared.Util;

namespace EconomySimulator;

/// <summary>The respawn rule of the game server's <c>SpawnController</c> (R-4.2-03, spec 4.2), in milliseconds.</summary>
public static class SpawnRules
{
    /// <summary>
    /// The base delay scaled by the type's spawn delay percent in the region; a shortened delay never goes below
    /// <paramref name="minRespawnMs"/> (nor below the base when the base is shorter than that floor).
    /// </summary>
    public static long RespawnDelayMs(int baseMs, int spawnDelayPercent, int minRespawnMs) =>
        Math.Max((long)baseMs * spawnDelayPercent / 100, Math.Min(baseMs, minRespawnMs));
}

/// <summary>What one run recorded after each control step (index 0 = the first step).</summary>
public sealed class RunRecord
{
    public List<long> GoldCreated { get; } = [];
    public List<long> GoldDestroyed { get; } = [];
    public List<long> GoldOnline { get; } = [];
    public List<long> Kills { get; } = [];

    /// <summary>Vendor goods used up (consumable use, spec 3.4, WP-M3-3), in units, and what they had cost at the vendor.</summary>
    public List<long> GoodsUsed { get; } = [];
    public List<long> GoodsUsedGold { get; } = [];

    /// <summary>Series by name (<c>spawn%</c>, <c>reward%</c>, <c>kills</c> counters per pool; <c>price%</c>, <c>sales</c> counters per item).</summary>
    public SortedDictionary<string, List<long>> Series { get; } = new(StringComparer.Ordinal);

    /// <summary>The named M3 sinks and flows per control step (<see cref="M3Names"/>); empty for a row without M3 activity.</summary>
    public SortedDictionary<string, List<long>> M3 { get; } = new(StringComparer.Ordinal);

    /// <summary>The material units the players hold after each control step (M3 rows only).</summary>
    public List<long> MaterialsHeld { get; } = [];

    public void Add(string name, long value)
    {
        if (!Series.TryGetValue(name, out var list))
            Series.Add(name, list = []);
        list.Add(value);
    }

    public void AddM3(string name, long value)
    {
        if (!M3.TryGetValue(name, out var list))
            M3.Add(name, list = []);
        list.Add(value);
    }
}

/// <summary>
/// One seeded run of an <see cref="EconomySimScenario"/> (spec 12.5, WP-M2-12) in one-second steps on a simulated clock.
/// Players hunt their spawn pool (round robin over the hunted pools): each second a player kills with the scenario's chance
/// when a monster of its pool is alive; the monster respawns after the <see cref="SpawnRules"/> delay at the spawn percent of
/// that moment; the kill rolls the monster's loot (<see cref="LootRoll"/>) and counts for the demand loop. Every
/// <c>killsPerVisit</c> kills the player sells its sell-only stacks (<see cref="PriceQuote"/>, per unit, sequentially; the
/// sales count at once), uses up its vendor goods for the time since its last visit, and tops them up as far as its gold
/// pays (the gold is destroyed). The demand loop steps every <c>controlIntervalMs</c>. Deterministic for a seed: one
/// <see cref="Pcg32Rng"/>, players and pools in a fixed order.
/// <para>
/// A row with M3 activity (<see cref="SimCrafting.Plays"/>, WP-M3-12) also harvests each second with the row's chance when a
/// node of the player's turn is available (one harvest depletes it, R-5.2-03; the yield is <see cref="HarvestMath"/> with the
/// repeats of the player's own window), keeps the materials of its loot and yields, buys its one-time goods at a visit and
/// spends the time since the last one in <see cref="SimCrafting.Visit"/>. Those rolls come from a stream of their own, so the
/// loot and kill rolls keep their order and a row without M3 activity gives the numbers it always gave for its seed.
/// </para>
/// </summary>
public sealed class Simulation
{
    private const int StepMs = 1_000;
    private const long HourMs = 3_600_000;
    private const int MsPerHourStep = (int)(HourMs / StepMs);

    /// <summary>The <see cref="Pcg32Rng"/> stream of the M3 activity.</summary>
    private const ulong M3Stream = 0x4D33534DUL; // "M3SM"

    private readonly SimEconomy _economy;
    private readonly EconomySimScenario _scenario;
    private readonly IReadOnlyList<SimNodePool> _nodes;
    private readonly SimCrafting? _crafting;

    public Simulation(SimEconomy economy, EconomySimScenario scenario)
    {
        _economy = economy;
        _scenario = scenario;
        var hunted = scenario.MonsterIds.Length > 0
            ? economy.Pools.Where(p => scenario.MonsterIds.Contains(p.MonsterId)).ToList()
            : economy.Pools.Where(p => !p.Boss).ToList();
        if (hunted.Count == 0)
            throw new InvalidDataException($"EconomySimScenario '{scenario.Id}': no spawn pool to hunt");
        Hunted = hunted;
        _nodes = scenario.HarvestsPerPlayerHour <= 0 ? []
            : economy.NodePools.Where(n => scenario.NodeIds.Length == 0 || scenario.NodeIds.Contains(n.NodeId)).ToList();
        if (scenario.HarvestsPerPlayerHour > 0 && (_nodes.Count == 0 || economy.Harvest == null))
            throw new InvalidDataException($"EconomySimScenario '{scenario.Id}': no resource node to harvest");
        _crafting = SimCrafting.Plays(scenario)
            ? economy.Crafting ?? throw new InvalidDataException($"EconomySimScenario '{scenario.Id}': the crafting tables are incomplete")
            : null;
    }

    /// <summary>The spawn pools the players hunt, in demand key order.</summary>
    public IReadOnlyList<SimPool> Hunted { get; }

    public RunRecord Run(ulong seed)
    {
        var rng = new Pcg32Rng(seed);
        var m3Rng = new Pcg32Rng(seed, M3Stream);
        var demand = new DemandModel(_economy, _scenario.Players);
        var pools = Hunted.Select(p => new PoolState(p)).ToArray();
        var nodes = _nodes.Select(n => new NodeState(n)).ToArray();
        var players = new PlayerState[_scenario.Players];
        int recentWindow = _economy.Harvest?.YieldRepeatWindow ?? 1;
        for (int i = 0; i < players.Length; i++)
        {
            players[i] = new PlayerState(i % pools.Length, _scenario.BuyItemIds.Length,
                _crafting == null ? null : new CraftingPlayer(recentWindow) { NodeTurn = i });
        }
        var goods = _scenario.BuyItemIds.Select(_economy.VendorGood).ToArray();
        var oneTime = _scenario.OneTimeBuyItemIds.Select(_economy.VendorGood).ToArray();
        var counters = new M3Counters();
        var record = new RunRecord();
        var drops = new List<ItemStack>();
        int controlMs = _economy.Demand.ControlIntervalMs;
        long endMs = _scenario.Hours * HourMs;
        long created = 0, destroyed = 0, kills = 0, used = 0, usedGold = 0;
        long nextStepMs = controlMs;

        for (long now = 0; now < endMs; now += StepMs)
        {
            foreach (var pool in pools)
                pool.RespawnDue(now);
            foreach (var node in nodes)
                node.RespawnDue(now);
            foreach (var player in players)
            {
                if (nodes.Length > 0 && m3Rng.NextInt(MsPerHourStep) < _scenario.HarvestsPerPlayerHour)
                    Harvest(player.Craft!, nodes, demand, now, m3Rng, counters);
                if (rng.NextInt(60_000) >= _scenario.KillsPerPlayerMinuteMilli)
                    continue;
                var pool = pools[player.Pool];
                if (pool.Alive == 0)
                    continue; // waits for a respawn
                var def = pool.Def;
                pool.Kill(now + SpawnRules.RespawnDelayMs(def.RespawnMs, demand.SpawnDelayPercent(def.MonsterNetId, def.RegionIndex),
                    _economy.Demand.MinRespawnMs));
                demand.AddKill(def.MonsterNetId, def.RegionIndex);
                kills++;
                drops.Clear();
                LootRoll.Roll(def.Loot, def.Rolls, def.NothingWeight, rng, drops);
                foreach (var drop in drops)
                {
                    if (_economy.TryGetSellItem(drop.ItemNetId, out _))
                    {
                        player.Add(drop.ItemNetId, drop.Rarity, drop.Count);
                    }
                    else if (player.Craft != null && _economy.IsMaterial(drop.ItemNetId))
                    {
                        player.Craft.Add(drop.ItemNetId, drop.Count);
                        counters.Add(M3Names.LootMaterials, drop.Count);
                    }
                }
                if (++player.KillsSinceVisit >= _scenario.KillsPerVisit)
                {
                    player.KillsSinceVisit = 0;
                    long elapsed = now - player.LastVisitMs;
                    created += Sell(player, demand);
                    destroyed += BuyOneTime(player, oneTime, counters);
                    destroyed += Restock(player, goods, now, ref used, ref usedGold);
                    if (player.Craft != null)
                        _crafting!.Visit(player.Craft, _scenario, elapsed, now, m3Rng, counters);
                }
            }

            if (now + StepMs >= nextStepMs)
            {
                nextStepMs += controlMs;
                demand.Step(_scenario.Players);
                Record(record, demand, players, created, destroyed, kills);
                record.GoodsUsed.Add(used);
                record.GoodsUsedGold.Add(usedGold);
                if (_crafting != null)
                    RecordM3(record, demand, players, counters, used);
                created = destroyed = kills = used = usedGold = 0;
            }
        }
        return record;
    }

    /// <summary>One harvest at the player's turn of the node pools, when one is available (spec 5.2; R-5.2-03 demand).</summary>
    private void Harvest(CraftingPlayer player, NodeState[] nodes, DemandModel demand, long now, IRng rng, M3Counters counters)
    {
        var node = nodes[player.NodeTurn % nodes.Length];
        player.NodeTurn++;
        if (node.Alive == 0)
            return; // depleted: this turn went on the walk
        var def = node.Def;
        var settings = _economy.Harvest!;
        int repeats = player.Recent.Repeats(JobActivity.Harvest, def.NodeNetId, settings.YieldRepeatWindow);
        var yielded = HarvestMath.Roll(def.Yield, _scenario.JobLevel, HarvestMath.BonusPercent(_scenario.JobLevel, 0, 0, settings),
            HarvestMath.RepeatPercent(repeats, settings), rng);
        if (yielded.IsEmpty)
            return;
        player.Recent.Add(JobActivity.Harvest, def.NodeNetId);
        player.Add(yielded.ItemNetId, yielded.Count);
        counters.Add(M3Names.Harvests, 1);
        counters.Add(M3Names.HarvestUnits, yielded.Count);
        node.Kill(now + RespawnMath.ScaledDelay(def.RespawnMs, demand.NodeSpawnDelayPercent(def.NodeNetId, def.RegionIndex),
            _economy.Gather?.MinRespawnMs ?? 0));
        demand.AddHarvest(def.NodeNetId, def.RegionIndex);
    }

    /// <summary>The goods a character buys once, in the row's order, as its gold pays (tools and molds, R-4.2-01; the gold is destroyed).</summary>
    private static long BuyOneTime(PlayerState player, SimVendorGood[] goods, M3Counters counters)
    {
        long spent = 0;
        if (player.Craft == null)
            return 0;
        foreach (var good in goods)
        {
            if (player.Gold < good.PriceGold || !player.Craft.OneTimeBought.Add(good.Id))
                continue;
            player.Gold -= good.PriceGold;
            spent += good.PriceGold;
            counters.Add(M3Names.VendorToolsAndMolds, good.PriceGold);
        }
        return spent;
    }

    private void RecordM3(RunRecord record, DemandModel demand, PlayerState[] players, M3Counters counters, long goodsUsed)
    {
        counters.Add(M3Names.ConsumableUse, goodsUsed);
        foreach (var name in M3Names.Sinks.Concat(M3Names.Flows))
            record.AddM3(name, counters.Take(name));
        record.MaterialsHeld.Add(players.Sum(p => p.Craft!.Bag.Values.Sum()));
        foreach (var node in _nodes)
        {
            record.Add("nodeSpawn% " + node.Name, demand.NodeSpawnDelayPercent(node.NodeNetId, node.RegionIndex));
            record.Add("harvests " + node.Name, demand.HarvestsMilli(node.NodeNetId, node.RegionIndex));
        }
    }

    private long Sell(PlayerState player, DemandModel demand)
    {
        long total = 0;
        foreach (var ((netId, rarity), count) in player.Bag)
        {
            if (!_economy.TryGetSellItem(netId, out var item) || !_economy.TryGetRarityPercent(rarity, out int rarityPercent))
                continue;
            total += PriceQuote.Quote(demand.PriceInputs(item.BaseGold, rarityPercent, netId), count).Total;
            demand.AddSale(netId, count);
        }
        player.Bag.Clear();
        player.Gold += total;
        return total;
    }

    /// <summary>
    /// Uses up the goods for the time since the last visit, then buys them back up to <c>buyKeep</c>. Consumable use is live
    /// (spec 3.4, WP-M3-3): <c>usedPerPlayerHour</c> per good, at most what its meters and cooldown allow
    /// (<see cref="SimVendorGood.MaxUsesPerHour"/>; a good without a <c>ConsumableDef</c> row is never used), and never more
    /// than the player holds.
    /// </summary>
    private long Restock(PlayerState player, SimVendorGood[] goods, long now, ref long usedUnits, ref long usedGold)
    {
        long elapsed = now - player.LastVisitMs;
        player.LastVisitMs = now;
        long spent = 0;
        for (int i = 0; i < goods.Length; i++)
        {
            long rate = Math.Min(_scenario.UsedPerPlayerHour, goods[i].MaxUsesPerHour);
            long used = elapsed * rate + player.UseCarry[i];
            player.UseCarry[i] = used % HourMs;
            long usedNow = Math.Min(player.Held[i], used / HourMs);
            player.Held[i] -= (int)usedNow;
            usedUnits += usedNow;
            usedGold += usedNow * goods[i].PriceGold;
            long want = _scenario.BuyKeep - player.Held[i];
            long units = Math.Min(want, player.Gold / goods[i].PriceGold);
            if (units <= 0)
                continue;
            player.Held[i] += (int)units;
            player.Gold -= units * goods[i].PriceGold;
            spent += units * goods[i].PriceGold;
        }
        return spent;
    }

    private void Record(RunRecord record, DemandModel demand, PlayerState[] players, long created, long destroyed, long kills)
    {
        record.GoldCreated.Add(created);
        record.GoldDestroyed.Add(destroyed);
        record.GoldOnline.Add(players.Sum(p => p.Gold));
        record.Kills.Add(kills);
        foreach (var pool in Hunted)
        {
            record.Add("spawn% " + pool.Name, demand.SpawnDelayPercent(pool.MonsterNetId, pool.RegionIndex));
            record.Add("reward% " + pool.Name, demand.RewardPercent(pool.MonsterNetId, pool.RegionIndex));
            record.Add("kills " + pool.Name, demand.KillsMilli(pool.MonsterNetId, pool.RegionIndex));
        }
        foreach (var item in HuntedItems())
        {
            record.Add("price% " + item.Id, demand.PricePercent(item.NetId));
            record.Add("sales " + item.Id, demand.SalesMilli(item.NetId));
        }
    }

    /// <summary>The sell-only items the hunted pools can drop, in net id order.</summary>
    public IEnumerable<SimSellItem> HuntedItems() => _economy.SellItems.Where(i => Hunted.Any(p => p.Loot.Any(c => c.ItemNetId == i.NetId)));

    private sealed class PoolState(SimPool def) : RespawnQueue(def.Points)
    {
        public SimPool Def { get; } = def;
    }

    private sealed class NodeState(SimNodePool def) : RespawnQueue(def.Points)
    {
        public SimNodePool Def { get; } = def;
    }

    /// <summary>Monsters or nodes of one pool: how many are up, and when the others come back.</summary>
    private abstract class RespawnQueue(int points)
    {
        private readonly PriorityQueue<long, long> _respawns = new();

        public int Alive { get; private set; } = points;

        public void Kill(long respawnAtMs)
        {
            Alive--;
            _respawns.Enqueue(respawnAtMs, respawnAtMs);
        }

        public void RespawnDue(long nowMs)
        {
            while (_respawns.TryPeek(out long at, out _) && at <= nowMs)
            {
                _respawns.Dequeue();
                Alive++;
            }
        }
    }

    private sealed class PlayerState(int pool, int goods, CraftingPlayer? craft)
    {
        public int Pool { get; } = pool;

        /// <summary>The player's M3 state; null on a row without M3 activity.</summary>
        public CraftingPlayer? Craft { get; } = craft;

        public int KillsSinceVisit { get; set; }
        public long Gold { get; set; }
        public long LastVisitMs { get; set; }
        public SortedDictionary<(int NetId, byte Rarity), int> Bag { get; } = new();
        public int[] Held { get; } = new int[goods];
        public long[] UseCarry { get; } = new long[goods];

        public void Add(int netId, byte rarity, int count) =>
            Bag[(netId, rarity)] = Bag.TryGetValue((netId, rarity), out int held) ? held + count : count;
    }
}
