using BotClient.Brain;
using Shared.Data.Tables;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's buy step (WP-M2-4; spec 4.2, R-4.2-01: vendors sell at fixed prices and the gold is destroyed). It tops the
/// behaviour's <c>loopBuyItemId</c> (a potion) in the Normal bag up to <c>loopBuyKeep</c> units, as far as the committed gold
/// (<c>GoldBalance</c>, kept by the client) pays: it walks to the nearest vendor whose <c>NpcVendorStock</c> has the item
/// and sends one <c>VendorBuyRequest</c> for the fixed price times the count.
/// Counters: <c>buy.units</c>, <c>buy.gold</c>, <c>buy.refused.&lt;reason&gt;</c>; done at once with <c>buy.stocked</c>
/// (enough held), <c>buy.noGold</c> (the gold does not pay one unit, no walk), <c>buy.noNpc</c>, or <c>buy.off</c> (no item set).
/// <para>
/// With an argument (<c>buy:m3.pickaxeT1</c>, WP-M3-12) it makes sure the bot holds at least one of that item in any
/// container (a tool lands in the Safe bag equipment area): done when held (<c>buy.stocked</c>) or bought; <b>failed</b>
/// when the gold does not pay (<c>buy.noGold</c>) or no vendor is reached (<c>buy.noNpc</c>), since the entry asks for it;
/// not yet while the item or a vendor stocking it is not in the tables.
/// </para>
/// <para>
/// From players (spec 4.5): <c>buy:listing:&lt;item&gt;[x&lt;n&gt;]</c> searches the auction house for the item (the pair's
/// listing) and buys it (WP-M4-7, <see cref="BuyListing"/>); <c>buy:stall</c> walks to the partner's stall and buys from it
/// (WP-M4-8; "not yet" naming the WP until it lands).
/// </para>
/// </summary>
public sealed class BuyStep : IBotStep
{
    /// <summary>The argument prefix of a purchase from an auction listing (WP-M4-7).</summary>
    public const string ListingPrefix = "listing:";

    /// <summary>The argument of a purchase from the partner's stall (WP-M4-8).</summary>
    public const string StallArgument = "stall";

    private readonly string _argument;
    private uint _pending;
    private uint _searchPending;
    private uint _listingPending;
    private int _searches;
    private long _nextSearchAtMs;
    private uint _npc;
    private uint _stall;
    private uint _browsePending;
    private uint _stallBuyPending;
    private string _offlineSeen = ""; // the partner's offline post a purchase already used (the loop repeats; kept across rounds)
    private string _offlineUsing = "";

    public BuyStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "buy";

    public void Begin(BotStepContext context)
    {
        _pending = 0;
        _searchPending = 0;
        _listingPending = 0;
        _searches = 0;
        _nextSearchAtMs = 0;
        _npc = 0;
        _stall = 0;
        _browsePending = 0;
        _stallBuyPending = 0;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_pending != 0)
            return Answer(context);
        // Buying from players (spec 4.5): the auction (WP-M4-7) and the partner's stall (WP-M4-8).
        if (_argument.StartsWith(ListingPrefix, StringComparison.Ordinal))
            return BuyListing(context, ref command);
        if (_argument == StallArgument || _argument == StallArgument + ":offline")
            return BuyStall(context, ref command);

        bool required = _argument.Length > 0;
        string itemId = required ? _argument : context.Behaviour.LoopBuyItemId;
        var item = itemId.Length == 0 ? null : context.Data.Rows<ItemDef>().FirstOrDefault(i => i.Id == itemId);
        if (item == null)
        {
            if (required)
                return context.NotAvailable(Id, $"item '{itemId}' not in ItemDef yet");
            context.Count("buy.off");
            return BotStepStatus.Done;
        }
        int held = 0;
        var contents = context.Inventory.Contents;
        IEnumerable<InventorySlotEntry> slots = contents?.Slots ?? [];
        if (required && contents != null)
            slots = slots.Concat(contents.Containers.SelectMany(c => c.Slots));
        foreach (var slot in slots)
        {
            if (slot.ItemNetId == item.NetId)
                held += slot.Count;
        }
        int missing = (required ? 1 : context.Behaviour.LoopBuyKeep) - held;
        if (missing <= 0)
        {
            context.Count("buy.stocked");
            return BotStepStatus.Done;
        }

        var stock = context.Data.Rows<NpcVendorStock>().Where(s => s.ItemId == itemId).ToList();
        if (required && stock.Count == 0)
            return context.NotAvailable(Id, $"no NpcVendorStock row sells '{itemId}' yet");
        BotStepStatus NotDone(string why) => required ? context.Failed($"{why} ({itemId})") : BotStepStatus.Done;
        long gold = context.Outposts.Gold ?? 0;
        if (stock.Count > 0 && gold < stock.Min(s => s.PriceGold))
        {
            context.Count("buy.noGold");
            return NotDone($"buy.noGold: {gold} gold, {stock.Min(s => s.PriceGold)} needed");
        }
        switch (context.ApproachNpc(npc => npc.Offers(NpcInteraction.Buy) && stock.Any(s => s.NpcId == npc.Id), ref command,
                    out uint npcEntityId, out var vendor))
        {
            case NpcApproach.None:
                context.Count("buy.noNpc");
                return NotDone("buy.noNpc: no vendor of it reached");
            case NpcApproach.Walking:
                return BotStepStatus.Running;
        }
        int price = stock.First(s => s.NpcId == vendor!.Id).PriceGold;
        int count = (int)Math.Min(Math.Min(missing, gold / price), item.MaxStack);
        if (count <= 0)
        {
            context.Count("buy.noGold");
            return NotDone($"buy.noGold: {gold} gold, {price} needed");
        }
        context.SendReliable(context.Outposts.BuyRequest(npcEntityId, (ushort)item.NetId, (ushort)count, (long)price * count, out _pending));
        return BotStepStatus.Running;
    }

    /// <summary>Searches before giving up (one a second): the partner's listing may still be saving.</summary>
    private const int ListingSearchTries = 10;

    private const int ListingSearchGapMs = 1_000;

    /// <summary>
    /// <c>buy:listing:&lt;item&gt;[x&lt;n&gt;]</c> (WP-M4-7): at the auctioneer, search the item by its net id (the name filter), then buy
    /// <c>n</c> units (default: all of it) of the cheapest listing the bot did not list itself. Counters <c>buy.listing.units</c>,
    /// <c>buy.listing.gold</c>, <c>buy.listing.tax</c>, <c>buy.listing.toMail</c>, <c>buy.listing.refused.&lt;reason&gt;</c>; failed
    /// with <c>buy.listing.none</c> when ten searches a second apart find none.
    /// </summary>
    private BotStepStatus BuyListing(BotStepContext context, ref BotCommand command)
    {
        if (context.Listings is not { } listings)
            return context.NotAvailable(Id, "no auction client in this context");
        if (!ListStep.TryItem(_argument[ListingPrefix.Length..], out string itemId, out int wanted))
            return context.Failed($"bad argument '{_argument}': buy:listing:<item>[x<n>]");
        var item = context.Data.Rows<ItemDef>().FirstOrDefault(i => i.Id == itemId);
        if (item == null)
            return context.NotAvailable(Id, $"item '{itemId}' not in ItemDef yet");
        if (_listingPending != 0)
        {
            if (listings.ResultFor(_listingPending) is not { } result)
                return BotStepStatus.Running;
            if (result.Reason != ListingReason.None)
            {
                context.Count("buy.listing.refused." + result.Reason);
                return context.Failed("buy.listing.refused." + result.Reason);
            }
            context.Count("buy.listing.units", result.Count);
            context.Count("buy.listing.gold", result.Price);
            context.Count("buy.listing.tax", result.Tax);
            if (result.ToMail)
                context.Count("buy.listing.toMail");
            return BotStepStatus.Done;
        }
        if (_searchPending == 0)
        {
            switch (context.ApproachNpc(n => n.Offers(NpcInteraction.Auction), ref command, out _npc, out _))
            {
                case NpcApproach.None:
                    context.Count("buy.noNpc");
                    return context.Failed("buy.noNpc: no auctioneer reached");
                case NpcApproach.Walking:
                    return BotStepStatus.Running;
            }
            if (context.NowMs < _nextSearchAtMs)
                return BotStepStatus.Running;
            if (_searches >= ListingSearchTries)
            {
                context.Count("buy.listing.none");
                return context.Failed($"buy.listing.none: no listing of {itemId} after {_searches} searches");
            }
            _searches++;
            context.SendReliable(listings.SearchRequest(_npc, new ListingSearchRequest { ItemNetIds = [(ushort)item.NetId] }, out _searchPending));
            return BotStepStatus.Running;
        }
        if (listings.PageFor(_searchPending) is not { } page)
            return BotStepStatus.Running;
        _searchPending = 0;
        var own = listings.Mine?.Rows.Select(r => r.ListingId).ToHashSet() ?? [];
        var row = page.Reason == ListingReason.None ? page.Rows.FirstOrDefault(r => !own.Contains(r.ListingId)) : null;
        if (row == null)
        {
            _nextSearchAtMs = context.NowMs + ListingSearchGapMs;
            return BotStepStatus.Running;
        }
        int count = wanted > 0 ? Math.Min(wanted, row.Item.Count) : row.Item.Count;
        context.SendReliable(listings.BuyRequest(_npc, row.ListingId, count, row.UnitPrice, out _listingPending));
        return BotStepStatus.Running;
    }

    /// <summary>
    /// <c>buy:stall[:offline]</c> (WP-M4-8): waits for the partner's <c>stall</c> post (its place; with <c>:offline</c> also for its
    /// <c>offline</c> post, so the purchase is made while the owner is offline, M4 decision 7), walks to the place, looks at the stall
    /// and buys all units of its first listing. Counters <c>buy.stall.units</c>, <c>buy.stall.gold</c>, <c>buy.stall.tax</c>,
    /// <c>buy.stall.toMail</c>, <c>buy.stall.refused.&lt;reason&gt;</c>; failed with <c>buy.stall.none</c> (no stall or nothing on it).
    /// </summary>
    private BotStepStatus BuyStall(BotStepContext context, ref BotCommand command)
    {
        if (context.Stalls is not { } stalls || context.Pair is not { } pair)
            return context.NotAvailable(Id, "buy:stall needs a stall client and a pair partner");
        if (_stallBuyPending != 0)
        {
            if (stalls.ResultFor(_stallBuyPending) is not { } result)
                return BotStepStatus.Running;
            _offlineSeen = _offlineUsing; // this offline post is used, whatever the answer
            if (result.Reason != StallReason.None)
            {
                string why = result.Reason == StallReason.StallListingRefused ? result.ListingReason.ToString() : result.Reason.ToString();
                context.Count("buy.stall.refused." + why);
                return context.Failed("buy.stall.refused." + why);
            }
            context.Count("buy.stall.units", result.Count);
            context.Count("buy.stall.gold", result.Price);
            context.Count("buy.stall.tax", result.Tax);
            if (result.ToMail)
                context.Count("buy.stall.toMail");
            return BotStepStatus.Done;
        }
        if (_argument.EndsWith(":offline", StringComparison.Ordinal))
        {
            if (!pair.TryRead(OfflineStep.OfflineKey, out string offline) || offline == _offlineSeen)
                return BotStepStatus.Running; // the owner has not gone offline (again) yet
            _offlineUsing = offline;
        }
        if (!pair.TryRead(StallStep.PlaceKey, out string placeText)
            || !int.TryParse(placeText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int place)
            || place < 0 || place >= stalls.Places.Count)
            return BotStepStatus.Running; // the partner has not opened its stall yet
        var at = stalls.Places[place];
        if (context.ApproachPoint(Shared.Simulation.FixVec2.FromCm(at.XCm, at.YCm), stalls.InteractRangeCm, ref command) != NpcApproach.InReach)
            return BotStepStatus.Running;
        if (_stall == 0)
        {
            _stall = Game.Gameplay.Entities.ClientStalls.StallOn(context.Entities, place);
            if (_stall == 0)
            {
                _offlineSeen = _offlineUsing;
                context.Count("buy.stall.none");
                return context.Failed($"buy.stall.none: no stall on place {place}");
            }
            context.SendReliable(stalls.BrowseRequest(_stall, out _browsePending));
            return BotStepStatus.Running;
        }
        if (stalls.ResultFor(_browsePending) is { Reason: not StallReason.None } refused)
        {
            context.Count("buy.stall.refused." + refused.Reason);
            return context.Failed("buy.stall.refused." + refused.Reason);
        }
        if (stalls.StateOf(_stall) is not { } state)
            return BotStepStatus.Running;
        var row = state.Open ? state.Rows.FirstOrDefault() : null;
        if (row == null)
        {
            _offlineSeen = _offlineUsing;
            context.Count("buy.stall.none");
            return context.Failed($"buy.stall.none: nothing on the stall on place {place}");
        }
        context.SendReliable(stalls.BuyRequest(_stall, row.ListingId, row.Item.Count, row.UnitPrice, out _stallBuyPending));
        return BotStepStatus.Running;
    }

    private BotStepStatus Answer(BotStepContext context)
    {
        if (context.Outposts.LastPurchase is not { } purchase || purchase.RequestId != _pending)
            return BotStepStatus.Running;
        if (purchase.Reason != OutpostReason.None)
        {
            context.Count("buy.refused." + purchase.Reason);
            return context.Failed("buy.refused." + purchase.Reason);
        }
        context.Count("buy.units", purchase.Count);
        context.Count("buy.gold", purchase.GoldSpent);
        return BotStepStatus.Done;
    }
}
