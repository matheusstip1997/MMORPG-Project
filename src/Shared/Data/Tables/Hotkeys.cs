using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Default key map (spec 9.7 <c>Hotkeys</c>). Client-only. One row per bindable command, with an Input System control
    /// path such as <c>&lt;Keyboard&gt;/i</c>. WP16 adds the first row, the inventory window's toggle (spec 9 assigns no
    /// key: placeholder). TODO(9.6): the movement, action and hotbar keys move here from <c>GameInput</c> with rebinding.
    /// </summary>
    [Table("Hotkeys", 1)]
    public sealed class Hotkeys : IValidatableRow
    {
        /// <summary>Opens and closes the inventory window (spec 9.13).</summary>
        public const string ToggleInventory = "toggleInventory";

        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Input System control path: <c>&lt;Device&gt;/control</c>.</summary>
        public string Binding { get; set; } = string.Empty;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (!IsControlPath(Binding))
                errors.Add(nameof(Binding), $"'{Binding}' is not an Input System control path like <Keyboard>/i");
        }

        /// <summary>The shape <c>&lt;Device&gt;/control</c>, without spaces.</summary>
        public static bool IsControlPath(string path)
        {
            int close = path.IndexOf(">/", StringComparison.Ordinal);
            return path.Length > 4 && path[0] == '<' && close > 1 && close + 2 < path.Length && path.IndexOf(' ') < 0;
        }

        public static Hotkeys? Find(ConfigSet set, string id)
        {
            if (!set.TryGet(typeof(Hotkeys), out var table))
                return null;
            foreach (var row in table.Rows)
            {
                if (((Hotkeys)row).Id == id)
                    return (Hotkeys)row;
            }
            return null;
        }
    }
}
