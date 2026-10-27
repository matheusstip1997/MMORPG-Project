using BotClient.Brain;
using Shared.Data.Tables;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's equip step (WP-M2-1; spec 4.1 M2 item model, R-3.5-07). It moves one piece of gear per round from a bag
/// container that takes equipment to the Equipped container (<c>InventoryMoveRequest</c>, the slot index is the
/// <c>EquipSlotDef</c> code), read from the kept <c>InventoryContents</c>.
/// <para>
/// The rule (the simplest sound one): a piece is usable when the own level reaches its <c>minLevel</c> and, for a weapon,
/// its weapon type is one of the class line's (<c>ClassGearDef.weaponTypes</c>; armor is not gated). Its slot is the
/// lowest <c>EquipSlotDef</c> code it lists. It is equipped when that slot is empty or holds a lower rarity tier; an equal
/// or higher tier stays (no stat math, no swapping back and forth). Gear changes only out of combat and not during an
/// action (the server refuses with <c>InCombat</c>), so the step waits for both (the combat state as
/// <see cref="BotStepContext.InCombat"/> tells it) before it asks.
/// </para>
/// Counters: <c>equip.items</c>, <c>equip.refused.&lt;reason&gt;</c>; done at once with <c>equip.nothing</c>.
/// </summary>
public sealed class EquipStep : IBotStep
{
    private uint _pending;

    public string Id => "equip";

    public void Begin(BotStepContext context) => _pending = 0;

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_pending != 0)
            return Answer(context);
        if (!Choose(context, out byte fromContainer, out byte fromSlot, out byte toContainer, out byte toSlot))
        {
            context.Count("equip.nothing");
            return BotStepStatus.Done;
        }
        if (context.InCombat || !context.View.ActionIdle || context.View.Dead)
            return BotStepStatus.Running; // gear waits for the combat state to end
        context.SendReliable(context.Inventory.MoveRequest(fromContainer, fromSlot, toContainer, toSlot, out _pending));
        return BotStepStatus.Running;
    }

    /// <summary>The first piece in container and slot order that the rule above equips; false when there is none.</summary>
    internal static bool Choose(BotStepContext context, out byte fromContainer, out byte fromSlot, out byte toContainer, out byte toSlot)
    {
        fromContainer = fromSlot = toContainer = toSlot = 0;
        var contents = context.Inventory.Contents;
        int? level = context.Progression.State?.Level ?? context.Own.Level;
        if (contents == null || level == null)
            return false;
        var containers = context.Data.Rows<ContainerDef>().ToList();
        var equipped = containers.FirstOrDefault(c => c.Kind == ContainerKind.Equipped);
        if (equipped == null)
            return false;
        var slotCodes = context.Data.Rows<EquipSlotDef>().ToDictionary(s => s.Id, s => s.Code, StringComparer.Ordinal);
        var weaponTypes = context.Data.Rows<ClassGearDef>().FirstOrDefault(g => g.Id == context.ClassId)?.WeaponTypes ?? [];
        var worn = contents.Containers.FirstOrDefault(c => c.Code == equipped.Code)?.Slots ?? [];

        foreach (var container in contents.Containers.OrderBy(c => c.Code))
        {
            var def = containers.FirstOrDefault(c => c.Code == container.Code);
            if (def == null || def.Kind != ContainerKind.Bag || Array.IndexOf(def.Accepts, ItemCategory.Equipment) < 0)
                continue;
            foreach (var piece in container.Slots.OrderBy(s => s.Slot))
            {
                var item = context.Item(piece.ItemNetId);
                if (item == null || item.Category != ItemCategory.Equipment || item.EquipSlots.Length == 0 || item.MinLevel > level
                    || (item.WeaponType != WeaponType.None && Array.IndexOf(weaponTypes, item.WeaponType) < 0))
                    continue;
                int code = item.EquipSlots.Where(slotCodes.ContainsKey).Select(s => slotCodes[s]).DefaultIfEmpty(-1).Min();
                if (code < 0)
                    continue;
                var current = worn.FirstOrDefault(s => s.Slot == code);
                if (current != null && current.Rarity >= piece.Rarity)
                    continue;
                fromContainer = (byte)container.Code;
                fromSlot = piece.Slot;
                toContainer = (byte)equipped.Code;
                toSlot = (byte)code;
                return true;
            }
        }
        return false;
    }

    private BotStepStatus Answer(BotStepContext context)
    {
        if (context.Inventory.LastMoveResult is not { } result || result.RequestId != _pending)
            return BotStepStatus.Running;
        if (result.Reason != InventoryMoveReason.None)
        {
            context.Count("equip.refused." + result.Reason);
            return BotStepStatus.Failed;
        }
        context.Count("equip.items");
        return BotStepStatus.Done;
    }
}
