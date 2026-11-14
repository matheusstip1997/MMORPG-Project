using GameServer.Entities;
using GameServer.Events;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Items;
using Shared.Util;

namespace GameServer.Market;

/// <summary>One stall place of the world: a <c>stall</c> marker, named on the wire by its ordinal (<c>Shared.World.StallPlaces</c>).</summary>
public sealed record StallPlace(int Ordinal, string MarkerId, FixVec2 Position, ushort Facing)
{
    public int XCm => FixedPoint.ToCmRound(Position.X);

    public int YCm => FixedPoint.ToCmRound(Position.Y);
}

/// <summary>
/// <b>The only writer of stalls</b> (spec 4.5 "M4 stalls", R-4.5-01, R-4.5-03; M4 decision 7 and the user answers of 2026-11-13;
/// WP-M4-8), on the world thread. A stall is a <see cref="ShopStall"/> entity on a free stall place of a city, open for
/// <c>StallSettings.durationMs</c>, selling through the shared listing model (<see cref="ListingService"/>, the only writer of
/// listings: every listing of a stall has kind <c>Stall</c>, the stall's id and the stall's close time as its expiry).
/// <list type="bullet">
/// <item><b>Open</b> on a free place in reach, one stall per character: the stall's first listing is created
/// (<see cref="ListingService.CreateUntil"/>: escrow hold, listing fee) and the stall's <c>shops</c> row is queued for the same
/// save, so both commit in one transaction; the entity appears and the opener is answered after the commit.</item>
/// <item><b>Add</b>, <b>cancel</b> (the units return by mail, the fee is kept) and <b>close</b> by the owner, at the stall only
/// (user answer 2026-11-13); <b>browse</b> and <b>buy</b> by anyone in reach (<see cref="ListingService.Buy"/>: the proceeds go to
/// the seller by mail, so the owner may be offline, M4 decision 7).</item>
/// <item>A stall <b>closes</b> when its last listing is gone (sold or cancelled, user answer 2026-11-13), when its time runs out
/// (its listings expire with it through the listing expiry job and return by mail) or when its owner closed it. The close is
/// written off the tick on its own transaction (<see cref="IShopStore"/>); the place and the owner are free once it committed.</item>
/// </list>
/// Requests reach the listing model with a tagged request id (<see cref="Tag"/>), so their answers come back here
/// (<see cref="TryTakeListingAnswer"/>) and leave as <see cref="StallResult"/>. Every race is decided here in memory; the
/// database's unique indexes are the backstop.
/// </summary>
public sealed class StallService : IShopBatchSource, IEventConsumer<ListingCreated>, IEventConsumer<ListingSold>,
    IEventConsumer<ListingCancelled>, IEventConsumer<ListingExpired>
{
    /// <summary>The bit that marks a listing request id as a stall's (client request ids never reach it).</summary>
    public const uint Tag = 0x8000_0000u;

    private readonly StallSettings _settings;
    private readonly ListingService _listings;
    private readonly IClock _clock;
    private readonly EventBus? _events;
    private readonly StallPlace[] _places;
    private readonly PlaceState[] _placeStates;
    private readonly Dictionary<string, int> _placeByMarker = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, Stall> _byEntity = new();
    private readonly Dictionary<string, Stall> _byShopId = new(StringComparer.Ordinal);
    private readonly Dictionary<long, OwnerState> _owners = new();
    private readonly Dictionary<long, List<Opening>> _pendingOpens = new();
    private readonly Dictionary<long, (Guid SaveId, List<Opening> Opens)> _inFlightOpens = new();
    private readonly Dictionary<(long CharacterId, int Connection, uint Tagged), Forwarded> _forwarded = new();
    private readonly HashSet<string> _opsInFlight = new(StringComparer.Ordinal);
    private readonly HashSet<Stall> _dirty = new();
    private readonly List<Stall> _scratch = new();
    private readonly Queue<CloseWrite> _closeQueue = new();
    private readonly List<CloseWrite> _closeWaiting = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(CloseWrite Write, bool Ok)> _closeDone = new();
    private Task? _closeTask;
    private bool _stopping;
    private uint _nextTag = 1;

    public StallService(StallSettings settings, ListingService listings, IClock clock, EventBus? events, IReadOnlyList<StallPlace> places)
    {
        _settings = settings;
        _listings = listings;
        _clock = clock;
        _events = events;
        _places = places.OrderBy(p => p.Ordinal).ToArray();
        for (int i = 0; i < _places.Length; i++)
        {
            if (_places[i].Ordinal != i)
                throw new ArgumentException($"stall place ordinals must run 0..{_places.Length - 1}", nameof(places));
            _placeByMarker[_places[i].MarkerId] = i;
        }
        _placeStates = new PlaceState[_places.Length];
    }

    public StallSettings Settings => _settings;

    /// <summary>The stall places of the world, by ordinal.</summary>
    public IReadOnlyList<StallPlace> Places => _places;

    /// <summary>True once <see cref="Load"/> ran: before that no request is taken (a place could still hold a stall).</summary>
    public bool Loaded { get; private set; }

    /// <summary>Writes the closes off the tick (null: closes stay in memory; worlds built for tests without a database).</summary>
    public IShopStore? Store { get; set; }

    /// <summary>Builds and registers a stall's entity on its place (set by the world).</summary>
    public Func<ShopRow, StallPlace, ShopStall>? Spawn { get; set; }

    /// <summary>Removes a stall's entity from the world (set by the world).</summary>
    public Action<ShopStall>? Despawn { get; set; }

    /// <summary>An answer for a connection of a character, once its operation committed (set by the world: the outbox).</summary>
    public Action<long, int, StallResult>? Answer { get; set; }

    /// <summary>Open stalls (not closed yet), in opening order.</summary>
    public int OpenCount => _byShopId.Count;

    /// <summary>Stall closes not written yet (in flight, queued or waiting to be tried again).</summary>
    public int ClosesPending => _closeQueue.Count + _closeWaiting.Count + (_closeTask is { IsCompleted: false } ? 1 : 0);

    /// <summary>The open stall with that entity id.</summary>
    public ShopStall? Find(uint entityId) => _byEntity.TryGetValue(entityId, out var stall) ? stall.Entity : null;

    /// <summary>The open stall with that id (its <c>shops</c> row).</summary>
    public ShopStall? FindShop(string shopId) => _byShopId.TryGetValue(shopId, out var stall) ? stall.Entity : null;

    /// <summary>The open stall of a character, if any.</summary>
    public ShopStall? StallOf(long ownerId) => _owners.TryGetValue(ownerId, out var owner) && owner.Stall != null ? owner.Stall.Entity : null;

    /// <summary>True while a place is free for a new stall (no stall open, opening or closing there).</summary>
    public bool IsFree(int ordinal) => ordinal >= 0 && ordinal < _places.Length && _placeStates[ordinal] == PlaceState.Free;

    /// <summary>
    /// Starts with the open stalls of the database (world start, after the listings were loaded): each comes back on its place,
    /// unless it is past its time, its place is gone or it has no listing on sale, when its close is written (crash cleanup).
    /// </summary>
    public void Load(IEnumerable<ShopRow> open)
    {
        long now = _clock.UtcNowMs;
        foreach (var row in open)
        {
            bool known = _placeByMarker.TryGetValue(row.PlaceMarkerId, out int ordinal);
            if (!known || row.ClosesUtcMs <= now || _listings.ActiveForStall(row.ShopId).Count == 0
                || _placeStates[ordinal] != PlaceState.Free || _owners.ContainsKey(row.OwnerId))
            {
                // Its place is blocked while the close writes, if it has one (a stale row there must close before a new open).
                if (known && _placeStates[ordinal] == PlaceState.Free)
                    _placeStates[ordinal] = PlaceState.Closing;
                _owners.TryAdd(row.OwnerId, new OwnerState(OwnerPhase.Closing, null));
                QueueClose(new CloseWrite(row.ShopId, row.OwnerId, known ? ordinal : -1, ShopCloseReason.Stale, now));
                continue;
            }
            Admit(row, ordinal);
        }
        Loaded = true;
    }

    /// <summary>
    /// Opens a stall on <paramref name="place"/> with its first listing: <paramref name="count"/> units of <paramref name="at"/> at
    /// <paramref name="unitPrice"/> each. <paramref name="xCm"/>, <paramref name="yCm"/> are the player's position. Returns the
    /// refusal to send now, or null when the open waits for the owner's save (answered through <see cref="Answer"/>).
    /// </summary>
    public StallResult? Open(in ListingAsk ask, string ownerName, int xCm, int yCm, int place, InventoryLocation at, int count, long unitPrice)
    {
        var refusal = CheckOwnerAsk(ask, StallAction.Open);
        if (refusal != null)
            return refusal;
        if (place < 0 || place >= _places.Length)
            return Refuse(ask, StallAction.Open, StallReason.StallNotFound);
        if (!InReach(_places[place], xCm, yCm))
            return Refuse(ask, StallAction.Open, StallReason.OutOfRange);
        if (_owners.TryGetValue(ask.CharacterId, out var owner))
            return Refuse(ask, StallAction.Open, owner.Phase == OwnerPhase.Open ? StallReason.StallOwnAlready : StallReason.StallBusy);
        if (_placeStates[place] != PlaceState.Free)
            return Refuse(ask, StallAction.Open, StallReason.StallPlaceTaken);

        long now = _clock.UtcNowMs;
        var row = new ShopRow(ask.OpKey, ask.CharacterId, _places[place].MarkerId, ownerName, now, now + _settings.DurationMs);
        uint tagged = NextTag();
        var listingAsk = ask with { RequestId = tagged };
        var answer = _listings.CreateUntil(listingAsk, ListingKind.Stall, row.ShopId, at, count, unitPrice, row.ClosesUtcMs,
            _settings.ListingsPerStall, out _);
        if (answer != null)
            return ListingRefusal(ask, StallAction.Open, answer);
        var opening = new Opening(row, place, ask, tagged);
        _placeStates[place] = PlaceState.Opening;
        _owners[ask.CharacterId] = new OwnerState(OwnerPhase.Opening, null);
        if (!_pendingOpens.TryGetValue(ask.CharacterId, out var list))
            _pendingOpens.Add(ask.CharacterId, list = new List<Opening>(1));
        list.Add(opening);
        _forwarded[(ask.CharacterId, ask.Connection, tagged)] = new Forwarded(ask.RequestId, StallAction.Open, null, opening);
        return null;
    }

    /// <summary>The owner adds a listing to their open stall in reach. Null when it waits for the save.</summary>
    public StallResult? Add(in ListingAsk ask, int xCm, int yCm, uint stallEntityId, InventoryLocation at, int count, long unitPrice)
    {
        var refusal = Gate(ask, StallAction.Add, stallEntityId, xCm, yCm, ownerOnly: true, out var stall);
        if (refusal != null)
            return refusal;
        if (_opsInFlight.Contains(ask.OpKey))
            return Refuse(ask, StallAction.Add, StallReason.StallBusy); // the same request again while it saves
        uint tagged = NextTag();
        var answer = _listings.CreateUntil(ask with { RequestId = tagged }, ListingKind.Stall, stall!.Row.ShopId, at, count, unitPrice,
            stall.Row.ClosesUtcMs, _settings.ListingsPerStall, out _);
        if (answer != null)
            return ListingRefusal(ask, StallAction.Add, answer);
        stall.PendingAdds++;
        _forwarded[(ask.CharacterId, ask.Connection, tagged)] = new Forwarded(ask.RequestId, StallAction.Add, stall, null, ask.OpKey);
        _opsInFlight.Add(ask.OpKey);
        return null;
    }

    /// <summary>The owner cancels one listing of their stall in reach: its units return by mail, the fee is kept.</summary>
    public StallResult? Cancel(in ListingAsk ask, int xCm, int yCm, uint stallEntityId, long listingId)
    {
        var refusal = Gate(ask, StallAction.Cancel, stallEntityId, xCm, yCm, ownerOnly: true, out var stall);
        if (refusal != null)
            return refusal;
        if (_opsInFlight.Contains(ask.OpKey))
            return Refuse(ask, StallAction.Cancel, StallReason.StallBusy); // the same request again while it saves
        if (_listings.Find(listingId) is not { } row || row.StallId != stall!.Row.ShopId)
            return Refuse(ask, StallAction.Cancel, StallReason.StallListingRefused, ListingReason.ListingNotFound, listingId);
        uint tagged = NextTag();
        var answer = _listings.Cancel(ask with { RequestId = tagged }, listingId);
        if (answer != null)
            return ListingRefusal(ask, StallAction.Cancel, answer);
        _forwarded[(ask.CharacterId, ask.Connection, tagged)] = new Forwarded(ask.RequestId, StallAction.Cancel, stall, null, ask.OpKey);
        _opsInFlight.Add(ask.OpKey);
        return null;
    }

    /// <summary>
    /// The owner closes their stall in reach: every listing on sale is cancelled (the units return by mail) and the stall closes
    /// once none is left; answered then. A listing whose purchase is saving is cancelled once it settled.
    /// </summary>
    public StallResult? Close(in ListingAsk ask, int xCm, int yCm, uint stallEntityId)
    {
        var refusal = Gate(ask, StallAction.Close, stallEntityId, xCm, yCm, ownerOnly: true, out var stall);
        if (refusal != null)
            return refusal;
        stall!.ClosingByOwner = true;
        stall.CloseAsk = ask;
        _dirty.Add(stall);
        CancelOnSale(stall);
        if (IsEmpty(stall))
            CloseStall(stall, ShopCloseReason.Owner);
        return null;
    }

    /// <summary>Buys units of a listing of the stall in reach (any player but its owner, whom the listing model refuses).</summary>
    public StallResult? Buy(in ListingAsk ask, int xCm, int yCm, uint stallEntityId, long listingId, int count, long unitPrice)
    {
        var refusal = Gate(ask, StallAction.Buy, stallEntityId, xCm, yCm, ownerOnly: false, out var stall);
        if (refusal != null)
            return refusal;
        if (_opsInFlight.Contains(ask.OpKey))
            return Refuse(ask, StallAction.Buy, StallReason.StallBusy); // the same request again while it saves
        if (_listings.Find(listingId) is not { } row || row.StallId != stall!.Row.ShopId)
            return Refuse(ask, StallAction.Buy, StallReason.StallListingRefused, ListingReason.ListingNotFound, listingId);
        uint tagged = NextTag();
        var answer = _listings.Buy(ask with { RequestId = tagged }, listingId, count, unitPrice);
        if (answer != null)
            return ListingRefusal(ask, StallAction.Buy, answer);
        _forwarded[(ask.CharacterId, ask.Connection, tagged)] = new Forwarded(ask.RequestId, StallAction.Buy, stall, null, ask.OpKey);
        _opsInFlight.Add(ask.OpKey);
        return null;
    }

    /// <summary>
    /// A player looks at the stall in reach: it is answered with a state now (the world builds it) and again on every change until
    /// it leaves, goes out of reach or the stall closes. Returns the refusal, or null with <paramref name="stall"/> set.
    /// </summary>
    public StallResult? Browse(in ListingAsk ask, int xCm, int yCm, uint stallEntityId, out ShopStall? stall)
    {
        stall = null;
        if (!Loaded)
            return Refuse(ask, StallAction.Browse, StallReason.InteractionUnavailable);
        var refusal = CheckStall(ask, StallAction.Browse, stallEntityId, xCm, yCm, ownerOnly: false, out var open, allowClosing: true);
        if (refusal != null)
            return refusal;
        var viewer = new Viewer(ask.CharacterId, ask.Connection);
        if (!open!.Viewers.Contains(viewer))
            open.Viewers.Add(viewer);
        stall = open.Entity;
        return null;
    }

    /// <summary>A player stopped looking at a stall (the window closed, out of reach, gone).</summary>
    public void Leave(long characterId, uint stallEntityId)
    {
        if (_byEntity.TryGetValue(stallEntityId, out var stall))
            stall.Viewers.RemoveAll(v => v.CharacterId == characterId);
    }

    /// <summary>The players looking at a stall (the world drops those out of reach and sends them the stall's changes).</summary>
    public IReadOnlyList<Viewer> ViewersOf(ShopStall stall) =>
        _byShopId.TryGetValue(stall.ShopId, out var open) ? open.Viewers : Array.Empty<Viewer>();

    /// <summary>Drops a viewer (out of reach, disconnected).</summary>
    public void DropViewer(ShopStall stall, Viewer viewer)
    {
        if (_byShopId.TryGetValue(stall.ShopId, out var open))
            open.Viewers.Remove(viewer);
    }

    /// <summary>The open stalls into <paramref name="into"/> (cleared first; a list the caller reuses, so a tick allocates nothing).</summary>
    public void CopyOpen(List<ShopStall> into)
    {
        into.Clear();
        foreach (var stall in _byShopId.Values)
            into.Add(stall.Entity);
    }

    /// <summary>True when the stall's owner asked to close it (no buy or add is taken any more).</summary>
    public bool IsClosing(ShopStall stall) => _byShopId.TryGetValue(stall.ShopId, out var open) && open.ClosingByOwner;

    /// <summary>The stalls whose listings changed since the last call (the world sends their state to the viewers and the owner).</summary>
    public IReadOnlyList<ShopStall> TakeDirty(List<ShopStall> into)
    {
        into.Clear();
        foreach (var stall in _dirty)
            into.Add(stall.Entity);
        _dirty.Clear();
        return into;
    }

    /// <summary>
    /// The listing model answered a request: true when it was one of a stall's (its tagged id), which then leaves as a
    /// <see cref="StallResult"/> (or, for an open, once the stall's row committed too); false for the auction's own.
    /// </summary>
    public bool TryTakeListingAnswer(long characterId, int connection, ListingResult result)
    {
        if ((result.RequestId & Tag) == 0)
            return false;
        if (!_forwarded.Remove((characterId, connection, result.RequestId), out var forwarded))
        {
            // An owner's close cancelling a listing (no answer), or a stale answer: never sent as an auction result.
            if (_ownerCancels.Remove(result.RequestId, out var closing))
            {
                closing.OwnerCancelsPending--; // its return is in the mail now: the stall closes once none is left
                if (_byShopId.ContainsKey(closing.Row.ShopId) && IsEmpty(closing))
                    CloseStall(closing, ShopCloseReason.Owner);
            }
            return true;
        }
        _opsInFlight.Remove(forwarded.OpKey);
        if (forwarded.Opening is { } opening)
        {
            opening.ListingAnswer = result; // the stall's row commits in the same save: answered in Committed
            return true;
        }
        if (forwarded.Action == StallAction.Add && forwarded.Stall is { } stall && stall.PendingAdds > 0)
        {
            stall.PendingAdds--;
            if (stall.PendingAdds == 0 && _byShopId.ContainsKey(stall.Row.ShopId) && stall.ClosingByOwner)
                CancelOnSale(stall); // a listing added while the owner closed: cancelled as well
        }
        Answer?.Invoke(characterId, connection, FromListing(forwarded.RequestId, forwarded.Action, forwarded.Stall, result));
        return true;
    }

    /// <summary>
    /// Phase 8: stalls past their time close (their listings expire in the listing expiry job of phase 7), an owner's close cancels
    /// the listings it could not before, and the closes are written off the tick, one at a time, again after a failure.
    /// </summary>
    public void Tick(long nowMs)
    {
        _scratch.Clear();
        foreach (var stall in _byShopId.Values)
        {
            if (nowMs >= stall.Row.ClosesUtcMs || stall.ClosingByOwner)
                _scratch.Add(stall);
        }
        foreach (var stall in _scratch)
        {
            if (nowMs >= stall.Row.ClosesUtcMs)
                CloseStall(stall, ShopCloseReason.Time);
            else
                CancelOnSale(stall);
        }
        while (_closeDone.TryDequeue(out var done))
        {
            if (done.Ok)
                Freed(done.Write);
            else
                _closeWaiting.Add(done.Write with { RetryAtMs = nowMs + _settings.CloseRetryMs });
        }
        for (int i = _closeWaiting.Count - 1; i >= 0; i--)
        {
            if (_closeWaiting[i].RetryAtMs > nowMs)
                continue;
            _closeQueue.Enqueue(_closeWaiting[i]);
            _closeWaiting.RemoveAt(i);
        }
        PumpClose();
    }

    /// <summary>
    /// The character's session ended without its save (a fault or lost ownership): an open no save took is undone (the listing
    /// model undoes its listing); one in a save in flight leaves its place and owner blocked until a restart reads the database.
    /// </summary>
    public void Forget(long characterId)
    {
        if (_pendingOpens.Remove(characterId, out var pending))
        {
            foreach (var opening in pending)
            {
                _placeStates[opening.Place] = PlaceState.Free;
                _forwarded.Remove((opening.Ask.CharacterId, opening.Ask.Connection, opening.Tagged));
            }
            if (_owners.TryGetValue(characterId, out var owner) && owner.Phase == OwnerPhase.Opening)
                _owners.Remove(characterId);
        }
        if (_inFlightOpens.Remove(characterId, out var inFlight))
        {
            foreach (var opening in inFlight.Opens)
                _placeStates[opening.Place] = PlaceState.Unknown;
            _owners[characterId] = new OwnerState(OwnerPhase.Unknown, null);
        }
        // Adds and other requests the listing model will never answer now (their saves were lost or never taken): forgotten, so
        // a stall can still close when empty.
        foreach (var key in _forwarded.Keys.Where(k => k.CharacterId == characterId).ToList())
        {
            var lost = _forwarded[key];
            _forwarded.Remove(key);
            _opsInFlight.Remove(lost.OpKey);
            if (lost.Action == StallAction.Add && lost.Stall is { PendingAdds: > 0 } stall)
                stall.PendingAdds--;
        }
        foreach (var stall in _byShopId.Values)
            stall.Viewers.RemoveAll(v => v.CharacterId == characterId);
    }

    public ShopBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_pendingOpens.Remove(characterId, out var pending))
            return null;
        _inFlightOpens[characterId] = (saveId, pending);
        return new ShopBatch(pending.Select(o => o.Row).ToList());
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_inFlightOpens.TryGetValue(characterId, out var inFlight) || inFlight.SaveId != saveId)
            return; // a save without a stall, or a stale completion: idempotent
        _inFlightOpens.Remove(characterId);
        foreach (var opening in inFlight.Opens)
        {
            var stall = Admit(opening.Row, opening.Place);
            _events?.Publish(new StallOpened(opening.Row.ShopId, characterId, opening.Place, opening.Row.PlaceMarkerId, opening.Row.ClosesUtcMs),
                characterId, opening.Ask.RegionIndex);
            var listing = opening.ListingAnswer;
            Answer?.Invoke(opening.Ask.CharacterId, opening.Ask.Connection, new StallResult
            {
                RequestId = opening.Ask.RequestId,
                Action = StallAction.Open,
                StallEntityId = stall.Entity.Id,
                ListingId = listing?.ListingId ?? 0,
                Count = listing?.Count ?? 0,
                Fee = listing?.Fee ?? 0,
            });
        }
    }

    public void Handle(in EventEnvelope envelope, in ListingCreated payload) => Changed(payload.StallId, empty: false);

    public void Handle(in EventEnvelope envelope, in ListingSold payload) => Changed(payload.StallId, empty: payload.CountLeft == 0);

    public void Handle(in EventEnvelope envelope, in ListingCancelled payload) => Changed(payload.StallId, empty: true);

    public void Handle(in EventEnvelope envelope, in ListingExpired payload) => Changed(payload.StallId, empty: true);

    /// <summary>Shutdown: starts no further close write and returns the one in flight (a queued close is written again by the next run's load).</summary>
    public Task StopAsync()
    {
        _stopping = true;
        return _closeTask ?? Task.CompletedTask;
    }

    /// <summary>The cancels an owner's close started, by tagged request id: the close is answered once their returns committed.</summary>
    private readonly Dictionary<uint, Stall> _ownerCancels = new();

    /// <summary>No listing on sale, none being added, and none of the owner's close still returning by mail.</summary>
    private bool IsEmpty(Stall stall) =>
        stall.PendingAdds == 0 && stall.OwnerCancelsPending == 0 && _listings.ActiveForStall(stall.Row.ShopId).Count == 0;

    /// <summary>Stall entities closed lately, so a late request hears <c>StallClosed</c> rather than "not found".</summary>
    private const int RememberedClosed = 1024;

    private readonly HashSet<uint> _recentlyClosed = new();
    private readonly Queue<uint> _recentlyClosedOrder = new();

    /// <summary>A listing of the stall changed: its viewers get the new state; a listing gone may leave it empty (it closes then).</summary>
    private void Changed(string? stallId, bool empty)
    {
        if (stallId == null || !_byShopId.TryGetValue(stallId, out var stall))
            return;
        _dirty.Add(stall);
        if (empty && IsEmpty(stall))
            CloseStall(stall, stall.ClosingByOwner ? ShopCloseReason.Owner : ShopCloseReason.SoldOut);
    }

    private Stall Admit(ShopRow row, int place)
    {
        var entity = Spawn?.Invoke(row, _places[place])
                     ?? throw new InvalidOperationException("the world did not set StallService.Spawn");
        var stall = new Stall(row, entity);
        _byEntity[entity.Id] = stall;
        _byShopId[row.ShopId] = stall;
        _placeStates[place] = PlaceState.Open;
        _owners[row.OwnerId] = new OwnerState(OwnerPhase.Open, stall);
        return stall;
    }

    /// <summary>The stall closes now: its entity goes, its viewers get its last state, its close is written off the tick.</summary>
    private void CloseStall(Stall stall, ShopCloseReason reason)
    {
        if (!_byShopId.Remove(stall.Row.ShopId))
            return;
        _byEntity.Remove(stall.Entity.Id);
        if (_recentlyClosed.Add(stall.Entity.Id))
        {
            _recentlyClosedOrder.Enqueue(stall.Entity.Id);
            while (_recentlyClosedOrder.Count > RememberedClosed)
                _recentlyClosed.Remove(_recentlyClosedOrder.Dequeue());
        }
        _dirty.Remove(stall);
        int place = stall.Entity.PlaceOrdinal;
        _placeStates[place] = PlaceState.Closing;
        _owners[stall.Row.OwnerId] = new OwnerState(OwnerPhase.Closing, null);
        long now = _clock.UtcNowMs;
        Closed?.Invoke(stall.Entity, stall.Viewers);
        Despawn?.Invoke(stall.Entity);
        _events?.Publish(new StallClosed(stall.Row.ShopId, stall.Row.OwnerId, place, stall.Row.PlaceMarkerId, (short)reason), stall.Row.OwnerId);
        if (stall.CloseAsk is { } ask)
        {
            Answer?.Invoke(ask.CharacterId, ask.Connection, new StallResult
            {
                RequestId = ask.RequestId, Action = StallAction.Close, StallEntityId = stall.Entity.Id,
            });
        }
        QueueClose(new CloseWrite(stall.Row.ShopId, stall.Row.OwnerId, place, reason, now));
    }

    /// <summary>A stall closed: the world sends its viewers the last state (set by the world).</summary>
    public Action<ShopStall, IReadOnlyList<Viewer>>? Closed { get; set; }

    /// <summary>An owner's close: every listing of the stall on sale is cancelled (the answers are dropped).</summary>
    private void CancelOnSale(Stall stall)
    {
        foreach (var row in _listings.ActiveForStall(stall.Row.ShopId))
        {
            if (!_listings.IsActive(row.ListingId))
                continue; // a purchase is saving: tried again next tick
            uint tagged = NextTag();
            var ask = new ListingAsk(stall.Row.OwnerId, -1, tagged, stall.Entity.RegionIndex, false,
                $"stallClose:{stall.Row.ShopId}:{row.ListingId}");
            if (_listings.Cancel(ask, row.ListingId) == null)
            {
                _ownerCancels[tagged] = stall;
                stall.OwnerCancelsPending++;
            }
        }
    }

    private void QueueClose(CloseWrite write)
    {
        if (Store == null)
        {
            Freed(write); // no database (a test world): the close is done in memory
            return;
        }
        _closeQueue.Enqueue(write);
        PumpClose();
    }

    /// <summary>A close is in the database: its place and its owner are free again.</summary>
    private void Freed(CloseWrite write)
    {
        if (write.Place >= 0 && _placeStates[write.Place] == PlaceState.Closing)
            _placeStates[write.Place] = PlaceState.Free;
        if (_owners.TryGetValue(write.OwnerId, out var owner) && owner.Phase == OwnerPhase.Closing)
            _owners.Remove(write.OwnerId);
    }

    private void PumpClose()
    {
        if (_stopping || Store == null || _closeQueue.Count == 0 || _closeTask is { IsCompleted: false })
            return;
        var write = _closeQueue.Dequeue();
        var store = Store;
        _closeTask = Task.Run(async () =>
        {
            bool ok;
            try
            {
                await store.CloseAsync(write.ShopId, write.Reason, write.AtUtcMs, CancellationToken.None);
                ok = true; // closed now, or closed before (idempotent)
            }
            catch (Exception)
            {
                ok = false; // unknown: tried again (the update is guarded by state)
            }
            _closeDone.Enqueue((write, ok));
        });
    }

    private StallResult? CheckOwnerAsk(in ListingAsk ask, StallAction action)
    {
        if (!Loaded || !_listings.Loaded)
            return Refuse(ask, action, StallReason.InteractionUnavailable);
        if (ask.Blocked && action != StallAction.Browse)
            return Refuse(ask, action, StallReason.InCombat);
        return null;
    }

    /// <summary>The owner checks and the stall checks of one request; <paramref name="stall"/> is set when it passes.</summary>
    private StallResult? Gate(in ListingAsk ask, StallAction action, uint stallEntityId, int xCm, int yCm, bool ownerOnly, out Stall? stall)
    {
        stall = null;
        return CheckOwnerAsk(ask, action) ?? CheckStall(ask, action, stallEntityId, xCm, yCm, ownerOnly, out stall);
    }

    private StallResult? CheckStall(in ListingAsk ask, StallAction action, uint stallEntityId, int xCm, int yCm, bool ownerOnly,
        out Stall? stall, bool allowClosing = false)
    {
        if (!_byEntity.TryGetValue(stallEntityId, out stall))
            return Refuse(ask, action, _recentlyClosed.Contains(stallEntityId) ? StallReason.StallClosed : StallReason.StallNotFound);
        if (!InReach(_places[stall.Entity.PlaceOrdinal], xCm, yCm))
            return Refuse(ask, action, StallReason.OutOfRange);
        if (ownerOnly && stall.Row.OwnerId != ask.CharacterId)
            return Refuse(ask, action, StallReason.StallNotYours);
        if (stall.ClosingByOwner && !allowClosing)
            return Refuse(ask, action, StallReason.StallClosed);
        return null;
    }

    private bool InReach(StallPlace place, int xCm, int yCm)
    {
        long dx = place.XCm - (long)xCm, dy = place.YCm - (long)yCm;
        long range = _settings.InteractRangeCm;
        return dx * dx + dy * dy <= range * range;
    }

    private uint NextTag()
    {
        uint tag = Tag | (_nextTag & ~Tag);
        _nextTag = (_nextTag + 1) & ~Tag;
        if (_nextTag == 0)
            _nextTag = 1;
        return tag;
    }

    private static StallResult Refuse(in ListingAsk ask, StallAction action, StallReason reason, ListingReason listingReason = ListingReason.None,
        long listingId = 0) =>
        new() { RequestId = ask.RequestId, Action = action, Reason = reason, ListingReason = listingReason, ListingId = listingId };

    private static StallResult ListingRefusal(in ListingAsk ask, StallAction action, ListingResult answer) =>
        FromListing(ask.RequestId, action, null, answer);

    private static StallResult FromListing(uint requestId, StallAction action, Stall? stall, ListingResult answer) => new()
    {
        RequestId = requestId,
        Action = action,
        Reason = answer.Reason switch
        {
            ListingReason.None => StallReason.None,
            ListingReason.ListingCap => StallReason.StallFull, // the stall's own cap (StallSettings.listingsPerStall)
            _ => StallReason.StallListingRefused,
        },
        ListingReason = answer.Reason,
        StallEntityId = stall?.Entity.Id ?? 0,
        ListingId = answer.ListingId,
        Count = answer.Count,
        Price = answer.Price,
        Tax = answer.Tax,
        Fee = answer.Fee,
        ToMail = answer.ToMail,
    };

    /// <summary>Someone looking at a stall: a character on one connection.</summary>
    public readonly record struct Viewer(long CharacterId, int Connection);

    private enum PlaceState : byte
    {
        Free,
        Opening,
        Open,
        /// <summary>Closed in memory, its close being written: no new stall until it committed.</summary>
        Closing,
        /// <summary>An open in a save lost with its character: blocked until a restart reads the database.</summary>
        Unknown,
    }

    private enum OwnerPhase : byte
    {
        Opening,
        Open,
        Closing,
        Unknown,
    }

    private sealed record OwnerState(OwnerPhase Phase, Stall? Stall);

    private sealed class Stall(ShopRow row, ShopStall entity)
    {
        public ShopRow Row { get; } = row;
        public ShopStall Entity { get; } = entity;
        public List<Viewer> Viewers { get; } = new();
        public int PendingAdds { get; set; }
        public int OwnerCancelsPending { get; set; }
        public bool ClosingByOwner { get; set; }
        public ListingAsk? CloseAsk { get; set; }
    }

    private sealed class Opening(ShopRow row, int place, ListingAsk ask, uint tagged)
    {
        public ShopRow Row { get; } = row;
        public int Place { get; } = place;
        public ListingAsk Ask { get; } = ask;
        public uint Tagged { get; } = tagged;
        public ListingResult? ListingAnswer { get; set; }
    }

    private sealed record Forwarded(uint RequestId, StallAction Action, Stall? Stall, Opening? Opening, string OpKey = "");

    private sealed record CloseWrite(string ShopId, long OwnerId, int Place, ShopCloseReason Reason, long AtUtcMs, long RetryAtMs = 0);
}
