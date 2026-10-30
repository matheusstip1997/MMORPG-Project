using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;

namespace GameServer.Tests.Economy;

/// <summary>
/// The demand loop (R-4.2-03, WP-M2-4): kill counters from <see cref="Killed"/> (idempotent, per region), the control step
/// on the world clock, the spawn delay and reward percents (clamped, smoothed), recovery after the hunting stops, sales
/// counters for prices, the saved state, and a world whose respawn delay follows the multiplier.
/// </summary>
public class DemandServiceTests
{
    private const int DemonNetId = 1; // m1.demon

    private sealed class Service
    {
        public Service(Action<ConfigSet>? edit = null)
        {
            var tables = TestCombat.Tables();
            edit?.Invoke(tables);
            Rules = EconomyRules.From(tables)!;
            Bus = new EventBus(1, Clock, TestPipelineSettings.BusLimits());
            Demand = new DemandService(Rules, TestMaps.Open(), Bus);
            Demand.Subscribe(Bus);
            Bus.Subscribe<DemandUpdated>(Updates);
        }

        public FakeClock Clock { get; } = new();
        public EconomyRules Rules { get; }
        public EventBus Bus { get; }
        public DemandService Demand { get; }
        public Recorder Updates { get; } = new();

        public void Kills(int count, int monsterNetId = DemonNetId)
        {
            for (int i = 0; i < count; i++)
                Bus.Publish(new Killed(1, 2, 0, monsterNetId, 0, 0), 5);
            Bus.DispatchQueued();
        }

        /// <summary>One control step <paramref name="minutes"/> times, on the world clock.</summary>
        public void Minutes(int minutes, int activePlayers = 1, int killsPerMinute = 0)
        {
            for (int i = 0; i < minutes; i++)
            {
                Kills(killsPerMinute);
                Clock.Advance(Rules.Demand.ControlIntervalMs);
                Assert.True(Demand.Tick(Clock.UtcNowMs, activePlayers));
            }
            Bus.DispatchQueued();
        }

        public void Start(int activePlayers = 1) => Assert.False(Demand.Tick(Clock.UtcNowMs, activePlayers));
    }

    private sealed class Recorder : IEventConsumer<DemandUpdated>
    {
        public List<DemandUpdated> Seen { get; } = [];
        public void Handle(in EventEnvelope envelope, in DemandUpdated payload) => Seen.Add(payload);
    }

    [Fact]
    public void Kills_count_per_type_and_region_once_per_event_and_never_for_players()
    {
        var s = new Service();
        s.Start();

        s.Kills(3);
        s.Bus.Publish(new Killed(1, 2, VictimCharacterId: 77, VictimDefId: 0), 5); // a player died
        s.Bus.DispatchQueued();
        var redelivered = new EventEnvelope(1, 0, 1, 5, 0); // an event id the service already saw
        s.Demand.Handle(redelivered, new Killed(1, 2, 0, DemonNetId));

        byte region = TestMaps.Open().Grid.RegionIndexAt(default);
        Assert.Equal(3_000, s.Demand.KillsMilli(DemonNetId, region));
    }

    [Fact]
    public void A_control_step_runs_once_per_interval_of_world_clock()
    {
        var s = new Service();
        s.Start();

        s.Clock.Advance(s.Rules.Demand.ControlIntervalMs - 1);
        Assert.False(s.Demand.Tick(s.Clock.UtcNowMs, 1));
        s.Clock.Advance(1);
        Assert.True(s.Demand.Tick(s.Clock.UtcNowMs, 1));
        Assert.False(s.Demand.Tick(s.Clock.UtcNowMs, 1));
        Assert.Equal(1, s.Demand.Steps);
    }

    [Fact]
    public void Heavy_hunting_slows_respawns_step_by_step_up_to_the_clamp_and_lowers_the_reward()
    {
        var s = new Service();
        s.Start();
        byte region = TestMaps.Open().Grid.RegionIndexAt(default);

        s.Minutes(1, killsPerMinute: 30); // far above the expected 9 for three players
        Assert.Equal(110, s.Demand.SpawnDelayPercent(DemonNetId, region)); // smoothed: +10 per step
        Assert.Equal(90, s.Demand.RewardPercent(DemonNetId, region));

        s.Minutes(60, killsPerMinute: 30);
        Assert.Equal(200, s.Demand.SpawnDelayPercent(DemonNetId, region)); // the curve's end, inside the 300 clamp
        Assert.Equal(60, s.Demand.RewardPercent(DemonNetId, region));
        var last = s.Updates.Seen[^1];
        Assert.Equal((DemonNetId, region, 200, 60), (last.MonsterNetId, last.RegionIndex, last.SpawnDelayPercent, last.RewardPercent));
    }

    [Fact]
    public void After_the_hunting_stops_the_multiplier_recovers()
    {
        var s = new Service();
        s.Start();
        byte region = TestMaps.Open().Grid.RegionIndexAt(default);
        s.Minutes(60, killsPerMinute: 30);
        Assert.Equal(200, s.Demand.SpawnDelayPercent(DemonNetId, region));

        s.Minutes(6 * 60); // six quiet hours

        Assert.Equal(0, s.Demand.KillsMilli(DemonNetId, region));
        Assert.Equal(80, s.Demand.SpawnDelayPercent(DemonNetId, region)); // rarely hunted: a bit faster
        Assert.Equal(150, s.Demand.RewardPercent(DemonNetId, region));
    }

    [Fact]
    public void More_active_players_make_the_same_kills_count_for_less()
    {
        var one = new Service();
        var many = new Service();
        one.Start();
        many.Start();
        byte region = TestMaps.Open().Grid.RegionIndexAt(default);

        one.Minutes(30, activePlayers: 1, killsPerMinute: 6);
        many.Minutes(30, activePlayers: 50, killsPerMinute: 6);

        Assert.True(one.Demand.SpawnDelayPercent(DemonNetId, region) > 100);
        Assert.True(many.Demand.SpawnDelayPercent(DemonNetId, region) < 100);
    }

    [Fact]
    public void Sales_recover_each_step_and_the_state_survives_a_restart()
    {
        var s = new Service();
        s.Start();
        var horn = new ItemDef { Id = "m1.demonHorn", NetId = 1 };
        s.Demand.RegisterSale(horn, 40);
        s.Kills(5);
        s.Minutes(1);
        Assert.Equal(36_000, s.Demand.SalesMilli(1)); // 90% kept per step

        var rows = new List<DemandStateRow>();
        s.Demand.Capture(rows, netId => netId == 1 ? "m1.demonHorn" : null);
        var restored = new Service();
        restored.Demand.Restore(rows, id => id == "m1.demonHorn" ? 1 : null);

        byte region = TestMaps.Open().Grid.RegionIndexAt(default);
        Assert.Equal(36_000, restored.Demand.SalesMilli(1));
        Assert.Equal(s.Demand.KillsMilli(DemonNetId, region), restored.Demand.KillsMilli(DemonNetId, region));
        Assert.Equal(s.Demand.SpawnDelayPercent(DemonNetId, region), restored.Demand.SpawnDelayPercent(DemonNetId, region));
        Assert.Contains(rows, r => r.Kind == DemandKeyKind.Kills && r.DefId == "m1.demon" && r.RegionId == "test.open");
    }

    [Fact]
    public void Harvests_count_per_node_type_and_region_once_per_event_and_survive_a_restart()
    {
        // R-5.2-03 (WP-M3-4): node counters on the same loop; a test publisher stands in for harvesting's Harvested.
        var tables = TestCombat.Tables();
        var nodes = GameServer.World.Nodes.NodeRules.From(tables);
        var rules = EconomyRules.From(tables)!;
        var bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
        var demand = new DemandService(rules, TestMaps.Open(), bus, nodes);
        demand.Subscribe(bus);
        Assert.True(nodes.TryGetNode("node.ore.t1", out var ore));
        byte region = TestMaps.Open().Grid.RegionIndexAt(default);

        for (int i = 0; i < 30; i++)
            bus.Publish(new Harvested(1, 1, ore.NetId, region, ore.NodeTier, $"harvest:{i}"), 1);
        bus.Publish(new Harvested(1, 1, 999, region, 1, "unknown-node"), 1); // no such node type
        bus.Publish(new Harvested(1, 1, ore.NetId, 200, 1, "unknown-region"), 1); // no such region
        bus.DispatchQueued();
        demand.Handle(new EventEnvelope(1, 0, 1, 1, 0), new Harvested(1, 1, ore.NetId, region, 1, "redelivered"));
        Assert.Equal(30_000, demand.HarvestsMilli(ore.NetId, region));
        Assert.Equal(0, demand.KillsMilli(ore.NetId, region)); // node counters are their own

        demand.Step(1);
        int percent = demand.NodeSpawnDelayPercent(ore.NetId, region);
        Assert.Equal(100 + rules.Demand.MaxStepChangePercent, percent); // smoothed like the kill counters

        var rows = new List<DemandStateRow>();
        demand.Capture(rows, _ => null);
        var row = Assert.Single(rows, r => r.Kind == DemandKeyKind.Harvests);
        Assert.Equal(("node.ore.t1", TestMaps.OpenRegionId), (row.DefId, row.RegionId));
        var restored = new DemandService(rules, TestMaps.Open(), null, nodes);
        restored.Restore(rows, _ => null);
        Assert.Equal(demand.HarvestsMilli(ore.NetId, region), restored.HarvestsMilli(ore.NetId, region));
        Assert.Equal(percent, restored.NodeSpawnDelayPercent(ore.NetId, region));

        // Without the node tables the rows are skipped and nothing is counted.
        var plain = new DemandService(rules, TestMaps.Open(), null);
        plain.Restore(rows, _ => null);
        Assert.Equal(100, plain.NodeSpawnDelayPercent(ore.NetId, region));
    }

    [Fact]
    public void In_the_world_a_monster_respawn_waits_the_scaled_delay_with_a_floor()
    {
        var f = new EconomyFixture();
        var world = f.Harness.World;
        byte region = TestMaps.Open().Grid.RegionIndexAt(default);
        // As if the demons had been hunted hard: spawn delay 150%.
        world.RestoreDemandState([new DemandStateRow(DemandKeyKind.Kills, "m1.demon", TestMaps.OpenRegionId, 100_000, 150, 100)]);
        int baseTicks = world.Combat!.MonsterRespawnTicks;

        var monster = Assert.Single(world.Entities.OfType<Monster>());
        monster.Combatant!.Die(world.ServerTick);
        uint removedTick = world.ServerTick;
        f.Harness.Ticks(1);
        while (world.ServerTick <= removedTick + (uint)(baseTicks * 3 / 2) - 1)
        {
            Assert.Empty(world.Entities.OfType<Monster>());
            f.Harness.Ticks(1);
        }
        f.Harness.Ticks(1);
        Assert.Single(world.Entities.OfType<Monster>());

        // Rarely hunted with a tiny clamp: the delay never drops below DemandSettings.minRespawnMs.
        var fast = new EconomyFixture(edit: set =>
        {
            var demand = set.Single<DemandSettings>();
            demand.SpawnDelayMinPercent = 1;
            demand.MinRespawnMs = 20_000;
        });
        fast.Harness.World.RestoreDemandState([new DemandStateRow(DemandKeyKind.Kills, "m1.demon", TestMaps.OpenRegionId, 0, 1, 100)]);
        var second = Assert.Single(fast.Harness.World.Entities.OfType<Monster>());
        second.Combatant!.Die(fast.Harness.World.ServerTick);
        uint died = fast.Harness.World.ServerTick;
        fast.Harness.Ticks(1);
        fast.Harness.Ticks(fast.Harness.World.Combat!.TicksFor(20_000) - 1);
        Assert.Empty(fast.Harness.World.Entities.OfType<Monster>());
        fast.Harness.Ticks(2);
        Assert.Single(fast.Harness.World.Entities.OfType<Monster>());
        Assert.True(fast.Harness.World.ServerTick - died < baseTicks);
    }
}
