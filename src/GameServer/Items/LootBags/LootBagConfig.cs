using GameServer.Entities;
using GameServer.Persistence;
using Shared.Data;
using Shared.Data.Tables;
using Shared.World;

namespace GameServer.Items.LootBags;

/// <summary>
/// The death loot bag rules in runtime form (spec 4.3 R-4.3-03, M4 decision 8; WP-M4-9): the owner window per area type
/// (<c>OwnerWindows</c>), the expiry, cap and job (<c>BagExpiry</c>), the reach (<c>LootSettings.pickupRangeCm</c>, the
/// ground drop's) and the base price of each item for the events' value (<c>PriceBase</c>, 0 without a row).
/// </summary>
public sealed class LootBagConfig
{
    private readonly IReadOnlyDictionary<string, int> _windowMsByArea;
    private readonly IReadOnlyDictionary<string, int> _baseGoldByItem;

    public LootBagConfig(IReadOnlyDictionary<string, int> windowMsByArea, BagExpiry expiry, int pickupRangeCm,
        IReadOnlyDictionary<string, int>? baseGoldByItem = null)
    {
        _windowMsByArea = windowMsByArea;
        Expiry = expiry;
        PickupRangeCm = pickupRangeCm;
        _baseGoldByItem = baseGoldByItem ?? new Dictionary<string, int>(StringComparer.Ordinal);
    }

    public BagExpiry Expiry { get; }

    public int PickupRangeCm { get; }

    /// <summary>The owner window of an area type that drops a bag; false for one without a row (it drops nothing).</summary>
    public bool TryGetWindowMs(string areaType, out int windowMs) => _windowMsByArea.TryGetValue(areaType, out windowMs);

    /// <summary>The base outpost price of one unit (<c>PriceBase.baseGold</c>), 0 for an item without one.</summary>
    public int BaseGold(string itemId) => _baseGoldByItem.TryGetValue(itemId, out int gold) ? gold : 0;

    /// <summary>The rules from the loaded tables; null when <c>OwnerWindows</c> or <c>BagExpiry</c> is missing (no loot bags).</summary>
    public static LootBagConfig? From(ConfigSet set, LootSettings loot)
    {
        if (!set.TryGet(typeof(BagExpiry), out var expiry) || expiry.Rows.Count != 1 || !set.TryGet(typeof(OwnerWindows), out var windows))
            return null;
        var byArea = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (OwnerWindows row in windows.Rows)
            byArea[row.Id] = row.OwnerWindowMs;
        var prices = new Dictionary<string, int>(StringComparer.Ordinal);
        if (set.TryGet(typeof(PriceBase), out var priceTable))
        {
            foreach (PriceBase row in priceTable.Rows)
                prices[row.ItemId] = row.BaseGold;
        }
        return new LootBagConfig(byArea, (BagExpiry)expiry.Rows[0], loot.PickupRangeCm, prices);
    }
}

/// <summary>Where the death loot service reads and changes the world (the world's adapter; tests use a double).</summary>
public interface ILootBagWorld
{
    Entity? FindEntity(uint id);

    /// <summary>The player of an online character, or null.</summary>
    Player? PlayerOf(long characterId);

    /// <summary>The region a player stands in (its id, area type and flags), or null outside every region.</summary>
    (string RegionId, string AreaType, RegionFlags Flags)? RegionOf(Entity entity);

    /// <summary>Spawns a bag entity at a stored spot (entity list and grid, like a ground drop).</summary>
    LootBag AddBag(string bagId, int xCm, int yCm, int heightCm);

    void RemoveBag(LootBag bag);

    bool CanSee(Player observer, Entity target);

    /// <summary>The two characters are in one party now (<c>IPartyQuery.SameParty</c>, spec 6.5).</summary>
    bool SameParty(long a, long b);
}

/// <summary>
/// The mail side of a take (spec 4.5 M4 mail; <c>MailService.Send</c>, carried by the taker's save): the owner's overflow and
/// a party member's collection for the owner. Null when the world has no mail (then nothing is mailed).
/// </summary>
public interface ILootBagMail
{
    /// <summary>Item rows one mail carries at most (<c>MailSettings.attachmentsPerMail</c>).</summary>
    int AttachmentsPerMail { get; }

    /// <summary>The draft would be queued on <paramref name="carrier"/>'s save (rules and the carrier's mailbox); nothing changes.</summary>
    bool CanSend(long carrier, MailDraft draft);

    /// <summary>Queues the draft on the carrier's save (true), or refuses it (false; nothing changed).</summary>
    bool Send(long carrier, MailDraft draft);
}
