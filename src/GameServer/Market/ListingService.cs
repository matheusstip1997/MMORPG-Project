using GameServer.Economy;
using GameServer.Events;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Economy;
using Shared.Simulation.Items;
using Shared.Util;

namespace GameServer.Market;

/// <summary>One listing request as the world hands it over in phase 1, after the auctioneer gate (WP-M4-7).</summary>
public readonly record struct ListingAsk(long CharacterId, int Connection, uint RequestId, byte RegionIndex, bool Blocked, string OpKey);

/// <summary>
/// <b>The only writer of listings</b> (spec 4.5 "listings", R-4.5-01, M4 decision 6; user answers 2026-11-12 and 2026-11-13;
/// WP-M4-7, shared with the stalls of WP-M4-8), on the world thread. It keeps every active listing in memory (loaded at world
/// start) and decides every race there; the database guards are the backstop.
/// <list type="bullet">
/// <item><b>Create</b>: the units of one bag slot go into escrow (<see cref="EscrowService.Hold"/>), the listing fee is destroyed
/// (<see cref="FeeService.Apply"/>, after every check) and the <c>listings</c> row is written, all in the seller's save; answered
/// after the commit.</item>
/// <item><b>Buy</b> (any count, user answer 2026-11-13): the units go to the buyer's bag, or by mail when they do not fit
/// (<see cref="EscrowService.ReleaseUnits"/>; a part leaves the rest in a new escrow); the buyer's payment less the sale tax goes
/// to the seller by mail (<see cref="MailService.Send"/>, reason <c>ListingPurchase</c>, always by mail, M4 decision 4) and the
/// sale tax is destroyed (<see cref="FeeService.Apply"/>, last); the listing row moves and a <c>listing_sales</c> row is written.
/// One transaction: the buyer's save. Answered after the commit.</item>
/// <item><b>Cancel</b> by the seller and <b>expiry</b> (<see cref="ExpireDue"/>): the escrow is released to the seller's mail and
/// the listing row closed in one transaction off the world thread (<see cref="EscrowService.ReleaseToMail"/> with a holder close);
/// the fee is kept. The seller may be offline for an expiry.</item>
/// </list>
/// Per-listing states: active, creating (its save not committed), settling (a purchase saving), closing (a cancel or an expiry
/// saving) and unknown (a save lost with a faulted character: never touched again until a restart reloads it). The seller is
/// never shown to anyone else (rule 8; user answer 2026-11-13).
/// </summary>
public sealed class ListingService : IListingBatchSource
{
    /// <summary>Closed listings remembered, so a late request hears why (sold out, expired) instead of "not found".</summary>
    private const int RememberedClosed = 1024;

    /// <summary>Answers remembered by operation key, so a repeated request gets its first answer.</summary>
    private const int RememberedAnswers = 1024;

    private readonly ListingSettings _settings;
    private readonly InventoryService _inventory;
    private readonly EscrowService _escrow;
    private readonly FeeService _fees;
    private readonly MailService _mail;
    private readonly IClock _clock;
    private readonly EventBus? _events;
    private readonly Func<string, short> _jobNetIdOf;
    private readonly long _maxGold;
    private readonly Dictionary<long, Listing> _byId = new();
    private readonly Dictionary<string, long> _byEscrow = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _byCreateKey = new(StringComparer.Ordinal);
    private readonly SortedSet<(long ExpiresMs, long ListingId)> _expiry = new();
    private readonly Dictionary<long, Pending> _pending = new();
    private readonly Dictionary<long, InFlight> _inFlight = new();
    private readonly Dictionary<long, ListingReason> _closed = new();
    private readonly Queue<long> _closedOrder = new();
    private readonly Dictionary<string, ListingResult?> _answers = new(StringComparer.Ordinal);
    private readonly Queue<string> _answerOrder = new();
    private readonly List<long> _expiring = new();
    private long _nextId = 1;

    public ListingService(ListingSettings settings, InventoryService inventory, EscrowService escrow, FeeService fees, MailService mail,
        IClock clock, EventBus? events, Func<string, short> jobNetIdOf, long maxGold)
    {
        _settings = settings;
        _inventory = inventory;
        _escrow = escrow;
        _fees = fees;
        _mail = mail;
        _clock = clock;
        _events = events;
        _jobNetIdOf = jobNetIdOf;
        _maxGold = maxGold;
    }

    public ListingSettings Settings => _settings;

    /// <summary>True once <see cref="Load"/> ran: before that no request is taken (a listing id could repeat).</summary>
    public bool Loaded { get; private set; }

    /// <summary>Asks a character's save at once (the scheduler's <c>RequestSave</c>; set by the SessionManager).</summary>
    public Action<long, SaveReason>? RequestSave { get; set; }

    /// <summary>An answer for a connection of a character, once its operation committed (set by the world: the outbox).</summary>
    public Action<long, int, ListingResult>? Answer { get; set; }

    /// <summary>Listings in memory that are active (not creating, closing or unknown).</summary>
    public int ActiveCount => _byId.Values.Count(l => l.State is State.Active or State.Settling);

    /// <summary>Starts with the active listings of the database (world start) and the highest listing id ever written.</summary>
    public void Load(IEnumerable<ListingRow> active, long maxListingId)
    {
        foreach (var row in active)
        {
            if (!_escrow.IsHeld(row.EscrowId))
                continue; // its escrow is not held (an inconsistent row): never sold or closed by this run; the database keeps it
            Add(new Listing(row) { State = State.Active });
        }
        _nextId = Math.Max(_nextId, maxListingId + 1);
        Loaded = true;
    }

    /// <summary>The listing with that id while this world holds it (read-only for views and tests).</summary>
    public ListingRow? Find(long listingId) => _byId.TryGetValue(listingId, out var listing) ? listing.Row : null;

    /// <summary>True while the listing can be bought (active, no purchase saving).</summary>
    public bool IsActive(long listingId) => _byId.TryGetValue(listingId, out var listing) && listing.State == State.Active;

    /// <summary>The seller's listings that are on sale (active or a purchase saving), oldest first.</summary>
    public IReadOnlyList<ListingRow> Mine(long sellerId, ListingKind kind) => _byId.Values
        .Where(l => l.Row.SellerId == sellerId && l.Row.Kind == kind && l.State is State.Active or State.Settling)
        .Select(l => l.Row).OrderBy(r => r.ListingId).ToList();

    /// <summary>
    /// A stall's listings on sale (WP-M4-8: the stall's buyers and whether it is empty), oldest first; the auction never shows
    /// them (the search reads kind 1 only) and they never count against the auction cap (user answers in WP-M4-8, 2026-11-13).
    /// </summary>
    public IReadOnlyList<ListingRow> ActiveForStall(string stallId) => _byId.Values
        .Where(l => l.Row.Kind == ListingKind.Stall && l.Row.StallId == stallId && l.State is State.Active or State.Settling)
        .Select(l => l.Row).OrderBy(r => r.ListingId).ToList();

    /// <summary>The listings counted against the seller's cap: on sale, being created or settling.</summary>
    public int CountFor(long sellerId, ListingKind kind) => _byId.Values.Count(l => l.Row.SellerId == sellerId && l.Row.Kind == kind
        && l.State is State.Active or State.Creating or State.Settling);

    /// <summary>
    /// List <paramref name="count"/> units of <paramref name="at"/> at <paramref name="unitPrice"/> each for the duration at
    /// <paramref name="durationIndex"/> (an auction listing). Returns the refusal (or a repeated request's first answer) to send
    /// now, or null when the listing waits for the seller's save (answered through <see cref="Answer"/>).
    /// </summary>
    public ListingResult? Create(in ListingAsk ask, InventoryLocation at, int count, long unitPrice, int durationIndex) =>
        CreateOf(ask, ListingKind.Auction, null, at, count, unitPrice, durationIndex, _settings.MaxActivePerCharacter);

    /// <summary>The create of any listing kind for one of the <c>ListingSettings.durationsMs</c>.</summary>
    public ListingResult? CreateOf(in ListingAsk ask, ListingKind kind, string? stallId, InventoryLocation at, int count, long unitPrice,
        int durationIndex, int cap)
    {
        int durationMs = _settings.DurationAt(durationIndex);
        if (durationMs <= 0 && !Repeated(ask.OpKey, out _))
            return Refuse(ask, ListingAction.Create, ListingReason.ListingDurationInvalid);
        return CreateUntil(ask, kind, stallId, at, count, unitPrice, _clock.UtcNowMs + durationMs, cap, out _);
    }

    /// <summary>
    /// The create of any listing kind until <paramref name="expiresUtcMs"/> set by the caller (WP-M4-8: a stall listing ends at
    /// the stall's close time and counts against the stall's own <paramref name="cap"/>, never the auction's). The listing rides
    /// the seller's save; <paramref name="waitKey"/> is its escrow hold's operation key (<c>InventoryService.IsCommitted</c>), so a
    /// caller's own rows queued for the seller's save now (a stall's <c>shops</c> row) commit in the same transaction.
    /// </summary>
    public ListingResult? CreateUntil(in ListingAsk ask, ListingKind kind, string? stallId, InventoryLocation at, int count,
        long unitPrice, long expiresUtcMs, int cap, out string? waitKey)
    {
        waitKey = null;
        if (Repeated(ask.OpKey, out var first))
            return first;
        long seller = ask.CharacterId;
        if (!Loaded || !_inventory.IsAttached(seller) || !_inventory.HasGold(seller) || !_mail.IsAttached(seller))
            return Refuse(ask, ListingAction.Create, ListingReason.InventoryUnavailable);
        if (ask.Blocked)
            return Refuse(ask, ListingAction.Create, ListingReason.InCombat);
        if (expiresUtcMs <= _clock.UtcNowMs || (kind == ListingKind.Stall) != (stallId != null))
            return Refuse(ask, ListingAction.Create, ListingReason.ListingDurationInvalid);
        var items = _inventory.CurrentInventory(seller);
        if (!items.TryIndex(at.Container, out int index) || at.Slot < 0 || at.Slot >= items.BagAt(index).SlotCount)
            return Refuse(ask, ListingAction.Create, ListingReason.ListingChanged);
        var stack = items.BagAt(index)[at.Slot];
        if (stack.IsEmpty || !_inventory.Rules.TryGetItem(stack.ItemNetId, out var def))
            return Refuse(ask, ListingAction.Create, ListingReason.ListingChanged);
        if (count <= 0 || count > stack.Count || (stack.IsInstance && count != 1))
            return Refuse(ask, ListingAction.Create, ListingReason.ListingCountInvalid);
        bool tainted = stack.IsInstance && _inventory.TryGetLatestProperties(seller, stack.InstanceId, out var properties) && properties.Tainted;
        var offer = ListingMath.ReasonOf(TradeRules.CheckOffer(def.Category, stack.Stolen, tainted, _inventory.IsNonTradable(seller, stack),
            items.Rule(index).IsEquipped));
        if (offer != ListingReason.None)
            return Refuse(ask, ListingAction.Create, offer);
        if (_inventory.Unreserved(seller, at) < count)
            return Refuse(ask, ListingAction.Create, ListingReason.ListingReserved);
        if (!ListingMath.PriceAllowed(unitPrice, count, _settings.MaxUnitPrice, _maxGold))
            return Refuse(ask, ListingAction.Create, ListingReason.ListingPriceInvalid);
        if (CountFor(seller, kind) >= cap)
            return Refuse(ask, ListingAction.Create, ListingReason.ListingCap);
        if (!_mail.HasRoom(seller))
            return Refuse(ask, ListingAction.Create, ListingReason.MailboxFull); // a listing is a new mail source (spec 4.5)
        ListingMath.TryTotal(unitPrice, count, _maxGold, out long total);
        var fee = _fees.Quote(seller, ask.RegionIndex, FeeType.Listing, total);
        if (_inventory.Gold(seller) < fee.Total)
            return Refuse(ask, ListingAction.Create, ListingReason.NotEnoughGold);

        long id = _nextId;
        var hold = _escrow.Hold(seller, new[] { new ReserveItem(at, count) }, 0,
            kind == ListingKind.Stall ? EscrowHolderKind.Stall : EscrowHolderKind.Listing, id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ask.OpKey);
        if (!hold.Applied)
            return Refuse(ask, ListingAction.Create, hold.Refusal == EscrowRefusal.NotOnline
                ? ListingReason.InventoryUnavailable : ListingReason.ListingChanged);
        _nextId++;
        // Every check passed and the hold is applied: the fee now (an applied fee has no undo; WP-M4-1 review).
        var applied = _fees.Apply(seller, fee, ask.OpKey, $"listing:{id}");
        if (!applied.Ok)
            throw new InvalidOperationException($"listing {id}: the fee was checked against the gold and still refused");
        long now = _clock.UtcNowMs;
        var row = new ListingRow(id, ask.OpKey, kind, stallId, seller, ask.OpKey, def.Id, def.NetId, (short)def.Category, stack.Rarity,
            def.MinLevel, def.JobId.Length == 0 ? (short)0 : _jobNetIdOf(def.JobId),
            0, // TODO(WP-M4-13): ItemDef.armorTier + 1 once the field merges (decision 9a)
            unitPrice, count, count, fee.Total, now, expiresUtcMs);
        var listing = new Listing(row) { State = State.Creating };
        Add(listing);
        _byCreateKey[ask.OpKey] = id;
        PendingOf(seller).Creates.Add(new PendingCreate(id, ask));
        Remember(ask.OpKey, null);
        waitKey = hold.WaitKey;
        RequestSave?.Invoke(seller, SaveReason.Class1);
        return null;
    }

    /// <summary>
    /// Buy <paramref name="count"/> units of <paramref name="listingId"/> at the unit price the buyer saw. Returns the refusal (or a
    /// repeated request's first answer) to send now, or null when the purchase waits for the buyer's save.
    /// </summary>
    public ListingResult? Buy(in ListingAsk ask, long listingId, int count, long seenUnitPrice)
    {
        if (Repeated(ask.OpKey, out var first))
            return first;
        long buyer = ask.CharacterId;
        if (!Loaded || !_inventory.IsAttached(buyer) || !_inventory.HasGold(buyer) || !_mail.IsAttached(buyer))
            return Refuse(ask, ListingAction.Buy, ListingReason.InventoryUnavailable, listingId);
        if (ask.Blocked)
            return Refuse(ask, ListingAction.Buy, ListingReason.InCombat, listingId);
        if (!_byId.TryGetValue(listingId, out var listing) || listing.State is State.Creating or State.Unknown)
            return Refuse(ask, ListingAction.Buy, _closed.TryGetValue(listingId, out var why) ? why : ListingReason.ListingNotFound, listingId);
        if (listing.State == State.Settling)
            return Refuse(ask, ListingAction.Buy, ListingReason.ListingBusy, listingId);
        if (listing.State == State.Closing)
            return Refuse(ask, ListingAction.Buy, listing.ClosingAs == ListingState.Expired ? ListingReason.ListingExpired
                : ListingReason.ListingNotFound, listingId);
        var row = listing.Row;
        if (_clock.UtcNowMs >= row.ExpiresUtcMs)
            return Refuse(ask, ListingAction.Buy, ListingReason.ListingExpired, listingId);
        if (row.SellerId == buyer)
            return Refuse(ask, ListingAction.Buy, ListingReason.ListingOwn, listingId);
        var check = ListingMath.CheckBuy(count, row.CountLeft, seenUnitPrice, row.UnitPrice);
        if (check != ListingReason.None)
            return Refuse(ask, ListingAction.Buy, check, listingId);
        if (!ListingMath.TryTotal(row.UnitPrice, count, _maxGold, out long price))
            return Refuse(ask, ListingAction.Buy, ListingReason.ListingPriceInvalid, listingId);
        if (_inventory.Gold(buyer) < price)
            return Refuse(ask, ListingAction.Buy, ListingReason.NotEnoughGold, listingId);
        var tax = _fees.Quote(row.SellerId, ask.RegionIndex, FeeType.SaleTax, price); // the seller's Merchant discount (R-4.2-05)
        long proceeds = ListingMath.Proceeds(price, tax.Total);
        if (_escrow.Find(row.EscrowId) is not { Items.Count: 1 } held)
            return Refuse(ask, ListingAction.Buy, ListingReason.ListingBusy, listingId);

        // Every draft is checked before anything moves (a refused send could not be undone after the release).
        var proceedsDraft = proceeds > 0 ? ProceedsDraft(row, buyer, count, tax.Total, proceeds, ask.OpKey) : null;
        if (proceedsDraft != null && _mail.Rules.Check(proceedsDraft) is { } proceedsError)
            throw new InvalidOperationException($"listing {row.ListingId}: the proceeds mail is invalid: {proceedsError}");
        string remainder = ask.OpKey + ":rest";
        bool toMail = false;
        var release = _escrow.ReleaseUnits(row.EscrowId, buyer, count, false, ask.OpKey, remainder, _settings.OwnerHistoryMax, out var units);
        if (release.Refusal == EscrowRefusal.BagFull)
        {
            // Bag full: the units arrive by mail in the same save (the buyer carries it; spec 4.5).
            var purchaseDraft = PurchaseDraft(row, buyer, held.Items[0] with { Count = count }, ask.OpKey);
            if (_mail.Rules.Check(purchaseDraft) is { } purchaseError)
                throw new InvalidOperationException($"listing {row.ListingId}: the purchase mail is invalid: {purchaseError}");
            release = _escrow.ReleaseUnits(row.EscrowId, buyer, count, true, ask.OpKey, remainder, _settings.OwnerHistoryMax, out units);
            if (release.Applied)
            {
                toMail = true;
                _mail.Send(buyer, PurchaseDraft(row, buyer, units[0], ask.OpKey));
            }
        }
        if (!release.Applied)
        {
            return Refuse(ask, ListingAction.Buy, release.Refusal switch
            {
                EscrowRefusal.NotOnline => ListingReason.InventoryUnavailable,
                EscrowRefusal.NotHeld => ListingReason.ListingBusy,
                _ => ListingReason.ListingChanged,
            }, listingId);
        }
        if (proceedsDraft != null)
        {
            var pay = _inventory.TryRemoveGold(buyer, proceeds, PayKey(ask.OpKey), InventoryReason.ListingPurchase, $"listing:{row.ListingId}");
            if (!pay.Applied)
                throw new InvalidOperationException($"listing {row.ListingId}: the payment was checked against the gold and still refused");
            _mail.Send(buyer, proceedsDraft);
        }
        if (!_fees.Apply(buyer, tax, ask.OpKey, $"listing:{row.ListingId}").Ok)
            throw new InvalidOperationException($"listing {row.ListingId}: the sale tax was checked against the gold and still refused");

        int left = row.CountLeft - count;
        string newEscrow = left > 0 ? remainder : row.EscrowId;
        var sale = new ListingSaleWrite(ask.OpKey, row.ListingId, buyer, count, row.UnitPrice, price, tax.Total, toMail, row.EscrowId,
            row.CountLeft, newEscrow, left, _clock.UtcNowMs);
        listing.State = State.Settling;
        PendingOf(buyer).Sales.Add(new PendingSale(sale, ask));
        Remember(ask.OpKey, null);
        RequestSave?.Invoke(buyer, SaveReason.Class1);
        return null;
    }

    /// <summary>The seller cancels a listing: the units left go back by mail (the fee is kept). Null when the release waits.</summary>
    public ListingResult? Cancel(in ListingAsk ask, long listingId)
    {
        if (Repeated(ask.OpKey, out var first))
            return first;
        if (!Loaded)
            return Refuse(ask, ListingAction.Cancel, ListingReason.InventoryUnavailable, listingId);
        if (!_byId.TryGetValue(listingId, out var listing) || listing.Row.SellerId != ask.CharacterId
            || listing.State is State.Creating or State.Unknown)
            return Refuse(ask, ListingAction.Cancel, listing != null && listing.Row.SellerId == ask.CharacterId
                ? ListingReason.ListingBusy
                : _closed.TryGetValue(listingId, out var why) ? why : ListingReason.ListingNotFound, listingId);
        if (listing.State == State.Settling)
            return Refuse(ask, ListingAction.Cancel, ListingReason.ListingBusy, listingId);
        if (listing.State == State.Closing)
            return Refuse(ask, ListingAction.Cancel, listing.ClosingAs == ListingState.Expired ? ListingReason.ListingExpired
                : ListingReason.ListingNotFound, listingId);
        if (!StartClose(listing, ListingState.Cancelled, ask.OpKey))
            return Refuse(ask, ListingAction.Cancel, ListingReason.ListingBusy, listingId);
        listing.CloseAsk = ask;
        Remember(ask.OpKey, null);
        return null;
    }

    /// <summary>
    /// The expiry job (phase 7): listings past their expiry start closing, at most <c>ListingSettings.expiryBatch</c> per tick,
    /// oldest first; one whose purchase is saving waits for it.
    /// </summary>
    public void ExpireDue(long nowMs)
    {
        if (!Loaded)
            return;
        _expiring.Clear();
        foreach (var (expiresMs, id) in _expiry)
        {
            if (expiresMs > nowMs || _expiring.Count >= _settings.ExpiryBatch)
                break;
            if (_byId.TryGetValue(id, out var listing) && listing.State == State.Active)
                _expiring.Add(id);
        }
        foreach (long id in _expiring)
        {
            var listing = _byId[id];
            StartClose(listing, ListingState.Expired, $"listingExpire:{id}");
        }
    }

    /// <summary>Test and dev console hook (WP-M3-13 style): the seller's active listings expire now, through <see cref="ExpireDue"/>.</summary>
    public int ExpireNow(long sellerId, long nowMs)
    {
        int moved = 0;
        foreach (var listing in _byId.Values.Where(l => l.Row.SellerId == sellerId && l.State == State.Active).ToList())
        {
            _expiry.Remove((listing.Row.ExpiresUtcMs, listing.Row.ListingId));
            listing.Row = listing.Row with { ExpiresUtcMs = Math.Min(listing.Row.ExpiresUtcMs, nowMs) };
            _expiry.Add((listing.Row.ExpiresUtcMs, listing.Row.ListingId));
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// <see cref="EscrowService.MailReleaseSettled"/>: a cancel's or an expiry's release to mail committed (the listing is closed in
    /// the same transaction) or found the escrow gone (the listing becomes unknown until a restart).
    /// </summary>
    public void OnMailReleaseSettled(string escrowId, bool released)
    {
        if (!_byEscrow.TryGetValue(escrowId, out long id) || !_byId.TryGetValue(id, out var listing) || listing.State != State.Closing)
            return;
        if (!released)
        {
            listing.State = State.Unknown;
            if (listing.CloseAsk is { } failed)
                Deliver(failed, new ListingResult
                {
                    RequestId = failed.RequestId, Action = ListingAction.Cancel, Reason = ListingReason.ListingBusy, ListingId = id,
                });
            return;
        }
        var row = listing.Row;
        Remove(listing, listing.ClosingAs == ListingState.Expired ? ListingReason.ListingExpired : ListingReason.ListingNotFound);
        if (listing.ClosingAs == ListingState.Expired)
            _events?.Publish(new ListingExpired(id, row.SellerId, (short)row.Kind, (ushort)row.ItemNetId, row.CountLeft, row.StallId), row.SellerId);
        else
            _events?.Publish(new ListingCancelled(id, row.SellerId, (short)row.Kind, (ushort)row.ItemNetId, row.CountLeft, row.StallId), row.SellerId);
        if (listing.CloseAsk is { } ask)
            Deliver(ask, new ListingResult
            {
                RequestId = ask.RequestId, Action = ListingAction.Cancel, ListingId = id, Count = (ushort)Math.Min(ushort.MaxValue, row.CountLeft),
            });
    }

    public ListingBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_pending.Remove(characterId, out var pending))
            return null;
        _inFlight[characterId] = new InFlight(saveId, pending);
        return new ListingBatch(pending.Creates.Select(c => _byId[c.ListingId].Row).ToList(), pending.Sales.Select(s => s.Write).ToList());
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_inFlight.TryGetValue(characterId, out var inFlight) || inFlight.SaveId != saveId)
            return; // a save without a batch, or a stale completion: idempotent
        _inFlight.Remove(characterId);
        foreach (var create in inFlight.Batch.Creates)
        {
            if (!_byId.TryGetValue(create.ListingId, out var listing) || listing.State != State.Creating)
                continue;
            listing.State = State.Active;
            var row = listing.Row;
            _events?.Publish(new ListingCreated(row.ListingId, row.SellerId, (short)row.Kind, (ushort)row.ItemNetId, row.CountInitial,
                row.UnitPrice, row.FeePaid, row.ExpiresUtcMs, row.StallId), row.SellerId, create.Ask.RegionIndex);
            Deliver(create.Ask, new ListingResult
            {
                RequestId = create.Ask.RequestId, Action = ListingAction.Create, ListingId = row.ListingId,
                Count = (ushort)Math.Min(ushort.MaxValue, row.CountInitial), Fee = row.FeePaid,
            });
        }
        foreach (var pendingSale in inFlight.Batch.Sales)
        {
            var sale = pendingSale.Write;
            if (!_byId.TryGetValue(sale.ListingId, out var listing) || listing.State != State.Settling)
                continue;
            var row = listing.Row;
            if (sale.NewCountLeft == 0)
                Remove(listing, ListingReason.ListingSold);
            else
            {
                _byEscrow.Remove(row.EscrowId);
                listing.Row = row with { EscrowId = sale.NewEscrowId, CountLeft = sale.NewCountLeft };
                _byEscrow[sale.NewEscrowId] = row.ListingId;
                listing.State = State.Active;
            }
            _events?.Publish(new ListingSold(row.ListingId, row.SellerId, sale.BuyerId, (short)row.Kind, (ushort)row.ItemNetId, sale.Count,
                sale.Price, sale.Tax, sale.ToMail, sale.NewCountLeft, row.StallId), sale.BuyerId, pendingSale.Ask.RegionIndex);
            Deliver(pendingSale.Ask, new ListingResult
            {
                RequestId = pendingSale.Ask.RequestId, Action = ListingAction.Buy, ListingId = row.ListingId,
                Count = (ushort)Math.Min(ushort.MaxValue, sale.Count), Price = sale.Price, Tax = sale.Tax, ToMail = sale.ToMail,
            });
        }
    }

    /// <summary>
    /// The character's session ended without its save (a fault or lost ownership): creates and purchases no save took are undone
    /// in memory (the escrow and the inventory undo their halves); those in a save in flight become unknown until a restart.
    /// </summary>
    public void Forget(long characterId)
    {
        if (_pending.Remove(characterId, out var pending))
        {
            foreach (var create in pending.Creates)
            {
                if (_byId.TryGetValue(create.ListingId, out var listing))
                    Remove(listing, ListingReason.ListingNotFound);
                Forget(create.Ask.OpKey);
            }
            foreach (var sale in pending.Sales)
            {
                if (_byId.TryGetValue(sale.Write.ListingId, out var listing) && listing.State == State.Settling)
                    listing.State = State.Active;
                Forget(sale.Ask.OpKey);
            }
        }
        if (_inFlight.Remove(characterId, out var inFlight))
        {
            foreach (var create in inFlight.Batch.Creates)
                if (_byId.TryGetValue(create.ListingId, out var listing))
                    listing.State = State.Unknown;
            foreach (var sale in inFlight.Batch.Sales)
                if (_byId.TryGetValue(sale.Write.ListingId, out var listing))
                    listing.State = State.Unknown;
        }
    }

    /// <summary>The gold key of a purchase's payment into the proceeds mail.</summary>
    public static string PayKey(string buyOpKey) => buyOpKey + ":pay";

    private bool StartClose(Listing listing, ListingState state, string opKey)
    {
        var refusal = _escrow.ReleaseToMail(listing.Row.EscrowId, listing.Row.SellerId, (short)MailKind.ListingReturn, opKey,
            new EscrowHolderClose((short)state));
        if (refusal != EscrowRefusal.None)
            return false;
        listing.State = State.Closing;
        listing.ClosingAs = state;
        return true;
    }

    private MailDraft ProceedsDraft(ListingRow row, long buyer, int count, long tax, long proceeds, string opKey) =>
        new(row.SellerId, MailKind.ListingProceeds, MailSubjects.ListingProceeds, new[]
        {
            new MailArg("item", MailArgKind.Item, row.ItemNetId),
            new MailArg("count", MailArgKind.Number, count),
            new MailArg("tax", MailArgKind.Number, tax),
        }, Array.Empty<MailAttachment>(), proceeds, opKey + ":proceeds", $"listing:{row.ListingId}:buyer:{buyer}");

    private static MailDraft PurchaseDraft(ListingRow row, long buyer, EscrowItemRow units, string opKey) =>
        new(buyer, MailKind.ListingPurchase, MailSubjects.ListingPurchase, Array.Empty<MailArg>(), new[]
        {
            new MailAttachment(units.DefId, (byte)units.Rarity, units.Count, units.Stolen, units.NonTradable, units.InstanceId,
                units.PropertiesJson),
        }, 0, opKey + ":purchase", $"listing:{row.ListingId}");

    private void Add(Listing listing)
    {
        var row = listing.Row;
        _byId[row.ListingId] = listing;
        _byEscrow[row.EscrowId] = row.ListingId;
        _expiry.Add((row.ExpiresUtcMs, row.ListingId));
    }

    private void Remove(Listing listing, ListingReason why)
    {
        var row = listing.Row;
        _byId.Remove(row.ListingId);
        _byEscrow.Remove(row.EscrowId);
        _expiry.Remove((row.ExpiresUtcMs, row.ListingId));
        _byCreateKey.Remove(row.CreateOpKey);
        if (_closed.TryAdd(row.ListingId, why))
        {
            _closedOrder.Enqueue(row.ListingId);
            while (_closedOrder.Count > RememberedClosed)
                _closed.Remove(_closedOrder.Dequeue());
        }
    }

    private void Deliver(in ListingAsk ask, ListingResult answer)
    {
        if (_answers.ContainsKey(ask.OpKey))
            _answers[ask.OpKey] = answer;
        Answer?.Invoke(ask.CharacterId, ask.Connection, answer);
    }

    private ListingResult Refuse(in ListingAsk ask, ListingAction action, ListingReason reason, long listingId = 0) =>
        new() { RequestId = ask.RequestId, Action = action, Reason = reason, ListingId = listingId };

    /// <summary>True for an operation key seen before: <paramref name="first"/> its answer, or null while it still saves.</summary>
    private bool Repeated(string opKey, out ListingResult? first) => _answers.TryGetValue(opKey, out first);

    private void Remember(string opKey, ListingResult? answer)
    {
        if (!_answers.TryAdd(opKey, answer))
            return;
        _answerOrder.Enqueue(opKey);
        while (_answerOrder.Count > RememberedAnswers)
            _answers.Remove(_answerOrder.Dequeue());
    }

    private void Forget(string opKey) => _answers.Remove(opKey);

    private Pending PendingOf(long characterId)
    {
        if (!_pending.TryGetValue(characterId, out var pending))
            _pending.Add(characterId, pending = new Pending());
        return pending;
    }

    private enum State : byte
    {
        Creating,
        Active,
        Settling,
        Closing,
        Unknown,
    }

    private sealed class Listing(ListingRow row)
    {
        public ListingRow Row { get; set; } = row;
        public State State { get; set; }
        public ListingState ClosingAs { get; set; }
        public ListingAsk? CloseAsk { get; set; }
    }

    private sealed record PendingCreate(long ListingId, ListingAsk Ask);

    private sealed record PendingSale(ListingSaleWrite Write, ListingAsk Ask);

    private sealed class Pending
    {
        public List<PendingCreate> Creates { get; } = new();
        public List<PendingSale> Sales { get; } = new();
    }

    private sealed record InFlight(Guid SaveId, Pending Batch);
}
