using Shared.Protocol;

namespace GameServer.Outposts;

/// <summary>
/// A player's answers to outpost and vendor requests, collected in phase 1 and sent in phase 10 (WP-M2-4). Refusals and
/// quotes go out in the same tick; an accepted sale or purchase (class 1, R-7.1-01) waits in <see cref="Pending"/> until
/// <c>InventoryService.IsCommitted</c> says its save committed (rule 5: commit before confirming). Held by the
/// <see cref="Entities.Player"/>; the answers of a connection are dropped when it goes away (the next one gets the whole
/// bag and gold).
/// </summary>
public sealed class OutpostAnswers
{
    internal List<OutpostInteractResult> Interactions { get; } = new();
    internal List<SellQuoteResult> Quotes { get; } = new();
    internal List<SellResult> SaleRefusals { get; } = new();
    internal List<VendorBuyResult> PurchaseRefusals { get; } = new();

    /// <summary>Accepted sales and purchases by the operation key their commit is checked with, oldest first.</summary>
    internal List<PendingClass1Answer> Pending { get; } = new();

    /// <summary>
    /// The last interaction time (world clock ms) with each outpost NPC, by entity id: a visit starts after
    /// <c>EconomySettings.visitGapMs</c> without one (event OutpostVisited). Kept across reconnects.
    /// </summary>
    internal Dictionary<uint, long> LastInteractionMs { get; } = new();

    /// <summary>The committed gold changed, or the client is new: send <see cref="GoldBalance"/> in phase 10.</summary>
    internal bool GoldDirty { get; set; }

    internal bool HasAnswers =>
        Interactions.Count > 0 || Quotes.Count > 0 || SaleRefusals.Count > 0 || PurchaseRefusals.Count > 0 || Pending.Count > 0;

    /// <summary>Answers no client will read: the connection that asked is gone.</summary>
    internal void ClearAnswers()
    {
        Interactions.Clear();
        Quotes.Clear();
        SaleRefusals.Clear();
        PurchaseRefusals.Clear();
        Pending.Clear();
    }
}

/// <summary>An accepted class 1 operation waiting for its save: a sale or a purchase result, by operation key.</summary>
internal readonly record struct PendingClass1Answer(string OpKey, SellResult? Sale, VendorBuyResult? Purchase);
