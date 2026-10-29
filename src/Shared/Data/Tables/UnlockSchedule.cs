using System;
using System.Collections.Generic;

namespace Shared.Data.Tables
{
    /// <summary>When a scheduled tutorial prompt shows (R-12.3-02); in every case the level must be reached too.</summary>
    public enum UnlockTrigger
    {
        /// <summary>As soon as the character has the row's level: at entry or at the level-up.</summary>
        LevelReached = 0,

        /// <summary>The first time a drop is in pickup reach (the pickup prompt's rule): the first pickup.</summary>
        LootInReach = 1,

        /// <summary>The first time an outpost NPC is in interaction reach.</summary>
        OutpostInReach = 2,

        /// <summary>The first time a vendor NPC is in interaction reach.</summary>
        VendorInReach = 3,

        /// <summary>After the first committed sale at an outpost.</summary>
        ItemSold = 4,
    }

    /// <summary>
    /// The tutorial's unlock schedule (spec 12.3 "M2 decisions", R-12.3-02; user decision 2026-10-28): one row per unlock,
    /// a level, the contextual prompt it shows once per character (R-12.3-03) and the HUD elements that appear from that
    /// level (spec 9.12 "HUD by level"). Client-only; no scripted quest chain (R-5.5-01). An element no row lists is always
    /// visible; menus are not locked. Rows are shown in level order, then row order. All rows are placeholders.
    /// </summary>
    [Table("UnlockSchedule", 1)]
    public sealed class UnlockSchedule : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = "";

        /// <summary>The level from which the row applies.</summary>
        [Range(1, 10_000)] public int Level { get; set; }

        /// <summary>When the prompt shows (<see cref="LevelReached"/> for a row with HUD elements only).</summary>
        public UnlockTrigger Trigger { get; set; }

        /// <summary>The <c>UiStrings</c> id of the prompt; empty for a row that only reveals HUD elements.</summary>
        [Optional, Ref(typeof(UiStrings), Optional = true)] public string Prompt { get; set; } = "";

        /// <summary>The <c>HudLayout</c> element ids hidden below <see cref="Level"/>; each id in at most one row.</summary>
        [Optional, Ref(typeof(HudLayout))] public string[] HudElements { get; set; } = Array.Empty<string>();

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (string.IsNullOrEmpty(Prompt) && HudElements.Length == 0)
                errors.Add(nameof(Prompt), "a row shows a prompt, HUD elements or both");
            if (HudElements.Length > 0 && Trigger != UnlockTrigger.LevelReached)
                errors.Add(nameof(Trigger), "HUD elements appear with the level: use levelReached");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in HudElements)
            {
                if (!seen.Add(element))
                    errors.Add(nameof(HudElements), $"'{element}' is listed twice");
            }
        }

        /// <summary>
        /// The level is at most the level cap; a prompt and a HUD element belong to one row only (one level each, and a prompt
        /// shown once per character, R-12.3-03).
        /// </summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (ProgressionSettings.Find(set) is { } progression && Level > progression.MaxLevel)
                errors.Add(nameof(Level), $"{Level} is above ProgressionSettings.maxLevel {progression.MaxLevel}");
            if (!set.TryGet(typeof(UnlockSchedule), out var table))
                return;
            foreach (var row in table.Rows)
            {
                var other = (UnlockSchedule)row;
                if (ReferenceEquals(other, this))
                    break;
                if (Prompt.Length > 0 && other.Prompt == Prompt)
                    errors.Add(nameof(Prompt), $"'{Prompt}' is already the prompt of row '{other.Id}'");
                foreach (var element in HudElements)
                {
                    if (Array.IndexOf(other.HudElements, element) >= 0)
                        errors.Add(nameof(HudElements), $"'{element}' is already listed by row '{other.Id}'");
                }
            }
        }
    }
}
