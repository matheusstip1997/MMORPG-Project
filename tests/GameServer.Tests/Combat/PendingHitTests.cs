using GameServer.Combat;
using GameServer.Entities;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Combat;

/// <summary>
/// Pending hits (R-3.1-03, R-3.3-02): a monster's hit on a player waits for the defender's view lag (at most the 200 ms
/// cap, 4 ticks) and is judged on what the defender saw. Each test tick runs phase 1 (the frame's view clock), phase 3
/// (the player's machine and timeline) and phase 5 (due hits), as the world does.
/// </summary>
public class PendingHitTests
{
    private const uint HitTick = 50;
    private const int NoCrit = 99;

    private sealed class Scene
    {
        public Scene(string classId = "warrior")
        {
            F = new HitFixture(new FixedRng(NoCrit));
            Monster = F.Monster(1);
            Player = F.Player(2, classId: classId);
        }

        public HitFixture F { get; }
        public Monster Monster { get; }
        public Player Player { get; }

        public int HitsLanded => F.Resolver.Notices.Count;

        /// <summary>The monster's basic attack hits the player on <see cref="HitTick"/>.</summary>
        public HitOutcome Hit(Entity? attacker = null, Player? target = null) =>
            F.Hit(attacker ?? Monster, target ?? Player, tick: HitTick, land: false);

        /// <summary>One tick: a frame seeing <paramref name="lag"/> ticks behind, the player's buttons, then due hits.</summary>
        public void Tick(uint tick, int lag, InputActions held = InputActions.None, int roundTripMs = -1, Player? player = null)
        {
            var p = player ?? Player;
            p.Defence.OnFrame(tick, tick - (uint)lag, 0, roundTripMs, F.Rules.ClockSync);
            F.Step(p, held, tick);
            F.Resolver.ResolveDue(tick);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_hit_waits_exactly_the_defenders_view_lag(int lag)
    {
        var s = new Scene();
        s.Tick(HitTick - 1, lag);
        Assert.Equal(HitOutcome.Pending, s.Hit());
        for (uint t = HitTick; t < HitTick + (uint)lag; t++)
        {
            s.Tick(t, lag);
            Assert.Equal(0, s.HitsLanded);
        }
        s.Tick(HitTick + (uint)lag, lag);
        Assert.Equal(HitKind.Damaged, Assert.Single(s.F.Resolver.Notices).Kind);
        Assert.Equal(0, s.F.Resolver.Pending.Count);
    }

    [Fact]
    public void The_wait_never_passes_the_200_ms_cap_whatever_the_round_trip()
    {
        var s = new Scene();
        s.Tick(HitTick - 1, lag: 40, roundTripMs: 5_000);
        Assert.Equal(4, s.Player.Defence.LagTicksCeil);
        s.Hit();
        for (uint t = HitTick; t < HitTick + 4; t++)
            s.Tick(t, lag: 40, roundTripMs: 5_000);
        Assert.Equal(0, s.HitsLanded);
        s.Tick(HitTick + 4, lag: 40, roundTripMs: 5_000);
        Assert.Equal(1, s.HitsLanded);
    }

    [Fact]
    public void A_short_round_trip_shortens_the_wait_to_round_trip_plus_interpolation_plus_tolerance()
    {
        // RTT 0: 0 + 100 + 50 ms = 3 ticks, although the client claims 4.
        var s = new Scene();
        s.Tick(HitTick - 1, lag: 4, roundTripMs: 0);
        s.Hit();
        s.Tick(HitTick, 4, roundTripMs: 0);
        s.Tick(HitTick + 1, 4, roundTripMs: 0);
        s.Tick(HitTick + 2, 4, roundTripMs: 0);
        Assert.Equal(0, s.HitsLanded);
        s.Tick(HitTick + 3, 4, roundTripMs: 0);
        Assert.Equal(1, s.HitsLanded);
        Assert.True(s.Player.Defence.TooOldClaims > 0);
    }

    [Fact]
    public void Without_view_lag_the_hit_lands_in_the_same_phase()
    {
        var s = new Scene();
        Assert.Equal(HitOutcome.Pending, s.Hit());
        s.F.Resolver.ResolveDue(HitTick);
        Assert.Equal(1, s.HitsLanded);
    }

    [Theory]
    // Lag 3: a dodge pressed on server tick T saw tick T - 3. The dodge's i-frames are its ticks 1-5.
    [InlineData(HitTick + 2, true)] // saw HitTick - 1: the hit falls on its tick 1, after the server already ran the hit
    [InlineData(HitTick + 3, false)] // saw HitTick: the hit falls on its startup tick
    [InlineData(HitTick - 2, true)] // saw HitTick - 5: the hit falls on its tick 5
    [InlineData(HitTick - 3, false)] // saw HitTick - 6: the i-frames ended before the hit
    public void A_dodge_counts_when_its_i_frames_covered_the_hit_on_the_defenders_screen(uint pressTick, bool dodged)
    {
        var s = new Scene();
        const int lag = 3;
        uint tick = HitTick - 8;
        for (; tick < HitTick; tick++)
            s.Tick(tick, lag, tick == pressTick ? InputActions.Dodge : InputActions.None);
        int hp = s.Player.Combatant!.Hp(s.F.NowMs(HitTick));
        Assert.Equal(HitOutcome.Pending, s.Hit());
        for (; tick <= HitTick + lag; tick++)
            s.Tick(tick, lag, tick == pressTick ? InputActions.Dodge : InputActions.None);

        var notice = Assert.Single(s.F.Resolver.Notices);
        Assert.Equal(dodged ? HitKind.Dodged : HitKind.Damaged, notice.Kind);
        Assert.Equal(dodged, s.Player.Combatant.Hp(s.F.NowMs(HitTick + lag)) >= hp);
    }

    [Fact]
    public void A_dodge_judged_at_resolve_time_on_the_server_would_have_missed_it()
    {
        // The same dodge as above (pressed 2 ticks after the server hit): on the server's clock it starts after the hit.
        var s = new Scene();
        s.Tick(HitTick - 1, 3);
        s.Hit();
        s.Tick(HitTick, 3);
        s.Tick(HitTick + 1, 3);
        s.Tick(HitTick + 2, 3, InputActions.Dodge);
        Assert.Equal(ActionState.Startup, s.Player.Actions.State);
        s.Tick(HitTick + 3, 3, InputActions.Dodge);
        Assert.Equal(HitKind.Dodged, Assert.Single(s.F.Resolver.Notices).Kind);
    }

    [Fact]
    public void A_hit_still_lands_when_its_attacker_died_after_swinging()
    {
        var s = new Scene();
        s.Tick(HitTick - 1, 2);
        s.Hit();
        s.Monster.Combatant!.Die(HitTick + 1);
        ActionMachine.Kill(s.Monster.Actions);
        s.Tick(HitTick, 2);
        s.Tick(HitTick + 1, 2);
        s.Tick(HitTick + 2, 2);
        Assert.Equal(HitKind.Damaged, Assert.Single(s.F.Resolver.Notices).Kind);
    }

    [Fact]
    public void A_target_that_leaves_the_world_drops_its_pending_hits()
    {
        var s = new Scene();
        var other = s.F.Player(3);
        s.Tick(HitTick - 1, 2);
        s.Tick(HitTick - 1, 2, player: other);
        s.Hit();
        s.Hit(target: other);
        s.F.Resolver.TargetRemoved(s.Player);
        Assert.Equal(1, s.F.Resolver.Pending.Count);
        s.F.Resolver.ResolveDue(HitTick + 2);
        Assert.Equal(other.Id, Assert.Single(s.F.Resolver.Notices).TargetId);
    }

    [Fact]
    public void A_target_killed_by_an_earlier_pending_hit_takes_no_more()
    {
        var s = new Scene();
        s.Player.Combatant!.Meters.Set(ResourceKind.Hp, 1, s.F.NowMs(HitTick));
        s.Tick(HitTick - 1, 1);
        s.Hit();
        s.Hit(s.F.Monster(4));
        s.Tick(HitTick, 1);
        s.Tick(HitTick + 1, 1);

        Assert.Equal(HitKind.Killed, Assert.Single(s.F.Resolver.Notices).Kind);
        Assert.False(s.Player.Combatant.IsAlive);
        Assert.Equal(0, s.F.Resolver.Pending.Count);
    }

    [Fact]
    public void Due_hits_land_in_the_order_they_happened_and_later_ones_wait_their_turn()
    {
        var s = new Scene();
        var near = s.F.Player(3);
        var other = s.F.Monster(4);
        s.Tick(HitTick - 1, 3);
        s.Tick(HitTick - 1, 1, player: near);
        s.Hit(); // monster 1 on player 2 (lag 3)
        s.Hit(other, near); // monster 4 on player 3 (lag 1)
        s.Hit(other); // monster 4 on player 2 (lag 3)
        s.Tick(HitTick + 1, 1, player: near);
        Assert.Equal(new uint[] { 3 }, s.F.Resolver.Notices.Select(n => n.TargetId));
        s.Tick(HitTick + 3, 3);
        Assert.Equal(new uint[] { 3, 2, 2 }, s.F.Resolver.Notices.Select(n => n.TargetId));
        Assert.Equal(new uint[] { 4, 1, 4 }, s.F.Resolver.Notices.Select(n => n.AttackerId));
    }

    [Fact]
    public void Queueing_and_landing_pending_hits_allocates_nothing()
    {
        // No bus: the test's event log boxes what it records; the pipeline itself must not allocate.
        var s = new Scene();
        var resolver = new HitResolver(s.F.Rules, s.F.Catalog, null, s.F.Clock, s.F.Rng);
        var effect = s.F.Effect("attack.basic.strike");
        s.Player.Defence.OnFrame(HitTick - 1, HitTick - 3, 0, -1, s.F.Rules.ClockSync);
        void Round(uint tick)
        {
            s.Player.Combatant!.Meters.Set(ResourceKind.Hp, 150, s.F.NowMs(tick)); // stays alive
            resolver.Resolve(new HitRequest(s.Monster, s.Player, effect, HitSource.Strike, tick, 100));
            resolver.ResolveDue(tick);
            resolver.ClearNotices();
        }
        for (uint t = 0; t < 8; t++)
            Round(HitTick + t); // warm up: lists grow, the JIT runs
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (uint t = 8; t < 40; t++)
            Round(HitTick + t);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void A_player_hitting_a_monster_is_never_pending()
    {
        var s = new Scene();
        s.Tick(HitTick - 1, 4);
        Assert.Equal(HitOutcome.Damaged, s.F.Hit(s.Player, s.Monster, tick: HitTick, land: false));
        Assert.Equal(0, s.F.Resolver.Pending.Count);
    }
}
