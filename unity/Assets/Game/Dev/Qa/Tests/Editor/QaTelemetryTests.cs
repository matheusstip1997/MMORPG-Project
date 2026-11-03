#nullable enable
using NUnit.Framework;

namespace Game.Dev.Qa.EditorTests
{
    /// <summary>The lock-demon step's zero-clamp check (M3 decision 15): LockFacingClamp records of one character.</summary>
    public sealed class QaTelemetryTests
    {
        private const string Clamped12 =
            "{\"k\":\"LockFacingClamp\",\"t\":1793207373328,\"w\":1,\"a\":3,\"entity\":12,\"tick\":905,\"sentFacing\":16384,\"allowedFacing\":15000,\"unchecked\":0}";
        private const string Unchecked12 =
            "{\"k\":\"LockFacingClamp\",\"t\":1793207373378,\"w\":1,\"a\":3,\"entity\":12,\"tick\":906,\"sentFacing\":16384,\"allowedFacing\":16384,\"unchecked\":1}";
        private const string Clamped120 =
            "{\"k\":\"LockFacingClamp\",\"t\":1793207373428,\"w\":1,\"a\":4,\"entity\":120,\"tick\":907,\"sentFacing\":1,\"allowedFacing\":2,\"unchecked\":0}";
        private const string Speed12 = "{\"k\":\"SpeedViolation\",\"t\":1793207373428,\"w\":1,\"a\":3,\"entity\":12,\"distanceCm\":40,\"allowedCm\":22}";

        [Test]
        public void Counts_only_the_characters_own_lock_records_split_by_the_unchecked_flag()
        {
            QaTelemetry.CountLockFacingClamps(new[] { Clamped12, Unchecked12, Clamped120, Speed12, Clamped12 }, 12, out int clamped,
                out int uncheckedFrames);

            Assert.That(clamped, Is.EqualTo(2));
            Assert.That(uncheckedFrames, Is.EqualTo(1));
        }

        [Test]
        public void No_records_count_zero()
        {
            QaTelemetry.CountLockFacingClamps(new[] { Speed12 }, 12, out int clamped, out int uncheckedFrames);

            Assert.That(clamped, Is.Zero);
            Assert.That(uncheckedFrames, Is.Zero);
        }
    }
}
