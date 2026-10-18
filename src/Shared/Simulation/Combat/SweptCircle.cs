using System;

namespace Shared.Simulation.Combat
{
    /// <summary>
    /// Swept collision for projectiles (spec 3.2): the segment a projectile moved this tick against a target circle, so a
    /// fast projectile cannot step over a target between two ticks. Pass the sum of the projectile's hit radius and the
    /// target's radius. Integer math only, no allocations (architecture rule 6).
    /// </summary>
    public static class SweptCircle
    {
        /// <summary>
        /// Largest step per axis and largest radius accepted (raw, 4096 cm): far more than a projectile moves in a tick, and
        /// small enough that every product below fits in <c>long</c>.
        /// </summary>
        public const int MaxRaw = 1 << 20;

        /// <summary>
        /// Whether the segment from <paramref name="start"/> to <paramref name="end"/> (raw Q24.8) passes within
        /// <paramref name="radiusRaw"/> of <paramref name="center"/>; touching counts. <paramref name="entryRaw"/> is how far
        /// from <paramref name="start"/> along the segment it first touches (0 when it starts inside), so hits on one
        /// segment can be ordered by it; it is 0 on a miss.
        /// </summary>
        public static bool Hits(FixVec2 start, FixVec2 end, FixVec2 center, int radiusRaw, out int entryRaw)
        {
            entryRaw = 0;
            long dx = (long)end.X - start.X;
            long dy = (long)end.Y - start.Y;
            if (dx > MaxRaw || dx < -MaxRaw || dy > MaxRaw || dy < -MaxRaw)
                throw new ArgumentOutOfRangeException(nameof(end), "the step is longer than SweptCircle.MaxRaw on an axis");
            if (radiusRaw < 0 || radiusRaw > MaxRaw)
                throw new ArgumentOutOfRangeException(nameof(radiusRaw));

            long cx = (long)center.X - start.X;
            long cy = (long)center.Y - start.Y;
            long radius = radiusRaw;
            // Outside the segment's bounds grown by the radius: a miss, and below it |cx|, |cy| <= 2^21.
            if (cx > Math.Max(dx, 0) + radius || cx < Math.Min(dx, 0) - radius
                || cy > Math.Max(dy, 0) + radius || cy < Math.Min(dy, 0) - radius)
                return false;

            long radiusSquared = radius * radius;
            if (cx * cx + cy * cy <= radiusSquared)
                return true; // starts inside (or a segment of length 0 inside)

            long dot = cx * dx + cy * dy;      // |dot| <= 2^42
            long lengthSquared = dx * dx + dy * dy;
            if (dot <= 0 || lengthSquared == 0)
                return false; // the start is the nearest point, and it is outside

            if (dot >= lengthSquared)
            {
                // The end is the nearest point of the segment.
                long ex = cx - dx;
                long ey = cy - dy;
                if (ex * ex + ey * ey > radiusSquared)
                    return false;
            }

            // Nearest point of the line (|dx * dot| <= 2^62), then how far back along the segment the circle is entered.
            long px = dx * dot / lengthSquared;
            long py = dy * dot / lengthSquared;
            long ox = cx - px;
            long oy = cy - py;
            long perpendicularSquared = ox * ox + oy * oy;
            if (perpendicularSquared > radiusSquared)
                return false;

            long length = Sqrt(lengthSquared);
            long along = dot / length;
            long entry = along - Sqrt(radiusSquared - perpendicularSquared);
            entryRaw = (int)(entry < 0 ? 0 : entry > length ? length : entry);
            return true;
        }

        /// <summary>Floor of the square root of a non-negative value, bit by bit.</summary>
        internal static long Sqrt(long value)
        {
            ulong remainder = (ulong)value;
            ulong root = 0;
            ulong bit = 1UL << 62;
            while (bit > remainder)
                bit >>= 2;
            while (bit != 0)
            {
                if (remainder >= root + bit)
                {
                    remainder -= root + bit;
                    root = (root >> 1) + bit;
                }
                else
                {
                    root >>= 1;
                }
                bit >>= 2;
            }
            return (long)root;
        }
    }
}
