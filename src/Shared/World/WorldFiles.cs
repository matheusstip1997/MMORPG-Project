namespace Shared.World
{
    /// <summary>
    /// Names and versions of the files UnityExporter writes into a world directory (spec 11.6), one definition for the
    /// exporter, the server's world loader and DataValidator.
    /// <list type="bullet">
    /// <item><c>manifest.json</c>: format and version (checked by the server at startup), tool version, bounds, cell
    /// size, agent radius, source hashes, and the size and SHA-256 of every other file.</item>
    /// <item><c>grid.bin</c>: walkability, per-cell region and height layers (<see cref="WorldGridFormat"/>).</item>
    /// <item><c>regions.json</c>: region index (as used by the grid) to <c>RegionDef</c> id.</item>
    /// <item><c>markers.json</c>: <c>ServerMarker</c>s (id, kind, ground position, facing, and <c>defId</c> for npc markers).</item>
    /// <item><c>blockers.json</c>: simplified <c>ServerBlocker</c> boxes (cm), the shapes behind the grid.</item>
    /// </list>
    /// All JSON is UTF-8 without BOM, LF line endings, fixed key order, lists sorted, so an export is byte-identical
    /// when its sources are.
    /// </summary>
    public static class WorldFiles
    {
        public const string ManifestFormat = "mmorpg.world";

        /// <summary>
        /// Bump when any file's shape or meaning changes; the server refuses other versions. 2: markers may carry a
        /// <c>defId</c> (npc markers, WP-M2-4).
        /// </summary>
        public const int ManifestVersion = 2;

        public const string Manifest = "manifest.json";
        public const string Grid = "grid.bin";
        public const string Regions = "regions.json";
        public const string Markers = "markers.json";
        public const string Blockers = "blockers.json";

        /// <summary>The files a manifest must list, in the order it lists them (ordinal by name).</summary>
        public static readonly string[] DataFiles = { Blockers, Grid, Markers, Regions };
    }
}
