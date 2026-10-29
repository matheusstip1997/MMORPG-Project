using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// The cap on one stat's Bystial bonuses (R-14.2-05, architecture rule 2: all bonuses capped): the sum of the placed
    /// cards' and the family set bonuses' flat and percent bonuses on <see cref="StatId"/> is clamped to
    /// -max..max. Every stat a <see cref="BystialCardDef"/> or <see cref="BystialFamilyBonus"/> names needs a row.
    /// Placeholders.
    /// </summary>
    [Table("BystialStatCap", 1)]
    public sealed class BystialStatCap : IValidatableRow
    {
        [TableId, Ref(typeof(StatDef))] public string StatId { get; set; } = string.Empty;

        [Range(0, 1_000_000)] public int MaxFlat { get; set; }

        [Range(0, 1_000)] public int MaxPercent { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(StatId))
                errors.Add(nameof(StatId), "must not be empty");
            if (MaxFlat == 0 && MaxPercent == 0)
                errors.Add(nameof(MaxFlat), "flat and percent caps are both 0: the stat could get no bonus");
        }
    }

    /// <summary>
    /// The cap on one effect kind's Bystial bonuses (R-14.2-05, rule 2), in whole percents: the sum per element (element
    /// resistance), per family (damage and exp against a family) or overall (build-up resistance) is clamped to
    /// 0..<see cref="MaxValue"/>. One row per kind an effect uses. Placeholders.
    /// </summary>
    [Table("BystialEffectCap", 1)]
    public sealed class BystialEffectCap : IValidatableRow, ISetValidatableRow
    {
        public BystialEffectKind Kind { get; set; }

        [Range(1, 1_000)] public int MaxValue { get; set; }

        public void Validate(RowErrors errors)
        {
            if (Kind == BystialEffectKind.None || !Enum.IsDefined(typeof(BystialEffectKind), Kind))
                errors.Add(nameof(Kind), $"{(int)Kind} is not an effect kind");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<BystialEffectCap>(set);
            if (rows.FindIndex(row => row.Kind == Kind) != rows.IndexOf(this))
                errors.Add(nameof(Kind), $"{Kind} has more than one row");
        }
    }

    /// <summary>
    /// How each monster type's card shows in the book window (spec 14.2, 9.13; WP-M2-8). Client-only data, one row per
    /// monster with <see cref="BystialCardDef"/> rows (that table's rule). PLACEHOLDERS: no card art yet
    /// (<see cref="ArtAddress"/> empty), the window draws <see cref="PlaceholderShape"/> in <see cref="PlaceholderColor"/>;
    /// point a row at an Addressables texture to use real art, no code change.
    /// </summary>
    [Table("BystialCardArt", 1)]
    public sealed class BystialCardArt : IValidatableRow
    {
        /// <summary>The monster id.</summary>
        [TableId, Ref(typeof(MonsterDef))] public string Id { get; set; } = string.Empty;

        /// <summary>Addressables address of the card texture; empty while it is a placeholder.</summary>
        public string ArtAddress { get; set; } = string.Empty;

        public IconShape PlaceholderShape { get; set; }

        /// <summary><c>#RRGGBB</c> or <c>#RRGGBBAA</c>, like <see cref="HudThemeTokens"/>.</summary>
        public string PlaceholderColor { get; set; } = string.Empty;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (ArtAddress.Length != 0 && (ArtAddress.Trim() != ArtAddress || ArtAddress.StartsWith("/", StringComparison.Ordinal)))
                errors.Add(nameof(ArtAddress), "must be empty or an Addressables address without surrounding spaces or a leading /");
            if (!Enum.IsDefined(typeof(IconShape), PlaceholderShape))
                errors.Add(nameof(PlaceholderShape), $"{(int)PlaceholderShape} is not an icon shape");
            if (!HudColor.TryParse(PlaceholderColor, out _))
                errors.Add(nameof(PlaceholderColor), $"'{PlaceholderColor}' is not #RRGGBB or #RRGGBBAA");
        }
    }
}
