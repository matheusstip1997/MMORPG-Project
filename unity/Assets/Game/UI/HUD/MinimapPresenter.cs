#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.Gameplay.Entities.Visuals;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.HUD
{
    /// <summary>
    /// Spec 9.3 <c>minimap</c> (WP-M2-10b): the exported grid drawn as the map (region tint per ring), north-up and centered
    /// on the player at <see cref="MinimapSettings.CmPerPx"/>, the player arrow turned by its facing, and the
    /// <see cref="MinimapMarkerDef"/> markers (NPCs, passes and the gate, the run's camp hints, the death spot, party members)
    /// clipped to the frame. Reads the feed every frame through <see cref="MinimapModel.Update"/>; rebuilds its marker
    /// elements only when the marker list changes. Allocates nothing per frame.
    /// </summary>
    public sealed class MinimapPresenter : HudPresenter
    {
        private readonly MinimapModel _model;
        private readonly Func<WorldClientFeed?> _feed;
        private readonly ITextureSource? _icons;
        private readonly List<MinimapMarkerElement> _markers = new List<MinimapMarkerElement>();
        private VisualElement _box = null!;
        private VisualElement _viewport = null!;
        private Image _map = null!;
        private VisualElement _layer = null!;
        private MinimapMarkerElement _arrow = null!;
        private Texture2D? _texture;
        private int _shownRevision = -1;
        private int _used;

        public MinimapPresenter(HudConfig config, HudView view, MinimapModel model, Func<WorldClientFeed?> feed, ITextureSource? icons)
            : base(config, view, model)
        {
            _model = model;
            _feed = feed;
            _icons = icons;
        }

        public MinimapModel Model => _model;

        /// <summary>The clipping frame: marker and arrow positions are relative to its top-left corner (tests and QA checks).</summary>
        public VisualElement Frame => _viewport;

        public Image Map => _map;

        public MinimapMarkerElement Arrow => _arrow;

        public bool IsShown => _box.style.display != DisplayStyle.None;

        /// <summary>The marker elements in use, in <see cref="MinimapModel.Points"/> order.</summary>
        public IEnumerable<MinimapMarkerElement> Markers
        {
            get
            {
                for (int i = 0; i < _used; i++)
                    yield return _markers[i];
            }
        }

        protected override void Build()
        {
            _box = View.Place(HudElementIds.Minimap, HudWidgets.Panel("hud-minimap", Theme));
            _viewport = HudWidgets.Box("hud-minimap__frame");
            _box.Add(_viewport);
            _map = new Image { pickingMode = PickingMode.Ignore, scaleMode = ScaleMode.StretchToFill };
            _map.AddToClassList("hud-minimap__map");
            _viewport.Add(_map);
            _layer = HudWidgets.Box("hud-minimap__markers");
            _viewport.Add(_layer);
            _arrow = new MinimapMarkerElement();
            _viewport.Add(_arrow);
            var own = _model.Config.First(MinimapSource.OwnEntity);
            if (own != null)
                _arrow.Set(own, null);
            _viewport.RegisterCallback<DetachFromPanelEvent>(_ => DestroyTexture());
            if (_icons != null)
                _icons.Finished += _ => _shownRevision = -1;
        }

        protected override void Refresh()
        {
            // The marker list changed: Animate rebuilds the elements on its next frame (it knows the frame's size).
        }

        public override void Animate(double nowMs)
        {
            var feed = _feed();
            if (feed != null)
                _model.Update(feed, nowMs);
            Draw();
        }

        /// <summary>Places the map, the markers and the arrow for the model's current state (also called by tests).</summary>
        public void Draw()
        {
            var texture = _model.Texture;
            float width = _viewport.layout.width, height = _viewport.layout.height;
            bool ready = texture != null && _model.HasPlayer && width > 0f && height > 0f;
            _map.style.display = ready ? DisplayStyle.Flex : DisplayStyle.None;
            _layer.style.display = ready ? DisplayStyle.Flex : DisplayStyle.None;
            _arrow.style.display = ready && _arrow.Style != null && _model.IsShown(_arrow.Style) ? DisplayStyle.Flex : DisplayStyle.None;
            if (!ready)
                return;

            if (_texture == null)
                _texture = Upload(texture!);
            float scale = _model.Config.Settings.CmPerPx;
            float mapWidth = texture!.Width * texture.TexelCm / scale, mapHeight = texture.Height * texture.TexelCm / scale;
            var southWest = _model.ToFrame(texture.OriginXCm, texture.OriginYCm, width, height);
            Place(_map, southWest.x, southWest.y - mapHeight, mapWidth, mapHeight);

            if (_shownRevision != _model.Revision)
                RebuildMarkers();
            for (int i = 0; i < _used; i++)
            {
                var marker = _markers[i];
                var point = _model.Points[i];
                var at = _model.ToFrame(point.XCm, point.YCm, width, height);
                float half = point.Style.SizePx * 0.5f;
                bool inside = at.x + half > 0f && at.x - half < width && at.y + half > 0f && at.y - half < height;
                marker.style.display = inside ? DisplayStyle.Flex : DisplayStyle.None;
                if (inside)
                    Place(marker, at.x - half, at.y - half, point.Style.SizePx, point.Style.SizePx);
            }

            if (_arrow.Style != null)
            {
                float size = _arrow.Style.SizePx;
                Place(_arrow, width * 0.5f - size * 0.5f, height * 0.5f - size * 0.5f, size, size);
                _arrow.style.rotate = new Rotate(new Angle(_model.ArrowDegrees, AngleUnit.Degree));
            }
        }

        private void RebuildMarkers()
        {
            _shownRevision = _model.Revision;
            var points = _model.Points;
            while (_markers.Count < points.Count)
            {
                var element = new MinimapMarkerElement();
                _markers.Add(element);
                _layer.Add(element);
            }
            for (int i = 0; i < _markers.Count; i++)
            {
                if (i < points.Count)
                    _markers[i].Set(points[i].Style, IconTexture(points[i].Style));
                else
                    _markers[i].style.display = DisplayStyle.None;
            }
            _used = points.Count;
            if (_arrow.Style != null)
                _arrow.Set(_arrow.Style, IconTexture(_arrow.Style));
        }

        private Texture2D? IconTexture(MinimapMarkerStyle style) =>
            style.IconAddress.Length > 0 && _icons != null && _icons.Get(style.IconAddress, out var texture) == PrefabState.Ready
                ? texture
                : null;

        private Texture2D Upload(MinimapTexture source)
        {
            var texture = new Texture2D(source.Width, source.Height, TextureFormat.RGBA32, false)
            {
                name = "Minimap",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            texture.SetPixels32(source.Pixels);
            texture.Apply(false, true);
            _map.image = texture;
            return texture;
        }

        private void DestroyTexture()
        {
            if (_texture == null)
                return;
            _map.image = null;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(_texture);
            else
                UnityEngine.Object.DestroyImmediate(_texture);
            _texture = null;
        }

        private static void Place(VisualElement element, float left, float top, float width, float height)
        {
            element.style.left = left;
            element.style.top = top;
            element.style.width = width;
            element.style.height = height;
        }
    }

    /// <summary>
    /// One minimap marker: the <see cref="MinimapIconRegistry"/> texture when one is loaded, otherwise its placeholder shape
    /// in the marker's color, drawn with vector paths and a dark outline so it reads on any ground. The arrow points north at
    /// rotation 0.
    /// </summary>
    public sealed class MinimapMarkerElement : VisualElement
    {
        public MinimapMarkerElement()
        {
            pickingMode = PickingMode.Ignore;
            AddToClassList("hud-minimap__marker");
            generateVisualContent += Draw;
        }

        public MinimapMarkerStyle? Style { get; private set; }

        public Texture2D? Texture { get; private set; }

        public void Set(MinimapMarkerStyle style, Texture2D? texture)
        {
            if (ReferenceEquals(style, Style) && texture == Texture)
                return;
            Style = style;
            Texture = texture;
            this.style.backgroundImage = texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);
            MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (Style == null || Texture != null || rect.width <= 0f || rect.height <= 0f)
                return;
            var paint = context.painter2D;
            float w = rect.width, h = rect.height;
            float r = Mathf.Min(w, h) * 0.42f;
            var c = new Vector2(w * 0.5f, h * 0.5f);
            paint.fillColor = Style.Color;
            paint.strokeColor = new Color(0f, 0f, 0f, 0.8f);
            paint.lineWidth = Mathf.Max(1f, r * 0.18f);
            paint.BeginPath();
            switch (Style.Shape)
            {
                case MinimapShape.Arrow:
                    paint.MoveTo(c + new Vector2(0f, -r));
                    paint.LineTo(c + new Vector2(r * 0.75f, r));
                    paint.LineTo(c + new Vector2(0f, r * 0.45f));
                    paint.LineTo(c + new Vector2(-r * 0.75f, r));
                    break;
                case MinimapShape.Square:
                    Polygon(paint, c, r * 1.1f, 4, 45f);
                    break;
                case MinimapShape.Diamond:
                    Polygon(paint, c, r * 1.1f, 4, 0f);
                    break;
                case MinimapShape.Triangle:
                    Polygon(paint, c + new Vector2(0f, r * 0.15f), r * 1.1f, 3, 0f);
                    break;
                case MinimapShape.Ring:
                    // Unfilled: a thick colored circle over a thin dark one.
                    paint.Arc(c, r, 0f, 360f);
                    paint.ClosePath();
                    paint.Stroke();
                    paint.strokeColor = Style.Color;
                    paint.lineWidth = Mathf.Max(1.5f, r * 0.2f);
                    paint.BeginPath();
                    paint.Arc(c, r, 0f, 360f);
                    paint.ClosePath();
                    paint.Stroke();
                    return;
                case MinimapShape.Bag:
                    // A sack: a round body and a narrow tied neck.
                    paint.Arc(c + new Vector2(0f, r * 0.25f), r * 0.75f, 0f, 360f);
                    paint.ClosePath();
                    paint.Fill();
                    paint.Stroke();
                    paint.BeginPath();
                    paint.MoveTo(c + new Vector2(-r * 0.35f, -r));
                    paint.LineTo(c + new Vector2(r * 0.35f, -r));
                    paint.LineTo(c + new Vector2(r * 0.15f, -r * 0.45f));
                    paint.LineTo(c + new Vector2(-r * 0.15f, -r * 0.45f));
                    break;
                default:
                    paint.Arc(c, r, 0f, 360f);
                    break;
            }
            paint.ClosePath();
            paint.Fill();
            paint.Stroke();
        }

        /// <summary>A regular polygon around <paramref name="center"/>; the first corner at <paramref name="startDeg"/> from straight up.</summary>
        private static void Polygon(Painter2D paint, Vector2 center, float radius, int corners, float startDeg)
        {
            for (int i = 0; i < corners; i++)
            {
                float angle = (startDeg + i * 360f / corners) * Mathf.Deg2Rad;
                var point = center + new Vector2(Mathf.Sin(angle) * radius, -Mathf.Cos(angle) * radius);
                if (i == 0)
                    paint.MoveTo(point);
                else
                    paint.LineTo(point);
            }
        }
    }
}
