namespace Shared.Simulation
{
    /// <summary>
    /// Mouse-relative movement (R-2.2-01, M3 decision 14 as revised after play): each key direction is an offset from the
    /// facing (W 0, W+D 45 degrees, D 90, S+D 135, S 180, S+A 225, A 270, W+A 315; opposite keys cancel out), so W runs
    /// exactly at the mouse at any angle, S exactly away, D and A strafe to its right and left. Integer binary angles
    /// (65536 = one turn, 0 = +Y, clockwise), pure: <see cref="MovementSim"/>, the dodge (<c>ActionMachine.DashHeading</c>),
    /// the bots and the QA run read the same numbers.
    /// </summary>
    public static class MoveSteering
    {
        /// <summary>Key directions: W, A, S, D and their pairs.</summary>
        public const int Directions = 8;

        /// <summary>45 degrees as a binary angle: the offset between neighbouring key directions.</summary>
        public const int DirectionAngle = 65536 / Directions;

        private const int DirectionShift = 13; // 65536 / 8 = 1 << 13

        // Direction index clockwise from the facing: 0 W, 1 W+D, 2 D, 3 S+D, 4 S, 5 S+A, 6 A, 7 W+A.
        private static readonly MoveKeys[] KeysForDirection =
        {
            MoveKeys.W, MoveKeys.W | MoveKeys.D, MoveKeys.D, MoveKeys.S | MoveKeys.D,
            MoveKeys.S, MoveKeys.S | MoveKeys.A, MoveKeys.A, MoveKeys.W | MoveKeys.A,
        };

        /// <summary>The direction index (0..7) of the held keys; -1 when none are held or opposite keys cancel out.</summary>
        public static int DirectionOf(MoveKeys keys)
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

        /// <summary>The keys of direction index <paramref name="direction"/> (any integer, taken modulo 8).</summary>
        public static MoveKeys KeysFor(int direction) => KeysForDirection[((direction % Directions) + Directions) % Directions];

        /// <summary>The offset of the held keys from the facing (W 0, D a quarter turn clockwise); false when no direction is held.</summary>
        public static bool TryOffset(MoveKeys keys, out ushort offset)
        {
            int direction = DirectionOf(keys);
            offset = 0;
            if (direction < 0)
                return false;
            offset = (ushort)(direction * DirectionAngle);
            return true;
        }

        /// <summary>The heading the held keys move along at <paramref name="facing"/>: the facing plus their offset; false when none.</summary>
        public static bool TryHeading(MoveKeys keys, ushort facing, out ushort heading)
        {
            bool any = TryOffset(keys, out ushort offset);
            heading = unchecked((ushort)(facing + offset));
            return any;
        }

        /// <summary>
        /// The inverse of the step: the keys whose heading at <paramref name="facing"/> is nearest <paramref name="wanted"/> (the
        /// offset nearest <c>wanted - facing</c>; an exact half rounds clockwise). With <paramref name="wanted"/> equal to the
        /// facing that is W, straight at it; otherwise within 22.5 degrees.
        /// </summary>
        public static MoveKeys KeysToward(ushort wanted, ushort facing) =>
            KeysForDirection[((unchecked((ushort)(wanted - facing)) + DirectionAngle / 2) >> DirectionShift) & (Directions - 1)];
    }
}
