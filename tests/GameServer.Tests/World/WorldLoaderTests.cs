using GameServer.Host.Config;
using GameServer.Tests.Support;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.World;

namespace GameServer.Tests.World;

/// <summary>Spec 11.6: the server verifies the exported world (manifest version, hashes, regions, markers) at startup.</summary>
public sealed class WorldLoaderTests : IDisposable
{
    private static readonly RegionDef[] Defs =
    [
        new() { Id = "m1.city", AreaType = "normalCity" },
        new() { Id = "m1.combat", AreaType = "openWorld" },
    ];

    private static readonly RegionDefaults[] Defaults =
    [
        new() { Id = "normalCity", PvpMode = PvpMode.Off, HonorEffects = true, DeathDrop = DeathDrop.Off },
        new() { Id = "openWorld", PvpMode = PvpMode.ArmedOnly, HonorEffects = true, DeathDrop = DeathDrop.On },
    ];

    private readonly TestWorldFiles _files = new();

    public void Dispose() => _files.Dispose();

    private WorldLoadResult Load() => WorldLoader.Load(_files.Directory, Defs, Defaults);

    private static void AssertRefused(WorldLoadResult result, string code)
    {
        Assert.False(result.Ok);
        Assert.Null(result.Map);
        Assert.Contains(result.Errors, e => e.Code == code);
    }

    [Fact]
    public void A_valid_export_loads_with_regions_flags_markers_and_blockers()
    {
        var result = Load();

        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var map = result.Map!;
        Assert.Equal(["m1.city", "m1.combat"], map.Regions.Select(r => r.Id));
        Assert.True(map.TryGetMarker("m1.spawn.city", out var spawn));
        Assert.Equal((MarkerKind.PlayerSpawn, FixVec2.FromCm(-250, 0), (ushort)16384), (spawn.Kind, spawn.Position, spawn.Facing));
        Assert.Single(map.Blockers);
        Assert.Null(WorldLoader.CheckPlayerSpawn(map, "m1.spawn.city"));
    }

    [Fact]
    public void The_region_lookup_reads_the_cell_and_resolves_its_area_defaults()
    {
        var map = Load().Map!;

        Assert.Equal("m1.city", map.RegionAt(FixVec2.FromCm(-450, 0))!.Id);
        Assert.Equal("m1.combat", map.RegionAt(FixVec2.FromCm(0, 0))!.Id);
        Assert.True(map.TryGetFlags(FixVec2.FromCm(-450, 0), out var city));
        Assert.Equal(new RegionFlags(PvpMode.Off, honorEffects: true, DeathDrop.Off), city);
        Assert.True(map.TryGetFlags(FixVec2.FromCm(400, 400), out var combat));
        Assert.Equal(PvpMode.ArmedOnly, combat.PvpMode);
        // Outside the grid there is no region.
        Assert.Null(map.RegionAt(FixVec2.FromCm(10_000, 0)));
        Assert.False(map.TryGetFlags(FixVec2.FromCm(10_000, 0), out _));
    }

    [Fact]
    public void Another_manifest_version_is_refused()
    {
        _files.FormatVersion = WorldFiles.ManifestVersion + 1;
        _files.WriteManifest();

        AssertRefused(Load(), WorldLoader.Codes.VersionMismatch);
    }

    [Fact]
    public void A_missing_manifest_is_refused()
    {
        File.Delete(_files.PathOf(WorldFiles.Manifest));

        AssertRefused(Load(), WorldLoader.Codes.MissingFile);
    }

    [Theory]
    [InlineData(WorldFiles.Grid)]
    [InlineData(WorldFiles.Markers)]
    [InlineData(WorldFiles.Regions)]
    [InlineData(WorldFiles.Blockers)]
    public void A_file_changed_after_export_is_refused(string name)
    {
        var bytes = File.ReadAllBytes(_files.PathOf(name));
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(_files.PathOf(name), bytes);

        AssertRefused(Load(), WorldLoader.Codes.FileMismatch);
    }

    [Fact]
    public void A_corrupt_grid_with_a_matching_hash_is_still_refused_by_the_decoder()
    {
        var bytes = File.ReadAllBytes(_files.PathOf(WorldFiles.Grid));
        bytes[6] = 2; // layer count
        File.WriteAllBytes(_files.PathOf(WorldFiles.Grid), bytes);
        _files.WriteManifest();

        AssertRefused(Load(), WorldLoader.Codes.GridInvalid);
    }

    [Fact]
    public void A_grid_that_disagrees_with_the_manifest_is_refused()
    {
        _files.WriteManifest();
        var manifest = File.ReadAllText(_files.PathOf(WorldFiles.Manifest)).Replace("\"cellSizeCm\": 100", "\"cellSizeCm\": 50");
        File.WriteAllText(_files.PathOf(WorldFiles.Manifest), manifest);

        AssertRefused(Load(), WorldLoader.Codes.GridMismatch);
    }

    [Fact]
    public void A_region_missing_from_RegionDef_is_refused()
    {
        _files.Regions[1] = (2, "m1.nowhere");
        _files.Write();

        AssertRefused(Load(), WorldLoader.Codes.UnknownRegion);
    }

    [Fact]
    public void Cells_pointing_past_the_region_list_are_refused()
    {
        _files.Regions.RemoveAt(1);
        _files.Write();

        AssertRefused(Load(), WorldLoader.Codes.BadRegion);
    }

    [Fact]
    public void Region_indices_must_count_from_one()
    {
        _files.Regions[0] = (3, "m1.city");
        _files.Write();

        AssertRefused(Load(), WorldLoader.Codes.BadRegion);
    }

    [Fact]
    public void Duplicate_marker_ids_are_refused()
    {
        _files.Markers.Add(new { id = "m1.spawn.city", kind = "playerSpawn", xCm = -150, yCm = 0, facing = 0 });
        _files.Write();

        AssertRefused(Load(), WorldLoader.Codes.DuplicateMarker);
    }

    [Theory]
    [InlineData("monsterSpawn", -250, 0, 0)]  // unknown kind
    [InlineData("playerSpawn", 50, 50, 0)]    // on the blocked cell (5, 5)
    [InlineData("playerSpawn", 900, 0, 0)]    // outside the grid
    [InlineData("playerSpawn", -250, 0, 70_000)] // facing out of range
    public void Bad_markers_are_refused(string kind, int xCm, int yCm, int facing)
    {
        _files.Markers.Add(new { id = "m1.zz", kind, xCm, yCm, facing });
        _files.Write();

        AssertRefused(Load(), WorldLoader.Codes.BadMarker);
    }

    [Fact]
    public void An_unknown_json_field_is_refused()
    {
        _files.Markers[0] = new { id = "m1.spawn.city", kind = "playerSpawn", xCm = -250, yCm = 0, facing = 0, extra = 1 };
        _files.Write();

        AssertRefused(Load(), WorldLoader.Codes.BadJson);
    }

    [Fact]
    public void An_empty_blocker_is_refused()
    {
        _files.Blockers.Add(new { minXCm = 10, minYCm = 0, maxXCm = 10, maxYCm = 100, minHeightCm = 0, maxHeightCm = 0 });
        _files.Write();

        AssertRefused(Load(), WorldLoader.Codes.BadBlocker);
    }

    [Fact]
    public void The_spawn_marker_from_config_must_exist()
    {
        var map = Load().Map!;

        Assert.Equal(WorldLoader.Codes.MissingSpawn, WorldLoader.CheckPlayerSpawn(map, "m1.spawn.elsewhere")!.Code);
    }

    [Fact]
    public void The_committed_M1_world_loads_with_the_repository_tables()
    {
        var tables = ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All);
        Assert.True(tables.Ok, string.Join("\n", tables.Errors));

        var result = WorldLoader.Load(RepositoryPaths.M1World(), tables.Set);

        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var map = result.Map!;
        var spawnId = tables.Set.Single<ServerLoop>().PlayerSpawnMarkerId;
        Assert.Null(WorldLoader.CheckPlayerSpawn(map, spawnId));
        map.TryGetMarker(spawnId, out var spawn);
        // Backlog C: players start in the safe city, next to the combat area.
        Assert.Equal("m1.city", map.RegionAt(spawn.Position)!.Id);
        Assert.Equal(PvpMode.Off, map.RegionAt(spawn.Position)!.Flags.PvpMode);
        Assert.Contains(map.Regions, r => r.Id == "m1.combat" && r.AreaType == "openWorld");
    }
}
