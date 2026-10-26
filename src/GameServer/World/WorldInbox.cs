using System.Collections.Concurrent;
using GameServer.Net;

namespace GameServer.World;

public enum WorldCommandKind : byte
{
    Join,
    Leave,
    /// <summary>A decoded <c>ChatSend</c> (local chat, R-6.5-01): the world checks and delivers it.</summary>
    ChatSend,
}

/// <summary>
/// A request from the network thread to the simulation thread (spec 1.6: network threads only enqueue).
/// Applied at the start of phase 1, in the order posted.
/// </summary>
public readonly struct WorldCommand
{
    private WorldCommand(WorldCommandKind kind, int peerId, long accountId, int slot, InputQueue? inputs,
        string? text = null, RequestBudget? budget = null)
    {
        Kind = kind;
        PeerId = peerId;
        AccountId = accountId;
        Slot = slot;
        Inputs = inputs;
        Text = text;
        Budget = budget;
    }

    public WorldCommandKind Kind { get; }

    public int PeerId { get; }

    public long AccountId { get; }

    /// <summary>The ticket's character slot (R-2.5-01); the SessionManager loads that character.</summary>
    public int Slot { get; }

    public InputQueue? Inputs { get; }

    /// <summary>The chat line as the client sent it (decoded, not yet checked).</summary>
    public string? Text { get; }

    /// <summary>The peer's request slot this command holds; the world releases it when it applies the command.</summary>
    public RequestBudget? Budget { get; }

    /// <summary>An accepted peer: its ticket's account and slot, and the queue its inputs go to.</summary>
    public static WorldCommand Join(int peerId, long accountId, int slot, InputQueue inputs) =>
        new(WorldCommandKind.Join, peerId, accountId, slot, inputs);

    public static WorldCommand Leave(int peerId) => new(WorldCommandKind.Leave, peerId, 0, 0, null);

    public static WorldCommand ChatSend(int peerId, string text, RequestBudget budget) =>
        new(WorldCommandKind.ChatSend, peerId, 0, 0, null, text, budget);
}

/// <summary>
/// Commands from the network thread to one world, applied at the start of its next tick (spec 1.6). Thread-safe.
/// Created before the world and the transport listener, so neither has to exist for the other to be built.
/// Client requests are bounded per peer by a <see cref="RequestBudget"/> before they are posted.
/// </summary>
public sealed class WorldInbox
{
    private readonly ConcurrentQueue<WorldCommand> _commands = new();

    public void Post(in WorldCommand command) => _commands.Enqueue(command);

    internal bool TryTake(out WorldCommand command) => _commands.TryDequeue(out command);
}
