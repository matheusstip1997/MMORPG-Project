#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Prediction;
using Shared.Data;
using Shared.Protocol;
using Shared.Simulation.Actions;
using UnityEngine;

namespace Game.Gameplay.Entities
{
    public delegate void HitsHandler(uint serverTick, ReadOnlySpan<HitEntry> hits);

    public delegate void ActionResultHandler(ushort actionNetId, byte skillSlot, ActionReason reason);

    /// <summary>A local chat line (<see cref="ChatLineMessage"/>): the name already went through <c>DisplayIdentity</c>.</summary>
    public delegate void ChatLineHandler(uint senderEntityId, string senderName, string text);

    /// <summary>
    /// What the world client knows, for the HUD (backlog I, spec 9.4): the world client (today the dev
    /// <c>PredictionDevClient</c>) creates one, raises its events as messages arrive and ticks run, and the HUD binds to them
    /// instead of reading the network. A plain fan-out: the connection has one listener, the feed has many. The HUD only
    /// reads what it gets here; it never changes the registry or the predictor.
    /// </summary>
    public sealed class WorldClientFeed
    {
        public WorldClientFeed(ConfigSet config, ActionCatalog catalog, EntityRegistry registry, LocalPlayerPredictor predictor,
            EntityViews? views, Camera? camera, double tickMs)
        {
            Config = config;
            Catalog = catalog;
            Registry = registry;
            Predictor = predictor;
            Views = views;
            Camera = camera;
            TickMs = tickMs;
        }

        /// <summary>The tables the client runs; replaced by a dev hot reload (<see cref="ConfigReloaded"/>).</summary>
        public ConfigSet Config { get; private set; }

        public ActionCatalog Catalog { get; private set; }

        public EntityRegistry Registry { get; }

        public LocalPlayerPredictor Predictor { get; }

        public EntityViews? Views { get; }

        public Camera? Camera { get; }

        public double TickMs { get; }

        /// <summary>Id of the exported world the client loaded (<c>data/world/&lt;id&gt;</c>), for the minimap; empty when unknown.</summary>
        public string WorldId { get; set; } = string.Empty;

        public EntityIdentities Identities { get; } = new EntityIdentities();

        /// <summary>The bag as last committed on the server (backlog G); null before the first message and after a disconnect.</summary>
        public InventoryContents? Inventory { get; private set; }

        /// <summary>Own level, exp, rank and skill points (spec 3.6, WP-M2-3); null before the first message and after a disconnect.</summary>
        public ProgressionState? Progression { get; private set; }
        /// <summary>The own stats and meters as last sent by the server (WP-M2-2); null before the first message and after a disconnect.</summary>
        public ClientStats? Stats { get; private set; }

        /// <summary>The own skill tree (WP-M2-5): the committed class, learned nodes and hotbar; null before the first state.</summary>
        public ClientSkillTree? SkillTree { get; private set; }
        /// <summary>The own Bystial book as last sent (WP-M2-8, <see cref="BookState"/>); null before the first.</summary>
        public BookState? Book { get; private set; }

        /// <summary>Where the book window sends its changes (WP-M2-8); null without a connection that can.</summary>
        public IBookRequests? BookRequests { get; set; }

        /// <summary>Where UI windows send their requests (the inventory window's bag moves); null while nothing can send.</summary>
        public IWorldRequests? Requests { get; set; }

        /// <summary>Where the quest board sends run requests (WP-M2-6); null while nothing can send.</summary>
        public IQuestRequests? QuestRequests { get; set; }

        /// <summary>Where the character sheet sends its attribute points (WP-M2-13); null while nothing can send.</summary>
        public IAttributeRequests? AttributeRequests { get; set; }

        /// <summary>The character's run as the server holds it (WP-M2-6, <see cref="RunStatus"/>); null before the first message and after a disconnect.</summary>
        public RunStatus? Run { get; private set; }

        /// <summary>
        /// Set by the UI while the pointer is on a window (or drags from one): the world client then ignores the mouse
        /// buttons for attack and parry, so a click in the inventory never swings or parries.
        /// </summary>
        public bool UiCapturesPointer { get; set; }

        /// <summary>
        /// The ground drop the pickup key asks for now (backlog G, <c>ClientInventory.NearestDrop</c>), 0 for none; set by the
        /// world client every frame. The HUD's pickup prompt shows at it.
        /// </summary>
        public uint PickupTarget { get; private set; }

        /// <summary>The pickup key as the player reads it, from its binding ("G"); empty until the world client sets it.</summary>
        public string PickupKeyName { get; private set; } = "";

        /// <summary>The committed gold (WP-M2-4, <see cref="GoldBalance"/>); null before the first message and after a disconnect.</summary>
        public long? Gold { get; private set; }

        /// <summary>
        /// The NPC the interaction key talks to now (WP-M2-4, <c>ClientOutposts.NearestNpc</c>), 0 for none; set by the world client
        /// every frame with its <c>NpcDef.netId</c> (<see cref="NpcTargetDefId"/>). The HUD's NPC prompt shows at it.
        /// </summary>
        public uint NpcTarget { get; private set; }

        public ushort NpcTargetDefId { get; private set; }

        /// <summary>
        /// The resource node in reach now (WP-M3-4, <c>ClientNodes.NearestNode</c>: an available node of one of the own jobs),
        /// 0 for none; set by the world client every frame with its <c>NodeDef.netId</c> (<see cref="NodeTargetDefId"/>). The
        /// hook for the harvest prompt and key of WP-M3-5.
        /// </summary>
        public uint NodeTarget { get; private set; }

        public ushort NodeTargetDefId { get; private set; }

        /// <summary>The own character's jobs (spec 5.1); the stand-in <see cref="AllJobs"/> until WP-M3-1's ClientJobs sets it.</summary>
        public IOwnJobs Jobs { get; set; } = AllJobs.Instance;

        /// <summary>The tick others are drawn at (interpolation clock), for world overlays; false until it runs.</summary>
        public bool HasRenderTick { get; private set; }

        public double RenderTick { get; private set; }

        /// <summary>A snapshot was applied to <see cref="Registry"/> (own resources, statuses and flags are current).</summary>
        public event Action? SnapshotApplied;

        /// <summary>Hits the server resolved on entities this client sees (backlog D3).</summary>
        public event HitsHandler? HitsReceived;

        /// <summary>A refused action or a parry result (spec 9.4 <c>ActionResult</c>).</summary>
        public event ActionResultHandler? ActionResultReceived;

        /// <summary>The server refused a pickup (backlog G): the reason code for the toast (spec 9.4, <c>ReasonCodeStrings</c>).</summary>
        public event Action<PickupReason>? PickupRefused;

        /// <summary>The server sent the bag as committed (backlog G); also in <see cref="Inventory"/>.</summary>
        public event Action<InventoryContents>? InventoryReceived;

        /// <summary>The server answered a bag move (WP16): a refusal at once, an accepted move once it is saved.</summary>
        public event Action<InventoryMoveResult>? InventoryMoveAnswered;

        /// <summary>The committed gold arrived (WP-M2-4); also in <see cref="Gold"/>.</summary>
        public event Action<long>? GoldReceived;

        /// <summary>The server answered an NPC interaction (WP-M2-4): None opens the NPC's window.</summary>
        public event Action<OutpostInteractResult>? OutpostInteracted;

        /// <summary>The server answered a sale quote (WP-M2-4).</summary>
        public event Action<SellQuoteResult>? SellQuoted;

        /// <summary>The server answered a sale (WP-M2-4): a refusal at once, a sale once it is saved.</summary>
        public event Action<SellResult>? SellAnswered;

        /// <summary>The server answered a vendor purchase (WP-M2-4): a refusal at once, a purchase once it is saved.</summary>
        public event Action<VendorBuyResult>? PurchaseAnswered;
        /// <summary>The server sent an outpost's quest board (WP-M2-6): after the quests interaction, or with a <c>BoardExpired</c> refusal.</summary>
        public event Action<QuestBoard>? QuestBoardReceived;

        /// <summary>The server answered a run request (WP-M2-6): at once, a claim once it is saved.</summary>
        public event Action<QuestRunResult>? QuestRunAnswered;

        /// <summary>The character's run changed (WP-M2-6); also in <see cref="Run"/>.</summary>
        public event Action<RunStatus>? RunReceived;

        /// <summary>The server sent the own level, exp and rank (WP-M2-3); also in <see cref="Progression"/>.</summary>
        public event Action<ProgressionState>? ProgressionReceived;

        /// <summary>The server sent the own final stats and meters (WP-M2-2, <see cref="OwnStats"/>); also in <see cref="Stats"/>.</summary>
        public event Action<ClientStats>? StatsReceived;

        /// <summary>The server sent the own skill tree (WP-M2-5); also in <see cref="SkillTree"/>.</summary>
        public event Action<ClientSkillTree>? SkillTreeReceived;

        /// <summary>The server answered a skill-tree request (WP-M2-5): a refusal at once, an accepted change once committed.</summary>
        public event Action<SkillTreeResult>? SkillTreeAnswered;

        /// <summary>The server answered an attribute point request (WP-M2-13): a refusal at once, a placement once committed.</summary>
        public event Action<AttributeSpendResult>? AttributeAnswered;
        /// <summary>The server sent the own Bystial book (WP-M2-8); also in <see cref="Book"/>.</summary>
        public event Action<BookState>? BookReceived;

        /// <summary>The server answered a book change (WP-M2-8): a refusal at once, an accepted change after its save.</summary>
        public event Action<BookChangeResult>? BookChangeAnswered;

        /// <summary>A player's display name arrived (<see cref="EntityIdentity"/>, already in <see cref="Identities"/>).</summary>
        public event Action<uint>? IdentityReceived;

        /// <summary>A local chat line from a player this client sees, or the echo of its own (R-6.5-01, spec 9.4).</summary>
        public event ChatLineHandler? ChatReceived;

        /// <summary>The server refused this client's chat line (<see cref="ChatRefusedMessage"/>).</summary>
        public event Action<ChatReason>? ChatRefused;

        /// <summary>
        /// Sends a checked chat line on the reliable channel; set by the world client. False when it is not connected.
        /// The HUD sends through <see cref="TrySendChat"/>, never the connection.
        /// </summary>
        public Func<string, bool>? ChatSender { get; set; }

        /// <summary>
        /// Set by the HUD while the chat input has focus: the world client then suspends the gameplay keys
        /// (<c>GameInput.Suspended</c>), so typing never moves or attacks.
        /// </summary>
        public bool TextInputActive { get; set; }

        /// <summary>
        /// The character the tutorial's seen prompts belong to (R-12.3-03), a file-name-safe key such as <c>r1c42</c> (realm
        /// and character) or <c>dev-a7</c> (a dev ticket's account); empty until the world client knows it.
        /// </summary>
        public string CharacterKey { get; set; } = "";

        public bool TrySendChat(string text) => ChatSender != null && ChatSender(text);

        public void RaiseChatLine(uint senderEntityId, string senderName, string text) =>
            ChatReceived?.Invoke(senderEntityId, senderName, text);

        public void RaiseChatRefused(ChatReason reason) => ChatRefused?.Invoke(reason);

        /// <summary>The predictor ran one client tick (cooldowns, charges and the own action state may have changed).</summary>
        public event Action? PredictedTick;

        /// <summary>
        /// The connection was lost: everything the HUD shows is stale. Carries the <c>ReasonCodeStrings</c> code to show (for
        /// example <c>Kicked</c> or <c>ConnectionLost</c>, spec 9.10), empty when the player left.
        /// </summary>
        public event Action<string>? Disconnected;

        public void RaiseSnapshotApplied() => SnapshotApplied?.Invoke();

        public void RaiseHits(uint serverTick, ReadOnlySpan<HitEntry> hits) => HitsReceived?.Invoke(serverTick, hits);

        public void RaiseActionResult(ushort actionNetId, byte skillSlot, ActionReason reason) =>
            ActionResultReceived?.Invoke(actionNetId, skillSlot, reason);

        public void RaisePickupRefused(PickupReason reason) => PickupRefused?.Invoke(reason);

        public void RaiseInventory(InventoryContents contents)
        {
            Inventory = contents;
            InventoryReceived?.Invoke(contents);
        }

        public void RaiseInventoryMoveAnswered(InventoryMoveResult result) => InventoryMoveAnswered?.Invoke(result);

        public void RaiseProgression(ProgressionState state)
        {
            Progression = state;
            ProgressionReceived?.Invoke(state);
        }

        public void RaiseStats(ClientStats stats)
        {
            Stats = stats;
            StatsReceived?.Invoke(stats);
        }

        public void RaiseSkillTree(ClientSkillTree tree)
        {
            SkillTree = tree;
            SkillTreeReceived?.Invoke(tree);
        }

        public void RaiseSkillTreeAnswered(SkillTreeResult result) => SkillTreeAnswered?.Invoke(result);

        public void RaiseAttributeAnswered(AttributeSpendResult result) => AttributeAnswered?.Invoke(result);
        public void RaiseBook(BookState state)
        {
            Book = state;
            BookReceived?.Invoke(state);
        }

        public void RaiseBookChangeAnswered(BookChangeResult result) => BookChangeAnswered?.Invoke(result);

        public void RaiseIdentity(uint entityId, string name)
        {
            Identities.Set(entityId, name);
            IdentityReceived?.Invoke(entityId);
        }

        /// <summary>
        /// Dev hot reload (spec 11.7): the client swapped in the tables the server accepted. Views that built anything from
        /// <see cref="Config"/> (the HUD's telegraph shapes) rebuild it.
        /// </summary>
        public event Action? ConfigReloaded;

        public void RaiseConfigReloaded(ConfigSet config, ActionCatalog catalog)
        {
            Config = config;
            Catalog = catalog;
            ConfigReloaded?.Invoke();
        }

        public void RaisePredictedTick() => PredictedTick?.Invoke();

        /// <summary>The entity the target lock holds (R-2.2-06), 0 when none: the HUD marks its name plate.</summary>
        public uint LockedTargetId { get; private set; }

        /// <summary><see cref="LockedTargetId"/> changed (a client-side choice; the server never hears of it).</summary>
        public event Action<uint>? LockedTargetChanged;

        public void RaiseLockedTarget(uint entityId)
        {
            if (entityId == LockedTargetId)
                return;
            LockedTargetId = entityId;
            LockedTargetChanged?.Invoke(entityId);
        }

        /// <summary>Once per frame from the world client: what a pickup press would ask for, and the key that presses it.</summary>
        public void SetPickupTarget(uint dropId, string keyName)
        {
            PickupTarget = dropId;
            PickupKeyName = keyName;
        }

        public void RaiseGold(long gold)
        {
            Gold = gold;
            GoldReceived?.Invoke(gold);
        }

        public void RaiseOutpostInteracted(OutpostInteractResult result) => OutpostInteracted?.Invoke(result);

        public void RaiseSellQuoted(SellQuoteResult result) => SellQuoted?.Invoke(result);

        public void RaiseSellAnswered(SellResult result) => SellAnswered?.Invoke(result);

        public void RaisePurchaseAnswered(VendorBuyResult result) => PurchaseAnswered?.Invoke(result);

        public void RaiseQuestBoard(QuestBoard board) => QuestBoardReceived?.Invoke(board);

        public void RaiseQuestRunAnswered(QuestRunResult result) => QuestRunAnswered?.Invoke(result);

        public void RaiseRun(RunStatus status)
        {
            Run = status;
            RunReceived?.Invoke(status);
        }

        /// <summary>Once per frame from the world client: the NPC the interaction key talks to (0 for none).</summary>
        public void SetNpcTarget(uint npcEntityId, ushort npcDefId)
        {
            NpcTarget = npcEntityId;
            NpcTargetDefId = npcEntityId == 0 ? (ushort)0 : npcDefId;
        }

        /// <summary>Once per frame from the world client: the resource node in reach (0 for none; WP-M3-4).</summary>
        public void SetNodeTarget(uint nodeEntityId, ushort nodeDefId)
        {
            NodeTarget = nodeEntityId;
            NodeTargetDefId = nodeEntityId == 0 ? (ushort)0 : nodeDefId;
        }

        public void RaiseDisconnected(string reasonCode)
        {
            Gold = null;
            NpcTarget = 0;
            NpcTargetDefId = 0;
            NodeTarget = 0;
            NodeTargetDefId = 0;
            Identities.Clear();
            HasRenderTick = false;
            PickupTarget = 0;
            Inventory = null;
            Progression = null;
            Stats = null;
            SkillTree = null;
            Book = null;
            Run = null;
            UiCapturesPointer = false;
            RaiseLockedTarget(0);
            Disconnected?.Invoke(reasonCode);
        }

        public void SetRenderTick(double renderTick)
        {
            RenderTick = renderTick;
            HasRenderTick = true;
        }
    }

    /// <summary>
    /// Player display names by entity id, from <see cref="EntityIdentity"/> (the server already applied
    /// <c>DisplayIdentity</c>, rule 8). Entity ids are never reused within a world, so a name never goes stale; the reliable
    /// name may arrive before or after the unreliable spawn, so names are kept until the connection ends, not removed on
    /// despawn.
    /// </summary>
    public sealed class EntityIdentities
    {
        private readonly Dictionary<uint, string> _names = new Dictionary<uint, string>();

        public int Count => _names.Count;

        public void Set(uint entityId, string name) => _names[entityId] = name;

        public bool TryGet(uint entityId, out string name)
        {
            if (_names.TryGetValue(entityId, out var found))
            {
                name = found;
                return true;
            }
            name = string.Empty;
            return false;
        }

        public void Clear() => _names.Clear();
    }

    /// <summary>Requests a UI window may send to the server through the world client (WP16).</summary>
    public interface IWorldRequests
    {
        /// <summary>
        /// Sends an inventory move (<see cref="InventoryMoveRequest"/>: between container slots, <c>ContainerDef.code</c>s, 0 the
        /// Normal bag; into or out of Equipped it equips or unequips, WP-M2-1) and returns its request id, or 0 when nothing was
        /// sent (not connected). The answer comes as <see cref="WorldClientFeed.InventoryMoveAnswered"/>.
        /// </summary>
        /// <param name="count">Units to move (WP-M2-11): 0 or the whole stack moves it all; fewer split it (protocol 24).</param>
        uint RequestInventoryMove(byte fromContainer, byte fromSlot, byte toContainer, byte toSlot, ushort count = 0);

        /// <summary>Sends an NPC interaction (WP-M2-4); the answer comes as <see cref="WorldClientFeed.OutpostInteracted"/>. 0: not sent.</summary>
        uint RequestOutpostInteract(uint npcEntityId, Shared.Data.Tables.NpcInteraction kind);

        /// <summary>Asks the outpost for a sale quote; the answer comes as <see cref="WorldClientFeed.SellQuoted"/>. 0: not sent.</summary>
        uint RequestSellQuote(uint npcEntityId, byte slot, ushort count);

        /// <summary>Sells at the quoted total; the answer comes as <see cref="WorldClientFeed.SellAnswered"/>. 0: not sent.</summary>
        uint RequestSell(uint npcEntityId, byte slot, ushort count, long quotedTotal);

        /// <summary>Buys from a vendor; the answer comes as <see cref="WorldClientFeed.PurchaseAnswered"/>. 0: not sent.</summary>
        uint RequestVendorBuy(uint npcEntityId, ushort itemNetId, ushort count, long quotedTotal);

        /// <summary>
        /// Sends a skill-tree request (WP-M2-5: learn, upgrade, evolve, respec, advance, a hotbar change); the answer comes as
        /// <see cref="WorldClientFeed.SkillTreeAnswered"/>, then the committed state as <see cref="WorldClientFeed.SkillTreeReceived"/>.
        /// 0: not sent.
        /// </summary>
        uint RequestSkillTree(SkillTreeOp op, string target, byte slot);
    }

    /// <summary>The Bystial book's request (WP-M2-8), apart from <see cref="IWorldRequests"/> so other windows' fakes stay as they are.</summary>
    public interface IBookRequests
    {
        /// <summary>
        /// Sends a book change (<see cref="BookChangeRequest"/>: a card into a slot, an occupied slot swaps, or
        /// <see cref="BookLimits.NoSlot"/> to take it out) and returns its request id, or 0 when nothing was sent (not
        /// connected). The answer comes as <see cref="WorldClientFeed.BookChangeAnswered"/>.
        /// </summary>
        uint RequestBookChange(ushort monsterNetId, byte slot);
    }

    /// <summary>The character sheet's request (WP-M2-13), apart from <see cref="IWorldRequests"/> so other windows' fakes stay as they are.</summary>
    public interface IAttributeRequests
    {
        /// <summary>
        /// Sends a request to place one free attribute point (<see cref="AttributeSpendRequest"/>, R-3.4-08: <c>Attributes.Str</c>
        /// ... <c>Attributes.Int</c>) and returns its request id, or 0 when nothing was sent (not connected). The answer comes as
        /// <see cref="WorldClientFeed.AttributeAnswered"/>, then the committed points as <see cref="WorldClientFeed.ProgressionReceived"/>.
        /// </summary>
        uint RequestAttributeSpend(byte attribute);
    }

    /// <summary>Run requests the quest board sends through the world client (WP-M2-6).</summary>
    public interface IQuestRequests
    {
        /// <summary>
        /// Sends a run request at an outpost NPC (<see cref="QuestRunRequest"/>: accept with the board's epoch and the offer slots
        /// in run order, deliver or forfeit a step, claim) and returns its id, or 0 when nothing was sent (not connected). The
        /// answer comes as <see cref="WorldClientFeed.QuestRunAnswered"/>.
        /// </summary>
        uint RequestQuestRun(uint npcEntityId, QuestRunAction action, uint epoch, byte stepIndex, byte[] slots);
    }

    /// <summary>A world client that offers a <see cref="WorldClientFeed"/> to the HUD (found on a scene object).</summary>
    public interface IWorldClientFeedSource
    {
        /// <summary>Null until the client loaded its config.</summary>
        WorldClientFeed? Feed { get; }
    }
}
