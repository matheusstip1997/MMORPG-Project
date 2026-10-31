using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// A component of a crafted item's quality (R-5.3-02: "material numbers + action numbers + skill-tree bonuses +
    /// minigame result + server randomness + a small bounded random term"). Config enum (camelCase names); append only: a
    /// new random term is one more member and one more <see cref="QualityCaps"/> row (spec 5.3).
    /// </summary>
    public enum QualityComponent : byte
    {
        None = 0,
        Materials = 1,
        Actions = 2,
        JobTree = 3,
        Minigame = 4,
        ServerRoll = 5,
        /// <summary>The small bounded random term of R-5.3-02: signed, from minus its cap to its cap.</summary>
        SmallTerm = 6,
    }

    /// <summary>
    /// The bound of one <see cref="QualityComponent"/> (R-5.3-02 "the minigame's contribution is capped"; spec 5.3 M3
    /// decision 7): every component of <c>QualityCalc</c> is clamped to 0..<see cref="Cap"/> (the small term to
    /// -cap..cap). The id is the component's camelCase name; one row per component (<see cref="ForgeCoverage"/>). The
    /// server roll and the small term are drawn up to their cap. Every number is a placeholder. Catalog 8.5.
    /// </summary>
    [Table("QualityCaps", 1)]
    public sealed class QualityCaps : IValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        [Range(1, 65_535)] public int Cap { get; set; }

        public void Validate(RowErrors errors)
        {
            if (!TryComponent(Id, out _))
                errors.Add(nameof(Id), $"'{Id}' is not a quality component (materials, actions, jobTree, minigame, serverRoll, smallTerm)");
        }

        public static bool TryComponent(string id, out QualityComponent component)
        {
            component = QualityComponent.None;
            if (string.IsNullOrEmpty(id) || !char.IsLower(id[0]))
                return false;
            if (!Enum.TryParse(char.ToUpperInvariant(id[0]) + id.Substring(1), false, out component))
                return false;
            return component != QualityComponent.None && Enum.IsDefined(typeof(QualityComponent), component);
        }
    }

    /// <summary>
    /// Quality to rarity (R-5.3-02 "mapped to rarity by config thresholds"; spec 5.3 M3 decision 7): a crafted item gets
    /// the rarity of the last row whose <see cref="MinQuality"/> its quality reaches, and that row's
    /// <see cref="StatBonusPercent"/> on its material stats (R-5.3-02 "item stats = materials used + a quality bonus").
    /// Rows ascend in file order from 0, one per <see cref="RarityDef"/> tier, common up to legendary (the forge is the only
    /// source of epic and legendary, spec 4.1). Every number is a placeholder. Catalog 8.5.
    /// </summary>
    [Table("QualityThresholds", 1)]
    public sealed class QualityThresholds : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        [Range(0, 65_535)] public int MinQuality { get; set; }

        [Ref(typeof(RarityDef))] public string RarityId { get; set; } = string.Empty;

        [Range(0, 1_000)] public int StatBonusPercent { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }

        /// <summary>The first row starts at 0; quality, rarity tier and bonus ascend; each rarity once.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            QualityThresholds? previous = null;
            foreach (var other in NetIds.Rows<QualityThresholds>(set))
            {
                if (ReferenceEquals(other, this))
                    break;
                previous = other;
                if (other.RarityId == RarityId)
                    errors.Add(nameof(RarityId), $"'{RarityId}' is also the rarity of '{other.Id}'");
            }
            if (previous == null)
            {
                if (MinQuality != 0)
                    errors.Add(nameof(MinQuality), "the first threshold starts at 0");
                return;
            }
            if (MinQuality <= previous.MinQuality)
                errors.Add(nameof(MinQuality), $"{MinQuality} must be above '{previous.Id}' ({previous.MinQuality})");
            if (StatBonusPercent < previous.StatBonusPercent)
                errors.Add(nameof(StatBonusPercent), $"must not be below '{previous.Id}' ({previous.StatBonusPercent})");
            var rarity = NetIds.Find<RarityDef>(set, RarityId, row => row.Id);
            var before = NetIds.Find<RarityDef>(set, previous.RarityId, row => row.Id);
            if (rarity != null && before != null && rarity.Tier <= before.Tier)
                errors.Add(nameof(RarityId), $"'{RarityId}' must be a higher rarity than '{previous.RarityId}'");
        }
    }

    /// <summary>
    /// Tier conversion odds (R-5.2-04; spec 5.3 M3 decision 7): converting into <see cref="ToTier"/> succeeds with
    /// <see cref="SuccessPercent"/> (never 100 %, falling as the tier rises), and a character may try it at most
    /// <see cref="DailyCap"/> times per UTC day (both outcomes count; arbitrage safety, spec 5.2). Every number is a
    /// placeholder. Catalog 8.5.
    /// </summary>
    [Table("ConversionOdds", 1)]
    public sealed class ConversionOdds : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>The output tier (T+1); tier 1 is never a conversion target.</summary>
        [Range(2, MaterialTierDef.MaxTier)] public int ToTier { get; set; }

        [Range(1, 99)] public int SuccessPercent { get; set; }

        [Range(1, 10_000)] public int DailyCap { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }

        /// <summary>One row per tier, a tier on the ladder, success falling as the tier rises (R-5.2-04).</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (ForgeSettings.Find(set) is { } settings && SuccessPercent > settings.MaxConversionPercent)
                errors.Add(nameof(SuccessPercent), $"{SuccessPercent} is above ForgeSettings.maxConversionPercent ({settings.MaxConversionPercent})");
            if (set.TryGet(typeof(MaterialTierDef), out _) && MaterialTierDef.ByTier(set, ToTier) == null)
                errors.Add(nameof(ToTier), $"{ToTier} is not a MaterialTierDef tier");
            foreach (var other in NetIds.Rows<ConversionOdds>(set))
            {
                if (ReferenceEquals(other, this))
                    continue;
                if (other.ToTier == ToTier)
                    errors.Add(nameof(ToTier), $"{ToTier} is also the tier of '{other.Id}'");
                else if (other.ToTier < ToTier && other.SuccessPercent < SuccessPercent)
                    errors.Add(nameof(SuccessPercent), $"{SuccessPercent} must not be above tier {other.ToTier}'s {other.SuccessPercent} (R-5.2-04)");
            }
        }

        public static ConversionOdds? ForTier(ConfigSet set, int toTier)
        {
            foreach (var row in NetIds.Rows<ConversionOdds>(set))
            {
                if (row.ToTier == toTier)
                    return row;
            }
            return null;
        }
    }
}
