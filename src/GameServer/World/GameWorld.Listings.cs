using GameServer.Combat;
using GameServer.Entities;
using GameServer.Market;
using GameServer.Net;
using GameServer.Outposts;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.World;

/// <summary>
/// Listings and the auction house in the world (spec 4.5, M4 decision 6; WP-M4-7): <see cref="ListingService"/> holds the active
/// listings. Phase 1 reads the listing requests, each through the auctioneer gate (the outpost pattern: the NPC must be in reach,
/// so the auction is not a remote market) and the <c>ListingSettings</c> rate limits (spec 7.6); a search goes to the search
/// worker (SQL off the tick) and comes back in a later phase 1; phase 7 runs the expiry job; phase 10 sends the answers.
/// Without <see cref="EnableListings"/> (worlds built for tests, a config without the tables, fees or mail) there is no auction:
/// every request is refused <c>InteractionUnavailable</c>.
/// </summary>
public sealed partial class GameWorld
{
    private ListingService? _listings;
    private IListingSearchJobs? _listingSearch;
    private ListingSender? _listingSender;
    private readonly HashSet<long> _listingSearches = new();

    /// <summary>The listings of the auction (WP-M4-7); null until <see cref="EnableListings"/>.</summary>
    public ListingService? Listings => _listings;

    /// <summary>The world clock the listings run on (UTC ms; the dev console moves expiries to it).</summary>
    internal long ListingClockMs => _clock.UtcNowMs;

    /// <summary>
    /// Turns the auction on (after <see cref="EnableMail"/>, before the <c>SessionManager</c> is built, which wires its saves):
    /// needs the items, escrow, fees and mail. <paramref name="search"/> runs searches off the tick (null: searches are refused
    /// as failed). The caller loads the active listings (<see cref="ListingService.Load"/>) before the world starts.
    /// </summary>
    public void EnableListings(ListingSettings settings, IListingSearchJobs? search)
    {
        if (_inventory == null || _escrow == null || Fees == null || _mail == null || Economy == null || Outposts == null)
            return;
        _listingSearch = search;
        _listingSender = new ListingSender(_transport);
        var jobs = _jobs?.Rules;
        _listings = new ListingService(settings, _inventory, _escrow, Fees, _mail, _clock, _events,
            jobId => jobs?.Find(jobId) is { } job ? (short)job.NetId : (short)0, Economy.Settings.MaxGold)
        {
            Answer = OnListingAnswer,
        };
        _escrow.MailReleaseSettled = _listings.OnMailReleaseSettled;
        Outposts.Register(NpcInteraction.Auction, new AuctionInteraction(this));
    }

    /// <summary>Phase 1: a listing request of <paramref name="player"/>; false when the request is not one.</summary>
    private bool HandleListingRequest(Player player, in ClientRequest request)
    {
        if (request.Kind is not (ReliableMessageId.ListingSearchRequest or ReliableMessageId.ListingCreateRequest
            or ReliableMessageId.ListingBuyRequest or ReliableMessageId.ListingCancelRequest or ReliableMessageId.MyListingsRequest))
            return false;
        long now = _clock.UtcNowMs;
        if (_listings == null || Outposts == null)
        {
            RefuseListing(player, request, ListingReason.InteractionUnavailable);
            return true;
        }
        var gate = Outposts.CheckReach(player, request.DropEntityId, NpcInteraction.Auction, out _);
        if (gate != OutpostReason.None)
        {
            RefuseListing(player, request, gate switch
            {
                OutpostReason.NpcNotFound => ListingReason.NpcNotFound,
                OutpostReason.OutOfRange => ListingReason.OutOfRange,
                _ => ListingReason.InteractionUnavailable,
            });
            return true;
        }
        var settings = _listings.Settings;
        bool search = request.Kind == ReliableMessageId.ListingSearchRequest;
        bool allowed = search
            ? player.ListingSearchBucket.TryTake(now, settings.SearchBurst, settings.SearchesPerPeriod, settings.SearchPeriodMs)
            : player.ListingWriteBucket.TryTake(now, settings.WriteBurst, settings.WritesPerPeriod, settings.WritePeriodMs);
        if (!allowed)
        {
            RefuseListing(player, request, ListingReason.ListingRateLimited);
            return true;
        }
        switch (request.Body)
        {
            case ListingSearchRequest query:
                SearchListings(player, query, now);
                break;
            case MyListingsRequest mine:
                player.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.MyListings, MyListingsOf(player, mine.RequestId)));
                break;
            case ListingCreateRequest create:
                AnswerListing(player, _listings.Create(AskOf(player, create.RequestId, "listing", now),
                    new InventoryLocation(create.Container, create.Slot), create.Count, create.UnitPrice, create.DurationIndex));
                break;
            case ListingBuyRequest buy:
                AnswerListing(player, _listings.Buy(AskOf(player, buy.RequestId, "listingBuy", now), buy.ListingId, buy.Count, buy.UnitPrice));
                break;
            case ListingCancelRequest cancel:
                AnswerListing(player, _listings.Cancel(AskOf(player, cancel.RequestId, "listingCancel", now), cancel.ListingId));
                break;
        }
        return true;
    }

    /// <summary>The ask of a request: who, which connection, the region (fees) and the operation key (one per run, entity, connection, request).</summary>
    private ListingAsk AskOf(Player player, uint requestId, string op, long now)
    {
        // A listing never interrupts combat or an action under way (M4 plan risk "souls-like feel").
        bool blocked = _inventory == null || CombatState.IsInCombat(player.LastCombatAtMs, now, _inventory.Rules.CombatStateMs)
                       || player.Actions.IsBusy;
        return new ListingAsk(player.CharacterId, player.Connection, requestId, player.RegionIndex, blocked,
            $"{op}:{_runId}:{player.Id}:{player.Connection}:{requestId}");
    }

    private void AnswerListing(Player player, ListingResult? answer)
    {
        if (answer != null)
            player.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.ListingResult, answer));
    }

    /// <summary>An operation committed (or a release settled): the answer goes to the connection that asked, if it is still bound.</summary>
    private void OnListingAnswer(long characterId, int connection, ListingResult answer)
    {
        if (_playersByCharacter.TryGetValue(characterId, out var player) && player.Connection == connection)
            player.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.ListingResult, answer));
    }

    private static void RefuseListing(Player player, in ClientRequest request, ListingReason reason)
    {
        switch (request.Kind)
        {
            case ReliableMessageId.ListingSearchRequest:
                player.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.ListingSearchResponse,
                    new ListingSearchResponse { RequestId = request.RequestId, Reason = reason }));
                break;
            case ReliableMessageId.MyListingsRequest:
                player.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.MyListings,
                    new MyListings { RequestId = request.RequestId, Reason = reason }));
                break;
            default:
                player.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.ListingResult, new ListingResult
                {
                    RequestId = request.RequestId, Reason = reason,
                    Action = request.Kind switch
                    {
                        ReliableMessageId.ListingCreateRequest => ListingAction.Create,
                        ReliableMessageId.ListingBuyRequest => ListingAction.Buy,
                        _ => ListingAction.Cancel,
                    },
                }));
                break;
        }
    }

    /// <summary>A search: one in flight per character (the worker's queue stays bounded); the page comes back in a later phase 1.</summary>
    private void SearchListings(Player player, ListingSearchRequest request, long now)
    {
        var settings = _listings!.Settings;
        if (_listingSearch == null || request.ItemNetIds.Length > settings.SearchItemIdsMax)
        {
            player.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.ListingSearchResponse, new ListingSearchResponse
            {
                RequestId = request.RequestId,
                Reason = _listingSearch == null ? ListingReason.ListingSearchFailed : ListingReason.ListingChanged,
            }));
            return;
        }
        if (!_listingSearches.Add(player.CharacterId))
        {
            player.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.ListingSearchResponse,
                new ListingSearchResponse { RequestId = request.RequestId, Reason = ListingReason.ListingRateLimited }));
            return;
        }
        var query = new ListingQuery(request.ItemNetIds.Select(id => (int)id).ToArray(),
            request.Category == 0 ? null : (short)(request.Category - 1), request.RarityMin, request.RarityMax, request.LevelMin,
            request.LevelMax, (short)Math.Min(request.JobNetId, (ushort)short.MaxValue), request.ArmorTier, request.AfterUnitPrice, request.AfterListingId,
            settings.PageSize + 1, now); // one row more says whether a next page exists
        _listingSearch.Request(new ListingSearchAsk(player.CharacterId, player.Connection, request.RequestId, query));
    }

    /// <summary>Phase 1 (after the commands): the search pages the worker read, to the connections that asked.</summary>
    private void DrainListingSearches()
    {
        if (_listingSearch == null || _listings == null || _inventory == null)
            return;
        while (_listingSearch.TryTakeAnswer(out var answer))
        {
            var ask = answer.Ask;
            _listingSearches.Remove(ask.CharacterId);
            if (!_playersByCharacter.TryGetValue(ask.CharacterId, out var player) || player.Connection != ask.Connection)
                continue;
            var response = new ListingSearchResponse { RequestId = ask.RequestId };
            if (answer.Rows == null)
                response.Reason = ListingReason.ListingSearchFailed;
            else
            {
                int pageSize = _listings.Settings.PageSize;
                response.HasMore = answer.Rows.Count > pageSize;
                response.Rows = answer.Rows.Take(pageSize).Select(r => ListingViews.Entry(_inventory.Rules, r))
                    .Where(e => e != null).Select(e => e!).ToArray();
            }
            player.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.ListingSearchResponse, response));
        }
    }

    private MyListings MyListingsOf(Player player, uint requestId)
    {
        var rows = _listings!.Mine(player.CharacterId, ListingKind.Auction);
        return new MyListings
        {
            RequestId = requestId,
            Cap = (ushort)_listings.Settings.MaxActivePerCharacter,
            Rows = rows.Select(r => ListingViews.Entry(_inventory!.Rules, r, _escrow!.Find(r.EscrowId)))
                .Where(e => e != null).Select(e => e!).Take(ReliableMessages.MaxMyListings).ToArray(),
        };
    }

    /// <summary>Phase 7: the expiry job (listings past their duration go back to their sellers by mail).</summary>
    private void ExpireListings() => _listings?.ExpireDue(_clock.UtcNowMs);

    /// <summary>The auctioneer (WP-M4-7): its yes opens the auction window, with the player's own listings pushed along.</summary>
    private sealed class AuctionInteraction(GameWorld world) : IOutpostInteractionHandler
    {
        public OutpostReason? Interact(Player player, Npc npc, uint requestId)
        {
            if (world._listings == null)
                return OutpostReason.InteractionUnavailable;
            player.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.MyListings, world.MyListingsOf(player, 0)));
            return OutpostReason.None;
        }
    }
}
