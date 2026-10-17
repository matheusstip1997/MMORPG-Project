using GameServer.Net;
using GameServer.World;
using Shared.Protocol;
using Shared.Simulation;

namespace GameServer.Entities;

/// <summary>
/// A connected player character (backlog C1: minimal). Created when a peer connects and removed when it disconnects.
/// TODO(B4): linger on disconnect, one session per account, character selection.
/// TODO(H1): load and save the character instead of spawning a fresh one.
/// </summary>
public sealed class Player : Entity
{
    public Player(uint id, int peerId, long accountId, InputQueue inputs, MovementState movement, int heightCm)
        : base(id, EntityKind.Player, movement, heightCm)
    {
        PeerId = peerId;
        AccountId = accountId;
        Inputs = inputs;
    }

    public int PeerId { get; }

    public long AccountId { get; }

    /// <summary>Filled by the network thread, drained one frame per tick in phase 1.</summary>
    public InputQueue Inputs { get; }

    /// <summary>The input applied this tick (phase 1), read by movement (phase 2).</summary>
    internal MovementInput CurrentInput { get; set; }

    internal bool HasInputThisTick { get; set; }

    /// <summary>False until an input from this client was applied.</summary>
    public bool HasInputAck { get; internal set; }

    /// <summary>Newest applied input sequence; snapshots carry it so the client replays the later frames (spec 2.2).</summary>
    public ushort LastInputSequence { get; internal set; }

    internal InterestSet Interest { get; } = new();
}
