using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data.Tables;

namespace GameServer.Tests.Items;

/// <summary>
/// Inventory changes ride on the class 2 saves (R-7.1-01, R-7.1-02): each save takes the pending batch, a retry resends the
/// identical batch, the player is told only after the commit, a fault drops the unsaved changes with the character.
/// </summary>
public sealed class InventorySaveSchedulingTests
{
    private const long Character = 7;

    private readonly FakeClock _clock = new();
    private readonly List<SaveJob> _sent = [];
    private readonly Queue<SaveCompletion> _completions = new();
    private readonly List<long> _committed = [];
    private readonly InventoryService _inventory;
    private readonly SaveScheduler _scheduler;
    private readonly ItemDef _horn;

    public InventorySaveSchedulingTests()
    {
        var items = ItemRules.From(TestCombat.Tables())!;
        Assert.True(items.TryGetItem("m1.demonHorn", out _horn));
        _inventory = new InventoryService(items, events: null) { ContentsCommitted = id => _committed.Add(id) };
        var values = new StartingValuesM1 { SaveClass2IntervalMs = 1_000, VolatileSaveIntervalMs = 10_000 };
        _scheduler = new SaveScheduler(values, _clock, new States(), new Sink(_sent), new Completions(_completions),
            inventory: _inventory);
        _inventory.MarkClass2 = id => _scheduler.MarkDirty(id, DirtyFlags.Class2);
        _scheduler.Track(Character, version: 1);
        Assert.True(_inventory.Attach(Character, []));
    }

    private void Batch()
    {
        _clock.Advance(1_000);
        _scheduler.Tick();
    }

    private void Complete(SaveJob job, SaveOutcome outcome = SaveOutcome.Saved)
    {
        _completions.Enqueue(new SaveCompletion(job.CharacterId, job.SaveId, outcome, job.ExpectedVersion + 1));
        _scheduler.Tick();
    }

    [Fact]
    public void A_pickup_marks_the_character_and_the_next_batch_carries_it()
    {
        _inventory.TryAdd(Character, _horn, 0, 2, "loot:a:p0", InventoryReason.LootPickup, "loot:a");
        Assert.False(_scheduler.IsIdle(Character));

        Batch();

        var job = Assert.Single(_sent);
        Assert.Equal(SaveReason.Class2Batch, job.Reason);
        Assert.Equal("loot:a:p0", Assert.Single(job.Inventory!.Ops).OpKey);
        Assert.Equal(new SlotState(1, 0, "m1.demonHorn", 0, false, 2), Assert.Single(job.Inventory.Slots));
        Assert.Empty(_committed);

        Complete(job);
        Assert.Equal(new[] { Character }, _committed);
        Assert.Equal(2, _inventory.Committed(Character)[0].Count);
        Assert.True(_scheduler.IsIdle(Character));
    }

    [Fact]
    public void A_failed_save_is_retried_with_the_identical_batch_and_newer_changes_wait_for_the_next()
    {
        _inventory.TryAdd(Character, _horn, 0, 2, "loot:a:p0", InventoryReason.LootPickup, "loot:a");
        Batch();
        var first = Assert.Single(_sent);
        Complete(first, SaveOutcome.Failed);
        _inventory.TryAdd(Character, _horn, 0, 1, "loot:b:p0", InventoryReason.LootPickup, "loot:b");

        Batch();

        Assert.Equal(2, _sent.Count);
        Assert.Same(first.Inventory, _sent[1].Inventory); // the same job, batch included
        Assert.Empty(_committed);
        Complete(_sent[1]);
        Assert.Equal(2, _inventory.Committed(Character)[0].Count); // what that save wrote, not the newer pickup

        Batch();
        Assert.Equal("loot:b:p0", Assert.Single(_sent[2].Inventory!.Ops).OpKey);
        Complete(_sent[2]);
        Assert.Equal(3, _inventory.Committed(Character)[0].Count);
    }

    [Fact]
    public void A_save_without_inventory_changes_carries_no_batch_and_stale_completions_change_nothing()
    {
        _scheduler.MarkDirty(Character, DirtyFlags.Class2);
        Batch();
        Assert.Null(Assert.Single(_sent).Inventory);

        _inventory.Committed(Character, Guid.NewGuid()); // unknown save id: ignored
        Assert.Empty(_committed);
    }

    [Fact]
    public void A_fault_leaves_the_changes_unsaved_and_unconfirmed()
    {
        var faults = new List<long>();
        var scheduler = new SaveScheduler(new StartingValuesM1 { SaveClass2IntervalMs = 1_000, VolatileSaveIntervalMs = 10_000 },
            _clock, new States(), new Sink(_sent), new Completions(_completions), (id, _) => faults.Add(id), _inventory);
        _inventory.MarkClass2 = id => scheduler.MarkDirty(id, DirtyFlags.Class2);
        scheduler.Track(Character, version: 1);
        _inventory.TryAdd(Character, _horn, 0, 2, "loot:a:p0", InventoryReason.LootPickup, "loot:a");
        _clock.Advance(1_000);
        scheduler.Tick();

        _completions.Enqueue(new SaveCompletion(Character, _sent[^1].SaveId, SaveOutcome.VersionConflict, 9));
        scheduler.Tick();

        Assert.Equal(new[] { Character }, faults);
        Assert.Empty(_committed);
        Assert.True(_inventory.Committed(Character).All(s => s.IsEmpty));
    }

    private sealed class States : ICharacterStateSource
    {
        public CharacterState Capture(long characterId) => new(1, 0, "main", 0, 0, 0, 0, 1, 1, 1);
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
