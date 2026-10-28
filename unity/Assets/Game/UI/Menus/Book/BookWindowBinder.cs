#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Book;
using Game.UI.ViewModels.Hud;
using Shared.Protocol;
using UnityEngine.InputSystem;

namespace Game.UI.Menus.Book
{
    /// <summary>What the player does in the Bystial book window; the binder turns it into requests (WP-M2-8).</summary>
    public interface IBookWindowActions
    {
        void SelectCard(ushort monsterNetId);
        void PlaceInto(int slot);
        void TakeOut();
        void CloseBook();
    }

    /// <summary>
    /// Connects the Bystial book window (spec 14.2, 9.13; WP-M2-8) to the world client: the book states and change answers
    /// of the <see cref="WorldClientFeed"/> go into the <see cref="BookWindowViewModel"/>; a card placed, swapped or taken
    /// out goes out through <see cref="WorldClientFeed.BookRequests"/>, one at a time; refusals show their
    /// <c>ReasonCodeStrings</c> text in the HUD toast, and a new or evolved card a news toast. The toggle key is the
    /// <c>Hotkeys</c> row <c>toggleBook</c>. Display time is the caller's clock in ms.
    /// </summary>
    public sealed class BookWindowBinder : IBookWindowActions, IDisposable
    {
        private readonly BookConfig _config;
        private readonly UiText _text;
        private readonly BookWindowViewModel _window;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private readonly InputAction? _toggle;
        private WorldClientFeed? _feed;

        /// <param name="readToggleKey">False in tests: no Input System action is made for the toggle key.</param>
        public BookWindowBinder(BookConfig config, UiText text, BookWindowViewModel window, ToastViewModel toast, int toastMs,
            Func<double> nowMs, bool readToggleKey = true)
        {
            _config = config;
            _text = text;
            _window = window;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
            if (readToggleKey && config.ToggleBinding.Length > 0)
            {
                _toggle = new InputAction("ToggleBook", InputActionType.Button, config.ToggleBinding);
                _toggle.Enable();
            }
        }

        public WorldClientFeed? Feed => _feed;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.BookReceived += OnBook;
            feed.BookChangeAnswered += OnAnswered;
            feed.Disconnected += OnDisconnected;
            if (feed.Book != null) // it may have come before the HUD bound
                OnBook(feed.Book);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.BookReceived -= OnBook;
            _feed.BookChangeAnswered -= OnAnswered;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void Dispose()
        {
            Unbind();
            _toggle?.Dispose();
        }

        /// <summary>Once per frame: the toggle key (ignored while the chat input has focus: a "b" typed in a line is text).</summary>
        public void Frame(double nowMs)
        {
            if (_toggle != null && _toggle.WasPressedThisFrame() && !(_feed?.TextInputActive ?? false))
                _window.Toggle();
        }

        public void SelectCard(ushort monsterNetId) => _window.Select(monsterNetId);

        /// <summary>The selected card into <paramref name="slot"/>; an occupied slot swaps (the server decides).</summary>
        public void PlaceInto(int slot)
        {
            if (_window.CanPlace(slot))
                Send(_window.Selected, (byte)slot);
        }

        public void TakeOut()
        {
            if (_window.CanTakeOut)
                Send(_window.Selected, BookLimits.NoSlot);
        }

        public void CloseBook() => _window.SetOpen(false);

        private void Send(ushort monsterNetId, byte slot)
        {
            uint requestId = _feed?.BookRequests?.RequestBookChange(monsterNetId, slot) ?? 0;
            if (requestId != 0)
                _window.SetPending(requestId);
        }

        private void OnBook(BookState state)
        {
            foreach (var news in _window.SetState(state))
            {
                var card = _config.Card(news.MonsterNetId);
                string name = card == null ? news.MonsterNetId.ToString() : _text.Get(card.NameId);
                _toast.ShowText(news.Earned ? BookTextIds.ToastEarned : BookTextIds.ToastEvolved, "card",
                    _text.Get(BookTextIds.Card, new TextArg("name", name), new TextArg("tier", _text.Get(Shared.Data.Tables.UiStringCoverage.BookTierId(news.Tier)))),
                    _now(), _toastMs);
            }
        }

        private void OnAnswered(BookChangeResult result)
        {
            string? refused = _window.OnResult(result);
            if (refused != null)
                _toast.Show(refused, false, _now(), _toastMs);
        }

        private void OnDisconnected(string reasonCode) => _window.Clear();
    }
}
