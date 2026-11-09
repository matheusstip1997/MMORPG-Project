using GameServer.Economy;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Simulation.Items;

namespace GameServer.Tests.Items;

/// <summary>
/// <see cref="InventoryService.TryTransfer"/> and the escrow halves (WP-M4-4, spec 4.5, 7.1): all or nothing, units move (never
/// created or destroyed), an instance keeps its id and properties with the giver in its owner history, gold flows
/// <c>move</c>, and <see cref="ItemTransferred"/> and <see cref="GoldChanged"/> go out only after the commit.
/// </summary>
[Trait("Category", "NoDuplication")]
public sealed class InventoryTransferTests
{
    private const long A = 1, B = 2;
    private const byte Normal = 1, Safe = 2;
    private const string Ash = "m1.demonAsh", Horn = "m1.demonHorn", Sword = "m2.trainingSword";

    private static readonly ItemRules Items = ItemRules.From(TestCombat.Tables())!;

    private readonly EventBus _events = new(1, new FakeClock(), TestPipelineSettings.BusLimits());
    private readonly Recorder<ItemTransferred> _transferred = new();
    private readonly Recorder<ItemCreated> _created = new();
    private readonly Recorder<ItemDestroyed> _destroyed = new();
    private readonly Recorder<GoldChanged> _gold = new();
    private readonly InventoryService _inventory;

    public InventoryTransferTests()
    {
        _events.Subscribe<ItemTransferred>(_transferred);
        _events.Subscribe<ItemCreated>(_created);
        _events.Subscribe<ItemDestroyed>(_destroyed);
        _events.Subscribe<GoldChanged>(_gold);
        _inventory = new InventoryService(Items, _events) { MaxGold = 1_000 };
        Attach(A, [new ItemStackRow(Normal, 0, Ash, 0, false, 10)], [Gear(A, 1, 1)], gold: 100);
        Attach(B, [new ItemStackRow(Normal, 0, Ash, 0, false, 95)], [], gold: 500);
    }

    private static ItemInstanceRow Gear(long owner, long seq, int slot) =>
        new(ItemInstanceIds.Make(owner, seq), Safe, slot, Sword, 2, false, false, ItemInstanceIds.NewProperties(owner));

    private void Attach(long id, ItemStackRow[] stacks, ItemInstanceRow[] instances, long gold)
    {
        Assert.True(_inventory.Attach(id, new InventoryLoad(stacks, instances, 10, true), "warrior", 20));
        _inventory.AttachGold(id, gold);
    }

    private static InventoryLocation At(int slot) => new(Normal, slot);

    private static readonly InventoryLocation Gear1 = new(Safe, 1);

    private void Offer(long id, string key, params ReserveItem[] items) => Assert.Equal(ReserveResult.Ok, _inventory.Reserve(id, key, items));

    private void SaveBoth()
    {
        var saveA = Guid.NewGuid();
        var saveB = Guid.NewGuid();
        _inventory.TakeBatch(A, saveA);
        _inventory.TakeBatch(B, saveB);
        _inventory.Committed(A, saveA);
        _inventory.Committed(B, saveB);
        _events.DispatchQueued();
    }

    private int Units(long id, string item) =>
        Enumerable.Range(0, _inventory.CurrentInventory(id).ContainerCount)
            .Select(i => _inventory.CurrentInventory(id).BagAt(i))
            .Sum(bag => Enumerable.Range(0, bag.SlotCount).Where(s => !bag[s].IsEmpty && Items.TryGetItem(bag[s].ItemNetId, out var d) && d.Id == item)
                .Sum(s => bag[s].Count));

    [Fact]
    public void A_trade_moves_the_sword_and_the_gold_at_once_and_publishes_moves_after_the_commit()
    {
        long sword = ItemInstanceIds.Make(A, 1);
        Offer(A, "trade:a", new ReserveItem(Gear1, 1), new ReserveItem(At(0), 4));

        var refusal = _inventory.TryTransfer(new TransferSide(A, "trade:a", 0), new TransferSide(B, null, 60), "trade:r:1",
            InventoryReason.Trade, 3, out var keys);

        Assert.Equal(TransferRefusal.None, refusal);
        Assert.Equal(("trade:r:1:1:out", "trade:r:1:2:in"), (keys.KeyA, keys.KeyB));
        Assert.Equal((6, 99), (Units(A, Ash), Units(B, Ash))); // 95 + 4 merged into one stack of 99
        Assert.Equal((160L, 440L), (_inventory.Gold(A), _inventory.Gold(B)));
        Assert.True(_inventory.TryGetLatestProperties(B, sword, out var properties)); // the same instance, now B's
        Assert.Equal(new long[] { A }, properties.OwnerHistory);
        Assert.False(_inventory.HasReservation(A, "trade:a"));
        Assert.Empty(_transferred.Items); // nothing before the commit (rule 5)

        SaveBoth();

        Assert.Equal(2, _transferred.Items.Count);
        Assert.All(_transferred.Items, t => Assert.Equal((A, B, InventoryReason.Trade), (t.FromCharacterId, t.ToCharacterId, t.Reason)));
        Assert.Contains(_transferred.Items, t => t.InstanceId == sword && t.Count == 1);
        Assert.Contains(_transferred.Items, t => t.InstanceId == 0 && t.Count == 4);
        Assert.Empty(_created.Items); // moved, never created or destroyed
        Assert.Empty(_destroyed.Items);
        Assert.Equal(new long[] { -60, 60 }, _gold.Items.Select(g => g.Delta).Order());
        Assert.All(_gold.Items, g => Assert.Equal(GoldFlow.Move, EconomyFlows.Of(g.Reason, g.Delta)));
        Assert.True(_inventory.IsCommitted(A, keys.KeyA) && _inventory.IsCommitted(B, keys.KeyB));
    }

    [Fact]
    public void The_batches_carry_the_instance_out_of_one_character_and_into_the_other_with_transfer_audit_kinds()
    {
        long sword = ItemInstanceIds.Make(A, 1);
        Offer(A, "trade:a", new ReserveItem(Gear1, 1));
        Assert.Equal(TransferRefusal.None, _inventory.TryTransfer(new TransferSide(A, "trade:a", 0), new TransferSide(B, null, 0),
            "trade:r:2", InventoryReason.Trade, 3, out _));

        var batchA = _inventory.TakeBatch(A, Guid.NewGuid())!;
        var batchB = _inventory.TakeBatch(B, Guid.NewGuid())!;

        Assert.Contains(batchA.Instances!, i => i.InstanceId == sword && i.Removed);
        Assert.Contains(batchB.Instances!, i => i.InstanceId == sword && !i.Removed && i.PropertiesJson.Contains($"\"ownerHistory\":[{A}]"));
        Assert.All(batchA.Audit, a => Assert.Equal(AuditKind.TransferOut, a.Kind));
        Assert.All(batchB.Audit, a => Assert.Equal(AuditKind.TransferIn, a.Kind));
        Assert.Equal(new[] { nameof(InventoryReason.Trade) }, batchA.Ops.Select(o => o.Reason));
    }

    [Fact]
    public void A_refused_transfer_changes_nothing()
    {
        Offer(A, "trade:a", new ReserveItem(At(0), 10));
        var before = (Units(A, Ash), Units(B, Ash), _inventory.Gold(A), _inventory.Gold(B));

        Assert.Equal(TransferRefusal.GoldShort, _inventory.TryTransfer(new TransferSide(A, "trade:a", 0), new TransferSide(B, null, 501),
            "trade:r:3", InventoryReason.Trade, 3, out _));
        Assert.Equal(TransferRefusal.Changed, _inventory.TryTransfer(new TransferSide(A, "trade:none", 0), new TransferSide(B, null, 0),
            "trade:r:5", InventoryReason.Trade, 3, out _));
        Assert.Equal(TransferRefusal.Empty, _inventory.TryTransfer(new TransferSide(A, null, 0), new TransferSide(B, null, 0),
            "trade:r:6", InventoryReason.Trade, 3, out _));

        Assert.Equal(before, (Units(A, Ash), Units(B, Ash), _inventory.Gold(A), _inventory.Gold(B)));
        Assert.False(_inventory.WasApplied(A, "trade:r:3:1:out"));
        Assert.True(_inventory.HasReservation(A, "trade:a"));
    }

    [Fact]
    public void Gold_that_would_pass_the_cap_is_refused()
    {
        // A holds 100 of a 1,000 cap; B gives 950.
        _inventory.TryAddGold(B, 450, "seed", InventoryReason.Admin, "test");
        var before = (_inventory.Gold(A), _inventory.Gold(B));

        Assert.Equal(TransferRefusal.GoldCap, _inventory.TryTransfer(new TransferSide(A, null, 0), new TransferSide(B, null, 950),
            "trade:r:7", InventoryReason.Trade, 3, out _));
        Assert.Equal(before, (_inventory.Gold(A), _inventory.Gold(B)));
    }

    [Fact]
    public void A_bag_that_cannot_take_everything_refuses_the_whole_transfer()
    {
        // Fill B's Normal bag: every slot holds a full stack of horns except slot 0 (95 ash).
        var bag = _inventory.CurrentInventory(B).BagAt(0);
        for (int slot = 1; slot < bag.SlotCount; slot++)
            _inventory.TryAdd(B, Item(Horn), 0, 99, $"fill:{slot}", InventoryReason.Admin, "test");
        Offer(A, "trade:a", new ReserveItem(At(0), 10)); // 4 fit on B's ash stack, 6 do not

        Assert.Equal(TransferRefusal.BagFull, _inventory.TryTransfer(new TransferSide(A, "trade:a", 0), new TransferSide(B, null, 0),
            "trade:r:8", InventoryReason.Trade, 3, out _));
        Assert.Equal((10, 95), (Units(A, Ash), Units(B, Ash)));
        Assert.True(_inventory.HasReservation(A, "trade:a")); // still offered
    }

    [Fact]
    public void A_receivers_own_outgoing_units_free_the_space_its_incoming_units_need()
    {
        var bag = _inventory.CurrentInventory(B).BagAt(0);
        for (int slot = 1; slot < bag.SlotCount; slot++)
            _inventory.TryAdd(B, Item(Horn), 0, 99, $"fill:{slot}", InventoryReason.Admin, "test");
        Offer(A, "trade:a", new ReserveItem(At(0), 10)); // 4 would merge onto B's 95, 6 need a slot B does not have...
        Offer(B, "trade:b", new ReserveItem(At(0), 95)); // ...but B gives its whole ash stack in the same swap

        Assert.Equal(TransferRefusal.None, _inventory.TryTransfer(new TransferSide(A, "trade:a", 0), new TransferSide(B, "trade:b", 0),
            "trade:r:9", InventoryReason.Trade, 3, out _));
        Assert.Equal((95, 10), (Units(A, Ash), Units(B, Ash)));
    }

    [Fact]
    public void A_repeated_key_returns_the_first_keys_and_moves_nothing_twice()
    {
        Offer(A, "trade:a", new ReserveItem(At(0), 4));
        Assert.Equal(TransferRefusal.None, _inventory.TryTransfer(new TransferSide(A, "trade:a", 0), new TransferSide(B, null, 0),
            "trade:r:10", InventoryReason.Trade, 3, out var first));
        Assert.Equal(TransferRefusal.None, _inventory.TryTransfer(new TransferSide(A, "trade:a", 0), new TransferSide(B, null, 0),
            "trade:r:10", InventoryReason.Trade, 3, out var second));
        Assert.Equal(first, second);
        Assert.Equal((6, 99), (Units(A, Ash), Units(B, Ash)));
    }

    [Fact]
    public void An_escrow_hold_and_release_move_units_and_gold_out_and_back_with_the_instance_intact()
    {
        long sword = ItemInstanceIds.Make(A, 1);
        var held = new List<EscrowItemRow>();
        string? holdKey = _inventory.TryHoldOut(A, [new ReserveItem(Gear1, 1), new ReserveItem(At(0), 3)], 40, "esc:1",
            InventoryReason.EscrowHold, "test", held, out var refusal);

        Assert.Equal(("esc:1:out", TransferRefusal.None), (holdKey, refusal));
        Assert.Equal((7, 60L, 0), (Units(A, Ash), _inventory.Gold(A), Units(A, Sword)));
        Assert.Equal(2, held.Count);
        Assert.Contains(held, h => h.InstanceId == sword && h.PropertiesJson != null);

        string? releaseKey = _inventory.TryReceive(B, held, 40, "rel:1", InventoryReason.EscrowRelease, "test", A, 3, out refusal);

        Assert.Equal(("rel:1:in", TransferRefusal.None), (releaseKey, refusal));
        Assert.Equal((98, 540L), (Units(B, Ash), _inventory.Gold(B)));
        Assert.True(_inventory.TryGetLatestProperties(B, sword, out var properties));
        Assert.Equal(new long[] { A }, properties.OwnerHistory);
        Assert.Null(_inventory.TryHoldOut(A, [new ReserveItem(At(0), 1)], 0, "esc:1", InventoryReason.EscrowHold, "test", [], out refusal));

        SaveBoth();
        Assert.Contains(_transferred.Items, t => t.FromCharacterId == A && t.ToCharacterId == 0 && t.Reason == InventoryReason.EscrowHold);
        Assert.Contains(_transferred.Items, t => t.FromCharacterId == 0 && t.ToCharacterId == B && t.Reason == InventoryReason.EscrowRelease);
        Assert.Empty(_created.Items);
        Assert.Empty(_destroyed.Items);
    }

    [Fact]
    public void An_escrow_hold_refuses_reserved_or_missing_units_and_too_much_gold()
    {
        Offer(A, "forge:1", new ReserveItem(At(0), 8));

        Assert.Null(_inventory.TryHoldOut(A, [new ReserveItem(At(0), 3)], 0, "esc:2", InventoryReason.EscrowHold, "t", [], out var refusal));
        Assert.Equal(TransferRefusal.Changed, refusal);
        Assert.Null(_inventory.TryHoldOut(A, [], 101, "esc:3", InventoryReason.EscrowHold, "t", [], out refusal));
        Assert.Equal(TransferRefusal.GoldShort, refusal);
        Assert.Null(_inventory.TryHoldOut(A, [new ReserveItem(At(5), 1)], 0, "esc:4", InventoryReason.EscrowHold, "t", [], out refusal));
        Assert.Equal(TransferRefusal.Changed, refusal);
        Assert.Equal((10, 100L), (Units(A, Ash), _inventory.Gold(A)));
    }

    private static ItemDef Item(string id)
    {
        Assert.True(Items.TryGetItem(id, out var item));
        return item;
    }

    private sealed class Recorder<T> : IEventConsumer<T> where T : struct, IGameEvent
    {
        public List<T> Items { get; } = [];

        public void Handle(in EventEnvelope envelope, in T payload) => Items.Add(payload);
    }
}
