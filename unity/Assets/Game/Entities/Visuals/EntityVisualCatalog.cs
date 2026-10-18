#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>The <see cref="EntityVisualDef"/> rows by id, as loaded and validated by <see cref="ClientConfig"/>.</summary>
    public sealed class EntityVisualCatalog
    {
        private readonly Dictionary<string, EntityVisualDef> _rows = new Dictionary<string, EntityVisualDef>(StringComparer.Ordinal);

        public EntityVisualCatalog(IEnumerable<EntityVisualDef> rows)
        {
            foreach (var row in rows)
                _rows[row.Id] = row;
        }

        public static EntityVisualCatalog From(ConfigSet set)
        {
            var rows = new List<EntityVisualDef>();
            if (set.TryGet(typeof(EntityVisualDef), out var table))
            {
                foreach (var row in table.Rows)
                    rows.Add((EntityVisualDef)row);
            }
            return new EntityVisualCatalog(rows);
        }

        public int Count => _rows.Count;

        /// <summary>False for an empty or unknown id: the view shows the capsule.</summary>
        public bool TryGet(string? id, out EntityVisualDef def)
        {
            if (!string.IsNullOrEmpty(id) && _rows.TryGetValue(id!, out var row))
            {
                def = row;
                return true;
            }
            def = null!;
            return false;
        }

        /// <summary>Every distinct prefab address the rows use (to preload them).</summary>
        public List<string> Addresses()
        {
            var addresses = new List<string>();
            foreach (var row in _rows.Values)
            {
                foreach (var address in new[] { row.PrefabAddress, row.WeaponAddress, row.OffHandAddress })
                {
                    if (address.Length > 0 && !addresses.Contains(address))
                        addresses.Add(address);
                }
            }
            return addresses;
        }
    }
}
