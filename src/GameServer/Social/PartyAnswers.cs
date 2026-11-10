using GameServer.Entities;
using Shared.Protocol;

namespace GameServer.Social;

/// <summary>A party change one player's frame shows: the kind, the entity and the name (as that player sees it) it is about.</summary>
internal readonly record struct PartyCause(PartyChangeKind Kind, uint EntityId, string Name);

/// <summary>
/// One player's party traffic (spec 6.5, WP-M4-2): the answers to its requests and the notices about its invites, the
/// invites it received, and whether its client needs a <see cref="PartyState"/> and why. Every change since the last send is
/// kept in order (<see cref="Causes"/>), so a leaving leader shows as "A left" and then "B leads", one state each; the name
/// is taken when the change happens, so it survives the character leaving the world. World thread only.
/// </summary>
public sealed class PartyAnswers
{
    /// <summary>Changes kept for one send at most; more in one tick keep the newest state and drop the oldest causes.</summary>
    internal const int MaxCauses = ReliableMessages.MaxPartyMembers;

    internal List<PartyResult> Results { get; } = new();

    internal List<PartyInvite> Invites { get; } = new();

    /// <summary>The changes since the last send, oldest first; empty for a refresh or a new connection.</summary>
    internal List<PartyCause> Causes { get; } = new(MaxCauses);

    /// <summary>Send a <see cref="PartyState"/> in phase 10: the party changed, a member came or went, or the client is new.</summary>
    public bool Dirty { get; internal set; }

    /// <summary>States sent to this connection (the message's version).</summary>
    public uint Version { get; internal set; }

    /// <summary>When the next refresh of HP and positions is due while in a party (UTC ms).</summary>
    internal long NextRefreshMs { get; set; }

    /// <summary>Whether the last state sent showed a party (a refresh is due only then).</summary>
    internal bool InParty { get; set; }

    /// <summary>A change <paramref name="recipient"/>'s frame shows; <paramref name="cause"/> is the player it is about (null once gone).</summary>
    internal void Mark(PartyChangeKind kind, Player? cause, Player recipient)
    {
        Dirty = true;
        if (kind == PartyChangeKind.None)
            return;
        if (Causes.Count == MaxCauses)
            Causes.RemoveAt(0);
        Causes.Add(new PartyCause(kind, cause?.Id ?? 0, cause == null ? string.Empty : DisplayIdentity.For(cause, recipient)));
    }

    /// <summary>Nothing changed in the party, but the frame should be sent now (a member connected or disconnected).</summary>
    internal void Touch() => Dirty = true;

    /// <summary>A new connection: the whole state again; answers for the old connection are dropped.</summary>
    internal void Reset()
    {
        ClearAnswers();
        Causes.Clear();
        Dirty = true;
        Version = 0;
        NextRefreshMs = 0;
    }

    internal void ClearAnswers()
    {
        Results.Clear();
        Invites.Clear();
    }
}
