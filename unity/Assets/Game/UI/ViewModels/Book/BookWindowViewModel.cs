#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.UI.ViewModels.Book
{
    /// <summary>A card that is new or reached a higher tier in the latest book state (for the news toast).</summary>
    public readonly struct BookNews
    {
        public BookNews(ushort monsterNetId, BystialTier tier, bool earned)
        {
            MonsterNetId = monsterNetId;
            Tier = tier;
            Earned = earned;
        }

        public ushort MonsterNetId { get; }
        public BystialTier Tier { get; }

        /// <summary>A first card (else an evolution).</summary>
        public bool Earned { get; }
    }

    /// <summary>
    /// The Bystial book window (spec 14.2, 9.13; WP-M2-8): the last <see cref="BookState"/> the server sent (the collection
    /// with its counters, the slots its rank opens), the selected card, and the one change waiting for its answer. Display
    /// only: the server decides every card and checks every change; a refusal comes back as its reason code.
    /// </summary>
    public sealed class BookWindowViewModel : ViewModel
    {
        private BookCard[] _cards = Array.Empty<BookCard>();
        private readonly List<BookNews> _news = new List<BookNews>();

        public bool Open { get; private set; }

        /// <summary>The collection in the server's order (monster id order); empty before the first state.</summary>
        public IReadOnlyList<BookCard> Cards => _cards;

        /// <summary>Slots the character's rank opens (R-14.2-04).</summary>
        public int SlotCount { get; private set; }

        public bool HasState { get; private set; }

        /// <summary>The selected card's monster net id; 0 for none.</summary>
        public ushort Selected { get; private set; }

        /// <summary>The change waiting for its answer; 0 for none (one at a time, so the view never runs ahead of the server).</summary>
        public uint Pending { get; private set; }

        public void SetOpen(bool open)
        {
            if (Open == open)
                return;
            Open = open;
            NotifyChanged();
        }

        public void Toggle() => SetOpen(!Open);

        /// <summary>
        /// Takes a new book state and returns the cards that are new or evolved since the previous state (none for the first
        /// state of a connection, which is no news). A selection whose card is gone is cleared.
        /// </summary>
        public IReadOnlyList<BookNews> SetState(BookState state)
        {
            _news.Clear();
            if (HasState)
            {
                foreach (var card in state.Cards)
                {
                    var before = Find(_cards, card.MonsterNetId);
                    var was = before?.Tier ?? BystialTier.None;
                    if (card.Tier > was)
                        _news.Add(new BookNews(card.MonsterNetId, card.Tier, was == BystialTier.None));
                }
            }
            _cards = state.Cards;
            SlotCount = state.SlotCount;
            HasState = true;
            if (Selected != 0 && Find(_cards, Selected) == null)
                Selected = 0;
            NotifyChanged();
            return _news;
        }

        /// <summary>The card of a monster net id in the latest state; null when the state has none.</summary>
        public BookCard? Card(ushort monsterNetId) => Find(_cards, monsterNetId);

        public BookCard? SelectedCard => Selected == 0 ? null : Find(_cards, Selected);

        /// <summary>The card in <paramref name="slot"/>; null when it is empty.</summary>
        public BookCard? InSlot(int slot)
        {
            foreach (var card in _cards)
            {
                if (card.Slot == slot)
                    return card;
            }
            return null;
        }

        /// <summary>Selects a card of the collection (a second click clears it).</summary>
        public void Select(ushort monsterNetId)
        {
            Selected = Selected == monsterNetId || Find(_cards, monsterNetId) == null ? (ushort)0 : monsterNetId;
            NotifyChanged();
        }

        /// <summary>The selected card can go into <paramref name="slot"/>: earned, not already in a slot, an open slot, nothing pending.</summary>
        public bool CanPlace(int slot) =>
            Pending == 0 && slot >= 0 && slot < SlotCount && SelectedCard is { } card && card.Tier != BystialTier.None
            && card.Slot == BookLimits.NoSlot;

        /// <summary>The selected card is in a slot and nothing is pending.</summary>
        public bool CanTakeOut => Pending == 0 && SelectedCard is { } card && card.Slot != BookLimits.NoSlot;

        public void SetPending(uint requestId)
        {
            Pending = requestId;
            NotifyChanged();
        }

        /// <summary>The answer to a change: the pending one is done. Returns the reason code of a refusal, else null.</summary>
        public string? OnResult(BookChangeResult result)
        {
            if (result.RequestId != Pending || Pending == 0)
                return null;
            Pending = 0;
            if (result.Reason == BookReason.None)
                Selected = 0; // the next state shows the card in its place
            NotifyChanged();
            return result.Reason == BookReason.None ? null : result.Reason.ToString();
        }

        /// <summary>The connection ended: nothing is known until the next state.</summary>
        public void Clear()
        {
            _cards = Array.Empty<BookCard>();
            SlotCount = 0;
            HasState = false;
            Selected = 0;
            Pending = 0;
            Open = false;
            NotifyChanged();
        }

        private static BookCard? Find(BookCard[] cards, ushort monsterNetId)
        {
            foreach (var card in cards)
            {
                if (card.MonsterNetId == monsterNetId)
                    return card;
            }
            return null;
        }
    }
}
