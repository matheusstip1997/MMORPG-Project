using GameServer.Events;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>
/// Moves between containers, equip, unequip, the starter weapon and what the equipped gear gives (WP-M2-1; spec 4.1 M2 item
/// model, R-4.1-02, R-3.5-07). The container and slot rules are the shared <see cref="Inventory"/>; the character's own
/// rules are here: a weapon's type must be in the class line's list (<see cref="ClassGearDef"/>) and the character must
/// have the item's <c>minLevel</c>. Out of combat and not during an action is the caller's check (it knows the entity,
/// <see cref="InventoryMoveHandler"/>). Each is an audited idempotent operation, save class 2 like the bag moves (R-7.1-01).
/// </summary>
public sealed partial class InventoryService : IEquipmentStatSource
{
    private static readonly ItemStatBonus[] NoBonuses = Array.Empty<ItemStatBonus>();
    private static readonly WeaponType[] NoWeapons = Array.Empty<WeaponType>();

    /// <summary>The character's level changed (WP-M2-3 level-ups): the next equip checks <c>ItemDef.minLevel</c> against it.</summary>
    public void SetLevel(long characterId, int level)
    {
        if (_characters.TryGetValue(characterId, out var inventory))
            inventory.Level = level;
    }

    /// <summary>
    /// The weapon types the character may equip now (R-3.5-07, WP-M2-5): its class line and learned weapon unlocks, set by
    /// the world at login, on a class advance and when an unlock is learned or cleared. An equipped weapon stays equipped
    /// when it leaves the set (TODO(open): whether a respec unequips it).
    /// </summary>
    public void SetUsableWeapons(long characterId, uint mask)
    {
        if (_characters.TryGetValue(characterId, out var inventory))
            inventory.UsableWeapons = mask;
    }

    /// <summary>A move that puts something into or takes something out of the Equipped container (it changes gear).</summary>
    public bool IsGearChange(InventoryLocation from, InventoryLocation to) =>
        _rules.Equipped is { } equipped && (from.Container == equipped.Code || to.Container == equipped.Code);

    /// <summary>
    /// <c>Move(container)</c>, equip and unequip in one (spec 4.1): moves a stack from one container slot to another. Into
    /// the Equipped container it equips (the item sits at its lowest slot and the gear it shares a slot with goes back to the
    /// source container), out of it it unequips; both refuse a weapon the class cannot use (<c>WeaponNotUsable</c>) and an
    /// item above the character's level (<c>LevelTooLow</c>). A refusal changes and records nothing.
    /// </summary>
    public InventoryOpResult TryMove(long characterId, InventoryLocation from, InventoryLocation to, string opKey, InventoryReason reason)
    {
        var inventory = Get(characterId);
        if (inventory.SeenOps.TryGetValue(opKey, out var previous))
            return new InventoryOpResult((BagMoveResult)previous, InventoryMoveReason.None, true);
        var items = inventory.Items;
        var incoming = items.IncomingEquip(from, to);
        if (!incoming.IsEmpty && _rules.TryGetItem(incoming.ItemNetId, out var gear) && gear.Category == ItemCategory.Equipment)
        {
            if (gear.MinLevel > inventory.Level)
                return Refused(InventoryMoveReason.LevelTooLow);
            if (gear.WeaponType != WeaponType.None && !CanUse(inventory, gear.WeaponType))
                return Refused(InventoryMoveReason.WeaponNotUsable);
        }
        _inventoryChanges.Clear();
        var move = items.TryMove(from, to, _lookup, _inventoryChanges);
        if (!move.Applied)
            return Refused(ReasonOf(move.Check));
        byte? equipped = _rules.Equipped is { } container ? (byte)container.Code : null;
        var kind = to.Container == equipped ? AuditKind.Equip : from.Container == equipped ? AuditKind.Unequip : AuditKind.Move;
        Record(inventory, opKey, kind, reason, $"{from}->{to}", (int)move.Result, _inventoryChanges);
        return new InventoryOpResult(move.Result, InventoryMoveReason.None, false);
    }

    private bool CanUse(CharacterInventory inventory, WeaponType type) =>
        inventory.UsableWeapons is { } mask ? (mask & WeaponMasks.Bit(type)) != 0 : _rules.ClassGear(inventory.ClassId)?.CanUse(type) == true;

    /// <summary>Equips the item at <paramref name="from"/> into equipment slot <paramref name="equipSlot"/> (<c>EquipSlotDef.code</c>).</summary>
    public InventoryOpResult TryEquip(long characterId, InventoryLocation from, int equipSlot, string opKey) =>
        _rules.Equipped is { } equipped
            ? TryMove(characterId, from, new InventoryLocation((byte)equipped.Code, equipSlot), opKey, InventoryReason.PlayerMove)
            : Refused(InventoryMoveReason.InventoryUnavailable);

    /// <summary>Unequips the item in equipment slot <paramref name="equipSlot"/> onto <paramref name="to"/>.</summary>
    public InventoryOpResult TryUnequip(long characterId, int equipSlot, InventoryLocation to, string opKey) =>
        _rules.Equipped is { } equipped
            ? TryMove(characterId, new InventoryLocation((byte)equipped.Code, equipSlot), to, opKey, InventoryReason.PlayerMove)
            : Refused(InventoryMoveReason.InventoryUnavailable);

    /// <summary>
    /// The class's common starter weapon (<c>ClassGearDef.starterWeaponId</c>), equipped (spec 4.1 M2 item model: "one common
    /// root weapon equipped at character creation"). The Auth API creates characters and never writes items (rule 4), so the
    /// session grants it at the character's first world entry, under the key <see cref="InventoryOpKeys.Starter"/>: applied
    /// once ever (the login reads whether the key exists). Into the receiving container instead when its slots are taken.
    /// False when nothing was granted (already granted, no starter, no room).
    /// </summary>
    public bool GrantStarterGear(long characterId)
    {
        var inventory = Get(characterId);
        string opKey = InventoryOpKeys.Starter(characterId);
        if (inventory.SeenOps.ContainsKey(opKey) || _rules.Equipped is not { } equipped
            || _rules.ClassGear(inventory.ClassId) is not { StarterWeaponId.Length: > 0 } gear
            || !_rules.TryGetItem(gear.StarterWeaponId, out var item))
            return false;
        var items = inventory.Items;
        ushort mask = _rules.EquipSlotMask(item.NetId);
        int primary = -1;
        bool free = true;
        for (int slot = 0; slot < 16; slot++)
        {
            if ((mask & (1 << slot)) == 0)
                continue;
            if (primary < 0)
                primary = slot;
            free &= items.CoveredBy(slot, _lookup) < 0;
        }
        InventoryLocation at;
        if (primary >= 0 && free)
            at = new InventoryLocation((byte)equipped.Code, primary);
        else if (ReceiverIndex(item.Category) is var index and >= 0 && items.BagAt(index).FirstEmpty() is var empty and >= 0)
            at = new InventoryLocation(items.Rule(index).Code, empty);
        else
            return false;
        if (NewInstance(inventory, item, 0) is not { } stack)
            return false;
        _inventoryChanges.Clear();
        items.TryPlace(at, stack, _inventoryChanges);
        Record(inventory, opKey, AuditKind.Add, InventoryReason.StarterGear, $"class:{inventory.ClassId}", 1, _inventoryChanges);
        return true;
    }

    public IReadOnlyList<ItemStatBonus> GetEquippedBonuses(long characterId) =>
        _characters.TryGetValue(characterId, out var inventory) ? inventory.Bonuses : NoBonuses;

    public IReadOnlyList<WeaponType> GetEquippedWeaponTypes(long characterId) =>
        _characters.TryGetValue(characterId, out var inventory) ? inventory.WeaponTypes : NoWeapons;

    /// <summary>The bonus and weapon caches from the committed equipped set (slot order, rule 6).</summary>
    private void RebuildGear(CharacterInventory inventory)
    {
        inventory.Bonuses.Clear();
        inventory.WeaponTypes.Clear();
        if (_equippedIndex < 0)
            return;
        foreach (var stack in inventory.Committed[_equippedIndex])
        {
            if (stack.IsEmpty || !_rules.TryGetItem(stack.ItemNetId, out var item))
                continue;
            int percent = _rules.StatPercent(stack.Rarity);
            foreach (var bonus in _rules.BaseBonuses(item.NetId))
                inventory.Bonuses.Add(bonus.Scale(percent));
            if (item.WeaponType != WeaponType.None && (_rules.EquipSlotMask(item.NetId) & _rules.WeaponSlotMask) != 0
                && !inventory.WeaponTypes.Contains(item.WeaponType))
                inventory.WeaponTypes.Add(item.WeaponType);
        }
    }

    private static InventoryOpResult Refused(InventoryMoveReason reason) => new(BagMoveResult.Refused, reason, false);

    /// <summary>The reason code of a shared-rules refusal (spec 9 reason codes).</summary>
    public static InventoryMoveReason ReasonOf(InventoryCheck check) => check switch
    {
        InventoryCheck.SlotEmpty => InventoryMoveReason.ItemSlotEmpty,
        InventoryCheck.StackFull => InventoryMoveReason.ItemStackFull,
        InventoryCheck.NotAccepted => InventoryMoveReason.ItemNotAccepted,
        InventoryCheck.NoRoom => InventoryMoveReason.BagFull,
        _ => InventoryMoveReason.ItemSlotInvalid,
    };
}
