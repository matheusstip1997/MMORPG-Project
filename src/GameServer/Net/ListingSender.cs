using GameServer.Entities;
using GameServer.Items;
using GameServer.Persistence;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (spec 4.5, 9.13; WP-M4-7): a player's listing answers in the order they were written (search pages, own listings,
/// create, buy and cancel results: an applied one only once committed, before the <c>InventoryContents</c> that shows it).
/// Reliable channel, MemoryPack.
/// </summary>
internal sealed class ListingSender
{
    private readonly ITransport _transport;

    public ListingSender(ITransport transport)
    {
        _transport = transport;
    }

    public void Send(Player player)
    {
        foreach (var message in player.ListingMessages)
            _transport.Send(player.PeerId, Channel.ReliableOrdered, message);
        player.ListingMessages.Clear();
    }
}

/// <summary>The wire views of listings (WP-M4-7): one row per listing, the held unit as a slot entry for the tooltip; never the seller.</summary>
internal static class ListingViews
{
    /// <summary>A row from a search result (the held unit read with the listing).</summary>
    public static ListingEntry? Entry(ItemRules items, ListingSearchRow row) =>
        Entry(items, row.ListingId, row.UnitPrice, row.ExpiresUtcMs, row.DefId, row.Rarity, row.Stolen, row.CountLeft, row.InstanceId,
            row.NonTradable, row.PropertiesJson);

    /// <summary>A row from a listing in memory and the unit its escrow holds; null when the escrow is unknown.</summary>
    public static ListingEntry? Entry(ItemRules items, ListingRow row, EscrowRecord? escrow)
    {
        if (escrow is not { Items.Count: > 0 })
            return null;
        var unit = escrow.Items[0];
        return Entry(items, row.ListingId, row.UnitPrice, row.ExpiresUtcMs, unit.DefId, (short)unit.Rarity, unit.Stolen, row.CountLeft,
            unit.InstanceId, unit.NonTradable, unit.PropertiesJson);
    }

    private static ListingEntry? Entry(ItemRules items, long listingId, long unitPrice, long expiresUtcMs, string defId, short rarity,
        bool stolen, int count, long instanceId, bool nonTradable, string? propertiesJson)
    {
        if (!items.TryGetItem(defId, out var def) || count <= 0)
            return null;
        var slot = new InventorySlotEntry
        {
            ItemNetId = (ushort)def.NetId,
            Rarity = (byte)Math.Clamp((int)rarity, 0, byte.MaxValue),
            Stolen = stolen,
            Count = (ushort)Math.Min(ushort.MaxValue, count),
            InstanceId = instanceId,
            NonTradable = nonTradable,
        };
        if (propertiesJson != null && ItemProperties.TryParse(propertiesJson, out var properties, out _) && items.Represents(properties))
            InventorySender.Fill(slot, properties, items);
        return new ListingEntry { ListingId = listingId, UnitPrice = unitPrice, ExpiresUtcMs = expiresUtcMs, Item = slot };
    }
}
