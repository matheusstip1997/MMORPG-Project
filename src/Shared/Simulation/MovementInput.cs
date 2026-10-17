using System;

namespace Shared.Simulation
{
    /// <summary>Movement keys held during a tick (R-2.2-01).</summary>
    [Flags]
    public enum MoveKeys : byte
    {
        None = 0,
        W = 1 << 0,
        A = 1 << 1,
        S = 1 << 2,
        D = 1 << 3,
    }

    /// <summary>
    /// The part of one tick's input that <see cref="MovementSim"/> reads.
    /// The wire input frame lives in <c>Shared/Protocol</c>; the server's input-apply phase and the client's
    /// prediction map it to this struct (keys as they are, facing via <c>Shared.Protocol.Quantize.AngleFromWire</c>),
    /// so the simulation does not depend on the wire format.
    /// </summary>
    public readonly struct MovementInput : IEquatable<MovementInput>
    {
        public MovementInput(MoveKeys keys, ushort facing)
        {
            Keys = keys;
            Facing = facing;
        }

        public MoveKeys Keys { get; }

        /// <summary>16-bit binary angle: 65536 = 360 degrees, 0 = +Y, clockwise.</summary>
        public ushort Facing { get; }

        public bool Equals(MovementInput other) => Keys == other.Keys && Facing == other.Facing;
        public override bool Equals(object? obj) => obj is MovementInput other && Equals(other);
        public override int GetHashCode() => ((int)Keys << 16) | Facing;
    }
}
