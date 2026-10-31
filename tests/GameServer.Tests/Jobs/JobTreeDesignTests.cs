using System.Text.Json.Nodes;
using GameServer.Host.Config;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Jobs;

namespace GameServer.Tests.Jobs;

/// <summary>
/// The design file stays the source (WP-M3-7): <c>docs/design/job-trees.json</c> (the user's design talk of 2026-10-30) and
/// the committed tables <c>JobTree</c>, <c>JobTreeSettings</c>, <c>JobBonusCap</c> and <c>JobSettings</c> say the same, field
/// by field, so a change to one without the other fails CI.
/// </summary>
public class JobTreeDesignTests
{
    private static readonly JsonNode Design =
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root(), "docs", "design", "job-trees.json")))!;

    private static readonly ConfigSet Tables = ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All).Set;

    private static int[] Ints(JsonNode? node) => node!.AsArray().Select(n => (int)n!).ToArray();

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    [Fact]
    public void Every_design_node_is_a_JobTree_row_with_the_same_fields()
    {
        var nodes = Design["nodes"]!.AsArray();
        Assert.Equal(nodes.Count, JobTree.Rows(Tables).Count);
        foreach (var node in nodes)
        {
            string id = (string)node!["id"]!;
            var row = JobTree.Find(Tables, id);
            Assert.True(row != null, $"{id} has no JobTree row");
            Assert.Equal((string)node["job"]!, row!.JobId);
            Assert.Equal(((int)node["row"]!, (int)node["col"]!), (row.Row, row.Col));
            Assert.Equal((int)node["maxLevel"]!, row.MaxLevel);
            Assert.Equal(Ints(node["costPerLevel"]), row.CostPerLevel);
            var requires = node["requires"]!.AsArray();
            Assert.Equal(requires.Select(r => (string)r!["node"]!), row.Requires);
            Assert.Equal(requires.Select(r => (int)r!["level"]!), row.RequiresLevels);
            var bonus = node["bonus"]!;
            Assert.Equal((string)bonus["kind"]!, Camel(row.BonusKind.ToString()));
            Assert.Equal(bonus["activities"]!.AsArray().Select(a => (string)a!), row.Activities.Select(a => Camel(a.ToString())));
            var perLevel = bonus["perLevel"]!.AsArray();
            if (row.BonusKind == JobBonusKind.UnlockMoldTiers)
            {
                Assert.Equal(Ints(Assert.Single(perLevel)), row.MoldTiers);
                Assert.Empty(row.ValuePerLevel);
            }
            else
            {
                Assert.Equal(Ints(perLevel), row.ValuePerLevel);
            }
            Assert.Equal((string)node["name"]!, NetIds.Rows<UiStrings>(Tables).Single(s => s.Id == row.NameId).Text);
        }
    }

    [Fact]
    public void The_settings_and_caps_are_the_design_files()
    {
        var settings = Design["settings"]!;
        Assert.Equal(Ints(settings["rowMinJobLevel"]!["values"]), JobTreeSettings.Find(Tables)!.RowMinJobLevel);
        var jobs = JobSettings.Find(Tables)!;
        Assert.Equal((int)settings["secondaryXpPercent"]!["value"]!, jobs.SecondaryXpPercent);
        Assert.Equal((int)settings["secondaryBankMaxLevels"]!["value"]!, jobs.SecondaryBankLevels);
        int cost = (int)settings["pointCostPerLevel"]!["value"]!;
        Assert.All(JobTree.Rows(Tables), row => Assert.All(row.CostPerLevel, c => Assert.Equal(cost, c)));

        var caps = Design["caps"]!["values"]!.AsObject();
        Assert.Equal(caps.Count, JobBonusCap.Rows(Tables).Count);
        foreach (var (kind, value) in caps)
            Assert.Equal((int)value!, JobBonusCap.Rows(Tables).Single(c => Camel(c.Kind.ToString()) == kind).Cap);
    }

    [Fact]
    public void The_design_files_bonus_kinds_are_the_enum()
    {
        var kinds = Design["bonusKinds"]!.AsArray().Select(k => (string)k!["id"]!);
        Assert.Equal(kinds, Enum.GetValues<JobBonusKind>().Where(k => k != JobBonusKind.None).Select(k => Camel(k.ToString())));
    }
}
