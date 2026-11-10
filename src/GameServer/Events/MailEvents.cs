using Shared.Protocol;

namespace GameServer.Events;

// System mail events (spec 4.5 M4 mail, 8.1; WP-M4-3). Published by the world only after the change is in the database
// (rule 5: commit, then events). Item ids are ItemDef.netId.

/// <summary>One attachment an event names: the item, its rarity and the units.</summary>
public readonly record struct MailItemCount(ushort ItemNetId, byte Rarity, int Count);

/// <summary>
/// A mail was written for <see cref="ToCharacterId"/> (spec 8.1 <c>MailSent</c>): by the save of <see cref="CarrierCharacterId"/>
/// (0 for a stand-alone send: the admin panel, a world job), with its attachments and gold, under the operation key that
/// produced them.
/// </summary>
public readonly record struct MailSent(long ToCharacterId, long MailId, MailKind Kind, IReadOnlyList<MailItemCount> Items, long Gold,
    long CarrierCharacterId, string OpKey) : IGameEvent;

/// <summary>
/// A character claimed a mail's attachments into its bag (spec 8.1, 8.2 the claim): what moved, and whether the mail is now
/// empty (<see cref="Complete"/>; a partial claim leaves the rest in the mail).
/// </summary>
public readonly record struct MailClaimed(long CharacterId, long MailId, MailKind Kind, IReadOnlyList<MailItemCount> Items, long Gold,
    bool Complete, string OpKey) : IGameEvent;

/// <summary>
/// An open mail passed its expiry and its attachments were destroyed (spec 4.5: a sink; <c>EconomyLedger</c> counts the gold
/// and the units under <see cref="InventoryReason.MailExpired"/>). Published once per mail by the world whose expiry job
/// closed it.
/// </summary>
public readonly record struct MailExpired(long CharacterId, long MailId, MailKind Kind, IReadOnlyList<MailItemCount> Items, long Gold)
    : IGameEvent;
