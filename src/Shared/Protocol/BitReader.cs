using System;

namespace Shared.Protocol
{
    /// <summary>
    /// Reads bit-packed hot messages written by <see cref="BitWriter"/>. Every read is bound-checked (R-2.5-04):
    /// reading past the end sets <see cref="Failed"/>, and from then on every read returns 0. Decoders check
    /// <see cref="Failed"/> before trusting a count or acting on the data. Never throws on malformed input.
    /// </summary>
    public ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private readonly int _lengthBits;
        private int _bitPosition;
        private bool _failed;

        public BitReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _lengthBits = buffer.Length > int.MaxValue / 8 ? int.MaxValue : buffer.Length * 8;
            _bitPosition = 0;
            _failed = false;
        }

        public int BitPosition => _bitPosition;
        public int RemainingBits => _lengthBits - _bitPosition;
        public bool Failed => _failed;

        /// <summary>Reads <paramref name="bitCount"/> bits (1 to 32).</summary>
        public uint ReadBits(int bitCount)
        {
            if (bitCount < 1 || bitCount > 32)
                throw new ArgumentOutOfRangeException(nameof(bitCount));
            if (_failed || bitCount > RemainingBits)
            {
                _failed = true;
                return 0;
            }

            uint result = 0;
            int done = 0;
            while (done < bitCount)
            {
                int byteIndex = _bitPosition >> 3;
                int bitOffset = _bitPosition & 7;
                int take = Math.Min(8 - bitOffset, bitCount - done);
                uint chunk = ((uint)_buffer[byteIndex] >> bitOffset) & ((1u << take) - 1);
                result |= chunk << done;
                done += take;
                _bitPosition += take;
            }
            return result;
        }

        public bool ReadBool() => ReadBits(1) != 0;

        /// <summary>Reads a two's complement value of <paramref name="bitCount"/> bits (2 to 32).</summary>
        public int ReadSigned(int bitCount)
        {
            if (bitCount < 2 || bitCount > 32)
                throw new ArgumentOutOfRangeException(nameof(bitCount));
            uint raw = ReadBits(bitCount);
            if (bitCount == 32)
                return (int)raw;
            int shift = 32 - bitCount;
            return ((int)(raw << shift)) >> shift;
        }

        /// <summary>Marks the message as invalid, for example after reading an unknown enum value.</summary>
        public void Fail() => _failed = true;

        /// <summary>
        /// True when nothing failed, the whole buffer was consumed, and the padding bits of the last byte are zero.
        /// Decoders call it last so trailing bytes are refused.
        /// </summary>
        public bool IsAtCleanEnd()
        {
            if (_failed || ((_bitPosition + 7) >> 3) != _buffer.Length)
                return false;
            int usedBitsInLastByte = _bitPosition & 7;
            return usedBitsInLastByte == 0 || _buffer[_buffer.Length - 1] >> usedBitsInLastByte == 0;
        }
    }
}
