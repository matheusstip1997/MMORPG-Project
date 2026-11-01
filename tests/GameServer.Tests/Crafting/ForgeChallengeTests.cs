using GameServer.Crafting;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using static GameServer.Tests.Crafting.ForgeServiceTests;

namespace GameServer.Tests.Crafting;

/// <summary>
/// The forge challenge (spec 5.3 <c>IForgeChallenge</c>, M3 decision 12; WP-M3-10) on <see cref="ForgeService"/> with the
/// repository tables and a fake clock: each hit opens a round of the Blacksmith's timing bar from the session seed; a press is
/// judged on the server's timeline (presses at 100 and 200 ms round trip score like at 0; a future, too old, late or repeated
/// press scores the minimum); the score is capped; an unplayed round scores 0 and never fails the craft; and a better score
/// raises the quality by exactly its minigame term. Numbers are the placeholder rows.
/// </summary>
public class ForgeChallengeTests
{
    private const int Smelt = 1, Hit = 2;
    private const int TickMs = 50;

    private static Fixture New(ulong worldSeed = 42) => new(worldSeed: worldSeed, challenge: rules => new ForgeChallenges(rules));

    private static ForgeChallengeDef Row(Fixture f) => f.Rules.Challenges["blacksmith"];

    /// <summary>Starts a sword session and hits once at the fixture's time: the open round's parameters.</summary>
    private static TimingBarParams StartAndHit(Fixture f)
    {
        Assert.Equal(ForgeReason.None, f.StartSword());
        Assert.Equal(ForgeReason.None, f.Forge.Action(Character, Hit));
        var run = f.Forge.Session(Character)!.Challenge!;
        Assert.Equal(1, run.Open);
        return run.Bar(1);
    }

    /// <summary>
    /// A press <paramref name="offsetMs"/> into the round as a client at <paramref name="roundTripMs"/> makes it: the state
    /// takes half the round trip down, the press half up, and the server reads it at the next tick (up to a tick later).
    /// </summary>
    private static ForgeHitOutcome Press(Fixture f, long offsetMs, int roundTripMs, int lateInTickMs = TickMs - 1, int round = 1)
    {
        f.NowMs += roundTripMs + offsetMs + lateInTickMs;
        Assert.Equal(ForgeReason.None, f.Forge.ChallengeInput(Character, round, offsetMs, roundTripMs, out var outcome));
        return outcome;
    }

    private static int ScoreOf(Fixture f, int round = 1) => f.Forge.Session(Character)!.Challenge!.ScoreOf(round);

    [Fact]
    public void A_hit_opens_a_round_from_the_session_seed_and_the_state_carries_it()
    {
        var f = New();
        var bar = StartAndHit(f);
        var session = f.Forge.Session(Character)!;
        var expected = TimingBar.Roll(session.Seed, 1, Row(f));
        Assert.Equal((expected.SpeedPermillePerSec, expected.TargetPermille), (bar.SpeedPermillePerSec, bar.TargetPermille));

        var state = f.Forge.State(Character, 9, ForgeReason.None);
        Assert.Equal(ForgeChallengeKind.TimingBar, state.ChallengeKind);
        Assert.Equal((byte)1, state.BarRound);
        Assert.Equal(((ushort)bar.SpeedPermillePerSec, (ushort)bar.TargetPermille, (ushort)bar.HalfWidthPermille, (ushort)bar.DurationMs,
            (ushort)bar.HitMaxScore), (state.BarSpeed, state.BarTarget, state.BarHalfWidth, state.BarDurationMs, state.BarHitMax));
        Assert.Empty(state.HitScores);
        Assert.Equal((ushort)Row(f).MaxScore, state.ChallengeMax);
    }

    [Fact]
    public void Smelt_opens_no_round_and_a_session_without_hits_scores_0()
    {
        var f = New();
        Assert.Equal(ForgeReason.None, f.StartSword());
        Assert.Equal(ForgeReason.None, f.Forge.Action(Character, Smelt));
        var session = f.Forge.Session(Character)!;
        Assert.Equal(0, session.Challenge!.Count);
        Assert.Equal(0, new ForgeChallenges(f.Rules).Score(session));
        Assert.Null(f.Forge.Finish(Character, 5, 1));
        f.Commit();
        Assert.Equal(ForgeReason.None, Assert.Single(f.Committed).Answer.Reason); // the craft works without any press
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    public void Presses_at_100_and_200_ms_round_trip_score_like_at_0(int roundTripMs)
    {
        // Same world seed: the same session seed and the same round; the press lands at the same offset on the client.
        var atZero = New();
        var bar = StartAndHit(atZero);
        long best = TimingBar.BestOffsetMs(in bar, 300);
        Assert.Equal(ForgeHitOutcome.Scored, Press(atZero, best, roundTripMs: 0));

        var lagged = New();
        Assert.Equal(bar.TargetPermille, StartAndHit(lagged).TargetPermille);
        Assert.Equal(ForgeHitOutcome.Scored, Press(lagged, best, roundTripMs));
        Assert.Equal(ScoreOf(atZero), ScoreOf(lagged));
        Assert.True(ScoreOf(lagged) >= bar.HitMaxScore - 1);
    }

    [Fact]
    public void A_press_claiming_more_time_than_the_server_saw_is_impossible_and_scores_the_minimum()
    {
        var f = New();
        var bar = StartAndHit(f);
        long best = TimingBar.BestOffsetMs(in bar, 300);
        f.NowMs += best - 1; // the server has seen less than the claimed offset: a forged future timestamp
        Assert.Equal(ForgeReason.None, f.Forge.ChallengeInput(Character, 1, best, 0, out var outcome));
        Assert.Equal(ForgeHitOutcome.Impossible, outcome);
        Assert.Equal(0, ScoreOf(f));
    }

    [Fact]
    public void A_press_older_than_the_round_trip_and_the_tolerance_is_late_and_scores_the_minimum()
    {
        var f = New();
        var bar = StartAndHit(f);
        long best = TimingBar.BestOffsetMs(in bar, 300);
        // Claimed 100 ms round trip, but the press is 100 + tolerance + 1 ms older than the server's clock allows.
        Assert.Equal(ForgeHitOutcome.Late, Press(f, best, roundTripMs: 100, lateInTickMs: Row(f).LatencyToleranceMs + 1));
        Assert.Equal(0, ScoreOf(f));
    }

    [Fact]
    public void The_forge_ceiling_holds_whatever_the_round_trip()
    {
        var f = New();
        var bar = StartAndHit(f);
        long best = TimingBar.BestOffsetMs(in bar, 300);
        // A round trip of 2 s would allow 2.1 s of lag; the forge's own ceiling (maxLagMs 400) does not.
        f.NowMs += best + Row(f).MaxLagMs + 1;
        f.Forge.ChallengeInput(Character, 1, best, 2_000, out var outcome);
        Assert.Equal(ForgeHitOutcome.Late, outcome);
    }

    [Fact]
    public void Without_a_round_trip_sample_only_the_ceiling_applies()
    {
        var f = New();
        var bar = StartAndHit(f);
        long best = TimingBar.BestOffsetMs(in bar, 300);
        f.NowMs += best + Row(f).MaxLagMs;
        f.Forge.ChallengeInput(Character, 1, best, -1, out var outcome);
        Assert.Equal(ForgeHitOutcome.Scored, outcome);
    }

    [Fact]
    public void A_press_after_the_round_is_late()
    {
        var f = New();
        var bar = StartAndHit(f);
        Assert.Equal(ForgeHitOutcome.Late, Press(f, bar.DurationMs + 1, roundTripMs: 0));
    }

    [Fact]
    public void A_repeated_press_scores_the_minimum_and_the_first_one_counts()
    {
        var f = New();
        var bar = StartAndHit(f);
        long best = TimingBar.BestOffsetMs(in bar, 300);
        Assert.Equal(ForgeHitOutcome.Scored, Press(f, best, roundTripMs: 0));
        int first = ScoreOf(f);
        Assert.Equal(ForgeHitOutcome.Repeat, Press(f, best + 5, roundTripMs: 0));
        Assert.Equal(first, ScoreOf(f));
        Assert.Equal(ForgeHitOutcome.Impossible, Press(f, best, roundTripMs: 0, round: 2)); // a round never opened
    }

    [Fact]
    public void A_press_outside_the_window_is_a_miss()
    {
        var f = New();
        var bar = StartAndHit(f);
        long best = TimingBar.BestOffsetMs(in bar, 300);
        long off = best + (bar.HalfWidthPermille * 2_000L + bar.SpeedPermillePerSec - 1) / bar.SpeedPermillePerSec; // two half widths on
        Assert.Equal(ForgeHitOutcome.Missed, Press(f, off, roundTripMs: 0));
        Assert.Equal(0, ScoreOf(f));
    }

    [Fact]
    public void The_next_action_or_the_finish_drops_an_open_round_and_reports_it()
    {
        var f = New();
        var closed = new List<ForgeHit>();
        f.Forge.HitClosed = (_, hit) => closed.Add(hit);
        StartAndHit(f);
        Assert.Equal(ForgeReason.None, f.Forge.Action(Character, Hit)); // round 1 dropped, round 2 opens
        Assert.Equal(new ForgeHit(1, 0, 0, ForgeHitOutcome.Dropped), Assert.Single(closed));
        Assert.Equal(2, f.Forge.Session(Character)!.Challenge!.Open);
        Assert.Null(f.Forge.Finish(Character, 7, 1));
        Assert.Equal(new ForgeHit(2, 0, 0, ForgeHitOutcome.Dropped), closed[^1]);
        f.Commit();
        Assert.Equal(ForgeReason.None, f.Committed.Single().Answer.Reason);
    }

    [Fact]
    public void A_scored_press_raises_the_preview_minimum_by_its_score()
    {
        var f = New();
        var bar = StartAndHit(f);
        var before = f.Forge.State(Character, 1, ForgeReason.None);
        Assert.Equal(ForgeHitOutcome.Scored, Press(f, TimingBar.BestOffsetMs(in bar, 300), roundTripMs: 0));
        var after = f.Forge.State(Character, 2, ForgeReason.None);
        int score = ScoreOf(f);
        Assert.Equal(before.QualityMin + score, after.QualityMin);
        Assert.Equal(before.QualityMax - (bar.HitMaxScore - score), after.QualityMax); // the open round's top is spent
        Assert.Equal(new[] { (ushort)score }, after.HitScores);
        Assert.Equal((byte)0, after.BarRound);
    }

    [Fact]
    public void A_better_score_raises_the_quality_by_its_minigame_term()
    {
        // The same world seed: the same session seed, the same rolls; only the press differs.
        var played = New();
        var bar = StartAndHit(played);
        Press(played, TimingBar.BestOffsetMs(in bar, 300), roundTripMs: 100);
        int score = ScoreOf(played);
        Assert.Null(played.Forge.Finish(Character, 5, 1));
        played.Commit();

        var idle = New();
        StartAndHit(idle);
        Assert.Null(idle.Forge.Finish(Character, 5, 1));
        idle.Commit();

        int better = played.Committed.Single().Answer.Quality, worse = idle.Committed.Single().Answer.Quality;
        Assert.True(score > 0);
        Assert.Equal(Math.Min(score, played.Rules.Caps.Minigame), better - worse);
    }

    [Fact]
    public void The_score_is_capped_by_the_row_maximum()
    {
        var f = New();
        var row = Row(f);
        var capped = new ForgeChallengeDef
        {
            Id = row.Id, Kind = row.Kind, ActionId = row.ActionId, SpeedMinPermillePerSec = row.SpeedMinPermillePerSec,
            SpeedMaxPermillePerSec = row.SpeedMaxPermillePerSec, TargetMinPermille = row.TargetMinPermille,
            TargetMaxPermille = row.TargetMaxPermille, HalfWidthPermille = row.HalfWidthPermille, DurationMs = row.DurationMs,
            LatencyToleranceMs = row.LatencyToleranceMs, MaxLagMs = row.MaxLagMs, HitMaxScore = row.HitMaxScore, MaxScore = row.HitMaxScore + 5,
        };
        var challenge = new TimingBarChallenge(capped, f.Rules);
        Assert.Equal(ForgeReason.None, f.StartSword());
        var session = f.Forge.Session(Character)!;
        challenge.Begin(session);
        for (int round = 1; round <= 3; round++)
        {
            challenge.OnAction(session, Hit, f.NowMs);
            var bar = session.Challenge!.Bar(round);
            long best = TimingBar.BestOffsetMs(in bar, 300);
            f.NowMs += best + 10;
            Assert.Equal(ForgeHitOutcome.Scored, challenge.Input(session, round, best, 0, f.NowMs).Outcome);
        }
        Assert.True(session.Challenge!.ClosedScore > capped.MaxScore);
        Assert.Equal(capped.MaxScore, challenge.Score(session));
        challenge.Range(session, out int min, out int max);
        Assert.Equal((capped.MaxScore, capped.MaxScore), (min, max));
    }

    [Fact]
    public void The_jeweler_plays_no_challenge()
    {
        var f = New();
        var challenges = new ForgeChallenges(f.Rules);
        Assert.IsType<NoForgeChallenge>(challenges.Of("jeweler"));
        Assert.True(f.Rules.TryGet(ForgeKind.Mold, 3, out var ring)); // m3.moldRing
        Assert.Equal(0, challenges.MaxScore(ring));
        Assert.True(f.Rules.TryGet(ForgeKind.Recipe, RefineIngot, out var refine));
        Assert.Equal(0, challenges.MaxScore(refine)); // recipes roll no quality: no round
    }
}
