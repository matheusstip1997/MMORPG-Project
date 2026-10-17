using System;

namespace Shared.World
{
    /// <summary>
    /// Kinds of <c>ServerMarker</c> (spec 11.6). Exported as camelCase strings (<c>playerSpawn</c>). M1 needs only
    /// player spawns; monster spawns, resource nodes, portals, outposts, shop spots and guard posts come with their systems.
    /// </summary>
    public enum MarkerKind
    {
        PlayerSpawn = 1,
    }

    /// <summary>camelCase names used in exported files, one definition for the exporter and the server.</summary>
    public static class WorldNames
    {
        public static string Of(MarkerKind kind) => kind switch
        {
            MarkerKind.PlayerSpawn => "playerSpawn",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown marker kind"),
        };

        public static bool TryParseMarkerKind(string? name, out MarkerKind kind)
        {
            switch (name)
            {
                case "playerSpawn":
                    kind = MarkerKind.PlayerSpawn;
                    return true;
                default:
                    kind = default;
                    return false;
            }
        }
    }
}
