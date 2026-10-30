#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.Menus.Outpost;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using Shared.Protocol;
using UnityEngine.InputSystem;

namespace Game.UI.HUD
{
    /// <summary>
    /// Connects the quick-item slot (spec 3.4 consumable use, 9.13; WP-M3-3) to the world client: once the character is known
    /// (<see cref="WorldClientFeed.CharacterKey"/> and its bag) it reads the binding from the per-character store; every frame
    /// it shows the bound consumable, its count and its cooldown; the <c>Hotkeys</c> row <c>quickItem</c> uses the lowest Normal
    /// bag stack of it through <see cref="WorldClientFeed.UseItemRequests"/> (a local refusal shows in the toast; the server's
    /// answers are toasted by the inventory window's binder). A consumable dragged onto the slot binds it
    /// (<see cref="TryBind"/>). Display time is the caller's clock in ms.
    /// </summary>
    public sealed class QuickItemBinder : IDisposable
    {
        private readonly QuickItemViewModel _model;
        private readonly InventoryConfig _inventory;
        private readonly UiText _text;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private readonly Func<string, IQuickItemStore> _storeFor;
        private readonly InputAction? _key;
        private readonly string _keyName;
        private WorldClientFeed? _feed;
        private bool _loaded;

        /// <param name="readKey">False in tests: no Input System action is made for the key.</param>
        public QuickItemBinder(Shared.Data.ConfigSet tables, InventoryConfig inventory, UiText text, QuickItemViewModel model,
            ToastViewModel toast, int toastMs, Func<double> nowMs, Func<string, IQuickItemStore> storeFor, bool readKey = true)
        {
            _inventory = inventory;
            _text = text;
            _model = model;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
            _storeFor = storeFor;
            string binding = Hotkeys.Find(tables, Hotkeys.QuickItem)?.Binding ?? "";
            _keyName = OutpostBinder.KeyNameOf(binding);
            if (readKey && binding.Length > 0)
            {
                _key = new InputAction("QuickItem", InputActionType.Button, binding);
                _key.Enable();
            }
        }

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.Disconnected += OnDisconnected;
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void Dispose()
        {
            Unbind();
            _key?.Dispose();
        }

        /// <summary>Once per frame: the binding once the character is known, the slot's content, and the key.</summary>
        public void Frame(double nowMs)
        {
            var consumables = _feed?.Consumables;
            if (_feed == null || consumables == null)
                return;
            if (!_loaded && _feed.CharacterKey.Length > 0 && _feed.Inventory != null)
            {
                consumables.LoadQuickItem(_storeFor(_feed.CharacterKey));
                _loaded = true;
            }
            int netId = consumables.NetIdOf(consumables.QuickItemId);
            consumables.QuickSlot(_feed.Inventory, out _, out int held);
            _model.Set((ushort)netId, held, _keyName);
            if (netId != 0 && consumables.TryGet(netId, out var def))
                _model.SetCooldown(consumables.ReadyAtMs(netId), def.CooldownMs);
            if (_key != null && _key.WasPressedThisFrame() && !_feed.TextInputActive)
                Use();
        }

        /// <summary>The quick-item key: use the bound consumable's lowest Normal bag stack.</summary>
        public void Use()
        {
            var consumables = _feed?.Consumables;
            if (_feed?.UseItemRequests == null || consumables == null || consumables.QuickItemId.Length == 0)
                return;
            if (!consumables.QuickSlot(_feed.Inventory, out byte slot, out _))
            {
                Toast(UseItemReason.ItemSlotEmpty);
                return;
            }
            var reason = _feed.UseItemRequests.RequestUseItem(0, slot, out _);
            if (reason != UseItemReason.None)
                Toast(reason);
        }

        /// <summary>
        /// A stack dropped on the slot (the inventory window's drag): binds a consumable to the key and saves it for the
        /// character; false for any other item (the window then refuses the drop as usual).
        /// </summary>
        public bool TryBind(ushort itemNetId)
        {
            var consumables = _feed?.Consumables;
            if (consumables == null || !consumables.BindQuickItem(itemNetId))
                return false;
            var item = _inventory.Item(itemNetId);
            string name = item != null ? _text.Get(item.Def.NameId) : "";
            _toast.ShowText(HudTextIds.ToastQuickItemBound, "item", name, _now(), _toastMs);
            return true;
        }

        private void Toast(UseItemReason reason) => _toast.Show(reason.ToString(), false, _now(), _toastMs);

        private void OnDisconnected(string reasonCode)
        {
            _loaded = false; // another character may come next
            _model.Set(0, 0, _keyName);
        }
    }
}
