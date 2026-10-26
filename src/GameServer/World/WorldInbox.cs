using System.Collections.Concurrent;
using GameServer.Admin;
using GameServer.Net;

namespace GameServer.World;

public enum WorldCommandKind : byte
{
    Join,
    Leave,

    /// <summary>An admin kick claimed by the <see cref="AdminCommandWorker"/> (spec 7.4).</summary>
    AdminKick,
}

/// <summary>
/// A request from the network thread or the admin command worker to the simulation thread (spec 1.6: other threads only
/// enqueue). Applied at the start of phase 1, in the order posted.
/// </summary>
public readonly struct WorldCommand
{
    private WorldCommand(WorldCommandKind kind, int peerId, long accountId, int slot, InputQueue? inputs,
        long actionId = 0, long characterId = 0, AdminCommandResults? adminResults = null)
    {
        Kind = kind;
        PeerId = peerId;
        AccountId = accountId;
        Slot = slot;
        Inputs = inputs;
        ActionId = actionId;
        CharacterId = characterId;
        AdminResults = adminResults;
    }

    public WorldCommandKind Kind { get; }

    public int PeerId { get; }

    public long AccountId { get; }

    /// <summary>The ticket's character slot (R-2.5-01); the SessionManager loads that character.</summary>
    public int Slot { get; }

    public InputQueue? Inputs { get; }

    /// <summary>Admin commands: the <c>admin_actions</c> row the outcome belongs to.</summary>
    public long ActionId { get; }

    /// <summary>Admin commands: the target character.</summary>
    public long CharacterId { get; }

    /// <summary>Admin commands: where the world reports the outcome.</summary>
    public AdminCommandResults? AdminResults { get; }

    /// <summary>An accepted peer: its ticket's account and slot, and the queue its inputs go to.</summary>
    public static WorldCommand Join(int peerId, long accountId, int slot, InputQueue inputs) =>
        new(WorldCommandKind.Join, peerId, accountId, slot, inputs);

    public static WorldCommand Leave(int peerId) => new(WorldCommandKind.Leave, peerId, 0, 0, null);

    /// <summary>Kick the character's client; the outcome goes to <paramref name="results"/>.</summary>
    public static WorldCommand AdminKick(long actionId, long characterId, AdminCommandResults results) =>
        new(WorldCommandKind.AdminKick, 0, 0, 0, null, actionId, characterId, results);
}

/// <summary>
/// Commands from the network thread to one world, applied at the start of its next tick (spec 1.6). Thread-safe.
/// Created before the world and the transport listener, so neither has to exist for the other to be built.
/// </summary>
public sealed class WorldInbox
{
    private readonly ConcurrentQueue<WorldCommand> _commands = new();

    public void Post(in WorldCommand command) => _commands.Enqueue(command);

    internal bool TryTake(out WorldCommand command) => _commands.TryDequeue(out command);
}
