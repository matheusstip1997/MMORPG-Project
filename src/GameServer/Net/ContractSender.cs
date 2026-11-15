using GameServer.Entities;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (spec 5.4, 9.13; WP-M4-10): a player's contract and portfolio answers, board pages and own contracts in the order they
/// were written (an applied operation only once committed, before the <c>InventoryContents</c> that shows it). Reliable channel,
/// MemoryPack.
/// </summary>
internal sealed class ContractSender(ITransport transport)
{
    public void Send(Player player)
    {
        foreach (var message in player.ContractMessages)
            transport.Send(player.PeerId, Channel.ReliableOrdered, message);
        player.ContractMessages.Clear();
    }
}
