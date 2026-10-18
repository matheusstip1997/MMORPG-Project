using System;
using Shared.Simulation.Stats;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Step 4 of damage formula v1 (spec 3.4 R-3.4-04, catalog 8.5): the damage factor of an attacker rank against a
    /// defender rank. <c>CombatConfig.TryBuild</c> checks that every pair appears exactly once and that each factor lies
    /// in the <see cref="DamageConstants"/> bounds. Ranks are camelCase strings (<c>f</c> ... <c>ss</c>).
    /// </summary>
    [Table("RankFactor", 1)]
    public sealed class RankFactor : IValidatableRow
    {
        public Rank Attacker { get; set; }

        public Rank Defender { get; set; }

        [Range(0, 1_000)] public int FactorPercent { get; set; }

        public void Validate(RowErrors errors)
        {
            if (!Enum.IsDefined(typeof(Rank), Attacker))
                errors.Add(nameof(Attacker), $"{(int)Attacker} is not a rank");
            if (!Enum.IsDefined(typeof(Rank), Defender))
                errors.Add(nameof(Defender), $"{(int)Defender} is not a rank");
        }
    }
}
