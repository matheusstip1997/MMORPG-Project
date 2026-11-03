#nullable enable
using Shared.Protocol;

namespace Game.Gameplay.Input
{
    /// <summary>
    /// R-2.2-05: WASD is screen-relative (W = screen-up) while the camera can turn around the character. The wire and
    /// <c>MovementSim</c> move in world axes, and the four key bits can name 8 directions, so the camera yaw is used in
    /// 45-degree steps (the camera settles on one when the turn keys are released, see <c>CameraYaw</c>) and the held
    /// keys are rotated by that step before they are sent. At a whole step the mapping is exact.
    /// </summary>
    public static class ScreenRelativeKeys
    {
        public const int Steps = Shared.Simulation.MoveSteering.Directions;

        /// <param name="screenKeys">Keys as held, relative to the screen.</param>
        /// <param name="cameraStep">Camera yaw in 45-degree steps clockwise from world +Y, any integer.</param>
        /// <returns>World keys for the wire. Opposite keys cancel first; no direction gives <see cref="MoveKeys.None"/>.</returns>
        /// <remarks>The direction table is the simulation's (<c>MoveSteering</c>: 0 W, 1 W+D, ... 7 W+A); wire and simulation
        /// keys share their bits.</remarks>
        public static MoveKeys ToWorld(MoveKeys screenKeys, int cameraStep)
        {
            int direction = Shared.Simulation.MoveSteering.DirectionOf((Shared.Simulation.MoveKeys)(byte)screenKeys);
            if (direction < 0)
                return MoveKeys.None;
            return (MoveKeys)(byte)Shared.Simulation.MoveSteering.KeysFor(direction + cameraStep);
        }
    }
}
