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

        /// <summary>Locks the nearest monster, cycles to another, a double press clears it (R-2.2-06, <see cref="TargetLockSettings"/>).</summary>
        public const string TargetLock = "targetLock";

        /// <summary>Talks to the NPC in reach: opens the outpost's sale or the vendor (spec 4.2, 9.13; WP-M2-4).</summary>
        public const string Interact = "interact";

        /// <summary>Opens and closes the skill tree window (spec 3.5, 9.13 pattern; WP-M2-5).</summary>
        public const string ToggleSkillTree = "toggleSkillTree";
        /// <summary>Opens and closes the Bystial book window (spec 14.2, 9.13; WP-M2-8).</summary>
        public const string ToggleBook = "toggleBook";

        /// <summary>Opens and closes the journal: the quest board's run view away from an outpost (spec 9.13 M2; WP-M2-11).</summary>
        public const string ToggleJournal = "toggleJournal";
        /// <summary>Dismisses the tutorial prompt shown now (spec 12.3, R-12.3-03; WP-M2-10).</summary>
        public const string DismissPrompt = "dismissPrompt";

        /// <summary>Opens and closes the character sheet (spec 9.13 M2; WP-M2-11).</summary>
        public const string ToggleCharacter = "toggleCharacter";

        /// <summary>Dev views (backlog J, <see cref="DevTools"/>; editor and dev builds only): the world geometry.</summary>
        public const string DevWorldGeometry = "devWorldGeometry";

        /// <summary>Dev view: hit shapes, projectile paths, ground areas and body circles.</summary>
        public const string DevHitboxes = "devHitboxes";

        /// <summary>Dev view: the action timeline, predicted against the server.</summary>
        public const string DevTimeline = "devTimeline";

        /// <summary>Dev view: the server's tick timing and the last table reload.</summary>
        public const string DevTickTiming = "devTickTiming";

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
