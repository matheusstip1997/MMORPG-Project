using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Jobs;

namespace Shared.Tests.Data;

/// <summary>
/// The job tree tables of WP-M3-7 (spec 5.1 R-5.1-04; <c>docs/design/job-trees.md</c>): <see cref="JobTree"/>,
/// <see cref="JobTreeSettings"/>, <see cref="JobBonusCap"/> and the secondary-job fields of <see cref="JobSettings"/>; every
/// validator rule, and the committed data builds <see cref="JobTreeRules"/>.
/// </summary>
public class JobTreeTablesTests
{
    internal static JobTree Node(string id, int row = 0, int col = 0, JobBonusKind kind = JobBonusKind.YieldPercent, int max = 2,
        string job = "blacksmith", string[]? requires = null, int[]? levels = null) => new()
    {
        Id = id, JobId = job, NameId = "n", TextId = "t", Row = row, Col = col, MaxLevel = max,
        CostPerLevel = Enumerable.Repeat(1, max).ToArray(), BonusKind = kind, Activities = new[] { JobActivity.Harvest },
        ValuePerLevel = kind == JobBonusKind.UnlockMoldTiers ? System.Array.Empty<int>() : Enumerable.Range(1, max).Select(l => l * 4).ToArray(),
        MoldTiers = kind == JobBonusKind.UnlockMoldTiers ? new[] { 2, 3 } : System.Array.Empty<int>(),
        Requires = requires ?? System.Array.Empty<string>(), RequiresLevels = levels ?? System.Array.Empty<int>(),
    };

    internal static ConfigSet Set(IEnumerable<JobTree> nodes, int[]? rows = null, IEnumerable<JobBonusCap>? caps = null, int maxLevel = 20)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(JobSettings), 1, new object[]
        {
            new JobSettings { MaxLevel = maxLevel, PointsPerLevel = 1, RecentOpKeys = 8, SecondaryXpPercent = 150, SecondaryBankLevels = 1 },
        }));
        set.Add(new ConfigTable(typeof(JobTreeSettings), 1, new object[] { new JobTreeSettings { RowMinJobLevel = rows ?? new[] { 1, 5, 10, 15 } } }));
        set.Add(new ConfigTable(typeof(JobBonusCap), 1, (caps ?? new[]
        {
            new JobBonusCap { Id = "yield", Kind = JobBonusKind.YieldPercent, Cap = 25 },
            new JobBonusCap { Id = "quality", Kind = JobBonusKind.QualityBonus, Cap = 12 },
        }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(JobTree), 1, nodes.Cast<object>().ToList()));
        return set;
    }

    private static List<string> Errors(ConfigSet set) =>
        TableValidator.Validate(set).Where(e => e.Code != ValidationCodes.BrokenRef).Select(e => e.Table + "." + e.Field + ": " + e.Message)
            .ToList();

    [Fact]
    public void A_consistent_tree_passes_and_builds_the_rules()
    {
        var set = Set(new[]
        {
            Node("a"), Node("b", row: 1, requires: new[] { "a" }, levels: new[] { 1 }), Node("m", row: 3, kind: JobBonusKind.UnlockMoldTiers, max: 1),
        });
        Assert.Empty(Errors(set));
        var rules = JobTreeRules.From(set)!;
        Assert.Equal(new[] { "a", "b", "m" }, rules.Nodes("blacksmith").Select(n => n.Id));
        Assert.Equal((5, 25), (rules.MinJobLevel(1), rules.Cap(JobBonusKind.YieldPercent)));
    }

    [Fact]
    public void Row_rules_catch_lengths_kinds_and_unlocks()
    {
        var shortCost = Node("a");
        shortCost.CostPerLevel = new[] { 1 };
        Assert.Contains(Errors(Set(new[] { shortCost })), e => e.Contains("costPerLevel") || e.Contains("CostPerLevel"));
        var falling = Node("a");
        falling.ValuePerLevel = new[] { 8, 4 };
        Assert.Contains(Errors(Set(new[] { falling })), e => e.Contains("gives less"));
        var noKind = Node("a", kind: JobBonusKind.None);
        Assert.Contains(Errors(Set(new[] { noKind })), e => e.Contains("not a job bonus kind"));
        var noActivity = Node("a");
        noActivity.Activities = System.Array.Empty<JobActivity>();
        Assert.Contains(Errors(Set(new[] { noActivity })), e => e.Contains("at least one activity"));
        var bigUnlock = Node("m", kind: JobBonusKind.UnlockMoldTiers, max: 2);
        Assert.Contains(Errors(Set(new[] { bigUnlock })), e => e.Contains("max level 1"));
        var tiered = Node("a");
        tiered.MoldTiers = new[] { 2 };
        Assert.Contains(Errors(Set(new[] { tiered })), e => e.Contains("only an unlockMoldTiers node"));
        Assert.Contains(Errors(Set(new[] { Node("a", requires: new[] { "a" }, levels: new[] { 1 }) })), e => e.Contains("cannot require itself"));
    }

    [Fact]
    public void Set_rules_catch_rows_links_cycles_cells_and_caps()
    {
        Assert.Contains(Errors(Set(new[] { Node("a", row: 4) })), e => e.Contains("rowMinJobLevel"));
        Assert.Contains(Errors(Set(new[] { Node("a", row: 1), Node("b", row: 0, col: 1, requires: new[] { "a" }, levels: new[] { 1 }) })),
            e => e.Contains("after this node's row"));
        Assert.Contains(Errors(Set(new[] { Node("a", job: "jeweler"), Node("b", col: 1, requires: new[] { "a" }, levels: new[] { 1 }) })),
            e => e.Contains("not the blacksmith tree"));
        Assert.Contains(Errors(Set(new[] { Node("a"), Node("b", col: 1, requires: new[] { "a" }, levels: new[] { 3 }) })),
            e => e.Contains("above its max level"));
        Assert.Contains(Errors(Set(new[]
        {
            Node("a", requires: new[] { "b" }, levels: new[] { 1 }), Node("b", col: 1, requires: new[] { "a" }, levels: new[] { 1 }),
        })), e => e.Contains("requires itself through"));
        Assert.Contains(Errors(Set(new[] { Node("a"), Node("b") })), e => e.Contains("already sits at row 0, column 0"));
        Assert.Contains(Errors(Set(new[] { Node("a", kind: JobBonusKind.ActionCount) })), e => e.Contains("has no JobBonusCap row"));
    }

    [Fact]
    public void Settings_and_cap_rules()
    {
        Assert.Contains(Errors(Set(new[] { Node("a") }, rows: new[] { 1, 5, 5 })), e => e.Contains("not after row"));
        Assert.Contains(Errors(Set(new[] { Node("a") }, rows: new[] { 1, 30 })), e => e.Contains("above JobSettings.maxLevel"));
        Assert.Contains(Errors(Set(new[] { Node("a") }, caps: new[]
        {
            new JobBonusCap { Id = "a", Kind = JobBonusKind.YieldPercent, Cap = 1 }, new JobBonusCap { Id = "b", Kind = JobBonusKind.YieldPercent, Cap = 2 },
        })), e => e.Contains("more than one row"));
        Assert.Contains(Errors(Set(new[] { Node("a") }, caps: new[]
        {
            new JobBonusCap { Id = "y", Kind = JobBonusKind.YieldPercent, Cap = 1 }, new JobBonusCap { Id = "u", Kind = JobBonusKind.UnlockMoldTiers, Cap = 1 },
        })), e => e.Contains("has no cap"));
    }

    [Fact]
    public void The_committed_tables_build_both_trees_with_the_design_numbers()
    {
        var set = RepositoryTables();
        var rules = JobRules.From(set)!;
        var tree = rules.Tree!;
        Assert.Equal(9, tree.Nodes("blacksmith").Count);
        Assert.Equal(9, tree.Nodes("jeweler").Count);
        Assert.Equal(32, tree.Nodes("blacksmith").Sum(n => n.MaxLevel)); // decision 1
        Assert.Equal(35, tree.Nodes("jeweler").Sum(n => n.MaxLevel));
        Assert.Equal((150, 1), (rules.Settings.SecondaryXpPercent, rules.Settings.SecondaryBankLevels));
        Assert.Equal(new[] { 150, 225 }, rules.SecondaryXpToNextByLevel.Take(2));
    }

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
