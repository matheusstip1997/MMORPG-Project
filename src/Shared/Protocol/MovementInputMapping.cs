using Shared.Simulation;
using SimMoveKeys = Shared.Simulation.MoveKeys;

namespace Shared.Protocol
{
    /// <summary>
    /// Maps a wire <see cref="InputFrame"/> to the <see cref="MovementInput"/> that <see cref="MovementSim"/> reads
    /// (spec 2.2). Client prediction and the server's input-apply phase both go through here, so they step the
    /// simulation with the same values.
    /// </summary>
    public static class MovementInputMapping
    {
        /// <summary>
        /// The wire and the simulation use the same key bits (W 1, A 2, S 4, D 8). The aim is rounded to
        /// <see cref="InputPacket.AimBits"/> bits, the value the server decodes, so a client that predicts from the
        /// frame it is about to send uses exactly what the server will apply. Already-decoded frames are unchanged. The
        /// frame's lock flag is read as it is (<see cref="InputFrame.Locked"/>): the walk never depends on it (M3 decision 16).
        /// </summary>
        public static MovementInput FromFrame(in InputFrame frame) =>
            new MovementInput((SimMoveKeys)((byte)frame.Keys & 0x0F), Quantize.RoundTripAngle(frame.Aim, InputPacket.AimBits));
    }
}
