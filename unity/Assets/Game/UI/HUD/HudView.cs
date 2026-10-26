#nullable enable
using System;
using Game.UI.Config;
using Game.UI.Presenters;
using Game.UI.ViewModels;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.HUD
{
    /// <summary>
    /// The HUD's widget layer: elements placed from <c>HudLayout</c> (spec 9.7) in the reference resolution; the panel
    /// scales the whole HUD (fit-height). World overlays live in their own layer under it.
    /// </summary>
    public sealed class HudView
    {
        private readonly HudConfig _config;

        public HudView(HudConfig config)
        {
            _config = config;
            Root = new VisualElement { name = "hud", pickingMode = PickingMode.Ignore };
            Root.AddToClassList("hud-layer");
        }

        public VisualElement Root { get; }

        /// <summary>Adds <paramref name="element"/> under its layout row. A missing row is a config error: logged, element hidden.</summary>
        public T Place<T>(string elementId, T element) where T : VisualElement
        {
            element.name = elementId;
            if (_config.TryGetLayout(elementId, out var row))
                UiLayout.Apply(element, row);
            else
            {
                Debug.LogError($"[HUD] HudLayout has no row '{elementId}'");
                element.style.display = DisplayStyle.None;
            }
            Root.Add(element);
            return element;
        }
    }

    /// <summary>
    /// Base of the HUD presenters (spec 9.9): builds its element once, redraws on its view models' <c>Changed</c> (never by
    /// polling), and may animate time-based parts (cooldown overlays, recharge fills) in <see cref="Animate"/>.
    /// </summary>
    public abstract class HudPresenter : IDisposable
    {
        private readonly ViewModel[] _sources;

        protected HudPresenter(HudConfig config, HudView view, params ViewModel[] sources)
        {
            Config = config;
            View = view;
            _sources = sources;
        }

        protected HudConfig Config { get; }

        protected HudView View { get; }

        protected HudTheme Theme => Config.Theme;

        public void Bind()
        {
            Build();
            foreach (var source in _sources)
                source.Changed += Refresh;
            Refresh();
        }

        public void Dispose()
        {
            foreach (var source in _sources)
                source.Changed -= Refresh;
        }

        /// <summary>Called every frame with the display clock; default: nothing to animate.</summary>
        public virtual void Animate(double nowMs)
        {
        }

        protected abstract void Build();

        protected abstract void Refresh();

        protected static void Show(VisualElement element, bool visible) =>
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }

    /// <summary>Small element factories of the HUD; colors come from <see cref="HudTheme"/>, structure from <c>Hud.uss</c>.</summary>
    public static class HudWidgets
    {
        public static Label Label(string text, string className)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("hud-text");
            label.AddToClassList(className);
            return label;
        }

        public static VisualElement Box(string className)
        {
            var box = new VisualElement { pickingMode = PickingMode.Ignore };
            box.AddToClassList(className);
            return box;
        }

        /// <summary>A panel with the theme's background and border (spec 9.7 tokens).</summary>
        public static VisualElement Panel(string className, HudTheme theme)
        {
            var panel = Box("hud-panel");
            panel.AddToClassList(className);
            panel.style.backgroundColor = theme.Panel;
            Border(panel, theme.Border);
            return panel;
        }

        public static void Border(VisualElement element, Color color)
        {
            var style = element.style;
            style.borderLeftColor = color;
            style.borderRightColor = color;
            style.borderTopColor = color;
            style.borderBottomColor = color;
        }

        /// <summary>A horizontal bar: a track with a fill whose width is the value's share.</summary>
        public static VisualElement Bar(string className, Color fillColor, out VisualElement fill)
        {
            var track = Box("hud-bar");
            track.AddToClassList(className);
            fill = Box("hud-bar__fill");
            fill.style.backgroundColor = fillColor;
            track.Add(fill);
            return track;
        }

        public static void SetFill(VisualElement fill, float fraction) =>
            fill.style.width = Length.Percent(Mathf.Clamp01(fraction) * 100f);

        public static void SetText(Label label, string text)
        {
            if (label.text != text)
                label.text = text;
        }
    }
}
