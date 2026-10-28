using GameServer.Net;
using GameServer.World;
using Shared.Protocol;
using Shared.Simulation;

namespace GameServer.Entities;

/// <summary>
/// A player character in the world, created by the <see cref="GameServer.Sessions.SessionManager"/> from the loaded
/// character. While its client is connected it is bound to a peer; after a disconnect it lingers unbound (R-2.1-08):
/// it takes no inputs and gets no snapshots, but stays in the world for others. A reconnect binds it again.
/// </summary>
public sealed class Player : Entity
{
    /// <summary><see cref="PeerId"/> of a player whose client is not connected.</summary>
    public const int NoPeer = -1;

    public Player(uint id, int peerId, long accountId, InputQueue inputs, MovementState movement, int heightCm,
        long characterId = 0, string name = "")
        : base(id, EntityKind.Player, movement, heightCm)
    {
        PeerId = peerId;
        AccountId = accountId;
        Inputs = inputs;
        CharacterId = characterId;
        Name = name;
    }

    /// <summary>The connected peer, or <see cref="NoPeer"/> while lingering.</summary>
    public int PeerId { get; private set; }

    public bool IsConnected => PeerId != NoPeer;

    public long AccountId { get; }

    /// <summary>The character row this entity was loaded from (0 only in tests that build players by hand).</summary>
    public long CharacterId { get; }

    /// <summary>
    /// The character's real name. Never shown as is: every display goes through
    /// <see cref="GameServer.Social.DisplayIdentity"/> (architecture rule 8). Empty for players built by hand in tests.
    /// </summary>
    internal string Name { get; }

    /// <summary>Filled by the network thread, drained one frame per tick in phase 1.</summary>
    public InputQueue Inputs { get; private set; }

    /// <summary>
    /// UTC ms of the last combat action (R-2.1-07, R-2.1-08: <c>lastCombatAt</c>); 0 = never.
    /// Set by the HitResolver whenever the player hits or is hit (dodged hits included).
    /// </summary>
    public long LastCombatAtMs { get; set; }

    /// <summary>
    /// How many ticks behind the server this player sees other entities (R-3.1-03): the applied tick minus the view tick
    /// of the newest applied input frame, under the clock-sync limits of phase 1 (<see cref="Defence"/>), rounded down. Its
    /// hits on monsters are tested against where the monster stood that many ticks ago.
    /// </summary>
    internal int ViewLagTicks => Defence.LagTicks;

    /// <summary>
    /// Its view clock and recent dodge and parry starts: monsters' hits on it are judged on what it saw (R-3.1-03,
    /// R-3.3-02; <see cref="GameServer.Combat.DefenceTimeline"/>).
    /// </summary>
    internal GameServer.Combat.DefenceTimeline Defence { get; } = new();

    /// <summary>The input applied this tick (phase 1), read by movement (phase 2); the newest one on a catch-up tick.</summary>
    internal MovementInput CurrentInput { get; set; }

    /// <summary>
    /// Every frame's movement input applied this tick, oldest first (spec 1.6 input catch-up): one, or up to
    /// <c>ServerLoop.MaxInputFramesPerTick</c> while a backlog drains. Phase 2 steps movement once per entry.
    /// </summary>
    internal MovementInput[] TickInputs { get; } = new MovementInput[Shared.Data.Tables.ServerLoop.MaxInputFramesPerTickLimit];

    /// <summary>How many of <see cref="TickInputs"/> phase 1 filled this tick (0 without input).</summary>
    internal int TickInputCount { get; set; }

    /// <summary>Ticks in a row that ended phase 1 with frames still queued (input catch-up).</summary>
    internal int InputBacklogStreak { get; set; }

    internal bool HasInputThisTick { get; set; }

    /// <summary>Action buttons held in the input applied this tick (phase 1), read by the action machine (phase 3).</summary>
    internal InputActions CurrentActions { get; set; }

    /// <summary>Hotbar slot held in the input applied this tick, 0 for none.</summary>
    internal byte CurrentSkillSlot { get; set; }

    /// <summary>Refused actions waiting for phase 10, oldest first (spec 1.7: on the reliable channel).</summary>
    internal PendingActionResults ActionResults { get; } = new();

    /// <summary>Answers to this tick's pickup requests (phase 1), sent in phase 10 (backlog G).</summary>
    internal List<Shared.Protocol.PickupResult> PickupResults { get; } = new();

    /// <summary>Refused bag moves of this tick (phase 1), sent in phase 10 (WP16).</summary>
    internal List<Shared.Protocol.InventoryMoveResult> MoveRefusals { get; } = new();

    /// <summary>
    /// Accepted bag moves waiting for the save that holds them (rule 5: confirm only after the commit), oldest first;
    /// phase 10 sends each one when <c>InventoryService.IsCommitted</c> says so, before the contents of that save.
    /// </summary>
    internal List<PendingMoveConfirmation> PendingMoveConfirmations { get; } = new();

    /// <summary>Answers to outpost and vendor requests, and the outpost visits (WP-M2-4).</summary>
    public GameServer.Outposts.OutpostAnswers Outposts { get; } = new();

    /// <summary>
    /// Connections this player has had, counting the first: a bag move's operation key includes it, so a client whose
    /// request counter restarts after a reconnect never repeats a key (the database keeps every key forever, rule 5).
    /// </summary>
    internal int Connection { get; private set; } = 1;

    /// <summary>
    /// The committed bag changed, or the client is new: send <see cref="Shared.Protocol.InventoryContents"/> in phase 10.
    /// </summary>
    internal bool InventoryDirty { get; set; }

    /// <summary>Inventory messages sent to the current connection.</summary>
    internal uint InventoryRevision { get; set; }

    /// <summary>
    /// Its level, exp or rank changed, or the client is new: send <see cref="ProgressionState"/> in phase 10 with
    /// <see cref="ProgressionCause"/> (WP-M2-3; the highest cause since the last send).
    /// </summary>
    internal bool ProgressionDirty { get; set; }

    internal ProgressionCause ProgressionCause { get; set; }

    /// <summary>
    /// Toxicity and satiety (R-3.4-03, WP-M2-2), on UTC time; null for players built without combat rules. Set at spawn
    /// from the character row and saved with every character save.
    /// </summary>
    internal GameServer.Stats.ConsumptionMeters? Consumption { get; set; }

    /// <summary>The <c>StatBlock.Version</c> the last <c>StatsChanged</c> carried; -1 before the first (spec 8.1).</summary>
    internal int PublishedStatsVersion { get; set; } = -1;

    /// <summary>Send <see cref="Shared.Protocol.OwnStats"/> in phase 10: the stats or meters changed, or the client is new.</summary>
    internal bool StatsMessageDue { get; set; }

    /// <summary>Send <see cref="Shared.Protocol.SkillTreeState"/> in phase 10 (WP-M2-5): a committed skill change, a hotbar change, or a new client.</summary>
    internal bool SkillTreeDirty { get; set; }

    internal Shared.Protocol.SkillTreeCause SkillTreeCause { get; set; }

    /// <summary>Answers to skill-tree requests (WP-M2-5), sent in phase 10 before the state that shows them.</summary>
    internal List<Shared.Protocol.SkillTreeResult> SkillTreeAnswers { get; } = new();

    /// <summary>Counts the <see cref="Shared.Protocol.SkillTreeState"/> messages of this player (its <c>Version</c>).</summary>
    internal uint SkillTreeVersion { get; set; }

    /// <summary>False until an input from this client was applied.</summary>
    public bool HasInputAck { get; internal set; }

    /// <summary>Newest applied input sequence; snapshots carry it so the client replays the later frames (spec 2.2).</summary>
    public ushort LastInputSequence { get; internal set; }

    internal InterestSet Interest { get; } = new();

    /// <summary>
    /// The chat rate limit of this character (<c>ChatSettings</c>, R-6.5-01). A field, so <see cref="TokenBucket"/>
    /// updates in place; it stays with the entity, so reconnecting while lingering does not refill it.
    /// </summary>
    internal GameServer.Net.Security.TokenBucket ChatBucket;

    /// <summary>
    /// Binds a (re)connected client: its inputs, a fresh input sequence, and an empty interest set so its first
    /// snapshot spawns everything it sees, itself included.
    /// </summary>
    internal void Bind(int peerId, InputQueue inputs)
    {
        PeerId = peerId;
        Inputs = inputs;
        CurrentInput = default;
        HasInputThisTick = false;
        TickInputCount = 0;
        InputBacklogStreak = 0;
        CurrentActions = InputActions.None;
        CurrentSkillSlot = 0;
        HasInputAck = false;
        LastInputSequence = 0;
        ActionResults.Clear(); // they answered the previous connection's inputs
        PickupResults.Clear();
        MoveRefusals.Clear();
        PendingMoveConfirmations.Clear(); // the new connection gets the whole bag; its request ids start again
        Connection++;
        InventoryDirty = true; // a new connection starts with the whole bag
        Outposts.ClearAnswers();
        Outposts.GoldDirty = true; // and the gold
        InventoryRevision = 0;
        ProgressionDirty = true; // and its level, exp and rank (WP-M2-3)
        ProgressionCause = ProgressionCause.Initial;
        StatsMessageDue = true; // a new connection starts with its stats (WP-M2-2)
        SkillTreeDirty = true; // and its skills and hotbar (WP-M2-5); answers for the old connection are dropped
        SkillTreeCause = Shared.Protocol.SkillTreeCause.Initial;
        SkillTreeAnswers.Clear();
        Defence.Reset();
        Interest.Reset();
    }

    /// <summary>Answers no client will read (it is not connected): pickup and bag move results.</summary>
    internal void ClearInventoryAnswers()
    {
        PickupResults.Clear();
        MoveRefusals.Clear();
        PendingMoveConfirmations.Clear();
        Outposts.ClearAnswers();
        SkillTreeAnswers.Clear();
    }

    /// <summary>Unbinds the client: the player stands still and takes no more inputs (R-2.1-08).</summary>
    internal void Unbind(InputQueue detached)
    {
        PeerId = NoPeer;
        Inputs = detached;
        CurrentInput = default;
        HasInputThisTick = false;
        TickInputCount = 0;
        InputBacklogStreak = 0;
        CurrentActions = InputActions.None;
        CurrentSkillSlot = 0;
        ActionResults.Clear();
        PickupResults.Clear();
        MoveRefusals.Clear();
        PendingMoveConfirmations.Clear();
        SkillTreeAnswers.Clear();
        Outposts.ClearAnswers();
        Defence.Reset(); // no client sees anything now: hits on it land at once
    }
}

/// <summary>An accepted bag move, by its operation key, waiting for its save to commit (WP16, rule 5).</summary>
internal readonly record struct PendingMoveConfirmation(string OpKey, Shared.Protocol.InventoryMoveResult Result);

/// <summary>One refused action for the client (<see cref="Shared.Protocol.ActionResult"/>).</summary>
internal readonly record struct PendingActionResult(ushort InputSequence, ushort ActionNetId, byte SkillSlot, ActionReason Reason);

/// <summary>
/// A player's refused actions between phase 3 and phase 10. At most one refusal per tick, sent every tick, so a few
/// slots are plenty; when full the newest is dropped and counted (it is UI feedback, not state).
/// </summary>
internal sealed class PendingActionResults
{
    public const int Capacity = 4;

    private readonly PendingActionResult[] _items = new PendingActionResult[Capacity];

    public int Count { get; private set; }

    public long Dropped { get; private set; }

    public PendingActionResult this[int index] => _items[index];

    public void Add(in PendingActionResult result)
    {
        if (Count == Capacity)
        {
            Dropped++;
            return;
        }
        _items[Count++] = result;
    }

    public void Clear() => Count = 0;
}
