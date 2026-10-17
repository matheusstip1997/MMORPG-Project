using UnityEngine;

namespace Game.WorldAuthoring
{
    /// <summary>
    /// One per exported scene: how UnityExporter builds the walkability grid (spec 11.6). Both values are authored world
    /// data, recorded in the manifest (decision log 2026-10-17), not config tables.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldExportSettings : MonoBehaviour
    {
        /// <summary>Folder name and manifest id of the export, for example <c>m1</c>.</summary>
        public string worldId = "m1";

        /// <summary>Walkability cell edge in cm.</summary>
        [Min(1)] public int cellSizeCm = 50;

        /// <summary>
        /// Blockers are grown by this radius before cells are marked, so the server and client can move a point.
        /// Placeholder until character size is defined.
        /// </summary>
        [Min(0)] public int agentRadiusCm = 40;
    }
}
