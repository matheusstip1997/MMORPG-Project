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
    /// The part of one tick's input that <see cref="MovementSim"/> reads, plus the mouse aim the action step reads.
    /// The wire input frame lives in <c>Shared/Protocol</c>; the server's input-apply phase and the client's
    /// prediction map it to this struct (<c>Shared.Protocol.MovementInputMapping</c>), so the simulation does not depend on
    /// the wire format. M3 decision 16: the walk never depends on a target lock, so the frame carries no facing.
    /// </summary>
    public readonly struct MovementInput : IEquatable<MovementInput>
    {
        public MovementInput(MoveKeys keys, ushort aim)
        {
            Keys = keys;
            Aim = aim;
        }

        /// <summary>World key bits (the client maps the screen keys by the camera step, R-2.2-05).</summary>
        public MoveKeys Keys { get; }

        /// <summary>
        /// The mouse aim (M3 decisions 15 and 16), a 16-bit binary angle (65536 = 360 degrees, 0 = +Y, clockwise): where a
        /// ground skill aims, and any other action that does not move when no lock aims it (spec 3.1).
        /// </summary>
        public ushort Aim { get; }

        public bool Equals(MovementInput other) => Keys == other.Keys && Aim == other.Aim;
        public override bool Equals(object? obj) => obj is MovementInput other && Equals(other);
        public override int GetHashCode() => ((int)Keys << 16) | Aim;
    }
}
