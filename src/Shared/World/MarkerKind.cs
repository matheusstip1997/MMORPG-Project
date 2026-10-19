using System;

namespace Shared.World
{
    /// <summary>
    /// Kinds of <c>ServerMarker</c> (spec 11.6). Exported as camelCase strings (<c>playerSpawn</c>). M1 has player spawns
    /// and monster spawns (named by <c>SpawnPoints</c> rows); resource nodes, portals, outposts, shop spots and guard posts
    /// come with their systems. Append only.
    /// </summary>
    public enum MarkerKind
    {
        PlayerSpawn = 1,
        MonsterSpawn = 2,
    }

    /// <summary>camelCase names used in exported files, one definition for the exporter and the server.</summary>
    public static class WorldNames
    {
        public static string Of(MarkerKind kind) => kind switch
        {
            MarkerKind.PlayerSpawn => "playerSpawn",
            MarkerKind.MonsterSpawn => "monsterSpawn",
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
                default:
                    kind = default;
                    return false;
            }
        }
    }
}
