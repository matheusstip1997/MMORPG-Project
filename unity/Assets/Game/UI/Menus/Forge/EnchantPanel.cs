#nullable enable
using System.Collections.Generic;
using System.Globalization;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Forge;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Forge
{
    /// <summary>What the player does in the enchant tab; the binder turns it into requests (WP-M3-8).</summary>
    public interface IEnchantPanelActions
    {
        void ChooseEnchant(int row);

        void Enchant();

        void ShowEnchantTab(bool active);

        /// <summary>The enchant contract banner's previous or next offer (WP-M4-10 part C).</summary>
        void StepEnchantContract(int delta);

        /// <summary>Works on the contract the banner shows: its escrowed item, Enchant sends its id.</summary>
        void WorkOnEnchantContract();

        /// <summary>Back to the own items.</summary>
        void StopEnchantContract();
    }

    /// <summary>
    /// The enchant tab of the station window (spec 5.3 R-5.3-04, 9.13; WP-M3-8), drawn inside <c>forge_window</c> by
    /// <see cref="ForgeWindowPresenter"/> when the tab is chosen: the items of the character's jobs that enchant at this station
    /// with "+N", the stones a try takes and those held, the table's odds of the chosen item's level, and the Enchant button.
    /// Text from <c>UiStrings</c>; the outcome comes from the server after its save.
    /// </summary>
    public sealed class EnchantPanel
    {
        private readonly UiText _text;
        private readonly HudTheme _theme;
        private readonly IEnchantPanelActions _actions;
        private readonly List<Button> _rows = new List<Button>();
        private VisualElement _root = null!;
        private ScrollView _list = null!;
        private Label _empty = null!;
        private Label _stones = null!;
        private Label _odds = null!;
        private Button _enchant = null!;
        private Label _waiting = null!;
        private VisualElement _banner = null!; // WP-M4-10 part C: the enchant contract banner
        private Label _bannerText = null!;
        private Button _bannerPrevious = null!;
        private Button _bannerNext = null!;
        private Button _bannerWork = null!;
        private Button _bannerStop = null!;

        public bool BannerShown => _banner.style.display == DisplayStyle.Flex;

        public string BannerText => _bannerText.text;

        public Button BannerWork => _bannerWork;

        public Button BannerNext => _bannerNext;

        public Button BannerStop => _bannerStop;

        public EnchantPanel(UiText text, HudTheme theme, EnchantPanelViewModel model, IEnchantPanelActions actions)
        {
            _text = text;
            _theme = theme;
            Model = model;
            _actions = actions;
        }

        public EnchantPanelViewModel Model { get; }

        public IEnchantPanelActions Actions => _actions;

        public VisualElement Root => _root;

        public int RowCount => _rows.Count;

        public Button Row(int index) => _rows[index];

        public Button EnchantButton => _enchant;

        public string StonesText => _stones.text;

        public string OddsText => _odds.text;

        public bool IsShown => _root.style.display == DisplayStyle.Flex;

        public VisualElement Build()
        {
            _root = HudWidgets.Box("enchant-panel");
            // Wave 1 QA F2's pattern: the panel takes the window's height left, its list scrolls and the rest never shrinks.
            _root.style.flexGrow = 1;
            _root.style.flexShrink = 1;
            _root.pickingMode = PickingMode.Position;
            _root.style.marginTop = 4;
            // The enchant contract banner (WP-M4-10 part C): the crafter's accepted enchant contracts this station enchants.
            _banner = HudWidgets.Box("enchant-contract");
            _banner.style.flexDirection = FlexDirection.Row;
            _banner.style.alignItems = Align.Center;
            _banner.style.flexShrink = 0;
            _banner.pickingMode = PickingMode.Position;
            HudWidgets.Border(_banner, _theme.Accent);
            _bannerPrevious = BannerButton("enchant_contract_previous", ContractTextIds.FormPrevious, () => _actions.StepEnchantContract(-1));
            _bannerText = Text("", "enchant-contract__text");
            _bannerText.style.flexGrow = 1;
            _bannerText.style.flexShrink = 1;
            _bannerNext = BannerButton("enchant_contract_next", ContractTextIds.FormNext, () => _actions.StepEnchantContract(1));
            _bannerWork = BannerButton("enchant_contract_work", ContractTextIds.ForgeWork, () => _actions.WorkOnEnchantContract());
            _bannerStop = BannerButton("enchant_contract_stop", ContractTextIds.ForgeStop, () => _actions.StopEnchantContract());
            _banner.Add(_bannerPrevious);
            _banner.Add(_bannerText);
            _banner.Add(_bannerNext);
            _banner.Add(_bannerWork);
            _banner.Add(_bannerStop);
            _root.Add(_banner);
            _list = new ScrollView(ScrollViewMode.Vertical) { name = "enchant_list" };
            _list.AddToClassList("enchant-list");
            _list.style.flexGrow = 1;
            _list.style.flexShrink = 1;
            _list.pickingMode = PickingMode.Position;
            _root.Add(_list);
            _empty = Text(_text.Get(EnchantTextIds.Empty), "enchant-empty");
            _empty.style.flexShrink = 0;
            _root.Add(_empty);
            _stones = Text("", "enchant-stones");
            _stones.style.flexShrink = 0;
            _root.Add(_stones);
            _odds = Text("", "enchant-odds");
            _odds.style.flexShrink = 0;
            _root.Add(_odds);
            _enchant = new Button(() => _actions.Enchant()) { focusable = false, name = "enchant_button", text = _text.Get(EnchantTextIds.Button) };
            Style(_enchant);
            _enchant.style.flexShrink = 0;
            _root.Add(_enchant);
            _waiting = Text(_text.Get(EnchantTextIds.Waiting), "enchant-waiting");
            _waiting.style.flexShrink = 0;
            _root.Add(_waiting);
            return _root;
        }

        public void Refresh(bool shown)
        {
            Show(_root, shown);
            if (!shown)
                return;
            RefreshBanner();
            var candidates = Model.Candidates;
            while (_rows.Count < candidates.Count)
            {
                int index = _rows.Count;
                var row = new Button(() => _actions.ChooseEnchant(index)) { focusable = false, name = "enchant_row_" + index };
                Style(row);
                row.style.unityTextAlign = UnityEngine.TextAnchor.MiddleLeft;
                row.style.flexShrink = 0;
                row.style.paddingTop = 4;
                row.style.paddingBottom = 4;
                _rows.Add(row);
                _list.Add(row);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                bool used = i < candidates.Count;
                Show(_rows[i], used);
                if (!used)
                    continue;
                var candidate = candidates[i];
                _rows[i].text = _text.Get(candidate.Maxed ? EnchantTextIds.ItemMaxed : EnchantTextIds.Item,
                    new TextArg("name", _text.Get(candidate.Item.NameId)), new TextArg("level", Number(candidate.Level)));
                HudWidgets.Border(_rows[i], i == Model.Selected ? _theme.Accent : _theme.Border);
            }
            Show(_empty, candidates.Count == 0);
            var chosen = Model.Chosen;
            Show(_stones, chosen != null && !Model.CustomerStones); // a contract's own stones are in its escrow
            Show(_odds, Model.Odds != null);
            Show(_enchant, chosen != null);
            if (chosen is { } item)
                HudWidgets.SetText(_stones, _text.Get(EnchantTextIds.Stones, new TextArg("stone", _text.Get(item.Stone.NameId)),
                    new TextArg("held", Number(Model.StonesHeld)), new TextArg("count", Number(item.StoneCount))));
            if (Model.Odds is { } odds)
                HudWidgets.SetText(_odds, _text.Get(EnchantTextIds.Odds, new TextArg("plus", Number(odds.PlusPercent)),
                    new TextArg("same", Number(odds.SamePercent)), new TextArg("minus", Number(odds.MinusPercent))));
            _enchant.SetEnabled(Model.CanEnchant);
            Show(_waiting, Model.PendingRequest != 0);
        }

        /// <summary>
        /// The banner: while working on a contract, "crafting for contract #N" with Stop; else the offer shown with previous, next
        /// and Work on it; hidden without offers.
        /// </summary>
        private void RefreshBanner()
        {
            var offers = Model.ContractOffers;
            var working = Model.Contract;
            Show(_banner, working != null || offers.Count > 0);
            if (working != null)
            {
                HudWidgets.SetText(_bannerText, _text.Get(ContractTextIds.ForgeWorking, new TextArg("id", Number(working.ContractId)),
                    new TextArg("output", OutputOf(working)), new TextArg("gold", Number(working.Gold))));
                Show(_bannerPrevious, false);
                Show(_bannerNext, false);
                Show(_bannerWork, false);
                Show(_bannerStop, Model.PendingRequest == 0);
                return;
            }
            if (offers.Count == 0)
                return;
            int at = System.Math.Min(Model.ContractOffer, offers.Count - 1);
            var offer = offers[at];
            HudWidgets.SetText(_bannerText, _text.Get(ContractTextIds.ForgeBanner, new TextArg("n", Number(at + 1)),
                new TextArg("total", Number(offers.Count)), new TextArg("output", OutputOf(offer)), new TextArg("gold", Number(offer.Gold))));
            Show(_bannerPrevious, offers.Count > 1);
            Show(_bannerNext, offers.Count > 1);
            Show(_bannerWork, true);
            _bannerWork.SetEnabled(Model.PendingRequest == 0);
            Show(_bannerStop, false);
        }

        /// <summary>"Enchant Forged Sword +2": the contract's item and its level at the proposal.</summary>
        private string OutputOf(Shared.Protocol.ContractEntry entry)
        {
            var slot = entry.Slots.Length > 0 ? entry.Slots[0] : null;
            string name = Model.Rules?.Item(entry.DefNetId) is { } item ? _text.Get(item.NameId) : Number(entry.DefNetId);
            return _text.Get(ContractTextIds.EnchantOutput, new TextArg("item", name), new TextArg("level", Number(slot?.EnchantLevel ?? 0)));
        }

        private Button BannerButton(string name, string textId, System.Action onClick)
        {
            var button = new Button(onClick) { focusable = false, name = name, text = _text.Get(textId) };
            Style(button);
            button.style.flexShrink = 0;
            return button;
        }

        private Label Text(string text, string className)
        {
            var label = HudWidgets.Label(text, className);
            label.style.color = _theme.Text;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        private void Style(Button button)
        {
            button.style.color = _theme.Text;
            button.style.backgroundColor = _theme.Panel;
            HudWidgets.Border(button, _theme.Border);
        }

        private static void Show(VisualElement element, bool visible) =>
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
