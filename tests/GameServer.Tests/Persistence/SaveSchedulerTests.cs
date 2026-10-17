using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data.Tables;

namespace GameServer.Tests.Persistence;

/// <summary>Save classes and the ordering barrier (R-7.1-01, R-7.1-02, R-7.1-05) with a fake clock.</summary>
public sealed class SaveSchedulerTests
{
    private const int Class2Ms = 1_000;
    private const int VolatileMs = 10_000;

    private readonly FakeClock _clock = new();
    private readonly FakeWorld _world = new();
    private readonly List<SaveJob> _sent = [];
    private readonly Queue<SaveCompletion> _completions = new();
    private readonly List<(long Id, SaveOutcome Outcome)> _faults = [];
    private readonly SaveScheduler _scheduler;

    public SaveSchedulerTests()
    {
        var values = new StartingValuesM1 { SaveClass2IntervalMs = Class2Ms, VolatileSaveIntervalMs = VolatileMs };
        _scheduler = new SaveScheduler(values, _clock, _world, new Sink(_sent), new Completions(_completions),
            (id, outcome) => _faults.Add((id, outcome)));
    }

    private void AdvanceAndTick(long ms)
    {
        _clock.Advance(ms);
        _scheduler.Tick();
    }

    private void Complete(SaveJob job, SaveOutcome outcome = SaveOutcome.Saved, long? version = null) =>
        _completions.Enqueue(new SaveCompletion(job.CharacterId, job.SaveId, outcome, version ?? job.ExpectedVersion + 1));

    [Fact]
    public void Class2ChangesAreSavedOnTheBatchInterval()
    {
        _scheduler.Track(1, version: 5);
        _scheduler.MarkDirty(1, DirtyFlags.Class2);

        AdvanceAndTick(Class2Ms - 1);
        Assert.Empty(_sent);

        AdvanceAndTick(1);
        var job = Assert.Single(_sent);
        Assert.Equal(1, job.CharacterId);
        Assert.Equal(5, job.ExpectedVersion);
        Assert.Equal(SaveReason.Class2Batch, job.Reason);
        Assert.Equal(_world.StateOf(1), job.State);
    }

    [Fact]
    public void VolatileChangesWaitForTheVolatileInterval()
    {
        _scheduler.Track(1, version: 1);
        _scheduler.MarkDirty(1, DirtyFlags.Volatile);

        for (var elapsed = Class2Ms; elapsed < VolatileMs; elapsed += Class2Ms)
            AdvanceAndTick(Class2Ms);
        Assert.Empty(_sent);

        AdvanceAndTick(Class2Ms);
        Assert.Equal(SaveReason.VolatilePeriodic, Assert.Single(_sent).Reason);
    }

    [Fact]
    public void CleanCharactersAreNotSaved()
    {
        _scheduler.Track(1, version: 1);
        AdvanceAndTick(VolatileMs);
        Assert.Empty(_sent);
        Assert.True(_scheduler.IsIdle(1));
    }

    [Fact]
    public void CharactersAreSavedInIdOrder()
    {
        foreach (var id in new long[] { 30, 10, 20 })
        {
            _scheduler.Track(id, version: 1);
            _scheduler.MarkDirty(id, DirtyFlags.Class2);
        }

        AdvanceAndTick(Class2Ms);
        Assert.Equal([10L, 20L, 30L], _sent.Select(j => j.CharacterId));
    }

    [Fact]
    public void OnlyOneSavePerCharacterIsInFlight()
    {
        _scheduler.Track(1, version: 1);
        _scheduler.MarkDirty(1, DirtyFlags.Class2);
        AdvanceAndTick(Class2Ms);
        var first = Assert.Single(_sent);

        _world.Change(1);
        _scheduler.MarkDirty(1, DirtyFlags.Class2);
        AdvanceAndTick(Class2Ms);
        Assert.Single(_sent); // still waiting for the first

        Complete(first, version: 2);
        _scheduler.Tick();
        Assert.Equal(2, _sent.Count);
        Assert.Equal(2, _sent[1].ExpectedVersion); // chained on the version the first save wrote
        Assert.Equal(_world.StateOf(1), _sent[1].State);
    }

    [Fact]
    public void EventSavesDoNotWaitForTheCadence()
    {
        _scheduler.Track(1, version: 1);
        _scheduler.MarkDirty(1, DirtyFlags.Volatile);
        _scheduler.RequestSave(1, SaveReason.Logout);

        _scheduler.Tick();
        Assert.Equal(SaveReason.Logout, Assert.Single(_sent).Reason);
    }

    [Fact]
    public void BarrierFlushesPendingClass2ChangesFirst()
    {
        _scheduler.Track(1, version: 1);
        _scheduler.MarkDirty(1, DirtyFlags.Class2);

        Assert.False(_scheduler.RequestBarrier(1));
        var job = Assert.Single(_sent); // issued at once, no tick needed
        Assert.Equal(SaveReason.Barrier, job.Reason);
        Assert.False(_scheduler.RequestBarrier(1)); // still in flight

        Complete(job);
        _scheduler.Tick();
        Assert.True(_scheduler.RequestBarrier(1));
    }

    [Fact]
    public void BarrierIgnoresVolatileChanges()
    {
        _scheduler.Track(1, version: 1);
        _scheduler.MarkDirty(1, DirtyFlags.Volatile);
        Assert.True(_scheduler.RequestBarrier(1));
        Assert.Empty(_sent);
    }

    [Fact]
    public void FailedSaveIsRetriedAsTheSameJobOnTheNextBatch()
    {
        _scheduler.Track(1, version: 1);
        _scheduler.MarkDirty(1, DirtyFlags.Class2);
        AdvanceAndTick(Class2Ms);
        var first = Assert.Single(_sent);

        Complete(first, SaveOutcome.Failed);
        _world.Change(1);
        _scheduler.MarkDirty(1, DirtyFlags.Class2);
        AdvanceAndTick(50);
        Assert.Single(_sent); // no retry every tick

        AdvanceAndTick(Class2Ms);
        Assert.Equal(2, _sent.Count);
        Assert.Equal(first, _sent[1]); // identical: same save ID, state and expected version

        Complete(_sent[1], version: 2);
        AdvanceAndTick(Class2Ms);
        Assert.Equal(3, _sent.Count); // the newer change follows
        Assert.Equal(2, _sent[2].ExpectedVersion);
    }

    [Theory]
    [InlineData(SaveOutcome.VersionConflict)]
    [InlineData(SaveOutcome.NotFound)]
    [InlineData(SaveOutcome.Deleted)]
    public void AnotherWriterFaultsTheCharacterAndStopsItsSaves(SaveOutcome outcome)
    {
        _scheduler.Track(1, version: 1);
        _scheduler.MarkDirty(1, DirtyFlags.Class2);
        AdvanceAndTick(Class2Ms);
        Complete(_sent[0], outcome, version: 7);

        _scheduler.MarkDirty(1, DirtyFlags.Class2);
        AdvanceAndTick(Class2Ms);

        Assert.Single(_sent);
        Assert.True(_scheduler.IsFaulted(1));
        Assert.Equal(1, _scheduler.VersionOf(1));
        Assert.Equal([(1L, outcome)], _faults);
        Assert.False(_scheduler.RequestBarrier(1));
    }

    [Fact]
    public void StaleAndRepeatedCompletionsAreIgnored()
    {
        _scheduler.Track(1, version: 1);
        _scheduler.MarkDirty(1, DirtyFlags.Class2);
        AdvanceAndTick(Class2Ms);
        var job = _sent[0];

        _completions.Enqueue(new SaveCompletion(1, Guid.NewGuid(), SaveOutcome.VersionConflict, 9));
        _completions.Enqueue(new SaveCompletion(99, job.SaveId, SaveOutcome.Saved, 9));
        _scheduler.Tick();
        Assert.False(_scheduler.IsFaulted(1));
        Assert.False(_scheduler.IsIdle(1));

        Complete(job, version: 2);
        Complete(job, SaveOutcome.VersionConflict, version: 3); // a duplicate after the real one
        _scheduler.Tick();
        Assert.Equal(2, _scheduler.VersionOf(1));
        Assert.False(_scheduler.IsFaulted(1));
    }

    [Fact]
    public void UntrackWaitsForTheLogoutSave()
    {
        _scheduler.Track(1, version: 1);
        _scheduler.MarkDirty(1, DirtyFlags.Volatile);
        Assert.False(_scheduler.TryUntrack(1));

        _scheduler.RequestSave(1, SaveReason.Logout);
        _scheduler.Tick();
        Assert.False(_scheduler.TryUntrack(1));

        Complete(_sent[0]);
        _scheduler.Tick();
        Assert.True(_scheduler.TryUntrack(1));
        Assert.Equal(0, _scheduler.TrackedCount);
    }

    private sealed class FakeWorld : ICharacterStateSource
    {
        private readonly Dictionary<long, int> _changes = new();

        public void Change(long id) => _changes[id] = _changes.GetValueOrDefault(id) + 1;

        public CharacterState StateOf(long id) =>
            new(1, _changes.GetValueOrDefault(id), "main", (int)id * 100, 0, 0, 0, 100, 50, 100);

        public CharacterState Capture(long characterId) => StateOf(characterId);
    }

    private sealed class Sink(List<SaveJob> sent) : ISaveJobSink
    {
        public void Enqueue(in SaveJob job) => sent.Add(job);
    }

    private sealed class Completions(Queue<SaveCompletion> queue) : ISaveCompletionSource
    {
        public bool TryDequeue(out SaveCompletion completion) => queue.TryDequeue(out completion);
    }
}
