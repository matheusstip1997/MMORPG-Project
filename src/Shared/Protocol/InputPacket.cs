using System;

namespace Shared.Protocol
{
    /// <summary>Movement keys held during an input frame (R-2.2-01).</summary>
    [Flags]
    public enum MoveKeys : byte
    {
        None = 0,
        W = 1,
        A = 2,
        S = 4,
        D = 8,
    }

    /// <summary>
    /// Action buttons held during an input frame (R-2.2-01): left click attack, Ctrl dodge, right click defend/parry,
    /// interact. These are held states; a press is the edge between two consecutive frames.
    /// </summary>
    [Flags]
    public enum InputActions : byte
    {
        None = 0,
        Attack = 1,
        Dodge = 2,
        Parry = 4,
        Interact = 8,
    }

    /// <summary>One client input frame, sampled once per client tick (spec 1.7, R-3.1-04).</summary>
    public struct InputFrame
    {
        /// <summary>Wrapping frame sequence; the server acknowledges it in snapshots for reconciliation (spec 2.2).</summary>
        public ushort Sequence;
        public uint ClientTick;
        /// <summary>When inside the client tick the input happened, in 1/64 of a tick (R-3.1-04 sub-tick timestamps).</summary>
        public byte SubTick;
        public MoveKeys Keys;
        /// <summary>Facing as a binary angle (65536 = one turn). Sent with <see cref="InputPacket.FacingBits"/> bits.</summary>
        public ushort Facing;
        public InputActions Actions;
        /// <summary>Hotbar slot used this frame, 1 to <see cref="InputPacket.MaxSkillSlot"/>; 0 means none.</summary>
        public byte SkillSlot;
    }

    /// <summary>
    /// Client input packet (spec 1.7): the newest frame plus the frames before it, so a lost packet is covered by the
    /// next one. The client sends StartingValuesM1.InputRedundancy frames.
    /// Layout, LSB first: id 8, frameCount 4, newest sequence 16, newest client tick 32, newest body;
    /// then for each older frame (sequence = previous - 1): tick delta to the newer frame 8 (1 to 255), body.
    /// Body: subTick 6, keys 4, facing 10, actions 4, skillSlot 4.
    /// Encryption and the input integrity tag (R-2.5-02) wrap this payload; they are not part of it.
    /// </summary>
    public static class InputPacket
    {
        /// <summary>Upper bound of StartingValuesM1.InputRedundancy; a test keeps the two in step.</summary>
        public const int MaxFrames = 10;
        public const int FrameCountBits = 4;
        public const int SequenceBits = 16;
        public const int ClientTickBits = 32;
        public const int TickDeltaBits = 8;
        public const int MaxTickDelta = (1 << TickDeltaBits) - 1;
        public const int SubTickBits = 6;
        public const int KeysBits = 4;
        /// <summary>Spec 1.7 allows 8 to 10 bits; 10 bits is about 0.35 degrees.</summary>
        public const int FacingBits = 10;
        public const int ActionsBits = 4;
        public const int SkillSlotBits = 4;
        public const int MaxSkillSlot = (1 << SkillSlotBits) - 1;

        private const int BodyBits = SubTickBits + KeysBits + FacingBits + ActionsBits + SkillSlotBits;
        private const int FirstFrameBits = HotMessage.IdBits + FrameCountBits + SequenceBits + ClientTickBits + BodyBits;
        private const int OlderFrameBits = TickDeltaBits + BodyBits;

        public static int SizeFor(int frameCount) => (FirstFrameBits + (frameCount - 1) * OlderFrameBits + 7) >> 3;

        public static int MaxSize => SizeFor(MaxFrames);

        /// <summary>
        /// Writes the frames, newest first, and returns the bytes written. Frames must have consecutive sequences
        /// (wrapping) and client ticks that decrease by 1 to 255 from one frame to the next.
        /// </summary>
        public static int Write(Span<byte> destination, ReadOnlySpan<InputFrame> framesNewestFirst)
        {
            int count = framesNewestFirst.Length;
            if (count < 1 || count > MaxFrames)
                throw new ArgumentException($"An input packet carries 1 to {MaxFrames} frames.", nameof(framesNewestFirst));
            if (destination.Length < SizeFor(count))
                throw new ArgumentException("Destination is too small.", nameof(destination));

            var writer = new BitWriter(destination);
            writer.WriteBits((uint)HotMessageId.Input, HotMessage.IdBits);
            writer.WriteBits((uint)count, FrameCountBits);

            InputFrame newest = framesNewestFirst[0];
            writer.WriteBits(newest.Sequence, SequenceBits);
            writer.WriteBits(newest.ClientTick, ClientTickBits);
            WriteBody(ref writer, newest);

            for (int i = 1; i < count; i++)
            {
                InputFrame newer = framesNewestFirst[i - 1];
                InputFrame frame = framesNewestFirst[i];
                if (frame.Sequence != (ushort)(newer.Sequence - 1))
                    throw new ArgumentException("Frame sequences must be consecutive, newest first.", nameof(framesNewestFirst));
                long tickDelta = (long)newer.ClientTick - frame.ClientTick;
                if (tickDelta < 1 || tickDelta > MaxTickDelta)
                    throw new ArgumentException($"Client ticks must decrease by 1 to {MaxTickDelta} per frame.", nameof(framesNewestFirst));
                writer.WriteBits((uint)tickDelta, TickDeltaBits);
                WriteBody(ref writer, frame);
            }
            return writer.Finish();
        }

        /// <summary>
        /// Reads a packet into <paramref name="framesNewestFirst"/>. Returns false, without throwing, for anything
        /// malformed: wrong id, bad count, wrong length, non-zero padding, a zero tick delta or tick underflow,
        /// or more frames than the destination holds (R-2.5-04).
        /// </summary>
        public static bool TryRead(ReadOnlySpan<byte> source, Span<InputFrame> framesNewestFirst, out int frameCount)
        {
            frameCount = 0;
            var reader = new BitReader(source);
            if (reader.ReadBits(HotMessage.IdBits) != (uint)HotMessageId.Input)
                return false;
            int count = (int)reader.ReadBits(FrameCountBits);
            if (reader.Failed || count < 1 || count > MaxFrames || count > framesNewestFirst.Length || source.Length != SizeFor(count))
                return false;

            ushort sequence = (ushort)reader.ReadBits(SequenceBits);
            uint tick = reader.ReadBits(ClientTickBits);
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    uint tickDelta = reader.ReadBits(TickDeltaBits);
                    if (tickDelta == 0 || tickDelta > tick)
                        return false;
                    sequence--;
                    tick -= tickDelta;
                }
                framesNewestFirst[i] = ReadBody(ref reader, sequence, tick);
            }

            if (!reader.IsAtCleanEnd())
                return false;
            frameCount = count;
            return true;
        }

        private static void WriteBody(ref BitWriter writer, in InputFrame frame)
        {
            if (frame.SubTick >= 1 << SubTickBits)
                throw new ArgumentOutOfRangeException(nameof(frame), "SubTick is in 1/64 of a tick (0 to 63).");
            if (frame.SkillSlot > MaxSkillSlot)
                throw new ArgumentOutOfRangeException(nameof(frame), $"SkillSlot is 0 to {MaxSkillSlot}.");
            writer.WriteBits(frame.SubTick, SubTickBits);
            writer.WriteBits((uint)frame.Keys, KeysBits);
            writer.WriteBits(Quantize.AngleToWire(frame.Facing, FacingBits), FacingBits);
            writer.WriteBits((uint)frame.Actions, ActionsBits);
            writer.WriteBits(frame.SkillSlot, SkillSlotBits);
        }

        private static InputFrame ReadBody(ref BitReader reader, ushort sequence, uint tick)
        {
            InputFrame frame;
            frame.Sequence = sequence;
            frame.ClientTick = tick;
            frame.SubTick = (byte)reader.ReadBits(SubTickBits);
            frame.Keys = (MoveKeys)reader.ReadBits(KeysBits);
            frame.Facing = Quantize.AngleFromWire(reader.ReadBits(FacingBits), FacingBits);
            frame.Actions = (InputActions)reader.ReadBits(ActionsBits);
            frame.SkillSlot = (byte)reader.ReadBits(SkillSlotBits);
            return frame;
        }
    }

    /// <summary>Comparison of wrapping 16-bit sequences.</summary>
    public static class Sequence
    {
        /// <summary>True when <paramref name="a"/> is newer than <paramref name="b"/> within half the sequence space.</summary>
        public static bool IsNewer(ushort a, ushort b) => a != b && (ushort)(a - b) < 0x8000;
    }
}
