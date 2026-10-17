#nullable enable
using System;
using Shared.Simulation;

namespace Game.Gameplay.Prediction
{
    /// <summary>
    /// Where to draw the local player between simulation ticks (spec 2.2: smooth visual corrections, no snapping unless
    /// the error is large). Presentation only: floats here never feed back into the simulation.
    /// <list type="bullet">
    /// <item>Between ticks the position is blended from the state before the last tick to the state after it, so
    /// movement looks continuous at any frame rate (one tick of visual delay).</item>
    /// <item>A correction moves both tick states by its shift and adds the opposite shift to a visual offset, so the
    /// drawn position does not jump; the offset then fades with time constant
    /// <c>ClientPrediction.CorrectionSmoothingMs</c>. An offset larger than <c>CorrectionSnapCm</c> is dropped at once
    /// (snap), as is every offset when the smoothing time is 0.</item>
    /// </list>
    /// Positions are ground-plane cm in simulation axes (+Y = screen-up at camera yaw 0).
    /// </summary>
    public sealed class LocalPlayerMotion
    {
        private readonly float _snapCm;
        private readonly float _smoothingMs;
        private float _previousX;
        private float _previousY;
        private float _currentX;
        private float _currentY;

        public LocalPlayerMotion(PredictionSettings settings)
        {
            _snapCm = settings.CorrectionSnapCm;
            _smoothingMs = settings.CorrectionSmoothingMs;
        }

        /// <summary>Visual offset still to fade out, in cm.</summary>
        public float OffsetXCm { get; private set; }

        public float OffsetYCm { get; private set; }

        /// <summary>Starts at a position with nothing to blend or fade (first state, respawn).</summary>
        public void Reset(FixVec2 position)
        {
            _previousX = _currentX = ToCm(position.X);
            _previousY = _currentY = ToCm(position.Y);
            OffsetXCm = 0;
            OffsetYCm = 0;
        }

        /// <summary>After each predicted tick: the new state becomes the blend target.</summary>
        public void OnTick(FixVec2 position)
        {
            _previousX = _currentX;
            _previousY = _currentY;
            _currentX = ToCm(position.X);
            _currentY = ToCm(position.Y);
        }

        /// <summary>After a reconciliation that moved the prediction by <paramref name="shift"/>.</summary>
        public void OnCorrected(FixVec2 shift)
        {
            float dx = ToCm(shift.X);
            float dy = ToCm(shift.Y);
            _previousX += dx;
            _previousY += dy;
            _currentX += dx;
            _currentY += dy;
            OffsetXCm -= dx;
            OffsetYCm -= dy;
            if (_smoothingMs <= 0 || OffsetXCm * OffsetXCm + OffsetYCm * OffsetYCm > _snapCm * _snapCm)
            {
                OffsetXCm = 0;
                OffsetYCm = 0;
            }
        }

        /// <summary>Fades the correction offset; call once per rendered frame.</summary>
        public void Update(float deltaMs)
        {
            if (_smoothingMs <= 0)
                return;
            float keep = (float)Math.Exp(-Math.Max(0f, deltaMs) / _smoothingMs);
            OffsetXCm *= keep;
            OffsetYCm *= keep;
        }

        /// <param name="alpha">How far the render time is into the next tick, 0 to 1 (<see cref="TickClock.Alpha"/>).</param>
        public void GetVisualCm(float alpha, out float xCm, out float yCm)
        {
            alpha = Math.Max(0f, Math.Min(1f, alpha));
            xCm = _previousX + (_currentX - _previousX) * alpha + OffsetXCm;
            yCm = _previousY + (_currentY - _previousY) * alpha + OffsetYCm;
        }

        private static float ToCm(int raw) => raw / (float)FixedPoint.RawPerCm;
    }
}
