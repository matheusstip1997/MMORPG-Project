using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// The inventory window's tables (spec 9.13, WP16): <see cref="Hotkeys"/>, <see cref="ItemIconRegistry"/>,
/// <see cref="InventoryWindowSettings"/> (its set rule: the toggle key and an icon row per item), the server's
/// <see cref="InventorySettings"/>, and the UiStrings coverage of rarities and item categories.
/// </summary>
public class InventoryWindowTablesTests
{
    private static ItemDef Item(string id, ItemCategory category = ItemCategory.SellOnly) => new()
    {
        Id = id, NetId = id.Length, Category = category, MaxStack = 99, VisualId = "drop", NameId = "item.name",
    };

    private static ItemIconRegistry Icon(string id, string color = "#c8453c", string address = "") =>
        new() { Id = id, IconAddress = address, PlaceholderShape = IconShape.Triangle, PlaceholderColor = color };

    private static ConfigSet Set(IEnumerable<ItemIconRegistry> icons, IEnumerable<Hotkeys>? hotkeys = null)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ItemDef), 2, new object[] { Item("m1.a"), Item("m1.bb") }));
        set.Add(new ConfigTable(typeof(ItemIconRegistry), 1, icons.Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(Hotkeys), 1,
            (hotkeys ?? new[] { new Hotkeys { Id = Hotkeys.ToggleInventory, Binding = "<Keyboard>/i" } }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(InventoryWindowSettings), 1, new object[]
        {
            new InventoryWindowSettings { Columns = 6, SlotSizePx = 44, SlotGapPx = 4, DragThresholdPx = 6, MovePreviewTimeoutMs = 5000 },
        }));
        return set;
    }

    private static IEnumerable<string> Errors(ConfigSet set) =>
        TableValidator.Validate(set).Where(e => e.Code != ValidationCodes.BrokenRef).Select(e => e.Table + ": " + e.Message);

    [Fact]
    public void An_icon_row_for_every_item_and_the_toggle_key_pass()
    {
        Assert.Empty(Errors(Set(new[] { Icon("m1.a"), Icon("m1.bb") })));
    }

    [Fact]
    public void An_item_without_an_icon_row_is_reported()
    {
        var errors = Errors(Set(new[] { Icon("m1.a") })).ToList();

        Assert.Contains(errors, e => e.StartsWith("InventoryWindowSettings") && e.Contains("'m1.bb'"));
    }

    [Fact]
    public void The_window_needs_its_toggle_key()
    {
        var errors = Errors(Set(new[] { Icon("m1.a"), Icon("m1.bb") }, new[] { new Hotkeys { Id = "other", Binding = "<Keyboard>/o" } }));

        Assert.Contains(errors, e => e.StartsWith("InventoryWindowSettings") && e.Contains(Hotkeys.ToggleInventory));
    }

    [Theory]
    [InlineData("#zzzzzz", "")]
    [InlineData("c8453c", "")]
    [InlineData("#c8453c", " Icons/Horn")]
    [InlineData("#c8453c", "/Icons/Horn")]
    public void Bad_icon_colors_and_addresses_are_refused(string color, string address)
    {
        var errors = Errors(Set(new[] { Icon("m1.a", color, address), Icon("m1.bb") }));

        Assert.Contains(errors, e => e.StartsWith("ItemIconRegistry"));
    }

    [Theory]
    [InlineData("<Keyboard>/i", true)]
    [InlineData("<Mouse>/leftButton", true)]
    [InlineData("i", false)]
    [InlineData("<Keyboard>/", false)]
    [InlineData("<Keyboard> /i", false)]
    [InlineData("Keyboard/i", false)]
    [InlineData("", false)]
    public void A_binding_must_be_an_input_system_control_path(string binding, bool valid)
    {
        Assert.Equal(valid, Hotkeys.IsControlPath(binding));
    }

    [Fact]
    public void Category_names_use_the_config_spelling()
    {
        Assert.Equal("itemCategory.sellOnly", UiStringCoverage.ItemCategoryId(ItemCategory.SellOnly));
        Assert.Equal("rarity.common", UiStringCoverage.RarityId("common"));
    }

    [Fact]
    public void Ui_string_coverage_asks_for_every_rarity_and_the_category_of_every_item()
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(UiStrings), 1, new object[] { new UiStrings { Id = "itemCategory.sellOnly", Text = "Sell-only" } }));
        set.Add(new ConfigTable(typeof(RarityDef), 2, new object[] { new RarityDef { Id = "common", Tier = 0, StatPercent = 100 } }));
        set.Add(new ConfigTable(typeof(ItemDef), 2, new object[] { Item("m1.a"), Item("m1.bb", ItemCategory.Material) }));

        var missing = UiStringCoverage.Check(set).Select(e => e.Message).ToList();

        Assert.Equal(2, missing.Count);
        Assert.Contains(missing, m => m.Contains("'rarity.common'"));
        Assert.Contains(missing, m => m.Contains("'itemCategory.material'"));
    }

    [Fact]
    public void The_repository_rows_are_complete()
    {
        var set = LoadRepository();

        Assert.Equal("<Keyboard>/i", Hotkeys.Find(set, Hotkeys.ToggleInventory)!.Binding);
        Assert.True(set.TryGet(typeof(ItemIconRegistry), out var icons));
        Assert.True(set.TryGet(typeof(ItemDef), out var items));
        Assert.Equal(items.Rows.Cast<ItemDef>().Select(i => i.Id).OrderBy(i => i), icons.Rows.Cast<ItemIconRegistry>().Select(i => i.Id).OrderBy(i => i));
        // Placeholders must be told apart: no two items share a shape and a color.
        Assert.Equal(icons.Rows.Count, icons.Rows.Cast<ItemIconRegistry>().Select(i => (i.PlaceholderShape, i.PlaceholderColor)).Distinct().Count());
        Assert.InRange(set.Single<InventorySettings>().MaxQueuedMoveRequests, 1, 64);
        var window = set.Single<InventoryWindowSettings>();
        var bag = ContainerDef.Find(set, ContainerDef.NormalBag);
        Assert.NotNull(bag);
        Assert.Equal(0, bag!.Slots % window.Columns); // full rows
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
