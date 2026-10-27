namespace Shared.Data.Tables
{
    /// <summary>
    /// The intoxication and satiety meters of R-3.4-03 (spec 3.4, WP-M2-2): their caps and how fast they decay, in whole
    /// points. Potions raise toxicity and food raises satiety (M3); a meter never goes above its max, so you cannot drink or
    /// eat past it. Both decay with time computed from UTC timestamps (<c>IClock</c>), also while the character is offline.
    /// Every value is a placeholder: the spec gives none.
    /// </summary>
    [Table("ConsumptionMeterSettings", 1)]
    [Singleton]
    public sealed class ConsumptionMeterSettings
    {
        [Range(1, 1_000_000)] public int ToxicityMax { get; set; }

        /// <summary>Points lost per minute.</summary>
        [Range(0, 1_000_000)] public int ToxicityDecayPerMinute { get; set; }

        [Range(1, 1_000_000)] public int SatietyMax { get; set; }

        [Range(0, 1_000_000)] public int SatietyDecayPerMinute { get; set; }
    }
}
