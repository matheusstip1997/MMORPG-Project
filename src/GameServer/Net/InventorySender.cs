using GameServer.Entities;
using GameServer.Items;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (backlog G): a player's pickup answers (<see cref="PickupResult"/>) and bag move refusals
/// (<see cref="InventoryMoveResult"/>, WP16), then the accepted moves whose save committed, then, when its committed bag
/// changed or the client is new, the whole Normal bag (<see cref="InventoryContents"/>) as committed to the database (rule
/// 5: the player hears about items only once they are saved). A confirmed move goes out just before the contents that
/// show it, so the client can drop its preview of the move when the new bag arrives. Reliable channel, MemoryPack.
/// Inventory messages are rare (a pickup, a move, a save), so encoding them allocates.
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
            Write(player, ReliableMessageId.PickupResult, result);
        player.PickupResults.Clear();
        foreach (var refusal in player.MoveRefusals)
            Write(player, ReliableMessageId.InventoryMoveResult, refusal);
        player.MoveRefusals.Clear();

        if (inventory == null || !inventory.IsAttached(player.CharacterId))
            return;
        var pending = player.PendingMoveConfirmations;
        int kept = 0;
        for (int i = 0; i < pending.Count; i++)
        {
            if (inventory.IsCommitted(player.CharacterId, pending[i].OpKey))
                Write(player, ReliableMessageId.InventoryMoveResult, pending[i].Result);
            else
                pending[kept++] = pending[i]; // still waiting for its save; order kept
        }
        pending.RemoveRange(kept, pending.Count - kept);

        if (!player.InventoryDirty)
            return;
        player.InventoryDirty = false;
        Write(player, ReliableMessageId.InventoryContents, Contents(inventory, player));
    }

    private void Write<T>(Player player, ReliableMessageId id, T message)
    {
        _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(id, message));
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
