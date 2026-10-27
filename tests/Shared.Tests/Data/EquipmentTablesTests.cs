using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// Validator rules of the equipment tables (WP-M2-1; spec 4.1 M2 item model, R-4.1-02, R-3.5-07): equipment fields on
/// <see cref="ItemDef"/>, <see cref="EquipSlotDef"/>, the container kinds and receivers of <see cref="ContainerDef"/>,
/// <see cref="ClassGearDef"/> and <see cref="ItemStatBonusDef"/>.
/// </summary>
public class EquipmentTablesTests
{
    private static ItemDef Gear(string id, int netId, string[] slots, WeaponType type = WeaponType.None, int maxStack = 1) => new()
    {
        Id = id, NetId = netId, Category = ItemCategory.Equipment, MaxStack = maxStack, VisualId = "drop", NameId = "item.name",
        EquipSlots = slots, WeaponType = type, MinLevel = 1,
    };

    private static ItemDef Sword() => Gear("sword", 101, ["weaponMain"], WeaponType.Sword);

    private static EquipSlotDef Slot(string id, int code, bool weapon = false) => new() { Id = id, Code = code, IsWeapon = weapon };

    private static EquipSlotDef[] Slots() =>
        [Slot("weaponMain", 0, true), Slot("weaponSecondary", 1, true), Slot("head", 2)];

    private static ContainerDef Container(string id, int code, int slots, ItemCategory[] accepts, bool receives,
        ContainerKind kind = ContainerKind.Bag) =>
        new() { Id = id, Code = code, Slots = slots, Accepts = accepts, ReceivesNewItems = receives, Kind = kind };

    private static ContainerDef[] Containers() =>
    [
        Container(ContainerDef.NormalBag, 1, 30, [ItemCategory.Material, ItemCategory.SellOnly], true),
        Container("safeEquipment", 2, 20, [ItemCategory.Equipment], true),
        Container("equipped", 5, 3, [ItemCategory.Equipment], false, ContainerKind.Equipped),
    ];

    private static ClassDef Class(string id, int netId) =>
        new() { Id = id, NetId = netId, VisualId = "drop", ArmorClass = ArmorClass.Heavy, MaxHp = 10, PoiseMax = 10, BodyRadiusCm = 40 };

    private static ConfigSet Set(ItemDef[]? items = null, EquipSlotDef[]? slots = null, ContainerDef[]? containers = null,
        ClassGearDef[]? gear = null, ItemStatBonusDef[]? bonuses = null, ClassDef[]? classes = null)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(EntityVisualDef), 1, new object[] { new EntityVisualDef { Id = "drop", PrefabAddress = "Visuals/Items/GroundDrop", ScalePercent = 100 } }));
        set.Add(new ConfigTable(typeof(UiStrings), 1, new object[] { new UiStrings { Id = "item.name", Text = "Item" } }));
        set.Add(new ConfigTable(typeof(StatDef), 1, new object[] { new StatDef { Id = "atk", Min = 0, Max = 1000 } }));
        set.Add(new ConfigTable(typeof(ClassDef), 1, (classes ?? [Class("warrior", 1)]).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(ItemDef), 2, (items ?? [Sword()]).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(EquipSlotDef), 1, (slots ?? Slots()).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(ContainerDef), 2, (containers ?? Containers()).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(ClassGearDef), 1, (gear ?? [new ClassGearDef { Id = "warrior", WeaponTypes = [WeaponType.Sword], StarterWeaponId = "sword" }]).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(ItemStatBonusDef), 1, (bonuses ?? [new ItemStatBonusDef { Id = "sword.atk", ItemId = "sword", StatId = "atk", Flat = 10 }]).Cast<object>().ToList()));
        return set;
    }

    private static List<ValidationError> Validate(ConfigSet set) => TableValidator.Validate(set);

    [Fact]
    public void Valid_rows_pass()
    {
        Assert.Empty(Validate(Set()));
    }

    [Fact]
    public void Equipment_must_fill_a_slot_and_hold_one_item()
    {
        Assert.Contains(Validate(Set(items: [Gear("sword", 101, [], WeaponType.Sword)])),
            e => e.Table == "ItemDef" && e.Field == nameof(ItemDef.EquipSlots));
        Assert.Contains(Validate(Set(items: [Gear("sword", 101, ["weaponMain"], WeaponType.Sword, maxStack: 5)])),
            e => e.Table == "ItemDef" && e.Field == nameof(ItemDef.MaxStack));
    }

    [Fact]
    public void A_weapon_slot_needs_a_weapon_type_and_a_weapon_type_needs_a_weapon_slot()
    {
        Assert.Contains(Validate(Set(items: [Gear("sword", 101, ["weaponMain"])])),
            e => e.Table == "ItemDef" && e.Field == nameof(ItemDef.WeaponType));
        Assert.Contains(Validate(Set(items: [Gear("sword", 101, ["head"], WeaponType.Sword)])),
            e => e.Table == "ItemDef" && e.Field == nameof(ItemDef.EquipSlots));
    }

    [Fact]
    public void A_dual_weapon_fills_both_weapon_slots_and_any_other_weapon_one()
    {
        // R-3.5-07 (user decision 2026-10-27): a dual weapon is one item that fills both weapon slots.
        var daggers = Gear("daggers", 102, ["weaponMain", "weaponSecondary"], WeaponType.DualDaggers);
        Assert.DoesNotContain(Validate(Set(items: [Sword(), daggers])), e => e.Table == "ItemDef");

        Assert.Contains(Validate(Set(items: [Sword(), Gear("daggers", 102, ["weaponMain"], WeaponType.DualDaggers)])),
            e => e.Table == "ItemDef" && e.Field == nameof(ItemDef.EquipSlots));
        Assert.Contains(Validate(Set(items: [Gear("sword", 101, ["weaponMain", "weaponSecondary"], WeaponType.Sword)])),
            e => e.Table == "ItemDef" && e.Field == nameof(ItemDef.EquipSlots));
    }

    [Fact]
    public void Only_equipment_has_slots_a_weapon_type_or_a_minimum_level()
    {
        var horn = new ItemDef
        {
            Id = "horn", NetId = 1, Category = ItemCategory.SellOnly, MaxStack = 99, VisualId = "drop", NameId = "item.name",
            EquipSlots = ["head"], WeaponType = WeaponType.Sword, MinLevel = 5,
        };

        var errors = Validate(Set(items: [Sword(), horn]));

        Assert.Contains(errors, e => e.Table == "ItemDef" && e.Field == nameof(ItemDef.EquipSlots));
        Assert.Contains(errors, e => e.Table == "ItemDef" && e.Field == nameof(ItemDef.WeaponType));
        Assert.Contains(errors, e => e.Table == "ItemDef" && e.Field == nameof(ItemDef.MinLevel));
    }

    [Fact]
    public void Equipment_slot_codes_run_from_zero_and_match_the_equipped_container()
    {
        Assert.Contains(Validate(Set(slots: [Slot("weaponMain", 0, true), Slot("weaponSecondary", 1, true), Slot("head", 5)])),
            e => e.Table == "EquipSlotDef" && e.Field == nameof(EquipSlotDef.Code));
        Assert.Contains(Validate(Set(slots: [Slot("weaponMain", 0, true), Slot("weaponSecondary", 1, true)])),
            e => e.Table == "EquipSlotDef" && e.Message.Contains("Equipped container"));
    }

    [Fact]
    public void One_container_receives_each_category_and_there_is_one_equipped_container_of_equipment()
    {
        var two = Containers().Append(Container("other", 3, 5, [ItemCategory.SellOnly], true)).ToArray();
        Assert.Contains(Validate(Set(containers: two)), e => e.Table == "ContainerDef" && e.Field == nameof(ContainerDef.ReceivesNewItems));

        var bad = Containers();
        bad[2] = Container("equipped", 5, 3, [ItemCategory.Material], true, ContainerKind.Equipped);
        var errors = Validate(Set(containers: bad));
        Assert.Contains(errors, e => e.Table == "ContainerDef" && e.Field == nameof(ContainerDef.Accepts));
        Assert.Contains(errors, e => e.Table == "ContainerDef" && e.Field == nameof(ContainerDef.ReceivesNewItems));

        var twoEquipped = Containers().Append(Container("equipped2", 6, 3, [ItemCategory.Equipment], false, ContainerKind.Equipped)).ToArray();
        Assert.Contains(Validate(Set(containers: twoEquipped)), e => e.Table == "ContainerDef" && e.Field == nameof(ContainerDef.Kind));
    }

    [Fact]
    public void Every_class_has_gear_rules_and_its_starter_is_a_weapon_it_can_use()
    {
        var set = Set(gear: [new ClassGearDef { Id = "warrior", WeaponTypes = [WeaponType.Bow], StarterWeaponId = "sword" }]);
        Assert.Contains(Validate(set), e => e.Table == "ClassGearDef" && e.Field == nameof(ClassGearDef.StarterWeaponId));

        set = Set(gear: [new ClassGearDef { Id = "warrior", WeaponTypes = [WeaponType.Sword, WeaponType.Sword] }]);
        Assert.Contains(Validate(set), e => e.Table == "ClassGearDef" && e.Field == nameof(ClassGearDef.WeaponTypes));
    }

    [Fact]
    public void A_class_without_a_gear_row_is_reported()
    {
        var set = Set(classes: [Class("warrior", 1), Class("mage", 2)]);

        Assert.Contains(Validate(set), e => e.Table == "ClassGearDef" && e.Message.Contains("'mage'"));
    }

    [Fact]
    public void Stat_bonuses_belong_to_equipment_name_a_value_and_one_row_per_stat()
    {
        var horn = new ItemDef { Id = "horn", NetId = 1, Category = ItemCategory.SellOnly, MaxStack = 99, VisualId = "drop", NameId = "item.name" };

        Assert.Contains(Validate(Set(items: [Sword(), horn], bonuses: [new ItemStatBonusDef { Id = "b", ItemId = "horn", StatId = "atk", Flat = 1 }])),
            e => e.Table == "ItemStatBonusDef" && e.Field == nameof(ItemStatBonusDef.ItemId));
        Assert.Contains(Validate(Set(bonuses: [new ItemStatBonusDef { Id = "b", ItemId = "sword", StatId = "atk" }])),
            e => e.Table == "ItemStatBonusDef" && e.Field == nameof(ItemStatBonusDef.Flat));
        Assert.Contains(Validate(Set(bonuses:
            [
                new ItemStatBonusDef { Id = "a", ItemId = "sword", StatId = "atk", Flat = 1 },
                new ItemStatBonusDef { Id = "b", ItemId = "sword", StatId = "atk", Percent = 2 },
            ])), e => e.Table == "ItemStatBonusDef" && e.Field == nameof(ItemStatBonusDef.StatId));
        Assert.Contains(Validate(Set(bonuses: [new ItemStatBonusDef { Id = "b", ItemId = "sword", StatId = "nope", Flat = 1 }])),
            e => e.Code == ValidationCodes.BrokenRef && e.Field == nameof(ItemStatBonusDef.StatId));
    }
}
