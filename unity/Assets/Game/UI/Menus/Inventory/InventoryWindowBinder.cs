#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Inventory;
using Shared.Protocol;
using UnityEngine.InputSystem;

namespace Game.UI.Menus.Inventory
{
    /// <summary>
    /// Connects the inventory window (spec 9.13, WP16) to the world client: the committed bag and the move answers of the
    /// <see cref="WorldClientFeed"/> go into the <see cref="InventoryWindowViewModel"/>; the player's moves go out through
    /// <see cref="WorldClientFeed.Requests"/> and show as a preview until answered; refusals (and a drag out of the window)
    /// show their <c>ReasonCodeStrings</c> text in the HUD toast. The toggle key is the <c>Hotkeys</c> row
    /// <c>toggleInventory</c>. Display time is the caller's clock in ms.
    /// </summary>
    public sealed class InventoryWindowBinder : IInventoryWindowActions, IDisposable
    {
        private readonly InventoryConfig _config;
        private readonly InventoryWindowViewModel _window;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private readonly InputAction? _toggle;
        private WorldClientFeed? _feed;

        /// <param name="readToggleKey">False in tests: no Input System action is made for the toggle key.</param>
        public InventoryWindowBinder(InventoryConfig config, InventoryWindowViewModel window, ToastViewModel toast, int toastMs,
            Func<double> nowMs, bool readToggleKey = true)
        {
            _config = config;
            _window = window;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
            if (readToggleKey && config.ToggleBinding.Length > 0)
            {
                _toggle = new InputAction("ToggleInventory", InputActionType.Button, config.ToggleBinding);
                _toggle.Enable();
            }
        }

        public WorldClientFeed? Feed => _feed;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.InventoryReceived += OnContents;
            feed.InventoryMoveAnswered += OnMoveAnswered;
            feed.Disconnected += OnDisconnected;
            if (feed.Inventory != null) // it may have come before the HUD bound
                OnContents(feed.Inventory);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.InventoryReceived -= OnContents;
            _feed.InventoryMoveAnswered -= OnMoveAnswered;
            _feed.Disconnected -= OnDisconnected;
            _feed.UiCapturesPointer = false;
            _feed = null;
        }

        public void Dispose()
        {
            Unbind();
            _toggle?.Dispose();
        }

        /// <summary>Once per frame: the toggle key, and moves the server never answered.</summary>
        public void Frame(double nowMs)
        {
            if (_toggle != null && _toggle.WasPressedThisFrame())
                _window.Toggle();
            _window.Expire(nowMs, _config.Settings.MovePreviewTimeoutMs);
        }

        public void Click(int slot) => Send(_window.Click(slot));

        public void Drop(int from, int to) => Send(_window.Drop(from, to));

        public void DropOutside(int from) => _toast.Show(_window.DropOutside(from), false, _now(), _toastMs);

        public void Close() => _window.SetOpen(false);

        private void Send(MoveIntent? intent)
        {
            if (intent == null || _feed?.Requests == null)
                return;
            var move = intent.Value;
            uint requestId = _feed.Requests.RequestInventoryMove(move.FromSlot, move.ToSlot);
            if (requestId != 0)
                _window.AddPending(requestId, move, _now());
        }

        private void OnContents(InventoryContents contents) => _window.SetContents(contents);

        private void OnMoveAnswered(InventoryMoveResult result)
        {
            string? refused = _window.OnMoveResult(result);
            if (refused != null)
                _toast.Show(refused, false, _now(), _toastMs);
        }

        private void OnDisconnected() => _window.Clear();
    }
}
