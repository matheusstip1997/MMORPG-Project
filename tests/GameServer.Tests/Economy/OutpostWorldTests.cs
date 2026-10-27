using GameServer.Entities;
using GameServer.Outposts;
using GameServer.Tests.Support;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Tests.Economy;

/// <summary>
/// NPCs and the interaction registry (spec 4.2, WP-M2-4): NPCs come from npc markers, ride in snapshots, are never
/// combatants; the common gate refuses an unknown NPC, an interaction it does not offer, and a player out of range
/// (<c>OutOfRange</c>); registered handlers answer at once or later; <c>OutpostVisited</c> once per visit.
/// </summary>
public class OutpostWorldTests
{
    [Fact]
    public void Npcs_are_placed_from_npc_markers_in_marker_order_and_reach_snapshots_without_being_combatants()
    {
        var f = new EconomyFixture();

        var npcs = f.Harness.World.Entities.OfType<Npc>().ToList();
        Assert.Equal([EconomyFixture.FarOutpost, EconomyFixture.Outpost, EconomyFixture.Vendor], npcs.Select(n => n.MarkerId));
        Assert.All(npcs, n => Assert.Null(n.Combatant));
        Assert.True(npcs.Max(n => n.Id) < f.Player.Id); // placed when the world is built
        var outpost = f.NpcAt(EconomyFixture.Outpost);
        Assert.Equal(("m1.outpost", (ushort)1), (outpost.Def.Id, outpost.DefId));

        var spawn = f.Transport.SnapshotsFor(1).SelectMany(s => s.Spawns).Single(s => s.EntityId == outpost.Id);
        Assert.Equal((EntityKind.Npc, (ushort)outpost.Def.NetId), (spawn.Kind, spawn.DefId));
        Assert.DoesNotContain(f.Transport.SnapshotsFor(1).SelectMany(s => s.Spawns), s => s.EntityId == f.NpcId(EconomyFixture.FarOutpost));
    }

    [Fact]
    public void An_npc_marker_naming_no_NpcDef_is_reported_by_the_config_marker_check()
    {
        var f = new EconomyFixture();
        var map = TestMaps.Map(TestMaps.Open().Grid,
            [TestMaps.SpawnMarker(0, 0), EconomyFixture.Npc("test.npc.ghost", 100, 0, "m1.nobody")]);

        var errors = WorldLoader.CheckConfigMarkers(map, f.Tables);

        Assert.Contains(errors, e => e.Message.Contains("m1.nobody"));
    }

    [Fact]
    public void Opening_the_outpost_in_reach_is_answered_yes()
    {
        var f = new EconomyFixture();

        f.Harness.SendInteract(1, 1, f.NpcId(EconomyFixture.Outpost), NpcInteraction.Sell);
        f.Harness.Ticks(1);

        var answer = Assert.Single(f.Transport.InteractResultsFor(1));
        Assert.Equal((1u, NpcInteraction.Sell, OutpostReason.None), (answer.RequestId, answer.Kind, answer.Reason));
    }

    [Fact]
    public void A_player_out_of_range_is_refused_with_OutOfRange()
    {
        var f = new EconomyFixture();

        f.Harness.SendInteract(1, 1, f.NpcId(EconomyFixture.FarOutpost), NpcInteraction.Sell);
        f.Harness.Ticks(1);

        Assert.Equal(OutpostReason.OutOfRange, Assert.Single(f.Transport.InteractResultsFor(1)).Reason);
        Assert.Empty(f.Events.Visits);
    }

    [Theory]
    [InlineData(EconomyFixture.Outpost, NpcInteraction.Buy)] // an outpost does not sell
    [InlineData(EconomyFixture.Vendor, NpcInteraction.Sell)] // a vendor never buys (R-4.2-01)
    [InlineData(EconomyFixture.Outpost, NpcInteraction.RankUp)] // not offered by m1.outpost until WP-M2-3
    public void An_interaction_the_npc_does_not_offer_is_refused(string npc, NpcInteraction kind)
    {
        var f = new EconomyFixture();

        f.Harness.SendInteract(1, 1, f.NpcId(npc), kind);
        f.Harness.Ticks(1);

        Assert.Equal(OutpostReason.InteractionUnavailable, Assert.Single(f.Transport.InteractResultsFor(1)).Reason);
    }

    [Fact]
    public void An_entity_that_is_not_an_npc_is_refused_with_NpcNotFound()
    {
        var f = new EconomyFixture();

        f.Harness.SendInteract(1, 1, f.Player.Id, NpcInteraction.Sell);
        f.Harness.SendInteract(1, 2, 60_000, NpcInteraction.Sell);
        f.Harness.Ticks(1);

        Assert.All(f.Transport.InteractResultsFor(1), r => Assert.Equal(OutpostReason.NpcNotFound, r.Reason));
    }

    [Fact]
    public void A_registered_handler_runs_after_the_gate_and_may_answer_later()
    {
        // WP-M2-3's rank-up: offered by the outpost row, handled by a system that registers it.
        var f = new EconomyFixture(edit: set =>
        {
            var outpost = NpcDef.Find(set, "m1.outpost")!;
            outpost.Interactions = [NpcInteraction.Sell, NpcInteraction.RankUp];
        });
        var handler = new RecordingHandler(answer: null);
        f.Harness.World.Outposts!.Register(NpcInteraction.RankUp, handler);

        f.Harness.SendInteract(1, 7, f.NpcId(EconomyFixture.Outpost), NpcInteraction.RankUp);
        f.Harness.SendInteract(1, 8, f.NpcId(EconomyFixture.FarOutpost), NpcInteraction.RankUp);
        f.Harness.Ticks(1);

        Assert.Equal([7u], handler.Requests); // the far one was refused by the gate
        Assert.Equal(OutpostReason.OutOfRange, Assert.Single(f.Transport.InteractResultsFor(1)).Reason);
        Assert.Throws<InvalidOperationException>(() => f.Harness.World.Outposts.Register(NpcInteraction.RankUp, handler));
    }

    [Fact]
    public void OutpostVisited_is_published_once_per_visit()
    {
        // A short gap, so the clock never runs past the session timeouts of the test world.
        var f = new EconomyFixture(edit: set => set.Single<EconomySettings>().VisitGapMs = 2_000);
        uint outpost = f.NpcId(EconomyFixture.Outpost);

        f.Harness.SendInteract(1, 1, outpost, NpcInteraction.Sell);
        f.Harness.SendInteract(1, 2, outpost, NpcInteraction.Sell);
        f.Harness.SendInteract(1, 3, f.NpcId(EconomyFixture.Vendor), NpcInteraction.Buy); // a vendor is not an outpost
        f.Harness.Ticks(1);
        f.Harness.Clock.Advance(f.Rules.Settings.VisitGapMs - 1);
        f.Harness.SendInteract(1, 4, outpost, NpcInteraction.Sell); // still the same visit: the gap counts from the last one
        f.Harness.Ticks(1);
        f.Harness.Clock.Advance(f.Rules.Settings.VisitGapMs);
        f.Harness.SendInteract(1, 5, outpost, NpcInteraction.Sell);
        f.Harness.Ticks(1);

        Assert.Equal(2, f.Events.Visits.Count);
        Assert.All(f.Events.Visits, v => Assert.Equal((f.CharacterId, outpost, 1), (v.CharacterId, v.NpcEntityId, v.NpcDefId)));
    }

    private sealed class RecordingHandler(OutpostReason? answer) : IOutpostInteractionHandler
    {
        public List<uint> Requests { get; } = [];

        public OutpostReason? Interact(Player player, Npc npc, uint requestId)
        {
            Requests.Add(requestId);
            return answer;
        }
    }
}
