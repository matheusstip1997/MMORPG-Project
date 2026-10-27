using GameServer.Entities;
using GameServer.Items;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (WP-M2-4): a player's outpost and vendor answers. <see cref="SendAnswers"/> runs before the
/// <see cref="InventorySender"/>: interaction answers, quotes and refusals of this tick, then the sales and purchases whose
/// save committed (rule 5), just before the <see cref="InventoryContents"/> that shows them. <see cref="SendGold"/> runs
/// after it: the committed gold (<see cref="GoldBalance"/>) whenever a save changed it or the client is new. Reliable
/// channel, MemoryPack; these messages are rare, so encoding them allocates.
/// </summary>
internal sealed class OutpostSender
{
    private readonly ITransport _transport;

    public OutpostSender(ITransport transport)
    {
        _transport = transport;
    }

    public long Sent { get; private set; }

    public void SendAnswers(Player player, InventoryService? inventory)
    {
        var answers = player.Outposts;
        if (!answers.HasAnswers)
            return;
        foreach (var result in answers.Interactions)
            Write(player, ReliableMessageId.OutpostInteractResult, result);
        answers.Interactions.Clear();
        foreach (var quote in answers.Quotes)
            Write(player, ReliableMessageId.SellQuoteResult, quote);
        answers.Quotes.Clear();
        foreach (var refusal in answers.SaleRefusals)
            Write(player, ReliableMessageId.SellResult, refusal);
        answers.SaleRefusals.Clear();
        foreach (var refusal in answers.PurchaseRefusals)
            Write(player, ReliableMessageId.VendorBuyResult, refusal);
        answers.PurchaseRefusals.Clear();

        if (inventory == null || !inventory.IsAttached(player.CharacterId))
        {
            answers.Pending.Clear();
            return;
        }
        var pending = answers.Pending;
        int kept = 0;
        for (int i = 0; i < pending.Count; i++)
        {
            if (!inventory.IsCommitted(player.CharacterId, pending[i].OpKey))
            {
                pending[kept++] = pending[i]; // still waiting for its save; order kept
                continue;
            }
            if (pending[i].Sale is { } sale)
                Write(player, ReliableMessageId.SellResult, sale);
            else if (pending[i].Purchase is { } purchase)
                Write(player, ReliableMessageId.VendorBuyResult, purchase);
        }
        pending.RemoveRange(kept, pending.Count - kept);
    }

    public void SendGold(Player player, InventoryService? inventory)
    {
        if (!player.Outposts.GoldDirty || inventory == null || !inventory.HasGold(player.CharacterId))
            return;
        player.Outposts.GoldDirty = false;
        Write(player, ReliableMessageId.GoldBalance, new GoldBalance { Gold = inventory.CommittedGold(player.CharacterId) });
    }

    private void Write<T>(Player player, ReliableMessageId id, T message)
    {
        _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(id, message));
        Sent++;
    }
}
