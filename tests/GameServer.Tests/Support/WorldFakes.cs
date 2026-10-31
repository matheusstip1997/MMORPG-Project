using GameServer.Host;
using GameServer.Net;
using GameServer.World;
using Shared.Protocol;

namespace GameServer.Tests.Support;

/// <summary>Monotonic fake: waiting moves time to the deadline; tests move it further to simulate work.</summary>
public sealed class FakeTickClock(long nowMicros = 1_000_000) : ITickClock
{
    public long NowMicros { get; private set; } = nowMicros;

    public void Advance(long micros) => NowMicros += micros;

    public void WaitUntil(long micros, CancellationToken cancellation)
    {
        if (micros > NowMicros)
            NowMicros = micros;
    }
}

/// <summary>Records what the world sends. With <see cref="Record"/> off it only counts, so it allocates nothing.</summary>
public sealed class FakeTransport : ITransport
{
    public bool Record { get; set; } = true;

    public int SendCount { get; private set; }

    public List<(int PeerId, Channel Channel, byte[] Data)> Sent { get; } = [];

    public int LocalPort => 0;

    public int ConnectedPeers => 0;

    public void Start(int port)
    {
    }

    public void Stop()
    {
    }

    public void PollEvents()
    {
    }

    /// <summary>What <see cref="MaxUnreliableBytes"/> returns for every peer (a real peer's MTU limit); unlimited by default.</summary>
    public int PeerMaxUnreliableBytes { get; set; } = int.MaxValue;

    public long UnreliableTooLarge { get; private set; }

    public TooLargeSend LastUnreliableTooLarge { get; private set; }

    public int MaxUnreliableBytes(int peerId) => PeerMaxUnreliableBytes;

    /// <summary>Drops and counts an unreliable message over the limit, as the real transport does.</summary>
    public void Send(int peerId, Channel channel, ReadOnlySpan<byte> data)
    {
        if (channel != Channel.ReliableOrdered && data.Length > PeerMaxUnreliableBytes)
        {
            UnreliableTooLarge++;
            LastUnreliableTooLarge = new TooLargeSend(peerId, data.Length, PeerMaxUnreliableBytes);
            return;
        }
        SendCount++;
        if (Record)
            Sent.Add((peerId, channel, data.ToArray()));
    }

    /// <summary>Kicks the world asked for, in order.</summary>
    public List<(int PeerId, ConnectRejectReason Reason)> Kicks { get; } = [];

    public void Disconnect(int peerId, ConnectRejectReason reason = ConnectRejectReason.None) => Kicks.Add((peerId, reason));

    public void Dispose()
    {
    }

    public List<DecodedSnapshot> SnapshotsFor(int peerId) =>
        Sent.Where(s => s.PeerId == peerId && s.Channel == Channel.UnreliableSequenced && s.Data[0] == (byte)HotMessageId.Snapshot)
            .Select(s => DecodedSnapshot.Decode(s.Data)).ToList();

    /// <summary>The newest snapshot sent to a peer as it went on the wire (for a client's <c>EntityRegistry</c>).</summary>
    public byte[] LastSnapshotBytes(int peerId) =>
        Sent.Last(s => s.PeerId == peerId && s.Channel == Channel.UnreliableSequenced && s.Data[0] == (byte)HotMessageId.Snapshot).Data;

    /// <summary>Hit messages sent to a peer (backlog D3), each with its server tick, in send order.</summary>
    public List<(uint Tick, HitEntry Hit)> HitsFor(int peerId)
    {
        var hits = new List<(uint, HitEntry)>();
        var buffer = new HitEntry[HitMessage.MaxHits];
        foreach (var (peer, channel, data) in Sent)
        {
            if (peer != peerId || channel != Channel.UnreliableSequenced || data[0] != (byte)HotMessageId.Hit)
                continue;
            Assert.True(HitMessage.TryRead(data, out uint tick, buffer, out int count));
            for (int i = 0; i < count; i++)
                hits.Add((tick, buffer[i]));
        }
        return hits;
    }

    /// <summary>Pickup answers sent to a peer (backlog G), in send order.</summary>
    public List<PickupResult> PickupResultsFor(int peerId) => Reliable(peerId, ReliableMessageId.PickupResult)
        .Select(data => Assert.IsType<PickupResult>(ReliableMessages.TryRead(data, out PickupResult r) ? r : null)).ToList();

    /// <summary>Committed bags sent to a peer (backlog G), in send order.</summary>
    public List<InventoryContents> InventoryFor(int peerId) => Reliable(peerId, ReliableMessageId.InventoryContents)
        .Select(data => Assert.IsType<InventoryContents>(ReliableMessages.TryRead(data, out InventoryContents c) ? c : null)).ToList();

    /// <summary>Bag move answers sent to a peer (WP16), in send order.</summary>
    public List<InventoryMoveResult> MoveResultsFor(int peerId) => Reliable(peerId, ReliableMessageId.InventoryMoveResult)
        .Select(data => Assert.IsType<InventoryMoveResult>(ReliableMessages.TryRead(data, out InventoryMoveResult r) ? r : null)).ToList();

    /// <summary>NPC interaction answers sent to a peer (WP-M2-4), in send order.</summary>
    public List<OutpostInteractResult> InteractResultsFor(int peerId) => Read<OutpostInteractResult>(peerId, ReliableMessageId.OutpostInteractResult);

    /// <summary>Sale quotes sent to a peer (WP-M2-4), in send order.</summary>
    public List<SellQuoteResult> QuotesFor(int peerId) => Read<SellQuoteResult>(peerId, ReliableMessageId.SellQuoteResult);

    /// <summary>Sale answers sent to a peer (WP-M2-4), in send order.</summary>
    public List<SellResult> SellResultsFor(int peerId) => Read<SellResult>(peerId, ReliableMessageId.SellResult);

    /// <summary>Vendor purchase answers sent to a peer (WP-M2-4), in send order.</summary>
    public List<VendorBuyResult> BuyResultsFor(int peerId) => Read<VendorBuyResult>(peerId, ReliableMessageId.VendorBuyResult);

    /// <summary>Committed gold balances sent to a peer (WP-M2-4), in send order.</summary>
    public List<long> GoldFor(int peerId) => Read<GoldBalance>(peerId, ReliableMessageId.GoldBalance).Select(g => g.Gold).ToList();

    /// <summary>Item use answers sent to a peer (WP-M3-3), in send order.</summary>
    public List<UseItemResult> UseResultsFor(int peerId) => Read<UseItemResult>(peerId, ReliableMessageId.UseItemResult);

    /// <summary>Harvest answers sent to a peer (WP-M3-5), in send order.</summary>
    public List<HarvestResult> HarvestResultsFor(int peerId) => Read<HarvestResult>(peerId, ReliableMessageId.HarvestResult);

    /// <summary>Quest boards sent to a peer (WP-M2-6), in send order; each passes the client's checks.</summary>
    public List<QuestBoard> QuestBoardsFor(int peerId) => Reliable(peerId, ReliableMessageId.QuestBoard)
        .Select(data => Assert.IsType<QuestBoard>(ReliableMessages.TryRead(data, out QuestBoard b) ? b : null)).ToList();

    /// <summary>Run request answers sent to a peer (WP-M2-6), in send order.</summary>
    public List<QuestRunResult> QuestResultsFor(int peerId) => Reliable(peerId, ReliableMessageId.QuestRunResult)
        .Select(data => Assert.IsType<QuestRunResult>(ReliableMessages.TryRead(data, out QuestRunResult r) ? r : null)).ToList();

    /// <summary>Run states sent to a peer (WP-M2-6), in send order; each passes the client's checks.</summary>
    public List<RunStatus> RunStatusFor(int peerId) => Reliable(peerId, ReliableMessageId.RunStatus)
        .Select(data => Assert.IsType<RunStatus>(ReliableMessages.TryRead(data, out RunStatus s) ? s : null)).ToList();

    /// <summary>The ids of the reliable messages sent to a peer, in send order.</summary>
    public List<ReliableMessageId> ReliableIdsFor(int peerId) => Sent
        .Where(s => s.PeerId == peerId && s.Channel == Channel.ReliableOrdered).Select(s => (ReliableMessageId)s.Data[0]).ToList();

    private List<T> Read<T>(int peerId, ReliableMessageId id) where T : class
    {
        var result = new List<T>();
        foreach (var data in Reliable(peerId, id))
        {
            T? value = null;
            Assert.True(ReliableMessageReader.TryRead(data, id, ref value), $"{id} did not decode");
            result.Add(value!);
        }
        return result;
    }
    /// <summary>Own level, exp and rank sent to a peer (WP-M2-3), in send order.</summary>
    public List<ProgressionState> ProgressionFor(int peerId) => Reliable(peerId, ReliableMessageId.ProgressionState)
        .Select(data => Assert.IsType<ProgressionState>(ReliableMessages.TryRead(data, out ProgressionState s) ? s : null)).ToList();

    /// <summary>Skill-tree states and answers sent to a peer (WP-M2-5), in send order.</summary>
    public List<SkillTreeState> SkillTreesFor(int peerId) => Read<SkillTreeState>(peerId, ReliableMessageId.SkillTreeState);

    /// <summary>Attribute point answers sent to a peer (WP-M2-13), in send order.</summary>
    public List<AttributeSpendResult> AttributeResultsFor(int peerId) => Read<AttributeSpendResult>(peerId, ReliableMessageId.AttributeSpendResult);

    public List<SkillTreeResult> SkillResultsFor(int peerId) => Read<SkillTreeResult>(peerId, ReliableMessageId.SkillTreeResult);
    /// <summary>Job states sent to a peer (WP-M3-1), in send order.</summary>
    public List<JobState> JobStatesFor(int peerId) => Read<JobState>(peerId, ReliableMessageId.JobState);

    /// <summary>Job pick answers sent to a peer (WP-M3-1), in send order.</summary>
    public List<JobResult> JobResultsFor(int peerId) => Read<JobResult>(peerId, ReliableMessageId.JobResult);

    /// <summary>Forge states sent to a peer (WP-M3-6), in send order.</summary>
    public List<ForgeState> ForgeStatesFor(int peerId) => Read<ForgeState>(peerId, ReliableMessageId.ForgeState);

    /// <summary>Forge finish answers sent to a peer (WP-M3-6), in send order.</summary>
    public List<ForgeResult> ForgeResultsFor(int peerId) => Read<ForgeResult>(peerId, ReliableMessageId.ForgeResult);
    /// <summary>Bystial book states sent to a peer (WP-M2-8), in send order.</summary>
    public List<BookState> BookFor(int peerId) => Read<BookState>(peerId, ReliableMessageId.BookState);

    /// <summary>Book change answers sent to a peer (WP-M2-8), in send order.</summary>
    public List<BookChangeResult> BookResultsFor(int peerId) => Read<BookChangeResult>(peerId, ReliableMessageId.BookChangeResult);

    /// <summary>Own-stats messages sent to a peer (WP-M2-2), raw, in send order.</summary>
    public List<byte[]> OwnStatsFor(int peerId) => Reliable(peerId, ReliableMessageId.OwnStats).ToList();

    /// <summary>The ids of the inventory messages sent to a peer (pickup and move answers, contents), in send order.</summary>
    public List<ReliableMessageId> InventoryMessageIdsFor(int peerId) => Sent
        .Where(s => s.PeerId == peerId && s.Channel == Channel.ReliableOrdered
                    && s.Data[0] is (byte)ReliableMessageId.PickupResult or (byte)ReliableMessageId.InventoryMoveResult
                        or (byte)ReliableMessageId.InventoryContents or (byte)ReliableMessageId.UseItemResult)
        .Select(s => (ReliableMessageId)s.Data[0]).ToList();

    private IEnumerable<byte[]> Reliable(int peerId, ReliableMessageId id) =>
        Sent.Where(s => s.PeerId == peerId && s.Channel == Channel.ReliableOrdered && s.Data[0] == (byte)id).Select(s => s.Data);

    /// <summary>Refused actions sent to a peer on the reliable channel (backlog D2).</summary>
    public List<(ushort Sequence, ushort ActionId, byte Slot, ActionReason Reason)> ActionResultsFor(int peerId)
    {
        var results = new List<(ushort, ushort, byte, ActionReason)>();
        foreach (var (peer, channel, data) in Sent)
        {
            if (peer != peerId || channel != Channel.ReliableOrdered || data[0] != (byte)ReliableMessageId.ActionResult)
                continue;
            Assert.True(ActionResult.TryRead(data, out ushort sequence, out ushort actionId, out byte slot, out ActionReason reason));
            results.Add((sequence, actionId, slot, reason));
        }
        return results;
    }

    /// <summary>Chat lines sent to a peer (R-6.5-01), in send order.</summary>
    public List<(uint SenderId, string Name, string Text)> ChatLinesFor(int peerId)
    {
        var lines = new List<(uint, string, string)>();
        foreach (var (peer, channel, data) in Sent)
        {
            if (peer != peerId || channel != Channel.ReliableOrdered || data[0] != (byte)ReliableMessageId.ChatMessage)
                continue;
            Assert.True(ChatCodec.TryReadLine(data, out var line));
            lines.Add((line.SenderEntityId, line.SenderName, line.Text));
        }
        return lines;
    }

    /// <summary>Chat refusals sent to a peer, in send order.</summary>
    public List<ChatReason> ChatRefusalsFor(int peerId)
    {
        var reasons = new List<ChatReason>();
        foreach (var (peer, channel, data) in Sent)
        {
            if (peer != peerId || channel != Channel.ReliableOrdered || data[0] != (byte)ReliableMessageId.ChatRefused)
                continue;
            Assert.True(ChatCodec.TryReadRefused(data, out var reason));
            reasons.Add(reason);
        }
        return reasons;
    }

    /// <summary>Player names sent to a peer on the reliable channel (spec 9.3 name plates), in send order.</summary>
    public List<(uint EntityId, string Name)> IdentitiesFor(int peerId)
    {
        var identities = new List<(uint, string)>();
        foreach (var (peer, channel, data) in Sent)
        {
            if (peer != peerId || channel != Channel.ReliableOrdered || data[0] != (byte)ReliableMessageId.EntityIdentity)
                continue;
            Assert.True(EntityIdentity.TryRead(data, out uint entityId, out string name));
            identities.Add((entityId, name));
        }
        return identities;
    }
}

/// <summary>A snapshot read back with the shared <see cref="SnapshotReader"/>.</summary>
public sealed record DecodedSnapshot(
    SnapshotHeader Header,
    List<uint> Despawns,
    List<SpawnEvent> Spawns,
    List<EntitySnapshot> Entities,
    List<OwnStatus> OwnStatuses,
    List<OwnBuildUp> OwnBuildUps)
{
    public static DecodedSnapshot Decode(byte[] data)
    {
        Assert.True(SnapshotReader.TryBegin(data, out var reader), "Not a valid snapshot.");
        var despawns = new List<uint>();
        var spawns = new List<SpawnEvent>();
        var entities = new List<EntitySnapshot>();
        var statuses = new List<OwnStatus>();
        var buildUps = new List<OwnBuildUp>();
        while (reader.TryReadOwnStatus(out var status))
            statuses.Add(status);
        while (reader.TryReadOwnBuildUp(out var buildUp))
            buildUps.Add(buildUp);
        while (reader.TryReadDespawn(out var id))
            despawns.Add(id);
        while (reader.TryReadSpawn(out var spawn))
            spawns.Add(spawn);
        while (reader.TryReadEntity(out var entity))
            entities.Add(entity);
        return new DecodedSnapshot(reader.Header, despawns, spawns, entities, statuses, buildUps);
    }

    /// <summary>The observer's own entity is always the first one (SnapshotSender).</summary>
    public EntitySnapshot Self => Entities[0];
}

public static class TestWorlds
{
    /// <summary>The <c>ChatSettings</c> of data/tables, written out like <see cref="Settings"/>.</summary>
    public static Shared.Data.Tables.ChatSettings Chat(int burst = 5, int messagesPerPeriod = 3, int periodMs = 5_000,
        int maxChars = 200, int maxQueuedLines = 8) => new()
    {
        MaxChars = maxChars,
        MessagesPerPeriod = messagesPerPeriod,
        PeriodMs = periodMs,
        Burst = burst,
        HistoryLines = 50,
        MaxQueuedLines = maxQueuedLines,
    };

    /// <summary>The M1 values from data/tables, written out so a table change does not silently change these tests.</summary>
    public static WorldSettings Settings(int snapshotRateHz = 20, int maxQueuedInputFrames = 4, int inputBacklogTicks = 10,
        int maxInputFramesPerTick = 2) => new(
        TickRateHz: 20,
        SnapshotRateHz: snapshotRateHz,
        MoveSpeedCmPerSec: 400,
        InterestRadiusCm: 3_000,
        GridCellCm: 1_500,
        PlayerSpawnMarkerId: TestMaps.SpawnId,
        SpeedTolerancePercent: 10,
        MaxQueuedInputFrames: maxQueuedInputFrames,
        MaxSnapshotBytes: 1_200,
        InputBacklogTicks: inputBacklogTicks,
        MaxInputFramesPerTick: maxInputFramesPerTick);

    public static InputFrame Frame(ushort sequence, MoveKeys keys = MoveKeys.None, ushort facing = 0,
        InputActions actions = InputActions.None, byte skillSlot = 0) => new()
    {
        Sequence = sequence,
        ClientTick = 1_000u + sequence,
        Keys = keys,
        Facing = facing,
        Actions = actions,
        SkillSlot = skillSlot,
    };
}

/// <summary>A world with a fake transport, driven tick by tick from the test thread.</summary>
public sealed class WorldHarness
{
    /// <param name="values">Session values (linger, save intervals); default: no linger, see <see cref="TestSessions.Values"/>.</param>
    public WorldHarness(WorldSettings? settings = null, IVisibilityQuery? visibility = null, ITickHook? events = null,
        ITickHook? telemetry = null, GameServer.Events.EventBus? bus = null, GameServer.World.Map.WorldMap? map = null,
        IMovementViolationSink? violations = null,
        Shared.Data.Tables.StartingValuesM1? values = null, Shared.Data.Tables.SessionSettings? sessionSettings = null,
        Shared.Simulation.Actions.ActionCatalog? actions = null, GameServer.Combat.CombatRules? combat = null,
        GameServer.Combat.ProjectileRules? projectiles = null, GameServer.Items.ItemRules? items = null, ulong rngSeed = 0,
        Shared.Data.Tables.ChatSettings? chat = null, bool inlineSessionOperations = true,
        GameServer.Combat.IStrikeTraceSink? strikeTrace = null, GameServer.Economy.EconomyRules? economy = null,
        GameServer.Progression.ProgressionRules? progression = null,
        Shared.Simulation.Skills.SkillRules? skills = null,
        GameServer.Bystial.BystialRules? bystial = null, GameServer.Quests.QuestRules? quests = null,
        GameServer.World.Nodes.NodeRules? nodes = null,
        Shared.Simulation.Jobs.JobRules? jobs = null,
        Shared.Simulation.Crafting.ForgeRules? forge = null)
    {
        var resolved = settings ?? TestWorlds.Settings();
        var session = sessionSettings ?? TestSessions.Settings();
        Inbox = new WorldInbox();
        Router = new InputRouter(Inbox, resolved.MaxQueuedInputFrames,
            (items?.Settings.MaxQueuedPickupRequests ?? 0) + (economy?.Settings.MaxQueuedRequests ?? 0) + (skills != null ? 8 : 0)
            + (quests?.Caps.MaxQueuedRequests ?? 0) + (jobs != null ? 4 : 0) + (nodes != null ? 4 : 0) + (forge != null ? 8 : 0), chat?.MaxQueuedLines ?? 0);
        World = new GameWorld(resolved, map ?? TestMaps.Open(), Inbox, Transport, visibility, events, telemetry, bus, violations,
            actions, combat, clock: Clock, rngSeed: rngSeed, projectiles: projectiles, items: items, runId: "test-run",
            chat: chat, strikeTrace: strikeTrace, economy: economy, progression: progression, skills: skills, bystial: bystial,
            quests: quests, nodes: nodes, jobs: jobs, forge: forge);
        Locks = new InMemoryOwnershipLocks(Clock, session.OwnershipLockTtlMs);
        Saves.Store = Characters;
        Sessions = new GameServer.Sessions.SessionManager(World, Characters, Locks, Saves, Saves, Transport, Clock,
            values ?? TestSessions.Values(), session, "test-server", runOperationsInline: inlineSessionOperations);
    }

    public FakeClock Clock { get; } = new();

    public FakeCharacterSource Characters { get; } = new();

    public InMemoryOwnershipLocks Locks { get; }

    public FakeSaveWorker Saves { get; } = new();

    public GameServer.Sessions.SessionManager Sessions { get; }

    public FakeTransport Transport { get; } = new();

    public WorldInbox Inbox { get; }

    public InputRouter Router { get; }

    public GameWorld World { get; }

    /// <summary>
    /// Connects a peer through the router, as the transport would, and runs a tick so it spawns. Each peer is its own
    /// account unless <paramref name="accountId"/> says otherwise (one session per account, R-2.1-04).
    /// </summary>
    public GameServer.Entities.Player Join(int peerId, long? accountId = null, byte slot = 0)
    {
        Connect(peerId, accountId, slot);
        World.Tick();
        Assert.True(World.TryGetPlayer(peerId, out var player));
        return player;
    }

    /// <summary>Connects a peer through the router without ticking.</summary>
    public void Connect(int peerId, long? accountId = null, byte slot = 0) =>
        Router.OnPeerConnected(peerId, new GameServer.Net.Tickets.SessionTicket(accountId ?? peerId, 1, slot, 0, new byte[16], 1));

    public void Leave(int peerId) => Router.OnPeerDisconnected(peerId, PeerDisconnectReason.ClosedByClient);

    /// <summary>Sends an input packet through the router, as the transport would after verifying its tag.</summary>
    public void SendInputs(int peerId, params InputFrame[] newestFirst)
    {
        var buffer = new byte[InputPacket.MaxSize];
        int length = InputPacket.Write(buffer, newestFirst);
        Router.OnDataReceived(peerId, Channel.UnreliableSequenced, buffer.AsSpan(0, length));
    }

    /// <summary>Sends a pickup request through the router, as the transport would after decrypting it (backlog G).</summary>
    public void SendPickup(int peerId, uint dropId) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered,
            ReliableMessages.Write(ReliableMessageId.PickupRequest, new PickupRequest { DropEntityId = dropId }));
    /// <summary>Sends a chat line through the router on the reliable channel, as the transport would after decrypting it.</summary>
    public void SendChat(int peerId, string text) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered,
            ChatCodec.WriteSend(new ReliableMessageWriter(), new ChatSendMessage { Text = text }));

    /// <summary>Sends a bag move request through the router (WP16 inventory window).</summary>
    public void SendMove(int peerId, uint requestId, byte fromSlot, byte toSlot) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.InventoryMoveRequest,
            new InventoryMoveRequest { RequestId = requestId, FromSlot = fromSlot, ToSlot = toSlot }));

    /// <summary>A move between container slots (<c>ContainerDef.code</c>s; WP-M2-1); a count splits the stack (WP-M2-11).</summary>
    public void SendMove(int peerId, uint requestId, byte fromContainer, byte fromSlot, byte toContainer, byte toSlot, ushort count = 0) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.InventoryMoveRequest,
            new InventoryMoveRequest
            {
                RequestId = requestId, FromContainer = fromContainer, FromSlot = fromSlot, ToContainer = toContainer, ToSlot = toSlot,
                Count = count,
            }));
    /// <summary>Sends a skill-tree request through the router (WP-M2-5).</summary>
    public void SendSkillTree(int peerId, uint requestId, SkillTreeOp op, string target = "", byte slot = 0) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.SkillTreeRequest,
            new SkillTreeRequest { RequestId = requestId, Op = op, Target = target, Slot = slot }));

    /// <summary>Sends an item use request through the router (WP-M3-3); container 0 is the Normal bag.</summary>
    public void SendUseItem(int peerId, uint requestId, byte slot, byte container = 0) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.UseItemRequest,
            new UseItemRequest { RequestId = requestId, Container = container, Slot = slot }));

    /// <summary>Sends a harvest request through the router (WP-M3-5).</summary>
    public void SendHarvest(int peerId, uint requestId, uint nodeEntityId) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.HarvestRequest,
            new HarvestRequest { RequestId = requestId, NodeEntityId = nodeEntityId }));

    /// <summary>Sends an attribute point request through the router (WP-M2-13).</summary>
    public void SendAttributeSpend(int peerId, uint requestId, int attribute) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.AttributeSpendRequest,
            new AttributeSpendRequest { RequestId = requestId, Attribute = (byte)attribute }));

    /// <summary>Sends a job pick through the router (WP-M3-1).</summary>
    public void SendJob(int peerId, uint requestId, uint npcEntityId, byte jobNetId, Shared.Simulation.Jobs.JobSlot slot) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.JobRequest,
            new JobRequest { RequestId = requestId, NpcEntityId = npcEntityId, JobNetId = jobNetId, Slot = slot }));

    /// <summary>Sends a forge start through the router (WP-M3-6): the inputs as (container, slot) pairs.</summary>
    public void SendForgeStart(int peerId, uint requestId, uint stationEntityId, ForgeKind kind, int defNetId, params (byte Container, byte Slot)[] inputs) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.ForgeStart, new ForgeStart
        {
            RequestId = requestId, StationEntityId = stationEntityId, Kind = kind, DefNetId = (ushort)defNetId,
            Inputs = inputs.Select(i => new ForgeInput { Container = i.Container, Slot = i.Slot }).ToArray(),
        }));

    /// <summary>Sends a forge action through the router (WP-M3-6).</summary>
    public void SendForgeAction(int peerId, uint requestId, int actionNetId) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.ForgeAction,
            new ForgeAction { RequestId = requestId, ActionNetId = (byte)actionNetId }));

    /// <summary>Sends a forge finish through the router (WP-M3-6).</summary>
    public void SendForgeFinish(int peerId, uint requestId) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.ForgeFinish,
            new ForgeFinish { RequestId = requestId }));

    /// <summary>Sends a forge cancel through the router (WP-M3-6).</summary>
    public void SendForgeCancel(int peerId, uint requestId) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.ForgeCancel,
            new ForgeCancel { RequestId = requestId }));

    /// <summary>Sends an NPC interaction request through the router (WP-M2-4).</summary>
    public void SendInteract(int peerId, uint requestId, uint npcEntityId, Shared.Data.Tables.NpcInteraction kind) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.OutpostInteractRequest,
            new OutpostInteractRequest { RequestId = requestId, NpcEntityId = npcEntityId, Kind = kind }));

    /// <summary>Sends a sale quote request through the router (WP-M2-4).</summary>
    public void SendQuote(int peerId, uint requestId, uint npcEntityId, byte slot, ushort count) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.SellQuoteRequest,
            new SellQuoteRequest { RequestId = requestId, NpcEntityId = npcEntityId, Slot = slot, Count = count }));

    /// <summary>Sends a sale through the router (WP-M2-4).</summary>
    public void SendSell(int peerId, uint requestId, uint npcEntityId, byte slot, ushort count, long quotedTotal) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.SellRequest,
            new SellRequest { RequestId = requestId, NpcEntityId = npcEntityId, Slot = slot, Count = count, QuotedTotal = quotedTotal }));

    /// <summary>Sends a Bystial book change through the router (WP-M2-8).</summary>
    public void SendBookChange(int peerId, uint requestId, ushort monsterNetId, byte slot) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.BookChangeRequest,
            new BookChangeRequest { RequestId = requestId, MonsterNetId = monsterNetId, Slot = slot }));

    /// <summary>Sends a vendor purchase through the router (WP-M2-4).</summary>
    public void SendBuy(int peerId, uint requestId, uint npcEntityId, ushort itemNetId, ushort count, long quotedTotal) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.VendorBuyRequest,
            new VendorBuyRequest { RequestId = requestId, NpcEntityId = npcEntityId, ItemNetId = itemNetId, Count = count, QuotedTotal = quotedTotal }));

    /// <summary>Sends a run request through the router (WP-M2-6).</summary>
    public void SendQuestRun(int peerId, uint requestId, uint npcEntityId, QuestRunAction action, uint epoch = 0, byte step = 0,
        params byte[] slots) =>
        Router.OnDataReceived(peerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.QuestRunRequest,
            new QuestRunRequest { RequestId = requestId, NpcEntityId = npcEntityId, Action = action, Epoch = epoch, StepIndex = step, Slots = slots }));

    public void Ticks(int count)
    {
        for (int i = 0; i < count; i++)
            World.Tick();
    }

    public DecodedSnapshot LastSnapshot(int peerId) => Transport.SnapshotsFor(peerId)[^1];
}
