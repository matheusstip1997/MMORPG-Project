using System;
using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// A family set bonus (R-14.2-05; user decision 2026-10-27: at 2 and 4 cards placed, placeholders): while at least
    /// <see cref="CardsPlaced"/> placed cards belong to <see cref="Family"/>, the book adds this row's stat bonus and/or
    /// effect. Thresholds add up: 4 cards give the 2-card and the 4-card rows (WP-M2-8 reading of the decision). An effect
    /// against a family (damage, exp) applies against this row's family. Capped like card bonuses (rule 2).
    /// </summary>
    [Table("BystialFamilyBonus", 1)]
    public sealed class BystialFamilyBonus : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        public MonsterFamily Family { get; set; }

        /// <summary>Cards of the family placed for the bonus to apply.</summary>
        [Range(2, BookLimits.MaxSlots)] public int CardsPlaced { get; set; }

        /// <summary>The stat bonus: a <see cref="StatDef"/> row, or empty for an effect-only bonus.</summary>
        [Ref(typeof(StatDef), Optional = true)] public string StatId { get; set; } = string.Empty;

        [Range(-1_000_000, 1_000_000)] public int Flat { get; set; }

        [Range(-1_000, 1_000)] public int Percent { get; set; }

        public BystialEffectKind EffectKind { get; set; }

        [Optional] public Element EffectElement { get; set; }

        [Range(0, 1_000)] public int EffectValue { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Family == MonsterFamily.None || !Enum.IsDefined(typeof(MonsterFamily), Family))
                errors.Add(nameof(Family), $"{(int)Family} is not a monster family");
            if (StatId.Length == 0 && (Flat != 0 || Percent != 0))
                errors.Add(nameof(StatId), "a flat or percent bonus names its stat");
            if (StatId.Length > 0 && Flat == 0 && Percent == 0)
                errors.Add(nameof(Flat), "flat and percent are both 0 for a stat bonus");
            if (StatId.Length == 0 && EffectKind == BystialEffectKind.None)
                errors.Add(nameof(EffectKind), "a set bonus gives a stat bonus, an effect, or both");
            BystialBonusRules.CheckEffect(EffectKind, EffectElement, EffectValue, errors);
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            foreach (var other in NetIds.Rows<BystialFamilyBonus>(set))
            {
                if (ReferenceEquals(other, this))
                    break;
                if (other.Family == Family && other.CardsPlaced == CardsPlaced)
                {
                    errors.Add(nameof(CardsPlaced), $"{Family} already has a {CardsPlaced}-card bonus ('{other.Id}')");
                    break;
                }
            }
            int slots = BystialSlots.MaxOf(set);
            if (set.TryGet(typeof(BystialSlots), out _) && CardsPlaced > slots)
                errors.Add(nameof(CardsPlaced), $"no rank has {CardsPlaced} slots (BystialSlots: at most {slots})");
            BystialBonusRules.CheckCaps(set, StatId, EffectKind, errors);
        }
    }
}
