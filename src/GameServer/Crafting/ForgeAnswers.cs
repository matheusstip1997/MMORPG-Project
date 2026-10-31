using Shared.Protocol;

namespace GameServer.Crafting;

/// <summary>
/// One player's forge traffic (spec 5.3, 9.13; WP-M3-6): the session states to send (answers to starts, actions and
/// cancels, and the server's own cancels) and the finish answers (refusals at once, results once committed). World thread only.
/// </summary>
public sealed class ForgeAnswers
{
    /// <summary>States sent to this connection (the message's version).</summary>
    public uint Version { get; set; }

    internal List<ForgeState> States { get; } = new();

    internal List<ForgeResult> Results { get; } = new();

    /// <summary>A new connection: answers for the old connection are dropped.</summary>
    public void Reset()
    {
        ClearAnswers();
        Version = 0;
    }

    public void ClearAnswers()
    {
        States.Clear();
        Results.Clear();
    }
}
