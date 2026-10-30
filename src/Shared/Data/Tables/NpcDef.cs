using System;

namespace Shared.Data.Tables
{
    /// <summary>What kind of NPC a <see cref="NpcDef"/> row is (spec 4.2). Config enum (camelCase names); append only.</summary>
    public enum NpcKind : byte
    {
        /// <summary>An Adventurer Guild outpost: sells sell-only drops (R-4.2-01), rank-up (R-3.6-04), quests (R-5.5-01).</summary>
        Outpost = 1,

        /// <summary>A city NPC vendor: sells potions and food at fixed prices, never buys (R-4.2-01, spec 4.1 M2 item model).</summary>
        Vendor = 2,

        /// <summary>A city job master: the permanent job pick (spec 5.1 M3 decisions, WP-M3-1).</summary>
        JobMaster = 3,
    }

    /// <summary>
    /// An interaction an NPC offers (spec 4.2, WP-M2-4). Config enum (camelCase names) and on the wire in
    /// <c>OutpostInteractRequest</c>: append only, never renumber. Each kind has one server handler registered with the
    /// world's <c>OutpostInteractions</c> (sell and buy by WP-M2-4, rankUp by WP-M2-3, quests by WP-M2-6).
    /// </summary>
    public enum NpcInteraction : byte
    {
        None = 0,
        Sell = 1,
        Buy = 2,
        RankUp = 3,
        Quests = 4,
        /// <summary>Pick the main or the secondary job (WP-M3-1); the job master offers it.</summary>
        Jobs = 5,
    }

    /// <summary>
    /// An NPC placed in the world by an exported <c>npc</c> marker whose <c>defId</c> names this row (spec 4.2, 11.6;
    /// WP-M2-4). NPCs are entities in the snapshot (drawn with <see cref="VisualId"/>), never combatants, never targetable.
    /// Values in data/tables/NpcDef.json are placeholders.
    /// </summary>
    [Table("NpcDef", 1)]
    public sealed class NpcDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Largest <see cref="NetId"/>: it travels as 16 bits in spawn events.</summary>
        public const int MaxNetId = ushort.MaxValue;

        /// <summary>Stable id, for example <c>m1.outpost</c>; the <c>defId</c> of npc markers.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Id on the wire (the spawn event's definition id); unique, never reused.</summary>
        [Range(1, MaxNetId)] public int NetId { get; set; }

        public NpcKind Kind { get; set; }

        /// <summary>What a player may do with it, the first one being what the interaction key opens.</summary>
        public NpcInteraction[] Interactions { get; set; } = Array.Empty<NpcInteraction>();

        /// <summary>Look of the NPC (placeholder model in M2).</summary>
        [Ref(typeof(EntityVisualDef))] public string VisualId { get; set; } = string.Empty;

        /// <summary>Display name (spec 9.1: user-facing text only from tables).</summary>
        [Ref(typeof(UiStrings))] public string NameId { get; set; } = string.Empty;

        /// <summary>A player whose position is at most this far from the NPC's may interact (server check, reason OutOfRange).</summary>
        [Range(1, 10_000)] public int InteractRangeCm { get; set; }

        public bool Offers(NpcInteraction interaction) => Array.IndexOf(Interactions, interaction) >= 0;

        /// <summary>Interactions each kind may offer: the outpost's of spec 4.2, 3.6 and 5.5; the vendor only sells.</summary>
        public static bool Allows(NpcKind kind, NpcInteraction interaction) => kind switch
        {
            NpcKind.Outpost => interaction == NpcInteraction.Sell || interaction == NpcInteraction.RankUp
                               || interaction == NpcInteraction.Quests,
            NpcKind.Vendor => interaction == NpcInteraction.Buy,
            NpcKind.JobMaster => interaction == NpcInteraction.Jobs,
            _ => false,
        };

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (!Enum.IsDefined(typeof(NpcKind), Kind))
                errors.Add(nameof(Kind), $"{(int)Kind} is not an NPC kind");
            if (Interactions.Length == 0)
                errors.Add(nameof(Interactions), "must list at least one interaction");
            for (int i = 0; i < Interactions.Length; i++)
            {
                var interaction = Interactions[i];
                if (interaction == NpcInteraction.None || !Enum.IsDefined(typeof(NpcInteraction), interaction))
                    errors.Add(nameof(Interactions), $"{(int)interaction} is not an interaction");
                else if (Array.IndexOf(Interactions, interaction) != i)
                    errors.Add(nameof(Interactions), $"lists {interaction} twice");
                else if (Enum.IsDefined(typeof(NpcKind), Kind) && !Allows(Kind, interaction))
                    errors.Add(nameof(Interactions), $"a {Kind} does not offer {interaction}");
            }
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            NetIds.CheckUnique<NpcDef>(set, this, NetId, row => row.NetId, row => row.Id, errors);
            if (Offers(NpcInteraction.Buy) && set.TryGet(typeof(NpcVendorStock), out var stock))
            {
                foreach (var row in stock.Rows)
                {
                    if (((NpcVendorStock)row).NpcId == Id)
                        return;
                }
                errors.Add(nameof(Interactions), "offers buy but has no NpcVendorStock row");
            }
        }

        public static NpcDef? Find(ConfigSet set, string id) => NetIds.Find<NpcDef>(set, id, row => row.Id);
    }
}
