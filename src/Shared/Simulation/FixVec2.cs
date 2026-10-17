using System;

namespace Shared.Simulation
{
    /// <summary>
    /// A ground-plane position or offset in Q24.8 centimeters (see <see cref="FixedPoint"/>).
    /// +Y is "W / screen-up", +X is "D / screen-right"; the client maps this plane to Unity world axes.
    /// </summary>
    public readonly struct FixVec2 : IEquatable<FixVec2>
    {
        public static readonly FixVec2 Zero = default;

        public FixVec2(int rawX, int rawY)
        {
            X = rawX;
            Y = rawY;
        }

        /// <summary>Raw Q24.8 X.</summary>
        public int X { get; }

        /// <summary>Raw Q24.8 Y.</summary>
        public int Y { get; }

        public static FixVec2 FromCm(int xCm, int yCm) => new FixVec2(FixedPoint.FromCm(xCm), FixedPoint.FromCm(yCm));

        /// <summary>Squared length in raw units squared.</summary>
        public long LengthSquared => (long)X * X + (long)Y * Y;

        public static FixVec2 operator +(FixVec2 a, FixVec2 b) => new FixVec2(a.X + b.X, a.Y + b.Y);
        public static FixVec2 operator -(FixVec2 a, FixVec2 b) => new FixVec2(a.X - b.X, a.Y - b.Y);
        public static bool operator ==(FixVec2 a, FixVec2 b) => a.Equals(b);
        public static bool operator !=(FixVec2 a, FixVec2 b) => !a.Equals(b);

        public bool Equals(FixVec2 other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is FixVec2 other && Equals(other);
        public override int GetHashCode() => unchecked(X * 397 ^ Y);
        public override string ToString() => $"({X}, {Y}) raw";
    }
}
