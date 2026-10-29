using Shared.Data.Tables;
using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>The Bystial book messages (protocol 21, WP-M2-8, spec 14.2, 9.4): round trips and the checks of their readers.</summary>
public class BookMessagesTests
{
    [Fact]
    public void A_book_state_round_trips_through_the_guarded_reader()
    {
        var sent = new BookState
        {
            Version = 3, Cause = BookCause.CardEarned, SlotCount = 2,
            Cards = new[]
            {
                new BookCard { MonsterNetId = 1, Tier = BystialTier.Normal, Slot = 0, Kills = 100, NextTierKills = 500 },
                new BookCard { MonsterNetId = 2, Tier = BystialTier.None, Slot = BookLimits.NoSlot, Kills = 37, NextTierKills = 100 },
            },
        };
        var bytes = ReliableMessages.Write(ReliableMessageId.BookState, sent);

        Assert.True(ReliableMessageReader.IsWellFormed(bytes, int.MaxValue));
        Assert.True(ReliableMessages.TryRead(bytes, out BookState read));
        Assert.Equal((3u, BookCause.CardEarned, (byte)2), (read.Version, read.Cause, read.SlotCount));
        Assert.Equal(2, read.Cards.Length);
        Assert.Equal((BystialTier.Normal, (byte)0, 100L, 500L), (read.Cards[0].Tier, read.Cards[0].Slot, read.Cards[0].Kills, read.Cards[0].NextTierKills));
        Assert.Equal((BystialTier.None, BookLimits.NoSlot, 37L), (read.Cards[1].Tier, read.Cards[1].Slot, read.Cards[1].Kills));
    }

    [Theory]
    [InlineData(2, 1)] // a slot at or past the slot count
    [InlineData(0, 0)] // a placed card without a tier
    public void A_state_with_an_impossible_slot_is_refused(int slot, int tier)
    {
        var state = new BookState
        {
            SlotCount = 2,
            Cards = new[] { new BookCard { MonsterNetId = 1, Tier = (BystialTier)tier, Slot = (byte)slot } },
        };
        Assert.False(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.BookState, state), out BookState _));
    }

    [Fact]
    public void Two_cards_of_one_monster_or_in_one_slot_are_refused()
    {
        var sameMonster = new BookState { SlotCount = 2, Cards = new[] { Card(1, 0), Card(1, BookLimits.NoSlot) } };
        var sameSlot = new BookState { SlotCount = 2, Cards = new[] { Card(1, 1), Card(2, 1) } };
        Assert.False(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.BookState, sameMonster), out BookState _));
        Assert.False(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.BookState, sameSlot), out BookState _));
    }

    [Fact]
    public void More_cards_than_the_cap_fail_the_guard()
    {
        var state = new BookState
        {
            Cards = Enumerable.Range(1, BookLimits.MaxCards + 1).Select(i => Card(i, BookLimits.NoSlot)).ToArray(),
        };
        var bytes = ReliableMessages.Write(ReliableMessageId.BookState, state);
        Assert.False(ReliableMessageReader.IsWellFormed(bytes, int.MaxValue));
        Assert.False(ReliableMessages.TryRead(bytes, out BookState _));
    }

    [Fact]
    public void A_change_request_and_its_result_round_trip()
    {
        var request = ReliableMessages.Write(ReliableMessageId.BookChangeRequest,
            new BookChangeRequest { RequestId = 9, MonsterNetId = 3, Slot = BookLimits.AnyFreeSlot });
        Assert.True(ReliableMessages.TryRead(request, out BookChangeRequest readRequest));
        Assert.Equal((9u, (ushort)3, BookLimits.AnyFreeSlot), (readRequest.RequestId, readRequest.MonsterNetId, readRequest.Slot));

        var result = ReliableMessages.Write(ReliableMessageId.BookChangeResult, new BookChangeResult { RequestId = 9, Reason = BookReason.InCombat });
        Assert.True(ReliableMessages.TryRead(result, out BookChangeResult readResult));
        Assert.Equal(BookReason.InCombat, readResult.Reason);
        var unknown = ReliableMessages.Write(ReliableMessageId.BookChangeResult,
            new BookChangeResult { Reason = BookLimits.LastReason + 1 });
        Assert.False(ReliableMessages.TryRead(unknown, out BookChangeResult _));
    }

    [Fact]
    public void Only_the_change_request_comes_from_clients()
    {
        Assert.True(ReliableMessageReader.TryGetSpec(ReliableMessageId.BookChangeRequest, out var request));
        Assert.Equal(ReliableDirection.ClientToServer, request.Direction);
        Assert.True(ReliableMessageReader.TryGetSpec(ReliableMessageId.BookState, out var state));
        Assert.Equal(ReliableDirection.ServerToClient, state.Direction);
        Assert.True(ReliableMessageReader.TryGetSpec(ReliableMessageId.BookChangeResult, out var result));
        Assert.Equal(ReliableDirection.ServerToClient, result.Direction);
    }

    [Fact]
    public void The_book_came_with_protocol_21_as_reliable_ids_25_to_27()
    {
        // WP-M2-8 takes main + 1 at merge (spec 1.7) and the next free reliable ids.
        Assert.True(ProtocolVersion.Current >= 21);
        Assert.Equal((25, 26, 27), ((int)ReliableMessageId.BookState, (int)ReliableMessageId.BookChangeRequest, (int)ReliableMessageId.BookChangeResult));
    }

    private static BookCard Card(int netId, byte slot) =>
        new() { MonsterNetId = (ushort)netId, Tier = BystialTier.Normal, Slot = slot };
}
