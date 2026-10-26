using GameServer.Entities;
using GameServer.Items;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (backlog G): a player's pickup answers (<see cref="PickupResult"/>), then, when its committed bag changed or
/// the client is new, the whole Normal bag (<see cref="InventoryContents"/>) as committed to the database (rule 5: the
/// player hears about items only once they are saved). Reliable channel, MemoryPack. Inventory messages are rare (a
/// pickup, a save), so encoding them allocates.
/// </summary>
internal sealed class InventorySender
{
    private readonly ITransport _transport;

    public InventorySender(ITransport transport)
    {
        _transport = transport;
    }

    public long Sent { get; private set; }

    public void Send(Player player, InventoryService? inventory)
    {
        foreach (var result in player.PickupResults)
        {
            _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.PickupResult, result));
            Sent++;
        }
        player.PickupResults.Clear();

        if (!player.InventoryDirty || inventory == null || !inventory.IsAttached(player.CharacterId))
            return;
        player.InventoryDirty = false;
        _transport.Send(player.PeerId, Channel.ReliableOrdered,
            ReliableMessages.Write(ReliableMessageId.InventoryContents, Contents(inventory, player)));
        Sent++;
    }

    internal static InventoryContents Contents(InventoryService inventory, Player player)
    {
        var committed = inventory.Committed(player.CharacterId);
        var slots = new List<InventorySlotEntry>();
        for (int i = 0; i < committed.Count; i++)
        {
            var stack = committed[i];
            if (stack.IsEmpty)
                continue;
            slots.Add(new InventorySlotEntry
            {
                Slot = (byte)i,
                ItemNetId = stack.ItemNetId,
                Rarity = stack.Rarity,
                Stolen = stack.Stolen,
                Count = (ushort)stack.Count,
            });
        }
        return new InventoryContents { Revision = ++player.InventoryRevision, SlotCount = (byte)committed.Count, Slots = slots.ToArray() };
    }
}
