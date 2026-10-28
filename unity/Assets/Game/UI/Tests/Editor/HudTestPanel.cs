#nullable enable
using System;
using System.Reflection;
using Game.UI.Config;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>
    /// A real UI Toolkit panel at the 1280 x 720 reference (spec 9.2) with the HUD's theme, styles and font, for the
    /// WP-M2-11 presenter tests: create it, yield one frame (<see cref="Ready"/>), add the HUD view, then lay out and send
    /// pointer events through the panel's own dispatch (enter and leave come from UI Toolkit, as for a player's mouse).
    /// </summary>
    internal sealed class HudTestPanel : IDisposable
    {
        private const string ThemePath = "Assets/Game/UI/Login/LoginTheme.tss"; // the theme HudSceneInstaller gives the HUD

        private readonly PanelSettings _settings;
        private readonly RenderTexture _target;
        private readonly GameObject _host;
        private readonly UIDocument _document;

        public HudTestPanel(string name)
        {
            _settings = ScriptableObject.CreateInstance<PanelSettings>();
            _settings.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            _settings.scaleMode = PanelScaleMode.ConstantPixelSize; // reference pixels
            _target = new RenderTexture(1280, 720, 24);
            _settings.targetTexture = _target;
            _host = new GameObject(name);
            _document = _host.AddComponent<UIDocument>();
            _document.panelSettings = _settings;
        }

        /// <summary>The panel root; valid after one frame.</summary>
        public VisualElement Root => _document.rootVisualElement;

        /// <summary>After the first frame: the HUD styles and font on the root.</summary>
        public void Ready(HudConfig hud)
        {
            Assert.That(Root, Is.Not.Null, "the UIDocument made no panel");
            Root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(Game.UI.Editor.HudSceneInstaller.StylesPath));
            Root.style.unityFontDefinition = FontDefinition.FromFont(AssetDatabase.LoadAssetAtPath<Font>(Game.UI.Editor.HudSceneInstaller.FontPath));
            Root.style.fontSize = hud.Theme.Tokens.FontSizeBody;
        }

        /// <summary>Runs the panel's style and layout pass now (an edit-mode panel is not updated by the player loop).</summary>
        public void Layout()
        {
            var update = Root.panel.GetType().GetMethod("UpdateWithoutRepaint",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            Assert.That(update, Is.Not.Null, "UI Toolkit's panel has no UpdateWithoutRepaint");
            update!.Invoke(Root.panel, null);
        }

        /// <summary>Moves the pointer to the middle of <paramref name="element"/> (enter and leave events follow).</summary>
        public void PointTo(VisualElement element) => PointAt(element.worldBound.center);

        public void PointAt(Vector2 position)
        {
            Send(EventType.MouseMove, position);
            Layout();
        }

        /// <summary>A left click on the middle of <paramref name="element"/>, through the panel.</summary>
        public void Click(VisualElement element)
        {
            var at = element.worldBound.center;
            Send(EventType.MouseMove, at);
            Send(EventType.MouseDown, at);
            Send(EventType.MouseUp, at);
            Layout();
        }

        private void Send(EventType type, Vector2 position)
        {
            var imgui = new Event { type = type, mousePosition = position, button = 0, clickCount = type == EventType.MouseDown ? 1 : 0 };
            EventBase e = type switch
            {
                EventType.MouseDown => PointerDownEvent.GetPooled(imgui),
                EventType.MouseUp => PointerUpEvent.GetPooled(imgui),
                _ => PointerMoveEvent.GetPooled(imgui),
            };
            using (e)
                Root.panel.visualTree.SendEvent(e);
        }

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(_host);
            UnityEngine.Object.DestroyImmediate(_settings);
            _target.Release();
            UnityEngine.Object.DestroyImmediate(_target);
        }
    }
}
