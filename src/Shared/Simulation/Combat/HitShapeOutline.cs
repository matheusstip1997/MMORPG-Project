using System;
using Shared.Data.Tables;

namespace Shared.Simulation.Combat
{
    /// <summary>
    /// The outline of a <see cref="HitShapeDef"/> placed like <see cref="HitShape.Overlaps"/> places it, for debug drawing
    /// (backlog J hitbox gizmos, spec 11.7): the same frame (forward along the locked facing, side to its right), the same
    /// <see cref="FixedTrig"/> rotation, origin offsets and cone half angle rounding, so what is drawn is the region the
    /// server tests with a target radius of 0. Arcs are chords: a point of the region can be up to
    /// <c>r (1 - cos(pi / segmentsPerTurn))</c> outside the drawn outline. A closed polygon in raw Q24.8 world units;
    /// integer math, no allocations.
    /// </summary>
    public static class HitShapeOutline
    {
        private const int Turn = 65_536;
        private const int HalfTurn = Turn / 2;
        private const int QuarterTurn = Turn / 4;

        /// <summary>Points <see cref="Write"/> may need for <paramref name="segmentsPerTurn"/>.</summary>
        public static int MaxPoints(int segmentsPerTurn) => segmentsPerTurn + 4;

        /// <summary>
        /// Writes the closed outline of <paramref name="row"/> cast from <paramref name="caster"/> with the locked
        /// <paramref name="facing"/> into <paramref name="points"/> (the last point joins the first) and returns the count.
        /// Arcs use <paramref name="segmentsPerTurn"/> chords per full turn (at least 8).
        /// </summary>
        public static int Write(HitShapeDef row, FixVec2 caster, ushort facing, int segmentsPerTurn, Span<FixVec2> points)
        {
            if (row == null)
                throw new ArgumentNullException(nameof(row));
            if (segmentsPerTurn < 8)
                throw new ArgumentOutOfRangeException(nameof(segmentsPerTurn), "at least 8");
            if (points.Length < MaxPoints(segmentsPerTurn))
                throw new ArgumentException($"needs room for {MaxPoints(segmentsPerTurn)} points", nameof(points));

            var frame = new Frame(caster, facing);
            long forward = FixedPoint.FromCm(row.ForwardOffsetCm);
            long side = FixedPoint.FromCm(row.SideOffsetCm);
            int count = 0;
            switch (row.Kind)
            {
                case HitShapeKind.Cone:
                {
                    long range = FixedPoint.FromCm(row.RangeCm);
                    int half = HitShape.HalfAngleToBinary(row.HalfAngleDeg);
                    bool fullCircle = half >= HalfTurn;
                    if (!fullCircle)
                        points[count++] = frame.ToWorld(forward, side);
                    // A full circle repeats its first point at the end, so it stops one chord short.
                    count = Arc(frame, forward, side, range, -half, 2 * half, segmentsPerTurn, !fullCircle, points, count);
                    break;
                }
                case HitShapeKind.Box:
                {
                    long length = FixedPoint.FromCm(row.LengthCm);
                    long halfWidth = FixedPoint.FromCm(row.WidthCm) / 2; // as HitShape.Box
                    points[count++] = frame.ToWorld(forward, side - halfWidth);
                    points[count++] = frame.ToWorld(forward + length, side - halfWidth);
                    points[count++] = frame.ToWorld(forward + length, side + halfWidth);
                    points[count++] = frame.ToWorld(forward, side + halfWidth);
                    break;
                }
                case HitShapeKind.Capsule:
                {
                    long length = FixedPoint.FromCm(row.LengthCm);
                    long radius = FixedPoint.FromCm(row.RadiusCm);
                    if (length == 0)
                        return Arc(frame, forward, side, radius, 0, Turn, segmentsPerTurn, false, points, 0);
                    // The far half circle (from the left side round the front to the right), then the near one.
                    count = Arc(frame, forward + length, side, radius, -QuarterTurn, HalfTurn, segmentsPerTurn, true, points, count);
                    count = Arc(frame, forward, side, radius, QuarterTurn, HalfTurn, segmentsPerTurn, true, points, count);
                    break;
                }
                default:
                    throw new ArgumentException($"Unknown hit shape kind {row.Kind} in '{row.Id}'.", nameof(row));
            }
            return count;
        }

        /// <summary>
        /// Points of the arc of <paramref name="radius"/> around (<paramref name="centerForward"/>, <paramref name="centerSide"/>)
        /// from <paramref name="start"/> through <paramref name="span"/> (binary angles from the forward axis toward the right).
        /// </summary>
        private static int Arc(in Frame frame, long centerForward, long centerSide, long radius, int start, int span,
            int segmentsPerTurn, bool includeEnd, Span<FixVec2> points, int count)
        {
            int chords = Math.Max(1, (int)(((long)span * segmentsPerTurn + Turn - 1) / Turn));
            int last = includeEnd ? chords : chords - 1;
            for (int i = 0; i <= last; i++)
            {
                var angle = unchecked((ushort)(start + (int)((long)span * i / chords)));
                long f = centerForward + ((radius * FixedTrig.Cos(angle)) >> 16);
                long s = centerSide + ((radius * FixedTrig.Sin(angle)) >> 16);
                points[count++] = frame.ToWorld(f, s);
            }
            return count;
        }

        /// <summary>The caster's frame of <see cref="HitShape.Overlaps"/>: forward = (sin, cos), right = (cos, -sin).</summary>
        private readonly struct Frame
        {
            private readonly FixVec2 _origin;
            private readonly long _sin;
            private readonly long _cos;

            public Frame(FixVec2 origin, ushort facing)
            {
                _origin = origin;
                _sin = FixedTrig.Sin(facing);
                _cos = FixedTrig.Cos(facing);
            }

            public FixVec2 ToWorld(long forward, long side) => new FixVec2(
                (int)(_origin.X + ((forward * _sin + side * _cos) >> 16)),
                (int)(_origin.Y + ((forward * _cos - side * _sin) >> 16)));
        }
    }
}
