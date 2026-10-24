namespace Shared.Data.Tables
{
    /// <summary>
    /// One damage element and its resistance caps for step 5 of damage formula v1 (spec 3.4 R-3.4-04, catalog 8.5):
    /// factor = 100 - clamp(resist, min, max) percent; a negative minimum allows weakness. R-3.8-10: the same resist
    /// stat also drives build-ups, with separate caps and formula there, so these fields are for damage only.
    /// The ids are the camelCase names of <see cref="Element"/> (R-3.4-07): <c>CombatConfig.TryBuild</c> wants exactly
    /// one row per element.
    /// </summary>
    [Table("ElementCaps", 1)]
    public sealed class ElementCaps : IValidatableRow
    {
        /// <summary>The element, for example <c>fire</c> (<see cref="Element"/> in camelCase).</summary>
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
            else if (!Elements.TryParse(Id, out _))
                errors.Add(nameof(Id), "must name an element of R-3.4-07 (fire, ice, water, wind, earth, lightning, holy, dark, blood, poison)");
            if (DamageResistMinPercent > DamageResistMaxPercent)
                errors.Add(nameof(DamageResistMinPercent), "must not be greater than DamageResistMaxPercent");
        }
    }
}
