using BotClient.Brain;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;
using Shared.Simulation.Skills;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's equip step (WP-M2-1; spec 4.1 M2 item model, R-3.5-07). It moves one piece of gear per round from a bag
/// container that takes equipment to the Equipped container (<c>InventoryMoveRequest</c>, the slot index is the
/// <c>EquipSlotDef</c> code), read from the kept <c>InventoryContents</c>.
/// <para>
/// The rule (the simplest sound one): a piece is usable when the own level reaches its <c>minLevel</c>, for a weapon its
/// weapon type is one of the class line's (<c>ClassGearDef.weaponTypes</c>), and for armor its tier is the bot's own
/// (R-4.1-05, WP-M4-13: <see cref="TierOf"/>, the class row and the learned armor passives). Its slot is the lowest
/// <c>EquipSlotDef</c> code it lists. It is equipped when that slot is empty or holds a lower rarity tier; an equal or higher
/// tier stays (no stat math, no swapping back and forth). Gear changes only out of combat and not during an action (the
/// server refuses with <c>InCombat</c>), so the step waits for both (the combat state as <see cref="BotStepContext.InCombat"/>
/// tells it) before it asks.
/// </para>
/// Counters: <c>equip.items</c>, <c>equip.refused.&lt;reason&gt;</c>; done at once with <c>equip.nothing</c>.
/// <para>
/// With the argument <c>crafted</c> (<c>equip:crafted</c>, the M3 exit run, WP-M3-12) it equips the crafted piece the
/// bot made (the newest instance with a crafter in <c>InventoryContents</c>, WP-M3-2 properties), whatever its rarity, and
/// fails without one (<c>equip.noCrafted</c>; WP-M3-6).
/// </para>
/// <para>
/// WP-M4-13 (the M4 exit run): <c>equip:&lt;item id&gt;</c> equips the first bag piece of that <c>ItemDef</c> and fails without
/// one (<c>equip.noItem</c>) or on a refusal; <c>equip:wrongTier</c> asks to wear the first bag armor piece of another tier
/// than the bot's own and is done only when the server refuses it with <c>ArmorTierMismatch</c> (<c>equip.wrongTierRefused</c>;
/// failed without such a piece, <c>equip.noWrongTier</c>, or when the server answers anything else).
/// </para>
/// </summary>
public sealed class EquipStep : IBotStep
{
    /// <summary>The argument of <c>equip:crafted</c>.</summary>
    public const string Crafted = "crafted";

    /// <summary>The argument of <c>equip:wrongTier</c> (WP-M4-13).</summary>
    public const string WrongTier = "wrongTier";

    private readonly string _argument;
    private uint _pending;

    public EquipStep(string argument = "")
    {
        if (argument.Length > 0 && argument != Crafted && argument != WrongTier && argument.IndexOf('.') < 0)
            throw new ArgumentException($"equip step: '{argument}' is not '{Crafted}', '{WrongTier}' or an item id");
        _argument = argument;
    }

    public string Id => "equip";

    public void Begin(BotStepContext context) => _pending = 0;

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_pending != 0)
            return Answer(context);
        byte fromContainer, fromSlot, toContainer, toSlot;
        bool chosen = _argument switch
        {
            WrongTier => ChooseWrongTier(context, out fromContainer, out fromSlot, out toContainer, out toSlot),
            Crafted => Choose(context, true, out fromContainer, out fromSlot, out toContainer, out toSlot),
            "" => Choose(context, false, out fromContainer, out fromSlot, out toContainer, out toSlot),
            _ => ChooseItem(context, _argument, out fromContainer, out fromSlot, out toContainer, out toSlot),
        };
        if (!chosen)
        {
            switch (_argument)
            {
                case Crafted:
                    context.Count("equip.noCrafted");
                    return context.Failed("no crafted piece to equip (craft one first)");
                case WrongTier:
                    context.Count("equip.noWrongTier");
                    return context.Failed("no armor piece of another tier in the bag");
                case "":
                    context.Count("equip.nothing");
                    return BotStepStatus.Done;
                default:
                    context.Count("equip.noItem");
                    return context.Failed($"no {_argument} in the bag to equip");
            }
        }
        if (context.InCombat || !context.View.ActionIdle || context.View.Dead)
            return BotStepStatus.Running; // gear waits for the combat state to end
        context.SendReliable(context.Inventory.MoveRequest(fromContainer, fromSlot, toContainer, toSlot, out _pending));
        return BotStepStatus.Running;
    }

    /// <summary>
    /// The bot's armor tier (R-3.5-08): <c>SkillTreeRules.EffectiveArmor</c> of its committed skill tree, the one the server's
    /// equip check and parry read; the class row's tier before the tree arrived; 0 when neither is known.
    /// </summary>
    internal static ArmorClass TierOf(BotStepContext context)
    {
        if (context.SkillTree is { Rules: { } rules, Skills: { } skills })
            return SkillTreeRules.EffectiveArmor(rules, skills);
        return context.Data.Rows<ClassDef>().FirstOrDefault(c => c.Id == context.ClassId)?.ArmorClass ?? 0;
    }

    /// <summary>Whether the bot may wear <paramref name="item"/> by its tier (anything that is not armor; armor of its own tier).</summary>
    internal static bool CanWear(BotStepContext context, ItemDef item)
    {
        var tier = TierOf(context);
        return tier == 0 || ArmorTiers.CanWear(item, tier);
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
        int? level = context.Progression.State?.Level ?? context.Own.Level;
        if (level == null)
            return false;
        var weaponTypes = context.Data.Rows<ClassGearDef>().FirstOrDefault(g => g.Id == context.ClassId)?.WeaponTypes ?? [];
        var worn = Worn(context);
        long newest = 0;
        bool found = false;
        foreach (var (container, piece, item, code) in Pieces(context))
        {
            if (item.MinLevel > level || (item.WeaponType != WeaponType.None && Array.IndexOf(weaponTypes, item.WeaponType) < 0)
                || !CanWear(context, item))
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
            (fromContainer, fromSlot, toContainer, toSlot) = (container, piece.Slot, EquippedCode(context), (byte)code);
            if (!crafted)
                return true;
            found = true;
        }
        return found;
    }

    /// <summary>The first bag piece of <paramref name="itemId"/> (<c>equip:&lt;item id&gt;</c>), whatever the slot holds.</summary>
    internal static bool ChooseItem(BotStepContext context, string itemId, out byte fromContainer, out byte fromSlot,
        out byte toContainer, out byte toSlot)
    {
        fromContainer = fromSlot = toContainer = toSlot = 0;
        foreach (var (container, piece, item, code) in Pieces(context))
        {
            if (item.Id != itemId)
                continue;
            (fromContainer, fromSlot, toContainer, toSlot) = (container, piece.Slot, EquippedCode(context), (byte)code);
            return true;
        }
        return false;
    }

    /// <summary>The first bag armor piece of another tier than the bot's (<c>equip:wrongTier</c>): what the server must refuse.</summary>
    internal static bool ChooseWrongTier(BotStepContext context, out byte fromContainer, out byte fromSlot, out byte toContainer,
        out byte toSlot)
    {
        fromContainer = fromSlot = toContainer = toSlot = 0;
        if (TierOf(context) == 0)
            return false;
        foreach (var (container, piece, item, code) in Pieces(context))
        {
            if (!ArmorTiers.IsArmor(item) || CanWear(context, item))
                continue;
            (fromContainer, fromSlot, toContainer, toSlot) = (container, piece.Slot, EquippedCode(context), (byte)code);
            return true;
        }
        return false;
    }

    /// <summary>Every equipment piece in the bag containers that take equipment, in container and slot order, with its lowest slot code.</summary>
    private static IEnumerable<(byte Container, InventorySlotEntry Piece, ItemDef Item, int Code)> Pieces(BotStepContext context)
    {
        var contents = context.Inventory.Contents;
        if (contents == null || EquippedCode(context) == byte.MaxValue)
            yield break;
        var containers = context.Data.Rows<ContainerDef>().ToList();
        var slotCodes = context.Data.Rows<EquipSlotDef>().ToDictionary(s => s.Id, s => s.Code, StringComparer.Ordinal);
        foreach (var container in contents.Containers.OrderBy(c => c.Code))
        {
            var def = containers.FirstOrDefault(c => c.Code == container.Code);
            if (def == null || def.Kind != ContainerKind.Bag || Array.IndexOf(def.Accepts, ItemCategory.Equipment) < 0)
                continue;
            foreach (var piece in container.Slots.OrderBy(s => s.Slot))
            {
                var item = context.Item(piece.ItemNetId);
                if (item == null || item.Category != ItemCategory.Equipment || item.EquipSlots.Length == 0)
                    continue;
                int code = item.EquipSlots.Where(slotCodes.ContainsKey).Select(s => slotCodes[s]).DefaultIfEmpty(-1).Min();
                if (code >= 0)
                    yield return ((byte)container.Code, piece, item, code);
            }
        }
    }

    private static byte EquippedCode(BotStepContext context) =>
        context.Data.Rows<ContainerDef>().FirstOrDefault(c => c.Kind == ContainerKind.Equipped) is { } equipped
            ? (byte)equipped.Code
            : byte.MaxValue;

    private static IReadOnlyList<InventorySlotEntry> Worn(BotStepContext context)
    {
        byte code = EquippedCode(context);
        return context.Inventory.Contents?.Containers.FirstOrDefault(c => c.Code == code)?.Slots ?? [];
    }

    private BotStepStatus Answer(BotStepContext context)
    {
        if (context.Inventory.LastMoveResult is not { } result || result.RequestId != _pending)
            return BotStepStatus.Running;
        if (_argument == WrongTier)
        {
            if (result.Reason == InventoryMoveReason.ArmorTierMismatch)
            {
                context.Count("equip.wrongTierRefused");
                return BotStepStatus.Done;
            }
            context.Count("equip.wrongTierNotRefused." + result.Reason);
            return context.Failed($"armor of another tier was answered {result.Reason}, not {InventoryMoveReason.ArmorTierMismatch}");
        }
        if (result.Reason != InventoryMoveReason.None)
        {
            context.Count("equip.refused." + result.Reason);
            return BotStepStatus.Failed;
        }
        context.Count("equip.items");
        return BotStepStatus.Done;
    }
}
