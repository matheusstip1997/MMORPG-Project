namespace Shared.Simulation
{
    /// <summary>
    /// The lock aim of M3 decision 16 (R-2.2-06, spec 3.1): under a target lock an action that does not move and is not a
    /// ground skill aims at the locked enemy. Server phase 3 and client prediction both turn the target's position on the
    /// player's view clock into the aim with <see cref="Toward"/> from the position after the frame's walk, so they agree
    /// whenever they agree on the two positions. Integer only.
    /// </summary>
    public static class LockAim
    {
        /// <summary>Resolution of the view clock: <c>InputFrame.SubTick</c> is in 1/64 of a tick.</summary>
        public const int SubTicks = 64;

        /// <summary>
        /// The binary angle from <paramref name="from"/> to <paramref name="target"/> (<see cref="FixedTrig.AngleOf"/>);
        /// false when both are the same point (no direction: the press aims at the mouse).
        /// </summary>
        public static bool Toward(FixVec2 from, FixVec2 target, out ushort aim)
        {
            long dx = (long)target.X - from.X;
            long dy = (long)target.Y - from.Y;
            aim = 0;
            if (dx == 0 && dy == 0)
                return false;
            aim = FixedTrig.AngleOf(dx, dy);
            return true;
        }

        /// <summary>
        /// The point <paramref name="sub64"/>/64 of the way from <paramref name="a"/> (a tick's position) to <paramref name="b"/>
        /// (the next tick's), each axis rounded toward <paramref name="a"/>: where the view clock's fraction sees an entity
        /// between two recorded ticks (R-3.1-03, R-3.1-04 sub-tick timestamps).
        /// </summary>
        public static FixVec2 Lerp64(FixVec2 a, FixVec2 b, int sub64)
        {
            if (sub64 <= 0)
                return a;
            if (sub64 >= SubTicks)
                return b;
            return new FixVec2(a.X + (int)(((long)b.X - a.X) * sub64 / SubTicks), a.Y + (int)(((long)b.Y - a.Y) * sub64 / SubTicks));
        }
    }
}
