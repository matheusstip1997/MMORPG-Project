using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Stats;

namespace Shared.Tests.Data;

/// <summary>Rules of the HUD tables (spec 9.7, backlog I) and the UiStrings coverage rule for the names the HUD shows.</summary>
public class HudTablesTests
{
    [Fact]
    public void Layout_offset_and_size_must_be_pairs()
    {
        var errors = new RowErrors();

        new HudLayout { Id = "boss_bar", Offset = new[] { 0 }, Size = new[] { 1, 2, 3 } }.Validate(errors);

        Assert.Equal(new[] { "Offset", "Size" }, errors.Items.Select(e => e.Field));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(ActionBindings.MaxSkillSlots + 1)]
    public void Layout_slots_must_be_skill_slots_or_zero(int slot)
    {
        var errors = new RowErrors();

        new HudLayout { Id = "hotbar_left", Offset = new[] { 0, 0 }, Size = new[] { 1, 1 }, Slots = new[] { 0, slot } }.Validate(errors);

        Assert.Equal(nameof(HudLayout.Slots), Assert.Single(errors.Items).Field);
    }

    [Fact]
    public void A_skill_slot_is_shown_once_but_the_unassigned_slot_repeats()
    {
        var left = Layout("hotbar_left", 1, 2, 0);
        var right = Layout("hotbar_right", 7, 2, 0);
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(HudLayout), 1, new object[] { left, right }));

        var leftErrors = new RowErrors();
        left.Validate(set, leftErrors);
        var rightErrors = new RowErrors();
        right.Validate(set, rightErrors);

        Assert.Empty(leftErrors.Items);
        Assert.Contains("skill slot 2", Assert.Single(rightErrors.Items).Message);
    }

    [Theory]
    [InlineData("#c8453c", 200, 69, 60, 255)]
    [InlineData("#0e121adb", 14, 18, 26, 219)]
    public void Colors_parse_with_and_without_alpha(string text, int r, int g, int b, int a)
    {
        Assert.True(HudColor.TryParse(text, out var color));

        Assert.Equal((r, g, b, a), (color.R, color.G, color.B, color.A));
    }

    [Theory]
    [InlineData("")]
    [InlineData("c8453c")]
    [InlineData("#c8453")]
    [InlineData("#c8453cz1")]
    [InlineData("rgba(1,2,3,0.5)")]
    public void Bad_colors_are_refused(string text)
    {
        Assert.False(HudColor.TryParse(text, out _));
    }

    [Fact]
    public void Theme_names_each_bad_color_and_a_crit_smaller_than_damage()
    {
        var tokens = Tokens();
        tokens.Hp = "red";
        tokens.FontSizeCrit = tokens.FontSizeDamage - 1;
        var errors = new RowErrors();

        tokens.Validate(errors);

        Assert.Equal(new[] { nameof(HudThemeTokens.Hp), nameof(HudThemeTokens.FontSizeCrit) }, errors.Items.Select(e => e.Field));
    }

    [Fact]
    public void Coverage_reports_bound_actions_spawned_monsters_ranks_and_statuses_without_names()
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(UiStrings), 1, new object[] { new UiStrings { Id = "action.dodge", Text = "Dodge" } }));
        set.Add(new ConfigTable(typeof(ActionBindings), 2, new object[]
        {
            new ActionBindings { ClassId = "warrior", NormalAttack = "attack.warrior", Dodge = "dodge", Parry = "parry", SkillSlots = new[] { "", "skill.a" } },
        }));
        set.Add(new ConfigTable(typeof(MonsterDef), 2, new object[]
        {
            new MonsterDef { Id = "m1.demon", Rank = Rank.F }, new MonsterDef { Id = "m1.unused", Rank = Rank.S },
        }));
        set.Add(new ConfigTable(typeof(SpawnPoints), 1, new object[] { new SpawnPoints { MarkerId = "a", MonsterId = "m1.demon" } }));
        set.Add(new ConfigTable(typeof(StatusEffectDef), 1, new object[] { new StatusEffectDef { Id = "burn" } }));

        var missing = UiStringCoverage.Check(set).Select(e => e.Message).ToList();

        Assert.All(new[] { "action.attack.warrior", "action.parry", "action.skill.a", "monster.m1.demon", "rank.F", "status.burn" },
            id => Assert.Contains(missing, m => m.Contains($"'{id}'")));
        Assert.DoesNotContain(missing, m => m.Contains("'action.dodge'") || m.Contains("m1.unused") || m.Contains("rank.S") || m.Contains("'action.'"));
    }

    [Fact]
    public void The_repository_hud_tables_load_and_every_hud_name_has_a_row()
    {
        var set = LoadRepository();

        Assert.Empty(UiStringCoverage.Check(set));
        var settings = set.Single<HudSettings>();
        Assert.Equal((1280, 720), (settings.ReferenceWidth, settings.ReferenceHeight));
        Assert.True(set.TryGet(typeof(HudLayout), out var layout));
        var slots = layout.Rows.Cast<HudLayout>().SelectMany(r => r.Slots).Where(s => s != 0).OrderBy(s => s);
        Assert.Equal(Enumerable.Range(1, 11), slots); // spec 9.6: keys 1-6 and Q E R F Z
    }

    private static HudLayout Layout(string id, params int[] slots) =>
        new() { Id = id, Offset = new[] { 0, 0 }, Size = new[] { 1, 1 }, Slots = slots };

    private static HudThemeTokens Tokens()
    {
        var tokens = new HudThemeTokens { FontSizeBody = 12, FontSizeSlotLabel = 11, FontSizeStack = 16, FontSizeDamage = 18, FontSizeCrit = 26 };
        foreach (var property in typeof(HudThemeTokens).GetProperties().Where(p => p.PropertyType == typeof(string)))
            property.SetValue(tokens, "#112233");
        return tokens;
    }

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
