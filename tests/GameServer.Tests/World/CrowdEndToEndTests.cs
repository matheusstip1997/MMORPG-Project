using GameServer.Host;
using GameServer.Host.Config;
using GameServer.Tests.Support;

namespace GameServer.Tests.World;

/// <summary>
/// Regression for the WP15 bot load test: 50 players standing at the city spawn over the real LiteNetLib transport. A late
/// joiner's first snapshot (49 spawns and 50 entities) was bigger than the peer takes in one datagram, so
/// <c>TooBigPacketException</c> stopped the world. Snapshots now stay within the peer's limit (R-2.5-03, spec 1.7): what
/// does not fit is left out and counted, and the world keeps ticking. Needs PostgreSQL and Redis like the other end-to-end tests.
/// </summary>
public sealed class CrowdEndToEndTests : IAsyncLifetime
{
    private const int Players = 50;

    private readonly ServerConfig _config = ServerTestEnvironment.LoadConfig();
    private readonly List<LoopbackGameClient> _clients = [];
    private ServerTestEnvironment _env = null!;
    private Bootstrap _server = null!;

    public async Task InitializeAsync()
    {
        _config.StartingValues.LingerOutOfCombatMs = 0;
        _config.StartingValues.LingerInCombatMs = 0;
        // Every client connects from 127.0.0.1 at once; the per-IP limits are for real players (R-2.5-03).
        _config.NetSecurity.ConnectAttemptsPerIpPerMinute = 600;
        _config.NetSecurity.ConnectAttemptBurstPerIp = 100;
        _config.NetSecurity.ConnectRequestsPerSecond = 1_000;
        _env = await ServerTestEnvironment.CreateAsync();
        _server = _env.StartServer(_config);
    }

    public async Task DisposeAsync()
    {
        foreach (var client in _clients)
            client.Dispose();
        await _env.DisposeAsync();
    }

    [Fact]
    public async Task Fifty_players_at_the_spawn_keep_the_world_ticking_and_every_snapshot_starts_with_its_own_entity()
    {
        for (long account = 1; account <= Players; account++)
            await _env.CreateCharacterAsync(account);
        for (long account = 1; account <= Players; account++)
            _clients.Add(_env.Connect(_server, _config, account));
        var clients = _clients.ToArray();

        // WaitFor fails as soon as the world thread stops.
        WaitFor(() => clients.All(c => c.OwnEntityId != null && c.Snapshots[^1].Entities.Count == Players), clients);
        uint tick = _server.World.ServerTick;
        WaitFor(() => _server.World.ServerTick >= tick + 20, clients);

        Assert.True(_server.World.SnapshotsTruncated > 0, "the late joiners' first snapshots did not fit one datagram");
        Assert.Equal(0, _server.Transport.UnreliableTooLarge);
        Assert.Equal(Players, clients.Select(c => c.OwnEntityId).Distinct().Count());
        foreach (var client in clients)
            Assert.All(client.Snapshots, s => Assert.Equal(client.OwnEntityId, s.Self.EntityId));
    }

    private void WaitFor(Func<bool> done, params LoopbackGameClient[] clients) => _env.WaitFor(done, clients);
}
