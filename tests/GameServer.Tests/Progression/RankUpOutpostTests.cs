using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Tests.Economy;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Progression;

/// <summary>
/// The rank-up at an outpost (R-3.6-04, WP-M2-3) on the WP-M2-4 interaction registry, and "outposts visited" from
/// <see cref="OutpostVisited"/>, with the repository tables (placeholder F to E: 60 kills, 2 monster types, 1 outpost).
/// The gate runs first; a refusal is answered at once; an accepted rank-up is answered only after its save committed
/// (rule 5); outposts count once each, by npc marker id; a level-up reaches <see cref="InventoryService"/> for the
/// <c>ItemDef.minLevel</c> checks.
/// </summary>
public class RankUpOutpostTests
{
    private const string Outpost = "test.npc.outpost";
    private const string FarOutpost = "test.npc.far";
    private const string OtherOutpost = "test.npc.outpost2"; // in reach, the same NpcDef
    private const string Vendor = "test.npc.vendor";

    private sealed class Fixture
    {
        public Fixture(params ItemInstanceRow[] instances)
        {
            var tables = TestCombat.Tables();
            Rules = ProgressionRules.From(tables)!;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<RankChanged>(Log);
            var map = TestMaps.Map(TestMaps.Open().Grid,
            [
                TestMaps.SpawnMarker(0, 0), TestCombat.MonsterMarker(2_000, 0), EconomyFixture.Npc(Outpost, 200, 0, "m1.outpost"),
                EconomyFixture.Npc(Vendor, 0, 200, "m1.vendor"), EconomyFixture.Npc(FarOutpost, 5_000, 5_000, "m1.outpost"),
                EconomyFixture.Npc(OtherOutpost, -200, 0, "m1.outpost"),
            ]);
            Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus,
                combat: TestCombat.Rules(tables, TestCombat.Point()), items: ItemRules.From(tables)!,
                economy: EconomyRules.From(tables)!, progression: Rules);
            var character = Harness.Characters.Add(1, 0, new CharacterState(1, 0, "", 0, 0, 0, 0, 100, 50, 100));
            Harness.Characters.Items[character.CharacterId] = [];
            Harness.Characters.Instances[character.CharacterId] = instances;
            Player = Harness.Join(1);
            Harness.Ticks(1);
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public ProgressionRules Rules { get; }
        public ProgressionWorldTests.EventLog Log { get; } = new();
        public Player Player { get; }
        public ProgressionStore Store => Harness.World.Progression!;
        public FakeTransport Transport => Harness.Transport;

        public uint NpcId(string markerId) => Harness.World.Entities.OfType<Npc>().Single(n => n.MarkerId == markerId).Id;

        public long Outposts => Store.CounterValue(Player.CharacterId, Rules.Counter(AchievementCategory.OutpostsVisited)!);

        /// <summary>F to E but the outpost: 60 kills of 2 monster types.</summary>
        public void KillsForE()
        {
            for (int i = 0; i < 59; i++)
                Store.RecordKill(Player.CharacterId, "m1.demon", boss: false);
            Store.RecordKill(Player.CharacterId, "m1.alien", boss: false);
        }

        /// <summary>The next class 2 batch (every second) and its completion.</summary>
        public void Save()
        {
            Harness.Clock.Advance(1_000);
            Harness.Ticks(2);
        }

        public void RankUp(uint requestId)
        {
            Harness.SendInteract(1, requestId, NpcId(Outpost), NpcInteraction.RankUp);
            Harness.Ticks(1);
        }
    }

    [Fact]
    public void A_rank_up_with_unmet_requirements_is_answered_at_once_with_RankRequirementsNotMet()
    {
        var f = new Fixture();

        f.RankUp(1);

        var answer = Assert.Single(f.Transport.InteractResultsFor(1));
        Assert.Equal((1u, NpcInteraction.RankUp, OutpostReason.RankRequirementsNotMet), (answer.RequestId, answer.Kind, answer.Reason));
        Assert.Equal(Rank.F, f.Store.Snapshot(f.Player.CharacterId).Rank);
    }

    [Fact]
    public void The_outpost_asked_counts_as_visited_before_the_check_so_a_first_visit_can_rank_up()
    {
        var f = new Fixture();
        f.KillsForE();
        Assert.Equal(0, f.Outposts);

        f.RankUp(1);

        Assert.Equal(1, f.Outposts);
        Assert.Equal(Rank.E, f.Store.Snapshot(f.Player.CharacterId).Rank);
    }

    [Fact]
    public void An_accepted_rank_up_is_answered_only_after_its_save_commits_and_a_repeat_gets_the_first_answer()
    {
        var f = new Fixture();
        f.KillsForE();
        f.Harness.Saves.AutoComplete = false;

        f.RankUp(1);
        f.Harness.Ticks(2); // the rank-up save is issued, not completed

        Assert.Equal(Rank.E, f.Player.Combatant!.Rank);
        Assert.Empty(f.Transport.InteractResultsFor(1));
        Assert.Empty(f.Log.Events.OfType<RankChanged>());
        f.RankUp(1); // double submit while the save is in flight: still no answer, nothing applied twice
        Assert.Empty(f.Transport.InteractResultsFor(1));

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        var answer = Assert.Single(f.Transport.InteractResultsFor(1));
        Assert.Equal((1u, f.NpcId(Outpost), OutpostReason.None), (answer.RequestId, answer.NpcEntityId, answer.Reason));
        Assert.Single(f.Log.Events.OfType<RankChanged>());
        Assert.Contains(f.Transport.ProgressionFor(1), s => s.Cause == ProgressionCause.RankUp && s.Rank == Rank.E);

        f.RankUp(1); // the same request after the commit: the first answer again
        Assert.Equal([OutpostReason.None, OutpostReason.None], f.Transport.InteractResultsFor(1).Select(a => a.Reason));
        Assert.Single(f.Harness.Characters.RankUps);
        Assert.Equal(Rank.E, f.Store.Snapshot(f.Player.CharacterId).Rank);
    }

    [Fact]
    public void A_rank_up_out_of_range_is_refused_by_the_gate_and_counts_no_outpost()
    {
        var f = new Fixture();
        f.KillsForE();

        f.Harness.SendInteract(1, 1, f.NpcId(FarOutpost), NpcInteraction.RankUp);
        f.Harness.Ticks(1);

        Assert.Equal(OutpostReason.OutOfRange, Assert.Single(f.Transport.InteractResultsFor(1)).Reason);
        Assert.Equal(0, f.Outposts);
        Assert.Equal(Rank.F, f.Store.Snapshot(f.Player.CharacterId).Rank);
    }

    [Fact]
    public void Outposts_visited_counts_each_outpost_once_by_marker_and_never_a_vendor()
    {
        var f = new Fixture();

        f.Harness.SendInteract(1, 1, f.NpcId(Outpost), NpcInteraction.Sell);
        f.Harness.SendInteract(1, 2, f.NpcId(Vendor), NpcInteraction.Buy);
        f.Harness.Ticks(1);
        f.RankUp(3); // the rank-up counts the outpost asked again: it is already counted

        Assert.Equal(1, f.Outposts);

        // Another outpost of the same NpcDef is another place.
        f.Harness.SendInteract(1, 4, f.NpcId(OtherOutpost), NpcInteraction.Sell);
        f.Harness.Ticks(1);

        Assert.Equal(2, f.Outposts);
    }

    [Fact]
    public void A_level_up_reaches_the_inventory_so_a_level_5_ring_can_be_equipped()
    {
        const byte safe = 2, equipped = 5, ring = 6;
        var f = new Fixture(new ItemInstanceRow(ItemInstanceIds.Make(1, 1), safe, 0, "m2.copperRing", 0, false, false,
            ItemInstanceIds.NewProperties(1))); // minLevel 5
        f.Harness.SendMove(1, requestId: 1, safe, 0, equipped, ring);
        f.Harness.Ticks(1);
        f.Save();
        Assert.Equal(InventoryMoveReason.LevelTooLow, f.Transport.MoveResultsFor(1)[^1].Reason);

        f.Harness.World.Exp!.Grant(f.Player.CharacterId, f.Player.Id, 1_360); // 172 + 276 + 392 + 520: level 5
        f.Harness.Ticks(1);
        f.Harness.SendMove(1, requestId: 2, safe, 0, equipped, ring);
        f.Harness.Ticks(1);
        f.Save();

        Assert.Equal(5, f.Store.GetLevel(f.Player.CharacterId));
        Assert.Equal(InventoryMoveReason.None, f.Transport.MoveResultsFor(1)[^1].Reason);
    }
}
