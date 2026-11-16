#nullable enable
using System;
using System.Collections.Generic;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using Shared.Protocol;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Contracts
{
    /// <summary>What the player does with the own portfolio (the Jobs window's portfolio tab; WP-M4-10 part B).</summary>
    public interface IPortfolioEditor
    {
        /// <summary>Opens (or closes) the pin picker for showcase slot <paramref name="showcaseSlot"/>.</summary>
        void OpenPinPicker(int showcaseSlot);
        /// <summary>Pins row <paramref name="row"/> of the open picker.</summary>
        void PickPin(int row);
        void Unpin(int showcaseSlot);
        void SetHidden(bool hidden);
    }

    /// <summary>
    /// One portfolio as UI Toolkit rows (spec 5.4 R-5.4-04, M4 decision 11; WP-M4-10 part B), for the Jobs window's portfolio tab
    /// (editable: pin, unpin, the hide flag) and the read-only views of another player's at the board and in the C window: the
    /// counters (job, item category, rarity, outcome, count), then the showcase of up to <c>ContractSettings.showcaseMax</c> pinned
    /// snapshots. Every text from <c>UiStrings</c>; item names from the items table, never from the server.
    /// </summary>
    public sealed class PortfolioPanel
    {
        private readonly HudConfig _config;
        private readonly ContractConfig _contracts;
        private readonly string _prefix;
        private readonly List<string> _rowTexts = new List<string>();

        public PortfolioPanel(HudConfig config, ContractConfig contracts, string prefix)
        {
            _config = config;
            _contracts = contracts;
            _prefix = prefix;
            Root = HudWidgets.Box(prefix + "-portfolio");
            Root.name = prefix + "_portfolio";
            Root.pickingMode = PickingMode.Position;
        }

        public VisualElement Root { get; }

        /// <summary>The rows shown, top to bottom, as their text (tests and the QA smoke).</summary>
        public IReadOnlyList<string> RowTexts => _rowTexts;

        /// <summary>The button named <paramref name="name"/> in the panel (<c>&lt;prefix&gt;_pin_&lt;slot&gt;</c>, ...), or null.</summary>
        public Button? ButtonNamed(string name) => Root.Q<Button>(name);

        /// <summary>
        /// Draws <paramref name="state"/> (null with <paramref name="waiting"/>: the read is out). With an <paramref name="editor"/>
        /// the showcase slots get Pin or Unpin and the hide flag its button; <paramref name="pinTarget"/> is the slot whose picker is
        /// open, listing <paramref name="pinCandidates"/> (the Normal bag's instances).
        /// </summary>
        public void Render(PortfolioState? state, bool waiting, IPortfolioEditor? editor = null, int pinTarget = -1,
            IReadOnlyList<InventorySlotEntry>? pinCandidates = null)
        {
            Root.Clear();
            _rowTexts.Clear();
            var title = Line(_config.Text.Get(ContractTextIds.PortfolioTitle));
            title.style.color = _config.Theme.Accent;
            if (state == null)
            {
                Line(_config.Text.Get(waiting ? ContractTextIds.PortfolioWaiting : ContractTextIds.PortfolioEmpty));
                return;
            }
            if (state.Hidden)
                Line(_config.Text.Get(ContractTextIds.PortfolioHidden));
            if (editor != null)
            {
                bool hidden = state.Hidden;
                Root.Add(Button(_prefix + "_hide", _config.Text.Get(hidden ? ContractTextIds.PortfolioShow : ContractTextIds.PortfolioHide),
                    () => editor.SetHidden(!hidden)));
            }
            if (state.Counters.Length == 0)
                Line(_config.Text.Get(ContractTextIds.PortfolioEmpty));
            foreach (var counter in state.Counters)
            {
                string job = _contracts.Job(counter.JobNetId) is { } def ? _config.Text.Get(def.NameId) : _config.Text.Get(ContractTextIds.PortfolioAny);
                string rarityId = _contracts.Inventory.RarityTextId(counter.Rarity);
                Line(_config.Text.Get(ContractTextIds.PortfolioCounter, new TextArg("job", job),
                    new TextArg("category", _config.Text.Get(ContractTextIds.Category(counter.Category))),
                    new TextArg("rarity", rarityId.Length > 0 ? _config.Text.Get(rarityId) : _config.Text.Get(ContractTextIds.PortfolioAny)),
                    new TextArg("outcome", _config.Text.Get(ContractTextIds.Outcome(counter.Outcome))),
                    new TextArg("count", NumberText.Of((long)counter.Count))));
            }

            int max = Math.Max(_contracts.ShowcaseMax, state.Showcase.Length);
            Line(_config.Text.Get(ContractTextIds.PortfolioShowcase, new TextArg("count", NumberText.Of(state.Showcase.Length)),
                new TextArg("max", NumberText.Of(max))));
            for (int slot = 0; slot < max; slot++)
            {
                PortfolioShowcaseEntry? pinned = Array.Find(state.Showcase, s => s.ShowcaseSlot == slot);
                var row = Row();
                string text = pinned != null ? ItemName(pinned.Item) : _config.Text.Get(ContractTextIds.PortfolioSlotEmpty);
                var label = Text(text);
                label.style.flexGrow = 1;
                row.Add(label);
                _rowTexts.Add(text);
                if (editor != null)
                {
                    int at = slot;
                    row.Add(pinned != null
                        ? Button(_prefix + "_unpin_" + at, _config.Text.Get(ContractTextIds.PortfolioUnpin), () => editor.Unpin(at))
                        : Button(_prefix + "_pin_" + at, _config.Text.Get(ContractTextIds.PortfolioPin), () => editor.OpenPinPicker(at)));
                }
                Root.Add(row);
                if (editor != null && pinTarget == slot)
                    Picker(editor, pinCandidates ?? Array.Empty<InventorySlotEntry>());
            }
        }

        private void Picker(IPortfolioEditor editor, IReadOnlyList<InventorySlotEntry> candidates)
        {
            var box = HudWidgets.Box(_prefix + "-portfolio-picker");
            box.pickingMode = PickingMode.Position;
            HudWidgets.Border(box, _config.Theme.Accent);
            box.Add(Text(_config.Text.Get(ContractTextIds.PortfolioPickTitle)));
            if (candidates.Count == 0)
                box.Add(Text(_config.Text.Get(ContractTextIds.PortfolioPickEmpty)));
            for (int i = 0; i < candidates.Count; i++)
            {
                int row = i;
                box.Add(Button(_prefix + "_pick_" + i, ItemName(candidates[i]), () => editor.PickPin(row)));
            }
            Root.Add(box);
        }

        private string ItemName(InventorySlotEntry entry) =>
            _contracts.Inventory.Item(entry.ItemNetId) is { } info ? _config.Text.Get(info.Def.NameId) : NumberText.Of((int)entry.ItemNetId);

        private Label Line(string text)
        {
            var label = Text(text);
            Root.Add(label);
            _rowTexts.Add(text);
            return label;
        }

        private VisualElement Row()
        {
            var row = HudWidgets.Box(_prefix + "-portfolio-row");
            row.style.flexDirection = FlexDirection.Row;
            row.pickingMode = PickingMode.Position;
            return row;
        }

        private Label Text(string text)
        {
            var label = HudWidgets.Label(text, _prefix + "-portfolio__text");
            label.enableRichText = false;
            label.style.color = _config.Theme.Text;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        private Button Button(string name, string text, Action onClick)
        {
            var button = new Button(onClick) { focusable = false, name = name, text = text };
            button.enableRichText = false;
            button.style.color = _config.Theme.Text;
            button.style.backgroundColor = _config.Theme.Panel;
            HudWidgets.Border(button, _config.Theme.Border);
            return button;
        }
    }
}
