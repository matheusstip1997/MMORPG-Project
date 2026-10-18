#nullable enable
using System;
using Shared.Data.Tables;

namespace Game.Gameplay.Input
{
    /// <summary>
    /// The camera's horizontal turn around the character (R-2.2-05; the 45-degree pitch never changes). While a turn
    /// key is held the yaw turns at <c>ClientCamera.RotateDegreesPerSec</c>; once released it settles on the nearest
    /// 45-degree step with time constant <c>YawSettleMs</c>, where screen-relative WASD is exact
    /// (<see cref="ScreenRelativeKeys"/>). Presentation and input only.
    /// </summary>
    public sealed class CameraYaw
    {
        public const float StepDegrees = 360f / ScreenRelativeKeys.Steps;

        private readonly float _degreesPerSec;
        private readonly float _settleMs;

        public CameraYaw(ClientCamera camera)
        {
            _degreesPerSec = camera.RotateDegreesPerSec;
            _settleMs = camera.YawSettleMs;
        }

        /// <summary>Yaw in degrees, clockwise from world +Y seen from above; not wrapped.</summary>
        public float Degrees { get; private set; }

        /// <summary>The 45-degree step the movement keys are rotated by: the nearest one to the current yaw.</summary>
        public int Step => (int)Math.Round(Degrees / StepDegrees, MidpointRounding.AwayFromZero);

        /// <param name="turn">-1 counter-clockwise, +1 clockwise, 0 released.</param>
        public void Update(int turn, float deltaMs)
        {
            deltaMs = Math.Max(0f, deltaMs);
            if (turn != 0)
            {
                Degrees += Math.Sign(turn) * _degreesPerSec * deltaMs / 1000f;
                return;
            }
            float target = Step * StepDegrees;
            if (_settleMs <= 0)
            {
                Degrees = target;
                return;
            }
            Degrees = target + (Degrees - target) * (float)Math.Exp(-deltaMs / _settleMs);
            if (Math.Abs(Degrees - target) < 0.01f)
                Degrees = target;
        }
    }
}
