namespace Shared.Data.Tables
{
    /// <summary>
    /// One stat a <see cref="Shared.Simulation.Stats.StatBlock"/> holds (spec 3.4 R-3.4-02, catalog 8.5). The row
    /// order is the stat index used at runtime. <see cref="Min"/> and <see cref="Max"/> clamp the final value after
    /// modifiers (the cooldown reduction cap, for example, will be a <see cref="Max"/>). Code reads some stats by id
    /// (<see cref="Shared.Simulation.Stats.StatIds"/>); <c>CombatConfig.TryBuild</c> checks that they exist.
    /// Units: whole points; whole percents for penetration percent, crit rate, crit damage and resistances;
    /// points per second for regeneration.
    /// TODO(open): primary attributes (STR, DEX, STA, INT, R-3.4-01) and how they feed the derived stats have no
    /// formula in the spec yet; they are added here when they do.
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
