using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>
/// The MemoryPack reliable messages of backlog G (PickupRequest, PickupResult, InventoryContents; WP16 InventoryMoveRequest,
/// InventoryMoveResult): round trips, framing,
/// and R-2.5-04 (random and mutated bytes are refused cleanly, never throw). Seeds are fixed so failures reproduce.
/// </summary>
public class LootMessagesTests
{
    private const int Iterations = 20_000;

    private static InventoryContents Contents() => new()
    {
        Revision = 3,
        SlotCount = 30,
        Slots =
        [
            new InventorySlotEntry { Slot = 0, ItemNetId = 1, Rarity = 0, Count = 2 },
            new InventorySlotEntry { Slot = 29, ItemNetId = 6, Rarity = 1, Stolen = true, Count = 99 },
        ],
    };

    [Fact]
    public void A_pickup_request_round_trips_behind_its_id()
    {
        var bytes = ReliableMessages.Write(ReliableMessageId.PickupRequest, new PickupRequest { DropEntityId = 123_456 });

        Assert.Equal((byte)ReliableMessageId.PickupRequest, bytes[0]);
        Assert.Equal(ReliableMessageId.PickupRequest, ReliableMessages.PeekId(bytes));
        Assert.True(ReliableMessages.TryRead(bytes, out PickupRequest request));
        Assert.Equal(123_456u, request.DropEntityId);
    }

    [Fact]
    public void A_pickup_result_round_trips()
    {
        var bytes = ReliableMessages.Write(ReliableMessageId.PickupResult,
            new PickupResult { DropEntityId = 7, Reason = PickupReason.LootLocked, Taken = 0 });

        Assert.True(ReliableMessages.TryRead(bytes, out PickupResult result));
        Assert.Equal(7u, result.DropEntityId);
        Assert.Equal(PickupReason.LootLocked, result.Reason);
    }

    [Fact]
    public void Inventory_contents_round_trip()
    {
        var bytes = ReliableMessages.Write(ReliableMessageId.InventoryContents, Contents());

        Assert.True(ReliableMessages.TryRead(bytes, out InventoryContents contents));
        Assert.Equal(3u, contents.Revision);
        Assert.Equal(30, contents.SlotCount);
        Assert.Equal(2, contents.Slots.Length);
        Assert.Equal(29, contents.Slots[1].Slot);
        Assert.True(contents.Slots[1].Stolen);
        Assert.Equal(99, contents.Slots[1].Count);
    }

    [Fact]
    public void A_message_under_another_id_is_refused()
    {
        var bytes = ReliableMessages.Write(ReliableMessageId.PickupRequest, new PickupRequest { DropEntityId = 1 });

        Assert.False(ReliableMessages.TryRead(bytes, out PickupResult _));
        Assert.False(ReliableMessages.TryRead(bytes, out InventoryContents _));
        Assert.False(ReliableMessages.TryRead(new byte[] { (byte)ReliableMessageId.PickupRequest }, out PickupRequest _));
        Assert.Equal(ReliableMessageId.None, ReliableMessages.PeekId(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Contents_with_a_slot_outside_the_bag_or_an_empty_entry_are_refused()
    {
        var outside = Contents();
        outside.Slots[0].Slot = 30;
        Assert.False(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.InventoryContents, outside), out InventoryContents _));

        var empty = Contents();
        empty.Slots[0].Count = 0;
        Assert.False(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.InventoryContents, empty), out InventoryContents _));
    }

    [Fact]
    public void A_result_with_an_unknown_reason_is_refused()
    {
        var bytes = ReliableMessages.Write(ReliableMessageId.PickupResult, new PickupResult { Reason = (PickupReason)200 });

        Assert.False(ReliableMessages.TryRead(bytes, out PickupResult _));
    }

    [Fact]
    public void A_move_request_and_its_result_round_trip()
    {
        var request = ReliableMessages.Write(ReliableMessageId.InventoryMoveRequest,
            new InventoryMoveRequest { RequestId = 4_000_000_000, FromSlot = 2, ToSlot = 29 });
        var result = ReliableMessages.Write(ReliableMessageId.InventoryMoveResult,
            new InventoryMoveResult { RequestId = 9, Outcome = InventoryMoveOutcome.Swapped });

        Assert.True(ReliableMessages.TryRead(request, out InventoryMoveRequest move));
        Assert.Equal((4_000_000_000u, (byte)2, (byte)29), (move.RequestId, move.FromSlot, move.ToSlot));
        Assert.True(ReliableMessages.TryRead(result, out InventoryMoveResult answer));
        Assert.Equal((9u, InventoryMoveReason.None, InventoryMoveOutcome.Swapped), (answer.RequestId, answer.Reason, answer.Outcome));
        Assert.False(ReliableMessages.TryRead(request, out InventoryMoveResult _));
    }

    [Theory]
    [InlineData((byte)200, (byte)0)] // unknown reason
    [InlineData((byte)0, (byte)200)] // unknown outcome
    [InlineData((byte)0, (byte)0)] // neither refused nor done
    [InlineData((byte)2, (byte)1)] // both
    public void A_move_result_that_is_not_exactly_a_refusal_or_an_outcome_is_refused(byte reason, byte outcome)
    {
        var bytes = ReliableMessages.Write(ReliableMessageId.InventoryMoveResult,
            new InventoryMoveResult { Reason = (InventoryMoveReason)reason, Outcome = (InventoryMoveOutcome)outcome });

        Assert.False(ReliableMessages.TryRead(bytes, out InventoryMoveResult _));
    }

    [Fact]
    public void Random_bytes_never_throw()
    {
        var random = new Random(2468);
        for (int i = 0; i < Iterations; i++)
        {
            var message = new byte[random.Next(0, 200)];
            random.NextBytes(message);
            if (message.Length > 0)
                message[0] = (byte)random.Next(0, 11);
            ReliableMessages.TryRead(message, out PickupRequest _);
            ReliableMessages.TryRead(message, out PickupResult _);
            ReliableMessages.TryRead(message, out InventoryContents _);
            ReliableMessages.TryRead(message, out InventoryMoveRequest _);
            ReliableMessages.TryRead(message, out InventoryMoveResult _);
        }
    }

    [Fact]
    public void Mutated_inventory_contents_never_throw()
    {
        var random = new Random(1357);
        var valid = ReliableMessages.Write(ReliableMessageId.InventoryContents, Contents());
        for (int i = 0; i < Iterations; i++)
        {
            var message = (byte[])valid.Clone();
            int flips = random.Next(1, 4);
            for (int f = 0; f < flips; f++)
                message[random.Next(1, message.Length)] = (byte)random.Next(256);
            if (random.Next(4) == 0)
                Array.Resize(ref message, random.Next(1, message.Length));
            if (ReliableMessages.TryRead(message, out InventoryContents contents))
                Assert.True(contents.Slots.Length <= contents.SlotCount);
        }
    }
}
