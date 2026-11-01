using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;

namespace GameServer.Crafting;

/// <summary>
/// The forge timing bar (spec 5.3 M3 decision 12; WP-M3-10), one job's <see cref="ForgeChallengeDef"/> row of kind
/// <c>timingBar</c>. Every use of the row's action opens a round whose speed and target come from the session's logged seed
/// (<see cref="TimingBar.Roll"/>, its own stream per round). One press per round: judged in time on the server's timeline
/// (<see cref="ForgeHitClock"/>, the round trip from the transport), scored by <see cref="TimingBar.Score"/>; a second press is
/// a repeat, and a round still open when the next action or the finish comes is dropped (0). The session's score is its
/// closed rounds, capped by the row's <c>maxScore</c> (and by <c>QualityCaps</c> minigame in <c>QualityCalc</c>).
/// </summary>
public sealed class TimingBarChallenge : IForgeChallenge
{
    private readonly ForgeChallengeDef _row;
    private readonly int _actionNetId;

    public TimingBarChallenge(ForgeChallengeDef row, ForgeRules rules)
    {
        _row = row;
        foreach (var definition in rules.Definitions)
        {
            foreach (var action in definition.Actions)
            {
                if (action.Id == row.ActionId)
                    _actionNetId = action.NetId;
            }
        }
    }

    public int MaxScore(ForgeDefinition definition) =>
        _actionNetId != 0 && definition.RollsQuality && definition.AllowsAction(_actionNetId) ? _row.MaxScore : 0;

    public int Score(ForgeSession session)
    {
        if (session.Challenge is not { } run)
            return 0;
        return Math.Min(run.ClosedScore, MaxScore(session.Definition));
    }

    public void Begin(ForgeSession session)
    {
        if (MaxScore(session.Definition) > 0)
            session.Challenge = new ForgeChallengeRun(_row, _actionNetId);
    }

    public ForgeHit OnAction(ForgeSession session, int actionNetId, long nowMs)
    {
        if (session.Challenge is not { } run)
            return ForgeHit.None;
        var dropped = Close(session);
        if (actionNetId == run.ActionNetId)
            run.TryOpen(TimingBar.Roll(session.Seed, run.Count + 1, _row), nowMs);
        return dropped;
    }

    public ForgeHit Input(ForgeSession session, int round, long offsetMs, int roundTripMs, long nowMs)
    {
        if (session.Challenge is not { } run || round < 1 || round > run.Count)
            return new ForgeHit(round, 0, 0, ForgeHitOutcome.Impossible);
        if (round != run.Open)
            return new ForgeHit(round, 0, 0, ForgeHitOutcome.Repeat);
        var bar = run.Bar(round);
        var outcome = ForgeHitClock.Judge(nowMs - run.IssuedAtMs(round), offsetMs, roundTripMs, _row.LatencyToleranceMs, _row.MaxLagMs,
            bar.DurationMs, out long lag);
        int score = 0;
        if (outcome == ForgeHitOutcome.None)
        {
            score = TimingBar.Score(in bar, offsetMs);
            outcome = score > 0 ? ForgeHitOutcome.Scored : ForgeHitOutcome.Missed;
        }
        run.CloseOpen(score, outcome);
        return new ForgeHit(round, score, lag, outcome);
    }

    public ForgeHit Close(ForgeSession session)
    {
        if (session.Challenge is not { Open: > 0 } run)
            return ForgeHit.None;
        int round = run.Open;
        run.CloseOpen(0, ForgeHitOutcome.Dropped);
        return new ForgeHit(round, 0, 0, ForgeHitOutcome.Dropped);
    }

    public void Range(ForgeSession session, out int min, out int max)
    {
        min = max = 0;
        if (session.Challenge is not { } run)
            return;
        int cap = MaxScore(session.Definition);
        min = Math.Min(run.ClosedScore, cap);
        max = Math.Min(min + (run.Open > 0 ? run.Bar(run.Open).HitMaxScore : 0), cap);
    }

    public void Describe(ForgeSession session, ForgeState state)
    {
        if (session.Challenge is not { } run)
            return; // a recipe, or a mold without the action: no bar
        state.ChallengeKind = ForgeChallengeKind.TimingBar;
        state.ChallengeMax = (ushort)Math.Min(MaxScore(session.Definition), ushort.MaxValue);
        // The state is a new message anyway; no array until a round closed (orchestrator review, low).
        var scores = run.ClosedCount == 0 ? Array.Empty<ushort>() : new ushort[run.ClosedCount];
        int at = 0;
        for (int round = 1; round <= run.Count; round++)
        {
            if (round != run.Open)
                scores[at++] = (ushort)Math.Min(run.ScoreOf(round), ushort.MaxValue);
        }
        state.HitScores = scores;
        if (run.Open == 0)
            return;
        var bar = run.Bar(run.Open);
        state.BarRound = (byte)run.Open;
        state.BarSpeed = (ushort)Math.Min(bar.SpeedPermillePerSec, ushort.MaxValue);
        state.BarTarget = (ushort)bar.TargetPermille;
        state.BarHalfWidth = (ushort)bar.HalfWidthPermille;
        state.BarDurationMs = (ushort)Math.Min(bar.DurationMs, ushort.MaxValue);
        state.BarHitMax = (ushort)Math.Min(bar.HitMaxScore, ushort.MaxValue);
    }
}
