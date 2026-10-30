#nullable enable
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>
    /// The visual id of a spawned entity from its spawn event's definition id (spec 1.7, protocol 3): a player's
    /// <see cref="ClassDef.NetId"/> names its class, a monster's <see cref="MonsterDef.NetId"/> its type, a ground drop's
    /// <see cref="ItemDef.NetId"/> its item (backlog G), an NPC's <see cref="NpcDef.NetId"/> its NPC (WP-M2-4), a resource
    /// node's <see cref="NodeDef.NetId"/> its node type (WP-M3-4, with a second look while depleted), and the row's
    /// <c>visualId</c> names the <see cref="EntityVisualDef"/> row to show.
    /// </summary>
    public sealed class DefinitionVisualIds
    {
        public static readonly DefinitionVisualIds Empty = new DefinitionVisualIds(new ClassDef[0], new MonsterDef[0]);

        private readonly Dictionary<ushort, string> _classes = new Dictionary<ushort, string>();
        private readonly Dictionary<ushort, string> _monsters = new Dictionary<ushort, string>();
        private readonly Dictionary<ushort, string> _items = new Dictionary<ushort, string>();
        private readonly Dictionary<ushort, string> _npcs = new Dictionary<ushort, string>();
        private readonly Dictionary<ushort, NodeDef> _nodes = new Dictionary<ushort, NodeDef>();

        public DefinitionVisualIds(IEnumerable<ClassDef> classes, IEnumerable<MonsterDef> monsters, IEnumerable<ItemDef>? items = null,
            IEnumerable<NpcDef>? npcs = null, IEnumerable<NodeDef>? nodes = null)
        {
            foreach (var row in nodes ?? new NodeDef[0])
                _nodes[(ushort)row.NetId] = row;
            foreach (var row in npcs ?? new NpcDef[0])
                _npcs[(ushort)row.NetId] = row.VisualId;
            foreach (var row in items ?? new ItemDef[0])
                _items[(ushort)row.NetId] = row.VisualId;
            foreach (var row in classes)
                _classes[(ushort)row.NetId] = row.VisualId;
            foreach (var row in monsters)
                _monsters[(ushort)row.NetId] = row.VisualId;
        }

        public static DefinitionVisualIds From(ConfigSet set) => new DefinitionVisualIds(Rows<ClassDef>(set), Rows<MonsterDef>(set), Rows<ItemDef>(set),
            Rows<NpcDef>(set), Rows<NodeDef>(set));

        /// <summary>False for definition id 0, an unknown id, or a kind without definitions.</summary>
        public bool TryGet(EntityKind kind, ushort defId, out string visualId)
        {
            visualId = string.Empty;
            if (defId == 0)
                return false;
            switch (kind)
            {
                case EntityKind.Player:
                    return _classes.TryGetValue(defId, out visualId!);
                case EntityKind.Monster:
                    return _monsters.TryGetValue(defId, out visualId!);
                case EntityKind.LootDrop:
                    return _items.TryGetValue(defId, out visualId!);
                case EntityKind.Npc:
                    return _npcs.TryGetValue(defId, out visualId!);
                case EntityKind.ResourceNode:
                    return TryGetNode(defId, depleted: false, out visualId);
                default:
                    return false;
            }
        }

        /// <summary>
        /// A resource node's look (WP-M3-4): <c>NodeDef.visualId</c>, or <c>depletedVisualId</c> while the snapshot carries
        /// <see cref="EntityFlags.Depleted"/>. False for an unknown node type.
        /// </summary>
        public bool TryGetNode(ushort defId, bool depleted, out string visualId)
        {
            visualId = string.Empty;
            if (!_nodes.TryGetValue(defId, out var node))
                return false;
            visualId = depleted ? node.DepletedVisualId : node.VisualId;
            return true;
        }

        private static List<T> Rows<T>(ConfigSet set)
        {
            var rows = new List<T>();
            if (set.TryGet(typeof(T), out var table))
            {
                foreach (var row in table.Rows)
                    rows.Add((T)row);
            }
            return rows;
        }
    }
}
