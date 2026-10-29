using System;
using Shared.Data.Tables;
using Shared.World;

namespace Shared.Simulation
{
    /// <summary>
    /// Advances one entity's position and facing by one fixed tick (spec 2.2). The client predicts with it
    /// and the server runs the same code on received inputs, so it is pure integer math: the same inputs
    /// give bit-identical results on every runtime (architecture rules 6 and 7).
    /// <para>
    /// Collision with the exported world is the overload that takes a <see cref="WorldGrid"/> (backlog C3); the
    /// server's speed check is <see cref="SpeedCheck"/>. Not here: mount bonus; dashes (the dodge's too) and input
    /// buffering (ActionMachine).
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

        /// <summary>
        /// One tick of movement, then collision with the world grid (<see cref="GridCollision.Move(WorldGrid, FixVec2, FixVec2, in MoverGate)"/>).
        /// Server phase 2 and client prediction both call this, so a blocked move ends on the same raw position on both sides.
        /// A player is held to its level's <paramref name="gate"/> (R-2.3-06); <c>default</c> holds none.
        /// </summary>
        public static MovementState Step(in MovementState state, in MovementInput input, bool actionActive,
            int speedCmPerSec, int tickRateHz, WorldGrid grid, in MoverGate gate = default)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            var next = Step(state, input, actionActive, speedCmPerSec, tickRateHz);
            if (next.Position == state.Position)
                return next;
            return new MovementState(GridCollision.Move(grid, state.Position, next.Position, gate), next.Facing);
        }

        /// <summary>
        /// One tick of movement along a binary angle instead of move keys, then collision with the world grid: the way the
        /// server moves entities without input (monster AI, backlog F). The facing becomes <paramref name="facing"/>. While
        /// <paramref name="actionActive"/> is true the entity stays put and keeps its facing (R-2.2-02).
        /// </summary>
        public static MovementState StepAlong(in MovementState state, ushort facing, bool actionActive, int speedCmPerSec,
            int tickRateHz, WorldGrid grid)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (actionActive)
                return state;
            var target = state.Position + FixedTrig.Along(facing, StepPerTick(speedCmPerSec, tickRateHz));
            return new MovementState(GridCollision.Move(grid, state.Position, target), facing);
        }

        /// <summary>
        /// One tick of movement with world and body collision (R-2.2-07): the walk response of <paramref name="rules"/>
        /// against <paramref name="bodies"/>, then the world grid (<see cref="Move"/>). Server phase 2 and client prediction
        /// both call this; with no bodies it is the grid-only <see cref="Step(in MovementState, in MovementInput, bool, int, int, WorldGrid, in MoverGate)"/>.
        /// A player is held to its level's <paramref name="gate"/> (R-2.3-06).
        /// </summary>
        public static MovementState Step(in MovementState state, in MovementInput input, bool actionActive,
            int speedCmPerSec, int tickRateHz, WorldGrid grid, BodyList bodies, int moverRadiusRaw, in BodyCollisionRules rules,
            in MoverGate gate = default)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            var next = Step(state, input, actionActive, speedCmPerSec, tickRateHz);
            if (next.Position == state.Position)
                return next;
            var position = Move(grid, state.Position, next.Position, bodies, moverRadiusRaw, rules.WalkResponse, rules, out _, gate);
            return new MovementState(position, next.Facing);
        }

        /// <summary>
        /// <see cref="StepAlong(in MovementState, ushort, bool, int, int, WorldGrid)"/> with body collision (the walk response):
        /// the monster AI's walk (R-2.2-07).
        /// </summary>
        public static MovementState StepAlong(in MovementState state, ushort facing, bool actionActive, int speedCmPerSec,
            int tickRateHz, WorldGrid grid, BodyList bodies, int moverRadiusRaw, in BodyCollisionRules rules)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (actionActive)
                return state;
            var target = state.Position + FixedTrig.Along(facing, StepPerTick(speedCmPerSec, tickRateHz));
            var position = Move(grid, state.Position, target, bodies, moverRadiusRaw, rules.WalkResponse, rules, out _);
            return new MovementState(position, facing);
        }

        /// <summary>
        /// One tick of a Dash effect (spec 3.1, D2) with world and body collision: the dash response of
        /// <paramref name="rules"/> (R-2.2-07: a dash or dodge stops at a body). Server phase 3 and client prediction.
        /// <paramref name="touched"/> is the index in <paramref name="bodies"/> of the body it touched, -1 for none. A player's
        /// dash (a dodge too) is held to its level's <paramref name="gate"/> like its walk (R-2.3-06).
        /// </summary>
        public static FixVec2 Dash(WorldGrid grid, FixVec2 from, FixVec2 delta, BodyList bodies, int moverRadiusRaw,
            in BodyCollisionRules rules, out int touched, in MoverGate gate = default) =>
            Move(grid, from, from + delta, bodies, moverRadiusRaw, rules.DashResponse, rules, out touched, gate);

        /// <summary>
        /// A move with body collision first (<see cref="BodyCollision.Sweep"/>), then world collision with the mover's level
        /// gate (<see cref="GridCollision.Move(WorldGrid, FixVec2, FixVec2, in MoverGate)"/>); when the world clip pushed the
        /// end deeper into a body (<see cref="BodyCollision.IsDeeper"/>: a body against a wall) the move does not happen. A
        /// blocked move only shortens, so the speed check and the position history are unaffected.
        /// </summary>
        public static FixVec2 Move(WorldGrid grid, FixVec2 from, FixVec2 to, BodyList bodies, int moverRadiusRaw,
            BodyResponse response, in BodyCollisionRules rules, out int touched, in MoverGate gate = default)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (bodies == null)
                throw new ArgumentNullException(nameof(bodies));
            var swept = BodyCollision.Sweep(from, to, moverRadiusRaw, bodies, response, rules, out touched);
            var end = GridCollision.Move(grid, from, swept, gate);
            return BodyCollision.IsDeeper(from, end, moverRadiusRaw, bodies, rules) ? from : end;
        }

        /// <summary>
        /// R-2.2-03 with the <c>moveSpeedPercent</c> stat (WP-M2-9: Cold's slow): <paramref name="baseCmPerSec"/> times
        /// (100 + <paramref name="moveSpeedPercent"/>) / 100, rounded down, never below 0 (the stat's own range is -90..100).
        /// The server and prediction use the same speed.
        /// </summary>
        public static int SpeedFor(int baseCmPerSec, int moveSpeedPercent)
        {
            if (moveSpeedPercent == 0)
                return baseCmPerSec;
            long speed = (long)baseCmPerSec * (100 + Math.Max(-100, moveSpeedPercent)) / 100;
            return (int)Math.Min(int.MaxValue, speed);
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
