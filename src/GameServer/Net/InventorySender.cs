using GameServer.Entities;
using GameServer.Items;
using Shared.Protocol;
using Shared.Simulation.Items;

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

    /// <summary>The committed containers (rule 5): the Normal bag in the M1 fields, every other container in <c>Containers</c>.</summary>
    internal static InventoryContents Contents(InventoryService inventory, Player player)
    {
        var containers = new List<InventoryContainerEntry>();
        InventorySlotEntry[] normal = Array.Empty<InventorySlotEntry>();
        int normalSlots = 0;
        var rules = inventory.Rules.Containers;
        for (int index = 0; index < rules.Count; index++)
        {
            var committed = inventory.CommittedContainer(player.CharacterId, index);
            var slots = Slots(inventory, player.CharacterId, (byte)rules[index].Code, committed);
            if (rules[index].Code == inventory.Rules.NormalBag.Code)
            {
                normal = slots;
                normalSlots = committed.Count;
            }
            else
            {
                containers.Add(new InventoryContainerEntry { Code = (byte)rules[index].Code, SlotCount = (byte)committed.Count, Slots = slots });
            }
        }
        return new InventoryContents
        {
            Revision = ++player.InventoryRevision, SlotCount = (byte)normalSlots, Slots = normal, Containers = containers.ToArray(),
        };
    }

    /// <summary>
    /// The committed slots of one container, each with its reserved flag (the reservations now, by slot) and an instance's
    /// typed properties (protocol 27, WP-M3-2).
    /// </summary>
    private static InventorySlotEntry[] Slots(InventoryService inventory, long characterId, byte code, IReadOnlyList<ItemStack> committed)
    {
        var slots = new List<InventorySlotEntry>();
        for (int i = 0; i < committed.Count; i++)
        {
            var stack = committed[i];
            if (stack.IsEmpty)
                continue;
            var entry = new InventorySlotEntry
            {
                Slot = (byte)i,
                ItemNetId = stack.ItemNetId,
                Rarity = stack.Rarity,
                Stolen = stack.Stolen,
                Count = (ushort)stack.Count,
                InstanceId = stack.InstanceId,
                NonTradable = inventory.IsNonTradable(characterId, stack),
                Reserved = inventory.IsReserved(characterId, new InventoryLocation(code, i)),
            };
            if (stack.IsInstance && inventory.TryGetProperties(characterId, stack.InstanceId, out var properties))
                Fill(entry, properties, inventory.Rules);
            slots.Add(entry);
        }
        return slots.ToArray();
    }

    private static void Fill(InventorySlotEntry entry, ItemProperties properties, ItemRules rules)
    {
        entry.Enchant = (byte)Math.Clamp(properties.Enchant, 0, byte.MaxValue);
        entry.Quality = (ushort)Math.Clamp(properties.Quality, 0, ushort.MaxValue);
        entry.CrafterId = properties.CrafterId;
        entry.Tainted = properties.Tainted;
        var gems = new List<ushort>(properties.Gems.Count);
        foreach (string gem in properties.Gems)
        {
            if (rules.TryGetItem(gem, out var item))
                gems.Add((ushort)item.NetId);
        }
        entry.Gems = gems.ToArray();
        var stats = new List<CraftedStatEntry>(properties.CraftedStats.Count);
        foreach (var line in properties.CraftedStats)
        {
            int index = rules.StatIndex(line.StatId);
            if (index is >= 0 and <= byte.MaxValue)
                stats.Add(new CraftedStatEntry { Stat = (byte)index, Flat = line.Flat, Percent = line.Percent });
        }
        entry.CraftedStats = stats.ToArray();
    }
}
