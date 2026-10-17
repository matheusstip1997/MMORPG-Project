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
        long characterId = 0)
        : base(id, EntityKind.Player, movement, heightCm)
    {
        PeerId = peerId;
        AccountId = accountId;
        Inputs = inputs;
        CharacterId = characterId;
    }

    /// <summary>The connected peer, or <see cref="NoPeer"/> while lingering.</summary>
    public int PeerId { get; private set; }

    public bool IsConnected => PeerId != NoPeer;

    public long AccountId { get; }

    /// <summary>The character row this entity was loaded from (0 only in tests that build players by hand).</summary>
    public long CharacterId { get; }

    /// <summary>Filled by the network thread, drained one frame per tick in phase 1.</summary>
    public InputQueue Inputs { get; private set; }

    /// <summary>
    /// UTC ms of the last combat action (R-2.1-07, R-2.1-08: <c>lastCombatAt</c>); 0 = never.
    /// TODO(D): set by HitResolver when the player hits or is hit.
    /// </summary>
    public long LastCombatAtMs { get; set; }

    /// <summary>The input applied this tick (phase 1), read by movement (phase 2).</summary>
    internal MovementInput CurrentInput { get; set; }

    internal bool HasInputThisTick { get; set; }

    /// <summary>False until an input from this client was applied.</summary>
    public bool HasInputAck { get; internal set; }

    /// <summary>Newest applied input sequence; snapshots carry it so the client replays the later frames (spec 2.2).</summary>
    public ushort LastInputSequence { get; internal set; }

    internal InterestSet Interest { get; } = new();

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
        HasInputAck = false;
        LastInputSequence = 0;
        Interest.Reset();
    }

    /// <summary>Unbinds the client: the player stands still and takes no more inputs (R-2.1-08).</summary>
    internal void Unbind(InputQueue detached)
    {
        PeerId = NoPeer;
        Inputs = detached;
        CurrentInput = default;
        HasInputThisTick = false;
    }
}
