using GameServer.Events;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>
/// Armor per tier (spec 4.1 R-4.1-05, 3.5 R-3.5-08, M4 decisions 9a and 9b; WP-M4-13): the character's tier the equip check
/// reads, and the pieces a tier change takes off. The tier has one source, <c>SkillTreeRules.EffectiveArmor</c> (the class row
/// and the learned armor passives); the world sets it at login and the skill tree's tier change sets it when the operation is
/// applied, so an equip sent while that save is in flight is checked against the new tier.
/// </summary>
public sealed partial class InventoryService
{
    /// <summary>The character's armor tier now (R-3.5-08): the next equip refuses armor of another tier (<c>ArmorTierMismatch</c>).</summary>
    public void SetArmorTier(long characterId, ArmorClass tier)
    {
        if (_characters.TryGetValue(characterId, out var inventory))
            inventory.ArmorTier = tier;
    }

    /// <summary>
    /// The tier the equip check uses: the one set by the world, or the class row's (a world without the skill tables); 0 when
    /// neither is known (no check).
    /// </summary>
    public ArmorClass ArmorTierOf(long characterId) =>
        _characters.TryGetValue(characterId, out var inventory) ? TierOf(inventory) : 0;

    private ArmorClass TierOf(CharacterInventory inventory) => inventory.ArmorTier ?? _rules.ClassArmor(inventory.ClassId);

    /// <summary>
    /// Fills <paramref name="slots"/> with the equip slot codes (in code order, rule 6) whose worn item is armor of another tier
    /// than <paramref name="tier"/> (decision 9b: what a tier change takes off). A piece filling several slots is listed once,
    /// at its lowest slot.
    /// </summary>
    public void WornArmorNotOf(long characterId, ArmorClass tier, List<int> slots)
    {
        slots.Clear();
        if (_equippedIndex < 0 || !_characters.TryGetValue(characterId, out var inventory))
            return;
        var bag = inventory.Items.BagAt(_equippedIndex);
        long lastInstance = 0;
        for (int slot = 0; slot < bag.SlotCount; slot++)
        {
            var stack = bag[slot];
            if (stack.IsEmpty || (stack.IsInstance && stack.InstanceId == lastInstance)
                || !_rules.TryGetItem(stack.ItemNetId, out var def) || ArmorTiers.CanWear(def, tier))
                continue;
            lastInstance = stack.InstanceId;
            slots.Add(slot);
        }
    }

    /// <summary>
    /// How many empty slots the container that receives <paramref name="category"/> has (R-4.1-02: equipment goes to the Safe bag
    /// equipment area); 0 without one.
    /// </summary>
    public int FreeSlotsFor(long characterId, ItemCategory category)
    {
        if (_rules.ReceiverOf(category) is not { } receiver || !_characters.TryGetValue(characterId, out var inventory)
            || !inventory.Items.TryIndex((byte)receiver.Code, out int index))
            return 0;
        var bag = inventory.Items.BagAt(index);
        int free = 0;
        for (int slot = 0; slot < bag.SlotCount; slot++)
        {
            if (bag[slot].IsEmpty)
                free++;
        }
        return free;
    }

    /// <summary>The first empty slot of the container that receives <paramref name="category"/>, or null when it is full.</summary>
    public InventoryLocation? FirstFreeSlotFor(long characterId, ItemCategory category)
    {
        if (_rules.ReceiverOf(category) is not { } receiver || !_characters.TryGetValue(characterId, out var inventory)
            || !inventory.Items.TryIndex((byte)receiver.Code, out int index))
            return null;
        int slot = inventory.Items.BagAt(index).FirstEmpty();
        return slot < 0 ? null : new InventoryLocation((byte)receiver.Code, slot);
    }

    /// <summary>
    /// Takes one worn instance out of the Equipped container to be mailed (decision 9b: the pieces that do not fit the bag go to
    /// the character's own mail in the same operation): one audited operation <paramref name="opKey"/> of kind
    /// <see cref="AuditKind.TransferOut"/> to no character (a move, published as <c>ItemTransferred</c>, never a sink), and
    /// <paramref name="attachment"/> the instance as it left (id, flags, properties). False, and nothing changed, for an empty,
    /// reserved or stackable slot; a repeated key is false with no attachment (its mail was queued by the first call).
    /// </summary>
    public bool TryTakeOutWorn(long characterId, int equipSlot, string opKey, InventoryReason reason, string source,
        out MailAttachment? attachment)
    {
        attachment = null;
        var inventory = Get(characterId);
        if (inventory.SeenOps.ContainsKey(opKey) || !TryWornUnit(inventory, equipSlot, out var unit))
            return false;
        var bag = inventory.Items.BagAt(_equippedIndex);
        _changes.Clear();
        if (!bag.TryRemove(equipSlot, 1, _changes))
            return false;
        Record(inventory, opKey, AuditKind.TransferOut, reason, source, 1, FromBag(_equippedIndex));
        inventory.TransferPeers[opKey] = 0;
        attachment = AttachmentOf(unit);
        return true;
    }

    /// <summary>
    /// What <see cref="TryTakeOutWorn"/> would mail for equip slot <paramref name="equipSlot"/>, changing nothing: the same
    /// conditions (a worn, unreserved instance), so a caller can check the whole mail before any piece leaves (decision 9b).
    /// </summary>
    public bool TryPeekWorn(long characterId, int equipSlot, out MailAttachment? attachment)
    {
        attachment = null;
        if (!_characters.TryGetValue(characterId, out var inventory) || !TryWornUnit(inventory, equipSlot, out var unit))
            return false;
        attachment = AttachmentOf(unit);
        return true;
    }

    private bool TryWornUnit(CharacterInventory inventory, int equipSlot, out MovedUnit unit)
    {
        unit = default;
        if (_equippedIndex < 0 || _rules.Equipped is not { } equipped)
            return false;
        var bag = inventory.Items.BagAt(_equippedIndex);
        if (equipSlot < 0 || equipSlot >= bag.SlotCount
            || Reserved(inventory, SlotKey(new InventoryLocation((byte)equipped.Code, equipSlot))) > 0)
            return false;
        var stack = bag[equipSlot];
        return stack.IsInstance && TryUnit(inventory, stack, 1, out unit);
    }

    private static MailAttachment AttachmentOf(in MovedUnit unit) =>
        new(unit.Def.Id, unit.Stack.Rarity, 1, unit.Stack.Stolen, unit.NonTradable, unit.Stack.InstanceId, unit.Properties?.ToJson());
}
