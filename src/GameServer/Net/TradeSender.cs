using GameServer.Entities;
using GameServer.Items;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (spec 4.5, 9.13; WP-M4-4): trade answers and states, reliable channel, MemoryPack, in the order the trade service
/// made them. A confirm that completed a swap is answered only once both saves committed (rule 5), before the
/// <c>InventoryContents</c> that shows it. Messages for a character no longer connected are dropped.
/// </summary>
internal sealed class TradeSender
{
    private readonly ITransport _transport;

    public TradeSender(ITransport transport)
    {
        _transport = transport;
    }

    public void Send(List<TradeMessage> outbox, Func<long, Player?> playerOf)
    {
        foreach (var message in outbox)
        {
            if (playerOf(message.CharacterId) is not { IsConnected: true } player)
                continue;
            if (message.Result is { } result)
                _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.TradeResult, result));
            if (message.State is { } state)
                _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.TradeState, state));
        }
    }

    /// <summary>A refusal before the trade service exists (a world without the tables).</summary>
    public void Refuse(Player player, uint requestId, TradeAction action, TradeReason reason)
    {
        if (player.IsConnected)
            _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.TradeResult,
                new TradeResult { RequestId = requestId, Action = action, Reason = reason }));
    }
}
