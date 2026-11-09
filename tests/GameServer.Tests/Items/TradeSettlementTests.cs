using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Tests.Items;

/// <summary>
/// A swap saving while a side leaves (WP-M4-4 review fix 1): <see cref="TradeService"/>, <see cref="InventoryService"/> and the
/// real <see cref="SaveScheduler"/> together, the session's part played as <c>SessionManager</c> plays it (a fault or a lost
/// session forgets the character's save, detaches its items and tells the trade). Dropped before the pair job was sent: the
/// partner is faulted too and the trade goes away with nothing saved. Dropped after: the pair commits as one and the trade
/// finishes for the side still there (its confirm answered, <see cref="TradeCompleted"/> published).
/// </summary>
[Trait("Category", "NoDuplication")]
public sealed class TradeSettlementTests
{
    private const long A = 1, B = 2;
    private const uint EntityA = 101, EntityB = 102;
    private const byte Normal = 1;
    private const int Class2Ms = 1_000;

    private static readonly ItemRules Items = ItemRules.From(TestCombat.Tables())!;

    private static readonly TradeSettings Settings = new()
    {
        RangeCm = 500, MaxSlots = 12, MaxGoldPerTrade = 1_000, InviteTimeoutMs = 30_000, LockTimeoutMs = 300_000,
        ConfirmTimeoutMs = 60_000, CancelCooldownMs = 5_000, OwnerHistoryMax = 16, StateResendMs = 1_000,
    };

    private readonly FakeClock _clock = new();
    private readonly EventBus _events;
    private readonly List<TradeCompleted> _completed = [];
    private readonly InventoryService _inventory = new(Items, null) { MaxGold = 10_000 };
    private readonly Sink _sink = new();
    private readonly Queue<SaveCompletion> _completions = new();
    private readonly SaveScheduler _saves;
    private readonly World _world;
    private readonly TradeService _trades;
    private uint _request;

    public TradeSettlementTests()
    {
        _events = new EventBus(1, _clock, TestPipelineSettings.BusLimits());
        _events.Subscribe<TradeCompleted>(new Recorder(_completed));
        var values = new StartingValuesM1 { SaveClass2IntervalMs = Class2Ms, VolatileSaveIntervalMs = 10_000 };
        _saves = new SaveScheduler(values, _clock, new States(), _sink, new Completions(_completions), (id, _) => Drop(id),
            inventory: _inventory);
        _inventory.MarkClass2 = id => _saves.MarkDirty(id, DirtyFlags.Class2);
        foreach (long id in new[] { A, B })
        {
            Assert.True(_inventory.Attach(id, new InventoryLoad([new ItemStackRow(Normal, 0, "m1.demonAsh", 0, false, 10)], [], 0, true),
                "warrior", 20));
            _inventory.AttachGold(id, 100);
            _saves.Track(id, version: 10 * id);
        }
        _world = new World(this);
        _trades = new TradeService(_inventory, _world, Settings, _events, "r");
    }

    /// <summary>What <c>SessionManager.LoseOwnership</c> and <c>GameWorld.RemovePlayer</c> do for a character.</summary>
    private void Drop(long id)
    {
        if (!_inventory.IsAttached(id))
            return;
        _saves.Forget(id);
        _inventory.Detach(id);
        _trades.Detached(id);
        _world.Gone.Add(id);
    }

    private void Do(long who, TradeAction action, byte container = 0, ushort slot = 0, ushort count = 0, long gold = 0)
    {
        var state = _trades.Outbox.LastOrDefault(m => m.CharacterId == who && m.State != null).State;
        _trades.Handle(who, ++_request, action, who == A ? EntityB : EntityA, container, slot, count, 0, gold, state?.Revision ?? 0);
    }

    /// <summary>A offers 4 ash for B's 30 gold; both confirm: the swap is applied and the pair linked.</summary>
    private void Confirmed()
    {
        Do(A, TradeAction.Invite);
        Do(B, TradeAction.Accept);
        Do(A, TradeAction.Add, Normal, 0, 4);
        Do(B, TradeAction.SetGold, gold: 30);
        Do(A, TradeAction.Lock);
        Do(B, TradeAction.Lock);
        Do(A, TradeAction.Confirm);
        Do(B, TradeAction.Confirm);
        Assert.True(_saves.IsLinked(A) && _saves.IsLinked(B));
    }

    private void Tick()
    {
        _saves.Tick();
        _trades.Outbox.Clear();
        _trades.Tick();
        _events.DispatchQueued();
    }

    [Fact]
    public void A_side_dropped_before_the_pair_was_sent_drops_the_trade_with_nothing_saved()
    {
        // A has its own save in flight, so the pair waits for it.
        _inventory.TryAdd(A, Item("m1.demonAsh"), 0, 1, "loot", InventoryReason.LootPickup, "t");
        _clock.Advance(Class2Ms);
        _saves.Tick();
        Assert.Single(_sink.Singles);
        Confirmed();

        Drop(A); // A's session lost: B holds half a swap that can never commit alone

        Assert.False(_inventory.IsAttached(B)); // faulted (TransferAborted) and dropped like A
        Tick();
        Assert.Empty(_sink.Pairs);
        Assert.Equal(0, _trades.OpenCount);
        Assert.Empty(_completed);
        Assert.False(_trades.IsTrading(A) || _trades.IsTrading(B));
    }

    [Fact]
    public void A_side_dropped_after_the_pair_was_sent_still_finishes_the_trade_for_the_other()
    {
        Confirmed();
        Tick();
        var pair = Assert.Single(_sink.Pairs);

        Drop(A); // A's session lost while the pair is in flight
        Assert.True(_inventory.IsAttached(B));
        Assert.False(_trades.IsTrading(A)); // free to trade again after a new login
        _completions.Enqueue(new SaveCompletion(A, pair.A.SaveId, SaveOutcome.Saved, pair.A.ExpectedVersion + 1));
        _completions.Enqueue(new SaveCompletion(B, pair.B.SaveId, SaveOutcome.Saved, pair.B.ExpectedVersion + 1));
        Tick();

        var completed = Assert.Single(_completed);
        Assert.Equal((A, B, 30L), (completed.CharacterA, completed.CharacterB, completed.GoldBtoA));
        Assert.Contains(_trades.Outbox, m => m.CharacterId == B && m.Result is { Action: TradeAction.Confirm, Reason: TradeReason.None });
        Assert.Contains(_trades.Outbox, m => m.CharacterId == B && m.State is { Phase: TradePhase.Done });
        Assert.Equal(0, _trades.OpenCount);
        Assert.Equal(70L, _inventory.CommittedGold(B));
    }

    [Fact]
    public void A_pair_that_faults_drops_both_and_the_trade()
    {
        Confirmed();
        Tick();
        var pair = Assert.Single(_sink.Pairs);

        _completions.Enqueue(new SaveCompletion(A, pair.A.SaveId, SaveOutcome.VersionConflict, 0));
        _completions.Enqueue(new SaveCompletion(B, pair.B.SaveId, SaveOutcome.VersionConflict, 0));
        Tick();
        Tick();

        Assert.False(_inventory.IsAttached(A) || _inventory.IsAttached(B));
        Assert.Equal(0, _trades.OpenCount);
        Assert.Empty(_completed);
    }

    private static ItemDef Item(string id)
    {
        Assert.True(Items.TryGetItem(id, out var item));
        return item;
    }

    private sealed class World(TradeSettlementTests test) : ITradeWorld
    {
        public HashSet<long> Gone { get; } = [];

        public long NowMs => test._clock.UtcNowMs;

        public bool TryGetCharacter(long characterId, out TradeParticipant participant)
        {
            participant = new TradeParticipant(characterId, characterId == A ? EntityA : EntityB, 0, 0, true, false, !Gone.Contains(characterId));
            return !Gone.Contains(characterId);
        }

        public bool TryGetEntity(uint entityId, long observerCharacterId, out TradeParticipant participant) =>
            TryGetCharacter(entityId == EntityA ? A : B, out participant);

        public string NameFor(long characterId, long observerCharacterId) => $"c{characterId}";

        public bool RequestTransferSave(TransferRecord transfer) => test._saves.RequestTransferSave(transfer);

        public void AbortTransferSave(TransferRecord transfer) => test._saves.AbortTransfer(transfer);
    }

    private sealed class States : ICharacterStateSource
    {
        public CharacterState Capture(long characterId) => new(1, 0, "main", 0, 0, 0, 0, 100, 50, 100);
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

    private sealed class Recorder(List<TradeCompleted> into) : IEventConsumer<TradeCompleted>
    {
        public void Handle(in EventEnvelope envelope, in TradeCompleted payload) => into.Add(payload);
    }
}
