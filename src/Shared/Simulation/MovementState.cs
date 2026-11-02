using System;

namespace Shared.Simulation
{
    /// <summary>Position and facing of one entity, advanced by <see cref="MovementSim"/>.</summary>
    public readonly struct MovementState : IEquatable<MovementState>
    {
        public MovementState(FixVec2 position, ushort facing)
            : this(position, facing, 0)
        {
        }

        public MovementState(FixVec2 position, ushort facing, byte keySector)
        {
            Position = position;
            Facing = facing;
            KeySector = keySector;
        }

        public FixVec2 Position { get; }

        /// <summary>16-bit binary angle: 65536 = 360 degrees, 0 = +Y, clockwise.</summary>
        public ushort Facing { get; }

        /// <summary>
        /// The compass sector the move keys were turned from on the last applied frame plus one (1 = north ... 8 =
        /// north-west), 0 for none yet (R-2.2-01, M3 decision 14; <see cref="MoveSteering.Sector"/>). Its hysteresis needs the
        /// previous sector, so the server and the prediction carry it here; it is never sent.
        /// </summary>
        public byte KeySector { get; }

        /// <summary>The same state at another position (a dash, a collision, a teleport): the facing and key sector stay.</summary>
        public MovementState WithPosition(FixVec2 position) => new MovementState(position, Facing, KeySector);

        /// <summary>The same state with another facing (an action's locked facing): the key sector stays.</summary>
        public MovementState WithFacing(ushort facing) => new MovementState(Position, facing, KeySector);

        /// <summary>The same position and facing with <paramref name="keySector"/> (a placement that keeps the player's own sector).</summary>
        public MovementState WithKeySector(byte keySector) => new MovementState(Position, Facing, keySector);

        public bool Equals(MovementState other) =>
            Position == other.Position && Facing == other.Facing && KeySector == other.KeySector;
        public override bool Equals(object? obj) => obj is MovementState other && Equals(other);
        public override int GetHashCode() => unchecked((Position.GetHashCode() * 397 ^ Facing) * 31 + KeySector);
        public override string ToString() => $"{Position} facing {Facing} sector {KeySector}";
    }
}
