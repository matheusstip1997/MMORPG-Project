using System;

namespace Shared.Data.Tables
{
    /// <summary>The sections of the character sheet, top to bottom (WP-M2-11).</summary>
    public enum CharacterSheetSection
    {
        Attributes,
        Offence,
        Defence,
        Resources,
        Resistances,
        Other,
    }

    /// <summary>How a stat's value reads on the character sheet (the units of <c>StatDef</c>, spec 3.4).</summary>
    public enum StatUnit
    {
        /// <summary>Whole points: "124".</summary>
        Points,
        /// <summary>Whole percents: "15%".</summary>
        Percent,
        /// <summary>Points per second (the regenerations): "3/s".</summary>
        PerSecond,
    }

    /// <summary>
    /// The character sheet (spec 9.13 M2, WP-M2-11): where each stat of <c>OwnStats</c> shows and how its value reads.
    /// Client-only display choices. One row per <see cref="StatDef"/> row (the set rule), so a new stat gets a place on the
    /// sheet in the change that adds it; its name is the <c>UiStrings</c> row <c>stat.&lt;id&gt;</c> (<see cref="UiStringCoverage"/>).
    /// The set rule also checks the <see cref="Hotkeys"/> row that opens the sheet.
    /// </summary>
    [Table("CharacterSheetStats", 1)]
    public sealed class CharacterSheetStats : IValidatableRow, ISetValidatableRow
    {
        [TableId, Ref(typeof(StatDef))] public string Id { get; set; } = string.Empty;

        public CharacterSheetSection Section { get; set; }

        /// <summary>Order inside the section (ties keep the table's order).</summary>
        [Range(0, 10_000)] public int Order { get; set; }

        public StatUnit Unit { get; set; }

        public void Validate(RowErrors errors)
        {
            if (!Enum.IsDefined(typeof(CharacterSheetSection), Section))
                errors.Add(nameof(Section), $"{(int)Section} is not a section");
            if (!Enum.IsDefined(typeof(StatUnit), Unit))
                errors.Add(nameof(Unit), $"{(int)Unit} is not a unit");
        }

        /// <summary>Checked once for the whole table (on its first row): every stat has a row, and the sheet has its key.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (!set.TryGet(typeof(CharacterSheetStats), out var table) || !ReferenceEquals(table.Rows[0], this))
                return;
            if (Hotkeys.Find(set, Hotkeys.ToggleCharacter) == null)
                errors.Add(null, $"the character sheet needs a Hotkeys row '{Hotkeys.ToggleCharacter}'");
            if (!set.TryGet(typeof(StatDef), out var stats))
                return;
            var placed = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var row in table.Rows)
                placed.Add(((CharacterSheetStats)row).Id);
            foreach (var row in stats.Rows)
            {
                string id = ((StatDef)row).Id;
                if (!placed.Contains(id))
                    errors.Add(nameof(Id), $"StatDef '{id}' has no row: every stat of OwnStats shows on the character sheet");
            }
        }
    }
}
