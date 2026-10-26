using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// The inventory window (spec 9.13, backlog G client part, WP16). Client-only display choices, placeholders in
    /// data/tables. Its place on screen is the <see cref="HudLayout"/> row <c>inventory_window</c>. The set rule checks that
    /// the data the window needs is complete: the <see cref="Hotkeys"/> row that opens it and an
    /// <see cref="ItemIconRegistry"/> row for every item.
    /// </summary>
    [Table("InventoryWindowSettings", 1)]
    [Singleton]
    public sealed class InventoryWindowSettings : ISetValidatableRow
    {
        /// <summary>Slots per grid row; the bag's slot count (<see cref="ContainerDef.Slots"/>) gives the number of rows.</summary>
        [Range(1, 20)] public int Columns { get; set; }

        /// <summary>Side of one slot square in the reference resolution (spec 9.2: 1280 x 720).</summary>
        [Range(16, 128)] public int SlotSizePx { get; set; }

        [Range(0, 32)] public int SlotGapPx { get; set; }

        /// <summary>How far the pointer moves with the button held before a press on a slot becomes a drag.</summary>
        [Range(1, 64)] public int DragThresholdPx { get; set; }

        /// <summary>
        /// A move the server has not answered within this time is dropped from the preview, which then shows the bag as
        /// last confirmed (a request lost to a full queue, or a save that never committed).
        /// </summary>
        [Range(500, 60_000)] public int MovePreviewTimeoutMs { get; set; }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (Hotkeys.Find(set, Hotkeys.ToggleInventory) == null)
                errors.Add(null, $"the window needs a Hotkeys row '{Hotkeys.ToggleInventory}'");
            if (!set.TryGet(typeof(ItemDef), out var items) || !set.TryGet(typeof(ItemIconRegistry), out var icons))
                return;
            foreach (var row in items.Rows)
            {
                string id = ((ItemDef)row).Id;
                bool found = false;
                foreach (var icon in icons.Rows)
                    found |= string.Equals(((ItemIconRegistry)icon).Id, id, StringComparison.Ordinal);
                if (!found)
                    errors.Add(null, $"item '{id}' has no ItemIconRegistry row");
            }
        }
    }
}
