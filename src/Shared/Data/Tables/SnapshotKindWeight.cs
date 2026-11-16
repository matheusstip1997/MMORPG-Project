using System;
using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// The priority weight of one entity kind in a full snapshot (<see cref="SnapshotPriority"/>), in percent. Kinds that do not
    /// move (NPCs, stalls, nodes) weigh less: the client holds their last pose. One row per <see cref="EntityKind"/> but None.
    /// </summary>
    [Table("SnapshotKindWeight", 1)]
    public sealed class SnapshotKindWeight : IValidatableRow
    {
        /// <summary>The entity kind in camelCase, for example <c>player</c> or <c>shopStall</c> (<see cref="EntityKind"/>).</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>The kind's weight in percent: a state of weight 50 waits about twice as long as one of weight 100.</summary>
        [Range(1, 100)] public int WeightPercent { get; set; }

        public void Validate(RowErrors errors)
        {
            if (!TryParseKind(Id, out _))
                errors.Add(nameof(Id), "must name an entity kind (player, monster, npc, projectile, lootBag, resourceNode, portal, shopStall, lootDrop)");
        }

        /// <summary>The kind of a row id: the camelCase name of an <see cref="EntityKind"/> other than None.</summary>
        public static bool TryParseKind(string id, out EntityKind kind)
        {
            for (var candidate = EntityKind.Player; candidate <= EntityKind.LootDrop; candidate++)
            {
                if (string.Equals(id, IdOf(candidate), StringComparison.Ordinal))
                {
                    kind = candidate;
                    return true;
                }
            }
            kind = EntityKind.None;
            return false;
        }

        /// <summary>The row id of a kind: its name in camelCase.</summary>
        public static string IdOf(EntityKind kind)
        {
            string name = kind.ToString();
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }
    }
}
