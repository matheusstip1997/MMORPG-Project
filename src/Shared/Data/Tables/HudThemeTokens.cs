using System;
using System.Globalization;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Colors and font sizes of the HUD (spec 9.7 <c>HudThemeTokens</c>). Client-only. Colors are <c>#RRGGBB</c> or
    /// <c>#RRGGBBAA</c>. The telegraph colors pair with an icon shape per parry color (spec 9.8), never color alone.
    /// </summary>
    [Table("HudThemeTokens", 1)]
    [Singleton]
    public sealed class HudThemeTokens : IValidatableRow
    {
        public string Panel { get; set; } = "";
        public string Border { get; set; } = "";
        public string Text { get; set; } = "";
        public string Accent { get; set; } = "";
        public string Hp { get; set; } = "";
        public string Mp { get; set; } = "";
        public string Stamina { get; set; } = "";
        public string Toxicity { get; set; } = "";
        public string Satiety { get; set; } = "";
        public string Parry { get; set; } = "";
        public string Dodge { get; set; } = "";
        public string GaugeMinus { get; set; } = "";
        public string GaugePlus { get; set; } = "";
        public string BuffBorder { get; set; } = "";
        public string DebuffBorder { get; set; } = "";

        /// <summary>Greyed, locked elements (the M1 class gauge, the unassigned slot).</summary>
        public string Locked { get; set; } = "";

        public string DamageText { get; set; } = "";
        public string CritText { get; set; } = "";
        public string HealText { get; set; } = "";

        public string TelegraphRed { get; set; } = "";
        public string TelegraphYellow { get; set; } = "";
        public string TelegraphBlue { get; set; } = "";
        public string TelegraphGreen { get; set; } = "";

        /// <summary>Area of an uncolored monster attack (a parry only reduces it, R-3.3-04); it has no icon.</summary>
        public string TelegraphNone { get; set; } = "";

        /// <summary>
        /// The stagger marker on a monster's name plate (R-3.3-01 poise break, spec 9.3 <c>stagger_marker</c>): its icon,
        /// border and text. The marker pairs it with a burst shape and the "Staggered" text (spec 9.8).
        /// </summary>
        public string Staggered { get; set; } = "";

        /// <summary>Spec 9.8: text at least 12 px in the reference resolution.</summary>
        [Range(12, 48)] public int FontSizeBody { get; set; }

        /// <summary>Spec 9.7: 11 for slot labels.</summary>
        [Range(11, 48)] public int FontSizeSlotLabel { get; set; }

        /// <summary>Spec 9.7: 16 for the parry and dodge stack numbers.</summary>
        [Range(12, 64)] public int FontSizeStack { get; set; }

        [Range(12, 96)] public int FontSizeDamage { get; set; }

        [Range(12, 96)] public int FontSizeCrit { get; set; }

        public void Validate(RowErrors errors)
        {
            foreach (var property in typeof(HudThemeTokens).GetProperties())
            {
                if (property.PropertyType == typeof(string) && !HudColor.TryParse((string)property.GetValue(this)!, out _))
                    errors.Add(property.Name, "must be a color #RRGGBB or #RRGGBBAA");
            }
            if (FontSizeCrit < FontSizeDamage)
                errors.Add(nameof(FontSizeCrit), "must not be smaller than FontSizeDamage (spec 9.3: crit style)");
        }
    }

    /// <summary>An RGBA color of the HUD theme, 0-255 per channel.</summary>
    public readonly struct HudColor
    {
        public HudColor(byte r, byte g, byte b, byte a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public byte A { get; }

        /// <summary>Parses <c>#RRGGBB</c> (opaque) or <c>#RRGGBBAA</c>.</summary>
        public static bool TryParse(string? text, out HudColor color)
        {
            color = default;
            if (text == null || (text.Length != 7 && text.Length != 9) || text[0] != '#')
                return false;
            if (!uint.TryParse(text.Substring(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
                return false;
            if (text.Length == 7)
                value = (value << 8) | 0xFF;
            color = new HudColor((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
            return true;
        }
    }
}
