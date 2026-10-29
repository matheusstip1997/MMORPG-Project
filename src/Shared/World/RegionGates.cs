using System;
using System.Collections.Generic;
using Shared.Data.Tables;

namespace Shared.World
{
    /// <summary>
    /// The level gates of one world's regions (R-2.3-06, spec 12.3 R-12.3-01): the <c>RegionDef.minLevel</c> of every region
    /// index of the exported grid. Read-only once built, so the server and client prediction share one rule.
    /// <para>
    /// A gate is one way: a mover below a region's level cannot <b>enter</b> it from a region with a lower level; moving
    /// inside it or leaving it is never blocked, so a character already inside (saved there before the gate existed) is
    /// never stuck.
    /// </para>
    /// </summary>
    public sealed class RegionGates
    {
        private readonly int[] _minLevel = new int[WorldGridFormat.MaxRegions + 1];

        /// <summary>No gate anywhere.</summary>
        public static readonly RegionGates None = new RegionGates();

        private RegionGates()
        {
        }

        /// <summary>True when at least one region has a level gate.</summary>
        public bool Any { get; private set; }

        /// <summary>
        /// Builds the gates of a world: <paramref name="regionIdsByIndex"/> is the world's <c>regions.json</c> in index order
        /// (entry i is region index i + 1); a region id without a <c>RegionDef</c> row has no gate (the world loader reports it).
        /// </summary>
        public static RegionGates From(IReadOnlyList<string> regionIdsByIndex, IEnumerable<RegionDef> regionDefs)
        {
            if (regionIdsByIndex == null)
                throw new ArgumentNullException(nameof(regionIdsByIndex));
            if (regionDefs == null)
                throw new ArgumentNullException(nameof(regionDefs));
            var byId = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var def in regionDefs)
                byId[def.Id] = def.MinLevel;
            int count = Math.Min(regionIdsByIndex.Count, WorldGridFormat.MaxRegions);
            var levels = new int[count + 1];
            for (int i = 0; i < count; i++)
                levels[i + 1] = byId.TryGetValue(regionIdsByIndex[i], out int level) ? level : 0;
            return FromMinLevels(levels);
        }

        /// <summary>The gates of a loaded world index (<see cref="WorldPackage.TryLoadIndex"/>): its regions by index.</summary>
        public static RegionGates From(WorldIndex index, IEnumerable<RegionDef> regionDefs)
        {
            if (index == null)
                throw new ArgumentNullException(nameof(index));
            if (regionDefs == null)
                throw new ArgumentNullException(nameof(regionDefs));
            var byId = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var def in regionDefs)
                byId[def.Id] = def.MinLevel;
            var levels = new int[WorldGridFormat.MaxRegions + 1];
            foreach (var region in index.Regions)
            {
                if (region.Index > 0 && region.Index <= WorldGridFormat.MaxRegions && byId.TryGetValue(region.Id, out int level))
                    levels[region.Index] = level;
            }
            return FromMinLevels(levels);
        }

        /// <summary>Builds the gates from the min level of each region index (entry 0, no region, is ignored).</summary>
        public static RegionGates FromMinLevels(IReadOnlyList<int> minLevelByIndex)
        {
            if (minLevelByIndex == null)
                throw new ArgumentNullException(nameof(minLevelByIndex));
            var gates = new RegionGates();
            int count = Math.Min(minLevelByIndex.Count, WorldGridFormat.MaxRegions + 1);
            for (int i = 1; i < count; i++)
            {
                if (minLevelByIndex[i] <= 0)
                    continue;
                gates._minLevel[i] = minLevelByIndex[i];
                gates.Any = true;
            }
            return gates;
        }

        /// <summary>The level a mover needs to enter the region (0 = none; also for index 0, no region).</summary>
        public int MinLevelOf(byte regionIndex) => _minLevel[regionIndex];

        /// <summary>
        /// Whether a mover of <paramref name="level"/> may not step from a cell of region <paramref name="from"/> into one of
        /// region <paramref name="to"/> (R-2.3-06).
        /// </summary>
        public bool Blocks(byte from, byte to, int level)
        {
            int needed = _minLevel[to];
            return needed > level && needed > _minLevel[from];
        }
    }

    /// <summary>
    /// The gates one mover is held to: the world's <see cref="RegionGates"/> and the mover's level. <c>default</c> holds no
    /// gate (monsters, and every caller from before R-2.3-06).
    /// </summary>
    public readonly struct MoverGate
    {
        public MoverGate(RegionGates gates, int level)
        {
            Gates = gates;
            Level = level;
        }

        public RegionGates? Gates { get; }

        public int Level { get; }

        /// <summary>True when a step may be refused at all; false skips the region reads.</summary>
        public bool Active => Gates != null && Gates.Any;

        public bool Blocks(byte from, byte to) => Gates != null && Gates.Blocks(from, to, Level);
    }
}
