using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Simulation.Stats
{
    /// <summary>
    /// The runtime form of the stat and damage tables (<see cref="StatDef"/>, <see cref="DamageConstants"/>,
    /// <see cref="RankFactor"/>, <see cref="ElementCaps"/>, <see cref="ResourceRegen"/> and the crit and stamina
    /// starting values of <see cref="StartingValuesM1"/>), built once after config loads. Immutable, shared by the server
    /// and the client (tooltip estimates, spec 3.4). <see cref="TryBuild"/> also checks the rules that span rows or
    /// tables, which the row validator cannot express.
    /// </summary>
    public sealed class CombatConfig
    {
        private readonly int[] _mitigationByRank;
        private readonly int[] _rankFactors;
        private readonly string[] _elementIds;
        private readonly int[] _elementResistStat;
        private readonly int[] _elementResistMin;
        private readonly int[] _elementResistMax;
        private readonly int[] _indexByElement = new int[Elements.Count + 1];

        private CombatConfig(StatRules stats, AttributeFormulaRules attributes, DamageConstants damage, int[] rankFactors,
            IReadOnlyList<ElementCaps> elements, StartingValuesM1 starting, ResourceRegen regen)
        {
            Stats = stats;
            Attributes = attributes;
            Ids = new CombatStatIds(stats);
            _mitigationByRank = (int[])damage.MitigationConstantByRank.Clone();
            _rankFactors = rankFactors;
            CritChanceCapPercent = damage.CritChanceCapPercent;
            CritDamageBonusCapPercent = damage.CritDamageBonusCapPercent;
            MinDamagePerHit = damage.MinDamagePerHit;
            BaseCritChancePercent = starting.CritChancePercent;
            BaseCritDamagePercent = starting.CritDamagePercent;
            StaminaRegenDelayMs = starting.StaminaRegenDelayMs;
            HpRegenDelayMs = regen.HpRegenDelayMs;
            MpRegenDelayMs = regen.MpRegenDelayMs;

            _elementIds = new string[elements.Count];
            _elementResistStat = new int[elements.Count];
            _elementResistMin = new int[elements.Count];
            _elementResistMax = new int[elements.Count];
            for (int i = 0; i < elements.Count; i++)
            {
                _elementIds[i] = elements[i].Id;
                _elementResistStat[i] = stats.IdFor(elements[i].ResistStat);
                _elementResistMin[i] = elements[i].DamageResistMinPercent;
                _elementResistMax[i] = elements[i].DamageResistMaxPercent;
            }
            _indexByElement[(int)Element.None] = DamageCalc.NoElement;
            for (int i = 0; i < elements.Count; i++)
            {
                if (Shared.Data.Tables.Elements.TryParse(elements[i].Id, out var element))
                    _indexByElement[(int)element] = i;
            }
        }

        public StatRules Stats { get; }

        /// <summary>
        /// How the primary attributes feed the derived stats (<see cref="AttributeFormula"/>, spec 3.4 "M2 primary
        /// attributes"); a character's stat block is built with it. Without the table (hand-built test sets) it has no rows.
        /// </summary>
        public AttributeFormulaRules Attributes { get; }

        /// <summary>Indices of the stats code reads by name.</summary>
        public CombatStatIds Ids { get; }

        /// <summary>Spec 12.7: 5% base chance (<c>StartingValuesM1.CritChancePercent</c>).</summary>
        public int BaseCritChancePercent { get; }

        /// <summary>Spec 12.7: 150% damage (<c>StartingValuesM1.CritDamagePercent</c>), the "1.5" of R-3.4-04 step 6.</summary>
        public int BaseCritDamagePercent { get; }

        public int CritChanceCapPercent { get; }

        public int CritDamageBonusCapPercent { get; }

        /// <summary>R-3.4-06: least damage of a damaging hit before crit and step 7 (<c>DamageConstants.MinDamagePerHit</c>).</summary>
        public int MinDamagePerHit { get; }

        public int HpRegenDelayMs { get; }

        public int MpRegenDelayMs { get; }

        public int StaminaRegenDelayMs { get; }

        public int ElementCount => _elementIds.Length;

        /// <summary>The mitigation constant C of step 3 for an attacker of this rank.</summary>
        public int MitigationConstant(Rank attackerRank) => _mitigationByRank[(int)attackerRank];

        public int RankFactorPercent(Rank attacker, Rank defender) => _rankFactors[(int)attacker * Ranks.Count + (int)defender];

        public bool TryGetElement(string id, out int element)
        {
            for (int i = 0; i < _elementIds.Length; i++)
            {
                if (string.Equals(_elementIds[i], id, StringComparison.Ordinal))
                {
                    element = i;
                    return true;
                }
            }
            element = -1;
            return false;
        }

        /// <summary>The ElementCaps index of an element for <see cref="DamageInput.Element"/>; <see cref="DamageCalc.NoElement"/> for none.</summary>
        public int ElementIndex(Element element) => (uint)element < (uint)_indexByElement.Length
            ? _indexByElement[(int)element]
            : throw new ArgumentOutOfRangeException(nameof(element));

        public string ElementId(int element) => _elementIds[element];

        public int ElementResistStat(int element) => _elementResistStat[element];

        public int ElementResistMinPercent(int element) => _elementResistMin[element];

        public int ElementResistMaxPercent(int element) => _elementResistMax[element];

        /// <summary>
        /// Builds the config from tables that already passed <c>TableValidator</c>, adding one error per broken
        /// cross-table rule: required stats exist, every rank pair appears exactly once in RankFactor and lies in the
        /// DamageConstants bounds, element resist stats exist, one ElementCaps row per <see cref="Element"/>, the
    /// <see cref="AttributeFormula"/> rules of <see cref="AttributeFormulaRules.TryBuild"/>. Returns null when anything is wrong.
        /// </summary>
        public static CombatConfig? TryBuild(ConfigSet set, List<ValidationError> errors)
        {
            int errorCount = errors.Count;
            var statRows = Rows<StatDef>(set, errors);
            var damage = Single<DamageConstants>(set, errors);
            var rankRows = Rows<RankFactor>(set, errors);
            var elementRows = Rows<ElementCaps>(set, errors);
            var starting = Single<StartingValuesM1>(set, errors);
            var regen = Single<ResourceRegen>(set, errors);
            if (statRows == null || damage == null || rankRows == null || elementRows == null || starting == null || regen == null)
                return null;

            StatRules stats;
            try
            {
                stats = new StatRules(statRows);
            }
            catch (ArgumentException e)
            {
                errors.Add(new ValidationError(ValidationCodes.RuleBroken, Name<StatDef>(), null, null, e.Message));
                return null;
            }

            // The own-stats message carries every stat (spec 9.4, WP-M2-2); its guard bounds the count.
            if (statRows.Count > Shared.Protocol.StatsCodec.MaxStatCount)
                errors.Add(new ValidationError(ValidationCodes.RuleBroken, Name<StatDef>(), null, null,
                    $"{statRows.Count} stats; the OwnStats message carries at most {Shared.Protocol.StatsCodec.MaxStatCount}"));

            foreach (var id in StatIds.Required)
            {
                if (!stats.TryGetId(id, out _))
                    errors.Add(new ValidationError(ValidationCodes.BrokenRef, Name<StatDef>(), null, null,
                        $"stat '{id}' is required by DamageCalc or ResourceMeters"));
            }

            // Repeats the row rule: DamageCalc divides by these and relies on the upper bound for exact math.
            var constants = damage.MitigationConstantByRank;
            if (constants == null || constants.Length != Ranks.Count
                || Array.Exists(constants, c => c < 1 || c > DamageConstants.MaxMitigationConstant))
                errors.Add(new ValidationError(ValidationCodes.RuleBroken, Name<DamageConstants>(), 0,
                    nameof(DamageConstants.MitigationConstantByRank),
                    $"must list one value in [1, {DamageConstants.MaxMitigationConstant}] per rank ({Ranks.Count})"));

            var rankFactors = new int[Ranks.Count * Ranks.Count];
            var seen = new int[Ranks.Count * Ranks.Count];
            for (int row = 0; row < rankRows.Count; row++)
            {
                var factor = rankRows[row];
                if ((uint)factor.Attacker >= Ranks.Count || (uint)factor.Defender >= Ranks.Count)
                    continue; // reported by the row validator
                int slot = (int)factor.Attacker * Ranks.Count + (int)factor.Defender;
                if (seen[slot]++ > 0)
                    errors.Add(new ValidationError(ValidationCodes.DuplicateId, Name<RankFactor>(), row, null,
                        $"pair {factor.Attacker} vs {factor.Defender} appears more than once"));
                if (factor.FactorPercent < damage.RankFactorMinPercent || factor.FactorPercent > damage.RankFactorMaxPercent)
                    errors.Add(new ValidationError(ValidationCodes.RuleBroken, Name<RankFactor>(), row, nameof(RankFactor.FactorPercent),
                        $"{factor.FactorPercent} is outside the DamageConstants bounds " +
                        $"[{damage.RankFactorMinPercent}, {damage.RankFactorMaxPercent}]"));
                rankFactors[slot] = factor.FactorPercent;
            }
            for (int slot = 0; slot < seen.Length; slot++)
            {
                if (seen[slot] == 0)
                    errors.Add(new ValidationError(ValidationCodes.RuleBroken, Name<RankFactor>(), null, null,
                        $"pair {(Rank)(slot / Ranks.Count)} vs {(Rank)(slot % Ranks.Count)} is missing"));
            }

            for (int row = 0; row < elementRows.Count; row++)
            {
                if (!stats.TryGetId(elementRows[row].ResistStat, out _))
                    errors.Add(new ValidationError(ValidationCodes.BrokenRef, Name<ElementCaps>(), row, nameof(ElementCaps.ResistStat),
                        $"'{elementRows[row].ResistStat}' does not exist in StatDef"));
            }
            // R-3.4-07: exactly one row per element (the row validator already refused ids that are not elements).
            var elementSeen = new int[Elements.Count + 1];
            for (int row = 0; row < elementRows.Count; row++)
            {
                if (Shared.Data.Tables.Elements.TryParse(elementRows[row].Id, out var element))
                    elementSeen[(int)element]++;
            }
            for (int value = 1; value <= Elements.Count; value++)
            {
                if (elementSeen[value] != 1)
                    errors.Add(new ValidationError(ValidationCodes.RuleBroken, Name<ElementCaps>(), null, null,
                        $"element '{ConfigTableReader.CamelCase(((Element)value).ToString())}' needs exactly one row (R-3.4-07)"));
            }

            // Spec 3.4 "M2 primary attributes": the attribute stats exist and no formula row feeds an attribute.
            var formulaRows = set.TryGet(typeof(AttributeFormula), out _) ? Rows<AttributeFormula>(set, errors)! : new List<AttributeFormula>();
            var attributes = AttributeFormulaRules.TryBuild(stats, formulaRows, errors);

            if (errors.Count > errorCount || attributes == null)
                return null;
            return new CombatConfig(stats, attributes, damage, rankFactors, elementRows, starting, regen);
        }

        private static string Name<T>() => TableInfo.For(typeof(T)).Name;

        private static List<T>? Rows<T>(ConfigSet set, List<ValidationError> errors)
        {
            if (!set.TryGet(typeof(T), out var table))
            {
                errors.Add(new ValidationError(ValidationCodes.MissingTable, Name<T>(), null, null, "needed by CombatConfig"));
                return null;
            }
            var rows = new List<T>(table.Rows.Count);
            foreach (var row in table.Rows)
                rows.Add((T)row);
            return rows;
        }

        private static T? Single<T>(ConfigSet set, List<ValidationError> errors) where T : class
        {
            var rows = Rows<T>(set, errors);
            if (rows == null)
                return null;
            if (rows.Count != 1)
            {
                errors.Add(new ValidationError(ValidationCodes.SingletonCount, Name<T>(), null, null, "needed by CombatConfig as one row"));
                return null;
            }
            return rows[0];
        }
    }

    /// <summary>Stat indices for the ids in <see cref="StatIds"/>, resolved once.</summary>
    public sealed class CombatStatIds
    {
        internal CombatStatIds(StatRules stats)
        {
            Atk = stats.IdFor(StatIds.Atk);
            Matk = stats.IdFor(StatIds.Matk);
            Def = stats.IdFor(StatIds.Def);
            Mdef = stats.IdFor(StatIds.Mdef);
            PhysPenPercent = stats.IdFor(StatIds.PhysPenPercent);
            PhysPenFlat = stats.IdFor(StatIds.PhysPenFlat);
            MagicPenPercent = stats.IdFor(StatIds.MagicPenPercent);
            MagicPenFlat = stats.IdFor(StatIds.MagicPenFlat);
            CritRate = stats.IdFor(StatIds.CritRate);
            CritDamage = stats.IdFor(StatIds.CritDamage);
            MaxHp = stats.IdFor(StatIds.MaxHp);
            MaxMp = stats.IdFor(StatIds.MaxMp);
            MaxStamina = stats.IdFor(StatIds.MaxStamina);
            HpRegen = stats.IdFor(StatIds.HpRegen);
            MpRegen = stats.IdFor(StatIds.MpRegen);
            StaminaRegen = stats.IdFor(StatIds.StaminaRegen);
            DamageDealtPercent = stats.IdFor(StatIds.DamageDealtPercent);
            DamageTakenPercent = stats.IdFor(StatIds.DamageTakenPercent);
            FlinchResist = stats.IdFor(StatIds.FlinchResist);
        }

        public int Atk { get; }
        public int Matk { get; }
        public int Def { get; }
        public int Mdef { get; }
        public int PhysPenPercent { get; }
        public int PhysPenFlat { get; }
        public int MagicPenPercent { get; }
        public int MagicPenFlat { get; }
        public int CritRate { get; }
        public int CritDamage { get; }
        public int MaxHp { get; }
        public int MaxMp { get; }
        public int MaxStamina { get; }
        public int HpRegen { get; }
        public int MpRegen { get; }
        public int StaminaRegen { get; }
        public int DamageDealtPercent { get; }
        public int DamageTakenPercent { get; }
        public int FlinchResist { get; }
    }
}
