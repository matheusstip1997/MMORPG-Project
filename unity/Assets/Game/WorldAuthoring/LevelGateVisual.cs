using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.WorldAuthoring
{
    /// <summary>
    /// The look of a level gate at a pass (R-2.3-06, spec 12.3 R-12.3-01; WP-M2-10): shown while the own level is below the
    /// <c>RegionDef.minLevel</c> of <see cref="regionId"/>, the region the pass leads into, hidden from that level on. Look
    /// only: the server and prediction stop the player by the region's level (the exported grid and <c>RegionDef</c>), never
    /// by this object, which has no collider so the export does not see it.
    /// </summary>
    public sealed class LevelGateVisual : MonoBehaviour
    {
        private static readonly List<LevelGateVisual> Live = new List<LevelGateVisual>();

        /// <summary>The <c>RegionDef</c> id the gate guards, for example <c>field1.ring2</c>.</summary>
        public string regionId = string.Empty;

        /// <summary>The gates in loaded scenes.</summary>
        public static IReadOnlyList<LevelGateVisual> All => Live;

        /// <summary>
        /// Shows every gate whose region needs more than <paramref name="level"/> (<paramref name="minLevelOf"/> gives a
        /// region's level, 0 for none) and hides the others.
        /// </summary>
        public static void ShowFor(int level, Func<string, int> minLevelOf)
        {
            foreach (var gate in Live)
                gate.SetShown(minLevelOf(gate.regionId) > level);
        }

        public bool Shown { get; private set; } = true;

        private void SetShown(bool shown)
        {
            Shown = shown;
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
                renderer.enabled = shown;
        }

        private void OnEnable() => Live.Add(this);

        private void OnDisable() => Live.Remove(this);
    }
}
