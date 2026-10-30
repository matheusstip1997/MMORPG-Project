using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Jobs;

namespace Shared.Tests.Data;

/// <summary>
/// The job tables of WP-M3-1 (spec 5.1): <see cref="JobDef"/>, <see cref="JobSettings"/>, <see cref="JobLevelCurve"/>,
/// <see cref="JobXpWeights"/>; the committed data passes, holds the user's decisions (Blacksmith and Jeweler, both secondary,
/// from level 12) and builds <see cref="JobRules"/>.
/// </summary>
public class JobTablesTests
{
    private static JobXpWeights Weights(string id, JobActivity activity, int window = 10) => new()
    {
        Id = id, Activity = activity, BaseXp = 10, TierPercent = new[] { 100, 150 }, RepeatWindow = window, RepeatStepPercent = 10,
        RepeatFloorPercent = 20,
    };

    private static ConfigSet Set(IEnumerable<JobDef>? jobs = null, IEnumerable<JobLevelCurve>? curve = null,
        IEnumerable<JobXpWeights>? weights = null, int maxLevel = 4, IEnumerable<ItemDef>? items = null)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ProgressionSettings), 2, new object[] { new ProgressionSettings { MaxLevel = 130 } }));
        set.Add(new ConfigTable(typeof(ItemDef), 2, (items ?? new[]
        {
            new ItemDef { Id = "mold", NetId = 1, Category = ItemCategory.MoldOrTool, MaxStack = 1, VisualId = "drop", NameId = "item" },
        }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(JobDef), 1, (jobs ?? new[]
        {
            new JobDef { Id = "blacksmith", NetId = 1, NameId = "job", AllowedAsSecondary = true, MinLevel = 12, StarterItemIds = new[] { "mold" } },
            new JobDef { Id = "jeweler", NetId = 2, NameId = "job", AllowedAsSecondary = true, MinLevel = 12 },
        }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(JobSettings), 1, new object[] { new JobSettings { MaxLevel = maxLevel, PointsPerLevel = 1, RecentOpKeys = 8 } }));
        set.Add(new ConfigTable(typeof(JobLevelCurve), 1, (curve ?? new[]
        {
            new JobLevelCurve { Level = 1, XpToNext = 100 }, new JobLevelCurve { Level = 2, XpToNext = 150 },
            new JobLevelCurve { Level = 3, XpToNext = 200 },
        }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(JobXpWeights), 1, (weights ?? new[]
        {
            Weights("harvest", JobActivity.Harvest), Weights("craft", JobActivity.Craft, 20),
        }).Cast<object>().ToList()));
        return set;
    }

    private static List<string> Errors(ConfigSet set) =>
        TableValidator.Validate(set).Where(e => e.Code != ValidationCodes.BrokenRef).Select(e => e.Table + "." + e.Field + ": " + e.Message)
            .ToList();

    [Fact]
    public void A_consistent_set_passes_and_builds_the_rules()
    {
        var set = Set();
        Assert.Empty(Errors(set));
        var rules = JobRules.From(set);
        Assert.NotNull(rules);
        Assert.Equal(new[] { "blacksmith", "jeweler" }, rules!.Jobs.Select(j => j.Id));
        Assert.Equal("jeweler", rules.FindByNetId(2)?.Id);
        Assert.Equal(150, rules.XpToNext(2));
        Assert.Equal(0, rules.XpToNext(4)); // the cap
        Assert.Equal(20, rules.WindowCapacity); // the largest repeat window
        Assert.Null(rules.WeightsOf(JobActivity.ItemUse)); // no row: no XP
    }

    [Fact]
    public void The_curve_must_cover_every_level_below_the_cap_in_order_never_decreasing()
    {
        Assert.Contains(Errors(Set(maxLevel: 5)), e => e.Contains("JobSettings.maxLevel - 1"));
        Assert.Contains(Errors(Set(curve: new[] { new JobLevelCurve { Level = 2, XpToNext = 100 } }, maxLevel: 2)),
            e => e.Contains("must be level 1"));
        Assert.Contains(Errors(Set(curve: new[]
        {
            new JobLevelCurve { Level = 1, XpToNext = 200 }, new JobLevelCurve { Level = 2, XpToNext = 100 },
            new JobLevelCurve { Level = 3, XpToNext = 300 },
        })), e => e.Contains("below the previous"));
        Assert.Null(JobRules.From(Set(maxLevel: 5))); // a curve that does not fit the cap builds nothing
    }

    [Fact]
    public void Job_net_ids_are_unique_and_starter_items_are_instances_listed_once()
    {
        Assert.NotEmpty(Errors(Set(jobs: new[]
        {
            new JobDef { Id = "a", NetId = 1, NameId = "job", MinLevel = 1 }, new JobDef { Id = "b", NetId = 1, NameId = "job", MinLevel = 1 },
        })));
        Assert.Contains(Errors(Set(jobs: new[] { new JobDef { Id = "a", NetId = 1, NameId = "job", MinLevel = 1, StarterItemIds = new[] { "mold", "mold" } } })),
            e => e.Contains("twice"));
        var stackable = new[] { new ItemDef { Id = "mold", NetId = 1, Category = ItemCategory.Material, MaxStack = 20, VisualId = "drop", NameId = "item" } };
        Assert.Contains(Errors(Set(items: stackable)), e => e.Contains("starter items are instances"));
        Assert.Contains(Errors(Set(jobs: new[] { new JobDef { Id = "a", NetId = 1, NameId = "job", MinLevel = 200 } })),
            e => e.Contains("above ProgressionSettings.maxLevel"));
    }

    [Fact]
    public void Each_activity_has_at_most_one_weight_row_and_a_real_activity_and_tiers()
    {
        Assert.Contains(Errors(Set(weights: new[] { Weights("a", JobActivity.Harvest), Weights("b", JobActivity.Harvest) })),
            e => e.Contains("more than one row"));
        Assert.Contains(Errors(Set(weights: new[] { Weights("a", JobActivity.None) })), e => e.Contains("not a job activity"));
        var noTiers = Weights("a", JobActivity.Harvest);
        noTiers.TierPercent = System.Array.Empty<int>();
        Assert.Contains(Errors(Set(weights: new[] { noTiers })), e => e.Contains("at least one tier"));
    }

    [Fact]
    public void The_committed_job_tables_hold_the_user_decisions()
    {
        var set = RepositoryTables();
        var rules = JobRules.From(set);
        Assert.NotNull(rules);
        Assert.Equal(new[] { "blacksmith", "jeweler" }, rules!.Jobs.Select(j => j.Id));
        Assert.All(rules.Jobs, job => Assert.True(job.AllowedAsSecondary));
        Assert.All(rules.Jobs, job => Assert.Equal(12, job.MinLevel));
        var master = NetIds<NpcDef>(set).Single(n => n.Id == "m1.jobMaster");
        Assert.Equal(NpcKind.JobMaster, master.Kind);
        Assert.Equal(new[] { NpcInteraction.Jobs }, master.Interactions);
        Assert.Equal(new[] { "character", "inventory", "skills", "jobs", "book", "journal" },
            NetIds<HudLayout>(set).Single(r => r.Id == "shortcut_row").Buttons);
    }

    private static List<T> NetIds<T>(ConfigSet set) where T : class =>
        set.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>().ToList() : new List<T>();

    private static ConfigSet RepositoryTables()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var tables = Path.Combine(directory!.FullName, "data", "tables");
        var errors = new List<ValidationError>();
        var set = ConfigTableReader.LoadAndValidate(ConfigTables.All, name =>
        {
            var path = Path.Combine(tables, name + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }, errors);
        Assert.Empty(errors);
        return set;
    }
}
