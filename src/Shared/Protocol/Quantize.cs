using System;

namespace Shared.Protocol
{
    /// <summary>
    /// Integer quantization for hot messages (spec 1.7, 2.3, 11.2). Spec 1.5 places Quantize under Util;
    /// it lives next to the wire formats until the Util folder settles.
    /// Angles are binary angles: a ushort where 65536 is one full turn.
    /// </summary>
    public static class Quantize
    {
        /// <summary>Rounds a binary angle to <paramref name="bitCount"/> bits (1 to 16), wrapping a full turn to 0.</summary>
        public static uint AngleToWire(ushort angle, int bitCount)
        {
            CheckAngleBits(bitCount);
            if (bitCount == 16)
                return angle;
            int shift = 16 - bitCount;
            uint rounded = ((uint)angle + (1u << (shift - 1))) >> shift;
            return rounded & ((1u << bitCount) - 1);
        }

        public static ushort AngleFromWire(uint wire, int bitCount)
        {
            CheckAngleBits(bitCount);
            if (wire >> bitCount != 0)
                throw new ArgumentOutOfRangeException(nameof(wire));
            return (ushort)(wire << (16 - bitCount));
        }

        /// <summary>The angle the receiver will see. The client predicts with this so it matches the server (spec 2.2).</summary>
        public static ushort RoundTripAngle(ushort angle, int bitCount) => AngleFromWire(AngleToWire(angle, bitCount), bitCount);

        /// <summary>
        /// Position relative to an origin (spec 2.3: large coordinates are sent relative to an origin), in integer
        /// centimeters. False when the offset does not fit in <paramref name="bitCount"/> signed bits.
        /// </summary>
        public static bool TryOffset(int positionCm, int originCm, int bitCount, out int offsetCm)
        {
            if (bitCount < 2 || bitCount > 32)
                throw new ArgumentOutOfRangeException(nameof(bitCount));
            long offset = (long)positionCm - originCm;
            if (offset < BitMath.SignedMin(bitCount) || offset > BitMath.SignedMax(bitCount))
            {
                offsetCm = 0;
                return false;
            }
            offsetCm = (int)offset;
            return true;
        }

        /// <summary>
        /// HP bucket for other entities' bars (spec 9.4). 0 only when dead, <paramref name="topBucket"/> only when full,
        /// so a damaged entity never looks full and a living one never looks empty.
        /// </summary>
        public static int Bucket(int current, int max, int topBucket)
        {
            if (topBucket < 2)
                throw new ArgumentOutOfRangeException(nameof(topBucket));
            if (current <= 0 || max <= 0)
                return 0;
            if (current >= max)
                return topBucket;
            return 1 + (int)((long)current * (topBucket - 1) / max);
        }

        private static void CheckAngleBits(int bitCount)
        {
            if (bitCount < 1 || bitCount > 16)
                throw new ArgumentOutOfRangeException(nameof(bitCount));
        }
    }
}
