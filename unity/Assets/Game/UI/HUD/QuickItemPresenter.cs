#nullable enable
using Game.Gameplay.Entities.Visuals;
using Game.UI.Config;
using Game.UI.Menus.Inventory;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.HUD
{
    /// <summary>
    /// Spec 9.3/9.13 <c>quick_item</c> (spec 3.4 consumable use; WP-M3-3): one square right of the hotbar with the bound
    /// consumable's icon (<c>ItemIconRegistry</c>), the count held, the key (<c>Hotkeys</c> <c>quickItem</c>) and a cooldown
    /// sweep; empty, a hint to drag a potion onto it. A drop target for the inventory window (<see cref="Contains"/>).
    /// </summary>
    public sealed class QuickItemPresenter : HudPresenter
    {
        private readonly QuickItemViewModel _model;
        private readonly InventoryConfig _inventory;
        private readonly AddressablesTextureSource? _icons;
        private VisualElement _root = null!;
        private ItemIconElement _icon = null!;
        private Label _count = null!;
        private Label _key = null!;
        private Label _hint = null!;
        private VisualElement _cooldown = null!;

        public QuickItemPresenter(HudConfig config, HudView view, QuickItemViewModel model, InventoryConfig inventory,
            AddressablesTextureSource? icons) : base(config, view, model)
        {
            _model = model;
            _inventory = inventory;
            _icons = icons;
        }

        /// <summary>True when <paramref name="panelPosition"/> is on the slot (the inventory window's drop test).</summary>
        public bool Contains(Vector2 panelPosition) => _root != null && _root.worldBound.Contains(panelPosition);

        /// <summary>The slot's element (QA checks and screenshots).</summary>
        public VisualElement Root => _root;

        public string CountText => _count.text;

        protected override void Build()
        {
            _root = View.Place(HudElementIds.QuickItem, HudWidgets.Box("hud-slot"));
            _root.name = "quick_item";
            _root.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(_root, Theme.Border);
            _cooldown = HudWidgets.Box("hud-slot__cooldown");
            _root.Add(_cooldown);
            _icon = new ItemIconElement { pickingMode = PickingMode.Ignore };
            _icon.AddToClassList("inv-slot__icon");
            _root.Add(_icon);
            _count = HudWidgets.Label("", "hud-slot__seconds");
            _count.style.fontSize = Theme.Tokens.FontSizeStack;
            _root.Add(_count);
            _key = HudWidgets.Label("", "hud-slot__key");
            _key.style.fontSize = Theme.Tokens.FontSizeSlotLabel;
            _root.Add(_key);
            _hint = HudWidgets.Label(Config.Text.Get(HudTextIds.QuickItemEmpty), "hud-slot__name");
            _hint.style.fontSize = Theme.Tokens.FontSizeSlotLabel;
            _hint.style.whiteSpace = WhiteSpace.Normal;
            _root.Add(_hint);
        }

        protected override void Refresh()
        {
            HudWidgets.SetText(_key, _model.KeyName);
            Show(_hint, !_model.IsBound);
            if (!_model.IsBound)
            {
                _icon.SetEmpty();
                HudWidgets.SetText(_count, "");
                return;
            }
            var row = _inventory.Item(_model.ItemNetId)?.Icon;
            Texture2D? texture = null;
            if (row != null && row.IconAddress.Length > 0 && _icons != null
                && _icons.Get(row.IconAddress, out var loaded) == PrefabState.Ready)
                texture = loaded;
            _icon.Set(row?.PlaceholderShape ?? IconShape.Square, row != null ? HudTheme.ColorOf(row.PlaceholderColor) : Theme.Locked, texture);
            HudWidgets.SetText(_count, NumberText.Of(_model.Held));
            _root.EnableInClassList("hud-slot--empty", _model.Held == 0); // bound, none left: greyed
        }

        public override void Animate(double nowMs) =>
            _cooldown.style.height = Length.Percent(_model.CooldownLeft(nowMs) * 100f);
    }
}
