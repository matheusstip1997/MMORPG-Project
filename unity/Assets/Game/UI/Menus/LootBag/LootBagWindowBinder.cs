#nullable enable
using System;
using System.Globalization;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.LootBag;
using Shared.Protocol;

namespace Game.UI.Menus.LootBag
{
    /// <summary>What the player does in the loot bag window; the binder turns it into requests (WP-M4-9).</summary>
    public interface ILootBagWindowActions
    {
        /// <summary>Takes one row (its <see cref="LootBagRowEntry.Index"/>).</summary>
        void TakeRow(byte row);

        void TakeAll();

        void CloseBag();
    }

    /// <summary>
    /// Connects the death loot bag window (spec 4.3 R-4.3-03, 9.13; WP-M4-9) to the world client. The pickup key at a bag (the
    /// world client sends the open) brings its contents, which open the window; every committed change while it is open and in
    /// reach brings them again, and a closed bag (taken, expired, out of reach) closes it. A row or Take all asks for a take through
    /// <see cref="WorldClientFeed.LootBagRequests"/>, one at a time; the answer comes after the commit: a refusal shows its
    /// <c>ReasonCodeStrings</c> text in the HUD toast, a take a news toast of what moved (back to the bag, by mail, or stolen). The
    /// countdowns redraw about once a second. Display time is the caller's clock in ms.
    /// </summary>
    public sealed class LootBagWindowBinder : ILootBagWindowActions, IDisposable
    {
        private const double RedrawMs = 1_000;

        private readonly LootBagWindowViewModel _window;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private WorldClientFeed? _feed;
        private double _redrawAtMs;

        public LootBagWindowBinder(LootBagWindowViewModel window, ToastViewModel toast, int toastMs, Func<double> nowMs)
        {
            _window = window;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
        }

        public WorldClientFeed? Feed => _feed;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.LootBagContentReceived += OnContent;
            feed.LootBagAnswered += OnAnswered;
            feed.Disconnected += OnDisconnected;
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.LootBagContentReceived -= OnContent;
            _feed.LootBagAnswered -= OnAnswered;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void Dispose() => Unbind();

        /// <summary>Once per frame: the countdowns redraw about once a second while the window is open.</summary>
        public void Frame(double nowMs)
        {
            if (!_window.Open || nowMs < _redrawAtMs)
                return;
            _redrawAtMs = nowMs + RedrawMs;
            _window.Redraw();
        }

        public void TakeRow(byte row)
        {
            if (_window.CanTake)
                SendTake(_feed?.LootBagRequests?.TakeFromLootBag(_window.BagEntityId, row) ?? 0);
        }

        public void TakeAll()
        {
            if (_window.CanTake)
                SendTake(_feed?.LootBagRequests?.TakeAllFromLootBag(_window.BagEntityId) ?? 0);
        }

        public void CloseBag()
        {
            uint bag = _window.BagEntityId;
            _window.Close();
            if (bag != 0)
                _feed?.LootBagRequests?.CloseLootBag(bag);
        }

        private void SendTake(uint requestId)
        {
            if (requestId != 0)
                _window.SetPendingTake(requestId);
        }

        private void OnContent(LootBagContent content)
        {
            _window.SetContent(content, _now());
            _redrawAtMs = _now() + RedrawMs;
        }

        private void OnAnswered(LootBagResult result)
        {
            var access = _window.PendingAccess; // before the answer clears it; the window may be closed by now
            string? refused = _window.OnResult(result, out bool applied);
            if (refused != null)
            {
                _toast.Show(refused, false, _now(), _toastMs);
                return;
            }
            if (!applied)
                return;
            string count = (result.Taken + result.Mailed).ToString(CultureInfo.InvariantCulture);
            if (result.Stolen)
                _toast.ShowText(LootBagTextIds.ToastStolen, "count", result.Taken.ToString(CultureInfo.InvariantCulture), _now(), _toastMs);
            else if (result.Taken == 0 && result.Mailed > 0 && access == LootBagAccess.CollectForOwner)
                _toast.ShowText(LootBagTextIds.ToastCollected, "count", count, _now(), _toastMs);
            else if (result.Mailed > 0)
                _toast.ShowText(LootBagTextIds.ToastTakenMailed, "count", result.Taken.ToString(CultureInfo.InvariantCulture), "mailed",
                    result.Mailed.ToString(CultureInfo.InvariantCulture), _now(), _toastMs);
            else
                _toast.ShowText(LootBagTextIds.ToastTaken, "count", count, _now(), _toastMs);
        }

        private void OnDisconnected(string reasonCode) => _window.Reset();
    }
}
