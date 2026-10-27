using System;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Combat;

namespace Shared.Simulation
{
    /// <summary>The <see cref="BodyCollisionSettings"/> row in raw units, as the shared code uses it. Default: collision off.</summary>
    public readonly struct BodyCollisionRules
    {
        /// <summary>No body collision (no settings row: worlds and tests built without the table).</summary>
        public static readonly BodyCollisionRules Off = default;

        public BodyCollisionRules(BodyResponse walkResponse, BodyResponse dashResponse, int overlapToleranceCm,
            int maxSlideIterations)
        {
            if (overlapToleranceCm < 0)
                throw new ArgumentOutOfRangeException(nameof(overlapToleranceCm));
            if (maxSlideIterations <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSlideIterations));
            WalkResponse = walkResponse;
            DashResponse = dashResponse;
            OverlapToleranceRaw = FixedPoint.FromCm(overlapToleranceCm);
            MaxSlideIterations = maxSlideIterations;
        }

        public static BodyCollisionRules From(BodyCollisionSettings? settings) =>
            settings == null
                ? Off
                : new BodyCollisionRules(settings.WalkResponse, settings.DashResponse, settings.OverlapToleranceCm,
                    settings.MaxSlideIterations);

        public bool Enabled => MaxSlideIterations > 0;

        public BodyResponse WalkResponse { get; }

        public BodyResponse DashResponse { get; }

        public int OverlapToleranceRaw { get; }

        public int MaxSlideIterations { get; }
    }

    /// <summary>
    /// Body collision (R-2.2-07, user decision 2026-10-27; spec 2.2 "Body collision (M1)"): a move must not enter the body
    /// circle of a living entity that <see cref="Blocks"/> the mover. Shared by the server (phase 2 walks, phase 3 dashes,
    /// the monster AI's walk) and client prediction, so the same bodies give the same raw position on both sides. Integer
    /// math only, no allocations, bodies in entity-id order (architecture rules 6 and 7).
    /// <para>
    /// The mover yields: nothing is ever pushed. A body is entered when the centre distance drops below the sum of the two
    /// radii minus <see cref="BodyCollisionRules.OverlapToleranceRaw"/>. A move stops where it first touches a body (the
    /// nearest contact along the move; equal contacts go to the lower id). <see cref="BodyResponse.Slide"/> then goes on
    /// with the rest of the move along the body's tangent, so a walk rounds a monster; <see cref="BodyResponse.Stop"/> ends
    /// it there. A body the mover already touches or overlaps (a spawn, a respawn or a leash reset placed it there; nothing
    /// else can) blocks only the part of the move that goes towards its centre: bodies that overlap may move apart or
    /// sideways, never deeper, so nobody is stuck.
    /// </para>
    /// <para>
    /// The result is never farther from the start than the requested end, so the server's speed check is unaffected and a
    /// blocked move only shortens. World collision (<see cref="World.GridCollision"/>) runs after this sweep and
    /// <see cref="IsDeeper"/> guards the combination (<see cref="MovementSim.Move"/>).
    /// </para>
    /// </summary>
    public static class BodyCollision
    {
        private const long UnitQ16 = 1 << 16;

        /// <summary>
        /// A step back from a computed contact point is at most a couple of raw units (integer rounding); past this many the
        /// move ends where it started.
        /// </summary>
        private const int MaxContactStepBack = 8;

        /// <summary>
        /// A mover this close to a body's contact distance counts as touching it. A contact point is rounded to raw units, so
        /// it sits up to a raw unit outside; without the margin a slide starting there would read as entering the body again
        /// and stop.
        /// </summary>
        private const long ContactMarginRaw = 1;

        /// <summary>
        /// R-2.2-07: who blocks whom. Monsters block players and other monsters; players block monsters. Players never block
        /// players (M1; TODO(open): the push rule between players, decided with PvP).
        /// </summary>
        public static bool Blocks(EntityKind mover, EntityKind other) =>
            (mover == EntityKind.Player && other == EntityKind.Monster)
            || (mover == EntityKind.Monster && (other == EntityKind.Player || other == EntityKind.Monster));

        /// <summary>
        /// How far from the move's start a body's centre can be and still matter: the move's longest axis, the mover's
        /// radius and the largest body radius (what a gather has to cover).
        /// </summary>
        public static long ReachRaw(FixVec2 from, FixVec2 to, int moverRadiusRaw, int maxBodyRadiusRaw)
        {
            long dx = Math.Abs((long)to.X - from.X);
            long dy = Math.Abs((long)to.Y - from.Y);
            return Math.Max(dx, dy) + moverRadiusRaw + maxBodyRadiusRaw;
        }

        /// <summary>
        /// Where a move from <paramref name="from"/> towards <paramref name="to"/> ends among <paramref name="bodies"/>.
        /// <paramref name="touched"/> is the index in <paramref name="bodies"/> of the last body the move touched, -1 for
        /// none. With the rules off, no bodies, no move, or a step longer than <see cref="SweptCircle.MaxRaw"/> on an axis
        /// (no tick moves that far), the move is unchanged.
        /// </summary>
        public static FixVec2 Sweep(FixVec2 from, FixVec2 to, int moverRadiusRaw, BodyList bodies, BodyResponse response,
            in BodyCollisionRules rules, out int touched)
        {
            touched = -1;
            if (!rules.Enabled || bodies.Count == 0 || from == to || TooLong(from, to))
                return to;

            int slides = response == BodyResponse.Slide ? rules.MaxSlideIterations : 0;
            var start = from;
            var end = to;
            for (int pass = 0; ; pass++)
            {
                bool canSlide = pass < slides;

                // Bodies already touched or overlapped: only the part of the move towards their centre is blocked.
                for (int i = 0; i < bodies.Count; i++)
                {
                    var body = bodies[i];
                    long radius = Combined(moverRadiusRaw, body.RadiusRaw, rules);
                    if (!Touches(start, body.Center, radius + ContactMarginRaw))
                        continue;
                    var move = end - start;
                    var normal = start - body.Center;
                    if ((long)move.X * normal.X + (long)move.Y * normal.Y >= 0)
                        continue;
                    touched = i;
                    if (!canSlide)
                        return start;
                    end = start + Tangent(move, normal);
                    if (end == start)
                        return start;
                }

                // The nearest body the rest of the move enters (lower id on a tie: bodies are in id order).
                int hit = -1;
                int nearest = int.MaxValue;
                long hitRadius = 0;
                for (int i = 0; i < bodies.Count; i++)
                {
                    var body = bodies[i];
                    long radius = Combined(moverRadiusRaw, body.RadiusRaw, rules);
                    if (radius > SweptCircle.MaxRaw || Touches(start, body.Center, radius + ContactMarginRaw))
                        continue;
                    if (SweptCircle.Hits(start, end, body.Center, (int)radius, out int entry) && entry < nearest)
                    {
                        hit = i;
                        nearest = entry;
                        hitRadius = radius;
                    }
                }
                // A tangent is rounded to raw units, so a very short slide can end a raw unit inside the body it rounds:
                // that slide does not happen.
                if (hit < 0)
                    return IsDeeper(start, end, moverRadiusRaw, bodies, rules) ? start : end;

                touched = hit;
                var center = bodies[hit].Center;
                var contact = ContactPoint(start, end, nearest, center, hitRadius);
                if (!canSlide)
                    return contact;
                var tangent = Tangent(end - contact, contact - center);
                if (tangent == FixVec2.Zero)
                    return contact;
                start = contact;
                end = contact + tangent;
            }
        }

        /// <summary>
        /// True when <paramref name="end"/> is inside a body and deeper than <paramref name="from"/> was: the world clip after
        /// the sweep (a wall next to a body) moved the end into it. The move then does not happen.
        /// </summary>
        public static bool IsDeeper(FixVec2 from, FixVec2 end, int moverRadiusRaw, BodyList bodies, in BodyCollisionRules rules)
        {
            if (!rules.Enabled || from == end)
                return false;
            for (int i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i];
                long radius = Combined(moverRadiusRaw, body.RadiusRaw, rules);
                if (!Touches(end, body.Center, radius))
                    continue;
                long endSq = DistanceSquared(end, body.Center);
                if (endSq < radius * radius && endSq < DistanceSquared(from, body.Center))
                    return true;
            }
            return false;
        }

        /// <summary>The centre distance below which two bodies overlap: the radii added up, less the tolerance.</summary>
        private static long Combined(int moverRadiusRaw, int bodyRadiusRaw, in BodyCollisionRules rules) =>
            Math.Max(0L, (long)moverRadiusRaw + bodyRadiusRaw - rules.OverlapToleranceRaw);

        /// <summary>Within <paramref name="radius"/> of the centre, touching included.</summary>
        private static bool Touches(FixVec2 point, FixVec2 center, long radius)
        {
            long dx = (long)point.X - center.X;
            long dy = (long)point.Y - center.Y;
            if (dx > radius || dx < -radius || dy > radius || dy < -radius)
                return false;
            return dx * dx + dy * dy <= radius * radius;
        }

        /// <summary>Squared distance, for points already known to be within a body's reach (no overflow there).</summary>
        private static long DistanceSquared(FixVec2 a, FixVec2 b)
        {
            long dx = (long)a.X - b.X;
            long dy = (long)a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        private static bool TooLong(FixVec2 from, FixVec2 to)
        {
            long dx = (long)to.X - from.X;
            long dy = (long)to.Y - from.Y;
            return dx > SweptCircle.MaxRaw || dx < -SweptCircle.MaxRaw || dy > SweptCircle.MaxRaw || dy < -SweptCircle.MaxRaw;
        }

        /// <summary>
        /// The point <paramref name="entryRaw"/> along the move, stepped back towards the start until it is outside the body
        /// (the entry is rounded, so the point can sit a raw unit inside).
        /// </summary>
        private static FixVec2 ContactPoint(FixVec2 start, FixVec2 end, int entryRaw, FixVec2 center, long radius)
        {
            long dx = (long)end.X - start.X;
            long dy = (long)end.Y - start.Y;
            long length = SweptCircle.Sqrt(dx * dx + dy * dy);
            if (length == 0)
                return start;
            long radiusSquared = radius * radius;
            long entry = entryRaw;
            for (int i = 0; i <= MaxContactStepBack && entry > 0; i++, entry--)
            {
                var point = new FixVec2(start.X + (int)(dx * entry / length), start.Y + (int)(dy * entry / length));
                if (DistanceSquared(point, center) >= radiusSquared)
                    return point;
            }
            return start;
        }

        /// <summary>
        /// <paramref name="move"/> without its component along <paramref name="normal"/> (the body's tangent at the mover).
        /// The normal is made a Q16 unit vector first, so nothing overflows at the largest radius.
        /// </summary>
        private static FixVec2 Tangent(FixVec2 move, FixVec2 normal)
        {
            long nx = normal.X, ny = normal.Y;
            long length = SweptCircle.Sqrt(nx * nx + ny * ny);
            if (length == 0)
                return move;
            long ux = nx * UnitQ16 / length;
            long uy = ny * UnitQ16 / length;
            long along = ((long)move.X * ux + (long)move.Y * uy) / UnitQ16;
            return new FixVec2((int)(move.X - ux * along / UnitQ16), (int)(move.Y - uy * along / UnitQ16));
        }
    }
}
