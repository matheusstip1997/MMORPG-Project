using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// The outpost quest tables of WP-M2-6 (spec 5.5 "M2 parameters"): <see cref="OutpostQuestDef"/>, <see cref="QuestItemDrop"/>,
/// <see cref="QuestCaps"/>, <see cref="QuestBoardSettings"/>, <see cref="RunBonus"/>, <see cref="ForfeitPenalty"/>; the committed
/// data passes and holds the user's placeholders.
/// </summary>
public class QuestTablesTests
{
    private static OutpostQuestDef Kind(string id, QuestKind kind, int weight = 10, int min = 1, int max = 5) => new()
    {
        Id = id, Kind = kind, Weight = weight, CountMin = min, CountMax = max, ExpPercentPerUnit = 50,
    };

    private static ConfigSet Set(IEnumerable<OutpostQuestDef>? kinds = null, IEnumerable<QuestItemDrop>? drops = null,
        int questMaxStack = 20, ItemCategory questCategory = ItemCategory.Quest, int maxSteps = 5, int offers = 6)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ItemDef), 2, new object[]
        {
            new ItemDef { Id = "sigil", NetId = 1, Category = questCategory, MaxStack = questMaxStack, VisualId = "drop", NameId = "item" },
        }));
        set.Add(new ConfigTable(typeof(ContainerDef), 2, new object[]
        {
            new ContainerDef
            {
                Id = "questBag", Code = 4, Slots = 20, Accepts = new[] { ItemCategory.Quest }, Kind = ContainerKind.Bag,
                ReceivesNewItems = true,
            },
        }));
        set.Add(new ConfigTable(typeof(OutpostQuestDef), 1, (kinds ?? new[]
        {
            Kind("kill", QuestKind.Kill, min: 5, max: 20), Kind("deliver", QuestKind.Deliver), Kind("returnItem", QuestKind.ReturnItem),
        }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(QuestItemDrop), 1, (drops ?? new[]
        {
            new QuestItemDrop { Id = "demon", MonsterId = "demon", ItemId = "sigil" },
        }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(QuestCaps), 1, new object[] { new QuestCaps { MaxActiveRuns = 1, MaxStepsPerRun = maxSteps, MaxQueuedRequests = 8 } }));
        set.Add(new ConfigTable(typeof(QuestBoardSettings), 1, new object[]
        {
            new QuestBoardSettings { OffersPerBoard = offers, RefreshMs = 600_000, RadiusCm = 10_000 },
        }));
        return set;
    }

    private static List<string> Errors(ConfigSet set) =>
        TableValidator.Validate(set).Where(e => e.Code != ValidationCodes.BrokenRef).Select(e => e.Table + "." + e.Field + ": " + e.Message)
            .ToList();

    [Fact]
    public void The_committed_quest_tables_hold_the_user_placeholders()
    {
        var set = RepositoryTables();
        var caps = set.Single<QuestCaps>();
        var board = set.Single<QuestBoardSettings>();
        Assert.Equal((1, 5), (caps.MaxActiveRuns, caps.MaxStepsPerRun));
        Assert.Equal((6, 600_000), (board.OffersPerBoard, board.RefreshMs));
        Assert.Equal((10, 50), (set.Single<RunBonus>().PerStepPercent, set.Single<RunBonus>().CapPercent));
        Assert.Equal((20, 100), (set.Single<ForfeitPenalty>().PerForfeitPercent, set.Single<ForfeitPenalty>().CapPercent));
        var kill = Rows<OutpostQuestDef>(set).Single(k => k.Kind == QuestKind.Kill);
        Assert.Equal((5, 20, true), (kill.CountMin, kill.CountMax, kill.DemandScaled));
        Assert.Equal(new[] { QuestKind.Kill, QuestKind.Deliver, QuestKind.ReturnItem }, Rows<OutpostQuestDef>(set).Select(k => k.Kind));
        var outpost = Rows<NpcDef>(set).Single(n => n.Id == "m1.outpost");
        Assert.True(outpost.Offers(NpcInteraction.Quests));
    }

    [Fact]
    public void A_consistent_set_passes()
    {
        Assert.Empty(Errors(Set()));
    }

    [Fact]
    public void Two_rows_of_one_kind_fail()
    {
        var errors = Errors(Set(kinds: new[] { Kind("kill", QuestKind.Kill), Kind("kill2", QuestKind.Kill) }));
        Assert.Contains(errors, e => e.Contains("already has row 'kill'"));
    }

    [Fact]
    public void A_board_without_any_weight_fails()
    {
        var errors = Errors(Set(kinds: new[] { Kind("kill", QuestKind.Kill, weight: 0) }));
        Assert.Contains(errors, e => e.Contains("board would be empty"));
    }

    [Fact]
    public void A_count_range_upside_down_or_kind_none_fails()
    {
        Assert.Contains(Errors(Set(kinds: new[] { Kind("kill", QuestKind.Kill, min: 9, max: 3) })), e => e.Contains("CountMax"));
        Assert.Contains(Errors(Set(kinds: new[] { Kind("none", QuestKind.None) })), e => e.Contains("kill, deliver or returnItem"));
    }

    [Fact]
    public void A_return_count_that_does_not_fit_one_quest_item_stack_fails()
    {
        var errors = Errors(Set(questMaxStack: 3));
        Assert.Contains(errors, e => e.Contains("do not fit one stack"));
    }

    [Fact]
    public void A_quest_drop_must_be_a_quest_item_and_once_per_monster()
    {
        Assert.Contains(Errors(Set(questCategory: ItemCategory.Material)), e => e.Contains("must be a quest item"));
        var twice = Errors(Set(drops: new[]
        {
            new QuestItemDrop { Id = "a", MonsterId = "demon", ItemId = "sigil" },
            new QuestItemDrop { Id = "b", MonsterId = "demon", ItemId = "sigil" },
        }));
        Assert.Contains(twice, e => e.Contains("already drops"));
    }

    [Fact]
    public void A_run_cannot_hold_more_steps_than_the_board_offers()
    {
        Assert.Contains(Errors(Set(maxSteps: 7, offers: 6)), e => e.Contains("distinct offers"));
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

    private static List<T> Rows<T>(ConfigSet set) where T : class =>
        set.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>().ToList() : new List<T>();
}
