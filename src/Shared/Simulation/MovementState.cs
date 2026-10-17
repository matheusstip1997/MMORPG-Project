using System;

namespace Shared.Simulation
{
    /// <summary>Position and facing of one entity, advanced by <see cref="MovementSim"/>.</summary>
    public readonly struct MovementState : IEquatable<MovementState>
    {
        public MovementState(FixVec2 position, ushort facing)
        {
            Position = position;
            Facing = facing;
        }

        public FixVec2 Position { get; }

        /// <summary>16-bit binary angle: 65536 = 360 degrees, 0 = +Y, clockwise.</summary>
        public ushort Facing { get; }

        public bool Equals(MovementState other) => Position == other.Position && Facing == other.Facing;
        public override bool Equals(object? obj) => obj is MovementState other && Equals(other);
        public override int GetHashCode() => unchecked(Position.GetHashCode() * 397 ^ Facing);
        public override string ToString() => $"{Position} facing {Facing}";
    }
}
