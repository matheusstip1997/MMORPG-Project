#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// Where a character's seen tutorial prompts are kept (R-12.3-03: a prompt shows once per character). The client keeps
    /// them locally (user-approved WP-M2-10 plan): prompts are presentation only, so a new machine shows them again.
    /// </summary>
    public interface ITutorialSeenStore
    {
        bool IsSeen(string promptRowId);

        /// <summary>Remembers the prompt at once (written before it is shown, so a crash or a relog never shows it twice).</summary>
        void MarkSeen(string promptRowId);
    }

    /// <summary>A store that forgets (tests, and a world client that has no character yet).</summary>
    public sealed class MemoryTutorialSeenStore : ITutorialSeenStore
    {
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        public bool IsSeen(string promptRowId) => _seen.Contains(promptRowId);

        public void MarkSeen(string promptRowId) => _seen.Add(promptRowId);
    }

    /// <summary>
    /// The tutorial's prompt schedule on the client (spec 12.3 R-12.3-02 and R-12.3-03; WP-M2-10): from the
    /// <see cref="UnlockSchedule"/> rows, the own level and the trigger facts of each frame, it queues the prompts that became
    /// due and are not seen yet, in schedule order (level, then row order), and shows one at a time. Engine-free; time
    /// is the caller's clock in ms.
    /// <para>
    /// A row is due once the level reaches it and its trigger happens at or after that: <c>levelReached</c> at once,
    /// <c>lootInReach</c>, <c>outpostInReach</c> and <c>vendorInReach</c> while that is true in a frame, <c>itemSold</c> after a
    /// committed sale. A due row stays queued even if its trigger ends while another prompt shows.
    /// </para>
    /// </summary>
    public sealed class ClientTutorial
    {
        private readonly List<UnlockSchedule> _rows;
        private readonly ITutorialSeenStore _seen;
        private readonly int _showMs;
        private readonly List<UnlockSchedule> _queue = new List<UnlockSchedule>();
        private bool _sold;
        private double _currentUntilMs;

        /// <param name="rows">The schedule rows with a prompt, in table order.</param>
        /// <param name="showMs"><c>TutorialSettings.promptShowMs</c>: how long a prompt stays when it is not dismissed.</param>
        public ClientTutorial(IEnumerable<UnlockSchedule> rows, ITutorialSeenStore seen, int showMs)
        {
            _seen = seen ?? throw new ArgumentNullException(nameof(seen));
            _showMs = showMs;
            _rows = new List<UnlockSchedule>();
            foreach (var row in rows)
            {
                if (!string.IsNullOrEmpty(row.Prompt))
                    _rows.Add(row);
            }
            // Level order, table order within a level (a stable sort).
            var ordered = new List<KeyValuePair<int, UnlockSchedule>>();
            for (int i = 0; i < _rows.Count; i++)
                ordered.Add(new KeyValuePair<int, UnlockSchedule>(i, _rows[i]));
            ordered.Sort((a, b) => a.Value.Level != b.Value.Level ? a.Value.Level.CompareTo(b.Value.Level) : a.Key.CompareTo(b.Key));
            _rows.Clear();
            foreach (var pair in ordered)
                _rows.Add(pair.Value);
        }

        /// <summary>The schedule of a loaded config set, or none.</summary>
        public static ClientTutorial From(ConfigSet config, ITutorialSeenStore seen)
        {
            var rows = new List<UnlockSchedule>();
            if (config.TryGet(typeof(UnlockSchedule), out var table))
            {
                foreach (var row in table.Rows)
                    rows.Add((UnlockSchedule)row);
            }
            return new ClientTutorial(rows, seen, TutorialSettings.Find(config)?.PromptShowMs ?? 0);
        }

        /// <summary>The own level (0 before the first <c>ProgressionState</c>: nothing is due then).</summary>
        public int Level { get; private set; }

        /// <summary>The prompt shown now, or null.</summary>
        public UnlockSchedule? Current { get; private set; }

        public void SetLevel(int level) => Level = level;

        /// <summary>A sale committed (<c>SellResult</c> without a reason): due <c>itemSold</c> rows queue at the next frame.</summary>
        public void OnItemSold() => _sold = true;

        /// <summary>The player dismissed the current prompt; the next queued one follows at the next frame.</summary>
        public void Dismiss() => Current = null;

        /// <summary>Clears the queue and the current prompt (a disconnect); seen prompts stay seen.</summary>
        public void Reset()
        {
            _queue.Clear();
            Current = null;
            _sold = false;
            Level = 0;
        }

        /// <summary>
        /// Once per frame with the facts of the frame: queues the rows that became due, ends the current prompt after its
        /// time, and starts the next queued one. True when <see cref="Current"/> changed.
        /// </summary>
        public bool Frame(double nowMs, bool lootInReach, bool outpostInReach, bool vendorInReach)
        {
            if (Level > 0)
            {
                foreach (var row in _rows)
                {
                    if (row.Level > Level || _queue.Contains(row) || ReferenceEquals(row, Current) || _seen.IsSeen(row.Id))
                        continue;
                    if (Due(row.Trigger, lootInReach, outpostInReach, vendorInReach))
                        _queue.Add(row);
                }
            }
            _sold = false;

            var before = Current;
            if (Current != null && nowMs >= _currentUntilMs)
                Current = null;
            if (Current == null && _queue.Count > 0)
            {
                Current = _queue[0];
                _queue.RemoveAt(0);
                _seen.MarkSeen(Current.Id); // R-12.3-03: seen when first shown
                _currentUntilMs = nowMs + _showMs;
            }
            return !ReferenceEquals(before, Current);
        }

        private bool Due(UnlockTrigger trigger, bool lootInReach, bool outpostInReach, bool vendorInReach)
        {
            switch (trigger)
            {
                case UnlockTrigger.LevelReached:
                    return true;
                case UnlockTrigger.LootInReach:
                    return lootInReach;
                case UnlockTrigger.OutpostInReach:
                    return outpostInReach;
                case UnlockTrigger.VendorInReach:
                    return vendorInReach;
                case UnlockTrigger.ItemSold:
                    return _sold;
                default:
                    return false;
            }
        }
    }
}
