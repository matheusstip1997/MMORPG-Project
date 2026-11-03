#nullable enable
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>
    /// HUD controls never keep the keyboard (wave 2 fixer, the #143 follow-up): a clicked HUD button would otherwise hold
    /// the focus, so Space or Enter submitted it and UI Toolkit's navigation moved the focus between buttons on WASD. Only
    /// the chat's text field takes the focus.
    /// </summary>
    public sealed class HudFocusTests
    {
        private sealed class FakeWindow : ViewModel
        {
            public bool Open { get; private set; }

            public void Toggle()
            {
                Open = !Open;
                NotifyChanged();
            }
        }

        [UnityTest]
        public IEnumerator A_clicked_HUD_button_takes_no_focus_and_only_the_chat_input_is_focusable()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            var hud = HudConfig.From(set);
            Assert.That(hud.TryGetLayout(HudElementIds.ShortcutRow, out var row), Is.True);
            var windows = row.Buttons.ToDictionary(id => id, _ => new FakeWindow());
            var panel = new HudTestPanel("hud-focus-test");
            ShortcutRowPresenter? shortcuts = null;
            ChatPresenter? chat = null;
            try
            {
                yield return null;
                panel.Ready(hud);
                var view = new HudView(hud);
                panel.Root.Add(view.Root);
                shortcuts = new ShortcutRowPresenter(hud, view, new ShortcutRowViewModel(new MemoryLocalSettings(), row.Buttons, row.Collapsible),
                    windows.ToDictionary(w => w.Key, w => new ShortcutTarget(w.Value, () => w.Value.Open, w.Value.Toggle, "X")));
                shortcuts.Bind();
                chat = new ChatPresenter(hud, view, new ChatViewModel(hud.Chat.HistoryLines, hud.Chat.MaxChars), _ => { });
                chat.Bind();
                panel.Layout();

                var button = shortcuts.ButtonFor(row.Buttons[0])!;
                panel.Click(button);
                Assert.That(windows[row.Buttons[0]].Open, Is.True, "the click still works");
                Assert.That(panel.Root.panel.focusController.focusedElement, Is.Null, "the clicked button keeps no focus");
                panel.Click(shortcuts.Arrow);
                Assert.That(panel.Root.panel.focusController.focusedElement, Is.Null, "nor does the arrow");

                var input = view.Root.Q<TextField>("chat_input");
                var focusable = new List<VisualElement>();
                view.Root.Query<VisualElement>().ForEach(e =>
                {
                    if (e.focusable && e != input && !input.Contains(e))
                        focusable.Add(e);
                });
                Assert.That(focusable.Select(e => e.GetType().Name + ":" + e.name), Is.Empty, "HUD elements that can take the keyboard");
                Assert.That(input.focusable, Is.True, "the chat input still types");
            }
            finally
            {
                chat?.Dispose();
                shortcuts?.Dispose();
                panel.Dispose();
            }
        }
    }
}
