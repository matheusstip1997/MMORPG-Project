namespace Shared.Data.Tables
{
    /// <summary>
    /// One stat a <see cref="Shared.Simulation.Stats.StatBlock"/> holds (spec 3.4 R-3.4-02, catalog 8.5). The row
    /// order is the stat index used at runtime. <see cref="Min"/> and <see cref="Max"/> clamp the final value after
    /// modifiers (the cooldown reduction cap, for example, will be a <see cref="Max"/>). Code reads some stats by id
    /// (<see cref="Shared.Simulation.Stats.StatIds"/>); <c>CombatConfig.TryBuild</c> checks that they exist.
    /// Units: whole points; whole percents for penetration percent, crit rate, crit damage and resistances;
    /// points per second for regeneration.
    /// The primary attributes (<c>str</c>, <c>dex</c>, <c>sta</c>, <c>int</c>, R-3.4-01) are stats too, so gear and statuses
    /// can change them; <see cref="AttributeFormula"/> feeds them into the derived stats (WP-M2-2). Every stat is capped
    /// by its min and max, multipliers included (architecture rule 2).
    /// </summary>
    [Table("StatDef", 1)]
    public sealed class StatDef : IValidatableRow
    {
        /// <summary>Stable id, for example <c>atk</c> or <c>fireRes</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        [Range(-1_000_000_000, 1_000_000_000)] public int Min { get; set; }

        [Range(-1_000_000_000, 1_000_000_000)] public int Max { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Min > Max)
                errors.Add(nameof(Min), "must not be greater than Max");
        }
    }
}
