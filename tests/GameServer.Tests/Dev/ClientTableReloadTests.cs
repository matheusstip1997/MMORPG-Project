using Game.Gameplay.Dev;
using GameServer.Dev;
using GameServer.Tests.Support;

namespace GameServer.Tests.Dev;

/// <summary>
/// The client side of the dev hot reload (spec 11.7, <see cref="ClientTableReload"/> from the Unity sources): it hashes the
/// files as the server does, and loads only the tables of the server's accepted reload.
/// </summary>
public sealed class ClientTableReloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mmorpg-client-reload-" + Guid.NewGuid().ToString("N"));

    public ClientTableReloadTests()
    {
        Directory.CreateDirectory(_directory);
        foreach (var file in Directory.EnumerateFiles(RepositoryPaths.Tables(), "*.json"))
            File.Copy(file, Path.Combine(_directory, Path.GetFileName(file)));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void Edit(string table, string from, string to)
    {
        var path = Path.Combine(_directory, table + ".json");
        var text = File.ReadAllText(path);
        Assert.Contains(from, text);
        File.WriteAllText(path, text.Replace(from, to));
    }

    [Fact]
    public void The_client_hashes_the_tables_as_the_server_does()
    {
        Assert.Equal(TableReloader.HashOf(_directory), ClientTableReload.HashOf(_directory));
        Edit("ActionDef", "\"recoveryMs\": 300,", "\"recoveryMs\": 350,");
        Assert.Equal(TableReloader.HashOf(_directory), ClientTableReload.HashOf(_directory));
        // A table the server does not swap is not in the hash.
        ulong before = ClientTableReload.HashOf(_directory);
        Edit("HudSettings", "\"toastMs\": 1500,", "\"toastMs\": 1600,");
        Assert.Equal(before, ClientTableReload.HashOf(_directory));
    }

    [Fact]
    public void Only_the_tables_of_the_servers_hash_are_loaded()
    {
        var reload = new ClientTableReload(_directory, ClientTableReload.HashOf(_directory));
        Assert.Equal(ClientReloadStatus.AlreadyApplied, reload.TryLoad(reload.AppliedHash).Status);

        Edit("ActionDef", "\"recoveryMs\": 300,", "\"recoveryMs\": 350,");
        ulong server = TableReloader.HashOf(_directory);
        Edit("ActionDef", "\"recoveryMs\": 350,", "\"recoveryMs\": 400,"); // changed again after the server read it
        Assert.Equal(ClientReloadStatus.HashMismatch, reload.TryLoad(server).Status);

        server = TableReloader.HashOf(_directory);
        var result = reload.TryLoad(server);
        Assert.Equal(ClientReloadStatus.Ready, result.Status);
        Assert.NotNull(result.Set);
        reload.MarkApplied(result);
        Assert.Equal(server, reload.AppliedHash);
    }
}
