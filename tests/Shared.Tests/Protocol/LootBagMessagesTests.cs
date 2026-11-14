using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>
/// The death loot bag messages (WP-M4-9, protocol 47): each round-trips through its reader, and a value out of range or a
/// broken list is refused (R-2.5-04); the stolen mark group of an inventory slot survives the trip.
/// </summary>
public class LootBagMessagesTests
{
    [Fact]
    public void A_request_round_trips_and_a_bad_op_or_bag_is_refused()
    {
        var request = new LootBagRequest { RequestId = 3, BagEntityId = 9, Op = LootBagOp.TakeOne, Row = 4 };
        Assert.True(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.LootBagRequest, request), out LootBagRequest read));
        Assert.Equal((3u, 9u, LootBagOp.TakeOne, (byte)4), (read.RequestId, read.BagEntityId, read.Op, read.Row));

        Assert.False(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.LootBagRequest,
            new LootBagRequest { RequestId = 1, BagEntityId = 9, Op = LootBagOp.None }), out LootBagRequest _));
        Assert.False(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.LootBagRequest,
            new LootBagRequest { RequestId = 1, BagEntityId = 0, Op = LootBagOp.Open }), out LootBagRequest _));
    }

    [Fact]
    public void Contents_with_two_rows_of_one_index_are_refused()
    {
        var row = new LootBagRowEntry { Index = 1, ItemNetId = 2, Count = 3 };
        var good = new LootBagContent { BagEntityId = 5, Access = LootBagAccess.Own, Rows = [row] };
        Assert.True(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.LootBagContent, good), out LootBagContent read));
        Assert.Equal((LootBagAccess.Own, 3), (read.Access, (int)read.Rows[0].Count));

        var twice = new LootBagContent { BagEntityId = 5, Rows = [row, row] };
        Assert.False(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.LootBagContent, twice), out LootBagContent _));
    }

    [Fact]
    public void A_result_and_the_own_bags_round_trip()
    {
        var result = new LootBagResult { RequestId = 2, BagEntityId = 5, Op = LootBagOp.TakeAll, Reason = LootBagReason.BagLocked, Stolen = true };
        Assert.True(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.LootBagResult, result), out LootBagResult read));
        Assert.Equal((LootBagReason.BagLocked, true), (read.Reason, read.Stolen));

        var own = new OwnLootBags { Revision = 1, Bags = [new OwnLootBagEntry { BagEntityId = 5, XCm = -100, YCm = 200, Units = 7 }] };
        Assert.True(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.OwnLootBags, own), out OwnLootBags back));
        Assert.Equal((-100, 200, (ushort)7), (back.Bags[0].XCm, back.Bags[0].YCm, back.Bags[0].Units));
    }

    [Fact]
    public void The_stolen_mark_group_of_a_slot_survives_the_inventory_contents()
    {
        var contents = new InventoryContents
        {
            SlotCount = 2,
            Slots = [new InventorySlotEntry { Slot = 0, ItemNetId = 1, Stolen = true, Count = 3, StolenMark = 2 }],
        };
        Assert.True(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.InventoryContents, contents), out InventoryContents read));
        Assert.Equal((byte)2, read.Slots[0].StolenMark);
    }
}
