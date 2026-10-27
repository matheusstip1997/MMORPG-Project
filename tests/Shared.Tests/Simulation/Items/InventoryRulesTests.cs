using Shared.Data.Tables;
using Shared.Simulation.Items;

namespace Shared.Tests.Simulation.Items;

/// <summary>
/// The shared multi-container rules (WP-M2-1; R-4.1-02 containers and accepted categories, spec 4.1 M2 item model, R-3.5-07
/// weapon slots and dual weapons): instance stacks, moves between containers, equip with displacement, unequip.
/// </summary>
public class InventoryRulesTests
{
    private const byte Normal = 1, Safe = 2, Valuables = 3, Equipped = 5;
    private const int Main = 0, Secondary = 1, Head = 2;

    private const ushort Horn = 1, Sword = 101, Daggers = 103, Shield = 104, Cap = 107;

    private sealed class Items : IItemRuleLookup
    {
        public bool TryGetItem(ushort itemNetId, out ItemRuleInfo info)
        {
            info = itemNetId switch
            {
                Horn => new ItemRuleInfo(ItemCategory.SellOnly, 99, 0),
                Sword => new ItemRuleInfo(ItemCategory.Equipment, 1, 1 << Main),
                Daggers => new ItemRuleInfo(ItemCategory.Equipment, 1, (1 << Main) | (1 << Secondary)),
                Shield => new ItemRuleInfo(ItemCategory.Equipment, 1, 1 << Secondary),
                Cap => new ItemRuleInfo(ItemCategory.Equipment, 1, 1 << Head),
                _ => default,
            };
            return itemNetId is Horn or Sword or Daggers or Shield or Cap;
        }
    }

    private static readonly Items Lookup = new();

    private static Inventory New(int safeSlots = 4) => new(new[]
    {
        new ContainerRule(Normal, 4, new[] { ItemCategory.Material, ItemCategory.SellOnly, ItemCategory.Consumable }, false),
        new ContainerRule(Safe, safeSlots, new[] { ItemCategory.Equipment, ItemCategory.MoldOrTool }, false),
        new ContainerRule(Valuables, 2, new[] { ItemCategory.Material, ItemCategory.SellOnly }, false),
        new ContainerRule(Equipped, 8, new[] { ItemCategory.Equipment }, true),
    });

    private static ItemStack Instance(ushort item, long id) => new(item, 0, false, 1, id);

    private static InventoryLocation At(byte container, int slot) => new(container, slot);

    private static void Put(Inventory inventory, byte container, int slot, in ItemStack stack)
    {
        Assert.True(inventory.TryIndex(container, out int index));
        inventory.BagAt(index).Load(slot, stack);
    }

    [Fact]
    public void Two_instances_of_one_item_never_merge_and_an_instance_is_one_item()
    {
        var bag = new Bag(3);
        bag.Load(0, Instance(Sword, 7));

        Assert.False(Instance(Sword, 7).SameKey(Instance(Sword, 8)));
        Assert.Equal(1, bag.Add(Instance(Sword, 8), 1, null));
        Assert.Equal(Instance(Sword, 8), bag[1]);
        Assert.Equal(1, bag.SpaceFor(Instance(Sword, 9), 1)); // only the empty slot
        Assert.Equal(BagMoveResult.Swapped, bag.TryMove(0, 1, 1, null));
        Assert.Equal(8, bag[0].InstanceId);
    }

    [Fact]
    public void A_move_between_containers_needs_the_target_to_take_the_category()
    {
        var inventory = New();
        Put(inventory, Normal, 0, new ItemStack(Horn, 0, false, 5));
        Put(inventory, Safe, 0, Instance(Sword, 1));
        var changes = new List<InventorySlotChange>();

        Assert.Equal(InventoryCheck.NotAccepted, inventory.TryMove(At(Normal, 0), At(Safe, 1), Lookup, changes).Check);
        Assert.Equal(InventoryCheck.NotAccepted, inventory.TryMove(At(Safe, 0), At(Normal, 1), Lookup, changes).Check);
        Assert.Empty(changes);

        var move = inventory.TryMove(At(Normal, 0), At(Valuables, 1), Lookup, changes);

        Assert.Equal(BagMoveResult.Moved, move.Result);
        Assert.Equal(5, inventory.At(At(Valuables, 1)).Count);
        Assert.True(inventory.At(At(Normal, 0)).IsEmpty);
        Assert.Equal(new[] { (Valuables, 1), (Normal, 0) }, changes.Select(c => (c.Container, c.Change.Slot)));
    }

    [Fact]
    public void Between_containers_the_same_key_merges_and_a_swap_needs_both_containers_to_take_what_they_get()
    {
        var inventory = New();
        Put(inventory, Normal, 0, new ItemStack(Horn, 0, false, 95));
        Put(inventory, Valuables, 0, new ItemStack(Horn, 0, false, 10));

        var merge = inventory.TryMove(At(Valuables, 0), At(Normal, 0), Lookup, null);

        Assert.Equal(BagMoveResult.Merged, merge.Result);
        Assert.Equal(99, inventory.At(At(Normal, 0)).Count);
        Assert.Equal(6, inventory.At(At(Valuables, 0)).Count);
        Assert.Equal(InventoryCheck.StackFull, inventory.TryMove(At(Valuables, 0), At(Normal, 0), Lookup, null).Check);
    }

    [Fact]
    public void Equipping_puts_the_item_at_its_slot_and_sends_what_was_there_back_to_the_source_slot()
    {
        var inventory = New();
        Put(inventory, Safe, 2, Instance(Sword, 2));
        Put(inventory, Equipped, Main, Instance(Sword, 1));

        var move = inventory.TryMove(At(Safe, 2), At(Equipped, Main), Lookup, null);

        Assert.Equal(BagMoveResult.Swapped, move.Result);
        Assert.Equal(2, inventory.At(At(Equipped, Main)).InstanceId);
        Assert.Equal(1, inventory.At(At(Safe, 2)).InstanceId);
    }

    [Fact]
    public void Equipment_goes_only_into_a_slot_it_fills_and_nothing_else_goes_into_equipped()
    {
        var inventory = New();
        Put(inventory, Safe, 0, Instance(Cap, 1));
        Put(inventory, Normal, 0, new ItemStack(Horn, 0, false, 1));

        Assert.Equal(InventoryCheck.NotAccepted, inventory.TryMove(At(Safe, 0), At(Equipped, Main), Lookup, null).Check);
        Assert.Equal(InventoryCheck.NotAccepted, inventory.TryMove(At(Normal, 0), At(Equipped, Head), Lookup, null).Check);
        Assert.Equal(BagMoveResult.Moved, inventory.TryMove(At(Safe, 0), At(Equipped, Head), Lookup, null).Result);
        Assert.Equal(InventoryCheck.SlotInvalid, inventory.TryMove(At(Equipped, Head), At(Equipped, Main), Lookup, null).Check);
    }

    [Fact]
    public void A_dual_weapon_fills_both_weapon_slots_and_displaces_the_sword_and_the_shield()
    {
        // R-3.5-07: one item that fills both weapon slots.
        var inventory = New();
        Put(inventory, Safe, 0, Instance(Daggers, 3));
        Put(inventory, Equipped, Main, Instance(Sword, 1));
        Put(inventory, Equipped, Secondary, Instance(Shield, 2));

        var move = inventory.TryMove(At(Safe, 0), At(Equipped, Secondary), Lookup, null);

        Assert.Equal(BagMoveResult.Swapped, move.Result);
        Assert.Equal(3, inventory.At(At(Equipped, Main)).InstanceId); // at its lowest slot, whichever slot it was dropped on
        Assert.True(inventory.At(At(Equipped, Secondary)).IsEmpty);
        Assert.Equal(Main, inventory.CoveredBy(Secondary, Lookup));
        Assert.Equal(1, inventory.At(At(Safe, 0)).InstanceId); // the first displaced takes the source slot
        Assert.Equal(2, inventory.At(At(Safe, 1)).InstanceId); // the next the first free slot

        // A shield into the covered slot takes the daggers out.
        var shieldBack = inventory.TryMove(At(Safe, 1), At(Equipped, Secondary), Lookup, null);
        Assert.Equal(BagMoveResult.Swapped, shieldBack.Result);
        Assert.Equal(2, inventory.At(At(Equipped, Secondary)).InstanceId);
        Assert.True(inventory.At(At(Equipped, Main)).IsEmpty);
        Assert.Equal(3, inventory.At(At(Safe, 1)).InstanceId);
    }

    [Fact]
    public void Equipping_refuses_when_the_displaced_gear_has_no_room()
    {
        var inventory = New(safeSlots: 1);
        Put(inventory, Safe, 0, Instance(Daggers, 3));
        Put(inventory, Equipped, Main, Instance(Sword, 1));
        Put(inventory, Equipped, Secondary, Instance(Shield, 2));
        var changes = new List<InventorySlotChange>();

        var move = inventory.TryMove(At(Safe, 0), At(Equipped, Main), Lookup, changes);

        Assert.Equal(InventoryCheck.NoRoom, move.Check);
        Assert.Empty(changes);
        Assert.Equal(1, inventory.At(At(Equipped, Main)).InstanceId);
    }

    [Fact]
    public void Unequipping_goes_into_an_empty_slot_of_a_container_that_takes_equipment()
    {
        var inventory = New();
        Put(inventory, Equipped, Head, Instance(Cap, 1));
        Put(inventory, Safe, 0, Instance(Sword, 2));

        Assert.Equal(InventoryCheck.NotAccepted, inventory.TryMove(At(Equipped, Head), At(Normal, 0), Lookup, null).Check);
        Assert.Equal(InventoryCheck.NotAccepted, inventory.TryMove(At(Equipped, Head), At(Safe, 0), Lookup, null).Check); // a sword does not fit the head
        Assert.Equal(BagMoveResult.Moved, inventory.TryMove(At(Equipped, Head), At(Safe, 3), Lookup, null).Result);
        Assert.Equal(1, inventory.At(At(Safe, 3)).InstanceId);
        Assert.True(inventory.At(At(Equipped, Head)).IsEmpty);
    }

    [Fact]
    public void Unequipping_onto_gear_that_fits_the_slot_equips_that_gear_and_the_unequipped_item_takes_its_place()
    {
        var inventory = New();
        Put(inventory, Equipped, Main, Instance(Sword, 1));
        Put(inventory, Equipped, Secondary, Instance(Shield, 2));
        Put(inventory, Safe, 0, Instance(Daggers, 3));

        Assert.Equal(Daggers, inventory.IncomingEquip(At(Equipped, Secondary), At(Safe, 0)).ItemNetId);
        var move = inventory.TryMove(At(Equipped, Secondary), At(Safe, 0), Lookup, null);

        Assert.Equal(BagMoveResult.Swapped, move.Result);
        Assert.Equal(3, inventory.At(At(Equipped, Main)).InstanceId);
        Assert.Equal(2, inventory.At(At(Safe, 0)).InstanceId); // the shield the player dragged lands where it was dropped
        Assert.Equal(1, inventory.At(At(Safe, 1)).InstanceId);
    }

    [Fact]
    public void Unknown_containers_and_slots_are_invalid_and_an_empty_source_is_reported()
    {
        var inventory = New();

        Assert.Equal(InventoryCheck.SlotInvalid, inventory.TryMove(At(9, 0), At(Normal, 0), Lookup, null).Check);
        Assert.Equal(InventoryCheck.SlotInvalid, inventory.TryMove(At(Normal, 0), At(Normal, 4), Lookup, null).Check);
        Assert.Equal(InventoryCheck.SlotInvalid, inventory.TryMove(At(Normal, 1), At(Normal, 1), Lookup, null).Check);
        Assert.Equal(InventoryCheck.SlotEmpty, inventory.TryMove(At(Normal, 0), At(Safe, 0), Lookup, null).Check);
    }

    [Fact]
    public void TryPlace_fills_only_an_empty_slot()
    {
        var inventory = New();
        var changes = new List<InventorySlotChange>();

        Assert.True(inventory.TryPlace(At(Equipped, Main), Instance(Sword, 1), changes));
        Assert.False(inventory.TryPlace(At(Equipped, Main), Instance(Sword, 2), changes));
        Assert.Single(changes);
        Assert.True(changes[0].Change.Before.IsEmpty);
    }

    [Theory]
    [InlineData(10, 0, 100, 10, 0)]
    [InlineData(10, 0, 110, 11, 0)]
    [InlineData(10, 3, 125, 12, 3)] // 12.5 and 3.75 truncate toward zero
    [InlineData(10, 3, 150, 15, 4)]
    [InlineData(10, 3, 200, 20, 6)]
    [InlineData(-7, -3, 150, -10, -4)]
    public void A_stat_bonus_scales_by_the_rarity_percent_with_integer_math(int flat, int percent, int statPercent, int scaledFlat,
        int scaledPercent)
    {
        var scaled = new ItemStatBonus("atk", flat, percent).Scale(statPercent);

        Assert.Equal(("atk", scaledFlat, scaledPercent), (scaled.StatId, scaled.Flat, scaled.Percent));
    }
}
