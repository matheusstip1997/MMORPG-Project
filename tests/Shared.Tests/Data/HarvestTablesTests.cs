using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// The harvest tables (spec 5.2, WP-M3-5): <see cref="NodeYieldEntry"/> (a stackable item a receiving container takes, its
/// count within one stack, a table some node rolls, a job level under the cap), <see cref="NodeDef"/>'s yield table (it has
/// rows, one of them from job level 1) and harvest action, the harvest <see cref="ActionDef"/> kind (a channel without
/// stamina or MP and exactly one harvest effect, which only a harvest action has), and <see cref="HarvestSettings"/> (its
/// window within the job's saved one). The committed rows pass in GameServer.Tests RepositoryDataTests.
/// </summary>
public class HarvestTablesTests
{
    private static NodeYieldEntry Entry(string id, string item = "ore", string table = "yield.ore", int weight = 10, int min = 1,
        int max = 3, int minJobLevel = 1) => new()
    {
        Id = id, TableId = table, ItemId = item, RarityId = "common", Weight = weight, CountMin = min, CountMax = max,
        MinJobLevel = minJobLevel,
    };

    private static ActionDef HarvestAction(string id = "harvest.ore", int channelMs = 3_000, int stamina = 0) => new()
    {
        Id = id, NetId = 239, Kind = ActionKind.Harvest, StartupMs = 200, ChannelMs = channelMs, ActiveMs = 100, RecoveryMs = 300,
        StaminaCost = stamina, CancelOnMove = true,
    };

    private static ActionEffectDef HarvestEffect(string actionId = "harvest.ore") => new()
    {
        Id = actionId + ".harvest", ActionId = actionId, Kind = EffectKind.Harvest, Phase = ActionPhase.Active,
    };

    private static ConfigSet Set(IEnumerable<NodeYieldEntry> entries, ActionDef? action = null, ActionEffectDef[]? effects = null,
        string yieldTableId = "yield.ore", int repeatWindow = 10)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ItemDef), 2, new object[]
        {
            new ItemDef { Id = "ore", NetId = 401, Category = ItemCategory.Material, MaxStack = 99, VisualId = "drop", NameId = "item" },
            new ItemDef { Id = "pick", NetId = 501, Category = ItemCategory.MoldOrTool, MaxStack = 1, VisualId = "drop", NameId = "item" },
            new ItemDef { Id = "horn", NetId = 1, Category = ItemCategory.SellOnly, MaxStack = 99, VisualId = "drop", NameId = "item" },
        }));
        set.Add(new ConfigTable(typeof(ContainerDef), 2, new object[]
        {
            new ContainerDef { Id = ContainerDef.NormalBag, Code = 1, Slots = 30, Accepts = [ItemCategory.Material], ReceivesNewItems = true },
        }));
        set.Add(new ConfigTable(typeof(RarityDef), 2, new object[] { new RarityDef { Id = "common", Tier = 0, StatPercent = 100 } }));
        set.Add(new ConfigTable(typeof(JobSettings), 1, new object[] { new JobSettings { MaxLevel = 20, PointsPerLevel = 1, RecentOpKeys = 64 } }));
        set.Add(new ConfigTable(typeof(JobXpWeights), 1, new object[]
        {
            new JobXpWeights { Id = "harvest", Activity = Shared.Simulation.Jobs.JobActivity.Harvest, BaseXp = 10, TierPercent = [100],
                RepeatWindow = repeatWindow, RepeatStepPercent = 10, RepeatFloorPercent = 20 },
        }));
        set.Add(new ConfigTable(typeof(HarvestSettings), 1, new object[]
        {
            new HarvestSettings { YieldRepeatWindow = 10, YieldRepeatStepPercent = 10, YieldRepeatFloorPercent = 50, YieldPercentPerJobLevel = 5,
                MaxBonusPercent = 200 },
        }));
        set.Add(new ConfigTable(typeof(ActionDef), 2, new object[] { action ?? HarvestAction() }));
        set.Add(new ConfigTable(typeof(ActionEffectDef), 2, (effects ?? new[] { HarvestEffect() }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(NodeDef), 2, new object[]
        {
            new NodeDef { Id = "node.ore", NetId = 1, JobId = "blacksmith", ToolType = ToolType.Pickaxe, MinToolTier = 1, NodeTier = 1,
                YieldTableId = yieldTableId, RespawnMs = 60_000, ActionId = (action ?? HarvestAction()).Id, ReachCm = 250,
                VisualId = "rock", DepletedVisualId = "rubble" },
        }));
        set.Add(new ConfigTable(typeof(NodeYieldEntry), 1, entries.Cast<object>().ToList()));
        return set;
    }

    private static List<string> Errors(ConfigSet set, params string[] tables) =>
        TableValidator.Validate(set).Where(e => tables.Contains(e.Table))
            .Select(e => e.Table + "." + e.Field + ": " + e.Message).ToList();

    private static readonly string[] Harvest = ["NodeYieldEntry", "NodeDef", "ActionDef", "ActionEffectDef", "HarvestSettings"];

    [Fact]
    public void A_yield_table_of_stackable_materials_with_a_level_1_entry_passes()
    {
        // The node rows' other references (job, looks, tiers) are NodeTablesTests': only the harvest fields count here.
        Assert.DoesNotContain(Errors(Set([Entry("a"), Entry("b", minJobLevel: 5)]), Harvest),
            e => !e.StartsWith("NodeDef.") || e.StartsWith("NodeDef.YieldTableId") || e.StartsWith("NodeDef.ActionId"));
    }

    [Fact]
    public void Every_node_has_a_yield_table_with_rows_and_a_level_1_entry()
    {
        Assert.Contains(Errors(Set([Entry("a")], yieldTableId: "yield.none"), "NodeDef"),
            e => e.StartsWith("NodeDef.YieldTableId") && e.Contains("no NodeYieldEntry row"));
        Assert.Contains(Errors(Set([Entry("a", minJobLevel: 3)]), "NodeDef"),
            e => e.StartsWith("NodeDef.YieldTableId") && e.Contains("minJobLevel 1"));
    }

    [Fact]
    public void An_entry_names_a_stackable_item_a_container_receives_within_one_stack()
    {
        Assert.Contains(Errors(Set([Entry("a"), Entry("tool", item: "pick", max: 1)]), "NodeYieldEntry"),
            e => e.StartsWith("NodeYieldEntry.ItemId") && e.Contains("does not stack"));
        Assert.Contains(Errors(Set([Entry("a"), Entry("horn", item: "horn")]), "NodeYieldEntry"),
            e => e.StartsWith("NodeYieldEntry.ItemId") && e.Contains("receivesNewItems"));
        Assert.Contains(Errors(Set([Entry("a", max: 100)]), "NodeYieldEntry"), e => e.StartsWith("NodeYieldEntry.CountMax"));
        Assert.Contains(Errors(Set([Entry("a"), Entry("missing", item: "nothing")]), "NodeYieldEntry"),
            e => e.StartsWith("NodeYieldEntry.ItemId"));
    }

    [Fact]
    public void Weights_are_positive_counts_ordered_and_job_levels_under_the_cap()
    {
        Assert.Contains(Errors(Set([Entry("a", weight: 0)]), "NodeYieldEntry"), e => e.StartsWith("NodeYieldEntry.Weight"));
        Assert.Contains(Errors(Set([Entry("a", min: 3, max: 2)]), "NodeYieldEntry"), e => e.StartsWith("NodeYieldEntry.CountMax"));
        Assert.Contains(Errors(Set([Entry("a"), Entry("b", minJobLevel: 21)]), "NodeYieldEntry"),
            e => e.StartsWith("NodeYieldEntry.MinJobLevel") && e.Contains("cap"));
    }

    [Fact]
    public void A_table_no_node_rolls_is_refused()
    {
        Assert.Contains(Errors(Set([Entry("a"), Entry("b", table: "yield.orphan")]), "NodeYieldEntry"),
            e => e.StartsWith("NodeYieldEntry.TableId") && e.Contains("no NodeDef"));
    }

    [Fact]
    public void A_node_action_is_a_harvest_channel_without_costs_and_one_harvest_effect()
    {
        var skill = HarvestAction();
        skill.Kind = ActionKind.UseItem;
        Assert.Contains(Errors(Set([Entry("a")], action: skill, effects: []), "NodeDef"), e => e.StartsWith("NodeDef.ActionId"));
        Assert.Contains(Errors(Set([Entry("a")], action: HarvestAction(channelMs: 0)), "ActionDef"), e => e.StartsWith("ActionDef.ChannelMs"));
        Assert.Contains(Errors(Set([Entry("a")], action: HarvestAction(stamina: 5)), "ActionDef"), e => e.StartsWith("ActionDef.StaminaCost"));
        Assert.Contains(Errors(Set([Entry("a")], effects: []), "ActionDef"), e => e.Contains("exactly one harvest effect (0)"));
        Assert.Contains(Errors(Set([Entry("a")], effects: [HarvestEffect(), new ActionEffectDef
            {
                Id = "second", ActionId = "harvest.ore", Kind = EffectKind.Harvest, Phase = ActionPhase.Active,
            }]), "ActionDef"), e => e.Contains("exactly one harvest effect (2)"));
    }

    [Fact]
    public void The_yield_window_fits_in_the_jobs_saved_window()
    {
        Assert.Contains(Errors(Set([Entry("a")], repeatWindow: 5), "HarvestSettings"),
            e => e.StartsWith("HarvestSettings.YieldRepeatWindow"));
    }
}
