using Shared.Simulation.Items;

namespace Shared.Tests.Simulation.Items;

/// <summary>Bag stacking rules (backlog G, spec 4.1 stack key, R-4.3-02 M1 "take what fits").</summary>
public class BagTests
{
    private const int MaxStack = 10;

    private static ItemStack Horn(int count, byte rarity = 0, bool stolen = false) => new(1, rarity, stolen, count);

    private static ItemStack Ash(int count) => new(2, 0, false, count);

    [Fact]
    public void Add_fills_stacks_of_the_same_key_first_then_empty_slots_in_slot_order()
    {
        var bag = new Bag(4);
        bag.Load(2, Horn(7));
        var changes = new List<SlotChange>();

        int added = bag.Add(Horn(15), MaxStack, changes);

        Assert.Equal(15, added);
        Assert.Equal(Horn(10), bag[2]); // topped up first
        Assert.Equal(Horn(10), bag[0]); // then the first empty slot
        Assert.Equal(Horn(2), bag[1]);
        Assert.True(bag[3].IsEmpty);
        Assert.Equal(new[] { 2, 0, 1 }, changes.Select(c => c.Slot));
        Assert.Equal(Horn(7), changes[0].Before);
    }

    [Fact]
    public void Add_takes_what_fits_and_reports_it()
    {
        var bag = new Bag(2);
        bag.Load(0, Ash(1));
        bag.Load(1, Horn(8));

        Assert.Equal(2, bag.Add(Horn(5), MaxStack, null));
        Assert.Equal(Horn(10), bag[1]);
        Assert.Equal(0, bag.Add(Horn(1), MaxStack, null));
        Assert.Equal(0, bag.SpaceFor(Horn(1), MaxStack));
    }

    [Fact]
    public void Rarity_and_the_stolen_flag_are_part_of_the_stack_key()
    {
        var bag = new Bag(3);
        bag.Add(Horn(1), MaxStack, null);
        bag.Add(Horn(1, rarity: 1), MaxStack, null);
        bag.Add(Horn(1, stolen: true), MaxStack, null);

        Assert.Equal(Horn(1), bag[0]);
        Assert.Equal(Horn(1, rarity: 1), bag[1]);
        Assert.Equal(Horn(1, stolen: true), bag[2]);
        Assert.Equal(1, bag.CountOf(Horn(0)));
    }

    [Fact]
    public void Space_counts_free_slots_and_room_in_matching_stacks()
    {
        var bag = new Bag(3);
        bag.Load(0, Horn(4));
        bag.Load(1, Ash(9));

        Assert.Equal(6 + MaxStack, bag.SpaceFor(Horn(1), MaxStack));
        Assert.Equal(1 + MaxStack, bag.SpaceFor(Ash(1), MaxStack));
    }

    [Fact]
    public void Remove_takes_units_from_one_slot_and_empties_it_at_zero()
    {
        var bag = new Bag(2);
        bag.Load(0, Horn(3));
        var changes = new List<SlotChange>();

        Assert.True(bag.TryRemove(0, 2, changes));
        Assert.Equal(Horn(1), bag[0]);
        Assert.True(bag.TryRemove(0, 1, changes));
        Assert.True(bag[0].IsEmpty);
        Assert.Equal(2, changes.Count);
    }

    [Theory]
    [InlineData(0, 4)] // more than it holds
    [InlineData(1, 1)] // an empty slot
    [InlineData(5, 1)] // out of range
    [InlineData(0, 0)] // nothing
    public void Remove_refuses_and_changes_nothing(int slot, int count)
    {
        var bag = new Bag(2);
        bag.Load(0, Horn(3));

        Assert.False(bag.TryRemove(slot, count, null));
        Assert.Equal(Horn(3), bag[0]);
    }

    [Fact]
    public void Move_into_an_empty_slot()
    {
        var bag = new Bag(3);
        bag.Load(0, Horn(3));

        Assert.Equal(BagMoveResult.Moved, bag.TryMove(0, 2, MaxStack, null));
        Assert.True(bag[0].IsEmpty);
        Assert.Equal(Horn(3), bag[2]);
    }

    [Fact]
    public void Move_onto_the_same_key_merges_and_leaves_the_rest()
    {
        var bag = new Bag(2);
        bag.Load(0, Horn(6));
        bag.Load(1, Horn(7));

        Assert.Equal(BagMoveResult.Merged, bag.TryMove(0, 1, MaxStack, null));
        Assert.Equal(Horn(3), bag[0]);
        Assert.Equal(Horn(10), bag[1]);
        Assert.Equal(BagMoveResult.Refused, bag.TryMove(0, 1, MaxStack, null)); // target full
    }

    [Fact]
    public void Move_onto_another_item_swaps()
    {
        var bag = new Bag(2);
        bag.Load(0, Horn(6));
        bag.Load(1, Ash(2));
        var changes = new List<SlotChange>();

        Assert.Equal(BagMoveResult.Swapped, bag.TryMove(0, 1, MaxStack, changes));
        Assert.Equal(Ash(2), bag[0]);
        Assert.Equal(Horn(6), bag[1]);
        Assert.Equal(2, changes.Count);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)] // empty source
    [InlineData(0, 9)]
    [InlineData(-1, 0)]
    public void Move_refuses_and_changes_nothing(int from, int to)
    {
        var bag = new Bag(2);
        bag.Load(0, Horn(6));

        Assert.Equal(BagMoveResult.Refused, bag.TryMove(from, to, MaxStack, null));
        Assert.Equal(Horn(6), bag[0]);
        Assert.True(bag[1].IsEmpty);
    }

    [Fact]
    public void Units_are_never_created_or_lost_by_moves()
    {
        var bag = new Bag(5);
        bag.Add(Horn(23), MaxStack, null);
        bag.Add(Ash(4), MaxStack, null);
        for (int i = 0; i < 40; i++)
            bag.TryMove(i % 5, (i * 3 + 1) % 5, MaxStack, null);

        Assert.Equal(23, bag.CountOf(Horn(0)));
        Assert.Equal(4, bag.CountOf(Ash(0)));
    }
}
