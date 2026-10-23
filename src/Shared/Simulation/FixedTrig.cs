namespace Shared.Simulation
{
    /// <summary>
    /// Integer sine and cosine of 16-bit binary angles (65536 = one turn, 0 = +Y, clockwise), so directions in the
    /// simulation are bit-identical on every runtime (architecture rule 6). A committed quarter-wave table in Q16
    /// (65536 = 1.0) at 1024 steps per turn, the resolution of facing on the wire (<c>InputPacket.FacingBits</c>); finer
    /// angles round down to the step. A test checks the table against <see cref="System.Math.Sin(double)"/>.
    /// </summary>
    public static class FixedTrig
    {
        /// <summary>1.0 in the Q16 results.</summary>
        public const int One = 1 << 16;

        private const int StepShift = 6; // 16-bit angle to 1024 steps per turn
        private const int QuarterSteps = 256;

        // round(sin(i * 90 / 256 degrees) * 65536) for i = 0..256.
        private static readonly int[] QuarterSine =
        {
            0, 402, 804, 1206, 1608, 2010, 2412, 2814, 3216, 3617, 4019, 4420,
            4821, 5222, 5623, 6023, 6424, 6824, 7224, 7623, 8022, 8421, 8820, 9218,
            9616, 10014, 10411, 10808, 11204, 11600, 11996, 12391, 12785, 13180, 13573, 13966,
            14359, 14751, 15143, 15534, 15924, 16314, 16703, 17091, 17479, 17867, 18253, 18639,
            19024, 19409, 19792, 20175, 20557, 20939, 21320, 21699, 22078, 22457, 22834, 23210,
            23586, 23961, 24335, 24708, 25080, 25451, 25821, 26190, 26558, 26925, 27291, 27656,
            28020, 28383, 28745, 29106, 29466, 29824, 30182, 30538, 30893, 31248, 31600, 31952,
            32303, 32652, 33000, 33347, 33692, 34037, 34380, 34721, 35062, 35401, 35738, 36075,
            36410, 36744, 37076, 37407, 37736, 38064, 38391, 38716, 39040, 39362, 39683, 40002,
            40320, 40636, 40951, 41264, 41576, 41886, 42194, 42501, 42806, 43110, 43412, 43713,
            44011, 44308, 44604, 44898, 45190, 45480, 45769, 46056, 46341, 46624, 46906, 47186,
            47464, 47741, 48015, 48288, 48559, 48828, 49095, 49361, 49624, 49886, 50146, 50404,
            50660, 50914, 51166, 51417, 51665, 51911, 52156, 52398, 52639, 52878, 53114, 53349,
            53581, 53812, 54040, 54267, 54491, 54714, 54934, 55152, 55368, 55582, 55794, 56004,
            56212, 56418, 56621, 56823, 57022, 57219, 57414, 57607, 57798, 57986, 58172, 58356,
            58538, 58718, 58896, 59071, 59244, 59415, 59583, 59750, 59914, 60075, 60235, 60392,
            60547, 60700, 60851, 60999, 61145, 61288, 61429, 61568, 61705, 61839, 61971, 62101,
            62228, 62353, 62476, 62596, 62714, 62830, 62943, 63054, 63162, 63268, 63372, 63473,
            63572, 63668, 63763, 63854, 63944, 64031, 64115, 64197, 64277, 64354, 64429, 64501,
            64571, 64639, 64704, 64766, 64827, 64884, 64940, 64993, 65043, 65091, 65137, 65180,
            65220, 65259, 65294, 65328, 65358, 65387, 65413, 65436, 65457, 65476, 65492, 65505,
            65516, 65525, 65531, 65535, 65536,
        };

        /// <summary>Sine in Q16 of a binary angle.</summary>
        public static int Sin(ushort angle)
        {
            int step = angle >> StepShift;            // 0..1023
            int quadrant = step / QuarterSteps;
            int index = step % QuarterSteps;
            switch (quadrant)
            {
                case 0: return QuarterSine[index];
                case 1: return QuarterSine[QuarterSteps - index];
                case 2: return -QuarterSine[index];
                default: return -QuarterSine[QuarterSteps - index];
            }
        }

        /// <summary>Cosine in Q16 of a binary angle.</summary>
        public static int Cos(ushort angle) => Sin(unchecked((ushort)(angle + 16384)));

        /// <summary>
        /// The offset of <paramref name="distanceRaw"/> along <paramref name="facing"/> (0 = +Y, clockwise, so +X is a quarter
        /// turn): x = d sin, y = d cos, each rounded toward negative infinity.
        /// </summary>
        public static FixVec2 Along(ushort facing, int distanceRaw) =>
            new FixVec2((int)(((long)distanceRaw * Sin(facing)) >> 16), (int)(((long)distanceRaw * Cos(facing)) >> 16));

        /// <summary>
        /// The binary angle of the direction (<paramref name="dx"/>, <paramref name="dy"/>) (0 = +Y, clockwise), rounded down
        /// to the table's step (1024 per turn, the wire facing resolution), so <see cref="Along"/> of the result points the
        /// same way within one step. The zero vector gives 0. Integer only: a binary search on the quarter-sine table.
        /// </summary>
        public static ushort AngleOf(long dx, long dy)
        {
            if (dx == 0 && dy == 0)
                return 0;
            int quadrant;
            long a, b; // a = the side along the quadrant's turn, b = the side along its start axis (b > 0)
            if (dx >= 0 && dy > 0) { quadrant = 0; a = dx; b = dy; }
            else if (dx > 0) { quadrant = 1; a = -dy; b = dx; }
            else if (dy < 0) { quadrant = 2; a = -dx; b = -dy; }
            else { quadrant = 3; a = dy; b = -dx; }
            return (ushort)((quadrant * QuarterSteps + StepsInQuadrant(a, b)) << StepShift);
        }

        /// <summary>The largest step i in [0, 256) with tan(i) &lt;= a / b, for a &gt;= 0 and b &gt; 0.</summary>
        private static int StepsInQuadrant(long a, long b)
        {
            // Large components are scaled down first so the products fit in a long; the direction barely changes.
            while (a > int.MaxValue || b > int.MaxValue)
            {
                a >>= 1;
                b >>= 1;
            }
            if (b == 0)
                return QuarterSteps - 1;
            int low = 0, high = QuarterSteps - 1;
            while (low < high)
            {
                int mid = (low + high + 1) >> 1;
                // tan(mid) <= a / b  <=>  sin(mid) * b <= a * cos(mid), cos(mid) = sin(256 - mid).
                if (QuarterSine[mid] * b <= a * QuarterSine[QuarterSteps - mid])
                    low = mid;
                else
                    high = mid - 1;
            }
            return low;
        }
    }
}
