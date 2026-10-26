using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// One build-up (spec 3.8 <c>BuildupDef</c>, backlog D6; M1 has Burn only): a meter from 0 to <see cref="MeterMax"/> per
    /// target (R-3.8-02) that hits raise (<see cref="EffectBuildUpDef"/> rows) and that triggers <see cref="StatusId"/> when
    /// it is full. The meter stays full while that status lasts, then resets to 0. Gains are scaled by the target's
    /// resistance to <see cref="Element"/> (R-3.8-10: the element's one resistance stat, with this row's own formula, not the
    /// damage caps of <see cref="ElementCaps"/>) and by the repeat penalty (R-3.8-05); a meter decays at a steady rate after
    /// a delay without a gain (R-3.8-06). Numbers are placeholders (spec 10: M2). TODO(open): the boss rules of R-3.8-08
    /// (a cap or lower multiplier per boss; immunity already comes from the status's tags), the arena normalization (R-3.8-09).
    /// </summary>
    [Table("BuildUpDef", 1)]
    public sealed class BuildUpDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>R-3.8-02: every meter runs from 0 to 100 and triggers at 100.</summary>
        public const int MeterMax = 100;

        /// <summary>Wire limit of <see cref="NetId"/> (4 bits in the own-entity snapshot block, spec 1.7).</summary>
        public const int MaxNetId = 15;

        /// <summary>Penalty levels a row may list (R-3.8-05).</summary>
        public const int MaxPenaltyLevels = 4;

        /// <summary>Stable id, for example <c>burn</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Id on the wire (own build-up meters in the snapshot header); unique.</summary>
        [Range(1, MaxNetId)] public int NetId { get; set; }

        /// <summary>R-3.8-10: the element whose resistance stat scales gains (Burn: fire); one build-up per element.</summary>
        public Element Element { get; set; }

        /// <summary>The status a full meter triggers through the status system (spec 3.7; Burn: <c>burn</c>).</summary>
        [Ref(typeof(StatusEffectDef))] public string StatusId { get; set; } = string.Empty;

        /// <summary>R-3.8-06: the meter starts to drop this long after the last hit that raised it.</summary>
        [Range(0, 600_000)] public int DecayDelayMs { get; set; }

        /// <summary>R-3.8-06: then it drops this many points per second.</summary>
        [Range(1, MeterMax)] public int DecayPerSec { get; set; }

        /// <summary>
        /// R-3.8-10: the build-up formula reads the element's resistance stat times this percent (100 = the same value as
        /// for damage), so build-up and damage resistance can become two stats later without code changes.
        /// </summary>
        [Range(0, 1_000)] public int ResistScalePercent { get; set; }

        /// <summary>R-3.8-03: the scaled resistance is clamped to at least this (negative: a weakness raises gains).</summary>
        [Range(-1_000, 100)] public int ResistMinPercent { get; set; }

        /// <summary>R-3.8-03: and at most this ("resistance is capped"; 100 would be immunity).</summary>
        [Range(-1_000, 100)] public int ResistMaxPercent { get; set; }

        /// <summary>
        /// R-3.8-05: how long after the meter resets (its effect ended) a new trigger deepens the penalty. A trigger inside
        /// the window goes one level deeper (at most the last level); a trigger after it starts again at the first level.
        /// </summary>
        [Range(0, 600_000)] public int RepeatPenaltyWindowMs { get; set; }

        /// <summary>
        /// R-3.8-05: the gain in percent at each penalty level while its window runs (level 1 first), at most
        /// <see cref="MaxPenaltyLevels"/> entries, each 0-100 and none above the one before ("chains get progressively harder").
        /// </summary>
        public int[] RepeatPenaltyGainPercents { get; set; } = Array.Empty<int>();

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Element == Element.None || (uint)Element > (uint)Elements.Last)
                errors.Add(nameof(Element), "must be one of the 10 elements (R-3.8-10)");
            if (ResistMinPercent > ResistMaxPercent)
                errors.Add(nameof(ResistMinPercent), "must not be above resistMaxPercent");
            var levels = RepeatPenaltyGainPercents;
            if (levels.Length == 0 || levels.Length > MaxPenaltyLevels)
                errors.Add(nameof(RepeatPenaltyGainPercents), $"needs 1 to {MaxPenaltyLevels} levels (R-3.8-05)");
            for (int i = 0; i < levels.Length; i++)
            {
                if (levels[i] < 0 || levels[i] > 100)
                    errors.Add(nameof(RepeatPenaltyGainPercents), "each level is 0 to 100 percent");
                else if (i > 0 && levels[i] > levels[i - 1])
                    errors.Add(nameof(RepeatPenaltyGainPercents), "a deeper level must not give more than the one before (R-3.8-05)");
            }
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            NetIds.CheckUnique<BuildUpDef>(set, this, NetId, row => row.NetId, row => row.Id, errors);
            if (!set.TryGet(typeof(BuildUpDef), out var table))
                return;
            foreach (var row in table.Rows)
            {
                var other = (BuildUpDef)row;
                if (!ReferenceEquals(other, this) && other.Element == Element)
                {
                    errors.Add(nameof(Element), $"'{other.Id}' already uses {Element} (one build-up per element, R-3.8-10)");
                    break;
                }
            }
        }

        public static BuildUpDef? Find(ConfigSet set, string id) => NetIds.Find<BuildUpDef>(set, id, row => row.Id);
    }

    /// <summary>
    /// One build-up change of a hit (spec 3.8: "action data lists build-up changes per hit, for example Burn +20, Cold -10"):
    /// every hit of <see cref="EffectId"/> that lands (not dodged or parried, R-3.8-07) changes the target's
    /// <see cref="BuildUpId"/> meter by <see cref="Amount"/> points. A gain is scaled by resistance and the repeat penalty
    /// (R-3.8-03); a reduction (negative, R-3.8-04) is not. Config rows hold no nested objects, so an effect with several
    /// changes has several rows (at most <see cref="MaxPerEffect"/>, one per build-up). Only effects that hit enemies have
    /// them: Strike, Projectile, and a GroundArea with <c>enemies</c> (each of its ticks).
    /// </summary>
    [Table("EffectBuildUpDef", 1)]
    public sealed class EffectBuildUpDef : IValidatableRow, ISetValidatableRow
    {
        public const int MaxPerEffect = 4;

        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(ActionEffectDef))] public string EffectId { get; set; } = string.Empty;

        [Ref(typeof(BuildUpDef))] public string BuildUpId { get; set; } = string.Empty;

        /// <summary>Points per hit: a gain (positive) or a reduction (negative), never 0.</summary>
        [Range(-BuildUpDef.MeterMax, BuildUpDef.MeterMax)] public int Amount { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Amount == 0)
                errors.Add(nameof(Amount), "a build-up change must change something");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (set.TryGet(typeof(ActionEffectDef), out var effects))
            {
                foreach (var row in effects.Rows)
                {
                    var effect = (ActionEffectDef)row;
                    if (effect.Id == EffectId && !effect.HitsEnemies())
                        errors.Add(nameof(EffectId), $"'{EffectId}' is a {effect.Kind} that hits no enemies; only Strike, Projectile and GroundArea on enemies change build-ups");
                }
            }
            if (!set.TryGet(typeof(EffectBuildUpDef), out var table))
                return;
            int count = 0;
            bool repeated = false;
            foreach (var row in table.Rows)
            {
                var other = (EffectBuildUpDef)row;
                if (other.EffectId != EffectId)
                    continue;
                count++;
                repeated |= !ReferenceEquals(other, this) && other.BuildUpId == BuildUpId;
            }
            if (repeated)
                errors.Add(nameof(BuildUpId), $"'{EffectId}' changes '{BuildUpId}' twice; use one row");
            if (count > MaxPerEffect)
                errors.Add(nameof(EffectId), $"'{EffectId}' has {count} build-up changes, at most {MaxPerEffect}");
        }
    }
}
