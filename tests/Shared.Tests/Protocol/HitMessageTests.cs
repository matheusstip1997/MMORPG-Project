using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>The hit message (backlog D3, spec 1.7 hot messages): layout, bounds and malformed input (R-2.5-04).</summary>
public class HitMessageTests
{
    private static HitEntry Hit(uint target, int amount = 17, HitKind kind = HitKind.Damaged, bool crit = false) =>
        new() { AttackerId = 1, TargetId = target, Amount = amount, Crit = crit, Kind = kind };

    private static byte[] Write(uint tick, params HitEntry[] hits)
    {
        var buffer = new byte[HitMessage.SizeFor(hits.Length)];
        int length = HitMessage.Write(buffer, tick, hits);
        Assert.Equal(buffer.Length, length);
        return buffer;
    }

    [Fact]
    public void Hits_round_trip_with_their_tick()
    {
        HitEntry[] hits =
        [
            Hit(2, 17), Hit(3, 0, HitKind.Dodged), Hit(4, HitMessage.MaxAmount, HitKind.Killed, crit: true),
            new() { AttackerId = uint.MaxValue, TargetId = uint.MaxValue, Amount = 5, Kind = HitKind.Parried },
        ];
        byte[] message = Write(123_456, hits);

        var read = new HitEntry[HitMessage.MaxHits];
        Assert.True(HitMessage.TryRead(message, out uint tick, read, out int count));
        Assert.Equal(123_456u, tick);
        Assert.Equal(hits, read[..count]);
        Assert.True(HotMessage.TryPeekId(message, out var id));
        Assert.Equal(HotMessageId.Hit, id);
    }

    [Fact]
    public void Sizes_follow_the_layout()
    {
        Assert.Equal(46, HitMessage.HeaderBits);
        Assert.Equal(99, HitMessage.EntryBits);
        Assert.Equal(19, HitMessage.SizeFor(1)); // 145 bits
        Assert.Equal(63, HitMessage.MaxHitsFor(1_200)); // the count field caps it
        Assert.Equal(1, HitMessage.MaxHitsFor(19));
        Assert.Equal(0, HitMessage.MaxHitsFor(18));
    }

    [Fact]
    public void Writing_refuses_what_the_layout_cannot_hold()
    {
        var buffer = new byte[HitMessage.SizeFor(HitMessage.MaxHits + 1)];
        Assert.Throws<ArgumentException>(() => HitMessage.Write(buffer, 1, ReadOnlySpan<HitEntry>.Empty));
        Assert.Throws<ArgumentException>(() => HitMessage.Write(buffer, 1, new HitEntry[HitMessage.MaxHits + 1]));
        Assert.Throws<ArgumentException>(() => HitMessage.Write(new byte[10], 1, [Hit(2)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => HitMessage.Write(buffer, 1, [Hit(2, amount: -1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => HitMessage.Write(buffer, 1, [Hit(2, kind: HitKind.None)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => HitMessage.Write(buffer, 1, [Hit(2, kind: (HitKind)5)]));
    }

    [Fact]
    public void Malformed_messages_are_refused()
    {
        byte[] valid = Write(9, Hit(2), Hit(3));
        var read = new HitEntry[HitMessage.MaxHits];
        Assert.True(HitMessage.TryRead(valid, out _, read, out _));

        byte[] wrongId = [.. valid];
        wrongId[0] = (byte)HotMessageId.Snapshot;
        Assert.False(HitMessage.TryRead(wrongId, out _, read, out _));

        Assert.False(HitMessage.TryRead(valid.AsSpan(0, valid.Length - 1), out _, read, out _));
        Assert.False(HitMessage.TryRead([.. valid, 0], out _, read, out _));
        Assert.False(HitMessage.TryRead(valid, out _, new HitEntry[1], out _)); // more hits than the destination holds
        Assert.False(HitMessage.TryRead(ReadOnlySpan<byte>.Empty, out _, read, out _));

        Assert.False(HitMessage.TryRead(Write(9, Hit(0)), out _, read, out _)); // target 0 is never an entity

        // Count 0: the 6 bits after the id and the tick (bit 40).
        byte[] zero = Write(9, Hit(2));
        zero[5] &= 0xC0;
        Assert.False(HitMessage.TryRead(zero, out _, read, out _));
    }

    [Fact]
    public void An_unknown_kind_is_refused()
    {
        byte[] message = Write(9, Hit(2, kind: HitKind.Parried));
        // The kind is the last 3 bits of the entry: bits 142-144, so 142 % 8 = 6 in byte 17 and bit 0 of byte 18.
        message[17] |= 0xC0;
        message[18] |= 0x01; // 0b111 = 7
        Assert.False(HitMessage.TryRead(message, out _, new HitEntry[1], out _));
    }
}
