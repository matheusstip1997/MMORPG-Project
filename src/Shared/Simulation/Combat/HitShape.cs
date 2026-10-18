using System;
using Shared.Data.Tables;

namespace Shared.Simulation.Combat
{
    /// <summary>
    /// A hit shape in simulation units (R-3.1-03, spec 3.1 <c>HitShapeDef</c>): a cone, box or capsule placed relative to the
    /// caster's position and locked facing, tested against target circles. Integer math only, no allocations, so the
    /// server and client prediction agree bit for bit (architecture rule 6).
    /// <para>
    /// The target's offset from the caster is turned into the caster's frame with <see cref="FixedTrig"/> (forward along the
    /// facing, side to its right) and the shape origin's offset is subtracted; every kind is symmetric across its axis, so
    /// only the side distance's size matters. Touching counts as a hit. The cone's half angle rounds to the facing resolution
    /// (1024 steps per turn).
    /// </para>
    /// </summary>
    public readonly struct HitShape
    {
        /// <summary>
        /// Targets farther than this from the shape origin on either axis (raw, 65 536 cm) are rejected before squaring, so the
        /// squares fit in <c>long</c> wherever the two entities are. It is far beyond every shape the table allows.
        /// </summary>
        private const long FarRaw = 1L << 24;

        /// <summary>Largest target radius <see cref="Overlaps"/> accepts (raw, 5000 cm).</summary>
        public const int MaxTargetRadiusRaw = 5_000 * FixedPoint.RawPerCm;

        private const int QuarterTurn = 16_384;
        private const int FacingSteps = 1024;

        private readonly int _forwardRaw;
        private readonly int _sideRaw;
        private readonly int _reachRaw;      // cone range, box or capsule length
        private readonly int _breadthRaw;    // box half width, capsule radius
        private readonly int _coneSin;       // Q16 sine and cosine of the cone's half angle
        private readonly int _coneCos;

        private HitShape(HitShapeKind kind, int forwardOffsetCm, int sideOffsetCm, int reachCm, int breadthRaw, int halfAngleDeg)
        {
            Kind = kind;
            _forwardRaw = FixedPoint.FromCm(forwardOffsetCm);
            _sideRaw = FixedPoint.FromCm(sideOffsetCm);
            _reachRaw = FixedPoint.FromCm(reachCm);
            _breadthRaw = breadthRaw;
            ushort halfAngle = HalfAngleToBinary(halfAngleDeg);
            _coneSin = FixedTrig.Sin(halfAngle);
            _coneCos = FixedTrig.Cos(halfAngle);
        }

        public HitShapeKind Kind { get; }

        /// <summary>A circular sector with its apex at the origin, its axis forward, 1 to 180 degrees each side.</summary>
        public static HitShape Cone(int forwardOffsetCm, int sideOffsetCm, int rangeCm, int halfAngleDeg)
        {
            if (rangeCm <= 0) throw new ArgumentOutOfRangeException(nameof(rangeCm));
            if (halfAngleDeg <= 0 || halfAngleDeg > 180) throw new ArgumentOutOfRangeException(nameof(halfAngleDeg));
            return new HitShape(HitShapeKind.Cone, forwardOffsetCm, sideOffsetCm, rangeCm, 0, halfAngleDeg);
        }

        /// <summary>A rectangle from the origin <paramref name="lengthCm"/> forward, <paramref name="widthCm"/> across, centered on the axis.</summary>
        public static HitShape Box(int forwardOffsetCm, int sideOffsetCm, int lengthCm, int widthCm)
        {
            if (lengthCm <= 0) throw new ArgumentOutOfRangeException(nameof(lengthCm));
            if (widthCm <= 0) throw new ArgumentOutOfRangeException(nameof(widthCm));
            // Half of a whole number of cm is a whole number of raw units (256 per cm).
            return new HitShape(HitShapeKind.Box, forwardOffsetCm, sideOffsetCm, lengthCm, FixedPoint.FromCm(widthCm) / 2, 0);
        }

        /// <summary>Every point within <paramref name="radiusCm"/> of the segment from the origin <paramref name="lengthCm"/> forward (0: a circle).</summary>
        public static HitShape Capsule(int forwardOffsetCm, int sideOffsetCm, int lengthCm, int radiusCm)
        {
            if (lengthCm < 0) throw new ArgumentOutOfRangeException(nameof(lengthCm));
            if (radiusCm <= 0) throw new ArgumentOutOfRangeException(nameof(radiusCm));
            return new HitShape(HitShapeKind.Capsule, forwardOffsetCm, sideOffsetCm, lengthCm, FixedPoint.FromCm(radiusCm), 0);
        }

        /// <summary>The shape of a validated <see cref="HitShapeDef"/> row.</summary>
        public static HitShape From(HitShapeDef row)
        {
            switch (row.Kind)
            {
                case HitShapeKind.Cone: return Cone(row.ForwardOffsetCm, row.SideOffsetCm, row.RangeCm, row.HalfAngleDeg);
                case HitShapeKind.Box: return Box(row.ForwardOffsetCm, row.SideOffsetCm, row.LengthCm, row.WidthCm);
                case HitShapeKind.Capsule: return Capsule(row.ForwardOffsetCm, row.SideOffsetCm, row.LengthCm, row.RadiusCm);
                default: throw new ArgumentException($"Unknown hit shape kind {row.Kind} in '{row.Id}'.", nameof(row));
            }
        }

        /// <summary>
        /// Whether the shape, cast from <paramref name="casterPosition"/> with the locked <paramref name="facing"/> (binary
        /// angle, 0 = +Y, clockwise), touches the target circle (center and radius in raw Q24.8 units).
        /// </summary>
        public bool Overlaps(FixVec2 casterPosition, ushort facing, FixVec2 targetPosition, int targetRadiusRaw)
        {
            if (targetRadiusRaw < 0 || targetRadiusRaw > MaxTargetRadiusRaw)
                throw new ArgumentOutOfRangeException(nameof(targetRadiusRaw));

            long dx = (long)targetPosition.X - casterPosition.X;
            long dy = (long)targetPosition.Y - casterPosition.Y;
            long sin = FixedTrig.Sin(facing);
            long cos = FixedTrig.Cos(facing);
            // The caster's frame: forward = (sin, cos), right = (cos, -sin) (FixedTrig.Along).
            long forward = ((dx * sin + dy * cos) >> 16) - _forwardRaw;
            long side = ((dx * cos - dy * sin) >> 16) - _sideRaw;
            if (side < 0)
                side = -side;
            if (forward > FarRaw || forward < -FarRaw || side > FarRaw)
                return false;

            long radius = targetRadiusRaw;
            switch (Kind)
            {
                case HitShapeKind.Cone: return ConeOverlaps(forward, side, radius);
                case HitShapeKind.Box: return BoxOverlaps(forward, side, radius);
                default: return CapsuleOverlaps(forward, side, radius);
            }
        }

        private bool ConeOverlaps(long forward, long side, long radius)
        {
            // Sign of the cross product of the edge direction (cos h, sin h) and the point: <= 0 inside the half angle.
            long cross = side * _coneCos - forward * _coneSin;
            if (cross <= 0)
            {
                // Inside the angle the nearest point of the sector is on the ray to the target, at most the range away.
                long reach = _reachRaw + radius;
                return forward * forward + side * side <= reach * reach;
            }
            // Outside it the nearest point is on the edge segment from the apex to the range (on the target's side).
            long along = (forward * _coneCos + side * _coneSin) >> 16;
            if (along <= 0)
                return forward * forward + side * side <= radius * radius; // the apex, measured without the rotation's rounding
            long perpendicular = cross >> 16;
            long beyond = along > _reachRaw ? along - _reachRaw : 0;
            return beyond * beyond + perpendicular * perpendicular <= radius * radius;
        }

        private bool BoxOverlaps(long forward, long side, long radius)
        {
            long outForward = forward - Clamp(forward, 0, _reachRaw);
            long outSide = side > _breadthRaw ? side - _breadthRaw : 0;
            return outForward * outForward + outSide * outSide <= radius * radius;
        }

        private bool CapsuleOverlaps(long forward, long side, long radius)
        {
            long outForward = forward - Clamp(forward, 0, _reachRaw);
            long reach = _breadthRaw + radius;
            return outForward * outForward + side * side <= reach * reach;
        }

        private static long Clamp(long value, long min, long max) => value < min ? min : value > max ? max : value;

        /// <summary>Degrees to the nearest facing step (1024 per turn) as a binary angle; 180 is exactly half a turn.</summary>
        private static ushort HalfAngleToBinary(int degrees)
        {
            int steps = (degrees * FacingSteps + 180) / 360;
            return (ushort)(steps * (QuarterTurn * 4 / FacingSteps));
        }
    }
}
