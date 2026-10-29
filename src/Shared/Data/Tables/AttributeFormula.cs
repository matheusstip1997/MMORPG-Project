namespace Shared.Data.Tables
{
    /// <summary>
    /// How the primary attributes feed one derived stat (spec 3.4 "M2 primary attributes", R-3.4-01/02; user decision
    /// 2026-10-27, placeholder formula): a coefficient per attribute in hundredths, so the stat's base becomes
    /// <c>class base + floor((str x Str + dex x Dex + sta x Sta + int x Int) / 100)</c> before its modifiers
    /// (<see cref="Shared.Simulation.Stats.StatBlock"/>). One row per derived stat; a stat without a row takes nothing
    /// from the attributes. The attributes are the final ones (gear and statuses on STR count). <c>CombatConfig.TryBuild</c>
    /// checks that the stat is not an attribute itself (no cycles). Every coefficient is a placeholder until the Warrior K pass.
    /// </summary>
    [Table("AttributeFormula", 1)]
    public sealed class AttributeFormula : IValidatableRow
    {
        public const int MaxCoefficient = 100_000;

        /// <summary>The derived stat, a <see cref="StatDef"/> id other than <c>str</c>, <c>dex</c>, <c>sta</c>, <c>int</c>.</summary>
        [TableId, Ref(typeof(StatDef))] public string Id { get; set; } = string.Empty;

        /// <summary>Hundredths of the stat per point of STR.</summary>
        [Range(-MaxCoefficient, MaxCoefficient)] public int Str { get; set; }

        [Range(-MaxCoefficient, MaxCoefficient)] public int Dex { get; set; }

        [Range(-MaxCoefficient, MaxCoefficient)] public int Sta { get; set; }

        [Range(-MaxCoefficient, MaxCoefficient)] public int Int { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Str == 0 && Dex == 0 && Sta == 0 && Int == 0)
                errors.Add(nameof(Str), "a row needs at least one coefficient other than 0");
        }
    }
}
