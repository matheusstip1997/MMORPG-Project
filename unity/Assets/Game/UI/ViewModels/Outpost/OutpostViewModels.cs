#nullable enable
using System;
using System.Collections.Generic;
using Game.UI.Config;
using Shared.Protocol;

namespace Game.UI.ViewModels.Outpost
{
    /// <summary>A stack of the bag the outpost takes (sell-only, not stolen), as the sale window lists it.</summary>
    public readonly struct SellableStack
    {
        public SellableStack(byte slot, ushort itemNetId, int count)
        {
            Slot = slot;
            ItemNetId = itemNetId;
            Count = count;
        }

        public byte Slot { get; }
        public ushort ItemNetId { get; }
        public int Count { get; }
    }

    /// <summary>A sale the player confirmed: the stack, the units and the quoted total (the server never pays less).</summary>
    public readonly struct SellIntent
    {
        public SellIntent(byte slot, ushort count, long quotedTotal)
        {
            Slot = slot;
            Count = count;
            QuotedTotal = quotedTotal;
        }

        public byte Slot { get; }
        public ushort Count { get; }
        public long QuotedTotal { get; }
    }

    /// <summary>Where the sale window's quote stands.</summary>
    public enum QuoteState
    {
        /// <summary>No stack selected, or the selection changed: a quote is needed.</summary>
        None,
        /// <summary>Asked for; waiting for the server.</summary>
        Pending,
        /// <summary>The server's quote for the current selection: the sale may be confirmed.</summary>
        Ready,
    }

    /// <summary>
    /// The outpost sale window (spec 4.2, 9.13 M2; WP-M2-4): the sellable stacks of the committed bag, the selected stack
    /// and count, the server's quote for it, and the last result. The client never holds the truth: the quote and the sale
    /// come from the server; a refusal's reason code goes to the HUD toast.
    /// </summary>
    public sealed class OutpostWindowViewModel : ViewModel
    {
        public const int None = -1;

        private readonly List<SellableStack> _stacks = new List<SellableStack>();
        private uint _pendingQuote;
        private uint _pendingSale;

        public bool Open { get; private set; }

        /// <summary>The outpost NPC the window talks to; 0 while closed.</summary>
        public uint NpcEntityId { get; private set; }

        public IReadOnlyList<SellableStack> Stacks => _stacks;

        /// <summary>The selected bag slot, or <see cref="None"/>.</summary>
        public int SelectedSlot { get; private set; } = None;

        /// <summary>Units of the selected stack to sell.</summary>
        public int Count { get; private set; }

        public QuoteState Quote { get; private set; }

        /// <summary>The quote for the current selection while <see cref="Quote"/> is Ready.</summary>
        public SellQuoteResult? LastQuote { get; private set; }

        /// <summary>A sale is on its way (sent, not answered yet): the confirm button waits.</summary>
        public bool Selling => _pendingSale != 0;

        /// <summary>The last sale that went through: units and gold; null before one.</summary>
        public (int Count, long Gold)? LastSold { get; private set; }

        public int StackCount(byte slot)
        {
            foreach (var stack in _stacks)
            {
                if (stack.Slot == slot)
                    return stack.Count;
            }
            return 0;
        }

        public void OpenFor(uint npcEntityId)
        {
            Open = true;
            NpcEntityId = npcEntityId;
            LastSold = null;
            Deselect();
            NotifyChanged();
        }

        public void Close()
        {
            if (!Open)
                return;
            Open = false;
            NpcEntityId = 0;
            Deselect();
            NotifyChanged();
        }

        /// <summary>
        /// The committed bag: the stacks the outpost takes, in slot order. A selected stack that changed or left loses its
        /// quote (true when a new quote is needed for a selection that still exists).
        /// </summary>
        public bool SetContents(InventoryContents contents, Func<ushort, bool, bool> sellable)
        {
            _stacks.Clear();
            foreach (var slot in contents.Slots)
            {
                if (sellable(slot.ItemNetId, slot.Stolen))
                    _stacks.Add(new SellableStack(slot.Slot, slot.ItemNetId, slot.Count));
            }
            _stacks.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            bool requote = false;
            if (SelectedSlot != None)
            {
                int held = StackCount((byte)SelectedSlot);
                if (held == 0)
                    Deselect();
                else if (held < Count || (LastQuote != null && LastQuote.Count > held))
                {
                    Count = Math.Min(Count, held);
                    InvalidateQuote();
                    requote = true;
                }
            }
            NotifyChanged();
            return requote;
        }

        /// <summary>Selects a stack with all its units; true when a quote should be asked for.</summary>
        public bool Select(byte slot)
        {
            int held = StackCount(slot);
            if (held == 0)
                return false;
            SelectedSlot = slot;
            Count = held;
            InvalidateQuote();
            NotifyChanged();
            return true;
        }

        /// <summary>Changes the count by <paramref name="delta"/> inside 1 to the stack; true when it changed (requote).</summary>
        public bool Step(int delta) => SetCount(Count + delta);

        /// <summary>The whole stack; true when it changed (requote).</summary>
        public bool SelectAll() => SelectedSlot != None && SetCount(StackCount((byte)SelectedSlot));

        /// <summary>What to ask a quote for; false without a selection.</summary>
        public bool TryQuoteRequest(out byte slot, out ushort count)
        {
            slot = SelectedSlot == None ? (byte)0 : (byte)SelectedSlot;
            count = (ushort)Count;
            return Open && SelectedSlot != None && Count > 0;
        }

        public void QuoteSent(uint requestId)
        {
            _pendingQuote = requestId;
            Quote = QuoteState.Pending;
            LastQuote = null;
            NotifyChanged();
        }

        /// <summary>The server's quote; a stale one (not the last asked) is ignored. Returns a refusal's reason code, else null.</summary>
        public string? OnQuote(SellQuoteResult quote)
        {
            if (quote.RequestId != _pendingQuote)
                return null;
            _pendingQuote = 0;
            if (quote.Reason != OutpostReason.None)
            {
                Quote = QuoteState.None;
                LastQuote = null;
                NotifyChanged();
                return quote.Reason.ToString();
            }
            Quote = QuoteState.Ready;
            LastQuote = quote;
            NotifyChanged();
            return null;
        }

        /// <summary>The sale to send when the player confirms; null unless a quote for the selection is ready.</summary>
        public SellIntent? Confirm()
        {
            if (!Open || Quote != QuoteState.Ready || LastQuote == null || Selling)
                return null;
            return new SellIntent(LastQuote.Slot, LastQuote.Count, LastQuote.Total);
        }

        public void SaleSent(uint requestId)
        {
            _pendingSale = requestId;
            NotifyChanged();
        }

        /// <summary>
        /// The server's answer to the sale: on success the result line shows it and the selection clears; a refusal returns
        /// its reason code for the toast (after <c>QuoteExpired</c> the caller asks for a new quote).
        /// </summary>
        public string? OnSale(SellResult result)
        {
            if (result.RequestId != _pendingSale)
                return null;
            _pendingSale = 0;
            if (result.Reason != OutpostReason.None)
            {
                InvalidateQuote();
                NotifyChanged();
                return result.Reason.ToString();
            }
            LastSold = (result.Count, result.Gold);
            Deselect();
            NotifyChanged();
            return null;
        }

        /// <summary>The connection ended: nothing shown is current.</summary>
        public void Clear()
        {
            _stacks.Clear();
            Open = false;
            NpcEntityId = 0;
            LastSold = null;
            Deselect();
            NotifyChanged();
        }

        private bool SetCount(int count)
        {
            if (SelectedSlot == None)
                return false;
            int clamped = Math.Max(1, Math.Min(count, StackCount((byte)SelectedSlot)));
            if (clamped == Count)
                return false;
            Count = clamped;
            InvalidateQuote();
            NotifyChanged();
            return true;
        }

        private void Deselect()
        {
            SelectedSlot = None;
            Count = 0;
            _pendingSale = 0;
            InvalidateQuote();
        }

        private void InvalidateQuote()
        {
            Quote = QuoteState.None;
            LastQuote = null;
            _pendingQuote = 0;
        }
    }

    /// <summary>A purchase the player confirmed: the item, the units, the total at the fixed price.</summary>
    public readonly struct BuyIntent
    {
        public BuyIntent(ushort itemNetId, ushort count, long total)
        {
            ItemNetId = itemNetId;
            Count = count;
            Total = total;
        }

        public ushort ItemNetId { get; }
        public ushort Count { get; }
        public long Total { get; }
    }

    /// <summary>
    /// The vendor window (R-4.2-01: potions and food at fixed prices; spec 9.13 M2; WP-M2-4): the vendor's stock from the
    /// tables, the selected row and count, the total, and the last result. The server checks the gold and the bag.
    /// </summary>
    public sealed class VendorWindowViewModel : ViewModel
    {
        public const int None = -1;

        private IReadOnlyList<StockEntry> _stock = Array.Empty<StockEntry>();
        private uint _pending;

        public bool Open { get; private set; }

        public uint NpcEntityId { get; private set; }

        public IReadOnlyList<StockEntry> Stock => _stock;

        public int Selected { get; private set; } = None;

        public int Count { get; private set; }

        /// <summary>The fixed price times the count; 0 without a selection.</summary>
        public long Total => Selected == None ? 0 : (long)_stock[Selected].PriceGold * Count;

        public bool Buying => _pending != 0;

        public (int Count, long Gold)? LastBought { get; private set; }

        public void OpenFor(uint npcEntityId, IReadOnlyList<StockEntry> stock)
        {
            Open = true;
            NpcEntityId = npcEntityId;
            _stock = stock;
            Selected = stock.Count > 0 ? 0 : None;
            Count = stock.Count > 0 ? 1 : 0;
            LastBought = null;
            _pending = 0;
            NotifyChanged();
        }

        public void Close()
        {
            if (!Open)
                return;
            Open = false;
            NpcEntityId = 0;
            Selected = None;
            Count = 0;
            _pending = 0;
            NotifyChanged();
        }

        public void Select(int index)
        {
            if (index < 0 || index >= _stock.Count || index == Selected)
                return;
            Selected = index;
            Count = 1;
            NotifyChanged();
        }

        /// <summary>Changes the count inside 1 to one stack of the item.</summary>
        public void Step(int delta)
        {
            if (Selected == None)
                return;
            int clamped = Math.Max(1, Math.Min(Count + delta, _stock[Selected].MaxCount));
            if (clamped == Count)
                return;
            Count = clamped;
            NotifyChanged();
        }

        public BuyIntent? Confirm()
        {
            if (!Open || Selected == None || Count <= 0 || Buying)
                return null;
            return new BuyIntent(_stock[Selected].ItemNetId, (ushort)Count, Total);
        }

        public void PurchaseSent(uint requestId)
        {
            _pending = requestId;
            NotifyChanged();
        }

        /// <summary>The server's answer; a refusal returns its reason code for the toast.</summary>
        public string? OnPurchase(VendorBuyResult result)
        {
            if (result.RequestId != _pending)
                return null;
            _pending = 0;
            if (result.Reason == OutpostReason.None)
                LastBought = (result.Count, result.GoldSpent);
            NotifyChanged();
            return result.Reason == OutpostReason.None ? null : result.Reason.ToString();
        }

        public void Clear()
        {
            Open = false;
            NpcEntityId = 0;
            _stock = Array.Empty<StockEntry>();
            Selected = None;
            Count = 0;
            _pending = 0;
            LastBought = null;
            NotifyChanged();
        }
    }

    /// <summary>The gold on the HUD (spec 9.3 <c>gold</c>; WP-M2-4): the committed balance, hidden before the server sent it.</summary>
    public sealed class GoldViewModel : ViewModel
    {
        public long? Gold { get; private set; }

        public void Set(long? gold)
        {
            if (gold == Gold)
                return;
            Gold = gold;
            NotifyChanged();
        }
    }

    /// <summary>
    /// The NPC prompt (spec 9.3 <c>npc_prompt</c>; WP-M2-4): "Press T to trade" at the NPC the interaction key talks to
    /// (<c>ClientOutposts.NearestNpc</c>, the server's range rule). Hidden while a window of that NPC is open.
    /// </summary>
    public sealed class NpcPromptViewModel : ViewModel
    {
        public bool Visible { get; private set; }

        public uint NpcEntityId { get; private set; }

        /// <summary>The <c>UiStrings</c> id of the prompt (by NPC kind).</summary>
        public string TextId { get; private set; } = "";

        public string KeyName { get; private set; } = "";

        public void Set(uint npcEntityId, string textId, string keyName)
        {
            bool visible = npcEntityId != 0 && textId.Length > 0;
            if (visible == Visible && npcEntityId == NpcEntityId && textId == TextId && keyName == KeyName)
                return;
            Visible = visible;
            NpcEntityId = visible ? npcEntityId : 0;
            TextId = textId;
            KeyName = keyName;
            NotifyChanged();
        }

        public void Clear() => Set(0, "", KeyName);
    }
}
