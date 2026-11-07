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
        set.Add(new ConfigTable(typeof(ProgressionSettings), 2,
            new object[] { new ProgressionSettings { MaxLevel = maxLevel, SkillPointsPerLevel = 1, AttributePointsPerLevel = 1 } }));
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
    public void A_hide_condition_needs_a_prompt_to_hide()
    {
        var noPrompt = Row("a", 12, "", "exp_bar");
        noPrompt.HideWhen = UnlockHideCondition.JobHeld;
        var withPrompt = Row("b", 12, "tutorial.prompt.b");
        withPrompt.HideWhen = UnlockHideCondition.NoJobHeld;

        var errors = Errors(Set(new[] { noPrompt, withPrompt }));

        Assert.Single(errors);
        Assert.Contains("a hide condition hides a prompt", errors[0]);
    }

    [Fact]
    public void The_committed_m3_rows_open_jobs_gathering_and_crafting_from_level_12()
    {
        // Spec 12.3 R-12.3-02, WP-M3-11; the "jobs are open" prompt hides once a job is held (user decision 2026-10-30).
        var set = LoadRepository();
        Assert.True(set.TryGet(typeof(UnlockSchedule), out var table));
        var byId = table.Rows.Cast<UnlockSchedule>().ToDictionary(r => r.Id);

        var jobs = byId["jobs"];
        Assert.Equal((12, UnlockTrigger.LevelReached, UnlockHideCondition.JobHeld), (jobs.Level, jobs.Trigger, jobs.HideWhen));
        var gather = byId["gather"];
        Assert.Equal((12, UnlockTrigger.NodeInReach, UnlockHideCondition.None), (gather.Level, gather.Trigger, gather.HideWhen));
        var craft = byId["craft"];
        Assert.True(craft.Level >= gather.Level, "crafting comes after gathering (spec 12.3)");
        Assert.Equal((UnlockTrigger.StationInReach, UnlockHideCondition.NoJobHeld), (craft.Trigger, craft.HideWhen));
        Assert.All(new[] { jobs, gather, craft }, r => Assert.Empty(r.HudElements)); // R-12.3-02: menus are not locked
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
    public void The_committed_schedule_hides_no_hud_element_and_gates_rings_2_to_4()
    {
        var set = LoadRepository();
        Assert.True(set.TryGet(typeof(UnlockSchedule), out var table));
        var schedule = table.Rows.Cast<UnlockSchedule>().ToList();
        // User decision 2026-10-28 after play: the exp bar and the minimap show from level 1; only the hotbar is listed, at 1.
        Assert.All(schedule.Where(r => r.HudElements.Length > 0), r => Assert.Equal(1, r.Level));
        Assert.DoesNotContain(schedule, r => r.HudElements.Contains("exp_bar") || r.HudElements.Contains("minimap"));
        Assert.Contains(schedule, r => r.Level == 10 && r.Prompt == "tutorial.prompt.leave");

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
