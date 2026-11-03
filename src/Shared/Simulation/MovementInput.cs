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
        /// <summary>An unlocked frame aiming along <paramref name="facing"/> (tests, the monster AI's own steps).</summary>
        public MovementInput(MoveKeys keys, ushort facing)
            : this(keys, facing, facing, false)
        {
        }

        public MovementInput(MoveKeys keys, ushort facing, ushort aim, bool locked)
        {
            Keys = keys;
            Facing = facing;
            Aim = aim;
            Locked = locked;
        }

        /// <summary>World key bits (the client maps the screen keys by the camera step, R-2.2-05).</summary>
        public MoveKeys Keys { get; }

        /// <summary>
        /// 16-bit binary angle: 65536 = 360 degrees, 0 = +Y, clockwise. The requested facing: it counts only while
        /// <see cref="Locked"/> (M3 decision 15, <see cref="MoveSteering.FacingFor"/>).
        /// </summary>
        public ushort Facing { get; }

        /// <summary>The mouse aim (M3 decision 15): where an action that does not move aims (spec 3.1).</summary>
        public ushort Aim { get; }

        /// <summary>A target lock is on (R-2.2-06): the facing is <see cref="Facing"/> and the keys are offsets from it.</summary>
        public bool Locked { get; }

        public bool Equals(MovementInput other) =>
            Keys == other.Keys && Facing == other.Facing && Aim == other.Aim && Locked == other.Locked;
        public override bool Equals(object? obj) => obj is MovementInput other && Equals(other);
        public override int GetHashCode() => unchecked((((int)Keys << 16) | Facing) * 31 + Aim * 2 + (Locked ? 1 : 0));
    }
}
