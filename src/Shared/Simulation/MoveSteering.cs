namespace Shared.Simulation
{
    /// <summary>
    /// The facing and the move direction of one frame (R-2.2-01, R-2.2-06, M3 decision 15). Without a target lock the keys
    /// are world directions (the client maps the screen keys by the camera step, R-2.2-05) and the character faces the way
    /// it walks, keeping its facing while it stands. With a lock the facing is the frame's (toward the locked target) and
    /// each key is an offset from it (W 0, W+D 45 degrees, D 90, S+D 135, S 180, S+A 225, A 270, W+A 315): W toward the
    /// target, S away, A and D around it. Opposite keys cancel out. Integer binary angles (65536 = one turn, 0 = +Y,
    /// clockwise), pure and stateless: <see cref="MovementSim"/>, the dodge (<c>ActionMachine.DashHeading</c>) and the
    /// bots read the same numbers.
    /// </summary>
    public static class MoveSteering
    {
        /// <summary>Key directions: W, A, S, D and their pairs.</summary>
        public const int Directions = 8;

        /// <summary>45 degrees as a binary angle: the offset between neighbouring key directions.</summary>
        public const int DirectionAngle = 65536 / Directions;

        // Direction index clockwise from +Y (unlocked) or from the facing (locked): 0 W, 1 W+D, 2 D, 3 S+D, 4 S, 5 S+A,
        // 6 A, 7 W+A.
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

        /// <summary>
        /// The angle of the held keys (W 0, D a quarter turn clockwise): their world heading without a lock, their offset from
        /// the facing with one; false when no direction is held.
        /// </summary>
        public static bool TryOffset(MoveKeys keys, out ushort offset)
        {
            int direction = DirectionOf(keys);
            offset = 0;
            if (direction < 0)
                return false;
            offset = (ushort)(direction * DirectionAngle);
            return true;
        }

        /// <summary>
        /// M3 decision 15: the facing a frame leaves the character with when it is free to move. With a lock,
        /// <paramref name="requested"/> (the frame's facing, toward the target); without one, the heading of the held keys,
        /// or <paramref name="previous"/> when none is held (so it keeps the last walk's or the last action's facing).
        /// </summary>
        public static ushort FacingFor(MoveKeys keys, bool locked, ushort requested, ushort previous)
        {
            if (locked)
                return requested;
            return TryOffset(keys, out ushort heading) ? heading : previous;
        }

        /// <summary>
        /// The heading the held keys move along: their world heading without a lock (the world axes), the facing plus their
        /// offset with one; false when no direction is held.
        /// </summary>
        public static bool TryMoveHeading(MoveKeys keys, bool locked, ushort facing, out ushort heading)
        {
            bool any = TryOffset(keys, out ushort offset);
            heading = locked ? unchecked((ushort)(facing + offset)) : offset;
            return any;
        }
    }
}
