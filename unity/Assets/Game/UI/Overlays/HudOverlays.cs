#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using Shared.Protocol;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Overlays
{
    /// <summary>
    /// World-anchored HUD overlays (spec 9.3 world overlays, 9.9 <c>Overlays/</c>): placed once per frame, after the entity
    /// views moved, by projecting a world point onto the HUD panel. Only active items are placed; elements are pooled.
    /// </summary>
    public abstract class WorldOverlay : IDisposable
    {
        private static readonly List<Renderer> RendererScratch = new List<Renderer>();

        protected WorldOverlay(HudConfig config, VisualElement layer)
        {
            Config = config;
            Layer = layer;
        }

        protected HudConfig Config { get; }

        protected VisualElement Layer { get; }

        public abstract void Place(WorldClientFeed feed, double nowMs);

        public virtual void Dispose()
        {
        }

        /// <summary>
        /// Where <paramref name="world"/> is on the HUD panel (reference pixels from the top left). Goes through viewport
        /// coordinates and the full-screen overlay layer's own size, so it holds whatever pixel size the camera and the panel
        /// render at (an off-screen capture included). False behind the camera or before the layer has a size.
        /// </summary>
        protected bool TryProject(Camera camera, Vector3 world, out Vector2 panelPosition)
        {
            panelPosition = default;
            if (camera == null)
                return false;
            var size = Layer.layout.size;
            if (float.IsNaN(size.x) || size.x <= 0f || size.y <= 0f)
                return false;
            var viewport = camera.WorldToViewportPoint(world);
            if (viewport.z <= 0f)
                return false;
            panelPosition = new Vector2(viewport.x * size.x, (1f - viewport.y) * size.y);
            return true;
        }

        /// <summary>
        /// Height of a view's top above its feet, from its renderers' bounds (the visual is loaded asynchronously and swapped
        /// in, so callers measure again now and then); <paramref name="fallbackM"/> while it has none.
        /// </summary>
        protected static float MeasureTopM(Transform anchor, float fallbackM)
        {
            anchor.GetComponentsInChildren(false, RendererScratch);
            float top = float.MinValue;
            foreach (var renderer in RendererScratch)
            {
                if (renderer is ParticleSystemRenderer)
                    continue;
                top = Mathf.Max(top, renderer.bounds.max.y);
            }
            RendererScratch.Clear();
            return top == float.MinValue ? fallbackM : top - anchor.position.y;
        }

        protected static void Show(VisualElement element, bool visible) =>
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        protected static void Move(VisualElement element, Vector2 position)
        {
            element.style.left = position.x;
            element.style.top = position.y;
        }
    }

    /// <summary>
    /// Spec 9.3 <c>nameplate</c> above each remote character (name from <c>DisplayIdentity</c> for players, the
    /// <c>UiStrings</c> monster name for monsters; red-name and armed icons from flags; guild tag TODO(M2)) and
    /// <c>monster_hp_bar</c> below each normal monster while it is damaged or locked. Bosses show their name only. The
    /// target lock (R-2.2-06) puts a marker above the locked target's name.
    /// </summary>
    public sealed class NamePlateOverlay : WorldOverlay
    {
        private const double MeasureEveryMs = 1000.0;

        private readonly NamePlateSet _model;
        private readonly Plate[] _plates;

        public NamePlateOverlay(HudConfig config, VisualElement layer, NamePlateSet model) : base(config, layer)
        {
            _model = model;
            _plates = new Plate[model.Capacity];
            var theme = config.Theme;
            for (int i = 0; i < _plates.Length; i++)
            {
                var plate = new Plate { Root = HudWidgets.Box("hud-plate") };
                plate.Name = HudWidgets.Label("", "hud-plate__name");
                plate.Name.style.color = theme.Text;
                plate.RedName = HudWidgets.Label(config.Text.Get(HudTextIds.PlateRedName), "hud-plate__flag");
                plate.RedName.style.color = theme.Hp;
                plate.Armed = HudWidgets.Label(config.Text.Get(HudTextIds.FlagArmed), "hud-plate__flag");
                plate.Armed.style.color = theme.Accent;
                var line = HudWidgets.Box("hud-plate__line");
                line.Add(plate.RedName);
                line.Add(plate.Name);
                line.Add(plate.Armed);
                plate.Lock = new LockMarker(theme.Accent) { tooltip = config.Text.Get(HudTextIds.PlateLocked) };
                Show(plate.Lock, false);
                plate.Root.Add(plate.Lock);
                plate.Root.Add(line);
                plate.HpBar = HudWidgets.Bar("hud-plate__hp", theme.Hp, out plate.HpFill);
                plate.HpBar.style.backgroundColor = theme.Panel;
                Show(plate.Root, false);
                Show(plate.HpBar, false);
                layer.Add(plate.Root);
                layer.Add(plate.HpBar);
                _plates[i] = plate;
            }
            model.PlateChanged += Refresh;
        }

        private sealed class Plate
        {
            public VisualElement Root = null!;
            public Label Name = null!;
            public Label RedName = null!;
            public Label Armed = null!;
            public VisualElement Lock = null!;
            public VisualElement HpBar = null!;
            public VisualElement HpFill = null!;
            public uint EntityId;
            public float TopM = -1f;
            public double NextMeasureMs;
        }

        public override void Dispose() => _model.PlateChanged -= Refresh;

        private void Refresh(int index)
        {
            ref readonly var data = ref _model[index];
            var plate = _plates[index];
            if (!data.Active)
            {
                Show(plate.Root, false);
                Show(plate.HpBar, false);
                plate.EntityId = 0;
                return;
            }
            if (plate.EntityId != data.EntityId)
            {
                plate.EntityId = data.EntityId;
                plate.TopM = -1f;
                plate.NextMeasureMs = 0;
            }
            HudWidgets.SetText(plate.Name, data.Name);
            Show(plate.RedName, (data.Flags & EntityFlags.RedName) != 0);
            Show(plate.Armed, data.Kind == PlateKind.Player && (data.Flags & EntityFlags.Armed) != 0);
            Show(plate.Lock, data.Locked);
            HudWidgets.SetFill(plate.HpFill, data.HpBucket / (float)Snapshot.TopHpBucket);
        }

        public override void Place(WorldClientFeed feed, double nowMs)
        {
            var views = feed.Views;
            var camera = feed.Camera;
            for (int i = 0; i < _plates.Length; i++)
            {
                ref readonly var data = ref _model[i];
                var plate = _plates[i];
                if (!data.Active || views == null || camera == null || !views.TryGetAnchor(data.EntityId, out var anchor))
                {
                    Show(plate.Root, false);
                    Show(plate.HpBar, false);
                    continue;
                }
                if (nowMs >= plate.NextMeasureMs)
                {
                    plate.TopM = MeasureTopM(anchor, Config.Settings.NamePlateHeightCm / 100f);
                    plate.NextMeasureMs = nowMs + MeasureEveryMs;
                }
                var feet = anchor.position;
                bool named = data.Name.Length > 0;
                if (named && TryProject(camera, feet + Vector3.up * plate.TopM, out var head))
                {
                    Move(plate.Root, head);
                    Show(plate.Root, true);
                }
                else
                {
                    Show(plate.Root, false);
                }
                if (data.ShowsHpBar && TryProject(camera, feet + Vector3.up * (Config.Settings.MonsterHpBarHeightCm / 100f), out var below))
                {
                    Move(plate.HpBar, below);
                    Show(plate.HpBar, true);
                }
                else
                {
                    Show(plate.HpBar, false);
                }
            }
        }

        /// <summary>Visible plates whose name is <paramref name="name"/> (QA checks).</summary>
        public int VisibleWithName(string name)
        {
            int count = 0;
            foreach (var plate in _plates)
            {
                if (plate.Root.style.display == DisplayStyle.Flex && plate.Name.text == name)
                    count++;
            }
            return count;
        }

        /// <summary>Monster HP bars shown now (QA checks).</summary>
        public int VisibleHpBars()
        {
            int count = 0;
            foreach (var plate in _plates)
            {
                if (plate.HpBar.style.display == DisplayStyle.Flex)
                    count++;
            }
            return count;
        }

        /// <summary>The entity whose shown plate carries the lock marker, 0 when none (QA checks).</summary>
        public uint VisibleLockMarkerEntity()
        {
            foreach (var plate in _plates)
            {
                if (plate.Root.style.display == DisplayStyle.Flex && plate.Lock.style.display == DisplayStyle.Flex)
                    return plate.EntityId;
            }
            return 0;
        }

        /// <summary>
        /// The target lock marker (R-2.2-06) above the locked target's name: a chevron pointing down at it, in the accent
        /// color with a dark outline. Its shape differs from every telegraph icon (spec 9.8: triangle, diamond, circle,
        /// square), which sit at the same height above a monster.
        /// </summary>
        private sealed class LockMarker : VisualElement
        {
            private readonly Color _fill;

            public LockMarker(Color fill)
            {
                fill.a = 1f;
                _fill = fill;
                pickingMode = PickingMode.Ignore;
                AddToClassList("hud-plate__lock");
                generateVisualContent += Draw;
            }

            private void Draw(MeshGenerationContext context)
            {
                var rect = contentRect;
                float w = rect.width, h = rect.height;
                if (w <= 0f || h <= 0f)
                    return;
                var paint = context.painter2D;
                paint.lineJoin = LineJoin.Miter;
                paint.lineCap = LineCap.Butt;
                for (int pass = 0; pass < 2; pass++)
                {
                    paint.strokeColor = pass == 0 ? Color.black : _fill;
                    paint.lineWidth = pass == 0 ? h * 0.5f : h * 0.3f;
                    paint.BeginPath();
                    paint.MoveTo(new Vector2(w * 0.15f, h * 0.2f));
                    paint.LineTo(new Vector2(w * 0.5f, h * 0.75f));
                    paint.LineTo(new Vector2(w * 0.85f, h * 0.2f));
                    paint.Stroke();
                }
            }
        }
    }

    /// <summary>
    /// Spec 9.3 <c>damage_numbers</c>: floating numbers over the target, rising and fading over their lifetime; crits
    /// bigger in the crit color, heals in the heal color, dodged and parried as words. Pooled with the view model.
    /// </summary>
    public sealed class DamageNumberOverlay : WorldOverlay
    {
        private readonly DamageNumberPool _model;
        private readonly Label[] _labels;

        public DamageNumberOverlay(HudConfig config, VisualElement layer, DamageNumberPool model) : base(config, layer)
        {
            _model = model;
            _labels = new Label[model.Capacity];
            for (int i = 0; i < _labels.Length; i++)
            {
                _labels[i] = HudWidgets.Label("", "hud-damage");
                Show(_labels[i], false);
                layer.Add(_labels[i]);
            }
            model.Spawned += OnSpawned;
            model.Expired += OnExpired;
        }

        public override void Dispose()
        {
            _model.Spawned -= OnSpawned;
            _model.Expired -= OnExpired;
        }

        private void OnSpawned(int index)
        {
            ref readonly var number = ref _model[index];
            var label = _labels[index];
            var theme = Config.Theme;
            label.text = number.Text;
            label.style.color = number.Style switch
            {
                DamageNumberStyle.Crit => theme.CritText,
                DamageNumberStyle.Heal => theme.HealText,
                _ => theme.DamageText,
            };
            label.style.fontSize = number.Style == DamageNumberStyle.Crit ? theme.Tokens.FontSizeCrit : theme.Tokens.FontSizeDamage;
            label.EnableInClassList("hud-damage--crit", number.Style == DamageNumberStyle.Crit);
            label.style.opacity = 1f;
        }

        private void OnExpired(int index) => Show(_labels[index], false);

        public override void Place(WorldClientFeed feed, double nowMs)
        {
            var views = feed.Views;
            var camera = feed.Camera;
            var settings = Config.Settings;
            uint own = feed.Registry.OwnEntityId ?? 0;
            for (int i = 0; i < _labels.Length; i++)
            {
                ref readonly var number = ref _model[i];
                if (!number.Active)
                    continue;
                Transform? anchor = null;
                if (views != null)
                {
                    if (number.TargetId == own)
                        anchor = views.LocalView;
                    else if (views.TryGetAnchor(number.TargetId, out var remote))
                        anchor = remote;
                }
                float age = (float)Math.Min(1.0, (nowMs - number.StartMs) / settings.DamageNumberLifetimeMs);
                var at = anchor != null
                    ? anchor.position + Vector3.up * ((settings.DamageNumberHeightCm + settings.DamageNumberRiseCm * age) / 100f)
                    : Vector3.zero;
                if (anchor == null || camera == null || !TryProject(camera, at, out var position))
                {
                    Show(_labels[i], false);
                    continue;
                }
                Move(_labels[i], position);
                _labels[i].style.opacity = 1f - age * age;
                Show(_labels[i], true);
            }
        }
    }

    /// <summary>
    /// Spec 9.3 <c>telegraph</c>: while a monster performs an attack, its ground shapes in the parry color (a neutral color
    /// when uncolored) and, for a colored attack, an icon above the monster whose shape names the color (spec 9.8: red
    /// triangle, yellow diamond, blue circle, green square). The areas are world meshes under the characters; the icons are
    /// on the HUD panel.
    /// </summary>
    public sealed class TelegraphOverlay : WorldOverlay
    {
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        private readonly TelegraphSet _model;
        private readonly Material? _material;
        private readonly Dictionary<int, Mesh> _meshes = new Dictionary<int, Mesh>();
        private readonly List<Area> _areas = new List<Area>();
        private readonly List<ColorIcon> _icons = new List<ColorIcon>();
        private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
        private GameObject? _root;

        public TelegraphOverlay(HudConfig config, VisualElement layer, TelegraphSet model, Shader? shader) : base(config, layer)
        {
            _model = model;
            if (shader != null)
                _material = CreateMaterial(shader);
            else
                Debug.LogWarning("[HUD] No telegraph shader: ground telegraphs are not drawn (rebuild the dev scene).");
        }

        private sealed class Area
        {
            public GameObject Object = null!;
            public MeshFilter Filter = null!;
            public MeshRenderer Renderer = null!;
        }

        /// <summary>Areas drawn this frame (QA checks).</summary>
        public int VisibleAreas { get; private set; }

        public override void Place(WorldClientFeed feed, double nowMs)
        {
            int areas = 0;
            int icons = 0;
            var views = feed.Views;
            var camera = feed.Camera;
            if (feed.HasRenderTick && views != null)
            {
                double renderTick = feed.RenderTick;
                for (int i = 0; i < _model.Count; i++)
                {
                    var telegraph = _model[i];
                    if (!TelegraphSet.Shows(telegraph, renderTick) || !views.TryGetAnchor(telegraph.EntityId, out var anchor))
                        continue;
                    if (_material != null)
                        DrawArea(areas++, telegraph, anchor);
                    if (telegraph.Color != ParryColor.None && camera != null
                        && TryProject(camera, anchor.position + Vector3.up * (MeasureTopM(anchor, 2f) + Config.Settings.TelegraphIconHeightCm / 100f), out var at))
                        DrawIcon(icons++, telegraph.Color, at);
                }
            }
            for (int i = areas; i < _areas.Count; i++)
                _areas[i].Object.SetActive(false);
            for (int i = icons; i < _icons.Count; i++)
                Show(_icons[i], false);
            VisibleAreas = areas;
        }

        private void DrawArea(int index, in Telegraph telegraph, Transform anchor)
        {
            if (index == _areas.Count)
                _areas.Add(NewArea());
            var area = _areas[index];
            if (!_meshes.TryGetValue(telegraph.ActionNetId, out var mesh))
            {
                mesh = TelegraphMeshes.Build(Config.Shapes(telegraph.ActionNetId));
                _meshes[telegraph.ActionNetId] = mesh;
            }
            area.Filter.sharedMesh = mesh;
            area.Object.transform.SetPositionAndRotation(
                anchor.position + Vector3.up * (Config.Settings.TelegraphGroundOffsetCm / 100f), anchor.rotation);
            _block.SetColor(BaseColor, Config.Theme.Telegraph(telegraph.Color));
            area.Renderer.SetPropertyBlock(_block);
            area.Object.SetActive(true);
        }

        private void DrawIcon(int index, ParryColor color, Vector2 at)
        {
            if (index == _icons.Count)
            {
                var icon = new ColorIcon();
                Layer.Add(icon);
                _icons.Add(icon);
            }
            var element = _icons[index];
            element.Set(color, Config.Theme.Telegraph(color));
            Move(element, at);
            Show(element, true);
        }

        private Area NewArea()
        {
            _root ??= new GameObject("HUD Telegraphs");
            var go = new GameObject("Telegraph");
            go.transform.SetParent(_root.transform, false);
            var area = new Area { Object = go, Filter = go.AddComponent<MeshFilter>(), Renderer = go.AddComponent<MeshRenderer>() };
            area.Renderer.sharedMaterial = _material;
            area.Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            area.Renderer.receiveShadows = false;
            return area;
        }

        /// <summary>Dev hot reload (spec 11.7): the shapes changed, so every telegraph mesh is built again when next drawn.</summary>
        public void ClearMeshes()
        {
            foreach (var mesh in _meshes.Values)
                UnityEngine.Object.Destroy(mesh);
            _meshes.Clear();
        }

        public override void Dispose()
        {
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            ClearMeshes();
            if (_material != null)
                UnityEngine.Object.Destroy(_material);
        }

        /// <summary>URP Unlit, transparent, both sides, no depth write: a tinted decal on the ground under the characters.</summary>
        private static Material CreateMaterial(Shader shader)
        {
            var material = new Material(shader) { name = "HUD Telegraph" };
            material.SetFloat("_Surface", 1f); // transparent
            material.SetFloat("_Blend", 0f);   // alpha
            material.SetFloat("_Cull", 0f);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            return material;
        }

        /// <summary>The icon that pairs a telegraph color with a shape (spec 9.8), drawn with vector paths.</summary>
        private sealed class ColorIcon : VisualElement
        {
            private ParryColor _color = ParryColor.None;
            private Color _fill;

            public ColorIcon()
            {
                pickingMode = PickingMode.Ignore;
                AddToClassList("hud-telegraph-icon");
                generateVisualContent += Draw;
            }

            public void Set(ParryColor color, Color fill)
            {
                fill.a = 1f;
                if (color == _color && fill == _fill)
                    return;
                _color = color;
                _fill = fill;
                MarkDirtyRepaint();
            }

            private void Draw(MeshGenerationContext context)
            {
                var rect = contentRect;
                float w = rect.width, h = rect.height;
                if (w <= 0f || h <= 0f)
                    return;
                var paint = context.painter2D;
                paint.fillColor = _fill;
                paint.strokeColor = Color.black;
                paint.lineWidth = 1.5f;
                paint.BeginPath();
                switch (_color)
                {
                    case ParryColor.Red: // triangle: cannot be parried
                        paint.MoveTo(new Vector2(w * 0.5f, 0f));
                        paint.LineTo(new Vector2(w, h));
                        paint.LineTo(new Vector2(0f, h));
                        break;
                    case ParryColor.Yellow: // diamond
                        paint.MoveTo(new Vector2(w * 0.5f, 0f));
                        paint.LineTo(new Vector2(w, h * 0.5f));
                        paint.LineTo(new Vector2(w * 0.5f, h));
                        paint.LineTo(new Vector2(0f, h * 0.5f));
                        break;
                    case ParryColor.Blue: // circle
                        paint.Arc(new Vector2(w * 0.5f, h * 0.5f), Mathf.Min(w, h) * 0.5f, 0f, 360f);
                        break;
                    default: // green: square
                        paint.MoveTo(new Vector2(w * 0.1f, h * 0.1f));
                        paint.LineTo(new Vector2(w * 0.9f, h * 0.1f));
                        paint.LineTo(new Vector2(w * 0.9f, h * 0.9f));
                        paint.LineTo(new Vector2(w * 0.1f, h * 0.9f));
                        break;
                }
                paint.ClosePath();
                paint.Fill();
                paint.Stroke();
            }
        }
    }
}
