using System.Diagnostics;
using GameServer.Events;
using GameServer.Host;
using GameServer.Telemetry;
using GameServer.Host.Config;
using GameServer.Net.Tickets;
using GameServer.Tests.Support;
using GameServer.World.Map;
using Shared.Data;
using Shared.Protocol;

namespace GameServer.Tests.World;

/// <summary>
/// The whole server loop over localhost: real transport, network thread, simulation thread at the configured 20 Hz,
/// and LiteNetLib clients with dev tickets (backlog C1), with sessions on real Redis and PostgreSQL (backlog B4). Each client
/// logs in to its own character; linger is off here, so a disconnect removes the character at once.
/// </summary>
public sealed class WorldLoopEndToEndTests : IAsyncLifetime
{
    private readonly ServerConfig _config = ServerTestEnvironment.LoadConfig();
    // New test characters stand on the M1 player spawn marker (ServerTestEnvironment.CreateCharacterAsync).
    private readonly int _spawnXCm = ServerTestEnvironment.SpawnCm().XCm;
    private readonly List<LoopbackGameClient> _clients = [];
    private ServerTestEnvironment _env = null!;
    private Bootstrap _server = null!;

    public async Task InitializeAsync()
    {
        _config.StartingValues.LingerOutOfCombatMs = 0;
        _config.StartingValues.LingerInCombatMs = 0;
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
    public async Task A_client_holding_W_sees_its_entity_move_in_the_snapshots_with_inputs_acknowledged()
    {
        var client = await ConnectAsync(accountId: 100);
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
        Assert.Equal(_spawnXCm, last.Self.XCm); // W keeps X at the spawn marker's

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
    public async Task A_second_client_spawns_for_the_first_and_each_sees_itself_first()
    {
        var first = await ConnectAsync(accountId: 1);
        WaitFor(() => first.OwnEntityId != null, first);
        var second = await ConnectAsync(accountId: 2);
        WaitFor(() => second.OwnEntityId != null, first, second);
        WaitFor(() => first.Snapshots[^1].Entities.Count == 2, first, second);

        Assert.NotEqual(first.OwnEntityId, second.OwnEntityId);
        Assert.Contains(first.Snapshots, s => s.Spawns.Any(spawn => spawn.EntityId == second.OwnEntityId && spawn.Kind == EntityKind.Player));
        Assert.Equal(second.OwnEntityId, first.Snapshots[^1].Entities[1].EntityId);

        second.Dispose();
        _clients.Remove(second);
        WaitFor(() => first.Snapshots.Any(s => s.Despawns.Contains(second.OwnEntityId!.Value)), first);
    }

    [Fact]
    public async Task Sessions_and_tick_health_reach_the_telemetry_files_and_shutdown_ends_open_sessions()
    {
        var leaving = await ConnectAsync(accountId: 7);
        var staying = await ConnectAsync(accountId: 8);
        WaitFor(() => leaving.OwnEntityId != null && staying.OwnEntityId != null && staying.Snapshots[^1].Entities.Count == 2,
            leaving, staying);
        uint leavingId = leaving.OwnEntityId!.Value;
        uint stayingId = staying.OwnEntityId!.Value;

        leaving.Dispose();
        _clients.Remove(leaving);
        WaitFor(() => staying.Snapshots.Any(s => s.Despawns.Contains(leavingId)), staying);
        // Run past one stats interval so the world writes its tick-health records.
        long statsTicks = WorldTelemetry.StatsIntervalTicks(_config.Telemetry, _config.StartingValues.TickRateHz);
        WaitFor(() => _server.World.ServerTick > statsTicks, staying);

        _server.Stop(); // the writer drains and closes the file last

        var records = Directory.GetFiles(_env.TelemetryDirectoryOf(_server), $"{_server.TelemetryRunId}-*.jsonl")
            .Order(StringComparer.Ordinal)
            .SelectMany(path => TelemetryJsonFormat.ReadFile(path).Records)
            .ToList();
        var sessions = records
            .Where(r => r.Kind is TelemetryKind.SessionStarted or TelemetryKind.SessionEnded)
            .Select(r => (r.Kind, r.AccountId, r.Subject, r.V0))
            .ToList();
        Assert.Equal(4, sessions.Count);
        Assert.Contains((TelemetryKind.SessionStarted, 7L, (long)leavingId, 0L), sessions);
        Assert.Contains((TelemetryKind.SessionStarted, 8L, (long)stayingId, 0L), sessions);
        Assert.Contains((TelemetryKind.SessionEnded, 7L, (long)leavingId, (long)SessionEndReason.Disconnected), sessions);
        Assert.Equal((TelemetryKind.SessionEnded, 8L, (long)stayingId, (long)SessionEndReason.ServerShutdown), sessions[^1]);
        Assert.All(records.Where(r => r.EventId != 0), r => Assert.Equal(Bootstrap.MainWorldId, r.WorldId));

        var tickStats = records.First(r => r.Kind == TelemetryKind.TickStats);
        Assert.Equal(Bootstrap.MainWorldId, tickStats.WorldId);
        Assert.InRange(tickStats.V1, tickStats.V0, long.MaxValue); // max >= last
        Assert.Contains(records, r => r.Kind == TelemetryKind.WorldCounters);
        Assert.DoesNotContain(records, r => r.Kind is TelemetryKind.WorldCrashed or TelemetryKind.QueueDropped);
    }

    private async Task<LoopbackGameClient> ConnectAsync(long accountId)
    {
        await _env.CreateCharacterAsync(accountId);
        var client = _env.Connect(_server, _config, accountId);
        _clients.Add(client);
        WaitFor(() => client.Connected, client);
        return client;
    }

    private void WaitFor(Func<bool> done, params LoopbackGameClient[] clients) => _env.WaitFor(done, clients);

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
}
