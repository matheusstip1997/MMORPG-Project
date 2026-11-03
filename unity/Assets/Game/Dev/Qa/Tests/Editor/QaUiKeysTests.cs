#nullable enable
using System.Collections;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Game.Dev.Qa.EditorTests
{
    /// <summary>
    /// The chat-echo step's keys (wave 2 fixer): on a real panel, the Enter that opened the HUD chat input and the typed line
    /// reach the field's text editor, as a player's keys do. Since #143 the step aimed them at the field the focus controller
    /// names, which delegates its focus to its inner text element, so the line never arrived.
    /// </summary>
    public sealed class QaUiKeysTests
    {
        private const string HudFontPath = "Assets/ThirdParty/Fonts/BarlowSemiCondensed/BarlowSemiCondensed-Regular.ttf"; // HudSceneInstaller.FontPath

        /// <summary>The panel's style and layout pass now (an edit-mode panel is not updated by the player loop; as HudTestPanel).</summary>
        private static void Layout(UIDocument document)
        {
            var panel = document.rootVisualElement.panel;
            var update = panel.GetType().GetMethod("UpdateWithoutRepaint",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null, System.Type.EmptyTypes, null);
            Assert.That(update, Is.Not.Null, "UI Toolkit's panel has no UpdateWithoutRepaint");
            update!.Invoke(panel, null);
        }

        [UnityTest]
        public IEnumerator The_opening_Enter_and_a_typed_line_reach_the_open_chat_input()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            var hud = HudConfig.From(set);
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>("Assets/Game/UI/Login/LoginTheme.tss");
            var target = new RenderTexture(1280, 720, 24);
            settings.targetTexture = target;
            var host = new GameObject("qa-ui-keys-test");
            var document = host.AddComponent<UIDocument>();
            document.panelSettings = settings;
            ChatPresenter? chat = null;
            try
            {
                yield return null;
                // The HUD's font, as HudRoot sets it: the field's caret advances only over text it can lay out.
                document.rootVisualElement.style.unityFontDefinition = FontDefinition.FromFont(AssetDatabase.LoadAssetAtPath<Font>(HudFontPath));
                var view = new HudView(hud);
                document.rootVisualElement.Add(view.Root);
                var model = new ChatViewModel(hud.Chat.HistoryLines, hud.Chat.MaxChars);
                chat = new ChatPresenter(hud, view, model, _ => { });
                chat.Bind();
                Layout(document); // styles and the font resolved, as after the frames the HUD was up
                var field = view.Root.Q<TextField>("chat_input");

                chat.PressEnter(); // the HUD's read of the QA keyboard's Enter
                Layout(document);
                Assert.That(QaUiKeys.FocusedIn(field), Is.Not.Null, "the open input has the focus");
                Assert.That(QaUiKeys.Press(field, KeyCode.Return, '\n'), Is.True, "the same press, through UI Toolkit");
                Assert.That(model.InputOpen && QaUiKeys.FocusedIn(field) != null, Is.True, "still open and focused");

                const string line = "QA smoke: hello <b>world</b>";
                Assert.That(QaUiKeys.Type(field, line), Is.True);
                Assert.That(chat.Draft, Is.EqualTo(line), "the typed line is in the field");
            }
            finally
            {
                chat?.Dispose();
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(settings);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }
    }
}
