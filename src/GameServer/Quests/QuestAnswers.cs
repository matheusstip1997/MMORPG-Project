using Shared.Protocol;

namespace GameServer.Quests;

/// <summary>
/// A player's quest messages, collected on the world thread and sent in phase 10 (WP-M2-6): the boards it opened, the answers to
/// its run requests (a claim's once its save committed), and whether its run changed. Held by the <see cref="Entities.Player"/>;
/// answers of a connection are dropped when it goes away (the next one gets the whole run).
/// </summary>
public sealed class QuestAnswers
{
    internal List<QuestBoard> Boards { get; } = new();
    internal List<QuestRunResult> Results { get; } = new();

    /// <summary>The run changed, or the client is new: send <see cref="RunStatus"/> in phase 10.</summary>
    internal bool RunDirty { get; set; }

    /// <summary><see cref="RunStatus"/> messages sent to the current connection.</summary>
    internal uint RunRevision { get; set; }

    internal void ClearAnswers()
    {
        Boards.Clear();
        Results.Clear();
    }
}
