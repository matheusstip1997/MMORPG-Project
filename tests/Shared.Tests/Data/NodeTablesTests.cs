using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// The resource node tables (spec 5.2, WP-M3-4): <see cref="NodeDef"/> (a job, a tool type, both tiers on the tool ladder
/// of <see cref="GatherSettings"/>, unique net ids, both looks in <see cref="EntityVisualDef"/>) and <see cref="GatherNodes"/>
/// (every row names a node type). Marker checks are the world loader's (GameServer.Tests WorldLoaderTests).
/// </summary>
public class NodeTablesTests
{
    private static NodeDef Node(string id, int netId, int minToolTier = 1, int nodeTier = 1) => new()
    {
        Id = id, NetId = netId, JobId = "blacksmith", ToolType = ToolType.Pickaxe, MinToolTier = minToolTier,
        NodeTier = nodeTier, RespawnMs = 60_000, ChannelMs = 3_000, ReachCm = 250, VisualId = "rock", DepletedVisualId = "rubble",
    };

    private static ConfigSet Set(IEnumerable<NodeDef> nodes, IEnumerable<GatherNodes>? placement = null, int tierCount = 3)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(GatherSettings), 1, new object[]
        {
            new GatherSettings
            {
                ToolTierCount = tierCount, HarvestRetentionPermille = 950, ExpectedHarvestsPerPlayerMilli = 2_000, MinRespawnMs = 10_000,
            },
        }));
        set.Add(new ConfigTable(typeof(EntityVisualDef), 1, new object[]
        {
            new EntityVisualDef { Id = "rock", PrefabAddress = "Visuals/Nodes/OreRock", ScalePercent = 100 },
            new EntityVisualDef { Id = "rubble", PrefabAddress = "Visuals/Nodes/Depleted", ScalePercent = 100 },
        }));
        set.Add(new ConfigTable(typeof(NodeDef), 1, nodes.Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(GatherNodes), 1,
            (placement ?? new[] { new GatherNodes { MarkerId = "field1.node.ring1.a", NodeId = "ore.t1" } }).Cast<object>().ToList()));
        return set;
    }

    private static List<string> Errors(ConfigSet set) =>
        TableValidator.Validate(set).Where(e => e.Table is "NodeDef" or "GatherNodes" or "GatherSettings")
            .Select(e => e.Table + "." + e.Field + ": " + e.Message).ToList();

    [Fact]
    public void The_committed_rows_shape_passes()
    {
        Assert.Empty(Errors(Set(new[] { Node("ore.t1", 1), Node("ore.t2", 2, 2, 2), Node("ore.t3", 3, 3, 3) })));
    }

    [Fact]
    public void R_5_2_02_tiers_stay_on_the_tool_ladder()
    {
        var errors = Errors(Set(new[] { Node("ore.t1", 1), Node("ore.t4", 2, minToolTier: 4, nodeTier: 4) }));

        Assert.Contains(errors, e => e.StartsWith("NodeDef.MinToolTier") && e.Contains("top tier 3"));
        Assert.Contains(errors, e => e.StartsWith("NodeDef.NodeTier") && e.Contains("top tier 3"));
    }

    [Fact]
    public void Net_ids_are_unique()
    {
        var errors = Errors(Set(new[] { Node("ore.t1", 1), Node("ore.t2", 1) }));

        Assert.Contains(errors, e => e.StartsWith("NodeDef.NetId") && e.Contains("also used"));
    }

    [Fact]
    public void A_node_names_its_job_and_tool()
    {
        var node = Node("ore.t1", 1);
        node.JobId = "";
        node.ToolType = ToolType.None;

        var errors = Errors(Set(new[] { node }));

        Assert.Contains(errors, e => e.StartsWith("NodeDef.JobId"));
        Assert.Contains(errors, e => e.StartsWith("NodeDef.ToolType"));
    }

    [Fact]
    public void Both_looks_need_an_entity_visual_row()
    {
        var node = Node("ore.t1", 1);
        node.DepletedVisualId = "missing";

        var errors = TableValidator.Validate(Set(new[] { node }));

        Assert.Contains(errors, e => e.Code == ValidationCodes.BrokenRef && e.Table == "NodeDef" && e.Field == "DepletedVisualId");
    }

    [Fact]
    public void A_placement_row_names_a_node_type()
    {
        var errors = TableValidator.Validate(Set(new[] { Node("ore.t1", 1) },
            new[] { new GatherNodes { MarkerId = "field1.node.ring1.a", NodeId = "ore.t9" } }));

        Assert.Contains(errors, e => e.Code == ValidationCodes.BrokenRef && e.Table == "GatherNodes");
    }

    [Fact]
    public void The_repository_rows_place_every_tier_by_ring()
    {
        // User decision 5 (2026-10-30): T1 ore in rings 1-2, T2 in ring 3, T3 in ring 4, the gem vein (T2) on ring 3.
        var set = RepositoryTables();
        var nodes = Find(set, typeof(NodeDef)).Rows.Cast<NodeDef>().ToDictionary(n => n.Id);
        var placement = Find(set, typeof(GatherNodes)).Rows.Cast<GatherNodes>().ToList();
        foreach (var row in placement)
        {
            int tier = nodes[row.NodeId].NodeTier;
            if (row.MarkerId.Contains(".ring1.") || row.MarkerId.Contains(".ring2."))
                Assert.Equal(1, tier);
            else if (row.MarkerId.Contains(".ring3."))
                Assert.Equal(2, tier);
            else
                Assert.Equal(3, tier);
        }
        Assert.Equal(2, placement.Count(p => p.NodeId == "node.gemVein"));
        Assert.All(nodes.Values, n => Assert.Equal("blacksmith", n.JobId));
    }

    private static ConfigTable Find(ConfigSet set, Type type)
    {
        Assert.True(set.TryGet(type, out var table), type.Name + " is not loaded");
        return table;
    }

    /// <summary>The committed data/tables, read with the client's portable reader and validated.</summary>
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
