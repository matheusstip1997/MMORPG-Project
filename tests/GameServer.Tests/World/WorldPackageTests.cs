using GameServer.Host.Config;
using GameServer.Tests.Support;
using Shared.Data;
using GameServer.World.Map;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.World;

namespace GameServer.Tests.World;

/// <summary>
/// Spec 11.6: the portable <see cref="WorldPackage"/> the Unity client loads its prediction grid with accepts and
/// refuses the same world directories as the server's <see cref="WorldLoader"/>, and yields the same grid.
/// </summary>
public sealed class WorldPackageTests : IDisposable
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

    private static bool ClientLoads(string directory, out WorldGrid? grid, out string error) =>
        WorldPackage.TryLoadGrid(name =>
        {
            var path = Path.Combine(directory, name);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }, out grid, out error);

    private void AssertBothRefuse(string expectedInError)
    {
        Assert.False(WorldLoader.Load(_files.Directory, Defs, Defaults).Ok);
        Assert.False(ClientLoads(_files.Directory, out var grid, out var error));
        Assert.Null(grid);
        Assert.Contains(expectedInError, error);
    }

    [Fact]
    public void The_committed_M1_world_gives_the_client_the_grid_the_server_collides_with()
    {
        var tables = ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All);
        Assert.True(tables.Ok, string.Join("\n", tables.Errors));
        var server = WorldLoader.Load(RepositoryPaths.M1World(), tables.Set);
        Assert.True(server.Ok, string.Join("\n", server.Errors));

        Assert.True(ClientLoads(RepositoryPaths.M1World(), out var client, out var error), error);

        var a = server.Map!.Grid;
        Assert.Equal((a.OriginXCm, a.OriginYCm, a.CellSizeCm, a.Width, a.Height, a.BaseHeightCm),
            (client!.OriginXCm, client.OriginYCm, client.CellSizeCm, client.Width, client.Height, client.BaseHeightCm));
        Assert.Equal(WorldGridWriter.Write(a), WorldGridWriter.Write(client));
        // The city wall (blockers.json, x -1050..-950) is not walkable for either.
        Assert.False(client.IsWalkable(FixVec2.FromCm(-1000, 1000)));
    }

    [Fact]
    public void A_valid_export_loads()
    {
        Assert.True(ClientLoads(_files.Directory, out var grid, out var error), error);
        Assert.Equal(WorldGridWriter.Write(_files.Grid), WorldGridWriter.Write(grid!));
    }

    [Fact]
    public void Another_manifest_version_is_refused()
    {
        _files.FormatVersion = WorldFiles.ManifestVersion + 1;
        _files.WriteManifest();

        AssertBothRefuse("format version");
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

        AssertBothRefuse("SHA-256");
    }

    [Fact]
    public void A_file_of_another_size_is_refused()
    {
        File.AppendAllText(_files.PathOf(WorldFiles.Markers), "\n");

        AssertBothRefuse("the manifest says");
    }

    [Fact]
    public void A_missing_file_is_refused()
    {
        File.Delete(_files.PathOf(WorldFiles.Regions));

        AssertBothRefuse("missing");
    }

    [Fact]
    public void A_grid_that_disagrees_with_the_manifest_is_refused()
    {
        var manifest = File.ReadAllText(_files.PathOf(WorldFiles.Manifest)).Replace("\"cellSizeCm\": 100", "\"cellSizeCm\": 50");
        File.WriteAllText(_files.PathOf(WorldFiles.Manifest), manifest);

        AssertBothRefuse("grid geometry");
    }

    [Fact]
    public void A_manifest_that_is_not_json_is_refused()
    {
        File.WriteAllText(_files.PathOf(WorldFiles.Manifest), "{ \"format\": ");

        AssertBothRefuse(WorldFiles.Manifest);
    }

    [Fact]
    public void A_missing_directory_is_refused()
    {
        Assert.False(ClientLoads(Path.Combine(_files.Directory, "nowhere"), out _, out var error));
        Assert.Contains(WorldFiles.Manifest, error);
    }
}
