using GameServer.Economy;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Simulation.Economy;

namespace GameServer.Tests.Economy;

/// <summary>
/// <see cref="FeeService"/> (spec 4.2 R-4.2-04/05/06, M4 decision 3; WP-M4-1) without a database: the quote by region and fee
/// type (unowned: all destroyed; owned: the clamped owner rate and the split), the Merchant hook, and <see cref="FeeService.Apply"/>
/// as one more gold entry of the caller's operation (reason <see cref="InventoryReason.Fee"/>, key <c>{op}:fee</c>), once per
/// key, refused without the gold, nothing for a zero fee, and <see cref="FeeCharged"/> only after the save committed.
/// </summary>
public class FeeServiceTests
{
    private const long Character = 7;

    private static readonly FeeRateTable Rates = FeeRateTable.From(
    [
        new FeeRates { Id = "listing", DefaultPercent = 2, MinPercent = 0, MaxPercent = 10, DestroyedSharePercent = 50 },
        new FeeRates { Id = "saleTax", DefaultPercent = 5, MinPercent = 0, MaxPercent = 15, DestroyedSharePercent = 50 },
        new FeeRates { Id = "contract", DefaultPercent = 5, MinPercent = 1, MaxPercent = 15, DestroyedSharePercent = 30 },
    ])!;

    private readonly EventBus _bus = new(1, new FakeClock(), TestPipelineSettings.BusLimits());
    private readonly FeeRecorder _recorder = new();
    private readonly InventoryService _inventory;
    private readonly FeeService _fees;

    public FeeServiceTests()
    {
        _inventory = new InventoryService(ItemRules.From(TestCombat.Tables())!, _bus);
        Assert.True(_inventory.Attach(Character, []));
        _inventory.AttachGold(Character, 1_000);
        _fees = new FeeService(Rates, _inventory, NoTerritoryOwners.Instance, _bus);
        _inventory.OpCommitted += _fees.OnOpCommitted;
        _bus.Subscribe<FeeCharged>(_recorder);
        _bus.Subscribe<GoldDestroyed>(_recorder);
    }

    /// <summary>The character's save: takes the pending batch and commits it, then the bus delivers.</summary>
    private void Commit()
    {
        var saveId = Guid.NewGuid();
        Assert.NotNull(_inventory.TakeBatch(Character, saveId));
        _inventory.Committed(Character, saveId);
        _bus.DispatchQueued();
    }

    [Theory]
    [InlineData(FeeType.Listing, 1_000, 2, 20)]
    [InlineData(FeeType.SaleTax, 1_000, 5, 50)]
    [InlineData(FeeType.Contract, 999, 5, 49)]
    public void Unowned_the_quote_is_the_default_rate_and_all_of_it_is_destroyed(FeeType type, long amount, int rate, long total)
    {
        var quote = _fees.Quote(Character, 3, type, amount);

        Assert.Equal(new FeeQuote(type, 3, amount, rate, 0, total, 0, total, 0), quote);
    }

    [Theory]
    [InlineData(FeeType.SaleTax, 8, 8, 40, 20)] // 8 % of 500 = 40, half destroyed
    [InlineData(FeeType.SaleTax, 40, 15, 75, 38)] // the owner's 40 % is clamped to the maximum 15; the odd gold to the sink
    [InlineData(FeeType.Contract, 0, 1, 5, 2)] // clamped up to the minimum 1; 30 % destroyed, rounded up
    public void Owned_the_owner_rate_is_clamped_and_the_fee_split(FeeType type, int ownerRate, int rate, long total, long destroyed)
    {
        var fees = new FeeService(Rates, _inventory, new OneOwner(42, ownerRate), _bus);

        var quote = fees.Quote(Character, 3, type, 500);

        Assert.Equal((rate, total, total - destroyed, destroyed, 42L), (quote.RatePercent, quote.Total, quote.OwnerShare,
            quote.AdventurerShare, quote.OwnerGuildId));
    }

    [Fact]
    public void Apply_removes_the_fee_as_a_Fee_sink_under_the_callers_key_and_announces_it_only_after_the_commit()
    {
        var quote = _fees.Quote(Character, 3, FeeType.SaleTax, 1_000);

        var result = _fees.Apply(Character, quote, "test:op:1", "npc:test");

        Assert.Equal(new FeeApplyResult(FeeApplyOutcome.Applied, 50), result);
        Assert.Equal(950, _inventory.Gold(Character));
        Assert.Equal(1_000, _inventory.CommittedGold(Character));
        Assert.Equal("test:op:1:fee", FeeService.OpKeyOf("test:op:1"));
        Assert.True(_inventory.TryGetGoldOp(Character, "test:op:1:fee", out long removed));
        Assert.Equal(50, removed);
        _bus.DispatchQueued();
        Assert.Empty(_recorder.Fees); // not before the save committed (rule 5)

        Commit();

        var charged = Assert.Single(_recorder.Fees);
        Assert.Equal(new FeeCharged(Character, FeeType.SaleTax, 3, 1_000, 50, 0, 50, "test:op:1:fee"), charged);
        Assert.Equal(new GoldDestroyed(Character, 50, InventoryReason.Fee), Assert.Single(_recorder.Destroyed));
        Assert.True(_inventory.IsCommitted(Character, FeeService.OpKeyOf("test:op:1")));
        Assert.Equal(0, _fees.AwaitingCommit);
    }

    [Fact]
    public void The_fee_rides_the_callers_batch_in_the_same_save()
    {
        Assert.True(_inventory.TryAddGold(Character, 500, "test:op:2:gold", InventoryReason.Admin, "test").Applied);
        _fees.Apply(Character, _fees.Quote(Character, 0, FeeType.SaleTax, 500), "test:op:2", "test");

        var batch = _inventory.TakeBatch(Character, Guid.NewGuid());

        Assert.NotNull(batch);
        Assert.Equal(["test:op:2:gold", "test:op:2:fee"], batch.Ops.Select(o => o.OpKey));
        var fee = Assert.Single(batch.Gold!.Entries, e => e.OpKey == "test:op:2:fee");
        Assert.Equal(new GoldEntry("test:op:2:fee", -25, 1_475, GoldFlow.Sink, nameof(InventoryReason.Fee), "test"), fee);
    }

    [Fact]
    public void A_repeated_key_changes_nothing_again_and_is_announced_once()
    {
        var quote = _fees.Quote(Character, 0, FeeType.Listing, 1_000);
        _fees.Apply(Character, quote, "test:op:3", "test");

        var again = _fees.Apply(Character, quote, "test:op:3", "test");
        Commit();
        var afterCommit = _fees.Apply(Character, quote, "test:op:3", "test");

        Assert.Null(_inventory.TakeBatch(Character, Guid.NewGuid())); // the repeat added nothing to save
        _bus.DispatchQueued();

        Assert.Equal(new FeeApplyResult(FeeApplyOutcome.Repeated, 20), again);
        Assert.Equal(new FeeApplyResult(FeeApplyOutcome.Repeated, 20), afterCommit);
        Assert.Equal(980, _inventory.CommittedGold(Character));
        Assert.Single(_recorder.Fees);
    }

    [Fact]
    public void Without_the_gold_nothing_changes_and_the_caller_refuses()
    {
        var quote = _fees.Quote(Character, 0, FeeType.Listing, 100_000); // 2 000 gold owed, 1 000 held

        var result = _fees.Apply(Character, quote, "test:op:4", "test");

        Assert.Equal(FeeApplyOutcome.NotEnoughGold, result.Outcome);
        Assert.False(result.Ok);
        Assert.Equal(1_000, _inventory.Gold(Character));
        Assert.Null(_inventory.TakeBatch(Character, Guid.NewGuid()));
        Assert.Equal(0, _fees.AwaitingCommit);
    }

    [Fact]
    public void A_zero_fee_records_nothing_and_announces_nothing()
    {
        var quote = _fees.Quote(Character, 0, FeeType.SaleTax, 19); // 0.95 rounds down to 0

        var result = _fees.Apply(Character, quote, "test:op:5", "test");

        Assert.Equal((0L, FeeApplyOutcome.NothingToCharge, true), (quote.Total, result.Outcome, result.Ok));
        Assert.Null(_inventory.TakeBatch(Character, Guid.NewGuid()));
        _bus.DispatchQueued();
        Assert.Empty(_recorder.Fees);
    }

    [Fact]
    public void An_owner_share_is_refused_until_the_guild_treasury_exists()
    {
        var fees = new FeeService(Rates, _inventory, new OneOwner(42, 5), _bus);
        var quote = fees.Quote(Character, 0, FeeType.SaleTax, 1_000);

        Assert.Throws<NotSupportedException>(() => fees.Apply(Character, quote, "test:op:6", "test")); // TODO(M8)
        Assert.Equal(1_000, _inventory.Gold(Character));
    }

    [Fact]
    public void The_Merchant_hook_gives_no_discount_while_no_job_holds_it()
    {
        Assert.All(FeeTypes.All, type => Assert.Equal(0, _fees.Quote(Character, 0, type, 10_000).DiscountPercent));
    }

    [Fact]
    public void Other_committed_operations_are_not_fees()
    {
        Assert.True(_inventory.TryRemoveGold(Character, 10, "test:op:7:gold", InventoryReason.VendorPurchase, "test").Applied);
        Commit();

        Assert.Empty(_recorder.Fees);
        Assert.Equal(InventoryReason.VendorPurchase, Assert.Single(_recorder.Destroyed).Reason);
    }

    private sealed class OneOwner(long guildId, int rate) : ITerritoryOwnerQuery
    {
        public bool TryGetOwner(byte regionIndex, FeeType type, out TerritoryFeeOwner owner)
        {
            owner = new TerritoryFeeOwner(guildId, rate);
            return true;
        }
    }

    private sealed class FeeRecorder : IEventConsumer<FeeCharged>, IEventConsumer<GoldDestroyed>
    {
        public List<FeeCharged> Fees { get; } = [];
        public List<GoldDestroyed> Destroyed { get; } = [];

        public void Handle(in EventEnvelope envelope, in FeeCharged payload) => Fees.Add(payload);
        public void Handle(in EventEnvelope envelope, in GoldDestroyed payload) => Destroyed.Add(payload);
    }
}
