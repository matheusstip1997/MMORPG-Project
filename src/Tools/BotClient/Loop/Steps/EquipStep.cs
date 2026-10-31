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
/// <para>
/// With the argument <c>crafted</c> (<c>equip:crafted</c>, the M3 exit run, WP-M3-12) it equips the crafted piece the
/// bot made (the newest instance with a crafter in <c>InventoryContents</c>, WP-M3-2 properties), whatever its rarity, and
/// fails without one (<c>equip.noCrafted</c>; WP-M3-6).
/// </para>
/// </summary>
public sealed class EquipStep : IBotStep
{
    /// <summary>The argument of <c>equip:crafted</c>.</summary>
    public const string Crafted = "crafted";

    private readonly string _argument;
    private uint _pending;

    public EquipStep(string argument = "")
    {
        if (argument.Length > 0 && argument != Crafted)
            throw new ArgumentException($"equip step: '{argument}' is not '{Crafted}'");
        _argument = argument;
    }

    public string Id => "equip";

    public void Begin(BotStepContext context) => _pending = 0;

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_pending != 0)
            return Answer(context);
        bool crafted = _argument == Crafted;
        if (!Choose(context, crafted, out byte fromContainer, out byte fromSlot, out byte toContainer, out byte toSlot))
        {
            context.Count(crafted ? "equip.noCrafted" : "equip.nothing");
            return crafted ? context.Failed("no crafted piece to equip (craft one first)") : BotStepStatus.Done;
        }
        if (context.InCombat || !context.View.ActionIdle || context.View.Dead)
            return BotStepStatus.Running; // gear waits for the combat state to end
        context.SendReliable(context.Inventory.MoveRequest(fromContainer, fromSlot, toContainer, toSlot, out _pending));
        return BotStepStatus.Running;
    }

    /// <summary>The first piece in container and slot order that the rule above equips; false when there is none.</summary>
    internal static bool Choose(BotStepContext context, out byte fromContainer, out byte fromSlot, out byte toContainer, out byte toSlot) =>
        Choose(context, false, out fromContainer, out fromSlot, out toContainer, out toSlot);

    /// <summary>
    /// With <paramref name="crafted"/>, the newest crafted piece (an instance with a crafter, the highest instance id) the rule
    /// lets the bot wear, whatever its rarity and whatever the slot holds (<c>equip:crafted</c>, WP-M3-6).
    /// </summary>
    internal static bool Choose(BotStepContext context, bool crafted, out byte fromContainer, out byte fromSlot, out byte toContainer,
        out byte toSlot)
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
        long newest = 0;
        bool found = false;

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
                if (crafted)
                {
                    if (piece.CrafterId == 0 || piece.InstanceId <= newest)
                        continue;
                    newest = piece.InstanceId;
                }
                else
                {
                    var current = worn.FirstOrDefault(s => s.Slot == code);
                    if (current != null && current.Rarity >= piece.Rarity)
                        continue;
                }
                fromContainer = (byte)container.Code;
                fromSlot = piece.Slot;
                toContainer = (byte)equipped.Code;
                toSlot = (byte)code;
                if (!crafted)
                    return true;
                found = true;
            }
        }
        return found;
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
