using NUnit.Framework;
using Shared.Simulation;
using Shared.Util;

namespace Game.Tests.EditMode
{
    /// <summary>
    /// Runs the shared MovementSim inside Unity (Mono) and checks it against the golden result that
    /// tests/Shared.Tests/Simulation/MovementSimTests.cs asserts on .NET: client prediction and the server
    /// must produce bit-identical states (architecture rules 6 and 7, spec 2.2).
    /// </summary>
    public class MovementSimTests
    {
        // StartingValuesM1 (data/tables/StartingValuesM1.json).
        private const int SpeedCmPerSec = 400;
        private const int TickRateHz = 20;

        // Keep in sync with GoldenScenario in the .NET tests.
        private const int Ticks = 10000;
        private const ulong Seed = 20261016;
        private const ulong Stream = 7;
        private const ulong ExpectedChecksum = 652694746327926806UL;

        [Test]
        public void Scripted_run_matches_the_dotnet_golden_result()
        {
            var rng = new Pcg32Rng(Seed, Stream);
            var state = new MovementState(FixVec2.Zero, 0);
            ulong checksum = 14695981039346656037UL;
            for (int tick = 0; tick < Ticks; tick++)
            {
                var keys = (MoveKeys)rng.NextInt(16);
                var facing = (ushort)rng.NextInt(65536);
                bool actionActive = rng.RollPercent(20);
                state = MovementSim.Step(state, new MovementInput(keys, facing), actionActive, SpeedCmPerSec, TickRateHz);
                checksum = Mix(checksum, (uint)state.Position.X);
                checksum = Mix(checksum, (uint)state.Position.Y);
                checksum = Mix(checksum, state.Facing);
            }

            Assert.AreEqual(new MovementState(new FixVec2(-109280, -147960), 55444), state);
            Assert.AreEqual(ExpectedChecksum, checksum);
        }

        [Test]
        public void Pcg32_matches_the_reference_output()
        {
            var rng = new Pcg32Rng(42, 54);

            Assert.AreEqual(0xa15c02b7u, rng.NextUInt());
            Assert.AreEqual(0x7b47f409u, rng.NextUInt());
        }

        private static ulong Mix(ulong hash, uint value)
        {
            for (int i = 0; i < 4; i++)
            {
                hash ^= (value >> (i * 8)) & 0xFF;
                hash *= 1099511628211UL;
            }
            return hash;
        }
    }
}
