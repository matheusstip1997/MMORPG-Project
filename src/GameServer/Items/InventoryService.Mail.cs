using GameServer.Events;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>
/// What a mail claim needs of the bag (spec 4.5 M4 mail, WP-M4-3; <c>Items/Mail/MailService</c> is the only caller): the room
/// for a stolen or clean stack, and an instance that keeps its identity when it changes hands (spec 4.1 M2 item model: "an id
/// keeps its creator when the item changes hands later"). Items and gold still change only here (rule 4).
/// </summary>
public sealed partial class InventoryService
{
    /// <summary>
    /// Units of <paramref name="item"/> that fit now in the container that receives its category, as a stolen or a clean stack
    /// (a stolen stack never merges with a clean one, R-4.4-01).
    /// </summary>
    public int SpaceFor(long characterId, ItemDef item, byte rarity, bool stolen)
    {
        int index = ReceiverIndex(item.Category);
        return index < 0
            ? 0
            : Get(characterId).Items.BagAt(index).SpaceFor(new ItemStack((ushort)item.NetId, rarity, stolen, 1), item.MaxStack);
    }

    /// <summary>
    /// Adds one instance of <paramref name="item"/> into the container that receives its category, under <paramref name="opKey"/>.
    /// With <paramref name="instanceId"/> above 0 the instance keeps that id, its stolen and non-tradable flags and its properties
    /// (its original owner and history included): an item that left another bag (a mail attachment). With 0 a new instance is
    /// minted for the character (its next id, properties as given or the defaults, owned by the character). Nothing is added
    /// (<see cref="AddResult.Added"/> 0, nothing recorded) when no slot is free, the ids ran out, or the character already holds
    /// that id (never two copies of one instance).
    /// </summary>
    public AddResult TryAddInstance(long characterId, ItemDef item, byte rarity, long instanceId, bool stolen, bool nonTradable,
        ItemProperties? properties, string opKey, InventoryReason reason, string source)
    {
        if (!ItemCategories.IsInstance(item.Category))
            throw new ArgumentException($"{item.Id} is not an instance category", nameof(item));
        if (instanceId < 0)
            throw new ArgumentOutOfRangeException(nameof(instanceId));
        if (properties != null && !_rules.Represents(properties))
            throw new ArgumentException($"{item.Id}: properties the tables cannot represent", nameof(properties));
        var inventory = Get(characterId);
        if (inventory.SeenOps.TryGetValue(opKey, out var previous))
            return new AddResult(previous, true);
        int index = ReceiverIndex(item.Category);
        if (index < 0)
            return new AddResult(0, false);
        var bag = inventory.Items.BagAt(index);
        int slot = bag.FirstEmpty();
        if (slot < 0)
            return new AddResult(0, false);
        long id;
        if (instanceId > 0)
        {
            if (inventory.Instances.ContainsKey(instanceId))
                return new AddResult(0, false);
            id = instanceId;
            var own = properties?.Clone() ?? ItemInstanceIds.NewPropertiesOf(characterId);
            inventory.Instances.Add(id, new InstanceInfo(item.Id, rarity, stolen, nonTradable, own));
        }
        else
        {
            if (inventory.InstanceSeq >= ItemInstanceIds.MaxSeq || inventory.CharacterId > ItemInstanceIds.MaxCharacterId)
                return new AddResult(0, false);
            id = ItemInstanceIds.Make(inventory.CharacterId, ++inventory.InstanceSeq);
            inventory.SeqChanged = true;
            var own = properties?.Clone() ?? ItemInstanceIds.NewPropertiesOf(characterId);
            own.OriginalOwner = characterId;
            inventory.Instances.Add(id, new InstanceInfo(item.Id, rarity, stolen, nonTradable, own));
        }
        _changes.Clear();
        bag.Add(new ItemStack((ushort)item.NetId, rarity, stolen, 1, id), 1, _changes);
        Record(inventory, opKey, AuditKind.Add, reason, source, 1, FromBag(index));
        return new AddResult(1, false, id);
    }
}
