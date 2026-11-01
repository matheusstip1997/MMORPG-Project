using GameServer.Entities;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (spec 11.7 dev console; WP-M3-13): a player's dev console answers. Reliable channel, MemoryPack; a command that
/// changed the character is answered only once its save committed (rule 5), before the messages that show the change.
/// </summary>
internal sealed class DevConsoleSender
{
    private readonly ITransport _transport;

    public DevConsoleSender(ITransport transport)
    {
        _transport = transport;
    }

    public void Send(Player player)
    {
        foreach (var answer in player.DevAnswers)
            _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.DevCommandResult, answer));
        player.DevAnswers.Clear();
    }
}
