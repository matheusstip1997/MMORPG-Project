using GameServer.Events;
using GameServer.Items.Mail;
using GameServer.Persistence;
using GameServer.Skills;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>
/// The worn armor when a skill tree operation changes the character's tier (spec 3.5 R-3.5-08, 4.1 R-4.1-05, M4 decision 9b;
/// WP-M4-13): the character wears the new tier only. Every worn piece of another tier comes off, in equip slot order (rule 6),
/// into the first free slot of the equipment receiver (the Safe bag equipment area) as an unequip, or, when that is full, out
/// to the character's own mail (<see cref="MailKind.ArmorReturn"/>, one mail: the armor slots fit one, a validator rule). All
/// of it rides the skill operation's one save (rule 5): the operation, the moves and the mail commit together or not at all,
/// and the answer comes after the commit. Writes only through <see cref="InventoryService"/> and <see cref="MailService"/>
/// (rule 4). Keys derive from the skill operation's key, which the skill tree answers only once, so the mail key is never sent
/// twice. The inventory's tier is set before the pieces come off, so the equip check already reads the new tier.
/// </summary>
public sealed class ArmorTierSweep : IArmorTierChange
{
    /// <summary>The <c>source</c> of the audit rows and the mail.</summary>
    public const string Source = "armorTier";

    private readonly InventoryService _inventory;
    private readonly Func<MailService?> _mail;
    private readonly List<int> _slots = new();

    public ArmorTierSweep(InventoryService inventory, Func<MailService?> mail)
    {
        _inventory = inventory;
        _mail = mail;
    }

    /// <summary>The key of the unequip of equip slot <paramref name="slot"/>.</summary>
    public static string UnequipKey(string opKey, int slot) => $"{opKey}:armor:{slot}";

    /// <summary>The key of a piece taken out to the mail.</summary>
    public static string MailOutKey(string opKey, int slot) => $"{opKey}:armorOut:{slot}";

    /// <summary>The send key of the one mail.</summary>
    public static string MailKey(string opKey) => $"{opKey}:armorMail";


    public SkillTreeReason Check(long characterId, ArmorClass tier) =>
        _inventory.IsAttached(characterId) && _inventory.Rules.Equipped != null ? Plan(characterId, tier, "check") : SkillTreeReason.None;

    public void Apply(long characterId, ArmorClass tier, string opKey)
    {
        if (!_inventory.IsAttached(characterId) || _inventory.Rules.Equipped is not { } equipped)
            return;
        _inventory.SetArmorTier(characterId, tier);
        // The same plan as Check, again on the state now (nothing ran in between on the world thread): when the mail of the
        // pieces that do not fit could not be sent, none of them leaves (they stay worn) and nothing throws (review of #237).
        if (Plan(characterId, tier, opKey) != SkillTreeReason.None)
            return;
        foreach (int slot in _toBag)
        {
            var from = new InventoryLocation((byte)equipped.Code, slot);
            if (_inventory.FirstFreeSlotFor(characterId, ItemCategory.Equipment) is { } to)
                _inventory.TryMove(characterId, from, to, UnequipKey(opKey, slot), InventoryReason.ArmorTierChange);
        }
        if (_draft == null || _mail() is not { } mail)
            return;
        // Every piece of the checked draft leaves through InventoryService, then the mail that carries exactly those pieces is
        // queued on the same save: the draft passed the mail rules and the carrier is attached, so Send only queues (or repeats).
        foreach (int slot in _toMail)
            _inventory.TryTakeOutWorn(characterId, slot, MailOutKey(opKey, slot), InventoryReason.ArmorTierChange, Source, out _);
        mail.Send(characterId, _draft);
    }

    private readonly List<int> _toBag = new();
    private readonly List<int> _toMail = new();
    private MailDraft? _draft;

    /// <summary>
    /// Which worn pieces of another tier go to the bag (the first free slots of the equipment receiver, in equip slot order)
    /// and which to the one mail, with that mail's draft built from the pieces as they would leave and checked against the mail
    /// rules. Changes nothing. <see cref="SkillTreeReason.ArmorReserved"/> when a craft holds a piece; <see cref="SkillTreeReason.Unavailable"/>
    /// when the overflow cannot be mailed (no mailbox held for the character, a world without mail, or a draft the rules refuse).
    /// </summary>
    private SkillTreeReason Plan(long characterId, ArmorClass tier, string opKey)
    {
        _toBag.Clear();
        _toMail.Clear();
        _draft = null;
        var equipped = _inventory.Rules.Equipped!;
        _inventory.WornArmorNotOf(characterId, tier, _slots);
        foreach (int slot in _slots)
        {
            if (_inventory.IsReserved(characterId, new InventoryLocation((byte)equipped.Code, slot)))
                return SkillTreeReason.ArmorReserved;
        }
        int free = _inventory.FreeSlotsFor(characterId, ItemCategory.Equipment);
        List<MailAttachment>? items = null;
        foreach (int slot in _slots)
        {
            if (_toBag.Count < free)
            {
                _toBag.Add(slot);
                continue;
            }
            if (!_inventory.TryPeekWorn(characterId, slot, out var attachment) || attachment == null)
                return SkillTreeReason.Unavailable;
            _toMail.Add(slot);
            (items ??= new List<MailAttachment>()).Add(attachment);
        }
        if (items == null)
            return SkillTreeReason.None;
        if (_mail() is not { } mail || !mail.IsAttached(characterId))
            return SkillTreeReason.Unavailable;
        var draft = new MailDraft(characterId, MailKind.ArmorReturn, MailSubjects.ArmorReturn, Array.Empty<MailArg>(), items, 0,
            MailKey(opKey), Source);
        if (mail.Rules.Check(draft) != null)
            return SkillTreeReason.Unavailable;
        _draft = draft;
        return SkillTreeReason.None;
    }
}
