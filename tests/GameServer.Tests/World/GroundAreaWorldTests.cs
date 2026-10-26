using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace GameServer.Tests.World;

/// <summary>
/// Ground areas and build-ups in the world tick by tick (spec 3.1, 3.8, backlog D6) with the committed tables: a mage casts
/// a test fire circle (5 s, a tick every 500 ms, Burn +20 per tick) from its hotbar (phase 3 places it after its channel, phase 5 ticks it), the demon standing
/// in it takes every tick, its Burn meter fills on the 5th tick and Burn's damage over time follows, credited to the mage;
/// the area's spawn and despawn reach the mage's client (protocol 9), and its own build-up meters ride in its snapshots.
/// </summary>
public class GroundAreaWorldTests
{
    private const byte FireCircleSlot = 1;

    private sealed class Setup
    {
        public Setup()
        {
            // The mage's slot 1 is a test fire circle here (the kits own the real rows and bindings), and monsters stand
            // still (no MonsterAiSettings), so the demon neither walks out nor interrupts the channel.
            var loaded = TestCombat.Tables();
            AreaFixture.AddTestRows(loaded);
            var tables = new ConfigSet();
            foreach (var table in loaded.Tables)
            {
                if (table.RowType != typeof(MonsterAiSettings))
                    tables.Add(table);
            }
            var mage = ActionBindings.For(tables, "mage")!;
            mage.SkillSlots = [AreaFixture.FireCircle];
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Log = AreaEventLog.Attach(Bus);
            Harness = new WorldHarness(
                map: TestCombat.OpenMap(TestCombat.MonsterMarker(0, 300)), // 3 m north: the circle's center
                events: new EventDispatchHook(Bus), bus: Bus,
                actions: ActionCatalog.From(tables),
                combat: TestCombat.Rules(tables, TestCombat.Point()));
            Harness.Characters.AutoCreateClassId = "mage";
            Mage = Harness.Join(1);
            for (int i = 0; i < 10 && Demon == null; i++)
            {
                Harness.Ticks(1);
                Demon = Harness.World.Entities.OfType<Monster>().FirstOrDefault();
            }
            Assert.NotNull(Demon);
        }

        public WorldHarness Harness { get; }

        public EventBus Bus { get; }

        public AreaEventLog Log { get; }

        public Player Mage { get; }

        public Monster? Demon { get; }

        public GroundAreaSystem Areas => Harness.World.GroundAreas!;

        public List<GroundAreaSpawn> Spawns()
        {
            var spawns = new List<GroundAreaSpawn>();
            foreach (var (peer, channel, data) in Harness.Transport.Sent)
            {
                if (peer != 1 || data[0] != (byte)HotMessageId.GroundAreaSpawn)
                    continue;
                Assert.Equal(Channel.UnreliableSequenced, channel);
                Assert.True(GroundAreaSpawnReader.TryBegin(data, out var reader));
                while (reader.TryRead(out var spawn))
                    spawns.Add(spawn);
            }
            return spawns;
        }

        public List<GroundAreaDespawn> Despawns()
        {
            var despawns = new List<GroundAreaDespawn>();
            foreach (var (peer, _, data) in Harness.Transport.Sent)
            {
                if (peer != 1 || data[0] != (byte)HotMessageId.GroundAreaDespawn)
                    continue;
                Assert.True(GroundAreaDespawnReader.TryBegin(data, out var reader));
                while (reader.TryRead(out var despawn))
                    despawns.Add(despawn);
            }
            return despawns;
        }
    }

    [Fact]
    public void A_fire_circle_burns_the_demon_standing_in_it()
    {
        var s = new Setup();
        var demon = s.Demon!;
        int hp = demon.Combatant!.Hp(s.Harness.World.NowMs);

        // Press slot 1 facing north; the area appears when the 1500 ms channel completes.
        s.Harness.SendInputs(1, TestWorlds.Frame(1, facing: 0, skillSlot: FireCircleSlot));
        for (int i = 0; i < 60 && s.Areas.Active.Count == 0; i++)
            s.Harness.Ticks(1);
        var area = Assert.Single(s.Areas.Active);
        Assert.Same(s.Mage, area.Owner);
        uint start = area.StartTick;
        Assert.Equal((100, 10), (area.DurationTicks, area.IntervalTicks));

        // Its spawn went out on the tick it was placed, repeated against loss.
        s.Harness.Ticks(2);
        var spawns = s.Spawns();
        Assert.Equal(3, spawns.Count);
        Assert.All(spawns, spawn => Assert.Equal(area.ToSpawn(), spawn));
        Assert.Equal((ushort)AreaFixture.FireCircleNetId, spawns[0].ActionId);
        Assert.Equal((s.Mage.XCm, s.Mage.YCm, start), (spawns[0].XCm, spawns[0].YCm, spawns[0].StartTick));

        // Burn +20 per tick: the 5th tick (start + 40) fills the meter and Burn lands, credited to the mage.
        while (s.Harness.World.ServerTick <= start + 39)
            s.Harness.Ticks(1);
        Assert.False(demon.Combatant.Statuses.Has("burn"));
        s.Harness.Ticks(1);
        Assert.True(demon.Combatant.Statuses.Has("burn"));
        Assert.Contains(s.Log.Events, e => e is BuildupTriggered t && t.TargetId == demon.Id && t.SourceId == s.Mage.Id);

        // The area ends in phase 5 of start + 100 and its despawn reaches the mage.
        while (s.Harness.World.ServerTick <= start + 102)
            s.Harness.Ticks(1);
        Assert.Equal((GroundAreaDespawnReason.Expired, start + 100), (area.Reason, area.EndTick));
        var despawns = s.Despawns();
        Assert.Equal(3, despawns.Count);
        Assert.All(despawns, d => Assert.Equal(new GroundAreaDespawn { AreaId = spawns[0].AreaId, Tick = start + 100,
            Reason = GroundAreaDespawnReason.Expired }, d));
        Assert.Empty(s.Areas.Active); // back to the pool

        // Ten area ticks and Burn's periodic ticks damaged the demon, all from the mage (damage numbers, spec 1.7).
        var hits = s.Harness.Transport.HitsFor(1).Where(h => h.Hit.TargetId == demon.Id).ToList();
        Assert.True(hits.Count > 10, $"{hits.Count} hits");
        Assert.All(hits, h => Assert.Equal(s.Mage.Id, h.Hit.AttackerId));
        Assert.True(demon.Combatant.Hp(s.Harness.World.NowMs) < hp);
    }

    [Fact]
    public void The_players_own_meters_ride_in_its_snapshot_header()
    {
        var s = new Setup();
        Assert.Empty(s.Harness.LastSnapshot(1).OwnBuildUps);
        var rule = s.Harness.World.Combat!.BuildUps[0];
        s.Mage.Combatant!.BuildUps[0].Gain(rule, 30, 0, s.Harness.World.ServerTick);

        s.Harness.Ticks(1);

        Assert.Equal(new OwnBuildUp { NetId = (byte)rule.NetId, Points = 30 }, Assert.Single(s.Harness.LastSnapshot(1).OwnBuildUps));
    }
}
