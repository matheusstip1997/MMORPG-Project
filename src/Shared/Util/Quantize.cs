using System;

namespace Shared.Util
{
    /// <summary>
    /// Conversions between simulation values and their quantized wire form.
    /// Facing in the simulation is a 16-bit binary angle (65536 = 360 degrees, 0 = +Y, clockwise);
    /// the input frame carries it in fewer bits (spec 1.7: 8-10 bits, chosen by the protocol).
    /// </summary>
    public static class Quantize
    {
        public const int FacingFullBits = 16;

        /// <summary>Rounds a 16-bit facing to the nearest of 2^bits steps; the full turn wraps to 0.</summary>
        public static int FacingToBits(ushort facing, int bits)
        {
            CheckBits(bits);
            if (bits == FacingFullBits)
                return facing;
            int shift = FacingFullBits - bits;
            int rounded = (facing + (1 << (shift - 1))) >> shift;
            return rounded & ((1 << bits) - 1);
        }

        /// <summary>Expands a <paramref name="bits"/>-bit facing back to the 16-bit simulation facing.</summary>
        public static ushort FacingFromBits(int value, int bits)
        {
            CheckBits(bits);
            if (value < 0 || value >= (1 << bits))
                throw new ArgumentOutOfRangeException(nameof(value), "does not fit in the given bits");
            return (ushort)(value << (FacingFullBits - bits));
        }

        private static void CheckBits(int bits)
        {
            if (bits < 1 || bits > FacingFullBits)
                throw new ArgumentOutOfRangeException(nameof(bits), "must be 1 to 16");
        }
    }
}
