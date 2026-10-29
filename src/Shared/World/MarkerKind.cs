using System;

namespace Shared.World
{
    /// <summary>
    /// Kinds of <c>ServerMarker</c> (spec 11.6). Exported as camelCase strings (<c>playerSpawn</c>). M1 has player spawns
    /// and monster spawns (named by <c>SpawnPoints</c> rows); M2 adds NPCs (outposts and vendors, whose marker names an
    /// <c>NpcDef</c> row in its <c>defId</c>) and resource nodes (data only until M3); portals, shop spots and guard posts
    /// come with their systems.
    /// Append only.
    /// </summary>
    public enum MarkerKind
    {
        PlayerSpawn = 1,
        MonsterSpawn = 2,
        /// <summary>An NPC (WP-M2-4): the marker's <c>defId</c> is an <c>NpcDef</c> id.</summary>
        Npc = 3,

        /// <summary>
        /// A resource node (spec 11.6, WP-M2-7): placed as data only; gathering is M3, which maps these markers to node
        /// types with a table, as <c>SpawnPoints</c> does for monsters. No <c>defId</c>; the server loads and ignores them.
        /// </summary>
        ResourceNode = 4,
    }

    /// <summary>camelCase names used in exported files, one definition for the exporter and the server.</summary>
    public static class WorldNames
    {
        public static string Of(MarkerKind kind) => kind switch
        {
            MarkerKind.PlayerSpawn => "playerSpawn",
            MarkerKind.MonsterSpawn => "monsterSpawn",
            MarkerKind.Npc => "npc",
            MarkerKind.ResourceNode => "resourceNode",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown marker kind"),
        };

        public static bool TryParseMarkerKind(string? name, out MarkerKind kind)
        {
            switch (name)
            {
                case "playerSpawn":
                    kind = MarkerKind.PlayerSpawn;
                    return true;
                case "monsterSpawn":
                    kind = MarkerKind.MonsterSpawn;
                    return true;
                case "npc":
                    kind = MarkerKind.Npc;
                    return true;
                case "resourceNode":
                    kind = MarkerKind.ResourceNode;
                    return true;
                default:
                    kind = default;
                    return false;
            }
        }

        /// <summary>
        /// True when markers of <paramref name="kind"/> carry a <c>defId</c> (the definition they place). Only NPC markers
        /// do; the others name their definition through config tables (<c>SpawnPoints</c>) or need none.
        /// </summary>
        public static bool NeedsDefId(MarkerKind kind) => kind == MarkerKind.Npc;
    }
}
