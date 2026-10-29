using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>ItemDef, RarityDef, ContainerDef, LootTableDef, LootTableEntry and LootSettings (backlog G): row and set rules.</summary>
public class ItemTablesTests
{
    private static ItemDef Item(string id = "m1.demonHorn", int netId = 1, ItemCategory category = ItemCategory.SellOnly,
        int maxStack = 99) => new()
    {
        Id = id, NetId = netId, Category = category, MaxStack = maxStack, VisualId = "drop", NameId = "item.name",
    };

    private static ContainerDef Bag(params ItemCategory[] accepts) => new()
    {
        Id = ContainerDef.NormalBag, Code = 1, Slots = 30,
        Accepts = accepts.Length == 0 ? new[] { ItemCategory.Material, ItemCategory.SellOnly, ItemCategory.Consumable } : accepts,
        ReceivesNewItems = true,
    };

    private static LootTableDef Table(string id = "m1.demon", string monsterId = "m1.demon") =>
        new() { Id = id, MonsterId = monsterId, Rolls = 1, NothingWeight = 0 };

    private static LootTableEntry Entry(string id = "m1.demon.horn", string tableId = "m1.demon", string itemId = "m1.demonHorn",
        int countMin = 1, int countMax = 2) => new()
    {
        Id = id, TableId = tableId, ItemId = itemId, RarityId = "common", Weight = 70, CountMin = countMin, CountMax = countMax,
    };

    private static MonsterDef Monster(string id, int netId) => new()
    {
        Id = id, NetId = netId, VisualId = "drop", MaxHp = 10, PoiseMax = 10, BodyRadiusCm = 40, MoveSpeedCmPerSec = 100,
        AggroRangeCm = 100, LeashRangeCm = 200,
    };

    private static ConfigSet Set(IEnumerable<ItemDef>? items = null, ContainerDef? bag = null, IEnumerable<LootTableDef>? tables = null,
        IEnumerable<LootTableEntry>? entries = null, IEnumerable<RarityDef>? rarities = null)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(EntityVisualDef), 1, new object[] { new EntityVisualDef { Id = "drop", PrefabAddress = "Visuals/Items/GroundDrop", ScalePercent = 100 } }));
        set.Add(new ConfigTable(typeof(UiStrings), 1, new object[] { new UiStrings { Id = "item.name", Text = "Item" } }));
        set.Add(new ConfigTable(typeof(MonsterDef), 2, new object[] { Monster("m1.demon", 1), Monster("m1.alien", 2) }));
        set.Add(new ConfigTable(typeof(ItemDef), 2, (items ?? new[] { Item() }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(RarityDef), 2, (rarities ?? new[] { new RarityDef { Id = "common", Tier = 0, StatPercent = 100 } }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(ContainerDef), 2, new object[] { bag ?? Bag() }));
        set.Add(new ConfigTable(typeof(LootTableDef), 1, (tables ?? new[] { Table() }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(LootTableEntry), 1, (entries ?? new[] { Entry() }).Cast<object>().ToList()));
        return set;
    }

    private static List<ValidationError> Validate(ConfigSet set) => TableValidator.Validate(set);

    [Fact]
    public void Valid_rows_pass()
    {
        Assert.Empty(Validate(Set()));
    }

    [Theory]
    [InlineData(ItemCategory.Equipment)]
    [InlineData(ItemCategory.MoldOrTool)]
    public void Instance_categories_hold_one_item_per_row(ItemCategory category)
    {
        // Spec 4.1 M2 item model (WP-M2-1): instances are allowed now, one item each.
        var errors = Validate(Set(items: [Item(category: category)]));

        Assert.Contains(errors, e => e.Table == "ItemDef" && e.Field == nameof(ItemDef.MaxStack));
    }

    [Fact]
    public void Quest_items_stack()
    {
        var errors = Validate(Set(items: [Item(category: ItemCategory.Quest)]));

        Assert.DoesNotContain(errors, e => e.Table == "ItemDef");
    }

    [Fact]
    public void Item_net_ids_are_unique()
    {
        var errors = Validate(Set(items: [Item(), Item("m1.demonAsh", 1)]));

        Assert.Contains(errors, e => e.Table == "ItemDef" && e.Field == "NetId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_000)]
    public void Max_stack_is_in_range(int maxStack)
    {
        var errors = Validate(Set(items: [Item(maxStack: maxStack)]));

        Assert.Contains(errors, e => e.Code == ValidationCodes.OutOfRange && e.Field == nameof(ItemDef.MaxStack));
    }

    [Fact]
    public void Rarity_tiers_are_unique()
    {
        var errors = Validate(Set(rarities: [new RarityDef { Id = "common", Tier = 0 }, new RarityDef { Id = "rare", Tier = 0 }]));

        Assert.Contains(errors, e => e.Table == "RarityDef" && e.Field == nameof(RarityDef.Tier));
    }

    [Fact]
    public void A_container_takes_at_least_one_category_and_lists_each_once()
    {
        Assert.Contains(Validate(Set(bag: new ContainerDef { Id = ContainerDef.NormalBag, Code = 1, Slots = 30 })),
            e => e.Table == "ContainerDef" && e.Field == nameof(ContainerDef.Accepts));
        Assert.Contains(Validate(Set(bag: Bag(ItemCategory.Material, ItemCategory.SellOnly, ItemCategory.Material))),
            e => e.Table == "ContainerDef" && e.Field == nameof(ContainerDef.Accepts));
    }

    [Fact]
    public void A_monster_has_at_most_one_loot_table()
    {
        var errors = Validate(Set(tables: [Table(), Table("m1.demon.second")],
            entries: [Entry(), Entry("m1.demon.second.horn", tableId: "m1.demon.second")]));

        Assert.Contains(errors, e => e.Table == "LootTableDef" && e.Field == nameof(LootTableDef.MonsterId));
    }

    [Fact]
    public void A_loot_table_needs_entries_and_an_existing_monster()
    {
        var errors = Validate(Set(tables: [Table(), Table("m1.alien", "m1.alien")]));
        Assert.Contains(errors, e => e.Table == "LootTableDef" && e.Message.Contains("no LootTableEntry"));

        errors = Validate(Set(tables: [Table(monsterId: "m1.nobody")]));
        Assert.Contains(errors, e => e.Code == ValidationCodes.BrokenRef && e.Field == nameof(LootTableDef.MonsterId));
    }

    [Fact]
    public void Entry_counts_are_ordered_and_fit_one_stack()
    {
        Assert.Contains(Validate(Set(entries: [Entry(countMin: 3, countMax: 2)])),
            e => e.Table == "LootTableEntry" && e.Field == nameof(LootTableEntry.CountMax));
        Assert.Contains(Validate(Set(items: [Item(maxStack: 5)], entries: [Entry(countMax: 6)])),
            e => e.Table == "LootTableEntry" && e.Field == nameof(LootTableEntry.CountMax));
    }

    [Fact]
    public void Entries_reference_items_rarities_and_tables()
    {
        var bad = Entry("m1.demon.bad");
        bad.ItemId = "m1.nothing";
        bad.RarityId = "legendary";
        var errors = Validate(Set(entries: [Entry(), bad]));

        Assert.Contains(errors, e => e.Code == ValidationCodes.BrokenRef && e.Field == nameof(LootTableEntry.ItemId));
        Assert.Contains(errors, e => e.Code == ValidationCodes.BrokenRef && e.Field == nameof(LootTableEntry.RarityId));
    }

    [Fact]
    public void An_item_no_receiving_container_takes_cannot_drop()
    {
        var errors = Validate(Set(bag: Bag(ItemCategory.Material)));

        Assert.Contains(errors, e => e.Table == "LootTableEntry" && e.Field == nameof(LootTableEntry.ItemId));
    }

    [Theory]
    [InlineData(nameof(LootSettings.PickupRangeCm), 0)]
    [InlineData(nameof(LootSettings.DropScatterCm), -1)]
    [InlineData(nameof(LootSettings.MaxQueuedPickupRequests), 0)]
    [InlineData(nameof(LootSettings.MaxDropsPerWorld), 0)]
    public void Loot_settings_are_in_range(string field, int value)
    {
        var settings = new LootSettings { PickupRangeCm = 300, DropScatterCm = 60, MaxQueuedPickupRequests = 4, MaxDropsPerWorld = 2000 };
        typeof(LootSettings).GetProperty(field)!.SetValue(settings, value);
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(LootSettings), 1, new object[] { settings }));

        Assert.Contains(TableValidator.Validate(set), e => e.Code == ValidationCodes.OutOfRange && e.Field == field);
    }
}

