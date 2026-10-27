using BotClient.Brain;
using BotClient.World;
using Game.Gameplay.Entities;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;

namespace BotClient.Loop;

/// <summary>Where <see cref="BotStepContext.ApproachNpc"/> stands this tick.</summary>
public enum NpcApproach : byte
{
    /// <summary>No NPC or npc marker qualifies (or the marker was reached and no NPC stands there).</summary>
    None,
    /// <summary>On the way (the command walks) or waiting for the server's own position to arrive in range.</summary>
    Walking,
    /// <summary>Within the NPC's <c>interactRangeCm</c> by both the predicted and the server's own position.</summary>
    InReach,
}

/// <summary>
/// What a loop step may use (the <see cref="IBotStep"/> contract): the bot's view of the world from the Unity client's
/// engine-free sources (ClientCore), its brain and tables, and the two ways to act (the tick's command, a reliable
/// request). One per bot.
/// </summary>
public sealed class BotStepContext
{
    private readonly Func<byte[], bool> _sendReliable;
    private readonly Action<string> _log;
    private readonly BotStepRegistry _registry;
    private readonly Dictionary<string, NpcDef> _npcsById;
    private readonly int _combatStateMs;
    private readonly Dictionary<int, ItemDef> _itemsByNetId;

    public BotStepContext(int botIndex, BotData data, BotBrain brain, BotPerception view, EntityRegistry entities,
        ClientInventory inventory, BotOwnState own, BotCounters counters, BotStepRegistry registry,
        Func<byte[], bool> sendReliable, Action<string> log)
    {
        BotIndex = botIndex;
        Data = data;
        Brain = brain;
        View = view;
        Entities = entities;
        Inventory = inventory;
        Own = own;
        Counters = counters;
        _registry = registry;
        _sendReliable = sendReliable;
        _log = log;
        Outposts = new ClientOutposts(data.Tables);
        _npcsById = data.Rows<NpcDef>().ToDictionary(n => n.Id, StringComparer.Ordinal);
        _combatStateMs = data.Rows<StartingValuesM1>().Select(v => v.CombatStateMs).FirstOrDefault();
        _itemsByNetId = data.Rows<ItemDef>().ToDictionary(i => i.NetId);
    }

    public int BotIndex { get; }

    /// <summary>Config tables, world grid and markers (the outpost's marker once the world has one).</summary>
    public BotData Data { get; }

    public BotBehaviour Behaviour => Brain.Behaviour;

    /// <summary>The fight and loot brain (<see cref="BotBrainMode"/>), also the grid-path walk.</summary>
    public BotBrain Brain { get; }

    /// <summary>This tick's perception: predicted position, meters, monsters and own drops in interest.</summary>
    public BotPerception View { get; }

    /// <summary>
    /// ClientCore's entity registry (read it, never change it): every entity of the newest snapshots by id, with
    /// <c>Kind</c>, <c>DefId</c> and position samples; NPC entities (outposts, vendors) appear here once the protocol has them.
    /// </summary>
    public EntityRegistry Entities { get; }

    /// <summary>ClientCore's inventory, kept by <c>InventoryContents</c> and <c>PickupResult</c>.</summary>
    public ClientInventory Inventory { get; }

    /// <summary>Own progress (level, exp, gold) the own readers fill in; unknown until the protocol carries it.</summary>
    public BotOwnState Own { get; }

    public BotCounters Counters { get; }

    /// <summary>The class of the own character (<c>ClassDef</c> id), for its gear (<c>ClassGearDef</c>, R-3.5-07).</summary>
    public string ClassId { get; init; } = "";

    /// <summary>
    /// ClientCore's outposts (WP-M2-4), kept by the world client from the first message on: NPC definitions, the outpost
    /// and vendor requests with their ids, the last answers, and the committed gold (<c>GoldBalance</c>).
    /// </summary>
    public ClientOutposts Outposts { get; init; }

    /// <summary>ClientCore's own level, exp and rank (<c>ProgressionState</c>, WP-M2-3), kept from the first message on.</summary>
    public ClientProgression Progression { get; init; } = new();

    public long NowMs => View.NowMs;

    /// <summary>The <c>ItemDef</c> of a net id (a bag slot's <c>ItemNetId</c>); null for an unknown one.</summary>
    public ItemDef? Item(int netId) => _itemsByNetId.TryGetValue(netId, out var item) ? item : null;

    /// <summary>
    /// The combat state of spec 12.7 as the client can tell it: a hit given or taken within
    /// <c>StartingValuesM1.combatStateMs</c>. The server decides; a gear change it refuses comes back as <c>InCombat</c>.
    /// </summary>
    public bool InCombat => NowMs - View.LastCombatAtMs < _combatStateMs;

    /// <summary>
    /// One tick of going to an NPC that <paramref name="wanted"/> accepts (an outpost that offers the sale, a vendor that
    /// stocks the potion), the way a player does: the nearest such NPC entity in interest; before one is in interest, the
    /// nearest npc marker of the map whose <c>NpcDef</c> it accepts. It walks to <c>approachReachPercent</c> of the NPC's
    /// <c>interactRangeCm</c>, then waits until the predicted and the server's own position are both within the range (the
    /// server checks it where it has the player, ClientOutposts.NearestNpc's rule). <see cref="NpcApproach.None"/> when no
    /// NPC or marker qualifies, or the marker is reached and no NPC stands there.
    /// </summary>
    public NpcApproach ApproachNpc(Func<NpcDef, bool> wanted, ref BotCommand command, out uint npcEntityId, out NpcDef? npcDef)
    {
        npcEntityId = 0;
        npcDef = null;
        var self = View.Self;
        NpcDef? best = null;
        FixVec2 bestAt = default;
        double bestCm = double.MaxValue;
        foreach (var entity in Entities.EntityValues)
        {
            if (entity.Kind != EntityKind.Npc || !Outposts.TryGetNpc(entity.DefId, out var npc) || !wanted(npc)
                || !entity.Samples.TryNewest(out var sample))
                continue;
            var at = FixVec2.FromCm(sample.XCm, sample.ZCm); // snapshot axes: Z is ground Y
            double cm = GridPath.DistanceCm(self, at);
            if (cm < bestCm || (cm == bestCm && entity.Id < npcEntityId))
            {
                best = npc;
                bestAt = at;
                bestCm = cm;
                npcEntityId = entity.Id;
            }
        }
        if (best != null)
        {
            if (bestCm > best.InteractRangeCm * Behaviour.ApproachReachPercent / 100.0)
            {
                command = Walk(bestAt, command);
                return NpcApproach.Walking;
            }
            npcDef = best;
            return GridPath.DistanceCm(View.Confirmed, bestAt) <= best.InteractRangeCm ? NpcApproach.InReach : NpcApproach.Walking;
        }

        // Not in interest yet: the map's marker (sorted by marker id, so ties go the same way every run).
        string? markerId = null;
        foreach (var (id, defId) in Data.MarkerDefIds.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!_npcsById.TryGetValue(defId, out var npc) || !wanted(npc))
                continue;
            double cm = GridPath.DistanceCm(self, Data.Markers[id]);
            if (cm < bestCm)
            {
                best = npc;
                bestCm = cm;
                markerId = id;
            }
        }
        if (markerId == null || bestCm <= best!.InteractRangeCm * Behaviour.ApproachReachPercent / 100.0)
            return NpcApproach.None;
        command = Walk(Data.Markers[markerId], command);
        return NpcApproach.Walking;
    }

    /// <summary>The command of one tick that walks toward <paramref name="goal"/> on the grid path (as the fight's approach).</summary>
    public BotCommand Walk(FixVec2 goal, BotCommand command) => Brain.WalkTo(View, goal, command);

    /// <summary>
    /// Sends a reliable request, built with <c>ReliableMessages.Write(id, message)</c> like <c>PickupRequest</c>. The
    /// server reads it only if its id has a <c>ReliableMessageCaps</c> row and an <c>InputRouter</c> route (R-2.5-07).
    /// </summary>
    public bool SendReliable(byte[] message) => _sendReliable(message);

    /// <summary>A progress counter of this bot (for example <c>sell.items</c>), summed into <c>summary.json</c> under <c>loop</c>.</summary>
    public void Count(string name, long amount = 1) => Counters.AddLoop(name, amount);

    public void Log(string line) => _log($"bot {BotIndex}: {line}");

    /// <summary>
    /// The return value of a step the game cannot play yet: logs <c>step '&lt;id&gt;' not available yet (&lt;why&gt;)</c> once per
    /// run, not once per bot and round.
    /// </summary>
    public BotStepStatus NotAvailable(string stepId, string why)
    {
        if (_registry.FirstTime("not-available:" + stepId))
            _log($"step '{stepId}' not available yet ({why})");
        return BotStepStatus.NotAvailable;
    }
}
