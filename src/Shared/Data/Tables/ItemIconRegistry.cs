using System;

namespace Shared.Data.Tables
{
    /// <summary>Shape of an item's placeholder icon (drawn in code, no art). Config enum (camelCase names).</summary>
    public enum IconShape : byte
    {
        Circle = 0,
        Square = 1,
        Diamond = 2,
        Triangle = 3,
        Hexagon = 4,
    }

    /// <summary>
    /// How each item shows in the inventory window (spec 9.13, backlog G client part, WP16). Client-only data, one row per
    /// <see cref="ItemDef"/> (checked by <see cref="InventoryWindowSettings"/>). PLACEHOLDERS in M1: no icon art yet
    /// (<see cref="IconAddress"/> empty), the window draws <see cref="PlaceholderShape"/> in <see cref="PlaceholderColor"/>;
    /// point a row at an Addressables texture to use real art, no code change.
    /// </summary>
    [Table("ItemIconRegistry", 1)]
    public sealed class ItemIconRegistry : IValidatableRow
    {
        /// <summary>The item id.</summary>
        [TableId, Ref(typeof(ItemDef))] public string Id { get; set; } = string.Empty;

        /// <summary>Addressables address of the icon texture; empty while it is a placeholder.</summary>
        public string IconAddress { get; set; } = string.Empty;

        public IconShape PlaceholderShape { get; set; }

        /// <summary><c>#RRGGBB</c> or <c>#RRGGBBAA</c>, like <see cref="HudThemeTokens"/>.</summary>
        public string PlaceholderColor { get; set; } = string.Empty;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (IconAddress.Length != 0 && (IconAddress.Trim() != IconAddress || IconAddress.StartsWith("/", StringComparison.Ordinal)))
                errors.Add(nameof(IconAddress), "must be empty or an Addressables address without surrounding spaces or a leading /");
            if (!Enum.IsDefined(typeof(IconShape), PlaceholderShape))
                errors.Add(nameof(PlaceholderShape), $"{(int)PlaceholderShape} is not an icon shape");
            if (!HudColor.TryParse(PlaceholderColor, out _))
                errors.Add(nameof(PlaceholderColor), $"'{PlaceholderColor}' is not #RRGGBB or #RRGGBBAA");
        }
    }
}
