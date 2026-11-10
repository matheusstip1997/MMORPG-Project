using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Tests.Items;

/// <summary>
/// The trade window's rules (spec 4.5 "M4 trade window", M4 decision 5, R-4.1-04; WP-M4-4) with a fake world: invite, accept,
/// decline; offers reserved and refused by their flags, each with its own reason; a change clears both locks; lock and confirm at
/// the current revision only; the swap re-validated, applied at once and answered only after the commit; cancellation on
/// distance, death, combat, disconnect and timeouts; one trade per player and the cooldown.
/// </summary>
[Trait("Category", "NoDuplication")]
public sealed class TradeServiceTests
{
    private const long A = 1, B = 2, C = 3;
    private const uint EntityA = 101, EntityB = 102, EntityC = 103;
    private const byte Normal = 1, Safe = 2, Quest = 4;
    private const string Ash = "m1.demonAsh", Horn = "m1.demonHorn", Sword = "m2.trainingSword", Sigil = "m2.questDemonSigil";

    private static readonly ItemRules Items = ItemRules.From(TestCombat.Tables())!;

    private static readonly TradeSettings Settings = new()
    {
        RangeCm = 500, MaxSlots = 3, MaxGoldPerTrade = 1_000, InviteTimeoutMs = 30_000, LockTimeoutMs = 300_000,
        ConfirmTimeoutMs = 60_000, CancelCooldownMs = 5_000, OwnerHistoryMax = 16, StateResendMs = 1_000,
    };

    private readonly EventBus _events = new(1, new FakeClock(), TestPipelineSettings.BusLimits());
    private readonly List<TradeCompleted> _completed = [];
    private readonly List<TradeCancelled> _cancelled = [];
    private readonly InventoryService _inventory = new(Items, null) { MaxGold = 10_000 };
    private readonly FakeTradeWorld _world = new();
    private readonly TradeService _trades;
    private uint _request;

    public TradeServiceTests()
    {
        _events.Subscribe<TradeCompleted>(new Sink<TradeCompleted>(_completed));
        _events.Subscribe<TradeCancelled>(new Sink<TradeCancelled>(_cancelled));
        Attach(A, [new ItemStackRow(Normal, 0, Ash, 0, false, 10), new ItemStackRow(Normal, 1, Horn, 0, false, 5),
            new ItemStackRow(Normal, 2, Ash, 0, true, 3), new ItemStackRow(Quest, 0, Sigil, 0, false, 1)],
            [new ItemInstanceRow(ItemInstanceIds.Make(A, 1), Safe, 0, Sword, 0, false, false, ItemInstanceIds.NewProperties(A)),
             new ItemInstanceRow(ItemInstanceIds.Make(A, 2), Safe, 1, Sword, 0, false, true, ItemInstanceIds.NewProperties(A))], 100);
        Attach(B, [], [], 500);
        Attach(C, [], [], 0);
        _world.Add(A, EntityA, 0, 0);
        _world.Add(B, EntityB, 300, 0);
        _world.Add(C, EntityC, 0, 300);
        _trades = new TradeService(_inventory, _world, Settings, _events, "r");
    }

    private void Attach(long id, ItemStackRow[] stacks, ItemInstanceRow[] instances, long gold)
    {
        Assert.True(_inventory.Attach(id, new InventoryLoad(stacks, instances, 10, true), "warrior", 20));
        _inventory.AttachGold(id, gold);
    }

    private TradeReason Do(long who, TradeAction action, uint target = 0, byte container = 0, ushort slot = 0, ushort count = 0,
        byte index = 0, long gold = 0, uint? revision = null)
    {
        uint seen = revision ?? Revision(who);
        _trades.Outbox.Clear();
        uint id = ++_request;
        _trades.Handle(who, id, action, target, container, slot, count, index, gold, seen);
        var answer = _trades.Outbox.LastOrDefault(m => m.CharacterId == who && m.Result?.RequestId == id);
        return answer.Result?.Reason ?? (TradeReason)255; // 255: held (a confirm waits for the commit)
    }

    private uint _lastRevision;

    private uint Revision(long who)
    {
        var state = States(who).LastOrDefault();
        if (state != null)
            _lastRevision = state.Revision;
        return _lastRevision;
    }

    private IEnumerable<TradeState> States(long who) => _trades.Outbox.Where(m => m.CharacterId == who && m.State != null).Select(m => m.State!);

    private void Open()
    {
        Assert.Equal(TradeReason.None, Do(A, TradeAction.Invite, EntityB));
        Assert.Equal(TradeReason.None, Do(B, TradeAction.Accept));
    }

    private void Commit(params long[] ids)
    {
        foreach (long id in ids)
        {
            var save = Guid.NewGuid();
            _inventory.TakeBatch(id, save);
            _inventory.Committed(id, save);
        }
    }

    private void Tick(long ms = 0)
    {
        _world.NowMs += ms;
        _trades.Outbox.Clear();
        _trades.Tick();
        _events.DispatchQueued();
    }

    private int Units(long id, string item) =>
        Enumerable.Range(0, _inventory.CurrentInventory(id).ContainerCount).Select(i => _inventory.CurrentInventory(id).BagAt(i))
            .Sum(bag => Enumerable.Range(0, bag.SlotCount)
                .Where(s => !bag[s].IsEmpty && Items.TryGetItem(bag[s].ItemNetId, out var d) && d.Id == item).Sum(s => bag[s].Count));

    [Fact]
    public void Two_players_trade_an_item_for_gold_answered_only_after_the_commit()
    {
        Open();
        Assert.Equal(TradeReason.None, Do(A, TradeAction.Add, container: Normal, slot: 0, count: 4));
        Assert.Equal(TradeReason.None, Do(B, TradeAction.SetGold, gold: 50));
        Assert.Equal(TradeReason.None, Do(A, TradeAction.Lock));
        Assert.Equal(TradeReason.None, Do(B, TradeAction.Lock));
        Assert.Equal((TradeReason)255, Do(A, TradeAction.Confirm)); // held
        Assert.Equal((TradeReason)255, Do(B, TradeAction.Confirm)); // held: the swap is applied and saving

        Assert.Equal((9, 4), (Units(A, Ash), Units(B, Ash))); // applied at once in memory (A keeps 6 plus its 3 stolen)
        var transfer = Assert.Single(_world.Transfers);
        Assert.Equal(("trade:r:1", A, B), (transfer.OpKey, transfer.CharacterA, transfer.CharacterB));
        Tick();
        Assert.DoesNotContain(_trades.Outbox, m => m.Result != null); // not committed yet
        Assert.True(_trades.IsTrading(A));

        Commit(A, B);
        Tick();

        Assert.Equal(2, _trades.Outbox.Count(m => m.Result is { Action: TradeAction.Confirm, Reason: TradeReason.None }));
        Assert.Contains(_trades.Outbox, m => m.State is { Phase: TradePhase.Done });
        var completed = Assert.Single(_completed);
        Assert.Equal((A, B, 0L, 50L), (completed.CharacterA, completed.CharacterB, completed.GoldAtoB, completed.GoldBtoA));
        Assert.Equal(4, completed.ItemsAtoB.Single().Count);
        Assert.False(_trades.IsTrading(A) || _trades.IsTrading(B));
        Assert.Equal((150L, 450L), (_inventory.CommittedGold(A), _inventory.CommittedGold(B)));
    }

    [Fact]
    public void Offered_units_are_reserved_so_a_sale_or_move_of_them_is_refused()
    {
        Open();
        Assert.Equal(TradeReason.None, Do(A, TradeAction.Add, container: Normal, slot: 0, count: 10));
        Assert.True(_inventory.IsReserved(A, new InventoryLocation(Normal, 0)));
        Assert.False(_inventory.TryRemove(A, Normal, 0, 1, "sell:x", InventoryReason.OutpostSale, "npc"));
        Assert.False(_inventory.TryMove(A, new InventoryLocation(Normal, 0), new InventoryLocation(Normal, 9), "move:x",
            InventoryReason.PlayerMove).Applied);

        Assert.Equal(TradeReason.None, Do(A, TradeAction.Cancel));
        Assert.False(_inventory.IsReserved(A, new InventoryLocation(Normal, 0)));
    }

    [Fact]
    public void Each_refused_unit_has_its_own_reason()
    {
        Open();
        Assert.Equal(TradeReason.TradeSellOnly, Do(A, TradeAction.Add, container: Normal, slot: 1, count: 1));
        Assert.Equal(TradeReason.TradeStolen, Do(A, TradeAction.Add, container: Normal, slot: 2, count: 1));
        Assert.Equal(TradeReason.TradeQuestItem, Do(A, TradeAction.Add, container: Quest, slot: 0, count: 1));
        Assert.Equal(TradeReason.TradeNotTradable, Do(A, TradeAction.Add, container: Safe, slot: 1, count: 1));
        Assert.Equal(TradeReason.None, Do(A, TradeAction.Add, container: Safe, slot: 0, count: 1)); // gear trades
        Assert.False(_inventory.TryMove(A, new InventoryLocation(Safe, 0), new InventoryLocation(5, 0), "equip", InventoryReason.PlayerMove)
            .Applied); // reserved: cannot be equipped while offered
        Assert.Equal(TradeReason.TradeChanged, Do(A, TradeAction.Add, container: Normal, slot: 20, count: 1)); // empty slot

        Assert.Equal(ReserveResult.Ok, _inventory.Reserve(A, "forge:1", [new ReserveItem(new InventoryLocation(Normal, 0), 8)]));
        Assert.Equal(TradeReason.TradeReserved, Do(A, TradeAction.Add, container: Normal, slot: 0, count: 5));
        Assert.Equal(TradeReason.None, Do(A, TradeAction.Add, container: Normal, slot: 0, count: 2)); // the 2 unreserved
        _inventory.Release(A, "forge:1");
        Assert.Equal(TradeReason.None, Do(A, TradeAction.Add, container: Normal, slot: 0, count: 1)); // 3 offers: MaxSlots
        Assert.Equal(TradeReason.TradeSlotsFull, Do(A, TradeAction.Add, container: Normal, slot: 0, count: 1));
    }

    [Fact]
    public void Equipped_gear_is_refused()
    {
        Assert.True(_inventory.TryMove(A, new InventoryLocation(Safe, 0), new InventoryLocation(5, 0), "equip", InventoryReason.PlayerMove).Applied);
        Open();

        Assert.Equal(TradeReason.TradeEquipped, Do(A, TradeAction.Add, container: 5, slot: 0, count: 1));
    }

    [Fact]
    public void A_change_by_either_side_clears_both_locks_and_a_stale_revision_is_refused()
    {
        Open();
        Assert.Equal(TradeReason.None, Do(A, TradeAction.Add, container: Normal, slot: 0, count: 4));
        uint seen = Revision(A);
        Assert.Equal(TradeReason.None, Do(A, TradeAction.Lock));
        Assert.Equal(TradeReason.None, Do(B, TradeAction.Lock));

        Assert.Equal(TradeReason.None, Do(B, TradeAction.SetGold, gold: 10)); // B changes its offer
        var state = States(A).Last();
        Assert.False(state.OwnLocked || state.PartnerLocked);

        Assert.Equal(TradeReason.TradeChanged, Do(A, TradeAction.Lock, revision: seen)); // A locks what it saw before the change
        Assert.Equal(TradeReason.TradeChanged, Do(A, TradeAction.Confirm, revision: seen + 1)); // not both locked
        Assert.Empty(_world.Transfers);
    }

    [Fact]
    public void A_confirm_after_a_unit_was_moved_away_is_refused_trade_changed_and_nothing_moves()
    {
        Open();
        Assert.Equal(TradeReason.None, Do(A, TradeAction.Add, container: Normal, slot: 0, count: 4));
        Assert.Equal(TradeReason.None, Do(B, TradeAction.SetGold, gold: 50));
        Do(A, TradeAction.Lock);
        Do(B, TradeAction.Lock);
        Assert.Equal((TradeReason)255, Do(A, TradeAction.Confirm));
        // The reservation is lost under the trade (e.g. a path that ends reservations) and the units are sold meanwhile.
        _inventory.ReleaseAll(A);
        Assert.True(_inventory.TryRemove(A, Normal, 0, 10, "sell:y", InventoryReason.OutpostSale, "npc"));

        uint rev = Revision(B);
        _trades.Outbox.Clear();
        _trades.Handle(B, 99, TradeAction.Confirm, 0, 0, 0, 0, 0, 0, rev);
        var answers = _trades.Outbox.Where(m => m.Result != null).Select(m => m.Result!).ToList();

        Assert.All(answers, a => Assert.Equal(TradeReason.TradeChanged, a.Reason));
        Assert.Equal(2, answers.Count); // both held confirms answered
        Assert.Empty(_world.Transfers);
        Assert.Equal((500L, 0), (_inventory.Gold(B), Units(B, Ash)));
        Assert.True(_trades.IsTrading(A)); // the window stays open: look again, lock again
    }

    [Fact]
    public void A_bag_that_cannot_take_the_offer_is_refused_at_confirm()
    {
        var bag = _inventory.CurrentInventory(B).BagAt(0);
        for (int slot = 0; slot < bag.SlotCount; slot++)
            _inventory.TryAdd(B, Item(Horn), 0, 99, $"fill:{slot}", InventoryReason.Admin, "t");
        Open();
        Do(A, TradeAction.Add, container: Normal, slot: 0, count: 4);
        Do(A, TradeAction.Lock);
        Do(B, TradeAction.Lock);
        Do(A, TradeAction.Confirm);

        Assert.Equal(TradeReason.TradeBagFull, Do(B, TradeAction.Confirm));
        Assert.Empty(_world.Transfers);
    }

    [Theory]
    [InlineData("far")]
    [InlineData("dead")]
    [InlineData("combat")]
    [InlineData("gone")]
    public void An_open_trade_ends_when_the_players_can_no_longer_trade(string what)
    {
        Open();
        Do(A, TradeAction.Add, container: Normal, slot: 0, count: 4);
        switch (what)
        {
            case "far": _world.Move(B, 700, 0); break;
            case "dead": _world.Set(B, alive: false); break;
            case "combat": _world.Set(A, inCombat: true); break;
            default: _world.Set(B, connected: false); break;
        }

        Tick();

        Assert.False(_trades.IsTrading(A) || _trades.IsTrading(B));
        Assert.False(_inventory.IsReserved(A, new InventoryLocation(Normal, 0)));
        var reason = States(A).Single().Reason;
        Assert.Equal(what switch { "far" => TradeReason.TradeTooFar, "dead" => TradeReason.TradeDead, "combat" => TradeReason.TradeInCombat,
            _ => TradeReason.TradeCancelled }, reason);
        _events.DispatchQueued();
        Assert.Single(_cancelled);
    }

    [Fact]
    public void Invites_and_open_trades_time_out()
    {
        Assert.Equal(TradeReason.None, Do(A, TradeAction.Invite, EntityB));
        Tick(Settings.InviteTimeoutMs);
        Assert.Equal(TradeReason.TradeTimeout, States(B).Single().Reason);

        Tick(Settings.CancelCooldownMs);
        Open();
        Do(A, TradeAction.Lock);
        Do(B, TradeAction.Lock);
        Tick(Settings.ConfirmTimeoutMs - 1);
        Assert.True(_trades.IsTrading(A));
        Tick(1);
        Assert.False(_trades.IsTrading(A));
    }

    [Fact]
    public void One_trade_per_player_self_and_the_cooldown()
    {
        Assert.Equal(TradeReason.TradeSelf, Do(A, TradeAction.Invite, EntityA));
        Assert.Equal(TradeReason.TradeTargetNotFound, Do(A, TradeAction.Invite, 999));
        Assert.Equal(TradeReason.None, Do(A, TradeAction.Invite, EntityB));
        Assert.Equal(TradeReason.TradeBusy, Do(C, TradeAction.Invite, EntityB));
        Assert.Equal(TradeReason.TradeBusy, Do(A, TradeAction.Invite, EntityC));

        Assert.Equal(TradeReason.None, Do(B, TradeAction.Decline));
        Assert.Equal(TradeReason.TradeDeclined, States(A).Single().Reason);
        Assert.Equal(TradeReason.TradeCooldown, Do(C, TradeAction.Invite, EntityA));
        Tick(Settings.CancelCooldownMs);
        Assert.Equal(TradeReason.None, Do(C, TradeAction.Invite, EntityA));
    }

    [Fact]
    public void Gold_offers_are_capped_by_the_settings_and_the_balance()
    {
        Open();
        Assert.Equal(TradeReason.TradeGoldCap, Do(B, TradeAction.SetGold, gold: Settings.MaxGoldPerTrade + 1));
        Assert.Equal(TradeReason.TradeNotEnoughGold, Do(A, TradeAction.SetGold, gold: 101));
        Assert.Equal(TradeReason.None, Do(A, TradeAction.SetGold, gold: 100));
    }

    [Fact]
    public void The_partner_name_comes_from_the_world_for_each_observer()
    {
        Open();
        Assert.Equal("name:2:for:1", States(A).Last().PartnerName);
        Assert.Equal("name:1:for:2", States(B).Last().PartnerName);
    }


    [Fact]
    public void An_invite_to_a_player_outside_the_inviters_interest_is_not_found_and_reveals_nothing()
    {
        _world.Hidden.Add((A, B)); // B is not sent to A (rule 9)

        Assert.Equal(TradeReason.TradeTargetNotFound, Do(A, TradeAction.Invite, EntityB));
        Assert.Empty(States(A));
        Assert.Empty(States(B));
        Assert.False(_trades.IsTrading(B));
    }

    [Fact]
    public void A_save_that_cannot_be_linked_refuses_the_confirm_without_moving_anything()
    {
        Open();
        Do(A, TradeAction.Add, container: Normal, slot: 0, count: 4);
        Do(B, TradeAction.SetGold, gold: 50);
        Do(A, TradeAction.Lock);
        Do(B, TradeAction.Lock);
        Do(A, TradeAction.Confirm);
        _world.LinkRefused = true; // e.g. a side's save faulted meanwhile

        Assert.Equal(TradeReason.TradeUnavailable, Do(B, TradeAction.Confirm)); // no throw on the world thread

        Assert.Equal((13, 0), (Units(A, Ash), Units(B, Ash))); // 10 plus 3 stolen: nothing left A
        Assert.Equal((100L, 500L), (_inventory.Gold(A), _inventory.Gold(B)));
        Assert.True(_inventory.HasReservation(A, $"trade:r:1:{A}")); // still offered, the window open
        Assert.True(_trades.IsTrading(A));
    }

    private static ItemDef Item(string id)
    {
        Assert.True(Items.TryGetItem(id, out var item));
        return item;
    }

    private sealed class Sink<T>(List<T> into) : IEventConsumer<T> where T : struct, IGameEvent
    {
        public void Handle(in EventEnvelope envelope, in T payload) => into.Add(payload);
    }

    private sealed class FakeTradeWorld : ITradeWorld
    {
        private readonly Dictionary<long, TradeParticipant> _players = new();

        public long NowMs { get; set; } = 1_000_000;

        public List<TransferRecord> Transfers { get; } = [];

        public void Add(long id, uint entity, int x, int y) => _players[id] = new TradeParticipant(id, entity, x, y, true, false, true);

        public void Move(long id, int x, int y) => _players[id] = _players[id] with { XCm = x, YCm = y };

        public void Set(long id, bool alive = true, bool inCombat = false, bool connected = true) =>
            _players[id] = _players[id] with { Alive = alive, InCombat = inCombat, Connected = connected };

        public bool TryGetCharacter(long characterId, out TradeParticipant participant) => _players.TryGetValue(characterId, out participant);

        /// <summary>Pairs (observer, target) the observer is not sent (outside its interest).</summary>
        public HashSet<(long Observer, long Target)> Hidden { get; } = [];

        public bool LinkRefused { get; set; }

        public List<TransferRecord> Aborted { get; } = [];

        public bool TryGetEntity(uint entityId, long observerCharacterId, out TradeParticipant participant)
        {
            participant = _players.Values.FirstOrDefault(p => p.EntityId == entityId);
            return participant.CharacterId != 0 && !Hidden.Contains((observerCharacterId, participant.CharacterId));
        }

        public string NameFor(long characterId, long observerCharacterId) => $"name:{characterId}:for:{observerCharacterId}";

        public bool RequestTransferSave(TransferRecord transfer)
        {
            if (LinkRefused)
                return false;
            Transfers.Add(transfer);
            return true;
        }

        public void AbortTransferSave(TransferRecord transfer) => Aborted.Add(transfer);
    }
}
