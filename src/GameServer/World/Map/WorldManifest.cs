namespace GameServer.World.Map;

/// <summary>manifest.json (spec 11.6). Field names are the camelCase JSON keys UnityExporter writes.</summary>
public sealed class WorldManifest
{
    public string Format { get; set; } = string.Empty;
    public int FormatVersion { get; set; }
    public string ToolVersion { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;
    public int CellSizeCm { get; set; }

    /// <summary>Blockers were grown by this radius when the grid was built; the mover is a point (decision log 2026-10-17).</summary>
    public int AgentRadiusCm { get; set; }

    public WorldBounds Bounds { get; set; } = new();
    public int WidthCells { get; set; }
    public int HeightCells { get; set; }
    public List<WorldSourceFile> Sources { get; set; } = [];
    public List<WorldDataFile> Files { get; set; } = [];
}

/// <summary>Ground-plane bounds in cm: [min, max) on both axes, the grid's edges.</summary>
public sealed class WorldBounds
{
    public int MinXCm { get; set; }
    public int MinYCm { get; set; }
    public int MaxXCm { get; set; }
    public int MaxYCm { get; set; }
}

/// <summary>An authoring source (the scene) and the SHA-256 of its text with line endings normalized to LF.</summary>
public sealed class WorldSourceFile
{
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>A generated file next to the manifest, with its exact size and SHA-256 (lowercase hex).</summary>
public sealed class WorldDataFile
{
    public string Name { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}
