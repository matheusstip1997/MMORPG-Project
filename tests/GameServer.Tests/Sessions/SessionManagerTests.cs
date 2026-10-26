using GameServer.Events;
using GameServer.Persistence;
using GameServer.Sessions;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using static GameServer.Tests.Support.TestWorlds;

namespace GameServer.Tests.Sessions;

/// <summary>
/// Backlog B4, tick by tick with fakes: logins and the ownership lock (R-2.1-04, R-2.1-06), linger and reattach
/// (R-2.1-08), save classes (R-7.1-01), graceful shutdown (R-2.1-09).
/// </summary>
public class SessionManagerTests
{
    private const int LingerMs = 10_000;
    private const int InCombatLingerMs = 30_000;
    private const int VolatileMs = 10_000;
    private static readonly CharacterState Saved = new(5, 1_234, SessionManager.MainWorldKey, 300, -400, 50, 1_000, 80, 40, 90);

    [Fact]
    public void Login_spawns_the_slot_character_at_its_saved_position_and_takes_its_lock()
    {
        var h = new Harness();
        var record = h.World.Characters.Add(accountId: 5, slot: 2, Saved);

        var player = h.World.Join(peerId: 1, accountId: 5, slot: 2);

        // Height comes from the world grid at the saved position (flat test map: 0); the saved height is only a fallback.
        Assert.Equal((300, -400, 0, (ushort)1_000), (player.XCm, player.YCm, player.HeightCm, player.Movement.Facing));
        Assert.Equal(record.CharacterId, player.CharacterId);
        Assert.StartsWith("test-server:", h.World.Locks.HolderOf(record.CharacterId));
        Assert.Equal(record.Version, h.Sessions.Saves.VersionOf(record.CharacterId));
        Assert.Equal([new SessionStarted(5, player.Id, Reattached: false)], h.Events);
        Assert.Equal(record.CharacterId, h.Envelopes[0].ActorCharacterId);
    }

    [Fact]
    public void A_saved_world_other_than_the_main_map_falls_back_to_the_spawn()
    {
        // The fallback is the world's player spawn marker (backlog C2), at the grid's height there.
        var h = new Harness(map: TestMaps.Open(spawnXCm: 700, spawnYCm: 800, heightCm: 9));
        h.World.Characters.Add(5, 0, Saved with { WorldId = "instance-42" });

        var player = h.World.Join(1, 5);

        Assert.Equal((700, 800, 9), (player.XCm, player.YCm, player.HeightCm));
    }

    [Fact]
    public void A_missing_or_deleted_character_is_refused_with_CharacterUnavailable()
    {
        var h = new Harness();
        h.World.Characters.AutoCreate = false;
        var deleted = h.World.Characters.Add(6, 0, Saved);
        h.World.Characters.MarkDeleted(deleted.CharacterId);

        h.World.Connect(1, accountId: 5); // no character in the slot
        h.World.Connect(2, accountId: 6); // pending deletion (R-2.1-05)
        h.World.Ticks(1);

        Assert.Equal(
            [(1, ConnectRejectReason.CharacterUnavailable), (2, ConnectRejectReason.CharacterUnavailable)],
            h.World.Transport.Kicks);
        Assert.Empty(h.World.World.Entities);
        Assert.Equal(0, h.Sessions.HeldCount);
    }

    [Fact]
    public void A_second_login_replaces_the_first_and_reattaches_the_same_entity_without_reading_the_database()
    {
        var h = new Harness(values: TestSessions.Values(lingerOutOfCombatMs: LingerMs));
        var first = h.World.Join(1, accountId: 7);

        var second = h.World.Join(2, accountId: 7);

        Assert.Same(first, second);
        Assert.Equal((1, ConnectRejectReason.SessionReplaced), Assert.Single(h.World.Transport.Kicks));
        Assert.False(h.World.World.TryGetPlayer(1, out _));
        Assert.Equal(1, h.World.Characters.Loads);
        Assert.Equal(
            [
                new SessionStarted(7, first.Id, false),
                new SessionEnded(7, first.Id, SessionEndReason.Replaced),
                new SessionStarted(7, first.Id, true),
            ],
            h.Events);

        // The old connection's own disconnect arrives later and changes nothing.
        h.World.Leave(1);
        h.World.Ticks(1);
        Assert.True(h.World.World.TryGetPlayer(2, out var still));
        Assert.Same(first, still);
        Assert.Equal(1, h.Sessions.OnlineCount);
    }

    [Fact]
    public void A_login_to_another_character_of_the_account_leaves_the_first_one_lingering()
    {
        var h = new Harness(values: TestSessions.Values(lingerOutOfCombatMs: LingerMs));
        var first = h.World.Join(1, accountId: 7, slot: 0);

        var second = h.World.Join(2, accountId: 7, slot: 1);

        Assert.NotSame(first, second);
        Assert.Equal((1, ConnectRejectReason.SessionReplaced), Assert.Single(h.World.Transport.Kicks));
        Assert.True(h.Sessions.IsLingering(first.CharacterId)); // a relog cannot dodge combat (R-2.1-08)
        Assert.Equal(2, h.World.World.Entities.Count);
    }

    [Fact]
    public void A_disconnected_character_lingers_unresponsive_and_a_reconnect_in_the_window_reattaches_it()
    {
        var h = new Harness(values: TestSessions.Values(lingerOutOfCombatMs: LingerMs));
        var player = h.World.Join(1, accountId: 3);
        h.World.Leave(1);
        h.World.Ticks(1);

        Assert.Contains(player, h.World.World.Entities);
        Assert.False(player.IsConnected);
        Assert.True(h.Sessions.IsLingering(player.CharacterId));
        int x = player.XCm;
        h.World.SendInputs(1, Frame(1, MoveKeys.D)); // a late packet from the old connection moves nothing
        h.World.Ticks(3);
        Assert.Equal(x, player.XCm);
        Assert.Equal(new SessionEnded(3, player.Id, SessionEndReason.Disconnected), h.Events[^1]);

        h.World.Clock.Advance(LingerMs - 1);
        var again = h.World.Join(2, accountId: 3);

        Assert.Same(player, again);
        Assert.False(h.Sessions.IsLingering(player.CharacterId));
        Assert.Equal(new SessionStarted(3, player.Id, Reattached: true), h.Events[^1]);
        Assert.Equal(1, h.World.Characters.Loads);
        // The new client's first snapshot spawns everything it sees again, itself included.
        Assert.Contains(h.World.Transport.SnapshotsFor(2)[0].Spawns, s => s.EntityId == player.Id);
    }

    [Fact]
    public void Linger_expiry_saves_the_character_then_removes_it_and_releases_the_lock()
    {
        var h = new Harness(values: TestSessions.Values(lingerOutOfCombatMs: LingerMs));
        var player = h.World.Join(1, accountId: 3);
        long id = player.CharacterId;
        h.World.SendInputs(1, Frame(1, MoveKeys.D));
        h.World.Ticks(1);
        int movedX = player.XCm;
        Assert.NotEqual(0, movedX);
        h.World.Leave(1);
        h.World.Ticks(1);

        h.World.Clock.Advance(LingerMs);
        h.World.Ticks(1); // the logout save goes out
        var save = h.World.Saves.Jobs[^1];
        Assert.Equal((id, SaveReason.Logout, movedX), (save.CharacterId, save.Reason, save.State.PosXCm));
        Assert.Contains(player, h.World.World.Entities); // still there until the save is in
        h.World.Ticks(1); // the save completed: removed, release sent

        Assert.Empty(h.World.World.Entities);
        Assert.Null(h.World.Locks.HolderOf(id));
        h.World.Ticks(1); // the release completed
        Assert.Equal(0, h.Sessions.HeldCount);
        Assert.Equal(movedX, h.World.Characters.Get(id).State.PosXCm);

        // The next login reads what was saved.
        var back = h.World.Join(2, accountId: 3);
        Assert.NotSame(player, back);
        Assert.Equal(movedX, back.XCm);
    }

    [Fact]
    public void A_character_in_combat_lingers_for_the_in_combat_window()
    {
        var h = new Harness(values: TestSessions.Values(lingerOutOfCombatMs: LingerMs, lingerInCombatMs: InCombatLingerMs));
        var player = h.World.Join(1, accountId: 3);
        player.LastCombatAtMs = h.World.Clock.UtcNowMs;
        h.World.Leave(1);
        h.World.Ticks(1);

        h.World.Clock.Advance(LingerMs + 1);
        h.World.Ticks(2);
        Assert.Contains(player, h.World.World.Entities);

        h.World.Clock.Advance(InCombatLingerMs);
        h.World.Ticks(2);
        Assert.Empty(h.World.World.Entities);
    }

    [Theory]
    [InlineData(0L, 50_000L, LingerMs)] // never fought
    [InlineData(45_000L, 50_000L, InCombatLingerMs)] // 5 s ago, combat state lasts 10 s
    [InlineData(40_000L, 50_000L, LingerMs)] // exactly when the combat state ends
    public void Linger_window_depends_on_the_combat_state(long lastCombatAtMs, long nowMs, long expected)
    {
        var values = TestSessions.Values(lingerOutOfCombatMs: LingerMs, lingerInCombatMs: InCombatLingerMs, combatStateMs: 10_000);

        Assert.Equal(expected, LingerManager.WindowMs(values, lastCombatAtMs, nowMs));
    }

    [Fact]
    public void Position_is_saved_at_the_volatile_interval_only_when_it_changed()
    {
        var h = new Harness(values: TestSessions.Values(volatileMs: VolatileMs));
        var player = h.World.Join(1, accountId: 3);
        h.World.SendInputs(1, Frame(1, MoveKeys.W));
        h.World.Ticks(2);

        h.World.Clock.Advance(VolatileMs);
        h.World.Ticks(1);
        var save = Assert.Single(h.World.Saves.Jobs);
        Assert.Equal((SaveReason.VolatilePeriodic, player.YCm), (save.Reason, save.State.PosYCm));

        h.World.Clock.Advance(VolatileMs);
        h.World.Ticks(2);
        Assert.Single(h.World.Saves.Jobs); // nothing changed, nothing written
    }

    [Fact]
    public void A_standing_players_regenerated_meters_are_saved_at_the_volatile_interval_and_survive_a_reconnect()
    {
        // QA wave 5 G1: R-7.1-01 class 3 includes HP, MP and stamina, so regeneration while standing still is saved
        // (at most one volatile interval lost on a crash, R-7.1-02).
        var h = new Harness(values: TestSessions.Values(volatileMs: VolatileMs), combat: TestCombat.Rules());
        var record = h.World.Characters.Add(5, 0, Saved with { Hp = 10, Mp = 1, Stamina = 1 });
        var player = h.World.Join(1, accountId: 5);
        Assert.Equal(10, player.Combatant!.Hp(h.World.World.NowMs));

        for (int t = 0; t < VolatileMs / 50; t++) // 10 s of world time without an input
        {
            h.World.Clock.Advance(50);
            h.World.Ticks(1);
        }
        var meters = GameServer.Combat.CombatRules.Capture(player.Combatant, h.World.World.NowMs);
        Assert.True(meters.Hp > 10 && meters.Mp > 1 && meters.Stamina > 1, $"regenerated {meters}");

        var save = Assert.Single(h.World.Saves.Jobs);
        Assert.Equal(SaveReason.VolatilePeriodic, save.Reason);
        Assert.True(save.State.Hp > 10 && save.State.Mp > 1 && save.State.Stamina > 1, $"saved {save.State}");
        Assert.Equal((Saved.PosXCm, Saved.PosYCm), (save.State.PosXCm, save.State.PosYCm)); // it never moved

        // The character leaves (no linger) and comes back with what was saved, not the meters it logged in with.
        h.World.Leave(1);
        h.World.Ticks(3);
        Assert.Equal(0, h.Sessions.HeldCount);
        Assert.True(h.World.Characters.Get(record.CharacterId).State.Hp >= save.State.Hp);
        var back = h.World.Join(2, accountId: 5);
        Assert.NotSame(player, back);
        Assert.True(back.Combatant!.Hp(h.World.World.NowMs) >= save.State.Hp);
    }

    [Fact]
    public void Lock_and_database_calls_start_off_the_tick_thread()
    {
        // QA wave 5 G3, spec 1.6: an async call runs synchronously up to its first await, so a server starts them on the
        // thread pool (a cold Npgsql call took about 30 ms of the tick of a first login).
        var h = new Harness(inlineSessionOperations: false);
        h.World.Connect(1, accountId: 5);
        int tickThread = Environment.CurrentManagedThreadId;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        GameServer.Entities.Player? player = null;
        while (player == null && watch.ElapsedMilliseconds < 5_000)
        {
            h.World.Ticks(1);
            if (h.World.World.TryGetPlayer(1, out var found))
                player = found;
            else
                Thread.Sleep(1);
        }

        Assert.NotNull(player);
        Assert.NotEqual(0, h.World.Characters.FindThreadId);
        Assert.NotEqual(tickThread, h.World.Characters.FindThreadId);
    }

    [Fact]
    public void A_character_locked_by_another_server_is_refused()    {
        var h = new Harness();
        var record = h.World.Characters.Add(5, 0, Saved);
        h.World.Locks.Set(record.CharacterId, "other-server:1");

        h.World.Connect(1, accountId: 5);
        h.World.Ticks(1);

        Assert.Equal((1, ConnectRejectReason.CharacterUnavailable), Assert.Single(h.World.Transport.Kicks));
        Assert.Empty(h.World.World.Entities);
        Assert.Equal("other-server:1", h.World.Locks.HolderOf(record.CharacterId));
        Assert.Equal(0, h.Sessions.HeldCount);
    }

    [Fact]
    public void A_crash_while_loading_gives_the_lock_back_and_the_next_login_works()
    {
        var h = new Harness();
        var record = h.World.Characters.Add(5, 0, Saved);
        h.World.Characters.LoadFailure = new InvalidOperationException("database down");

        h.World.Connect(1, accountId: 5);
        h.World.Ticks(1);

        Assert.Equal((1, ConnectRejectReason.ServerUnavailable), Assert.Single(h.World.Transport.Kicks));
        Assert.Null(h.World.Locks.HolderOf(record.CharacterId));
        Assert.Equal(0, h.Sessions.HeldCount);

        h.World.Characters.LoadFailure = null;
        var player = h.World.Join(2, accountId: 5);
        Assert.Equal(record.CharacterId, player.CharacterId);
    }

    [Fact]
    public void Leaving_while_the_load_is_in_flight_releases_the_lock_when_it_returns()
    {
        var h = new Harness();
        var record = h.World.Characters.Add(5, 0, Saved);
        var gate = new TaskCompletionSource();
        h.World.Characters.LoadGate = gate;
        h.World.Connect(1, accountId: 5);
        h.World.Ticks(1);
        Assert.NotNull(h.World.Locks.HolderOf(record.CharacterId));

        h.World.Leave(1);
        h.World.Ticks(1);
        Open(gate);
        h.World.Ticks(2);

        Assert.Empty(h.World.World.Entities);
        Assert.Null(h.World.Locks.HolderOf(record.CharacterId));
        Assert.Equal(0, h.Sessions.HeldCount);
        Assert.Equal(0, h.Sessions.PendingOperations);
        Assert.Empty(h.Events);
    }

    [Fact]
    public void A_relog_while_the_first_load_is_in_flight_waits_for_it_and_gets_the_character()
    {
        var h = new Harness();
        var record = h.World.Characters.Add(4, 0, Saved);
        var gate = new TaskCompletionSource();
        h.World.Characters.LoadGate = gate;
        h.World.Connect(1, accountId: 4);
        h.World.Ticks(1);

        h.World.Connect(2, accountId: 4);
        h.World.Ticks(1);
        Open(gate);
        h.World.Ticks(1);

        Assert.Equal((1, ConnectRejectReason.SessionReplaced), Assert.Single(h.World.Transport.Kicks));
        Assert.True(h.World.World.TryGetPlayer(2, out var player));
        Assert.Equal(record.CharacterId, player.CharacterId);
        Assert.Equal(1, h.World.Characters.Loads);
    }

    [Fact]
    public void The_heartbeat_keeps_the_lock_alive_past_its_ttl()
    {
        var h = new Harness(sessionSettings: TestSessions.Settings(lockTtlMs: 3_000, heartbeatMs: 1_000));
        var player = h.World.Join(1, accountId: 5);

        for (int i = 0; i < 10; i++)
        {
            h.World.Clock.Advance(1_000);
            h.World.Ticks(1);
        }

        Assert.True(h.World.Locks.Refreshes >= 9);
        Assert.NotNull(h.World.Locks.HolderOf(player.CharacterId));
        Assert.Contains(player, h.World.World.Entities);
    }

    [Fact]
    public void A_lost_lock_removes_the_character_without_saving_and_kicks_the_client()
    {
        var h = new Harness(sessionSettings: TestSessions.Settings(lockTtlMs: 3_000, heartbeatMs: 1_000));
        var player = h.World.Join(1, accountId: 5);
        h.World.SendInputs(1, Frame(1, MoveKeys.D));
        h.World.Ticks(1);
        h.World.Locks.Set(player.CharacterId, "other-server:9"); // expired and taken over elsewhere

        h.World.Clock.Advance(1_000);
        h.World.Ticks(2); // the heartbeat's refresh, then its result

        Assert.Empty(h.World.World.Entities);
        Assert.Equal((1, ConnectRejectReason.CharacterUnavailable), Assert.Single(h.World.Transport.Kicks));
        Assert.Empty(h.World.Saves.Jobs);
        Assert.Equal("other-server:9", h.World.Locks.HolderOf(player.CharacterId));
        Assert.Equal(new SessionEnded(5, player.Id, SessionEndReason.OwnershipLost), h.Events[^1]);
        Assert.Equal(0, h.Sessions.HeldCount);
    }

    [Fact]
    public void Redis_unreachable_for_a_whole_ttl_gives_the_character_up()
    {
        var h = new Harness(sessionSettings: TestSessions.Settings(lockTtlMs: 3_000, heartbeatMs: 1_000));
        var player = h.World.Join(1, accountId: 5);
        h.World.Locks.Failure = new InvalidOperationException("redis down");

        h.World.Clock.Advance(2_000);
        h.World.Ticks(1);
        Assert.Contains(player, h.World.World.Entities);

        h.World.Clock.Advance(1_000);
        h.World.Ticks(1);
        Assert.Empty(h.World.World.Entities);
        Assert.Equal((1, ConnectRejectReason.CharacterUnavailable), Assert.Single(h.World.Transport.Kicks));
    }

    [Fact]
    public void A_save_conflict_means_another_writer_and_removes_the_character_without_saving_again()
    {
        var h = new Harness(values: TestSessions.Values(volatileMs: VolatileMs));
        var player = h.World.Join(1, accountId: 5);
        h.World.Saves.Outcome = SaveOutcome.VersionConflict;
        h.World.SendInputs(1, Frame(1, MoveKeys.D));
        h.World.Ticks(1);

        h.World.Clock.Advance(VolatileMs);
        h.World.Ticks(2);

        Assert.Empty(h.World.World.Entities);
        Assert.Single(h.World.Saves.Jobs);
        Assert.Equal((1, ConnectRejectReason.CharacterUnavailable), Assert.Single(h.World.Transport.Kicks));
        Assert.Equal(new SessionEnded(5, player.Id, SessionEndReason.OwnershipLost), h.Events[^1]);
    }

    [Fact]
    public void Shutdown_kicks_with_ServerMaintenance_saves_online_and_lingering_characters_and_releases_every_lock()
    {
        var h = new Harness(values: TestSessions.Values(lingerOutOfCombatMs: LingerMs));
        var online = h.World.Join(1, accountId: 1);
        var lingering = h.World.Join(2, accountId: 2);
        h.World.SendInputs(1, Frame(1, MoveKeys.D));
        h.World.SendInputs(2, Frame(1, MoveKeys.A));
        h.World.Ticks(1);
        h.World.Leave(2);
        h.World.Ticks(1);

        h.World.World.Shutdown();

        Assert.Equal((1, ConnectRejectReason.ServerMaintenance), Assert.Single(h.World.Transport.Kicks));
        Assert.Equal(
            [(online.CharacterId, SaveReason.Logout), (lingering.CharacterId, SaveReason.Logout)],
            h.World.Saves.Jobs.Select(j => (j.CharacterId, j.Reason)).Order());
        Assert.Equal(online.XCm, h.World.Characters.Get(online.CharacterId).State.PosXCm);
        Assert.Equal(lingering.XCm, h.World.Characters.Get(lingering.CharacterId).State.PosXCm);
        Assert.Null(h.World.Locks.HolderOf(online.CharacterId));
        Assert.Null(h.World.Locks.HolderOf(lingering.CharacterId));
        Assert.Empty(h.World.World.Entities);
        Assert.Equal(new SessionEnded(1, online.Id, SessionEndReason.ServerShutdown), h.Events[^1]);

        // Nothing logs in after the shutdown.
        h.World.Connect(3, accountId: 3);
        h.Sessions.OnConnected(3, 3, 0, new GameServer.Net.InputQueue(1));
        Assert.Equal((3, ConnectRejectReason.ServerMaintenance), h.World.Transport.Kicks[^1]);
    }

    /// <summary>
    /// Completes a held load so its continuations run inline, as they would on the thread pool: under xUnit's
    /// synchronization context .NET would queue them instead and the test would race the next tick.
    /// </summary>
    private static void Open(TaskCompletionSource gate)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            gate.SetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    /// <summary>A world harness with a bus whose session events are recorded.</summary>
    private sealed class Harness : IEventConsumer<SessionStarted>, IEventConsumer<SessionEnded>
    {
        public Harness(GameServer.World.WorldSettings? settings = null, StartingValuesM1? values = null,
            SessionSettings? sessionSettings = null, GameServer.World.Map.WorldMap? map = null,
            GameServer.Combat.CombatRules? combat = null, bool inlineSessionOperations = true)
        {
            var bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            bus.Subscribe<SessionStarted>(this);
            bus.Subscribe<SessionEnded>(this);
            World = new WorldHarness(settings, events: new EventDispatchHook(bus), bus: bus, map: map, values: values,
                sessionSettings: sessionSettings, combat: combat, inlineSessionOperations: inlineSessionOperations);
        }

        public WorldHarness World { get; }

        public SessionManager Sessions => World.Sessions;

        public List<IGameEvent> Events { get; } = [];

        public List<EventEnvelope> Envelopes { get; } = [];

        public void Handle(in EventEnvelope envelope, in SessionStarted payload)
        {
            Events.Add(payload);
            Envelopes.Add(envelope);
        }

        public void Handle(in EventEnvelope envelope, in SessionEnded payload)
        {
            Events.Add(payload);
            Envelopes.Add(envelope);
        }
    }
}
