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
        public const int Steps = 8;

        // Direction index clockwise from world +Y (W): 0 N, 1 NE, 2 E, 3 SE, 4 S, 5 SW, 6 W, 7 NW.
        private static readonly MoveKeys[] KeysForDirection =
        {
            MoveKeys.W, MoveKeys.W | MoveKeys.D, MoveKeys.D, MoveKeys.S | MoveKeys.D,
            MoveKeys.S, MoveKeys.S | MoveKeys.A, MoveKeys.A, MoveKeys.W | MoveKeys.A,
        };

        /// <param name="screenKeys">Keys as held, relative to the screen.</param>
        /// <param name="cameraStep">Camera yaw in 45-degree steps clockwise from world +Y, any integer.</param>
        /// <returns>World keys for the wire. Opposite keys cancel first; no direction gives <see cref="MoveKeys.None"/>.</returns>
        public static MoveKeys ToWorld(MoveKeys screenKeys, int cameraStep)
        {
            int direction = DirectionOf(screenKeys);
            if (direction < 0)
                return MoveKeys.None;
            int step = ((cameraStep % Steps) + Steps) % Steps;
            return KeysForDirection[(direction + step) % Steps];
        }

        private static int DirectionOf(MoveKeys keys)
        {
            int dx = ((keys & MoveKeys.D) != 0 ? 1 : 0) - ((keys & MoveKeys.A) != 0 ? 1 : 0);
            int dy = ((keys & MoveKeys.W) != 0 ? 1 : 0) - ((keys & MoveKeys.S) != 0 ? 1 : 0);
            if (dx == 0 && dy == 0)
                return -1;
            if (dx == 0)
                return dy > 0 ? 0 : 4;
            if (dy == 0)
                return dx > 0 ? 2 : 6;
            if (dy > 0)
                return dx > 0 ? 1 : 7;
            return dx > 0 ? 3 : 5;
        }
    }
}
