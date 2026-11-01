using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// <see cref="ForgeChallengeDef"/> (spec 5.3 M3 decision 12; WP-M3-10): one test per validator rule, the coverage rule (one row
/// per job) and the committed rows (the Blacksmith's timing bar on hit, the Jeweler none).
/// </summary>
public class ForgeChallengeDefTests
{
    private static ForgeChallengeDef Bar() => new()
    {
        Id = "smith", Kind = ForgeChallengeKind.TimingBar, ActionId = "hit", SpeedMinPermillePerSec = 600, SpeedMaxPermillePerSec = 1_200,
        TargetMinPermille = 250, TargetMaxPermille = 750, HalfWidthPermille = 120, DurationMs = 4_000, LatencyToleranceMs = 100,
        MaxLagMs = 400, HitMaxScore = 30, MaxScore = 150,
    };

    private static ConfigSet Set(params ForgeChallengeDef[] rows)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(JobDef), 1, new object[]
        {
            new JobDef { Id = "smith", NetId = 1, NameId = "job", MinLevel = 12 }, new JobDef { Id = "jeweler", NetId = 2, NameId = "job", MinLevel = 12 },
        }));
        set.Add(new ConfigTable(typeof(ForgeActionDef), 1, new object[] { new ForgeActionDef { Id = "hit", NetId = 2, NameId = "a", QualityMax = 60 } }));
        set.Add(new ConfigTable(typeof(QualityCaps), 1, new object[] { new QualityCaps { Id = "minigame", Cap = 150 } }));
        set.Add(new ConfigTable(typeof(StartingValuesM1), 1, new object[] { new StartingValuesM1 { LagCompensationCapMs = 200 } }));
        set.Add(new ConfigTable(typeof(ForgeChallengeDef), 1, rows.Cast<object>().ToList()));
        return set;
    }

    private static List<ValidationError> Errors(ConfigSet set) =>
        TableValidator.Validate(set).Concat(ForgeCoverage.Check(set)).Where(e => e.Table == "ForgeChallengeDef").ToList();

    private static bool Has(ForgeChallengeDef row, string field) =>
        Errors(Set(row, new ForgeChallengeDef { Id = "jeweler" })).Any(e => e.Table == "ForgeChallengeDef" && e.Field == field);

    [Fact]
    public void A_timing_bar_and_a_none_row_are_valid() =>
        Assert.Empty(Errors(Set(Bar(), new ForgeChallengeDef { Id = "jeweler", Kind = ForgeChallengeKind.None })));

    [Fact]
    public void Every_job_has_a_row() =>
        Assert.Contains(Errors(Set(Bar())), e => e.Table == "ForgeChallengeDef" && e.Message.Contains("jeweler"));

    [Fact]
    public void A_none_row_names_no_action()
    {
        var row = new ForgeChallengeDef { Id = "smith", ActionId = "hit" };
        Assert.True(Has(row, nameof(ForgeChallengeDef.ActionId)));
    }

    [Fact]
    public void A_timing_bar_names_its_action()
    {
        var row = Bar();
        row.ActionId = string.Empty;
        Assert.True(Has(row, nameof(ForgeChallengeDef.ActionId)));
    }

    [Fact]
    public void The_action_must_exist()
    {
        var row = Bar();
        row.ActionId = "stir";
        Assert.True(Has(row, nameof(ForgeChallengeDef.ActionId)));
    }

    [Fact]
    public void Speeds_and_targets_are_ordered()
    {
        var row = Bar();
        row.SpeedMaxPermillePerSec = 500;
        Assert.True(Has(row, nameof(ForgeChallengeDef.SpeedMaxPermillePerSec)));
        row = Bar();
        row.TargetMaxPermille = 200;
        Assert.True(Has(row, nameof(ForgeChallengeDef.TargetMaxPermille)));
    }

    [Fact]
    public void The_window_stays_on_the_bar()
    {
        var row = Bar();
        row.TargetMaxPermille = 900; // 900 + 120 > 1000
        Assert.True(Has(row, nameof(ForgeChallengeDef.HalfWidthPermille)));
        row = Bar();
        row.HalfWidthPermille = 0;
        Assert.True(Has(row, nameof(ForgeChallengeDef.HalfWidthPermille)));
    }

    [Fact]
    public void A_round_gives_the_slowest_marker_a_full_sweep()
    {
        var row = Bar();
        row.DurationMs = 1_000; // 600 permille per second for 1 s: not the whole bar
        Assert.True(Has(row, nameof(ForgeChallengeDef.DurationMs)));
    }

    [Fact]
    public void The_tolerance_is_within_the_clock_sync_cap_and_the_ceiling_above_it()
    {
        var row = Bar();
        row.LatencyToleranceMs = 250;
        Assert.True(Has(row, nameof(ForgeChallengeDef.LatencyToleranceMs)));
        row = Bar();
        row.MaxLagMs = 150;
        Assert.True(Has(row, nameof(ForgeChallengeDef.MaxLagMs)));
    }

    [Fact]
    public void Scores_stay_within_the_minigame_cap()
    {
        var row = Bar();
        row.MaxScore = 200;
        Assert.True(Has(row, nameof(ForgeChallengeDef.MaxScore)));
        row = Bar();
        row.HitMaxScore = 160; // above maxScore
        Assert.True(Has(row, nameof(ForgeChallengeDef.MaxScore)));
    }

    [Fact]
    public void The_committed_rows_give_the_blacksmith_a_timing_bar_on_hit()
    {
        var set = Simulation.Crafting.ForgeMathTests.LoadRepository();
        Assert.Empty(ForgeCoverage.Check(set));
        var blacksmith = ForgeChallengeDef.ForJob(set, "blacksmith");
        Assert.NotNull(blacksmith);
        Assert.Equal(ForgeChallengeKind.TimingBar, blacksmith!.Kind);
        Assert.Equal("hit", blacksmith.ActionId);
        Assert.Equal(ForgeChallengeKind.None, ForgeChallengeDef.ForJob(set, "jeweler")?.Kind);
    }
}
