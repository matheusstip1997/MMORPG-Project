using System.Reflection;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Shared.Tests.Protocol;

public class InputPacketTests
{
    private static InputFrame Frame(ushort sequence, uint tick, ushort facing = 0) => new()
    {
        Sequence = sequence,
        ClientTick = tick,
        SubTick = 17,
        Keys = MoveKeys.W | MoveKeys.A,
        Facing = Quantize.RoundTripAngle(facing, InputPacket.FacingBits),
        Actions = InputActions.Attack | InputActions.Parry,
        SkillSlot = 3,
    };

    private static byte[] Write(params InputFrame[] frames)
    {
        var buffer = new byte[InputPacket.MaxSize];
        int length = InputPacket.Write(buffer, frames);
        return buffer.AsSpan(0, length).ToArray();
    }

    [Fact]
    public void Three_redundant_frames_round_trip()
    {
        InputFrame[] frames = [Frame(10, 1000, 4096), Frame(9, 999, 2048), Frame(8, 997, 1024)];
        byte[] packet = Write(frames);

        var read = new InputFrame[InputPacket.MaxFrames];
        Assert.True(InputPacket.TryRead(packet, read, out int count));
        Assert.Equal(3, count);
        Assert.Equal(frames, read[..3]);
        Assert.Equal(InputPacket.SizeFor(3), packet.Length);
        Assert.Equal(20, packet.Length);
    }

    [Fact]
    public void Boundary_values_round_trip()
    {
        var newest = new InputFrame
        {
            Sequence = 0,
            ClientTick = uint.MaxValue,
            SubTick = 63,
            Keys = MoveKeys.W | MoveKeys.A | MoveKeys.S | MoveKeys.D,
            Facing = Quantize.RoundTripAngle(65535, InputPacket.FacingBits),
            Actions = InputActions.Attack | InputActions.Dodge | InputActions.Parry | InputActions.Interact,
            SkillSlot = InputPacket.MaxSkillSlot,
        };
        var older = new InputFrame { Sequence = ushort.MaxValue, ClientTick = uint.MaxValue - InputPacket.MaxTickDelta };
        byte[] packet = Write(newest, older);

        var read = new InputFrame[2];
        Assert.True(InputPacket.TryRead(packet, read, out _));
        Assert.Equal(newest, read[0]);
        Assert.Equal(older, read[1]);
    }

    [Fact]
    public void Ten_frames_fit_and_eleven_are_refused()
    {
        var frames = Enumerable.Range(0, InputPacket.MaxFrames).Select(i => Frame((ushort)(50 - i), (uint)(500 - i))).ToArray();
        byte[] packet = Write(frames);
        var read = new InputFrame[InputPacket.MaxFrames];
        Assert.True(InputPacket.TryRead(packet, read, out int count));
        Assert.Equal(InputPacket.MaxFrames, count);

        var tooMany = Enumerable.Range(0, InputPacket.MaxFrames + 1).Select(i => Frame((ushort)(50 - i), (uint)(500 - i))).ToArray();
        Assert.Throws<ArgumentException>(() => InputPacket.Write(new byte[64], tooMany));
    }

    [Fact]
    public void Writer_refuses_frames_it_cannot_encode()
    {
        var buffer = new byte[InputPacket.MaxSize];
        Assert.Throws<ArgumentException>(() => InputPacket.Write(buffer, ReadOnlySpan<InputFrame>.Empty));
        Assert.Throws<ArgumentException>(() => InputPacket.Write(buffer, new[] { Frame(5, 100), Frame(3, 99) }));
        Assert.Throws<ArgumentException>(() => InputPacket.Write(buffer, new[] { Frame(5, 100), Frame(4, 100) }));
        Assert.Throws<ArgumentException>(() => InputPacket.Write(buffer, new[] { Frame(5, 1000), Frame(4, 1000 - 256) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => InputPacket.Write(buffer, new[] { Frame(5, 100) with { SubTick = 64 } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => InputPacket.Write(buffer, new[] { Frame(5, 100) with { SkillSlot = 16 } }));
        Assert.Throws<ArgumentException>(() => InputPacket.Write(new byte[3], new[] { Frame(5, 100) }));
    }

    [Fact]
    public void Every_truncation_and_extension_is_refused()
    {
        byte[] packet = Write(Frame(10, 1000), Frame(9, 999), Frame(8, 998));
        var read = new InputFrame[InputPacket.MaxFrames];
        for (int length = 0; length < packet.Length; length++)
            Assert.False(InputPacket.TryRead(packet.AsSpan(0, length), read, out _));
        Assert.False(InputPacket.TryRead([.. packet, 0], read, out _));
    }

    [Fact]
    public void Malicious_headers_are_refused()
    {
        byte[] packet = Write(Frame(10, 1000), Frame(9, 999));
        var read = new InputFrame[InputPacket.MaxFrames];

        byte[] wrongId = [.. packet];
        wrongId[0] = (byte)HotMessageId.Snapshot;
        Assert.False(InputPacket.TryRead(wrongId, read, out _));

        // Frame count is the low 4 bits of byte 1.
        byte[] zeroFrames = [.. packet];
        zeroFrames[1] &= 0xF0;
        Assert.False(InputPacket.TryRead(zeroFrames, read, out _));

        byte[] fifteenFrames = [.. packet];
        fifteenFrames[1] |= 0x0F;
        Assert.False(InputPacket.TryRead(fifteenFrames, read, out _));

        Assert.False(InputPacket.TryRead(packet, new InputFrame[1], out _));
    }

    [Fact]
    public void Zero_tick_delta_and_tick_underflow_are_refused()
    {
        var read = new InputFrame[InputPacket.MaxFrames];
        Assert.False(InputPacket.TryRead(Forge(newestTick: 1000, tickDelta: 0), read, out _));
        Assert.False(InputPacket.TryRead(Forge(newestTick: 3, tickDelta: 4), read, out _));
        Assert.True(InputPacket.TryRead(Forge(newestTick: 3, tickDelta: 3), read, out _));
    }

    [Fact]
    public void Max_frames_matches_the_input_redundancy_range()
    {
        // StartingValuesM1.InputRedundancy (spec 1.7) can never ask for more frames than a packet carries.
        var range = typeof(StartingValuesM1).GetProperty(nameof(StartingValuesM1.InputRedundancy))!.GetCustomAttribute<RangeAttribute>()!;
        Assert.Equal(InputPacket.MaxFrames, range.Max);
        Assert.True(InputPacket.MaxFrames < 1 << InputPacket.FrameCountBits);
    }

    /// <summary>A two-frame packet built bit by bit, to reach values the writer refuses to produce.</summary>
    private static byte[] Forge(uint newestTick, uint tickDelta)
    {
        var buffer = new byte[InputPacket.SizeFor(2)];
        var writer = new BitWriter(buffer);
        writer.WriteBits((uint)HotMessageId.Input, 8);
        writer.WriteBits(2, InputPacket.FrameCountBits);
        writer.WriteBits(7, InputPacket.SequenceBits);
        writer.WriteBits(newestTick, InputPacket.ClientTickBits);
        writer.WriteBits(0, 28);
        writer.WriteBits(tickDelta, InputPacket.TickDeltaBits);
        writer.WriteBits(0, 28);
        writer.Finish();
        return buffer;
    }
}
