#nullable enable
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.Gameplay.Entities.Visuals;
using Game.UI.Config;
using Game.UI.Menus.Inventory;
using Game.UI.Overlays;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Inventory;
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
        private AddressablesTextureSource? _icons;
        private PanelSettings? _panel;
        private string _error = "";

        /// <summary>The view models, for the QA smoke checks; null until the HUD loaded.</summary>
        public HudViewModels? Models { get; private set; }

        public ResourcePanelPresenter? ResourcePanel { get; private set; }

        public ChatPresenter? Chat { get; private set; }

        public NamePlateOverlay? NamePlates { get; private set; }

        public TelegraphOverlay? Telegraphs { get; private set; }

        /// <summary>The inventory window (spec 9.13, WP16) and its state, for the QA smoke checks; null until the HUD loaded.</summary>
        public InventoryWindowPresenter? InventoryWindow { get; private set; }

        public InventoryWindowViewModel? InventoryModel { get; private set; }

        public InventoryWindowBinder? InventoryBinder => _inventoryBinder;

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
            _overlays.Add(Telegraphs);
            _overlays.Add(NamePlates);
            _overlays.Add(new DamageNumberOverlay(config, overlayLayer, models.DamageNumbers));

            var view = new HudView(config);
            root.Add(view.Root);
            ResourcePanel = new ResourcePanelPresenter(config, view, models.Resources);
            Add(ResourcePanel);
            Add(new BossBarPresenter(config, view, models.BossBar));
            Chat = new ChatPresenter(config, view, models.Chat, draft => _binder?.SubmitChat(draft));
            Add(Chat);
            Add(new ToastPresenter(config, view, models.Toast));
            Add(new CastBarPresenter(config, view, models.CastBar));
            Add(new BuffRowPresenter(config, view, models.BuffRow));
            Add(new ParryDodgePresenter(config, view, models.Charges));
            Add(new HotbarPresenter(config, view, models.Hotbar, HudElementIds.HotbarLeft));
            Add(new GaugePresenter(config, view));
            Add(new HotbarPresenter(config, view, models.Hotbar, HudElementIds.HotbarRight));

            // The inventory window (WP16) last, so it draws over the HUD widgets.
            var inventory = InventoryConfig.From(tables);
            InventoryModel = new InventoryWindowViewModel(inventory.BagSlots, inventory.Settings.Columns, inventory.MaxStack);
            _inventoryBinder = new InventoryWindowBinder(inventory, InventoryModel, models.Toast, config.Settings.ToastMs, () => NowMs);
            _icons = new AddressablesTextureSource();
            InventoryWindow = new InventoryWindowPresenter(config, view, inventory, InventoryModel, _inventoryBinder, _icons);
            Add(InventoryWindow);

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
        }

        private void Update()
        {
            if (_binder == null)
                return;
            double now = NowMs;
            _binder.Frame(now);
            _inventoryBinder?.Frame(now);
            if (_binder.Feed != null && InventoryWindow != null)
                _binder.Feed.UiCapturesPointer = InventoryWindow.CapturesPointer; // clicks in the window are not attacks
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
            _binder?.Dispose();
            _inventoryBinder?.Dispose();
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
