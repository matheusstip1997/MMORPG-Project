using Shared.World;
using UnityEngine;

namespace Game.WorldAuthoring
{
    /// <summary>
    /// A point the server needs (spec 11.6): spawn points and NPCs (outposts and vendors, WP-M2-4) now; resource nodes,
    /// portals, shop spots and guard posts later. Exported with its ground position (X, Z) and its yaw as a 16-bit facing
    /// (0 = +Z, clockwise seen from above, the MovementSim convention). Ids are unique per world and use letters, digits,
    /// '.', '_' and '-'. An npc marker names the <c>NpcDef</c> row it places in <see cref="defId"/> (the other kinds leave
    /// it empty: their definitions come from config tables such as <c>SpawnPoints</c>).
    /// </summary>
    public sealed class ServerMarker : MonoBehaviour
    {
        public string markerId = string.Empty;
        public MarkerKind kind = MarkerKind.PlayerSpawn;

        /// <summary>The definition this marker places: an <c>NpcDef</c> id for npc markers, empty otherwise.</summary>
        public string defId = string.Empty;

        private void OnDrawGizmos()
        {
            Gizmos.color = kind == MarkerKind.Npc ? new Color(0.2f, 0.8f, 1f, 1f) : new Color(1f, 0.85f, 0.1f, 1f);
            Gizmos.DrawSphere(transform.position, 0.4f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * 1.5f);
        }
    }
}
