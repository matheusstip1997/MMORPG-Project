using System.Diagnostics;
using GameServer.Host;
using GameServer.Tests.Support;
using Shared.Protocol;
using StackExchange.Redis;

namespace GameServer.Tests.Sessions;

/// <summary>
/// Backlog B4 over localhost: real Bootstrap servers, LiteNetLib clients with dev tickets, Redis locks and PostgreSQL
/// characters. Waits use the real clock, so linger windows here are short.
/// </summary>
public sealed class SessionsEndToEndTests
{
    [Fact]
    public async Task A_second_login_kicks_the_first_with_SessionReplaced_and_takes_over_the_same_entity()
    {
        await using var env = await ServerTestEnvironment.CreateAsync();
        var config = Config(lingerMs: 10_000);
        var server = env.StartServer(config);
        await env.CreateCharacterAsync(accountId: 1);

        using var first = env.Connect(server, config, accountId: 1);
        env.WaitFor(() => first.OwnEntityId != null, first);
        using var second = env.Connect(server, config, accountId: 1);
        env.WaitFor(() => first.Disconnected && second.OwnEntityId != null, first, second);

        Assert.Equal(ConnectRejectReason.SessionReplaced, first.ServerReason); // R-2.1-04
        Assert.Equal(first.OwnEntityId, second.OwnEntityId); // same entity (R-2.1-08)
    }

    [Fact]
    public async Task Two_servers_cannot_own_the_same_character_and_the_second_gets_it_with_its_saved_position_once_released()
    {
        await using var env = await ServerTestEnvironment.CreateAsync();
        var config = Config(lingerMs: 300);
        var serverA = env.StartServer(config);
        var serverB = env.StartServer(config);
        var character = await env.CreateCharacterAsync(accountId: 2, xCm: 100, yCm: 200);

        using var onA = env.Connect(serverA, config, accountId: 2);
        env.WaitFor(() => onA.OwnEntityId != null, onA);
        using var onB = env.Connect(serverB, config, accountId: 2);
        env.WaitFor(() => onB.Disconnected, onA, onB);

        Assert.Equal(ConnectRejectReason.CharacterUnavailable, onB.ServerReason); // R-2.1-06
        Assert.StartsWith(serverA.ServerInstanceId, env.LockHolder(character.CharacterId));

        HoldKeys(env, config, onA, MoveKeys.W, TimeSpan.FromMilliseconds(500));
        env.WaitFor(() => onA.Snapshots[^1].Header.LastInputSequence == onA.LastSequence, onA);
        int movedY = onA.Snapshots[^1].Self.ZCm; // snapshots send the ground-plane Y as Z
        Assert.True(movedY > 200);
        onA.Dispose();
        env.WaitFor(() => env.LockHolder(character.CharacterId) == null); // linger over, saved, released

        using var again = env.Connect(serverB, config, accountId: 2);
        env.WaitFor(() => again.OwnEntityId != null, again);
        Assert.Equal((100, movedY), (again.Snapshots[^1].Self.XCm, again.Snapshots[^1].Self.ZCm));
        Assert.StartsWith(serverB.ServerInstanceId, env.LockHolder(character.CharacterId));
    }

    [Fact]
    public async Task A_reconnect_within_the_linger_window_reattaches_to_the_entity_others_still_see()
    {
        await using var env = await ServerTestEnvironment.CreateAsync();
        var config = Config(lingerMs: 5_000);
        var server = env.StartServer(config);
        await env.CreateCharacterAsync(accountId: 3);
        await env.CreateCharacterAsync(accountId: 4);

        using var watcher = env.Connect(server, config, accountId: 4);
        var leaving = env.Connect(server, config, accountId: 3);
        env.WaitFor(() => watcher.OwnEntityId != null && leaving.OwnEntityId != null && watcher.WithoutNpcs(watcher.Snapshots[^1]).Count == 2,
            watcher, leaving);
        uint entityId = leaving.OwnEntityId!.Value;

        leaving.Dispose();
        var watch = Stopwatch.StartNew();
        env.WaitFor(() => watch.ElapsedMilliseconds > 500, watcher);
        Assert.Contains(watcher.Snapshots[^1].Entities, e => e.EntityId == entityId); // lingering, still in the world

        using var back = env.Connect(server, config, accountId: 3);
        env.WaitFor(() => back.OwnEntityId != null, back, watcher);

        Assert.Equal(entityId, back.OwnEntityId);
        Assert.DoesNotContain(watcher.Snapshots, s => s.Despawns.Contains(entityId));
    }

    [Fact]
    public async Task Linger_expiry_saves_the_character_and_releases_its_lock()
    {
        await using var env = await ServerTestEnvironment.CreateAsync();
        var config = Config(lingerMs: 300);
        var server = env.StartServer(config);
        var character = await env.CreateCharacterAsync(accountId: 5);

        var client = env.Connect(server, config, accountId: 5);
        env.WaitFor(() => client.OwnEntityId != null, client);
        Assert.NotNull(env.LockHolder(character.CharacterId));
        HoldKeys(env, config, client, MoveKeys.D, TimeSpan.FromMilliseconds(500));
        env.WaitFor(() => client.Snapshots[^1].Header.LastInputSequence == client.LastSequence, client);
        int movedX = client.Snapshots[^1].Self.XCm;
        client.Dispose();

        env.WaitFor(() => env.LockHolder(character.CharacterId) == null);

        var saved = await env.Characters.LoadAsync(character.CharacterId);
        Assert.Equal(movedX, saved!.State.PosXCm);
        Assert.True(saved.Version > character.Version);
    }

    [Fact]
    public async Task A_lock_left_by_a_crashed_server_refuses_the_login_until_its_ttl_runs_out()
    {
        await using var env = await ServerTestEnvironment.CreateAsync();
        var config = Config(lingerMs: 0);
        var server = env.StartServer(config);
        var character = await env.CreateCharacterAsync(accountId: 6);
        await env.Redis.GetDatabase().StringSetAsync(env.LockKey(character.CharacterId), "crashed:1", TimeSpan.FromMilliseconds(1_500));

        using var refused = env.Connect(server, config, accountId: 6);
        env.WaitFor(() => refused.Disconnected, refused);
        Assert.Equal(ConnectRejectReason.CharacterUnavailable, refused.ServerReason);

        env.WaitFor(() => env.LockHolder(character.CharacterId) == null);
        using var admitted = env.Connect(server, config, accountId: 6);
        env.WaitFor(() => admitted.OwnEntityId != null, admitted);
    }

    [Fact]
    public async Task A_database_that_cannot_be_reached_refuses_the_login_with_ServerUnavailable_and_holds_no_lock()
    {
        await using var env = await ServerTestEnvironment.CreateAsync();
        var config = Config(lingerMs: 0);
        var server = env.StartServer(config,
            realmDatabase: "Host=127.0.0.1;Port=1;Username=nobody;Password=none;Database=none;Timeout=2");

        using var client = env.Connect(server, config, accountId: 7);
        env.WaitFor(() => client.Disconnected, client);

        Assert.Equal(ConnectRejectReason.ServerUnavailable, client.ServerReason);
        var server0 = env.Redis.GetServer(env.Redis.GetEndPoints()[0]);
        Assert.Empty(server0.Keys(pattern: env.Prefix + "realm:*"));
    }

    [Fact]
    public async Task Graceful_shutdown_kicks_with_ServerMaintenance_saves_and_releases()
    {
        await using var env = await ServerTestEnvironment.CreateAsync();
        var config = Config(lingerMs: 10_000);
        var server = env.StartServer(config);
        var character = await env.CreateCharacterAsync(accountId: 8);

        using var client = env.Connect(server, config, accountId: 8);
        env.WaitFor(() => client.OwnEntityId != null, client);
        HoldKeys(env, config, client, MoveKeys.S, TimeSpan.FromMilliseconds(500));
        env.WaitFor(() => client.Snapshots[^1].Header.LastInputSequence == client.LastSequence, client);
        int movedY = client.Snapshots[^1].Self.ZCm;

        server.Stop();
        env.WaitFor(() => client.Disconnected, client);

        Assert.Equal(ConnectRejectReason.ServerMaintenance, client.ServerReason); // R-2.1-09
        Assert.Null(env.LockHolder(character.CharacterId));
        var saved = await env.Characters.LoadAsync(character.CharacterId);
        Assert.Equal(movedY, saved!.State.PosYCm);
    }

    private static ServerConfig Config(int lingerMs)
    {
        var config = ServerTestEnvironment.LoadConfig();
        config.StartingValues.LingerOutOfCombatMs = lingerMs;
        config.StartingValues.LingerInCombatMs = lingerMs;
        return config;
    }

    /// <summary>Sends input frames with <paramref name="keys"/> at the client tick rate for <paramref name="duration"/>.</summary>
    private static void HoldKeys(ServerTestEnvironment env, ServerConfig config, LoopbackGameClient client, MoveKeys keys, TimeSpan duration)
    {
        long periodMs = 1_000 / config.StartingValues.TickRateHz;
        var watch = Stopwatch.StartNew();
        long next = 0;
        env.WaitFor(() =>
        {
            if (watch.ElapsedMilliseconds >= next)
            {
                client.SendFrame(keys, facing: 0, config.StartingValues.InputRedundancy);
                next += periodMs;
            }
            return watch.Elapsed >= duration;
        }, client);
    }
}
