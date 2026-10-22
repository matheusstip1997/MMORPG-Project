using System;
using Shared.World;

namespace Shared.Simulation.Combat
{
    /// <summary>
    /// The straight path of one projectile (spec 3.2): from <see cref="Origin"/> along <see cref="Facing"/> at
    /// <see cref="SpeedCmPerSec"/>, up to <see cref="LimitRaw"/>: its range, or less where a wall stops it. The server
    /// simulates it and the client draws it from the same spawn values with this code, so both put the projectile on the
    /// same raw point at every tick (rule 6). Integer only, no allocations.
    /// <para>
    /// Time: the projectile is spawned in phase 3 of tick <see cref="StartTick"/> and moves in phase 4 of every tick from
    /// then on, so after phase 4 of tick T it has flown <c>T - StartTick + 1</c> steps: <c>PositionAt(T - StartTick + 1)</c>.
    /// A step is <c>speed / tickRate</c>; the distance after <c>n</c> steps is <c>min(limit, speed * n / tickRate)</c>.
    /// </para>
    /// </summary>
    public readonly struct ProjectilePath
    {
        private readonly long _speedRawPerSec;

        private ProjectilePath(FixVec2 origin, ushort facing, int speedCmPerSec, int rangeCm, uint startTick, int tickRateHz,
            int limitRaw, bool stoppedByWorld)
        {
            Origin = origin;
            Facing = facing;
            SpeedCmPerSec = speedCmPerSec;
            RangeCm = rangeCm;
            StartTick = startTick;
            TickRateHz = tickRateHz;
            _speedRawPerSec = (long)speedCmPerSec * FixedPoint.RawPerCm;
            LimitRaw = limitRaw;
            StoppedByWorld = stoppedByWorld;
        }

        /// <summary>Where it starts (raw Q24.8, on whole cm so the spawn message carries it exactly).</summary>
        public FixVec2 Origin { get; }

        /// <summary>The facing locked at the start of the action (16-bit binary angle; FixedTrig uses its top 10 bits).</summary>
        public ushort Facing { get; }

        public int SpeedCmPerSec { get; }

        public int RangeCm { get; }

        public uint StartTick { get; }

        public int TickRateHz { get; }

        /// <summary>How far it flies (raw): the range, or the distance to the wall that stops it.</summary>
        public int LimitRaw { get; }

        /// <summary>True when a wall ends it before its range.</summary>
        public bool StoppedByWorld { get; }

        /// <summary>
        /// The path from a spawn. With <paramref name="grid"/> (the projectile stops on the world) the whole path is cast
        /// against it once with <see cref="GridRay"/>, and it ends where it would enter a non-walkable cell.
        /// </summary>
        public static ProjectilePath Create(FixVec2 origin, ushort facing, int speedCmPerSec, int rangeCm, uint startTick,
            int tickRateHz, WorldGrid? grid)
        {
            if (speedCmPerSec <= 0)
                throw new ArgumentOutOfRangeException(nameof(speedCmPerSec), "must be positive");
            if (rangeCm <= 0 || rangeCm > (int.MaxValue >> FixedPoint.FractionBits))
                throw new ArgumentOutOfRangeException(nameof(rangeCm));
            if (tickRateHz <= 0)
                throw new ArgumentOutOfRangeException(nameof(tickRateHz), "must be positive");

            int rangeRaw = FixedPoint.FromCm(rangeCm);
            int limit = rangeRaw;
            bool stopped = false;
            if (grid != null && GridRay.Cast(grid, origin, origin + FixedTrig.Along(facing, rangeRaw), out var stop))
            {
                // The distance along the facing to the stop point (the stop is on the path, so this is its length).
                long along = (((long)(stop.X - origin.X) * FixedTrig.Sin(facing)) + ((long)(stop.Y - origin.Y) * FixedTrig.Cos(facing)))
                    >> 16;
                limit = (int)Math.Max(0, Math.Min(rangeRaw, along));
                stopped = true;
            }
            return new ProjectilePath(origin, facing, speedCmPerSec, rangeCm, startTick, tickRateHz, limit, stopped);
        }

        /// <summary>Distance flown (raw) after <paramref name="steps"/> steps; 0 before the first.</summary>
        public int DistanceRawAt(int steps)
        {
            if (steps <= 0)
                return 0;
            long distance = _speedRawPerSec * steps / TickRateHz;
            return distance >= LimitRaw ? LimitRaw : (int)distance;
        }

        /// <summary>Position (raw) after <paramref name="steps"/> steps.</summary>
        public FixVec2 PositionAt(int steps) => Origin + FixedTrig.Along(Facing, DistanceRawAt(steps));

        /// <summary>The end of the path (raw).</summary>
        public FixVec2 End => Origin + FixedTrig.Along(Facing, LimitRaw);

        /// <summary>Steps until it has flown its whole path: the first <c>n</c> with <c>DistanceRawAt(n) == LimitRaw</c> (at least 1).</summary>
        public int FlightSteps
        {
            get
            {
                long steps = ((long)LimitRaw * TickRateHz + _speedRawPerSec - 1) / _speedRawPerSec;
                return (int)Math.Max(1, steps);
            }
        }

        /// <summary>The tick whose phase 4 takes the last step (and despawns it unless a hit ends it first).</summary>
        public uint LastTick => unchecked(StartTick + (uint)FlightSteps - 1);

        /// <summary>Steps flown after phase 4 of <paramref name="tick"/>: <c>tick - StartTick + 1</c>, 0 before the start.</summary>
        public int StepsAfter(uint tick)
        {
            long steps = (long)tick - StartTick + 1;
            return steps <= 0 ? 0 : steps >= int.MaxValue ? int.MaxValue : (int)steps;
        }
    }
}
