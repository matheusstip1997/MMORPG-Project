using Shared.Data.Tables;
using Shared.Simulation.Crafting;

namespace Shared.Tests.Simulation.Crafting;

/// <summary>
/// The forge timing bar's pure math (spec 5.3 M3 decision 12; WP-M3-10): the marker's bouncing position, the score of a press
/// on a grid of offsets (the centre at the maximum, falling to 0 at the window's edge, 0 outside the window or the round),
/// the seeded roll of a round, and the perfect press the bots aim at. The numbers are placeholders; the tests pin the rules.
/// </summary>
public class TimingBarTests
{
    // 1000 permille per second: the marker is at offsetMs permille during the first second, then comes back.
    private static readonly TimingBarParams Bar = new(speedPermillePerSec: 1_000, targetPermille: 500, halfWidthPermille: 100,
        durationMs: 4_000, hitMaxScore: 30);

    private static ForgeChallengeDef Row() => new()
    {
        Id = "blacksmith", Kind = ForgeChallengeKind.TimingBar, ActionId = "hit", SpeedMinPermillePerSec = 600,
        SpeedMaxPermillePerSec = 1_200, TargetMinPermille = 250, TargetMaxPermille = 750, HalfWidthPermille = 120, DurationMs = 4_000,
        LatencyToleranceMs = 100, MaxLagMs = 400, HitMaxScore = 30, MaxScore = 150,
    };

    [Theory]
    // offset ms, position permille
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(250, 250)]
    [InlineData(1_000, 1_000)] // the far end
    [InlineData(1_250, 750)] // coming back
    [InlineData(2_000, 0)] // back at the start
    [InlineData(2_300, 300)] // the next lap
    [InlineData(3_999, 1)]
    public void The_marker_bounces_along_the_bar(long offsetMs, int position) =>
        Assert.Equal(position, TimingBar.Position(in Bar, offsetMs));

    [Theory]
    // offset ms, score: centre 500 = 30, falling linearly (rounded down) to 0 at 100 permille from it
    [InlineData(500, 30)]
    [InlineData(1_500, 30)] // the centre on the way back
    [InlineData(450, 15)] // 50 off: half
    [InlineData(550, 15)]
    [InlineData(475, 22)] // 25 off: 30 * 75 / 100 = 22.5, rounded down
    [InlineData(401, 0)] // 99 off: 30 * 1 / 100 = 0.3, rounded down
    [InlineData(400, 0)] // on the edge
    [InlineData(600, 0)]
    [InlineData(100, 0)] // far outside the window
    [InlineData(-1, 0)] // before the round
    [InlineData(4_001, 0)] // after the round
    public void A_press_scores_by_its_distance_from_the_centre(long offsetMs, int score) =>
        Assert.Equal(score, TimingBar.Score(in Bar, offsetMs));

    [Fact]
    public void A_press_after_the_round_scores_0_even_on_the_centre()
    {
        var shortBar = new TimingBarParams(1_000, 500, 100, durationMs: 400, hitMaxScore: 30);
        Assert.Equal(0, TimingBar.Score(in shortBar, 500));
    }

    [Fact]
    public void The_score_never_exceeds_the_hit_maximum()
    {
        for (long offset = -10; offset <= Bar.DurationMs + 10; offset++)
            Assert.InRange(TimingBar.Score(in Bar, offset), 0, Bar.HitMaxScore);
    }

    [Fact]
    public void A_round_rolls_from_the_session_seed_within_the_row()
    {
        var row = Row();
        for (int round = 1; round <= 32; round++)
        {
            var bar = TimingBar.Roll(0xC0FFEEUL, round, row);
            Assert.Equal(bar.SpeedPermillePerSec, TimingBar.Roll(0xC0FFEEUL, round, row).SpeedPermillePerSec); // replayable (rule 6)
            Assert.Equal(bar.TargetPermille, TimingBar.Roll(0xC0FFEEUL, round, row).TargetPermille);
            Assert.InRange(bar.SpeedPermillePerSec, row.SpeedMinPermillePerSec, row.SpeedMaxPermillePerSec);
            Assert.InRange(bar.TargetPermille, row.TargetMinPermille, row.TargetMaxPermille);
            Assert.Equal(row.HalfWidthPermille, bar.HalfWidthPermille);
            Assert.Equal(row.DurationMs, bar.DurationMs);
            Assert.Equal(row.HitMaxScore, bar.HitMaxScore);
        }
        // Rounds and sessions differ (one stream per round).
        var targets = Enumerable.Range(1, 16).Select(r => TimingBar.Roll(0xC0FFEEUL, r, row).TargetPermille).Distinct().Count();
        Assert.True(targets > 1);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(600L)]
    [InlineData(1_700L)]
    public void The_best_offset_lands_on_the_centre(long afterMs)
    {
        var row = Row();
        for (ulong seed = 1; seed <= 50; seed++)
        {
            var bar = TimingBar.Roll(seed, 1, row);
            long best = TimingBar.BestOffsetMs(in bar, afterMs);
            Assert.True(best >= afterMs, $"seed {seed}: {best} before {afterMs}");
            Assert.True(best <= bar.DurationMs);
            Assert.True(TimingBar.Score(in bar, best) >= bar.HitMaxScore - 1, $"seed {seed}: {TimingBar.Score(in bar, best)} at {best}");
        }
    }

    [Fact]
    public void No_best_offset_after_the_round() =>
        Assert.Equal(-1, TimingBar.BestOffsetMs(in Bar, Bar.DurationMs));
}
