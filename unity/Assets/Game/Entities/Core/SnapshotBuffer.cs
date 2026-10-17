#nullable enable
using System;

namespace Game.Gameplay.Entities
{
    /// <summary>One remote entity's state at one server tick, in snapshot axes (Y-up, whole cm).</summary>
    public struct EntitySample
    {
        public uint Tick;
        public int XCm;
        public int YCm;
        public int ZCm;
        /// <summary>Binary angle, 65536 = one turn.</summary>
        public ushort Facing;
    }

    /// <summary>A blended pose, in snapshot axes. Presentation only.</summary>
    public struct EntityPose
    {
        public float XCm;
        public float YCm;
        public float ZCm;
        /// <summary>Facing in binary-angle units (65536 = one turn), possibly fractional.</summary>
        public float Facing;
    }

    /// <summary>
    /// The recent snapshot samples of one remote entity, kept in tick order, and the blend between the two around the
    /// render time (spec 2.2/12.7: remote entities are drawn <c>InterpolationDelayMs</c> behind).
    /// Reordered samples are inserted in place, duplicates ignored, gaps (dropped snapshots) blended across.
    /// Past the newest sample the pose holds there (no extrapolation); before the oldest it holds the oldest.
    /// Does not allocate after construction.
    /// </summary>
    public sealed class SnapshotBuffer
    {
        private readonly EntitySample[] _samples;
        private int _count;

        /// <param name="capacity">Samples kept; enough for the interpolation delay plus a few late ones.</param>
        public SnapshotBuffer(int capacity)
        {
            if (capacity < 2)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _samples = new EntitySample[capacity];
        }

        /// <summary>Capacity for a delay of <paramref name="delayMs"/>: twice the ticks it spans plus a margin of four.</summary>
        public static int CapacityFor(int delayMs, double tickMs) => 2 * (int)Math.Ceiling(delayMs / tickMs) + 4;

        public int Count => _count;

        public uint NewestTick => _count > 0 ? _samples[_count - 1].Tick : 0;

        public void Clear() => _count = 0;

        /// <summary>Adds a sample; false when it is a duplicate or older than everything kept in a full buffer.</summary>
        public bool Add(in EntitySample sample)
        {
            int index = _count;
            while (index > 0 && IsAfter(_samples[index - 1].Tick, sample.Tick))
                index--;
            if (index > 0 && _samples[index - 1].Tick == sample.Tick)
                return false;

            if (_count == _samples.Length)
            {
                if (index == 0)
                    return false;
                // Drop the oldest to make room.
                Array.Copy(_samples, 1, _samples, 0, _count - 1);
                _count--;
                index--;
            }
            if (index < _count)
                Array.Copy(_samples, index, _samples, index + 1, _count - index);
            _samples[index] = sample;
            _count++;
            return true;
        }

        /// <summary>Blends the samples around <paramref name="renderTick"/>. False when there are none.</summary>
        public bool TrySample(double renderTick, out EntityPose pose)
        {
            pose = default;
            if (_count == 0)
                return false;

            if (_count == 1 || renderTick <= Unwrap(_samples[0].Tick, renderTick))
            {
                pose = ToPose(_samples[0]);
                return true;
            }
            for (int i = 1; i < _count; i++)
            {
                double t1 = Unwrap(_samples[i].Tick, renderTick);
                if (renderTick > t1)
                    continue;
                double t0 = Unwrap(_samples[i - 1].Tick, renderTick);
                float a = (float)((renderTick - t0) / (t1 - t0));
                pose = Blend(_samples[i - 1], _samples[i], a);
                return true;
            }
            pose = ToPose(_samples[_count - 1]);
            return true;
        }

        /// <summary>Drops samples no longer needed: everything before the last one at or before the render time.</summary>
        public void Prune(double renderTick)
        {
            int keepFrom = 0;
            for (int i = 1; i < _count; i++)
            {
                if (Unwrap(_samples[i].Tick, renderTick) <= renderTick)
                    keepFrom = i;
                else
                    break;
            }
            if (keepFrom == 0)
                return;
            Array.Copy(_samples, keepFrom, _samples, 0, _count - keepFrom);
            _count -= keepFrom;
        }

        private static bool IsAfter(uint a, uint b) => (int)(a - b) > 0;

        /// <summary>A tick as a double near <paramref name="reference"/>, so the 32-bit wrap does not break comparisons.</summary>
        private static double Unwrap(uint tick, double reference)
        {
            uint referenceTick = (uint)(long)Math.Floor(reference);
            return Math.Floor(reference) + (int)(tick - referenceTick);
        }

        private static EntityPose ToPose(in EntitySample s) =>
            new EntityPose { XCm = s.XCm, YCm = s.YCm, ZCm = s.ZCm, Facing = s.Facing };

        private static EntityPose Blend(in EntitySample from, in EntitySample to, float a)
        {
            // Shortest way around the circle.
            short turn = (short)(ushort)(to.Facing - from.Facing);
            return new EntityPose
            {
                XCm = from.XCm + (to.XCm - from.XCm) * a,
                YCm = from.YCm + (to.YCm - from.YCm) * a,
                ZCm = from.ZCm + (to.ZCm - from.ZCm) * a,
                Facing = from.Facing + turn * a,
            };
        }
    }
}
