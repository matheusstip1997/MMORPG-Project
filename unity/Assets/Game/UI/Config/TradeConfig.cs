#nullable enable
namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the trade window and the trade invite prompt (spec 9.1: no user-facing text in code; spec 4.5,
    /// 9.13; WP-M4-4). An EditMode test checks that each constant has a row; refusals and the end of a trade are their
    /// <c>TradeReason</c> code's <c>ReasonCodeStrings</c> row; item names are their <c>ItemDef.nameId</c>.
    /// </summary>
    public static class TradeTextIds
    {
        /// <summary>Trade with {name}.</summary>
        public const string Title = "trade.title";
        public const string You = "trade.you";
        /// <summary>{name}.</summary>
        public const string Partner = "trade.partner";
        /// <summary>{item} x{count}.</summary>
        public const string Offer = "trade.offer";
        /// <summary>{item} +{enchant}.</summary>
        public const string OfferEnchanted = "trade.offerEnchanted";
        /// <summary>{rarity} {quality} {gems}: the second line of an offer (quality and gems empty when none).</summary>
        public const string OfferDetails = "trade.offerDetails";
        /// <summary>{count} gems.</summary>
        public const string OfferGems = "trade.offerGems";
        public const string Empty = "trade.empty";
        public const string DropHint = "trade.dropHint";
        /// <summary>Gold: {gold}.</summary>
        public const string Gold = "trade.gold";
        public const string SetGold = "trade.setGold";
        public const string Lock = "trade.lock";
        public const string Locked = "trade.locked";
        public const string Confirm = "trade.confirm";
        public const string Confirmed = "trade.confirmed";
        /// <summary>Confirm ({seconds} s).</summary>
        public const string ConfirmCountdown = "trade.confirmCountdown";
        public const string Cancel = "trade.cancel";
        public const string Saving = "trade.saving";
        /// <summary>{name} wants to trade ({seconds} s).</summary>
        public const string Invited = "trade.invited";
        /// <summary>Waiting for {name} to answer ({seconds} s).</summary>
        public const string Inviting = "trade.inviting";
        public const string Accept = "trade.accept";
        public const string Decline = "trade.decline";
        /// <summary>The trade with {name} is done.</summary>
        public const string ToastDone = "trade.toast.done";

        public static readonly string[] All =
        {
            Title, You, Partner, Offer, OfferEnchanted, OfferDetails, OfferGems, Empty, DropHint, Gold, SetGold, Lock, Locked, Confirm, Confirmed,
            ConfirmCountdown, Cancel, Saving, Invited, Inviting, Accept, Decline, ToastDone,
        };
    }
}
