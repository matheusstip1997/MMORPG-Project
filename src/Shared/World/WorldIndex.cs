using System;
using System.Collections.Generic;

namespace Shared.World
{
    /// <summary>One row of <c>regions.json</c>: the grid's region index and the <c>RegionDef</c> id it stands for.</summary>
    public readonly struct WorldIndexRegion
    {
        public WorldIndexRegion(int index, string id)
        {
            Index = index;
            Id = id;
        }

        public int Index { get; }
        public string Id { get; }
    }

    /// <summary>One exported marker of <c>markers.json</c> (spec 11.6): ground position in cm, facing as a binary angle.</summary>
    public readonly struct WorldIndexMarker
    {
        public WorldIndexMarker(string id, MarkerKind kind, int xCm, int yCm, ushort facing, string defId)
        {
            Id = id;
            Kind = kind;
            XCm = xCm;
            YCm = yCm;
            Facing = facing;
            DefId = defId;
        }

        public string Id { get; }
        public MarkerKind Kind { get; }
        public int XCm { get; }
        public int YCm { get; }
        public ushort Facing { get; }

        /// <summary>The <c>NpcDef</c> id of an npc marker; empty for the other kinds.</summary>
        public string DefId { get; }
    }

    /// <summary>
    /// The regions and markers of an exported world as the client reads them (<see cref="WorldPackage.TryLoadIndex"/>;
    /// the minimap of WP-M2-10b places NPCs, camps and passes from it). The server's <c>WorldLoader</c> reads the same files
    /// into its own map. Load time only.
    /// </summary>
    public sealed class WorldIndex
    {
        private readonly Dictionary<string, WorldIndexMarker> _markersById = new Dictionary<string, WorldIndexMarker>(StringComparer.Ordinal);

        public WorldIndex(IReadOnlyList<WorldIndexRegion> regions, IReadOnlyList<WorldIndexMarker> markers)
        {
            Regions = regions;
            Markers = markers;
            foreach (var marker in markers)
                _markersById[marker.Id] = marker;
        }

        public IReadOnlyList<WorldIndexRegion> Regions { get; }
        public IReadOnlyList<WorldIndexMarker> Markers { get; }

        public bool TryGetMarker(string id, out WorldIndexMarker marker) => _markersById.TryGetValue(id, out marker);

        /// <summary>The grid's region index of a <c>RegionDef</c> id; 0 (no region) when the world has none by that id.</summary>
        public int RegionIndexOf(string regionId)
        {
            foreach (var region in Regions)
            {
                if (string.Equals(region.Id, regionId, StringComparison.Ordinal))
                    return region.Index;
            }
            return 0;
        }
    }
}
