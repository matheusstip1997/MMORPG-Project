using GameServer.Entities;
using GameServer.Quests;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (WP-M2-6): a player's quest messages, after the outpost answers (an interaction's yes before the board it opened):
/// the boards of this tick, the run request answers (a claim's once its save committed), then the <see cref="RunStatus"/> when
/// the run changed or the client is new. Reliable channel, MemoryPack; these messages are rare, so encoding them allocates.
/// </summary>
internal sealed class QuestSender
{
    private readonly ITransport _transport;

    public QuestSender(ITransport transport)
    {
        _transport = transport;
    }

    public long Sent { get; private set; }

    public void Send(Player player, RunStore? runs)
    {
        var answers = player.Quests;
        foreach (var board in answers.Boards)
            Write(player, ReliableMessageId.QuestBoard, board);
        answers.Boards.Clear();
        foreach (var result in answers.Results)
            Write(player, ReliableMessageId.QuestRunResult, result);
        answers.Results.Clear();
        if (!answers.RunDirty || runs == null || !runs.IsAttached(player.CharacterId))
            return;
        answers.RunDirty = false;
        Write(player, ReliableMessageId.RunStatus, runs.Status(player.CharacterId, ++answers.RunRevision));
    }

    private void Write<T>(Player player, ReliableMessageId id, T message)
    {
        _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(id, message));
        Sent++;
    }
}
