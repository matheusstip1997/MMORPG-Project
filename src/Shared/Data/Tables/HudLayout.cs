using System;
using System.Collections.Generic;

namespace Shared.Data.Tables
{
    /// <summary>
    /// One element of the main HUD (spec 9.7 <c>HudLayout</c>, element ids of spec 9.3). Client-only. Coordinates are in the
    /// reference resolution of <see cref="HudSettings"/>, with the same rules as <see cref="LoginLayout"/>: the offset points
    /// into the screen from the anchor, and on a centered axis it places the element's center (x right, y down).
    /// </summary>
    [Table("HudLayout", 1)]
    public sealed class HudLayout : IValidatableRow, ISetValidatableRow
    {
        /// <summary>The spec 9.3 element id, for example <c>resource_panel</c>.</summary>
        [TableId] public string Id { get; set; } = "";

        public UiAnchor Anchor { get; set; }

        /// <summary>[x, y] from the anchor (spec 9.7).</summary>
        public int[] Offset { get; set; } = Array.Empty<int>();

        /// <summary>[width, height]; 0 means sized by content.</summary>
        public int[] Size { get; set; } = Array.Empty<int>();

        /// <summary>
        /// Hotbar elements only: the <c>ActionBindings</c> skill slot shown at each position, in reading order (spec 9.6:
        /// 1-6 are keys 1-6, 7 Q, 8 E, 9 R, 10 F, 11 Z); 0 is the unassigned slot, drawn dashed.
        /// </summary>
        [Optional] public int[] Slots { get; set; } = Array.Empty<int>();

        /// <summary>
        /// The shortcut row's arrow (spec 9.3 "Shortcut row behavior", WP-M2-11): the element can be collapsed to the arrow.
        /// The collapsed state is a local client setting (spec 9.6).
        /// </summary>
        [Optional] public bool Collapsible { get; set; }

        /// <summary>
        /// Collapsible rows only: the menu buttons, left to right after the arrow, each one of <see cref="ShortcutButtonIds"/>
        /// (spec 9.3; a button joins the list once its window exists).
        /// </summary>
        [Optional] public string[] Buttons { get; set; } = Array.Empty<string>();

        /// <summary>The menu buttons the client opens a window for (WP-M2-11; spec 9.3 CH, INV, SK, the book, JR).</summary>
        public static readonly string[] ShortcutButtonIds = { "character", "inventory", "skills", "book", "journal" };

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            UiLayoutRules.Check(Offset, Size, errors);
            foreach (int slot in Slots)
            {
                if (slot < 0 || slot > ActionBindings.MaxSkillSlots)
                    errors.Add(nameof(Slots), $"{slot} is not a skill slot (0 to {ActionBindings.MaxSkillSlots})");
            }
            if (Buttons.Length > 0 && !Collapsible)
                errors.Add(nameof(Buttons), "only a collapsible row has buttons");
            var buttons = new HashSet<string>();
            foreach (string button in Buttons)
            {
                if (Array.IndexOf(ShortcutButtonIds, button) < 0)
                    errors.Add(nameof(Buttons), $"'{button}' is not a shortcut button ({string.Join(", ", ShortcutButtonIds)})");
                else if (!buttons.Add(button))
                    errors.Add(nameof(Buttons), $"'{button}' is listed more than once");
            }
        }

        /// <summary>A skill slot is shown at most once on the whole HUD.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (Slots.Length == 0 || !set.TryGet(typeof(HudLayout), out var table))
                return;
            var seen = new HashSet<int>();
            foreach (var row in table.Rows)
            {
                var layout = (HudLayout)row;
                foreach (int slot in layout.Slots)
                {
                    if (slot == 0 || seen.Add(slot))
                        continue;
                    if (ReferenceEquals(layout, this))
                        errors.Add(nameof(Slots), $"skill slot {slot} is shown more than once");
                }
            }
        }
    }

    /// <summary>The offset and size rules <see cref="LoginLayout"/> and <see cref="HudLayout"/> share (spec 9.7).</summary>
    public static class UiLayoutRules
    {
        public const int MaxWidth = 7_680;
        public const int MaxHeight = 4_320;

        public static void Check(int[] offset, int[] size, RowErrors errors)
        {
            if (offset.Length != 2)
                errors.Add("Offset", "must be [x, y]");
            if (size.Length != 2)
                errors.Add("Size", "must be [width, height]");
            else if (size[0] < 0 || size[1] < 0 || size[0] > MaxWidth || size[1] > MaxHeight)
                errors.Add("Size", "must be between 0 and the largest reference resolution");
        }
    }
}
