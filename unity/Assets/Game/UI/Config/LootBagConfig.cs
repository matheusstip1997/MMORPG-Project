#nullable enable
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the death loot bag window (spec 9.1: no user-facing text in code; spec 4.3, 9.13; WP-M4-9). An
    /// EditMode test checks that each constant has a row.
    /// </summary>
    public static class LootBagTextIds
    {
        /// <summary>The viewer's own bag.</summary>
        public const string TitleOwn = "loot.bag.title.own";
        /// <summary>A party member's bag inside its owner window: what is taken goes to its owner.</summary>
        public const string TitleForOwner = "loot.bag.title.forOwner";
        /// <summary>Someone else's bag after its owner window.</summary>
        public const string TitleOther = "loot.bag.title.other";
        /// <summary>Someone else's bag inside its owner window.</summary>
        public const string TitleLocked = "loot.bag.title.locked";
        /// <summary>Only the owner (and their party) for {seconds} s more.</summary>
        public const string Window = "loot.bag.window";
        /// <summary>Crumbles in {minutes} min (the expiry).</summary>
        public const string Expires = "loot.bag.expires";
        /// <summary>What you take goes to its owner by mail.</summary>
        public const string ForOwnerNote = "loot.bag.forOwnerNote";
        /// <summary>Taking from it marks the items stolen.</summary>
        public const string StolenWarning = "loot.bag.stolenWarning";
        /// <summary>{name} x{count}.</summary>
        public const string Row = "loot.bag.row";
        /// <summary>{name} x{count} (stolen).</summary>
        public const string RowStolen = "loot.bag.rowStolen";
        public const string Empty = "loot.bag.empty";
        public const string TakeAll = "loot.bag.takeAll";
        public const string Close = "loot.bag.close";
        /// <summary>The close button's label for screen readers (spec 9.8).</summary>
        public const string CloseLabel = "loot.bag.closeLabel";
        /// <summary>You took {count} items back.</summary>
        public const string ToastTaken = "loot.bag.toast.taken";
        /// <summary>You took {count} items; {mailed} more went to your mail.</summary>
        public const string ToastTakenMailed = "loot.bag.toast.takenMailed";
        /// <summary>{count} items went to their owner by mail.</summary>
        public const string ToastCollected = "loot.bag.toast.collected";
        /// <summary>You took {count} items; they are marked stolen.</summary>
        public const string ToastStolen = "loot.bag.toast.stolen";

        public static readonly string[] All =
        {
            TitleOwn, TitleForOwner, TitleOther, TitleLocked, Window, Expires, ForOwnerNote, StolenWarning, Row, RowStolen, Empty,
            TakeAll, Close, CloseLabel, ToastTaken, ToastTakenMailed, ToastCollected, ToastStolen,
        };
    }

    /// <summary>The config tables the loot bag window reads (WP-M4-9): the items by net id for the row names.</summary>
    public sealed class LootBagConfig
    {
        private readonly Dictionary<int, ItemDef> _items = new Dictionary<int, ItemDef>();

        /// <summary>The item of a net id; null for one the tables lack.</summary>
        public ItemDef? Item(int netId) => _items.TryGetValue(netId, out var item) ? item : null;

        public void AddItem(ItemDef item) => _items[item.NetId] = item;

        public static LootBagConfig From(ConfigSet set)
        {
            var config = new LootBagConfig();
            if (set.TryGet(typeof(ItemDef), out var items))
            {
                foreach (var row in items.Rows)
                    config.AddItem((ItemDef)row);
            }
            return config;
        }
    }
}
