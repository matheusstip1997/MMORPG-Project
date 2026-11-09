using GameServer.Events;
using GameServer.Items;
using GameServer.Jobs;
using Shared.Simulation.Economy;

namespace GameServer.Economy;

/// <summary>
/// What a fee costs (<see cref="FeeService.Quote"/>): the type, where it is paid (the world's region index), the amount it is
/// taken on, the rate and discount used, and the split. <see cref="OwnerGuildId"/> is 0 when the territory has no owner.
/// </summary>
public readonly record struct FeeQuote(FeeType Type, byte RegionIndex, long Amount, int RatePercent, int DiscountPercent,
    long Total, long OwnerShare, long AdventurerShare, long OwnerGuildId);

/// <summary>What <see cref="FeeService.Apply"/> did.</summary>
public enum FeeApplyOutcome : byte
{
    /// <summary>The destroyed share was removed; it rides the caller's next save.</summary>
    Applied = 0,
    /// <summary>The same operation key was applied before: nothing changed again (double submit, retry).</summary>
    Repeated = 1,
    /// <summary>The fee is 0 (a small amount rounds down, or a 0 % rate): nothing to remove and no <see cref="FeeCharged"/>.</summary>
    NothingToCharge = 2,
    /// <summary>The payer holds less gold than the fee: nothing changed; the caller refuses with its own reason.</summary>
    NotEnoughGold = 3,
}

/// <summary>The outcome of <see cref="FeeService.Apply"/> and the gold it destroyed (the first time, for a repeat).</summary>
public readonly record struct FeeApplyResult(FeeApplyOutcome Outcome, long Destroyed)
{
    /// <summary>The fee is in place (applied now, before, or nothing was owed): the caller may go on with its operation.</summary>
    public bool Ok => Outcome != FeeApplyOutcome.NotEnoughGold;
}

/// <summary>
/// <b>The only fee computer</b> (spec 4.2 R-4.2-04/05/06, flow "Fee computation and split"; M4 decision 3; CLAUDE.md rule 4;
/// WP-M4-1). Every listing fee, sale tax and contract fee goes through it; nothing else computes a fee.
/// <list type="bullet">
/// <item><see cref="Quote"/>: the rate of the region's owner within the <c>FeeRates</c> bounds, or the default rate when the
/// territory has no owner (always in M4: <see cref="NoTerritoryOwners"/>); the Merchant discount on listing fees and sale
/// taxes; the split. All integer math (<see cref="FeeMath"/>), rounded down so the payer never pays more than the rate.</item>
/// <item><see cref="Apply"/>: <b>inside the caller's operation</b>. It removes the destroyed share through
/// <see cref="InventoryService.TryRemoveGold"/> (reason <see cref="InventoryReason.Fee"/>, key <c>{op}:fee</c>), so the
/// removal is one more gold entry of the caller's pending batch and commits in the caller's save, in one transaction with
/// the rest of the operation (rule 5; the <c>gold_ledger</c> sink row and the <see cref="GoldDestroyed"/> event come from
/// there). The caller checks the gold first (the fee plus its own costs) and applies every half after its checks, as the
/// outpost sale does; a refused <see cref="Apply"/> changes nothing.</item>
/// <item>After the commit (<see cref="OnOpCommitted"/>, the <see cref="InventoryService.OpCommitted"/> hook):
/// <see cref="FeeCharged"/>, once per fee.</item>
/// </list>
/// World thread only.
/// </summary>
public sealed class FeeService
{
    private readonly FeeRateTable _rates;
    private readonly InventoryService _inventory;
    private readonly ITerritoryOwnerQuery _owners;
    private readonly EventBus? _events;
    private readonly Dictionary<(long CharacterId, string OpKey), FeeQuote> _awaitingCommit = new();

    public FeeService(FeeRateTable rates, InventoryService inventory, ITerritoryOwnerQuery owners, EventBus? events)
    {
        _rates = rates;
        _inventory = inventory;
        _owners = owners;
        _events = events;
    }

    /// <summary>The payer's jobs, for the Merchant discount (R-4.2-05); null without the job tables.</summary>
    public IJobQuery? Jobs { get; set; }

    /// <summary>Applied fees whose save has not committed yet.</summary>
    public int AwaitingCommit => _awaitingCommit.Count;

    /// <summary>The fee's own gold operation key for a caller's operation key (what <see cref="InventoryService.IsCommitted"/> takes).</summary>
    public static string OpKeyOf(string callerOpKey) => callerOpKey + ":fee";

    /// <summary>
    /// What a fee of <paramref name="type"/> on <paramref name="amount"/> gold costs <paramref name="payerCharacterId"/> in the
    /// region at <paramref name="regionIndex"/>. Read-only.
    /// </summary>
    public FeeQuote Quote(long payerCharacterId, byte regionIndex, FeeType type, long amount)
    {
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        var row = _rates[type];
        bool owned = _owners.TryGetOwner(regionIndex, type, out var owner);
        int rate = owned ? FeeMath.ClampRate(owner.RatePercent, row.MinPercent, row.MaxPercent) : row.DefaultPercent;
        int discount = FeeMath.DiscountApplies(type) ? MerchantDiscountPercent(payerCharacterId) : 0;
        var split = FeeMath.Split(FeeMath.Total(type, amount, rate, discount), owned, row.DestroyedSharePercent);
        return new FeeQuote(type, regionIndex, amount, rate, discount, split.Total, split.OwnerShare, split.AdventurerShare,
            owned ? owner.GuildId : 0);
    }

    /// <summary>
    /// Charges <paramref name="quote"/> to <paramref name="characterId"/> as part of the caller's operation
    /// <paramref name="opKey"/> (the fee's key is <see cref="OpKeyOf"/>): the destroyed share leaves the character's gold now
    /// and is saved with the caller's batch. <paramref name="source"/> goes to the ledger row (for example <c>npc:m1.auction</c>).
    /// </summary>
    public FeeApplyResult Apply(long characterId, in FeeQuote quote, string opKey, string source)
    {
        if (quote.OwnerShare > 0)
        {
            // TODO(M8): credit the owner guild's restricted treasury (R-4.2-06) through the treasury's writer, in the same
            // transaction. Unreachable in M4: NoTerritoryOwners owns nothing.
            throw new NotSupportedException("an owned territory's fee share needs the guild treasury (M8)");
        }
        if (quote.AdventurerShare <= 0)
            return new FeeApplyResult(FeeApplyOutcome.NothingToCharge, 0);
        string feeKey = OpKeyOf(opKey);
        var gold = _inventory.TryRemoveGold(characterId, quote.AdventurerShare, feeKey, InventoryReason.Fee, source);
        if (!gold.Applied)
            return new FeeApplyResult(FeeApplyOutcome.NotEnoughGold, 0);
        if (gold.Repeated)
            return new FeeApplyResult(FeeApplyOutcome.Repeated, gold.Amount);
        _awaitingCommit[(characterId, feeKey)] = quote;
        return new FeeApplyResult(FeeApplyOutcome.Applied, gold.Amount);
    }

    /// <summary>
    /// <see cref="InventoryService.OpCommitted"/>: a save committed <paramref name="opKey"/>; for a fee applied here,
    /// <see cref="FeeCharged"/> goes out, once.
    /// </summary>
    public void OnOpCommitted(long characterId, string opKey, InventoryReason reason)
    {
        if (reason != InventoryReason.Fee || !_awaitingCommit.Remove((characterId, opKey), out var quote))
            return;
        _events?.Publish(new FeeCharged(characterId, quote.Type, quote.RegionIndex, quote.Amount, quote.Total, quote.OwnerShare,
            quote.AdventurerShare, opKey), characterId, quote.RegionIndex);
    }

    /// <summary>
    /// The Merchant job's discount on auction and shop fees and sale taxes (R-4.2-05: a job modifier in FeeService), read
    /// from <see cref="Jobs"/>. TODO(open): the Merchant job has no <c>JobDef</c> row (the job list beyond the Blacksmith and
    /// the Jeweler is open, M4 decision 3), so no character holds it and the discount and its size are undecided: 0.
    /// </summary>
    private int MerchantDiscountPercent(long payerCharacterId) => 0;
}
