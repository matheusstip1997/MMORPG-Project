using System.Collections.Concurrent;
using System.Diagnostics;
using GameServer.Admin;
using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Net;
using GameServer.Persistence;
using GameServer.World;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Util;

namespace GameServer.Sessions;

/// <summary>
/// Sessions of one world (spec 2.1, backlog B4): logins, the ownership lock, one session per account, linger and
/// reattach, the save classes of online characters, and graceful shutdown.
/// <para>
/// Threading: everything here runs on the world's simulation thread, like <see cref="SaveScheduler"/>. Redis and
/// PostgreSQL calls run as tasks whose results come back through a completion queue drained at the start of
/// <see cref="Tick"/> (phase 1), so the tick never waits on I/O (spec 1.6) and every decision (reattach versus
/// expiry, replace versus load) is taken on one thread in command order.
/// </para>
/// <para>
/// A login: the ticket was validated before the peer was accepted (R-2.5-01). Then the character in the ticket's slot
/// is resolved, its ownership lock is taken (R-2.1-06), the row is read again after the lock (the latest save) and
/// the character spawns at its saved position, or reattaches to its entity if it is still in this world (R-2.1-08).
/// A login for an account that already has a session replaces it (R-2.1-04): the old client is kicked with
/// <see cref="ConnectRejectReason.SessionReplaced"/> and its character lingers as after a disconnect, so a relog
/// cannot dodge combat.
/// </para>
/// <para>
/// A character this world holds is in one of three states: acquiring (lock and load in flight), held (an entity in
/// the world, online or lingering, maybe closing: final save in flight), releasing (entity gone, lock release in
/// flight). A login that finds it acquiring or releasing waits for that to finish.
/// </para>
/// TODO(B4-queue): login queue when the world is at its cap (R-2.1-09). TODO(D): logout request, blocked in combat
/// (R-2.1-07). TODO(WorldManager): sessions across worlds and servers (cross-server replace needs pub/sub);
/// TODO(H, law): pending jail on login (spec 2.1 flow).
/// </summary>
public sealed class SessionManager : ICharacterStateSource
{
    /// <summary>The <c>world_id</c> of the main map in character rows. TODO(WorldManager): instance ids.</summary>
    public const string MainWorldKey = "main";

    private readonly GameWorld _world;
    private readonly ICharacterSource _characters;
    private readonly IOwnershipLocks _locks;
    private readonly ITransport _transport;
    private readonly IClock _clock;
    private readonly SessionSettings _settings;
    private readonly string _serverId;
    private readonly LingerManager _linger;
    private readonly SaveScheduler _saves;
    private readonly bool _runOperationsInline;
    private readonly ConcurrentQueue<Completion> _completions = new();
    private readonly Dictionary<int, Session> _byPeer = new();
    private readonly Dictionary<long, Session> _byAccount = new();
    private readonly Dictionary<long, Hold> _holds = new();
    private readonly List<Hold> _holdOrder = new(); // by character id (architecture rule 6)
    private readonly List<long> _expired = new();
    private readonly List<long> _faulted = new();
    private long _nextHeartbeatMs;
    private long _holdSequence;
    private int _pendingOperations;
    private bool _shutDown;

    /// <param name="serverId">Names this process in lock tokens; unique per run.</param>
    /// <param name="runOperationsInline">Tests with completed fakes: start lock and database calls on the calling thread,
    /// so their completions are there for the same tick. Servers start them on the thread pool.</param>
    public SessionManager(GameWorld world, ICharacterSource characters, IOwnershipLocks locks, ISaveJobSink saveJobs,
        ISaveCompletionSource saveCompletions, ITransport transport, IClock clock, StartingValuesM1 values,
        SessionSettings settings, string serverId, bool runOperationsInline = false)
    {
        _runOperationsInline = runOperationsInline;
        _world = world;
        _characters = characters;
        _locks = locks;
        _transport = transport;
        _clock = clock;
        _settings = settings;
        _serverId = serverId;
        _linger = new LingerManager(values);
        // Inventory changes (backlog G, class 2) ride on this world's character saves (R-7.1-01).
        _saves = new SaveScheduler(values, clock, this, saveJobs, saveCompletions, (id, _) => _faulted.Add(id), world.Inventory,
            world.Progression);
        if (world.Inventory is { } inventory)
        {
            inventory.MarkClass2 = MarkClass2;
            inventory.RequestClass1 = RequestClass1; // WP-M2-4: outpost sales and vendor purchases (R-7.1-01)
        }
        // Progression (WP-M2-3, spec 3.6) rides on the same saves; level-ups and rank-ups ask for one at once (R-7.1-05).
        if (world.Progression is { } progression)
        {
            progression.MarkClass2 = MarkClass2;
            progression.RequestSave = RequestSave;
        }
        _nextHeartbeatMs = clock.UtcNowMs + settings.HeartbeatIntervalMs;
        world.AttachSessions(this);
    }

    /// <summary>Sessions bound to a character in the world.</summary>
    public int OnlineCount => _byPeer.Values.Count(s => s.Online);

    public int LingeringCount => _linger.Count;

    /// <summary>Characters this world holds the lock for, or is acquiring or releasing.</summary>
    public int HeldCount => _holds.Count;

    /// <summary>Lock and database calls started and not yet applied.</summary>
    public int PendingOperations => _pendingOperations;

    public SaveScheduler Saves => _saves;

    public bool Holds(long characterId) => _holds.ContainsKey(characterId);

    public bool IsLingering(long characterId) => _linger.IsLingering(characterId);

    /// <summary>The entity of a held character, online or lingering.</summary>
    public bool TryGetPlayer(long characterId, out Player player)
    {
        player = null!;
        if (!_holds.TryGetValue(characterId, out var hold) || hold.Player == null)
            return false;
        player = hold.Player;
        return true;
    }

    /// <summary>A peer was accepted with a valid ticket (phase 1 command).</summary>
    public void OnConnected(int peerId, long accountId, int slot, InputQueue inputs)
    {
        // A peer id is reused only after its Leave, which was posted first; this guards against a lost Leave.
        OnDisconnected(peerId);
        if (_shutDown)
        {
            _transport.Disconnect(peerId, ConnectRejectReason.ServerMaintenance);
            return;
        }

        if (_byAccount.TryGetValue(accountId, out var previous))
        {
            // R-2.1-04: the new login wins. The old character lingers like after a disconnect.
            _transport.Disconnect(previous.PeerId, ConnectRejectReason.SessionReplaced);
            End(previous, SessionEndReason.Replaced);
        }

        var session = new Session(peerId, accountId, slot, inputs);
        _byPeer.Add(peerId, session);
        _byAccount.Add(accountId, session);
        Start(() => FindAsync(session));
    }

    /// <summary>The peer's connection ended (phase 1 command). Unknown peers (already replaced or refused) are ignored.</summary>
    public void OnDisconnected(int peerId)
    {
        if (_byPeer.TryGetValue(peerId, out var session))
            End(session, SessionEndReason.Disconnected);
    }

    /// <summary>
    /// An admin kick (spec 7.4, phase 1 command): the character's client is disconnected with
    /// <see cref="ConnectRejectReason.Kicked"/> and its session ends like a disconnect, so the character lingers
    /// (R-2.1-08; user decision 2026-10-26) and is then saved and released as usual. A character that is lingering
    /// already, or that this world does not hold in the world, is left as it is.
    /// </summary>
    public AdminOutcome AdminKick(long characterId)
    {
        if (!_holds.TryGetValue(characterId, out var hold) || hold.State != HoldState.Held || hold.Player == null)
            return AdminOutcome.NotOnline;
        var session = hold.Session;
        if (session == null || !session.Online)
            return AdminOutcome.NotConnected;
        _transport.Disconnect(session.PeerId, ConnectRejectReason.Kicked);
        End(session, SessionEndReason.Kicked);
        return AdminOutcome.Kicked;
    }

    /// <summary>Once per tick, in phase 1 after the commands.</summary>
    public void Tick()
    {
        DrainCompletions();
        var now = _clock.UtcNowMs;

        // A closing character's logout save is its last one (spec 2.1 B4, R-7.1-01): meters that regenerate while that save
        // is in flight must not mark it again, or removal and the lock release wait for the next volatile save. A reattach
        // clears Closing, so marking resumes.
        foreach (var hold in _holdOrder)
        {
            if (hold.State == HoldState.Held && !hold.Closing)
                MarkIfChanged(hold);
        }

        _linger.TakeExpired(now, _expired);
        foreach (var id in _expired)
        {
            if (_holds.TryGetValue(id, out var hold) && hold.State == HoldState.Held && hold.Session == null)
                BeginClose(hold);
        }
        _expired.Clear();

        _saves.Tick();
        HandleSaveFaults();

        foreach (var hold in _holdOrder)
        {
            if (hold.State == HoldState.Held && hold.Closing)
                TryFinishClose(hold);
        }

        if (now >= _nextHeartbeatMs)
        {
            _nextHeartbeatMs = now + _settings.HeartbeatIntervalMs;
            Heartbeat(now);
        }
    }

    /// <summary>
    /// Graceful shutdown (R-2.1-09), after the simulation thread stopped: logins were already blocked at the transport.
    /// Ends every session (kicked with ServerMaintenance), saves every character, waiting up to
    /// <c>SessionSettings.ShutdownSaveTimeoutMs</c>, then releases every lock. Blocks the calling thread.
    /// </summary>
    public void Shutdown()
    {
        if (_shutDown)
            return;
        _shutDown = true;

        // Logins in flight finish first; with _shutDown set they release their lock instead of spawning.
        WaitForOperations(_settings.ShutdownSaveTimeoutMs);

        var sessions = _byPeer.Values
            .OrderBy(s => s.Online ? s.Hold!.Player!.Id : uint.MaxValue)
            .ThenBy(s => s.PeerId)
            .ToList();
        foreach (var session in sessions)
        {
            _transport.Disconnect(session.PeerId, ConnectRejectReason.ServerMaintenance);
            End(session, SessionEndReason.ServerShutdown);
        }

        var held = _holdOrder.Where(h => h.State == HoldState.Held).ToList();
        foreach (var hold in held)
        {
            _linger.Cancel(hold.CharacterId);
            MarkIfChanged(hold);
            if (_saves.IsTracked(hold.CharacterId) && !_saves.IsIdle(hold.CharacterId))
                _saves.RequestSave(hold.CharacterId, SaveReason.Logout);
        }

        // The tick has stopped, so this thread drives the scheduler until every save is in or the time is up.
        var watch = Stopwatch.StartNew();
        while (held.Any(h => _saves.IsTracked(h.CharacterId) && !_saves.IsIdle(h.CharacterId) && !_saves.IsFaulted(h.CharacterId))
               && watch.ElapsedMilliseconds < _settings.ShutdownSaveTimeoutMs)
        {
            _saves.Tick();
            Thread.Sleep(5);
        }
        var unsaved = held.Count(h => _saves.IsTracked(h.CharacterId) && !_saves.IsIdle(h.CharacterId));
        if (unsaved > 0)
            Console.Error.WriteLine($"Shutdown: {unsaved} character(s) not saved within {_settings.ShutdownSaveTimeoutMs} ms.");

        foreach (var hold in held)
        {
            _saves.Forget(hold.CharacterId);
            _world.RemovePlayer(hold.Player!);
            Release(hold);
        }
        WaitForOperations(_settings.ShutdownSaveTimeoutMs);
    }

    /// <summary>An inventory change of a held character waits for the next class 2 batch (R-7.1-01).</summary>
    private void MarkClass2(long characterId)
    {
        if (_saves.IsTracked(characterId))
            _saves.MarkDirty(characterId, DirtyFlags.Class2);
    }

    /// <summary>
    /// A class 1 operation (R-7.1-01) changed the character: save it now, not on the class 2 cadence. The save carries the
    /// pending class 2 changes in the same transaction (R-7.1-02).
    /// </summary>
    private void RequestClass1(long characterId)
    {
        if (_saves.IsTracked(characterId))
            _saves.RequestSave(characterId, SaveReason.Class1);
    }

    /// <summary>A level-up or rank-up of a held character saves at the next tick (R-7.1-05).</summary>
    private void RequestSave(long characterId, SaveReason reason)
    {
        if (_saves.IsTracked(characterId))
            _saves.RequestSave(characterId, reason);
    }

    /// <summary>The character's state for a save (<see cref="ICharacterStateSource"/>), world thread only.</summary>
    public CharacterState Capture(long characterId)
    {
        var hold = _holds[characterId];
        var player = hold.Player ?? throw new InvalidOperationException($"character {characterId} has no entity");
        // Position: MovementSim ground X and Y in pos_x_cm / pos_y_cm, the height in pos_z_cm, facing as the binary angle.
        // HP, MP and stamina from the combatant's meters (without combat rules: the loaded values).
        // Level, exp, rank and skill points from the progression store (WP-M2-3); the loaded values without one.
        var state = hold.Record!.State with
        {
            WorldId = MainWorldKey,
            PosXCm = player.XCm,
            PosYCm = player.YCm,
            PosZCm = player.HeightCm,
            Facing = player.Movement.Facing,
        };
        if (_world.Progression is { } progression && progression.TryGetSnapshot(characterId, out var progress))
            state = state with { Level = progress.Level, Exp = progress.Exp, Rank = progress.Rank, SkillPoints = progress.SkillPoints };
        if (player.Combatant == null)
            return state;
        var meters = CombatRules.Capture(player.Combatant, _world.NowMs);
        return state with { Hp = meters.Hp, Mp = meters.Mp, Stamina = meters.Stamina };
    }

    /// <summary>
    /// Where a character without a usable saved position appears (new character, or its saved world is not this one).
    /// It is the exported player spawn marker (<c>ServerLoop.PlayerSpawnMarkerId</c>, backlog C2).
    /// </summary>
    private (MovementState Movement, int HeightCm) FallbackSpawn() => _world.DefaultSpawn();

    /// <summary>Its saved position on the main map, or the fallback spawn when it was never placed or is elsewhere.</summary>
    private (MovementState Movement, int HeightCm) PlacementFor(in CharacterState state)
    {
        if (state.WorldId != MainWorldKey) // includes CharacterState.NotPlacedWorldId (a new character)
            return FallbackSpawn();
        return (new MovementState(FixVec2.FromCm(state.PosXCm, state.PosYCm), (ushort)state.Facing), state.PosZCm);
    }

    private void End(Session session, SessionEndReason reason)
    {
        if (session.Ended)
            return;
        session.Ended = true;
        if (_byPeer.TryGetValue(session.PeerId, out var byPeer) && byPeer == session)
            _byPeer.Remove(session.PeerId);
        if (_byAccount.TryGetValue(session.AccountId, out var byAccount) && byAccount == session)
            _byAccount.Remove(session.AccountId);

        var hold = session.Hold;
        session.Hold = null;
        if (hold == null || hold.Session != session)
            return; // still resolving its character
        hold.Session = null;
        if (!session.Online)
            return; // was waiting for an acquisition or release; that completion now finds nobody waiting
        session.Online = false;

        var player = hold.Player!;
        _world.UnbindPeer(player);
        _world.Events?.Publish(new SessionEnded(session.AccountId, player.Id, reason), hold.CharacterId);
        if (reason is SessionEndReason.ServerShutdown or SessionEndReason.OwnershipLost)
            return; // the caller removes the character

        // R-2.1-08: the character stays in the world, unresponsive, for the linger window.
        if (_linger.Start(hold.CharacterId, player.LastCombatAtMs, _clock.UtcNowMs) == 0)
            BeginClose(hold);
    }

    private void Refuse(Session session, ConnectRejectReason reason)
    {
        _transport.Disconnect(session.PeerId, reason);
        End(session, SessionEndReason.Disconnected);
    }

    private void Attach(Hold hold, Session session, bool reattached)
    {
        hold.Session = session;
        session.Hold = hold;
        session.Online = true;
        hold.Closing = false; // a reattach cancels a removal in progress; its save simply completes
        _linger.Cancel(hold.CharacterId);
        var player = hold.Player!;
        _world.BindPeer(player, session.PeerId, session.Inputs);
        _world.Events?.Publish(new SessionStarted(session.AccountId, player.Id, reattached), hold.CharacterId);
    }

    /// <summary>Linger over (or none): the logout save (R-7.1-01 class 3), then removal and lock release.</summary>
    private void BeginClose(Hold hold)
    {
        hold.Closing = true;
        MarkIfChanged(hold);
        if (!_saves.IsIdle(hold.CharacterId))
            _saves.RequestSave(hold.CharacterId, SaveReason.Logout);
        TryFinishClose(hold);
    }

    private void TryFinishClose(Hold hold)
    {
        // Idle means nothing pending or in flight: the database has the character's last state.
        if (_saves.IsFaulted(hold.CharacterId) || !_saves.TryUntrack(hold.CharacterId))
            return;
        _world.RemovePlayer(hold.Player!);
        Release(hold);
    }

    /// <summary>
    /// Volatile state (R-7.1-01 class 3) is dirty when what a save writes of it changed since the last mark: the
    /// position or facing, or the HP, MP and stamina meters (regeneration, hits, costs), so a player standing still loses
    /// at most one volatile interval of them on a crash (R-7.1-02).
    /// TODO(D6): cooldowns and active effects have no column in schema v1; they join this check with their columns.
    /// </summary>
    private void MarkIfChanged(Hold hold)
    {
        var player = hold.Player!;
        var position = (player.XCm, player.YCm, player.HeightCm, player.Movement.Facing);
        var meters = player.Combatant == null ? default : CombatRules.Capture(player.Combatant, _world.NowMs);
        if ((position == hold.LastMarkedPosition && meters == hold.LastMarkedMeters) || !_saves.IsTracked(hold.CharacterId))
            return;
        hold.LastMarkedPosition = position;
        hold.LastMarkedMeters = meters;
        _saves.MarkDirty(hold.CharacterId, DirtyFlags.Volatile);
    }

    /// <summary>
    /// A version conflict, missing or deleted row means another writer exists (R-7.1-02): the character goes without
    /// another save.
    /// </summary>
    private void HandleSaveFaults()
    {
        foreach (var id in _faulted)
        {
            if (_holds.TryGetValue(id, out var hold) && hold.State == HoldState.Held)
                LoseOwnership(hold);
        }
        _faulted.Clear();
    }

    /// <summary>
    /// This world no longer owns the character (R-2.1-06): its lock expired or was taken, or a save showed another
    /// writer. It is removed without saving, so it can never overwrite the new owner.
    /// </summary>
    private void LoseOwnership(Hold hold)
    {
        if (hold.Session is { } session)
        {
            _transport.Disconnect(session.PeerId, ConnectRejectReason.CharacterUnavailable);
            End(session, SessionEndReason.OwnershipLost);
        }
        _linger.Cancel(hold.CharacterId);
        _saves.Forget(hold.CharacterId);
        _world.RemovePlayer(hold.Player!);
        Release(hold); // compare-and-delete: removes the lock only if it is somehow still ours
    }

    private void Heartbeat(long now)
    {
        foreach (var hold in _holdOrder)
        {
            if (hold.State != HoldState.Held)
                continue;
            if (hold.RefreshInFlight)
                continue;
            hold.RefreshInFlight = true;
            var token = hold.Token;
            Start(() => RefreshAsync(hold, token));
        }

        // A refresh that cannot get through for a whole TTL means the lock may be gone (R-2.1-06).
        for (int i = _holdOrder.Count - 1; i >= 0; i--)
        {
            var hold = _holdOrder[i];
            if (hold.State == HoldState.Held && now - hold.LastConfirmedMs >= _settings.OwnershipLockTtlMs)
                LoseOwnership(hold);
        }
    }

    private void StartAcquire(Hold hold)
    {
        hold.State = HoldState.Acquiring;
        hold.Token = OwnershipLockToken.Create(_serverId, ++_holdSequence);
        var token = hold.Token;
        Start(() => AcquireAsync(hold, token));
    }

    private void Release(Hold hold)
    {
        hold.State = HoldState.Releasing;
        hold.Player = null;
        hold.Closing = false;
        hold.RefreshInFlight = false;
        var token = hold.Token;
        Start(() => ReleaseAsync(hold, token));
    }

    private Hold AddHold(long characterId)
    {
        var hold = new Hold(characterId);
        _holds.Add(characterId, hold);
        var index = _holdOrder.Count;
        while (index > 0 && _holdOrder[index - 1].CharacterId > characterId)
            index--;
        _holdOrder.Insert(index, hold);
        return hold;
    }

    private void RemoveHold(Hold hold)
    {
        _holds.Remove(hold.CharacterId);
        _holdOrder.Remove(hold);
    }

    // ---- Completions (world thread) ----

    private void DrainCompletions()
    {
        while (_completions.TryDequeue(out var completion))
        {
            _pendingOperations--;
            switch (completion.Kind)
            {
                case CompletionKind.Found:
                    OnFound(completion);
                    break;
                case CompletionKind.Acquired:
                    OnAcquired(completion);
                    break;
                case CompletionKind.Refreshed:
                    OnRefreshed(completion);
                    break;
                case CompletionKind.Released:
                    OnReleased(completion.Hold!);
                    break;
            }
        }
    }

    private void OnFound(Completion completion)
    {
        var session = completion.Session!;
        if (session.Ended)
            return;
        if (_shutDown)
        {
            Refuse(session, ConnectRejectReason.ServerMaintenance);
            return;
        }
        if (completion.Error != null)
        {
            Refuse(session, ConnectRejectReason.ServerUnavailable);
            return;
        }
        var record = completion.Record;
        if (record == null || record.IsPendingDeletion)
        {
            Refuse(session, ConnectRejectReason.CharacterUnavailable);
            return;
        }

        if (_holds.TryGetValue(record.CharacterId, out var hold))
        {
            if (hold.Session is { } other && other != session)
            {
                _transport.Disconnect(other.PeerId, ConnectRejectReason.SessionReplaced);
                End(other, SessionEndReason.Replaced);
            }
            session.Hold = hold;
            hold.Session = session;
            if (hold.State == HoldState.Held)
                Attach(hold, session, reattached: true); // R-2.1-08: same entity, no database read
            return; // acquiring or releasing: that completion serves this session
        }

        hold = AddHold(record.CharacterId);
        session.Hold = hold;
        hold.Session = session;
        StartAcquire(hold);
    }

    private void OnAcquired(Completion completion)
    {
        var hold = completion.Hold!;
        if (hold.Token != completion.Token)
            return;
        var waiting = hold.Session;

        switch (completion.Result)
        {
            case AcquireResult.Busy:
                // Loaded on another server (R-2.1-06), or a crashed server's lock that has not expired yet.
                RemoveHold(hold);
                if (waiting != null)
                    Refuse(waiting, ConnectRejectReason.CharacterUnavailable);
                return;
            case AcquireResult.Failed:
                RemoveHold(hold); // the task already gave the lock back, or it expires with its TTL
                if (waiting != null)
                    Refuse(waiting, ConnectRejectReason.ServerUnavailable);
                return;
        }

        var record = completion.Record;
        if (record == null || record.IsPendingDeletion || waiting == null || _shutDown)
        {
            if (waiting != null)
                Refuse(waiting, _shutDown ? ConnectRejectReason.ServerMaintenance : ConnectRejectReason.CharacterUnavailable);
            Release(hold);
            return;
        }

        // The player's combatant comes from its class (backlog D5 wiring). A class_id the tables do not know (a row written
        // before a class was removed) cannot be built: refused like an unplayable character.
        ClassDef? classDef = null;
        if (_world.Combat is { } combat && !combat.TryGetClass(record.ClassId, out classDef))
        {
            Refuse(waiting, ConnectRejectReason.CharacterUnavailable);
            Release(hold);
            return;
        }

        // Backlog G: the containers as saved (instances WP-M2-1). A row the item tables cannot represent (an item removed from
        // the tables) makes the character unplayable, like an unknown class. The class and level gate what it may equip.
        var items = completion.Items ?? InventoryLoad.Empty;
        if (_world.Inventory is { } inventory && !inventory.Attach(record.CharacterId, items, record.ClassId, record.State.Level))
        {
            Refuse(waiting, ConnectRejectReason.CharacterUnavailable);
            Release(hold);
            return;
        }
        _world.Inventory?.AttachGold(record.CharacterId, record.Gold); // WP-M2-4: gold rides on the character row

        // WP-M2-3: level, exp, rank and the achievement counters as saved; before the spawn, which reads the rank.
        _world.Progression?.Attach(record.CharacterId, record.State, completion.Progression ?? ProgressionRecord.Empty);

        hold.Record = record;
        hold.State = HoldState.Held;
        hold.LastConfirmedMs = _clock.UtcNowMs;
        var (movement, heightCm) = PlacementFor(record.State);
        // A new character starts with full meters; any other with its saved ones.
        SavedMeters? saved = record.State.WorldId == CharacterState.NotPlacedWorldId
            ? null
            : new SavedMeters(record.State.Hp, record.State.Mp, record.State.Stamina);
        hold.Player = _world.SpawnPlayer(record.AccountId, record.CharacterId, movement, heightCm, classDef, saved, record.Name);
        var player = hold.Player;
        hold.LastMarkedPosition = (player.XCm, player.YCm, player.HeightCm, player.Movement.Facing);
        hold.LastMarkedMeters = player.Combatant == null ? default : CombatRules.Capture(player.Combatant, _world.NowMs);
        _saves.Track(record.CharacterId, record.Version);
        Attach(hold, waiting, reattached: false);
        // Spec 4.1 M2 item model: the class's starter weapon, equipped, once ever (the Auth API creates characters without
        // items, rule 4). Tracked first, so the grant rides on the next class 2 save.
        if (!items.StarterGranted)
            _world.Inventory?.GrantStarterGear(record.CharacterId);
    }

    private void OnRefreshed(Completion completion)
    {
        var hold = completion.Hold!;
        if (hold.State != HoldState.Held || hold.Token != completion.Token)
            return;
        hold.RefreshInFlight = false;
        if (completion.Error != null)
            return; // unknown: the heartbeat gives up once a whole TTL passed without a confirmed refresh
        if (completion.Ok)
            hold.LastConfirmedMs = _clock.UtcNowMs;
        else
            LoseOwnership(hold);
    }

    private void OnReleased(Hold hold)
    {
        if (hold.State != HoldState.Releasing)
            return;
        if (hold.Session is { } waiting)
        {
            if (_shutDown)
            {
                RemoveHold(hold);
                Refuse(waiting, ConnectRejectReason.ServerMaintenance);
                return;
            }
            StartAcquire(hold); // a login arrived while the lock was going away
            return;
        }
        RemoveHold(hold);
    }

    // ---- Operations (thread pool; they only read their arguments and return a completion) ----

    private void Start(Func<Task<Completion>> operation)
    {
        _pendingOperations++;
        // On the thread pool: an async call runs synchronously up to its first await, and the first Npgsql or Redis call
        // of a process JIT-compiles its client there (about 30 ms of the tick a first login started, QA wave 5 G3).
        if (_runOperationsInline)
            _ = RunAsync(operation);
        else
            _ = Task.Run(() => RunAsync(operation));
    }

    private async Task RunAsync(Func<Task<Completion>> operation)
    {
        // Each operation catches its own failures; this is the last guard so a completion always arrives.
        Completion completion;
        try
        {
            completion = await operation().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            completion = new Completion(CompletionKind.Orphaned, Error: e);
        }
        _completions.Enqueue(completion);
    }

    private async Task<Completion> FindAsync(Session session)
    {
        try
        {
            var record = await _characters.FindBySlotAsync(session.AccountId, session.Slot).ConfigureAwait(false);
            return new Completion(CompletionKind.Found, Session: session, Record: record);
        }
        catch (Exception e)
        {
            return new Completion(CompletionKind.Found, Session: session, Error: e);
        }
    }

    private async Task<Completion> AcquireAsync(Hold hold, string token)
    {
        var id = hold.CharacterId;
        try
        {
            if (!await _locks.TryAcquireAsync(id, token).ConfigureAwait(false))
                return new Completion(CompletionKind.Acquired, Hold: hold, Token: token, Result: AcquireResult.Busy);
        }
        catch (Exception e)
        {
            return new Completion(CompletionKind.Acquired, Hold: hold, Token: token, Result: AcquireResult.Failed, Error: e);
        }

        try
        {
            // Read after the lock: the latest save of whoever held it before.
            var record = await _characters.LoadAsync(id).ConfigureAwait(false);
            var items = record == null ? null : await _characters.LoadItemsAsync(id).ConfigureAwait(false);
            var progression = record == null ? null : await _characters.LoadProgressionAsync(id).ConfigureAwait(false);
            return new Completion(CompletionKind.Acquired, Hold: hold, Token: token, Result: AcquireResult.Ok, Record: record,
                Items: items, Progression: progression);
        }
        catch (Exception e)
        {
            // Crash while loading: give the lock back so the character is not stuck until the TTL.
            try
            {
                await _locks.ReleaseAsync(id, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The TTL frees it.
            }
            return new Completion(CompletionKind.Acquired, Hold: hold, Token: token, Result: AcquireResult.Failed, Error: e);
        }
    }

    private async Task<Completion> RefreshAsync(Hold hold, string token)
    {
        try
        {
            var ok = await _locks.RefreshAsync(hold.CharacterId, token).ConfigureAwait(false);
            return new Completion(CompletionKind.Refreshed, Hold: hold, Token: token, Ok: ok);
        }
        catch (Exception e)
        {
            return new Completion(CompletionKind.Refreshed, Hold: hold, Token: token, Error: e);
        }
    }

    private async Task<Completion> ReleaseAsync(Hold hold, string token)
    {
        try
        {
            await _locks.ReleaseAsync(hold.CharacterId, token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The lock expires with its TTL; nothing else to do.
        }
        return new Completion(CompletionKind.Released, Hold: hold, Token: token);
    }

    /// <summary>Shutdown only: drains completions on the calling thread until no operation is pending or time is up.</summary>
    private void WaitForOperations(int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        DrainCompletions();
        while (_pendingOperations > 0 && watch.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(5);
            DrainCompletions();
        }
    }

    private enum HoldState : byte
    {
        Acquiring,
        Held,
        Releasing,
    }

    private enum CompletionKind : byte
    {
        Found,
        Acquired,
        Refreshed,
        Released,
        /// <summary>An operation threw outside its own handling; only counted.</summary>
        Orphaned,
    }

    private enum AcquireResult : byte
    {
        Ok,
        Busy,
        Failed,
    }

    private sealed record Completion(
        CompletionKind Kind,
        Session? Session = null,
        Hold? Hold = null,
        string? Token = null,
        CharacterRecord? Record = null,
        InventoryLoad? Items = null,
        ProgressionRecord? Progression = null,
        AcquireResult Result = AcquireResult.Ok,
        bool Ok = false,
        Exception? Error = null);

    private sealed class Session(int peerId, long accountId, int slot, InputQueue inputs)
    {
        public int PeerId { get; } = peerId;
        public long AccountId { get; } = accountId;
        public int Slot { get; } = slot;
        public InputQueue Inputs { get; } = inputs;

        /// <summary>The character this session waits for or is bound to.</summary>
        public Hold? Hold { get; set; }

        /// <summary>Bound to the character's entity.</summary>
        public bool Online { get; set; }

        public bool Ended { get; set; }
    }

    private sealed class Hold(long characterId)
    {
        public long CharacterId { get; } = characterId;
        public HoldState State { get; set; }
        public string Token { get; set; } = "";

        /// <summary>The session bound to the entity, or waiting for the acquisition or release to finish.</summary>
        public Session? Session { get; set; }

        public Player? Player { get; set; }

        /// <summary>The row as loaded; fields the world does not simulate yet are saved back as they were.</summary>
        public CharacterRecord? Record { get; set; }

        public bool Closing { get; set; }
        public long LastConfirmedMs { get; set; }
        public bool RefreshInFlight { get; set; }
        public (int X, int Y, int Height, ushort Facing) LastMarkedPosition { get; set; }
        public SavedMeters LastMarkedMeters { get; set; }
    }
}
