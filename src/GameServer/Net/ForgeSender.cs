using GameServer.Crafting;
using GameServer.Entities;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (spec 5.3, 9.13; WP-M3-6): a player's forge states and finish answers, then its enchant answers (WP-M3-8). Reliable channel, MemoryPack, never the hot
/// snapshot; a result is answered only once its save committed (rule 5), before the <c>InventoryContents</c> that shows it.
/// </summary>
internal sealed class ForgeSender
{
    private readonly ITransport _transport;

    public ForgeSender(ITransport transport)
    {
        _transport = transport;
    }

    public void Send(Player player)
    {
        var answers = player.Forge;
        foreach (var state in answers.States)
        {
            state.Version = ++answers.Version;
            _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.ForgeState, state));
        }
        answers.States.Clear();
        foreach (var result in answers.Results)
            _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.ForgeResult, result));
        answers.Results.Clear();
        foreach (var result in answers.Enchants)
            _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.EnchantResult, result));
        answers.Enchants.Clear();
    }
}
