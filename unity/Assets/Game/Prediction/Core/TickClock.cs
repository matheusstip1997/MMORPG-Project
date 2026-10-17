#nullable enable
using System;

namespace Game.Gameplay.Prediction
{
    /// <summary>
    /// Paces the client's fixed simulation ticks (spec 1.6: 20 Hz) from the rendered frame times. After a hitch it runs
    /// at most <c>ClientPrediction.MaxTicksPerFrame</c> ticks in one frame and drops the rest of the time, so a long
    /// stall does not burst more frames at the server than its input queue keeps.
    /// </summary>
    public sealed class TickClock
    {
        private readonly double _tickMs;
        private readonly int _maxTicksPerFrame;
        private double _accumulatedMs;

        public TickClock(PredictionSettings settings)
        {
            _tickMs = settings.TickMs;
            _maxTicksPerFrame = settings.MaxTicksPerFrame;
        }

        /// <summary>Time dropped by the catch-up cap, in ms.</summary>
        public double DroppedMs { get; private set; }

        /// <summary>How far the current time is into the next tick, 0 to 1.</summary>
        public float Alpha => (float)(_accumulatedMs / _tickMs);

        public void Reset()
        {
            _accumulatedMs = 0;
            DroppedMs = 0;
        }

        /// <summary>Adds a frame's time and returns how many ticks to run now.</summary>
        public int Advance(double deltaMs)
        {
            _accumulatedMs += Math.Max(0, deltaMs);
            int ticks = (int)(_accumulatedMs / _tickMs);
            if (ticks > _maxTicksPerFrame)
            {
                DroppedMs += (ticks - _maxTicksPerFrame) * _tickMs;
                _accumulatedMs -= (ticks - _maxTicksPerFrame) * _tickMs;
                ticks = _maxTicksPerFrame;
            }
            _accumulatedMs -= ticks * _tickMs;
            return ticks;
        }
    }
}
