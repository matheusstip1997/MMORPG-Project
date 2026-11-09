using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data.Tables;

namespace GameServer.Tests.Persistence;

/// <summary>
/// The two-character save of a transfer (WP-M4-4, spec 4.5, 7.1 R-7.1-02 over two characters): <see cref="SaveScheduler"/>
/// holds both characters' save slots, never saves a linked character alone, issues one pair job once neither has a save in
/// flight (both pending batches inside: the barrier by inclusion), resends the identical job after an unknown outcome and
/// faults both on any other failure.
/// </summary>
[Trait("Category", "NoDuplication")]
public sealed class SaveSchedulerTransferTests
{
    private const int Class2Ms = 1_000;

    private readonly FakeClock _clock = new();
    private readonly States _states = new();
    private readonly Sink _sink = new();
    private readonly Queue<SaveCompletion> _completions = new();
    private readonly List<(long Id, SaveOutcome Outcome)> _faults = [];
    private readonly Batches _batches = new();
    private readonly SaveScheduler _scheduler;

    public SaveSchedulerTransferTests()
    {
        var values = new StartingValuesM1 { SaveClass2IntervalMs = Class2Ms, VolatileSaveIntervalMs = 10_000 };
        _scheduler = new SaveScheduler(values, _clock, _states, _sink, new Completions(_completions),
            (id, outcome) => _faults.Add((id, outcome)), inventory: _batches);
        _scheduler.Track(1, version: 10);
        _scheduler.Track(2, version: 20);
    }

    private static TransferRecord Transfer(string key = "trade:r:1") => TransferRecord.Create(key, TransferKind.Trade, 2, 1, "{}");

    private void Tick(long ms = 0)
    {
        _clock.Advance(ms);
        _scheduler.Tick();
    }

    private void Complete(SaveJob job, SaveOutcome outcome, long? version = null) =>
        _completions.Enqueue(new SaveCompletion(job.CharacterId, job.SaveId, outcome, version ?? job.ExpectedVersion + 1));

    private void CompletePair(TransferSaveJob pair, SaveOutcome outcome)
    {
        Complete(pair.A, outcome);
        Complete(pair.B, outcome);
    }

    [Fact]
    public void A_transfer_saves_both_characters_in_one_job_with_their_pending_batches()
    {
        _batches.Pending.Add(1); // a class 2 pickup of character 1 not saved yet
        _scheduler.MarkDirty(1, DirtyFlags.Class2);
        _batches.Pending.Add(2);

        Assert.True(_scheduler.RequestTransferSave(Transfer()));
        Tick();

        Assert.Empty(_sink.Singles);
        var pair = Assert.Single(_sink.Pairs);
        Assert.Equal((1, 2), (pair.A.CharacterId, pair.B.CharacterId));
        Assert.Equal((10, 20), (pair.A.ExpectedVersion, pair.B.ExpectedVersion));
        Assert.Equal(SaveReason.Transfer, pair.A.Reason);
        Assert.NotNull(pair.A.Inventory); // both pending batches ride the pair (R-7.1-02 by inclusion)
        Assert.NotNull(pair.B.Inventory);
        Assert.Equal("trade:r:1", pair.Transfer.OpKey);

        CompletePair(pair, SaveOutcome.Saved);
        Tick();
        Assert.Equal((11, 21), (_scheduler.VersionOf(1), _scheduler.VersionOf(2)));
        Assert.Equal(new long[] { 1, 2 }, _batches.Done.Select(c => c.Id).Order());
        Assert.False(_scheduler.IsLinked(1));
        Assert.True(_scheduler.IsIdle(1) && _scheduler.IsIdle(2));
    }

    [Fact]
    public void A_linked_character_is_never_saved_alone()
    {
        Assert.True(_scheduler.RequestTransferSave(Transfer()));
        _scheduler.RequestSave(1, SaveReason.Logout);
        _scheduler.MarkDirty(1, DirtyFlags.Class2 | DirtyFlags.Volatile);

        Assert.False(_scheduler.RequestBarrier(1)); // a class 1 operation of either waits for the pair
        Tick(Class2Ms);

        Assert.Empty(_sink.Singles);
        Assert.Single(_sink.Pairs);
        Assert.False(_scheduler.TryUntrack(1)); // a logout waits for the pair
    }

    [Fact]
    public void The_pair_waits_until_neither_side_has_a_save_in_flight()
    {
        _scheduler.MarkDirty(2, DirtyFlags.Class2);
        Tick(Class2Ms);
        var single = Assert.Single(_sink.Singles);
        Assert.Equal(2, single.CharacterId);

        Assert.True(_scheduler.RequestTransferSave(Transfer()));
        Tick();
        Assert.Empty(_sink.Pairs);

        Complete(single, SaveOutcome.Saved);
        Tick();
        var pair = Assert.Single(_sink.Pairs);
        Assert.Equal(21, pair.B.ExpectedVersion); // after the save that was in flight
    }

    [Fact]
    public void An_unknown_outcome_resends_the_identical_pair_once()
    {
        Assert.True(_scheduler.RequestTransferSave(Transfer()));
        Tick();
        var pair = Assert.Single(_sink.Pairs);

        CompletePair(pair, SaveOutcome.Failed);
        Tick();
        Assert.Single(_sink.Pairs); // retried on the class 2 cadence, not every tick
        Tick(Class2Ms);

        Assert.Equal(2, _sink.Pairs.Count);
        Assert.Equal(pair, _sink.Pairs[1]); // same save ids: a pair that did commit is recognised
        Assert.Empty(_sink.Singles);
        CompletePair(pair, SaveOutcome.Saved);
        Tick();
        Assert.False(_scheduler.IsLinked(1) || _scheduler.IsLinked(2));
    }

    [Theory]
    [InlineData(SaveOutcome.VersionConflict)]
    [InlineData(SaveOutcome.DuplicateOperation)]
    [InlineData(SaveOutcome.Deleted)]
    public void Any_other_outcome_faults_both(SaveOutcome outcome)
    {
        Assert.True(_scheduler.RequestTransferSave(Transfer()));
        Tick();
        CompletePair(_sink.Pairs[0], outcome);
        Tick();

        Assert.True(_scheduler.IsFaulted(1));
        Assert.True(_scheduler.IsFaulted(2));
        Assert.Equal(new long[] { 1, 2 }, _faults.Select(f => f.Id).Order());
        Assert.Empty(_batches.Done);
    }

    [Fact]
    public void A_side_faulted_before_the_pair_was_sent_faults_its_partner()
    {
        _scheduler.MarkDirty(1, DirtyFlags.Class2);
        Tick(Class2Ms);
        var single = Assert.Single(_sink.Singles); // character 1 saving alone, the transfer applied meanwhile
        Assert.True(_scheduler.RequestTransferSave(Transfer()));

        Complete(single, SaveOutcome.VersionConflict);
        Tick();

        Assert.Contains((2L, SaveOutcome.TransferAborted), _faults);
        Assert.True(_scheduler.IsFaulted(2));
        Assert.Empty(_sink.Pairs);
    }

    [Fact]
    public void A_side_forgotten_before_the_pair_was_sent_faults_its_partner()
    {
        _scheduler.MarkDirty(1, DirtyFlags.Class2);
        Tick(Class2Ms);
        Assert.True(_scheduler.RequestTransferSave(Transfer()));

        _scheduler.Forget(1); // ownership lost

        Assert.Equal([(2L, SaveOutcome.TransferAborted)], _faults);
    }

    [Fact]
    public void A_side_forgotten_after_the_pair_was_sent_leaves_the_partner_to_the_pair_outcome()
    {
        Assert.True(_scheduler.RequestTransferSave(Transfer()));
        Tick();
        var pair = _sink.Pairs[0];

        _scheduler.Forget(1);
        CompletePair(pair, SaveOutcome.Failed);
        Tick(Class2Ms);

        Assert.Empty(_faults);
        Assert.Equal(2, _sink.Pairs.Count); // the remaining side resends the identical pair
        Assert.Equal(pair, _sink.Pairs[1]);
        CompletePair(pair, SaveOutcome.Saved);
        Tick();
        Assert.Equal(21, _scheduler.VersionOf(2));
    }

    [Fact]
    public void A_second_transfer_cannot_link_a_character_already_linked_or_faulted()
    {
        _scheduler.Track(3, version: 30);
        Assert.True(_scheduler.RequestTransferSave(Transfer()));
        Assert.False(_scheduler.RequestTransferSave(TransferRecord.Create("trade:r:2", TransferKind.Trade, 1, 3, "{}")));
        Assert.False(_scheduler.RequestTransferSave(TransferRecord.Create("trade:r:3", TransferKind.Trade, 3, 99, "{}"))); // untracked
    }

    [Fact]
    public void An_aborted_link_lets_both_save_alone_again_and_a_sent_pair_cannot_be_aborted()
    {
        Assert.True(_scheduler.RequestTransferSave(Transfer()));
        Assert.True(_scheduler.AbortTransfer(Transfer()));
        Assert.False(_scheduler.IsLinked(1) || _scheduler.IsLinked(2));
        _scheduler.MarkDirty(1, DirtyFlags.Class2);
        Tick(Class2Ms);
        Assert.Contains(_sink.Singles, j => j.CharacterId == 1);
        Assert.Empty(_sink.Pairs);

        Assert.True(_scheduler.RequestTransferSave(Transfer("trade:r:2")));
        foreach (var single in _sink.Singles)
            Complete(single, SaveOutcome.Saved); // the pair waits until neither side has a save in flight
        Tick();
        Assert.Single(_sink.Pairs);
        Assert.False(_scheduler.AbortTransfer(Transfer("trade:r:2"))); // already sent: it commits or fails as one
    }

    private sealed class States : ICharacterStateSource
    {
        public CharacterState Capture(long characterId) => new(1, 0, "main", (int)characterId * 100, 0, 0, 0, 100, 50, 100);
    }

    private sealed class Sink : ISaveJobSink, ITransferJobSink
    {
        public List<SaveJob> Singles { get; } = [];
        public List<TransferSaveJob> Pairs { get; } = [];

        public void Enqueue(in SaveJob job) => Singles.Add(job);

        public void EnqueueTransfer(in TransferSaveJob job) => Pairs.Add(job);
    }

    private sealed class Completions(Queue<SaveCompletion> queue) : ISaveCompletionSource
    {
        public bool TryDequeue(out SaveCompletion completion) => queue.TryDequeue(out completion);
    }

    /// <summary>An inventory batch source: a character in <see cref="Pending"/> has one pending operation.</summary>
    private sealed class Batches : IInventoryBatchSource
    {
        public HashSet<long> Pending { get; } = [];
        public List<(long Id, Guid SaveId)> Done { get; } = [];

        public InventoryBatch? TakeBatch(long characterId, Guid saveId) => Pending.Remove(characterId)
            ? new InventoryBatch([new InventoryOpRecord($"op:{characterId}", "LootPickup", "test")], [], [])
            : null;

        public void Committed(long characterId, Guid saveId) => Done.Add((characterId, saveId));
    }
}
