using UnityEngine;

namespace Game.WorldAuthoring
{
    /// <summary>
    /// A region of the world (spec 2.3 <c>RegionDef</c>, R-2.3-05): every walkability cell whose center lies inside the
    /// box footprint belongs to <see cref="regionId"/>, a row id of data/tables/RegionDef.json (its flags come from
    /// RegionDefaults by area type). Volumes must not overlap, and every walkable cell must be inside exactly one
    /// (decision log 2026-10-17). Several volumes may share one id. The world's export bounds are the union of all
    /// volumes, rounded out to whole cells.
    /// M1: footprint only (one walkable layer); TODO(open) height ranges for overlapping surfaces.
    /// </summary>
    public sealed class RegionVolume : MonoBehaviour
    {
        public string regionId = string.Empty;
        public Vector3 center = Vector3.zero;
        public Vector3 size = new Vector3(10f, 10f, 10f);

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.8f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(center, size);
        }
    }
}
