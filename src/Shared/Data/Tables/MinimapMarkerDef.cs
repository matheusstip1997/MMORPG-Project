using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// What feeds a minimap marker kind (spec 9.3 "Minimap v0 marker set", WP-M2-10b). Config enum (camelCase names);
    /// append only.
    /// </summary>
    public enum MinimapSource : byte
    {
        /// <summary>The own character: the arrow at the center, turned by its facing.</summary>
        OwnEntity = 1,

        /// <summary>Party members (M4 fills the list; nothing in M2).</summary>
        PartyMember = 2,

        /// <summary>The world's npc markers whose <c>NpcDef</c> is of one of <see cref="MinimapMarkerDef.NpcKinds"/>.</summary>
        Npc = 3,

        /// <summary>
        /// The walkable crossings between the two <see cref="MinimapMarkerDef.Regions"/> (ridge passes, the tutorial gate of
        /// spec 12.3), found in the exported grid: one marker per connected crossing.
        /// </summary>
        RegionPass = 4,

        /// <summary>A hint ring on each camp of the active run's steps of <see cref="MinimapMarkerDef.QuestKinds"/>.</summary>
        RunCamp = 5,

        /// <summary>
        /// Where the own character fell (spec 9.5 "loot bag marker"; user decision 2026-10-28: player death loot bags are M4,
        /// R-4.3-03, so the client marks the death position until then). TODO(M4): the own loot bag entity becomes the source.
        /// </summary>
        DeathSpot = 6,

        /// <summary>
        /// The job nodes of spec 9.3 (WP-M3-4): the world's resourceNode markers whose <c>GatherNodes</c> row places one of
        /// <see cref="MinimapMarkerDef.NodeIds"/>, shown only when the node's job is one of the player's own jobs.
        /// </summary>
        JobNode = 7,
    }

    /// <summary>
    /// One minimap marker kind (spec 9.3, 9.12; WP-M2-10b). Client-only data, placeholders. Rules R-9.12-01 (each source's
    /// fields are set exactly when the source uses them), R-9.12-02 (one own-entity row, at most one death spot row, no region
    /// pair twice) and R-9.12-03 (color, size, a level the progression reaches).
    /// </summary>
    [Table("MinimapMarkerDef", 1)]
    public sealed class MinimapMarkerDef : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        public MinimapSource Source { get; set; }

        [Ref(typeof(MinimapIconRegistry))] public string IconId { get; set; } = string.Empty;

        /// <summary>The marker's color, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
        public string Color { get; set; } = string.Empty;

        /// <summary>Side of the marker's box in the reference resolution (spec 9.2: 1280 x 720).</summary>
        [Range(4, 32)] public int SizePx { get; set; }

        /// <summary>Lower draws first; the own arrow should draw last.</summary>
        [Range(0, 1000)] public int DrawOrder { get; set; }

        /// <summary>The marker shows from this own level on (0: always).</summary>
        [Range(0, 10_000)] public int VisibleFromLevel { get; set; }

        /// <summary><see cref="MinimapSource.Npc"/> only: the NPC kinds this row marks.</summary>
        public NpcKind[] NpcKinds { get; set; } = Array.Empty<NpcKind>();

        /// <summary><see cref="MinimapSource.RegionPass"/> only: the two regions whose walkable crossings this row marks.</summary>
        [Ref(typeof(RegionDef))] public string[] Regions { get; set; } = Array.Empty<string>();

        /// <summary><see cref="MinimapSource.RunCamp"/> only: the run step kinds whose camps get the hint (kill, returnItem).</summary>
        public QuestKind[] QuestKinds { get; set; } = Array.Empty<QuestKind>();

        /// <summary><see cref="MinimapSource.JobNode"/> only: the node types this row marks (WP-M3-4).</summary>
        [Optional, Ref(typeof(NodeDef))] public string[] NodeIds { get; set; } = Array.Empty<string>();

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (!Enum.IsDefined(typeof(MinimapSource), Source))
                errors.Add(nameof(Source), $"{(int)Source} is not a minimap source");
            if (!HudColor.TryParse(Color, out _))
                errors.Add(nameof(Color), $"'{Color}' is not #RRGGBB or #RRGGBBAA");

            // R-9.12-01: the source-specific fields.
            bool npc = Source == MinimapSource.Npc, pass = Source == MinimapSource.RegionPass, camp = Source == MinimapSource.RunCamp;
            if (npc != (NpcKinds.Length > 0))
                errors.Add(nameof(NpcKinds), npc ? "an npc row lists at least one NPC kind" : "only npc rows list NPC kinds");
            for (int i = 0; i < NpcKinds.Length; i++)
            {
                if (!Enum.IsDefined(typeof(NpcKind), NpcKinds[i]))
                    errors.Add(nameof(NpcKinds), $"{(int)NpcKinds[i]} is not an NPC kind");
                else if (Array.IndexOf(NpcKinds, NpcKinds[i]) != i)
                    errors.Add(nameof(NpcKinds), $"'{NpcKinds[i]}' is listed twice");
            }
            if (pass && (Regions.Length != 2 || string.Equals(Regions[0], Regions[1], StringComparison.Ordinal)))
                errors.Add(nameof(Regions), "a regionPass row names exactly two different regions");
            else if (!pass && Regions.Length != 0)
                errors.Add(nameof(Regions), "only regionPass rows name regions");
            if (camp != (QuestKinds.Length > 0))
                errors.Add(nameof(QuestKinds), camp ? "a runCamp row lists at least one step kind" : "only runCamp rows list step kinds");
            for (int i = 0; i < QuestKinds.Length; i++)
            {
                if (QuestKinds[i] != QuestKind.Kill && QuestKinds[i] != QuestKind.ReturnItem)
                    errors.Add(nameof(QuestKinds), $"'{QuestKinds[i]}' steps have no camps (kill and returnItem only)");
                else if (Array.IndexOf(QuestKinds, QuestKinds[i]) != i)
                    errors.Add(nameof(QuestKinds), $"'{QuestKinds[i]}' is listed twice");
            }
            bool node = Source == MinimapSource.JobNode;
            if (node != (NodeIds.Length > 0))
                errors.Add(nameof(NodeIds), node ? "a jobNode row lists at least one node type" : "only jobNode rows list node types");
            for (int i = 0; i < NodeIds.Length; i++)
            {
                if (Array.IndexOf(NodeIds, NodeIds[i]) != i)
                    errors.Add(nameof(NodeIds), $"'{NodeIds[i]}' is listed twice");
            }
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (!set.TryGet(typeof(MinimapMarkerDef), out var table))
                return;
            // R-9.12-02: one arrow, one death spot at most, each region pair once (whichever order it is written in).
            int own = 0, death = 0, samePair = 0;
            foreach (var row in table.Rows)
            {
                var other = (MinimapMarkerDef)row;
                own += other.Source == MinimapSource.OwnEntity ? 1 : 0;
                death += other.Source == MinimapSource.DeathSpot ? 1 : 0;
                if (Source == MinimapSource.RegionPass && other.Source == MinimapSource.RegionPass && SamePair(other))
                    samePair++;
            }
            if (Source == MinimapSource.OwnEntity && own != 1)
                errors.Add(nameof(Source), $"the table has {own} ownEntity rows, exactly one is allowed");
            if (Source == MinimapSource.DeathSpot && death > 1)
                errors.Add(nameof(Source), $"the table has {death} deathSpot rows, at most one is allowed");
            if (samePair > 1)
                errors.Add(nameof(Regions), $"the region pair {Regions[0]}/{Regions[1]} is marked by {samePair} rows");
            if (own == 0 && ReferenceEquals(table.Rows[0], this))
                errors.Add(nameof(Source), "the table needs one ownEntity row (the player arrow)");

            // R-9.12-03: a level the characters can reach.
            var progression = ProgressionSettings.Find(set);
            if (progression != null && VisibleFromLevel > progression.MaxLevel)
                errors.Add(nameof(VisibleFromLevel), $"{VisibleFromLevel} is above the level cap {progression.MaxLevel}");
        }

        private bool SamePair(MinimapMarkerDef other)
        {
            if (Regions.Length != 2 || other.Regions.Length != 2)
                return false;
            return (Same(Regions[0], other.Regions[0]) && Same(Regions[1], other.Regions[1]))
                   || (Same(Regions[0], other.Regions[1]) && Same(Regions[1], other.Regions[0]));
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
    }
}
