using System;

namespace Shared.Simulation
{
    /// <summary>
    /// Advances one entity's position and facing by one fixed tick (spec 2.2). The client predicts with it
    /// and the server runs the same code on received inputs, so it is pure integer math: the same inputs
    /// give bit-identical results on every runtime (architecture rules 6 and 7).
    /// <para>
    /// Not here yet: collision and world bounds (workstream C), the server speed check against
    /// maxSpeed(stat + mount), mount bonus, dodge motion and input buffering (ActionMachine).
    /// </para>
    /// </summary>
    public static class MovementSim
    {
        /// <summary>
        /// floor(65536 / sqrt(2)) rounded down to 46340: each diagonal axis step is slightly below
        /// step / sqrt(2), so a diagonal tick is never longer than a straight one.
        /// </summary>
        private const long InvSqrt2Q16 = 46340;

        /// <summary>
        /// One tick of movement.
        /// <para>
        /// R-2.2-02: while <paramref name="actionActive"/> is true the move keys and the input facing are
        /// ignored, so the entity stays put and keeps the facing it had when the action started. Tick phase 2
        /// (movement) runs before phase 3 (actions, spec 1.6), so the facing captured at action start is the
        /// one set by the input of the tick the action starts on.
        /// </para>
        /// </summary>
        /// <param name="speedCmPerSec">Movement speed (R-2.2-03; for M1 <c>StartingValuesM1.MoveSpeedCmPerSec</c>).</param>
        /// <param name="tickRateHz">Simulation tick rate (<c>StartingValuesM1.TickRateHz</c>).</param>
        public static MovementState Step(in MovementState state, in MovementInput input, bool actionActive,
            int speedCmPerSec, int tickRateHz)
        {
            if (actionActive)
                return state;

            // TODO(open): facing changes between actions are rate-limited (spec 2.2 implementation notes),
            // but the limit is not in config yet. Ask before adding a value.
            ushort facing = input.Facing;

            int dx = Axis(input.Keys, MoveKeys.D, MoveKeys.A);
            int dy = Axis(input.Keys, MoveKeys.W, MoveKeys.S);
            if (dx == 0 && dy == 0)
                return new MovementState(state.Position, facing);

            int step = StepPerTick(speedCmPerSec, tickRateHz);
            if (dx != 0 && dy != 0)
                step = (int)((step * InvSqrt2Q16) >> 16);

            var delta = new FixVec2(dx * step, dy * step);
            return new MovementState(state.Position + delta, facing);
        }

        /// <summary>Straight-line distance per tick in Q24.8 cm, rounded down.</summary>
        public static int StepPerTick(int speedCmPerSec, int tickRateHz)
        {
            if (speedCmPerSec < 0)
                throw new ArgumentOutOfRangeException(nameof(speedCmPerSec), "must not be negative");
            if (tickRateHz <= 0)
                throw new ArgumentOutOfRangeException(nameof(tickRateHz), "must be positive");
            return (int)((long)speedCmPerSec * FixedPoint.RawPerCm / tickRateHz);
        }

        // Opposite keys cancel each other.
        private static int Axis(MoveKeys keys, MoveKeys positive, MoveKeys negative)
        {
            int value = 0;
            if ((keys & positive) != 0) value++;
            if ((keys & negative) != 0) value--;
            return value;
        }
    }
}
