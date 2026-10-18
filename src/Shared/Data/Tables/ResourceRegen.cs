namespace Shared.Data.Tables
{
    /// <summary>
    /// Regeneration delays of the HP and MP meters (spec 3.4 R-3.4-03): after a spend or damage, regeneration waits
    /// this long. The stamina delay is the M1 starting value <see cref="StartingValuesM1.StaminaRegenDelayMs"/>
    /// (spec 12.7); regeneration rates are stats (<c>hpRegen</c>, <c>mpRegen</c>, <c>staminaRegen</c>).
    /// Both values are placeholders: the spec gives none.
    /// </summary>
    [Table("ResourceRegen", 1)]
    [Singleton]
    public sealed class ResourceRegen
    {
        [Range(0, 600_000)] public int HpRegenDelayMs { get; set; }

        [Range(0, 600_000)] public int MpRegenDelayMs { get; set; }
    }
}
