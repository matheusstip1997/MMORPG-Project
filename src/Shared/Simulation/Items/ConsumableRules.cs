using System;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace Shared.Simulation.Items
{
    /// <summary>
    /// The meter rules of using a consumable (R-3.4-03, spec 3.4 "Consumable use"; WP-M3-3), shared by the server (before the
    /// action starts and again at its effect point), the client's pre-check and the economy simulator. Pure; no allocation.
    /// </summary>
    public static class ConsumableRules
    {
        /// <summary>
        /// <c>TooToxic</c> when the potion's toxicity would pass <c>toxicityMax</c>, <c>TooFull</c> when the food's satiety
        /// would pass <c>satietyMax</c> ("you cannot overeat"), otherwise <c>None</c>. Reaching the cap exactly is allowed.
        /// The meters are in thousandths of a point (<see cref="DecayMeter"/>), read at the moment of use.
        /// </summary>
        public static UseItemReason Check(ConsumableDef row, long toxicityMilli, long satietyMilli,
            ConsumptionMeterSettings settings)
        {
            if (row.Toxicity > 0 && Passes(toxicityMilli, row.Toxicity, settings.ToxicityMax))
                return UseItemReason.TooToxic;
            if (row.Satiety > 0 && Passes(satietyMilli, row.Satiety, settings.SatietyMax))
                return UseItemReason.TooFull;
            return UseItemReason.None;
        }

        /// <summary>
        /// The most uses an hour can hold in steady state (the economy simulator's cap on <c>usedPerPlayerHour</c>): the
        /// meter's decay per hour over the cost, and the cooldown. <see cref="int.MaxValue"/> when nothing limits it.
        /// </summary>
        public static int MaxUsesPerHour(ConsumableDef row, ConsumptionMeterSettings settings)
        {
            long most = int.MaxValue;
            if (row.CooldownMs > 0)
                most = Math.Min(most, 3_600_000L / row.CooldownMs);
            if (row.Toxicity > 0)
                most = Math.Min(most, 60L * Math.Max(0, settings.ToxicityDecayPerMinute) / row.Toxicity);
            if (row.Satiety > 0)
                most = Math.Min(most, 60L * Math.Max(0, settings.SatietyDecayPerMinute) / row.Satiety);
            return (int)most;
        }

        private static bool Passes(long meterMilli, int cost, int max) =>
            Math.Max(0, meterMilli) + (long)cost * DecayMeter.MilliPerPoint > (long)Math.Max(0, max) * DecayMeter.MilliPerPoint;
    }
}
