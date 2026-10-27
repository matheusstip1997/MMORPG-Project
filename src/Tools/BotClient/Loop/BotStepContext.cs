using BotClient.Brain;
using BotClient.World;
using Game.Gameplay.Entities;
using Shared.Data.Tables;
using Shared.Simulation;

namespace BotClient.Loop;

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

    public long NowMs => View.NowMs;

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
