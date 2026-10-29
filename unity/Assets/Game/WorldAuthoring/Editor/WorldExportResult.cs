#nullable enable
using System.Collections.Generic;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>One problem the exporter's validator found; <see cref="Code"/> is stable for reports and tests.</summary>
    public sealed class ExportIssue
    {
        public ExportIssue(string code, string objectPath, string message)
        {
            Code = code;
            ObjectPath = objectPath;
            Message = message;
        }

        public string Code { get; }

        /// <summary>Hierarchy path of the offending object, or empty for scene-wide problems.</summary>
        public string ObjectPath { get; }

        public string Message { get; }

        public override string ToString() => ObjectPath.Length == 0 ? $"{Code}: {Message}" : $"{Code} {ObjectPath}: {Message}";
    }

    /// <summary>Validator codes (spec 11.6: marker ids, region conflicts, bounds, size limits).</summary>
    public static class ExportCodes
    {
        public const string MissingSettings = "MissingSettings";
        public const string BadSettings = "BadSettings";
        public const string BadShape = "BadShape";
        public const string NoRegions = "NoRegions";
        public const string BadRegionId = "BadRegionId";
        public const string TooManyRegions = "TooManyRegions";
        public const string RegionOverlap = "RegionOverlap";
        public const string WalkableWithoutRegion = "WalkableWithoutRegion";
        public const string SizeLimit = "SizeLimit";
        public const string BadMarkerId = "BadMarkerId";
        public const string DuplicateMarker = "DuplicateMarker";
        public const string BadMarkerKind = "BadMarkerKind";
        public const string MarkerOutOfBounds = "MarkerOutOfBounds";
        public const string MarkerNotWalkable = "MarkerNotWalkable";
        /// <summary>An npc marker without a valid defId (WP-M2-4).</summary>
        public const string MissingMarkerDef = "MissingMarkerDef";
        /// <summary>A defId on a marker kind that does not carry one.</summary>
        public const string UnexpectedMarkerDef = "UnexpectedMarkerDef";
        public const string HeightRange = "HeightRange";
    }

    /// <summary>What one export produced: the files by name (manifest included) or the errors that stopped it.</summary>
    public sealed class WorldExportResult
    {
        public List<ExportIssue> Errors { get; } = new List<ExportIssue>();

        /// <summary>File name to exact bytes, sorted by name. Empty when there are errors.</summary>
        public SortedDictionary<string, byte[]> Files { get; } = new SortedDictionary<string, byte[]>(System.StringComparer.Ordinal);

        public bool Ok => Errors.Count == 0;

        public string WorldId { get; set; } = string.Empty;
        public string SourceScene { get; set; } = string.Empty;
        public int WidthCells { get; set; }
        public int HeightCells { get; set; }
        public int WalkableCells { get; set; }
        public int RegionCount { get; set; }
        public int MarkerCount { get; set; }
        public int BlockerCount { get; set; }

        /// <summary>Walkable cells per region id (exporter v1 report, WP-M2-7).</summary>
        public SortedDictionary<string, int> WalkableCellsByRegion { get; } = new SortedDictionary<string, int>(System.StringComparer.Ordinal);

        /// <summary>Markers per kind name (exporter v1 report).</summary>
        public SortedDictionary<string, int> MarkersByKind { get; } = new SortedDictionary<string, int>(System.StringComparer.Ordinal);

        public void Error(string code, string objectPath, string message) => Errors.Add(new ExportIssue(code, objectPath, message));
    }
}
