using System;

namespace Shared.Simulation.Stats
{
    /// <summary>
    /// The stats of one entity (spec 3.4 implementation): a base value per <see cref="StatRules"/> stat plus a list of
    /// <see cref="StatModifier"/>s by source. Final values are cached and recomputed only after a modifier is added or
    /// removed or a base value changes, the first time a value is read (or on <see cref="Refresh"/>).
    /// <para>
    /// Final value = clamp(floor((base + sum of flat) x (100 + sum of percent) / 100), StatDef min, StatDef max).
    /// The percent sum is floored at -100 so percents alone never flip a sign; flooring is toward negative infinity.
    /// </para>
    /// <para>
    /// Expiry is explicit: call <see cref="ExpireModifiers"/> with <c>IClock.UtcNowMs</c> (tick phase 6, spec 3.7)
    /// before reading stats in a tick. A modifier is active while <c>now &lt; ExpiresAtMs</c>.
    /// No allocation per call: the modifier array grows only when it is full. Not thread-safe (world thread only).
    /// </para>
    /// </summary>
    public sealed class StatBlock
    {
        public const int DefaultModifierCapacity = 16;

        private readonly StatRules _rules;
        private readonly int[] _base;
        private readonly int[] _values;
        private readonly long[] _flatSums;
        private readonly long[] _percentSums;
        private StatModifier[] _modifiers;
        private int _modifierCount;
        private long _nextExpiryMs = long.MaxValue;
        private bool _dirty = true;

        public StatBlock(StatRules rules, int modifierCapacity = DefaultModifierCapacity)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            if (modifierCapacity < 1)
                throw new ArgumentOutOfRangeException(nameof(modifierCapacity), "must be positive");
            _base = new int[rules.Count];
            _values = new int[rules.Count];
            _flatSums = new long[rules.Count];
            _percentSums = new long[rules.Count];
            _modifiers = new StatModifier[modifierCapacity];
        }

        public StatRules Rules => _rules;

        /// <summary>Changes whenever a recompute changes at least one final value (for <c>StatsChanged</c>, spec 8.1).</summary>
        public int Version { get; private set; }

        public int ModifierCount => _modifierCount;

        /// <summary>Earliest <see cref="StatModifier.ExpiresAtMs"/> of the active modifiers, or <c>long.MaxValue</c>.</summary>
        public long NextExpiryMs => _nextExpiryMs;

        /// <summary>How many times the final values were recomputed (tests check the cache).</summary>
        internal int RecomputeCount { get; private set; }

        public StatModifier ModifierAt(int index)
        {
            if ((uint)index >= (uint)_modifierCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _modifiers[index];
        }

        /// <summary>Final value of a stat.</summary>
        public int Get(int statId)
        {
            if (_dirty)
                Recompute();
            return _values[statId];
        }

        public int GetBase(int statId) => _base[statId];

        /// <summary>Sets a base value (class template, level-up). Not clamped: only the final value is.</summary>
        public void SetBase(int statId, int value)
        {
            CheckStat(statId);
            if (_base[statId] == value)
                return;
            _base[statId] = value;
            _dirty = true;
        }

        public void Add(in StatModifier modifier)
        {
            CheckStat(modifier.StatId);
            if (_modifierCount == _modifiers.Length)
                Array.Resize(ref _modifiers, _modifiers.Length * 2);
            _modifiers[_modifierCount++] = modifier;
            if (modifier.Expires && modifier.ExpiresAtMs < _nextExpiryMs)
                _nextExpiryMs = modifier.ExpiresAtMs;
            _dirty = true;
        }

        /// <summary>Removes every modifier of one source (gear unequipped, buff ended). Returns how many were removed.</summary>
        public int RemoveSource(StatSourceType sourceType, long sourceId)
        {
            int kept = 0;
            long nextExpiry = long.MaxValue;
            for (int i = 0; i < _modifierCount; i++)
            {
                var modifier = _modifiers[i];
                if (modifier.SourceType == sourceType && modifier.SourceId == sourceId)
                    continue;
                Keep(modifier, ref kept, ref nextExpiry);
            }
            return Compact(kept, nextExpiry);
        }

        /// <summary>
        /// Removes modifiers whose <see cref="StatModifier.ExpiresAtMs"/> is at or before <paramref name="nowMs"/>.
        /// Returns how many were removed; costs nothing when none is due (cached next expiry).
        /// </summary>
        public int ExpireModifiers(long nowMs)
        {
            if (nowMs < _nextExpiryMs)
                return 0;
            int kept = 0;
            long nextExpiry = long.MaxValue;
            for (int i = 0; i < _modifierCount; i++)
            {
                var modifier = _modifiers[i];
                if (modifier.Expires && modifier.ExpiresAtMs <= nowMs)
                    continue;
                Keep(modifier, ref kept, ref nextExpiry);
            }
            return Compact(kept, nextExpiry);
        }

        /// <summary>Recomputes now if anything changed. Returns true when a final value changed (emit <c>StatsChanged</c>).</summary>
        public bool Refresh()
        {
            if (!_dirty)
                return false;
            int before = Version;
            Recompute();
            return Version != before;
        }

        private void Keep(in StatModifier modifier, ref int kept, ref long nextExpiry)
        {
            _modifiers[kept++] = modifier;
            if (modifier.Expires && modifier.ExpiresAtMs < nextExpiry)
                nextExpiry = modifier.ExpiresAtMs;
        }

        private int Compact(int kept, long nextExpiry)
        {
            int removed = _modifierCount - kept;
            for (int i = kept; i < _modifierCount; i++)
                _modifiers[i] = default;
            _modifierCount = kept;
            _nextExpiryMs = nextExpiry;
            if (removed > 0)
                _dirty = true;
            return removed;
        }

        private void Recompute()
        {
            Array.Clear(_flatSums, 0, _flatSums.Length);
            Array.Clear(_percentSums, 0, _percentSums.Length);
            // Sums are order independent, so the result does not depend on the order modifiers were added.
            for (int i = 0; i < _modifierCount; i++)
            {
                _flatSums[_modifiers[i].StatId] += _modifiers[i].Flat;
                _percentSums[_modifiers[i].StatId] += _modifiers[i].Percent;
            }

            bool changed = false;
            for (int stat = 0; stat < _values.Length; stat++)
            {
                // Both factors fit in 32 bits after clamping, so the product fits in a long.
                long flat = ClampToInt(_base[stat] + _flatSums[stat]);
                long percent = Math.Max(-100L, Math.Min(int.MaxValue, _percentSums[stat]));
                int value = _rules.Clamp(stat, FloorDiv(flat * (100 + percent), 100));
                if (value != _values[stat])
                {
                    _values[stat] = value;
                    changed = true;
                }
            }
            _dirty = false;
            RecomputeCount++;
            if (changed)
                Version++;
        }

        private void CheckStat(int statId)
        {
            if ((uint)statId >= (uint)_values.Length)
                throw new ArgumentOutOfRangeException(nameof(statId), $"stat {statId} is not in StatDef ({_values.Length} stats)");
        }

        private static long ClampToInt(long value) => Math.Max(int.MinValue, Math.Min(int.MaxValue, value));

        /// <summary>Division rounding toward negative infinity (C# division truncates toward zero).</summary>
        internal static long FloorDiv(long value, long divisor)
        {
            long quotient = value / divisor;
            return (value % divisor != 0 && (value < 0) != (divisor < 0)) ? quotient - 1 : quotient;
        }
    }
}
