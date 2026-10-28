using System;

namespace Shared.Data.Tables
{
    /// <summary>Shape of a minimap marker's placeholder icon (drawn in code, no art). Config enum (camelCase names); append only.</summary>
    public enum MinimapShape : byte
    {
        /// <summary>An arrow pointing up at facing 0 (north), turned with the facing.</summary>
        Arrow = 0,
        Circle = 1,
        Square = 2,
        Diamond = 3,
        Triangle = 4,

        /// <summary>An unfilled circle (the camp hint).</summary>
        Ring = 5,

        /// <summary>A sack: a body with a tied neck (the death spot, later the loot bag).</summary>
        Bag = 6,
    }

    /// <summary>
    /// How each minimap marker kind looks (spec 9.3, 9.12; WP-M2-10b), like <see cref="ItemIconRegistry"/>. Client-only
    /// data, PLACEHOLDERS: no icon art yet (<see cref="IconAddress"/> empty), the minimap draws <see cref="PlaceholderShape"/>
    /// in the marker's color; point a row at an Addressables texture to use real art, no code change. Rule R-9.12-04: every
    /// row is used by a <see cref="MinimapMarkerDef"/> row.
    /// </summary>
    [Table("MinimapIconRegistry", 1)]
    public sealed class MinimapIconRegistry : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Addressables address of the icon texture; empty while it is a placeholder.</summary>
        public string IconAddress { get; set; } = string.Empty;

        public MinimapShape PlaceholderShape { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (IconAddress.Length != 0 && (IconAddress.Trim() != IconAddress || IconAddress.StartsWith("/", StringComparison.Ordinal)))
                errors.Add(nameof(IconAddress), "must be empty or an Addressables address without surrounding spaces or a leading /");
            if (!Enum.IsDefined(typeof(MinimapShape), PlaceholderShape))
                errors.Add(nameof(PlaceholderShape), $"{(int)PlaceholderShape} is not a minimap shape");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (!set.TryGet(typeof(MinimapMarkerDef), out var markers))
                return;
            foreach (var row in markers.Rows)
            {
                if (string.Equals(((MinimapMarkerDef)row).IconId, Id, StringComparison.Ordinal))
                    return;
            }
            errors.Add(nameof(Id), $"icon '{Id}' is used by no MinimapMarkerDef row");
        }
    }
}
