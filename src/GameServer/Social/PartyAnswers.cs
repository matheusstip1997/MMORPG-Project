using Shared.Protocol;

namespace GameServer.Social;

/// <summary>
/// One player's party traffic (spec 6.5, WP-M4-2): the answers to its requests and the notices about its invites, the
/// invites it received, and whether its client needs a <see cref="PartyState"/> and why. World thread only.
/// </summary>
public sealed class PartyAnswers
{
    internal List<PartyResult> Results { get; } = new();

    internal List<PartyInvite> Invites { get; } = new();

    /// <summary>Send a <see cref="PartyState"/> in phase 10: the party changed, or the client is new.</summary>
    public bool Dirty { get; internal set; }

    /// <summary>The cause of the next state (the last change since the previous send).</summary>
    public PartyChangeKind Cause { get; internal set; }

    public long CauseCharacterId { get; internal set; }

    /// <summary>States sent to this connection (the message's version).</summary>
    public uint Version { get; internal set; }

    /// <summary>When the next refresh of HP and positions is due while in a party (UTC ms).</summary>
    internal long NextRefreshMs { get; set; }

    /// <summary>Whether the last state sent showed a party (a refresh is due only then).</summary>
    internal bool InParty { get; set; }

    internal void Mark(PartyChangeKind cause, long causeCharacterId)
    {
        Dirty = true;
        Cause = cause;
        CauseCharacterId = causeCharacterId;
    }

    /// <summary>A new connection: the whole state again; answers for the old connection are dropped.</summary>
    internal void Reset()
    {
        ClearAnswers();
        Mark(PartyChangeKind.None, 0);
        Version = 0;
        NextRefreshMs = 0;
    }

    internal void ClearAnswers()
    {
        Results.Clear();
        Invites.Clear();
    }
}
