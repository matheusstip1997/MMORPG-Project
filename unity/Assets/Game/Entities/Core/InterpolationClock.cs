#nullable enable
using System;
using Game.Gameplay.Prediction;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// Render time for remote entities, in server ticks (fractional): the newest server time this client knows minus
    /// <c>StartingValuesM1.InterpolationDelayMs</c>, so there are usually two snapshots around it to blend.
    /// <list type="bullet">
    /// <item>The target is the newest snapshot's tick plus the local time since it arrived, minus the delay.</item>
    /// <item>The render time runs with local time and drifts toward the target with time constant
    /// <c>ClientPrediction.InterpolationClockCatchUpMs</c>, so arrival jitter does not make motion stutter.</item>
    /// <item>An error larger than the delay (at least one tick) means the buffer ran dry or the server jumped: it snaps.</item>
    /// </list>
    /// Presentation only (doubles are fine here: nothing feeds back into the simulation).
    /// </summary>
    public sealed class InterpolationClock
    {
        private readonly double _tickMs;
        private readonly double _delayMs;
        private readonly double _catchUpMs;
        private readonly double _snapMs;
        private uint _newestTick;
        private double _newestArrivalMs;
        private double _renderMs;

        public InterpolationClock(PredictionSettings settings)
        {
            _tickMs = settings.TickMs;
            _delayMs = settings.InterpolationDelayMs;
            _catchUpMs = settings.InterpolationClockCatchUpMs;
            _snapMs = Math.Max(_delayMs, _tickMs);
        }

        public bool HasTime { get; private set; }

        /// <summary>Render time in server ticks.</summary>
        public double RenderTick => _renderMs / _tickMs;

        public long Snaps { get; private set; }

        public void Reset()
        {
            HasTime = false;
            _newestTick = 0;
            _newestArrivalMs = 0;
            _renderMs = 0;
            Snaps = 0;
        }

        /// <summary>A snapshot of <paramref name="serverTick"/> arrived at local time <paramref name="localNowMs"/>.</summary>
        public void OnSnapshot(uint serverTick, double localNowMs)
        {
            if (HasTime && (int)(serverTick - _newestTick) <= 0)
                return;
            _newestTick = serverTick;
            _newestArrivalMs = localNowMs;
            if (!HasTime)
            {
                HasTime = true;
                _renderMs = Target(localNowMs);
            }
        }

        /// <summary>Advances the render time by one rendered frame.</summary>
        public void Advance(double localNowMs, double deltaMs)
        {
            if (!HasTime)
                return;
            _renderMs += Math.Max(0, deltaMs);
            double error = Target(localNowMs) - _renderMs;
            if (Math.Abs(error) > _snapMs)
            {
                _renderMs += error;
                Snaps++;
                return;
            }
            _renderMs += error * (1 - Math.Exp(-Math.Max(0, deltaMs) / _catchUpMs));
        }

        private double Target(double localNowMs) =>
            _newestTick * _tickMs + (localNowMs - _newestArrivalMs) - _delayMs;
    }
}
