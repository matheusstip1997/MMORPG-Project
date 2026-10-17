using System;

namespace Shared.Protocol
{
    /// <summary>
    /// Writes bit-packed hot messages (spec 11.2) into a caller-owned buffer, least significant bit first.
    /// Never allocates. Running out of space is not an exception: <see cref="Overflowed"/> becomes true and
    /// later writes are ignored, so a caller can roll back with <see cref="Rewind"/> and split the message.
    /// Values that do not fit their bit count are programming errors and throw.
    /// </summary>
    public ref struct BitWriter
    {
        private readonly Span<byte> _buffer;
        private readonly int _capacityBits;
        private int _bitPosition;
        private bool _overflowed;

        public BitWriter(Span<byte> buffer)
        {
            _buffer = buffer;
            _capacityBits = buffer.Length > int.MaxValue / 8 ? int.MaxValue : buffer.Length * 8;
            _bitPosition = 0;
            _overflowed = false;
        }

        public int BitPosition => _bitPosition;
        public int RemainingBits => _capacityBits - _bitPosition;
        public bool Overflowed => _overflowed;

        /// <summary>Bytes used so far, counting a partly written last byte.</summary>
        public int BytesWritten => (_bitPosition + 7) >> 3;

        /// <summary>Writes the low <paramref name="bitCount"/> bits (1 to 32) of <paramref name="value"/>.</summary>
        public void WriteBits(uint value, int bitCount)
        {
            CheckValue(value, bitCount);
            if (_overflowed || bitCount > RemainingBits)
            {
                _overflowed = true;
                return;
            }
            Put(_bitPosition, value, bitCount);
            _bitPosition += bitCount;
        }

        public void WriteBool(bool value) => WriteBits(value ? 1u : 0u, 1);

        /// <summary>Writes a two's complement value in <paramref name="bitCount"/> bits (2 to 32).</summary>
        public void WriteSigned(int value, int bitCount)
        {
            if (bitCount < 2 || bitCount > 32)
                throw new ArgumentOutOfRangeException(nameof(bitCount));
            if (value < BitMath.SignedMin(bitCount) || value > BitMath.SignedMax(bitCount))
                throw new ArgumentOutOfRangeException(nameof(value), $"{value} does not fit in {bitCount} signed bits.");
            WriteBits((uint)value & BitMath.Mask(bitCount), bitCount);
        }

        /// <summary>Overwrites bits that were already written, for example a count reserved before the items.</summary>
        public void PatchBits(int bitPosition, uint value, int bitCount)
        {
            CheckValue(value, bitCount);
            if (bitPosition < 0 || bitPosition + bitCount > _bitPosition)
                throw new ArgumentOutOfRangeException(nameof(bitPosition), "Only bits already written can be patched.");
            Put(bitPosition, value, bitCount);
        }

        /// <summary>Moves back to an earlier position and clears the overflow, dropping everything written after it.</summary>
        public void Rewind(int bitPosition)
        {
            if (bitPosition < 0 || bitPosition > _bitPosition)
                throw new ArgumentOutOfRangeException(nameof(bitPosition));
            _bitPosition = bitPosition;
            _overflowed = false;
        }

        /// <summary>Zeroes the padding bits of the last byte (output is deterministic) and returns the bytes used.</summary>
        public int Finish()
        {
            int usedBitsInLastByte = _bitPosition & 7;
            if (usedBitsInLastByte != 0)
                _buffer[_bitPosition >> 3] &= (byte)((1 << usedBitsInLastByte) - 1);
            return BytesWritten;
        }

        private void Put(int bitPosition, uint value, int bitCount)
        {
            int done = 0;
            while (done < bitCount)
            {
                int byteIndex = bitPosition >> 3;
                int bitOffset = bitPosition & 7;
                int take = Math.Min(8 - bitOffset, bitCount - done);
                uint mask = (1u << take) - 1;
                uint chunk = (value >> done) & mask;
                _buffer[byteIndex] = (byte)((_buffer[byteIndex] & ~(mask << bitOffset)) | (chunk << bitOffset));
                done += take;
                bitPosition += take;
            }
        }

        private static void CheckValue(uint value, int bitCount)
        {
            if (bitCount < 1 || bitCount > 32)
                throw new ArgumentOutOfRangeException(nameof(bitCount));
            if (bitCount < 32 && value >> bitCount != 0)
                throw new ArgumentOutOfRangeException(nameof(value), $"{value} does not fit in {bitCount} bits.");
        }
    }

    internal static class BitMath
    {
        public static uint Mask(int bitCount) => bitCount == 32 ? uint.MaxValue : (1u << bitCount) - 1;
        public static int SignedMin(int bitCount) => bitCount == 32 ? int.MinValue : -(1 << (bitCount - 1));
        public static int SignedMax(int bitCount) => bitCount == 32 ? int.MaxValue : (1 << (bitCount - 1)) - 1;
    }
}
