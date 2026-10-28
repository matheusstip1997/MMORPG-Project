#nullable enable
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.Gameplay.Entities.Visuals;
using Game.UI.Config;
using Game.UI.Menus.Book;
using Game.UI.Menus.Inventory;
using Game.UI.Menus.Outpost;
using Game.UI.Menus.Skills;
using Game.UI.Menus.Quests;
using Game.UI.Overlays;
using Game.UI.ViewModels.Book;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Inventory;
using Game.UI.ViewModels.Outpost;
using Game.UI.ViewModels.Skills;
using Game.UI.ViewModels.Quests;
using Shared.Data.Tables;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.HUD
{
    /// <summary>
    /// Entry point of the main HUD (spec 9, backlog I) in the world scene: loads the HUD tables, builds the widgets on a UI
    /// Toolkit panel scaled from the reference resolution (fit-height, spec 9.7) with the world overlays under them, and
    /// binds the view models to the world client's <see cref="WorldClientFeed"/>; the inventory window (WP16) sits on top.
    /// Added to the dev world scene by <c>DevSceneBuilder</c>. The HUD only displays what the server and the prediction report (spec 9.1).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class HudRoot : MonoBehaviour
    {
        [Tooltip("HUD structure styles (sizes, layout); colors and font sizes come from HudThemeTokens.")]
        [SerializeField] private StyleSheet? styles;

        [Tooltip("HUD font (spec 9.7: Barlow Semi Condensed).")]
        [SerializeField] private Font? font;

        [Tooltip("Shader of the ground telegraphs (URP Unlit, made transparent at runtime).")]
        [SerializeField] private Shader? telegraphShader;

        [Tooltip("The world client offering the feed (IWorldClientFeedSource); found in the scene when empty.")]
        [SerializeField] private MonoBehaviour? client;

        private readonly List<HudPresenter> _presenters = new List<HudPresenter>();
        private readonly List<WorldOverlay> _overlays = new List<WorldOverlay>();
        private HudConfig? _config;
        private HudFeedBinder? _binder;
        private InventoryWindowBinder? _inventoryBinder;
        private BookWindowBinder? _bookBinder;
        private AddressablesTextureSource? _icons;
        private OutpostBinder? _outpostBinder;
        private SkillTreeBinder? _skillBinder;
        private QuestBinder? _questBinder;
        private PanelSettings? _panel;
        private string _error = "";

        /// <summary>The view models, for the QA smoke checks; null until the HUD loaded.</summary>
        public HudViewModels? Models { get; private set; }

        public ResourcePanelPresenter? ResourcePanel { get; private set; }

        /// <summary>The exp bar and its multiplier note (spec 9.3, WP-M2-3).</summary>
        public ExpBarPresenter? ExpBar { get; private set; }

        public ChatPresenter? Chat { get; private set; }

        public NamePlateOverlay? NamePlates { get; private set; }

        public TelegraphOverlay? Telegraphs { get; private set; }

        /// <summary>The P and D squares (QA checks the parry flash, fix/parry-feel).</summary>
        public ParryDodgePresenter? ParryDodge { get; private set; }

        /// <summary>The parried monster's flash (fix/parry-feel).</summary>
        public HitFlashOverlay? HitFlashes { get; private set; }

        /// <summary>The pickup prompt at a ground drop in range (backlog G), for the QA smoke checks.</summary>
        public PickupPromptOverlay? PickupPrompt { get; private set; }

        /// <summary>The inventory window (spec 9.13, WP16) and its state, for the QA smoke checks; null until the HUD loaded.</summary>
        public InventoryWindowPresenter? InventoryWindow { get; private set; }

        public InventoryWindowViewModel? InventoryModel { get; private set; }

        public InventoryWindowBinder? InventoryBinder => _inventoryBinder;

        /// <summary>The outpost and vendor windows, the NPC prompt and the gold (WP-M2-4), for tests and the QA smoke checks.</summary>
        public OutpostWindowPresenter? OutpostWindow { get; private set; }

        public VendorWindowPresenter? VendorWindow { get; private set; }

        public OutpostWindowViewModel? OutpostModel { get; private set; }

        public VendorWindowViewModel? VendorModel { get; private set; }

        public GoldPresenter? Gold { get; private set; }

        public NpcPromptOverlay? NpcPrompt { get; private set; }

        public OutpostBinder? OutpostBinder => _outpostBinder;

        /// <summary>The skill tree window (WP-M2-5) and its state, for tests and the QA smoke checks; null until the HUD loaded.</summary>
        public SkillTreeWindowPresenter? SkillTreeWindow { get; private set; }

        public SkillTreeWindowViewModel? SkillTreeModel { get; private set; }

        public SkillTreeBinder? SkillTreeBinder => _skillBinder;

        /// <summary>The two hotbar sides (spec 9.3), for the skill window's drops and the QA checks.</summary>
        public HotbarPresenter? HotbarLeft { get; private set; }

        public HotbarPresenter? HotbarRight { get; private set; }
        /// <summary>The Bystial book window (WP-M2-8), for tests and the QA smoke checks.</summary>
        public BookWindowPresenter? BookWindow { get; private set; }

        public BookWindowViewModel? BookModel { get; private set; }

        public BookWindowBinder? BookBinder => _bookBinder;
        /// <summary>The quest board and run builder (WP-M2-6).</summary>
        public QuestBoardPresenter? QuestBoard { get; private set; }

        public QuestBoardViewModel? QuestModel { get; private set; }

        /// <summary>The run tracker (spec 9.3, WP-M2-6).</summary>
        public RunTrackerPresenter? RunTracker { get; private set; }

        public QuestBinder? QuestBinder => _questBinder;

        /// <summary>The buff row, for the QA smoke's `buff row shows` check.</summary>
        public BuffRowPresenter? BuffRow { get; private set; }

        private static double NowMs => Time.realtimeSinceStartupAsDouble * 1000.0;

        private void Awake()
        {
            if (!ClientConfig.TryLoad(out var tables, out var error))
            {
                Fail(error);
                return;
            }
            var config = HudConfig.From(tables);
            _config = config;
            var document = GetComponent<UIDocument>();
            ApplyScaling(document, config.Settings);

            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.AddToClassList("hud-root");
            if (styles != null)
                root.styleSheets.Add(styles);
            if (font != null)
                root.style.unityFontDefinition = FontDefinition.FromFont(font);
            root.style.fontSize = config.Theme.Tokens.FontSizeBody;
            root.style.color = config.Theme.Text;

            var models = new HudViewModels(config.Settings.DamageNumberPool, config.Settings.NamePlatePool,
                config.Chat.HistoryLines, config.Chat.MaxChars);
            Models = models;

            // World overlays first, so the widgets draw over them.
            var overlayLayer = new VisualElement { name = "hud_overlays", pickingMode = PickingMode.Ignore };
            overlayLayer.AddToClassList("hud-layer");
            root.Add(overlayLayer);
            Telegraphs = new TelegraphOverlay(config, overlayLayer, models.Telegraphs, telegraphShader);
            NamePlates = new NamePlateOverlay(config, overlayLayer, models.NamePlates);
            PickupPrompt = new PickupPromptOverlay(config, overlayLayer, models.PickupPrompt);
            _overlays.Add(Telegraphs);
            _overlays.Add(NamePlates);
            _overlays.Add(PickupPrompt);
            _overlays.Add(new DamageNumberOverlay(config, overlayLayer, models.DamageNumbers));
            HitFlashes = new HitFlashOverlay(config, overlayLayer, models.HitFlashes);
            _overlays.Add(HitFlashes);

            var view = new HudView(config);
            root.Add(view.Root);
            ResourcePanel = new ResourcePanelPresenter(config, view, models.Resources, models.Progress);
            Add(ResourcePanel);
            ExpBar = new ExpBarPresenter(config, view, models.Progress);
            Add(ExpBar);
            Add(new BossBarPresenter(config, view, models.BossBar));
            Chat = new ChatPresenter(config, view, models.Chat, draft => _binder?.SubmitChat(draft));
            Add(Chat);
            Add(new ToastPresenter(config, view, models.Toast));
            Add(new CastBarPresenter(config, view, models.CastBar));
            BuffRow = new BuffRowPresenter(config, view, models.BuffRow);
            Add(BuffRow);
            ParryDodge = new ParryDodgePresenter(config, view, models.Charges);
            Add(ParryDodge);
            HotbarLeft = new HotbarPresenter(config, view, models.Hotbar, HudElementIds.HotbarLeft);
            Add(HotbarLeft);
            Add(new GaugePresenter(config, view));
            HotbarRight = new HotbarPresenter(config, view, models.Hotbar, HudElementIds.HotbarRight);
            Add(HotbarRight);

            // The inventory window (WP16) last, so it draws over the HUD widgets.
            var inventory = InventoryConfig.From(tables);
            InventoryModel = new InventoryWindowViewModel(inventory.Containers, inventory.NormalBagCode, inventory.Settings.Columns, inventory);
            _inventoryBinder = new InventoryWindowBinder(inventory, InventoryModel, models.Toast, config.Settings.ToastMs, () => NowMs);
            _icons = new AddressablesTextureSource();
            InventoryWindow = new InventoryWindowPresenter(config, view, inventory, InventoryModel, _inventoryBinder, _icons);
            Add(InventoryWindow);

            // Outposts and vendors (WP-M2-4): the gold under the resource panel, the two windows, the prompt at the NPC in reach.
            var outpost = OutpostConfig.From(tables, inventory);
            var goldModel = new GoldViewModel();
            var promptModel = new NpcPromptViewModel();
            OutpostModel = new OutpostWindowViewModel();
            VendorModel = new VendorWindowViewModel();
            _outpostBinder = new OutpostBinder(outpost, OutpostModel, VendorModel, promptModel, goldModel, models.Toast,
                config.Settings.ToastMs, () => NowMs);
            Gold = new GoldPresenter(config, view, goldModel);
            Add(Gold);
            OutpostWindow = new OutpostWindowPresenter(config, view, outpost, OutpostModel, _outpostBinder);
            Add(OutpostWindow);
            VendorWindow = new VendorWindowPresenter(config, view, VendorModel, _outpostBinder);
            Add(VendorWindow);
            NpcPrompt = new NpcPromptOverlay(config, overlayLayer, promptModel);
            _overlays.Add(NpcPrompt);

            // The skill tree window (WP-M2-5): learned skills dragged from it land on a hotbar slot.
            var skills = SkillTreeConfig.From(tables);
            SkillTreeModel = new SkillTreeWindowViewModel(skills.Rules);
            _skillBinder = new SkillTreeBinder(skills, config.Text, SkillTreeModel, models.Toast, config.Settings.ToastMs, () => NowMs, HotbarSlotAt);
            SkillTreeWindow = new SkillTreeWindowPresenter(config, view, SkillTreeModel, skills.Rules, inventory.Settings.DragThresholdPx, _skillBinder);
            Add(SkillTreeWindow);
            // The Bystial book (WP-M2-8, spec 14.2): its window over the HUD widgets, opened by its key.
            var book = BookConfig.From(tables);
            BookModel = new BookWindowViewModel();
            _bookBinder = new BookWindowBinder(book, config.Text, BookModel, models.Toast, config.Settings.ToastMs, () => NowMs);
            BookWindow = new BookWindowPresenter(config, view, book, BookModel, _bookBinder);
            Add(BookWindow);
            // Outpost quests and runs (WP-M2-6): the board in the sale window's place, the run tracker on the right.
            var quests = QuestConfig.From(tables, config, inventory);
            QuestModel = new QuestBoardViewModel(quests.MaxStepsPerRun);
            var trackerModel = new RunTrackerViewModel();
            _questBinder = new QuestBinder(QuestModel, trackerModel, models.Toast, config.Settings.ToastMs, () => NowMs);
            QuestBoard = new QuestBoardPresenter(config, view, quests, QuestModel, _questBinder);
            Add(QuestBoard);
            RunTracker = new RunTrackerPresenter(config, view, quests, trackerModel);
            Add(RunTracker);
            var questModel = QuestModel;
            _outpostBinder.OtherWindowOpen = npc => questModel.Open && questModel.NpcEntityId == npc;

            _binder = new HudFeedBinder(config, models, () => NowMs);
        }

        private void Start()
        {
            if (_binder == null)
                return;
            var source = client as IWorldClientFeedSource ?? FindSource();
            if (source?.Feed == null)
            {
                Fail("No world client feed in the scene: the HUD shows nothing (rebuild the dev scene).");
                return;
            }
            _binder.Bind(source.Feed);
            _inventoryBinder?.Bind(source.Feed);
            _outpostBinder?.Bind(source.Feed);
            _skillBinder?.Bind(source.Feed);
            _bookBinder?.Bind(source.Feed);
            _questBinder?.Bind(source.Feed);
            source.Feed.ConfigReloaded += OnConfigReloaded;
        }

        /// <summary>Dev hot reload (spec 11.7): telegraphs, names and buff icons follow the reloaded tables.</summary>
        private void OnConfigReloaded()
        {
            var feed = _binder?.Feed;
            if (feed == null || _config == null)
                return;
            _config.RetuneCombat(feed.Config, feed.Catalog);
            Telegraphs?.ClearMeshes();
        }

        private void Update()
        {
            if (_binder == null)
                return;
            double now = NowMs;
            _binder.Frame(now);
            _inventoryBinder?.Frame(now);
            _outpostBinder?.Frame(now);
            _skillBinder?.Frame(now);
            _bookBinder?.Frame(now);
            _questBinder?.Frame(now);
            if (_binder.Feed != null && InventoryWindow != null)
                _binder.Feed.UiCapturesPointer = InventoryWindow.CapturesPointer // clicks in a window are not attacks
                    || (OutpostWindow?.CapturesPointer ?? false) || (VendorWindow?.CapturesPointer ?? false)
                    || (SkillTreeWindow?.CapturesPointer ?? false) || (BookWindow?.CapturesPointer ?? false)
                    || (QuestBoard?.CapturesPointer ?? false);
            foreach (var presenter in _presenters)
                presenter.Animate(now);
        }

        /// <summary>After every Update: the entity views are placed for this frame, so the overlays follow them.</summary>
        private void LateUpdate()
        {
            var feed = _binder?.Feed;
            if (feed == null)
                return;
            double now = NowMs;
            foreach (var overlay in _overlays)
                overlay.Place(feed, now);
        }

        private void OnDestroy()
        {
            if (_binder?.Feed != null)
                _binder.Feed.ConfigReloaded -= OnConfigReloaded;
            _binder?.Dispose();
            _inventoryBinder?.Dispose();
            _outpostBinder?.Dispose();
            _skillBinder?.Dispose();
            _bookBinder?.Dispose();
            _questBinder?.Dispose();
            _icons?.Dispose();
            foreach (var presenter in _presenters)
                presenter.Dispose();
            _presenters.Clear();
            foreach (var overlay in _overlays)
                overlay.Dispose();
            _overlays.Clear();
            if (_panel != null)
                Destroy(_panel);
        }

        private void OnGUI()
        {
            // Only for a broken setup, before any HUD exists; the text is a developer message.
            if (_error.Length > 0)
                GUI.Label(new Rect(10, 40, Screen.width - 20, 40), _error);
        }

        /// <summary>The hotbar slot under a panel position (either side), -1 for none: where the skill window drops a skill.</summary>
        private int HotbarSlotAt(Vector2 panelPosition)
        {
            int slot = HotbarLeft?.SlotAt(panelPosition) ?? -1;
            return slot > 0 ? slot : HotbarRight?.SlotAt(panelPosition) ?? -1;
        }

        private void Add(HudPresenter presenter)
        {
            presenter.Bind();
            _presenters.Add(presenter);
        }

        private static IWorldClientFeedSource? FindSource()
        {
            foreach (var behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (behaviour is IWorldClientFeedSource source)
                    return source;
            }
            return null;
        }

        /// <summary>Spec 9.7: reference resolution from config, scaled with the screen height; the asset is copied for play mode.</summary>
        private void ApplyScaling(UIDocument document, HudSettings settings)
        {
            if (document.panelSettings == null)
            {
                Fail("The HUD UIDocument has no PanelSettings; rebuild the dev scene (MMORPG / Dev / Rebuild Prediction Dev Scene).");
                return;
            }
            _panel = Instantiate(document.panelSettings);
            _panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            _panel.referenceResolution = new Vector2Int(settings.ReferenceWidth, settings.ReferenceHeight);
            _panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            _panel.match = settings.Scaling == UiScaleMode.FitHeight ? 1f : 0f;
            document.panelSettings = _panel;
        }

        private void Fail(string message)
        {
            _error = message;
            Debug.LogError($"[HUD] {message}");
        }
    }
}
