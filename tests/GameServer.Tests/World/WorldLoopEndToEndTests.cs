using System.Diagnostics;
using GameServer.Host;
using GameServer.Host.Config;
using GameServer.Net.Tickets;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Protocol;

namespace GameServer.Tests.World;

/// <summary>
/// The whole server loop over localhost: real transport, network thread, simulation thread at the configured 20 Hz,
/// and LiteNetLib clients with dev tickets (backlog C1).
/// </summary>
public sealed class WorldLoopEndToEndTests : IDisposable
{
    private const int Realm = 1;
    private static readonly byte[] Secret = Enumerable.Repeat((byte)0x3C, 32).ToArray();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly SystemClock _clock = new();
    private readonly ServerConfig _config = LoadRepositoryConfig();
    private readonly Bootstrap _server;
    private readonly List<LoopbackGameClient> _clients = [];

    public WorldLoopEndToEndTests()
    {
        var settings = new ServerSettings(Secret, TicketKeyId: 1, Realm, Port: 0, DataDirectory: "unused");
        _server = new Bootstrap(_config, settings, _clock, new StopwatchTickClock());
        _server.Start();
    }

    public void Dispose()
    {
        foreach (var client in _clients)
            client.Dispose();
        _server.Dispose();
    }

    [Fact]
    public void A_client_holding_W_sees_its_entity_move_in_the_snapshots_with_inputs_acknowledged()
    {
        var client = Connect(accountId: 100);
        WaitFor(() => client.OwnEntityId != null, client);
        int startZ = client.Snapshots[^1].Self.ZCm;

        // Hold W for about a second at the client's 20 Hz input rate.
        RunClients(TimeSpan.FromSeconds(1), () => client.SendFrame(MoveKeys.W, facing: 0, _config.StartingValues.InputRedundancy));
        WaitFor(() => client.Snapshots[^1].Header.HasInputAck && client.Snapshots[^1].Header.LastInputSequence == client.LastSequence, client);

        var last = client.Snapshots[^1];
        // W is +Y on the ground plane, which snapshots carry as Z. Each applied frame is one straight step; a frame
        // can be dropped when timer jitter bursts the client past the input queue, never applied twice.
        int stepCm = _config.StartingValues.MoveSpeedCmPerSec / _config.StartingValues.TickRateHz;
        int moved = last.Self.ZCm - startZ;
        Assert.Equal(0, moved % stepCm);
        Assert.InRange(moved / stepCm, client.LastSequence - _config.ServerLoop.MaxQueuedInputFrames, client.LastSequence);
        Assert.Equal(0, last.Self.XCm);

        var acked = client.Snapshots.Where(s => s.Header.HasInputAck).ToList();
        Assert.NotEmpty(acked);
        for (int i = 1; i < acked.Count; i++)
        {
            Assert.False(Sequence.IsNewer(acked[i - 1].Header.LastInputSequence, acked[i].Header.LastInputSequence));
            Assert.True(acked[i].Self.ZCm >= acked[i - 1].Self.ZCm);
        }
        Assert.Equal(0, _server.World.SnapshotsTruncated);
    }

    [Fact]
    public void A_second_client_spawns_for_the_first_and_each_sees_itself_first()
    {
        var first = Connect(accountId: 1);
        WaitFor(() => first.OwnEntityId != null, first);
        var second = Connect(accountId: 2);
        WaitFor(() => second.OwnEntityId != null, first, second);
        WaitFor(() => first.Snapshots[^1].Entities.Count == 2, first, second);

        Assert.NotEqual(first.OwnEntityId, second.OwnEntityId);
        Assert.Contains(first.Snapshots, s => s.Spawns.Any(spawn => spawn.EntityId == second.OwnEntityId && spawn.Kind == EntityKind.Player));
        Assert.Equal(second.OwnEntityId, first.Snapshots[^1].Entities[1].EntityId);

        second.Dispose();
        _clients.Remove(second);
        WaitFor(() => first.Snapshots.Any(s => s.Despawns.Contains(second.OwnEntityId!.Value)), first);
    }

    private LoopbackGameClient Connect(long accountId)
    {
        var issued = new DevTicketIssuer(1, Secret, _clock, _config.NetSecurity.TicketLifetimeMs).Issue(accountId, Realm, slot: 0);
        var client = new LoopbackGameClient(_server.Transport.LocalPort, issued);
        _clients.Add(client);
        WaitFor(() => client.Connected, client);
        return client;
    }

    private void WaitFor(Func<bool> done, params LoopbackGameClient[] clients)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(watch.Elapsed < Timeout, "Timed out waiting for the server.");
            Assert.Null(_server.WorldThread.Failure);
            foreach (var client in clients)
                client.Poll();
            Thread.Sleep(5);
        }
    }

    /// <summary>Calls <paramref name="sendFrame"/> every client tick while polling all clients.</summary>
    private void RunClients(TimeSpan duration, Action sendFrame)
    {
        long periodMs = 1_000 / _config.StartingValues.TickRateHz;
        var watch = Stopwatch.StartNew();
        long nextFrameMs = 0;
        while (watch.Elapsed < duration)
        {
            if (watch.ElapsedMilliseconds >= nextFrameMs)
            {
                sendFrame();
                nextFrameMs += periodMs;
            }
            foreach (var client in _clients)
                client.Poll();
            Thread.Sleep(1);
        }
    }

    private static ServerConfig LoadRepositoryConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var result = ConfigLoader.LoadAndValidate(Path.Combine(directory.FullName, "data", "tables"), ConfigTables.All);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        return ServerConfig.From(result.Set);
    }
}
