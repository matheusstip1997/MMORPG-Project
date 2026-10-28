using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// The tutorial's tables (spec 12.3, WP-M2-10): <c>UnlockSchedule</c> (R-12.3-02) against <c>UiStrings</c>, <c>HudLayout</c>
/// and the level cap, <c>TutorialSettings</c>, and the <c>RegionDef.minLevel</c> gate (R-2.3-06).
/// </summary>
public class TutorialTablesTests
{
    private static ConfigSet Set(IEnumerable<UnlockSchedule> rows, int maxLevel = 130, params RegionDef[] regions)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ProgressionSettings), 1, new object[] { new ProgressionSettings { MaxLevel = maxLevel, SkillPointsPerLevel = 1 } }));
        set.Add(new ConfigTable(typeof(UiStrings), 1, new object[]
        {
            new UiStrings { Id = "tutorial.prompt.a", Text = "A" }, new UiStrings { Id = "tutorial.prompt.b", Text = "B" },
        }));
        set.Add(new ConfigTable(typeof(HudLayout), 1, new object[]
        {
            new HudLayout { Id = "exp_bar", Offset = new[] { 0, 0 }, Size = new[] { 1, 1 } },
            new HudLayout { Id = "minimap", Offset = new[] { 0, 0 }, Size = new[] { 1, 1 } },
        }));
        set.Add(new ConfigTable(typeof(UnlockSchedule), 1, rows.Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(RegionDefaults), 1, new object[] { new RegionDefaults { Id = "openWorld" } }));
        set.Add(new ConfigTable(typeof(RegionDef), 1,
            (regions.Length > 0 ? regions : new[] { new RegionDef { Id = "r", AreaType = "openWorld" } }).Cast<object>().ToList()));
        return set;
    }

    private static UnlockSchedule Row(string id, int level, string prompt = "", params string[] hud) => new()
    {
        Id = id, Level = level, Trigger = UnlockTrigger.LevelReached, Prompt = prompt, HudElements = hud,
    };

    private static List<string> Errors(ConfigSet set) =>
        TableValidator.Validate(set).Where(e => e.Table is "UnlockSchedule" or "RegionDef").Select(e => e.ToString()).ToList();

    [Fact]
    public void A_schedule_of_prompts_and_hud_elements_is_valid()
    {
        Assert.Empty(Errors(Set(new[] { Row("a", 1, "tutorial.prompt.a"), Row("b", 10, "tutorial.prompt.b", "exp_bar", "minimap") })));
    }

    [Fact]
    public void Prompts_and_hud_elements_must_exist()
    {
        var errors = Errors(Set(new[] { Row("a", 1, "tutorial.prompt.missing"), Row("b", 10, "", "no_such_element") }));

        Assert.Contains(errors, e => e.Contains("tutorial.prompt.missing"));
        Assert.Contains(errors, e => e.Contains("no_such_element"));
    }

    [Fact]
    public void A_prompt_and_a_hud_element_belong_to_one_row()
    {
        var errors = Errors(Set(new[]
        {
            Row("a", 1, "tutorial.prompt.a", "exp_bar"), Row("b", 5, "tutorial.prompt.a"), Row("c", 10, "", "exp_bar"),
        }));

        Assert.Contains(errors, e => e.Contains("'tutorial.prompt.a' is already the prompt of row 'a'"));
        Assert.Contains(errors, e => e.Contains("'exp_bar' is already listed by row 'a'"));
    }

    [Fact]
    public void A_row_needs_a_prompt_or_hud_elements_and_hud_elements_come_with_the_level()
    {
        var empty = Row("a", 1);
        var triggered = Row("b", 5, "tutorial.prompt.b", "exp_bar");
        triggered.Trigger = UnlockTrigger.ItemSold;
        var errors = Errors(Set(new[] { empty, triggered }));

        Assert.Contains(errors, e => e.Contains("a row shows a prompt, HUD elements or both"));
        Assert.Contains(errors, e => e.Contains("use levelReached"));
    }

    [Fact]
    public void Levels_stay_within_the_level_cap()
    {
        var errors = Errors(Set(new[] { Row("a", 131, "tutorial.prompt.a") }, 130,
            new RegionDef { Id = "gated", AreaType = "openWorld", MinLevel = 200 }));

        Assert.Contains(errors, e => e.Contains("131 is above ProgressionSettings.maxLevel 130"));
        Assert.Contains(errors, e => e.Contains("200 is above ProgressionSettings.maxLevel 130"));
    }

    [Fact]
    public void The_committed_schedule_reveals_the_exp_bar_at_level_10_and_gates_rings_2_to_4()
    {
        var set = LoadRepository();
        Assert.True(set.TryGet(typeof(UnlockSchedule), out var table));
        var schedule = table.Rows.Cast<UnlockSchedule>().ToList();
        Assert.Contains(schedule, r => r.Level == 10 && r.HudElements.Contains("exp_bar"));
        Assert.Contains(schedule, r => r.Level == 1 && r.HudElements.Contains("hotbar_left"));

        set.TryGet(typeof(RegionDef), out var regions);
        var byId = regions.Rows.Cast<RegionDef>().ToDictionary(r => r.Id);
        Assert.Equal(0, byId["field1.ring1"].MinLevel);
        Assert.Equal(0, byId["m1.city"].MinLevel);
        Assert.All(new[] { "field1.ring2", "field1.ring3", "field1.ring4" }, id => Assert.Equal(10, byId[id].MinLevel));
        Assert.True(TutorialSettings.Find(set)!.PromptShowMs > 0);
    }

    /// <summary>The committed data/tables, read with the client's portable reader and validated.</summary>
    private static ConfigSet LoadRepository()
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
