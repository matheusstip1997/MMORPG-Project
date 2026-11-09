using System;
using System.Collections.Generic;
using Shared.Data.Tables;

namespace Shared.Simulation.Items
{
    /// <summary>Why a unit cannot enter a trade (R-4.1-04, R-4.5-01, M4 decision 5); <see cref="Ok"/> when it can.</summary>
    public enum TradeOfferCheck : byte
    {
        Ok = 0,
        /// <summary>A sell-only drop (R-4.1-01: only for selling at outposts).</summary>
        SellOnly = 1,
        /// <summary>A quest item (bound to the player).</summary>
        Quest = 2,
        /// <summary>A stolen-tagged unit or a tainted instance (R-4.4-03).</summary>
        Stolen = 3,
        /// <summary>An instance whose own non-tradable flag is set.</summary>
        NotTradable = 4,
        /// <summary>Gear that is equipped.</summary>
        Equipped = 5,
    }

    /// <summary>
    /// The pure rules of a transfer between two owners (spec 4.5 "M4 trade window", WP-M4-4): which units may be offered, whether
    /// everything a side receives fits its containers (stack merges included, after its own offered units left), and the owner
    /// history a traded instance carries (spec 4.1). Shared, so the client can dim units it may not offer.
    /// </summary>
    public static class TradeRules
    {
        /// <summary>
        /// R-4.1-04 and decision 5: sell-only, quest, stolen or tainted, non-tradable and equipped units never enter a trade, each
        /// with its own reason; molds, tools, gems and materials trade. The first matching reason in that order.
        /// </summary>
        public static TradeOfferCheck CheckOffer(ItemCategory category, bool stolen, bool tainted, bool instanceNonTradable,
            bool inEquipped)
        {
            if (category == ItemCategory.SellOnly)
                return TradeOfferCheck.SellOnly;
            if (category == ItemCategory.Quest)
                return TradeOfferCheck.Quest;
            if (stolen || tainted)
                return TradeOfferCheck.Stolen;
            if (instanceNonTradable)
                return TradeOfferCheck.NotTradable;
            if (inEquipped)
                return TradeOfferCheck.Equipped;
            return TradeOfferCheck.Ok;
        }

        /// <summary>
        /// Adds every unit of <paramref name="incoming"/> to <paramref name="scratch"/> (a copy of the receiving container, the
        /// receiver's own outgoing units already taken out), merging onto stacks of the same key first. True when all of them
        /// fit. <paramref name="scratch"/> is changed; pass a copy.
        /// </summary>
        public static bool FitsAll(Bag scratch, IReadOnlyList<ItemStack> incoming, IReadOnlyList<int> maxStacks)
        {
            if (incoming.Count != maxStacks.Count)
                throw new ArgumentException("one max stack per incoming stack", nameof(maxStacks));
            for (int i = 0; i < incoming.Count; i++)
            {
                if (incoming[i].IsEmpty)
                    continue;
                if (scratch.Add(incoming[i], incoming[i].IsInstance ? 1 : maxStacks[i], null) < incoming[i].Count)
                    return false;
            }
            return true;
        }

        /// <summary>A copy of <paramref name="bag"/> (the scratch of <see cref="FitsAll"/>).</summary>
        public static Bag Copy(Bag bag)
        {
            var copy = new Bag(bag.SlotCount);
            for (int slot = 0; slot < bag.SlotCount; slot++)
                copy.Load(slot, bag[slot]);
            return copy;
        }

        /// <summary>
        /// Spec 4.1 (owner history, "TODO(M4)" closed by WP-M4-4): a traded instance records the giver. Appends
        /// <paramref name="previousOwner"/> unless it is already the last entry, then keeps the newest <paramref name="max"/>.
        /// </summary>
        public static void AppendOwner(List<long> history, long previousOwner, int max)
        {
            if (previousOwner <= 0 || max <= 0)
                return;
            if (history.Count == 0 || history[history.Count - 1] != previousOwner)
                history.Add(previousOwner);
            if (history.Count > max)
                history.RemoveRange(0, history.Count - max);
        }
    }
}
