namespace Shared.Data.Tables
{
    /// <summary>
    /// One damage element and its resistance caps for step 5 of damage formula v1 (spec 3.4 R-3.4-04, catalog 8.5):
    /// factor = 100 - clamp(resist, min, max) percent; a negative minimum allows weakness. R-3.8-10: the same resist
    /// stat also drives build-ups, with separate caps and formula there, so these fields are for damage only.
    /// TODO(open): the spec defines no element list; the rows are placeholders taken from the 3.5 class table.
    /// </summary>
    [Table("ElementCaps", 1)]
    public sealed class ElementCaps : IValidatableRow
    {
        /// <summary>Stable id, for example <c>fire</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>The defender's resistance stat for this element, in whole percents.</summary>
        [Ref(typeof(StatDef))] public string ResistStat { get; set; } = string.Empty;

        /// <summary>Lowest resistance the damage formula uses; -50 means at most 150% damage.</summary>
        [Range(-900, 100)] public int DamageResistMinPercent { get; set; }

        /// <summary>Highest resistance the damage formula uses; 100 would mean immune.</summary>
        [Range(-900, 100)] public int DamageResistMaxPercent { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (DamageResistMinPercent > DamageResistMaxPercent)
                errors.Add(nameof(DamageResistMinPercent), "must not be greater than DamageResistMaxPercent");
        }
    }
}
