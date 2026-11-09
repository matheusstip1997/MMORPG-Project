using System.Text.Json;

namespace BotClient.LoadTest;

/// <summary>Fees of one type (<c>FeeCharged</c>, WP-M4-1): how many, their total, the destroyed share and the owner share.</summary>
public sealed record FeeTotals(long Count, long Total, long Destroyed, long OwnerShare);

/// <summary>Listings of one kind (auction or stall; WP-M4-7, WP-M4-8): created with their fees, sold with their gold and tax, cancelled, expired.</summary>
public sealed record ListingTotals(long Created, long Fees, long Sold, long SoldGold, long Taxes, long Cancelled, long Expired);

/// <summary>Mail of one direction (sent, claimed, expired; WP-M4-3): how many, the gold and the item units attached.</summary>
public sealed record MailTotals(long Count, long Gold, long Units);

/// <summary>
/// The M4 section of the economy report (WP-M4-12, spec 12.5; the record kinds and fields in the BotClient README, "M4
/// telemetry"): fees by type, trades and their value, listings, stalls, death loot bags, contracts by outcome, party rolls
/// per party and member, mail; gold and item units by the M4 <c>InventoryReason</c> names. Always printed, zeros until the
/// WPs write the records. The M4 reasons are found <b>by name</b> in <see cref="EconomyReport.InventoryReasonNames"/>, so
/// the merge order picks their numbers; a name the enum does not have yet prints "not in InventoryReason yet".
/// </summary>
public sealed record EconomyReportM4
{
    public const string FeeChargedKind = "FeeCharged";
    public const string TradeCompletedKind = "TradeCompleted";
    public const string ListingCreatedKind = "ListingCreated";
    public const string ListingSoldKind = "ListingSold";
    public const string ListingCancelledKind = "ListingCancelled";
    public const string ListingExpiredKind = "ListingExpired";
    public const string StallOpenedKind = "StallOpened";
    public const string StallClosedKind = "StallClosed";
    public const string LootBagCreatedKind = "LootBagCreated";
    public const string LootBagPickedUpKind = "LootBagPickedUp";
    public const string LootBagExpiredKind = "LootBagExpired";
    public const string ContractCompletedKind = "ContractCompleted";
    public const string ContractExpiredKind = "ContractExpired";
    public const string ContractCancelledKind = "ContractCancelled";
    public const string LootRolledKind = "LootRolled";
    public const string MailSentKind = "MailSent";
    public const string MailClaimedKind = "MailClaimed";
    public const string MailExpiredKind = "MailExpired";

    /// <summary>
    /// The M4 <c>InventoryReason</c> names the section always lists (plan section 6, WP-M4-12): each WP appends its member
    /// (camelCase name) to the enum and to <see cref="EconomyReport.InventoryReasonNames"/> at its merge; a test requires every
    /// enum value from 20 on to be named here.
    /// </summary>
    public static readonly IReadOnlyList<string> M4ReasonNames =
    [
        "trade", "listingPurchase", "listingProceeds", "fee", "mailClaim", "mailExpired", "escrowHold", "escrowRelease",
        "deathDrop", "bagExpired", "contractCancel",
    ];

    /// <summary>The <c>feeType</c> values of <c>FeeCharged</c> (WP-M4-1, <c>Shared/Simulation/Economy/FeeType</c>: 0 listing, 1 saleTax, 2 contract; a test compares them).</summary>
    public static readonly IReadOnlyList<string> FeeTypeNames = ["listing", "saleTax", "contract"];

    /// <summary>The <c>kind</c> values of the listing records (1 auction, WP-M4-7; 2 stall, WP-M4-8).</summary>
    public static readonly IReadOnlyList<string> ListingKindNames = ["none", "auction", "stall"];

    public IReadOnlyDictionary<string, FeeTotals> FeesByType { get; init; } = new SortedDictionary<string, FeeTotals>(StringComparer.Ordinal);
    public long Trades { get; init; }
    public long TradeGold { get; init; }
    public long TradeUnits { get; init; }
    public IReadOnlyDictionary<string, ListingTotals> ListingsByKind { get; init; } = new SortedDictionary<string, ListingTotals>(StringComparer.Ordinal);
    public long StallsOpened { get; init; }
    public long StallsClosed { get; init; }
    public long BagsCreated { get; init; }
    public long BagUnits { get; init; }
    public long BagsRecovered { get; init; }
    public long BagsStolenFrom { get; init; }
    public long BagsExpired { get; init; }
    public long BagUnitsDestroyed { get; init; }
    public long BagValueDestroyed { get; init; }
    public long ContractsCompleted { get; init; }
    public long ContractGold { get; init; }
    public long ContractFees { get; init; }
    public long ContractsExpired { get; init; }
    public long ContractsCancelled { get; init; }
    public long ContractCancelCost { get; init; }

    /// <summary>Party loot rolls won per party and winning member (<c>LootRolled</c>: party id, winner).</summary>
    public IReadOnlyDictionary<long, IReadOnlyDictionary<long, long>> RollsByParty { get; init; } =
        new SortedDictionary<long, IReadOnlyDictionary<long, long>>();

    public MailTotals MailSent { get; init; } = new(0, 0, 0);
    public MailTotals MailClaimed { get; init; } = new(0, 0, 0);
    public MailTotals MailExpired { get; init; } = new(0, 0, 0);

    /// <summary>The section's lines (<c>report --kind economy</c>), with the gold and item units of <paramref name="report"/> by the M4 reasons.</summary>
    public IEnumerable<string> Describe(EconomyReport report)
    {
        var goldIn = report.Hours.SelectMany(h => h.CreatedByReason).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
        var goldOut = report.Hours.SelectMany(h => h.DestroyedByReason).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
        var listed = M4ReasonNames.Where(InEnum).ToList();
        var missing = M4ReasonNames.Where(r => !InEnum(r)).ToList();
        yield return "M4 gold and items by reason (gold in/out, item units created/destroyed): "
            + (listed.Count == 0 ? "-" : string.Join(", ", listed.Select(r =>
                Invariant($"{r} {goldIn.GetValueOrDefault(r)}/{goldOut.GetValueOrDefault(r)} {report.ItemsCreatedByReason.GetValueOrDefault(r)}/{report.ItemsDestroyedByReason.GetValueOrDefault(r)}"))))
            + (missing.Count == 0 ? "" : "; not in InventoryReason yet: " + string.Join(", ", missing));
        yield return "M4 fees by type (count, total, destroyed, owner share): " + string.Join(", ", FeeTypeNames
            .Concat(FeesByType.Keys).Distinct()
            .Select(t => (t, f: FeesByType.GetValueOrDefault(t) ?? new FeeTotals(0, 0, 0, 0)))
            .Select(x => Invariant($"{x.t} {x.f.Count}x {x.f.Total} ({x.f.Destroyed} destroyed, {x.f.OwnerShare} to owners)")));
        yield return Invariant($"M4 trades {Trades}: gold moved {TradeGold}, item units moved {TradeUnits}");
        yield return "M4 listings (created/fees, sold/gold/taxes, cancelled, expired): " + string.Join(", ", ListingKindNames.Skip(1)
            .Concat(ListingsByKind.Keys).Distinct()
            .Select(k => (k, l: ListingsByKind.GetValueOrDefault(k) ?? new ListingTotals(0, 0, 0, 0, 0, 0, 0)))
            .Select(x => Invariant($"{x.k} {x.l.Created}/{x.l.Fees}, {x.l.Sold}/{x.l.SoldGold}/{x.l.Taxes}, {x.l.Cancelled}, {x.l.Expired}")));
        yield return Invariant($"M4 stalls opened {StallsOpened}, closed {StallsClosed}");
        yield return Invariant($"M4 loot bags created {BagsCreated} ({BagUnits} units), recovered {BagsRecovered}, stolen from {BagsStolenFrom}, ")
            + Invariant($"expired {BagsExpired} ({BagUnitsDestroyed} units, value {BagValueDestroyed} destroyed)");
        yield return Invariant($"M4 contracts completed {ContractsCompleted} (gold {ContractGold}, fees {ContractFees}), expired {ContractsExpired}, ")
            + Invariant($"cancelled {ContractsCancelled} (cost {ContractCancelCost})");
        yield return Invariant($"M4 party rolls {RollsByParty.Values.Sum(p => p.Values.Sum())}: ") + (RollsByParty.Count == 0 ? "-"
            : string.Join("; ", RollsByParty.Select(p => Invariant($"party {p.Key}: ") + string.Join(", ", p.Value.Select(w => Invariant($"{w.Key} won {w.Value}"))))));
        yield return Invariant($"M4 mail sent {MailSent.Count} ({MailSent.Gold} gold, {MailSent.Units} units), claimed {MailClaimed.Count} ")
            + Invariant($"({MailClaimed.Gold} gold, {MailClaimed.Units} units), expired {MailExpired.Count} ({MailExpired.Gold} gold, {MailExpired.Units} units destroyed)");
    }

    /// <summary>The section for <c>report --kind economy --json</c> (key <c>m4</c>): every M4 reason, fee type and listing kind listed.</summary>
    public object ToJson(EconomyReport report)
    {
        var goldIn = report.Hours.SelectMany(h => h.CreatedByReason).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
        var goldOut = report.Hours.SelectMany(h => h.DestroyedByReason).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
        return new
        {
            reasons = M4ReasonNames.ToDictionary(r => r, r => new
            {
                inEnum = InEnum(r),
                goldIn = goldIn.GetValueOrDefault(r),
                goldOut = goldOut.GetValueOrDefault(r),
                itemsCreated = report.ItemsCreatedByReason.GetValueOrDefault(r),
                itemsDestroyed = report.ItemsDestroyedByReason.GetValueOrDefault(r),
            }),
            fees = FeeTypeNames.Concat(FeesByType.Keys).Distinct().ToDictionary(t => t, t => FeesByType.GetValueOrDefault(t) ?? new FeeTotals(0, 0, 0, 0)),
            trades = new { count = Trades, gold = TradeGold, units = TradeUnits },
            listings = ListingKindNames.Skip(1).Concat(ListingsByKind.Keys).Distinct()
                .ToDictionary(k => k, k => ListingsByKind.GetValueOrDefault(k) ?? new ListingTotals(0, 0, 0, 0, 0, 0, 0)),
            stalls = new { opened = StallsOpened, closed = StallsClosed },
            lootBags = new
            {
                created = BagsCreated, units = BagUnits, recovered = BagsRecovered, stolenFrom = BagsStolenFrom, expired = BagsExpired,
                unitsDestroyed = BagUnitsDestroyed, valueDestroyed = BagValueDestroyed,
            },
            contracts = new
            {
                completed = ContractsCompleted, gold = ContractGold, fees = ContractFees, expired = ContractsExpired,
                cancelled = ContractsCancelled, cancelCost = ContractCancelCost,
            },
            partyRolls = RollsByParty.ToDictionary(p => p.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                p => p.Value.ToDictionary(w => w.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), w => w.Value)),
            mail = new { sent = MailSent, claimed = MailClaimed, expired = MailExpired },
        };
    }

    /// <summary>The M4 reason name is a member of <c>InventoryReason</c> (the report lists it at its value).</summary>
    public static bool InEnum(string reason) => EconomyReport.InventoryReasonNames.Contains(reason);

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>Gathers the M4 records while <see cref="EconomyReport.Read"/> walks the telemetry.</summary>
    internal sealed class Accumulator
    {
        private readonly SortedDictionary<string, FeeTotals> _fees = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, ListingTotals> _listings = new(StringComparer.Ordinal);
        private readonly SortedDictionary<long, SortedDictionary<long, long>> _rolls = new();
        private long _trades, _tradeGold, _tradeUnits, _stallsOpened, _stallsClosed;
        private long _bags, _bagUnits, _recovered, _stolen, _bagsExpired, _bagUnitsDestroyed, _bagValue;
        private long _completed, _contractGold, _contractFees, _contractsExpired, _cancelled, _cancelCost;
        private MailTotals _sent = new(0, 0, 0), _claimed = new(0, 0, 0), _mailExpired = new(0, 0, 0);

        /// <summary>Takes one record of an M4 kind; false for any other kind.</summary>
        public bool Add(string kind, JsonElement root)
        {
            switch (kind)
            {
                case FeeChargedKind:
                {
                    string type = FeeName(Long(root, "feeType"));
                    var seen = _fees.GetValueOrDefault(type) ?? new FeeTotals(0, 0, 0, 0);
                    _fees[type] = new FeeTotals(seen.Count + 1, seen.Total + Long(root, "total"), seen.Destroyed + Long(root, "destroyed"),
                        seen.OwnerShare + Long(root, "ownerShare"));
                    return true;
                }
                case TradeCompletedKind:
                    _trades++;
                    _tradeGold += Long(root, "goldAtoB") + Long(root, "goldBtoA");
                    _tradeUnits += Long(root, "unitsAtoB") + Long(root, "unitsBtoA");
                    return true;
                case ListingCreatedKind:
                    Listing(root, l => l with { Created = l.Created + 1, Fees = l.Fees + Long(root, "fee") });
                    return true;
                case ListingSoldKind:
                    Listing(root, l => l with { Sold = l.Sold + 1, SoldGold = l.SoldGold + Long(root, "price"), Taxes = l.Taxes + Long(root, "tax") });
                    return true;
                case ListingCancelledKind:
                    Listing(root, l => l with { Cancelled = l.Cancelled + 1 });
                    return true;
                case ListingExpiredKind:
                    Listing(root, l => l with { Expired = l.Expired + 1 });
                    return true;
                case StallOpenedKind:
                    _stallsOpened++;
                    return true;
                case StallClosedKind:
                    _stallsClosed++;
                    return true;
                case LootBagCreatedKind:
                    _bags++;
                    _bagUnits += Long(root, "units");
                    return true;
                case LootBagPickedUpKind:
                    if (Long(root, "stolen") != 0)
                        _stolen++;
                    else
                        _recovered++;
                    return true;
                case LootBagExpiredKind:
                    _bagsExpired++;
                    _bagUnitsDestroyed += Long(root, "units");
                    _bagValue += Long(root, "value");
                    return true;
                case ContractCompletedKind:
                    _completed++;
                    _contractGold += Long(root, "gold");
                    _contractFees += Long(root, "fee");
                    return true;
                case ContractExpiredKind:
                    _contractsExpired++;
                    return true;
                case ContractCancelledKind:
                    _cancelled++;
                    _cancelCost += Long(root, "cost");
                    return true;
                case LootRolledKind:
                {
                    long party = Long(root, "party"), winner = Long(root, "winner");
                    if (!_rolls.TryGetValue(party, out var members))
                        _rolls[party] = members = new SortedDictionary<long, long>();
                    members[winner] = members.GetValueOrDefault(winner) + 1;
                    return true;
                }
                case MailSentKind:
                    _sent = Mail(_sent, root);
                    return true;
                case MailClaimedKind:
                    _claimed = Mail(_claimed, root);
                    return true;
                case MailExpiredKind:
                    _mailExpired = Mail(_mailExpired, root);
                    return true;
                default:
                    return false;
            }
        }

        public EconomyReportM4 Build() => new()
        {
            FeesByType = _fees,
            Trades = _trades,
            TradeGold = _tradeGold,
            TradeUnits = _tradeUnits,
            ListingsByKind = _listings,
            StallsOpened = _stallsOpened,
            StallsClosed = _stallsClosed,
            BagsCreated = _bags,
            BagUnits = _bagUnits,
            BagsRecovered = _recovered,
            BagsStolenFrom = _stolen,
            BagsExpired = _bagsExpired,
            BagUnitsDestroyed = _bagUnitsDestroyed,
            BagValueDestroyed = _bagValue,
            ContractsCompleted = _completed,
            ContractGold = _contractGold,
            ContractFees = _contractFees,
            ContractsExpired = _contractsExpired,
            ContractsCancelled = _cancelled,
            ContractCancelCost = _cancelCost,
            RollsByParty = _rolls.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<long, long>)p.Value),
            MailSent = _sent,
            MailClaimed = _claimed,
            MailExpired = _mailExpired,
        };

        private void Listing(JsonElement root, Func<ListingTotals, ListingTotals> change)
        {
            string kind = Name(ListingKindNames, Long(root, "kind"), "kind ");
            _listings[kind] = change(_listings.GetValueOrDefault(kind) ?? new ListingTotals(0, 0, 0, 0, 0, 0, 0));
        }

        private static MailTotals Mail(MailTotals seen, JsonElement root) =>
            new(seen.Count + 1, seen.Gold + Long(root, "gold"), seen.Units + Long(root, "units"));

        private static string Name(IReadOnlyList<string> names, long value, string prefix) =>
            value > 0 && value < names.Count ? names[(int)value] : prefix + value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>A <c>FeeType</c> value (from 0) by its name; an unknown one as <c>feeType &lt;n&gt;</c>.</summary>
        private static string FeeName(long value) =>
            value >= 0 && value < FeeTypeNames.Count ? FeeTypeNames[(int)value] : "feeType " + value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static long Long(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
    }
}
