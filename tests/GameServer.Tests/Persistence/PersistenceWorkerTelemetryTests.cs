using System.Diagnostics;
using GameServer.Persistence;
using GameServer.Telemetry;
using GameServer.Tests.Support;

namespace GameServer.Tests.Persistence;

/// <summary>Save failures reach telemetry (backlog H2); no database needed.</summary>
public class PersistenceWorkerTelemetryTests
{
    [Fact]
    public async Task A_save_that_throws_is_recorded_and_still_completes_as_failed()
    {
        var clock = new FakeClock();
        var queue = new TelemetryQueue(64);
        await using var worker = new PersistenceWorker(new ThrowingStore(), clock, queue);

        worker.Enqueue(new SaveJob(77, default, ExpectedVersion: 3, Guid.NewGuid(), SaveReason.Logout));

        var watch = Stopwatch.StartNew();
        SaveCompletion completion;
        while (!worker.TryDequeue(out completion))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "The save never completed.");
            await Task.Delay(1);
        }

        Assert.Equal(SaveOutcome.Failed, completion.Outcome);
        var buffer = new TelemetryRecord[4];
        Assert.Equal(1, queue.DrainTo(buffer));
        Assert.Equal(new TelemetryRecord(TelemetryKind.SaveFailed, clock.UtcNowMs, CharacterId: 77, V0: (long)SaveReason.Logout),
            buffer[0]);
    }

    [Fact]
    public void Telemetry_without_a_clock_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new PersistenceWorker(new ThrowingStore(), clock: null, new TelemetryQueue(64)));
    }

    private sealed class ThrowingStore : ICharacterSaveStore
    {
        public Task<SaveResult> SaveAsync(long characterId, CharacterState state, long expectedVersion, Guid saveId,
            InventoryBatch? inventory = null, CancellationToken ct = default, ProgressionBatch? progression = null,
            SkillBatch? skills = null) =>
            throw new TimeoutException("database unreachable");
    }
}
