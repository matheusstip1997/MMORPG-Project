#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Game.UI.Config
{
    /// <summary>
    /// The HUD by level (spec 9.12, R-12.3-02; WP-M2-10): the level each HUD element appears from, the lowest
    /// <c>UnlockSchedule</c> row that lists it in <c>hudElements</c>. An element no row lists is always visible. The schedule
    /// is the only place an element's level lives: a package whose element waits for a level adds its own row.
    /// </summary>
    public sealed class HudUnlocks
    {
        private readonly Dictionary<string, int> _levels = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>No element waits for a level.</summary>
        public static readonly HudUnlocks None = new HudUnlocks(Array.Empty<UnlockSchedule>());

        public HudUnlocks(IEnumerable<UnlockSchedule> rows)
        {
            foreach (var row in rows)
            {
                foreach (var element in row.HudElements)
                {
                    if (!_levels.TryGetValue(element, out int level) || row.Level < level)
                        _levels[element] = row.Level;
                }
            }
        }

        public static HudUnlocks From(ConfigSet set)
        {
            var rows = new List<UnlockSchedule>();
            if (set.TryGet(typeof(UnlockSchedule), out var table))
            {
                foreach (var row in table.Rows)
                    rows.Add((UnlockSchedule)row);
            }
            return new HudUnlocks(rows);
        }

        /// <summary>The level <paramref name="elementId"/> appears from; 0 when no row lists it.</summary>
        public int LevelOf(string elementId) => _levels.TryGetValue(elementId, out int level) ? level : 0;

        /// <summary>Whether <paramref name="elementId"/> shows at <paramref name="level"/> (0 before the first level).</summary>
        public bool Shows(string elementId, int level) => !_levels.TryGetValue(elementId, out int needed) || level >= needed;
    }
}
