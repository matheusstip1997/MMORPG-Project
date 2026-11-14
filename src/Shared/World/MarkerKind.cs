using System;

namespace Shared.World
{
    /// <summary>
    /// Kinds of <c>ServerMarker</c> (spec 11.6). Exported as camelCase strings (<c>playerSpawn</c>). M1 has player spawns
    /// and monster spawns (named by <c>SpawnPoints</c> rows); M2 adds NPCs (outposts and vendors, whose marker names an
    /// <c>NpcDef</c> row in its <c>defId</c>) and resource nodes (data only until M3); M4 adds stall places (WP-M4-8);
    /// portals and guard posts come with their systems.
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

        /// <summary>
        /// A stall place (WP-M4-8, spec 4.5, M4 decision 7): a free place in a city where a player opens a shop stall. No
        /// <c>defId</c>; the cap of stalls per city is the number of these markers. Its place ordinal (its index among the stall
        /// markers in id order, <see cref="StallPlaces"/>) names it on the wire.
        /// </summary>
        Stall = 5,
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
            MarkerKind.Stall => "stall",
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
                case "stall":
                    kind = MarkerKind.Stall;
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

namespace Shared.World
{
    /// <summary>
    /// Stall places (WP-M4-8): a stall place's ordinal is its index among the <see cref="MarkerKind.Stall"/> markers sorted by
    /// marker id (ordinal string order), the same on the server (<c>WorldMap.Markers</c>) and on the client (the world index), so
    /// requests, the stall entity's definition id and the client's prompt name a place by that number.
    /// </summary>
    public static class StallPlaces
    {
        /// <summary>Most stall places one world may hold (the ordinal travels as a byte and as the entity's 16-bit def id).</summary>
        public const int MaxPlaces = 250;

        /// <summary>The stall marker ids of <paramref name="markerIds"/> and kinds, in ordinal order (sorted by id, ordinal).</summary>
        public static System.Collections.Generic.List<string> Ordered(System.Collections.Generic.IEnumerable<(string Id, MarkerKind Kind)> markers)
        {
            var ids = new System.Collections.Generic.List<string>();
            foreach (var (id, kind) in markers)
            {
                if (kind == MarkerKind.Stall)
                    ids.Add(id);
            }
            ids.Sort(System.StringComparer.Ordinal);
            return ids;
        }
    }
}
