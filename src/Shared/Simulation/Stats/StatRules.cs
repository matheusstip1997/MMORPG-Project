using System;
using System.Collections.Generic;
using Shared.Data.Tables;

namespace Shared.Simulation.Stats
{
    /// <summary>
    /// The stat list from <see cref="StatDef"/>, built once when config loads and shared by every
    /// <see cref="StatBlock"/>. A stat id is its row index. Immutable.
    /// </summary>
    public sealed class StatRules
    {
        private readonly string[] _ids;
        private readonly int[] _min;
        private readonly int[] _max;
        private readonly Dictionary<string, int> _index;

        /// <summary>Rows must be valid (<c>TableValidator</c>): unique ids, min not above max.</summary>
        public StatRules(IReadOnlyList<StatDef> rows)
        {
            if (rows == null)
                throw new ArgumentNullException(nameof(rows));
            _ids = new string[rows.Count];
            _min = new int[rows.Count];
            _max = new int[rows.Count];
            _index = new Dictionary<string, int>(rows.Count, StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Min > row.Max)
                    throw new ArgumentException($"stat {row.Id}: min {row.Min} is above max {row.Max}", nameof(rows));
                if (_index.ContainsKey(row.Id))
                    throw new ArgumentException($"stat {row.Id} appears twice", nameof(rows));
                _ids[i] = row.Id;
                _min[i] = row.Min;
                _max[i] = row.Max;
                _index.Add(row.Id, i);
            }
        }

        public int Count => _ids.Length;

        public string IdOf(int statId) => _ids[statId];

        public int Min(int statId) => _min[statId];

        public int Max(int statId) => _max[statId];

        public bool TryGetId(string id, out int statId) => _index.TryGetValue(id, out statId);

        /// <summary>For lookups of stats that must exist (validated at load).</summary>
        public int IdFor(string id) =>
            _index.TryGetValue(id, out var statId) ? statId : throw new KeyNotFoundException($"stat {id} is not in StatDef");

        public int Clamp(int statId, long value)
        {
            if (value < _min[statId]) return _min[statId];
            if (value > _max[statId]) return _max[statId];
            return (int)value;
        }
    }
}
