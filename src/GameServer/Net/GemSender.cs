using GameServer.Entities;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (spec 5.3, 9.13; WP-M3-9): a player's gem answers. Reliable channel, MemoryPack; an applied socket or removal is
/// answered only once its save committed (rule 5), before the <c>InventoryContents</c> that shows it.
/// </summary>
internal sealed class GemSender
{
    private readonly ITransport _transport;

    public GemSender(ITransport transport)
    {
        _transport = transport;
    }

    public void Send(Player player)
    {
        foreach (var answer in player.GemAnswers)
            _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.GemResult, answer));
        player.GemAnswers.Clear();
    }
}
