using GameServer.Host.Config;
using GameServer.Tests.Support;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.World;

namespace GameServer.Tests.World;

/// <summary>
/// WP-M2-10b: the portable <see cref="WorldPackage.TryLoadIndex"/> the client's minimap reads the regions and markers with
/// gives what the server's <see cref="WorldLoader"/> reads from the same files, after the same file checks.
/// </summary>
public sealed class WorldIndexTests : IDisposable
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

    private static bool ClientLoads(string directory, out WorldIndex? index, out string error) =>
        WorldPackage.TryLoadIndex(name =>
        {
            var path = Path.Combine(directory, name);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }, out index, out error);

    [Fact]
    public void The_committed_M1_world_gives_the_client_the_server_s_regions_and_markers()
    {
        var tables = ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All);
        Assert.True(tables.Ok, string.Join("\n", tables.Errors));
        var server = WorldLoader.Load(RepositoryPaths.M1World(), tables.Set);
        Assert.True(server.Ok, string.Join("\n", server.Errors));

        Assert.True(ClientLoads(RepositoryPaths.M1World(), out var index, out var error), error);

        Assert.Equal(server.Map!.Regions.Select(r => (r.Index, r.Id)), index!.Regions.Select(r => ((byte)r.Index, r.Id)));
        Assert.Equal(server.Map.Markers.Select(m => (m.Id, m.Kind, m.Position, m.Facing, m.DefId)),
            index.Markers.Select(m => (m.Id, m.Kind, FixVec2.FromCm(m.XCm, m.YCm), m.Facing, m.DefId)));
        Assert.Equal(4, index.Markers.Count(m => m.Kind == MarkerKind.Npc));
        Assert.True(index.TryGetMarker("field1.npc.outpost.mid", out var outpost));
        Assert.Equal(("m1.outpost", 17300, 1100), (outpost.DefId, outpost.XCm, outpost.YCm));
        Assert.Equal(1, index.RegionIndexOf("field1.ring1"));
        Assert.Equal(0, index.RegionIndexOf("nowhere"));
    }

    [Fact]
    public void A_marker_changed_after_export_is_refused()
    {
        var bytes = File.ReadAllBytes(_files.PathOf(WorldFiles.Markers));
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(_files.PathOf(WorldFiles.Markers), bytes);

        Assert.False(ClientLoads(_files.Directory, out var index, out var error));
        Assert.Null(index);
        Assert.Contains("SHA-256", error);
    }

    [Fact]
    public void An_unknown_marker_kind_is_refused_like_the_server_refuses_it()
    {
        _files.Markers.Add(new { id = "x", kind = "portal", xCm = 0, yCm = 0, facing = 0 });
        _files.Write();

        using var valid = new TestWorldFiles();
        Assert.True(WorldLoader.Load(valid.Directory, Defs, Defaults).Ok);
        Assert.False(WorldLoader.Load(_files.Directory, Defs, Defaults).Ok);
        Assert.False(ClientLoads(_files.Directory, out _, out var error));
        Assert.Contains("unknown marker kind 'portal'", error);
    }

    [Fact]
    public void A_valid_export_gives_its_regions_and_markers()
    {
        Assert.True(ClientLoads(_files.Directory, out var index, out var error), error);

        Assert.Equal(new[] { (1, "m1.city"), (2, "m1.combat") }, index!.Regions.Select(r => (r.Index, r.Id)));
        var spawn = Assert.Single(index.Markers);
        Assert.Equal(("m1.spawn.city", MarkerKind.PlayerSpawn, -250, 0, (ushort)16384, ""),
            (spawn.Id, spawn.Kind, spawn.XCm, spawn.YCm, spawn.Facing, spawn.DefId));
    }
}
