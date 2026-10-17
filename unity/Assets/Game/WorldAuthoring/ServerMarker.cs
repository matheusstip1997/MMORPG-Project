using Shared.World;
using UnityEngine;

namespace Game.WorldAuthoring
{
    /// <summary>
    /// A point the server needs (spec 11.6): spawn points now; resource nodes, portals, outposts, shop spots and guard
    /// posts later. Exported with its ground position (X, Z) and its yaw as a 16-bit facing (0 = +Z, clockwise seen
    /// from above, the MovementSim convention). Ids are unique per world and use letters, digits, '.', '_' and '-'.
    /// </summary>
    public sealed class ServerMarker : MonoBehaviour
    {
        public string markerId = string.Empty;
        public MarkerKind kind = MarkerKind.PlayerSpawn;

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.85f, 0.1f, 1f);
            Gizmos.DrawSphere(transform.position, 0.4f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * 1.5f);
        }
    }
}
