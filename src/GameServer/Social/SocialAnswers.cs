using Shared.Protocol;

namespace GameServer.Social;

/// <summary>
/// One player's social traffic (spec 6.5 "M4 social", WP-M4-16): the answers to its requests and the notices about friend
/// requests, and whether its client needs a fresh <see cref="SocialState"/>. World thread only.
/// </summary>
public sealed class SocialAnswers
{
    internal List<SocialResult> Results { get; } = new();

    /// <summary>Send a <see cref="SocialState"/> in phase 10: a list changed, a friend came or went, or the client is new.</summary>
    public bool Dirty { get; internal set; }

    /// <summary>States sent to this connection (the message's version).</summary>
    public uint Version { get; internal set; }

    /// <summary>A new connection: the whole state again; answers for the old connection are dropped.</summary>
    internal void Reset()
    {
        Results.Clear();
        Dirty = true;
        Version = 0;
    }

    internal void ClearAnswers() => Results.Clear();
}
