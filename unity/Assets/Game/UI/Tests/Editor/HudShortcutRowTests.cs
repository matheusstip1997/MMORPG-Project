#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Data.Tables;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.UI.Tests
{
    /// <summary>
    /// The shortcut row (spec 9.3 <c>shortcut_row</c>, WP-M2-11) on a real panel: the layout row's buttons in order with
    /// their <c>UiStrings</c> labels, a click toggles its window, the open window's button is marked, the arrow collapses the
    /// row (remembered in the local settings), hover names the button and its key, and the pointer mask.
    /// </summary>
    public sealed class HudShortcutRowTests
    {
        /// <summary>A window's open state, as the real window view models have it.</summary>
        private sealed class FakeWindow : ViewModel
        {
            public bool Open { get; private set; }

            public void Toggle()
            {
                Open = !Open;
                NotifyChanged();
            }
        }

        private static HudConfig Committed()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return HudConfig.From(set);
        }

        private static Dictionary<string, ShortcutTarget> Targets(Dictionary<string, FakeWindow> windows) =>
            windows.ToDictionary(w => w.Key, w => new ShortcutTarget(w.Value, () => w.Value.Open, w.Value.Toggle, "X"));

        [Test]
        public void The_committed_row_is_collapsible_and_names_the_windows_that_exist()
        {
            var hud = Committed();

            Assert.That(hud.TryGetLayout(HudElementIds.ShortcutRow, out var row), Is.True);
            Assert.That(row.Collapsible, Is.True);
            // Spec 9.3 order: CH, INV, SK, JB (WP-M3-1), the book, JR; the rest join with their windows.
            var order = new[] { "character", "inventory", "skills", "jobs", "book", "journal" };
            Assert.That(row.Buttons, Is.SubsetOf(order));
            Assert.That(row.Buttons.Select(id => System.Array.IndexOf(order, id)), Is.Ordered);
        }

        [UnityTest]
        public System.Collections.IEnumerator Buttons_toggle_their_windows_and_the_arrow_collapses_the_row_on_a_real_panel()
        {
            var hud = Committed();
            Assert.That(hud.TryGetLayout(HudElementIds.ShortcutRow, out var row), Is.True);
            var windows = row.Buttons.ToDictionary(id => id, _ => new FakeWindow());
            var settings = new MemoryLocalSettings();
            var panel = new HudTestPanel("shortcut-row-test");
            ShortcutRowPresenter? presenter = null;
            try
            {
                yield return null;
                panel.Ready(hud);
                var view = new HudView(hud);
                panel.Root.Add(view.Root);
                presenter = new ShortcutRowPresenter(hud, view, new ShortcutRowViewModel(settings, row.Buttons, row.Collapsible), Targets(windows));
                presenter.Bind();
                panel.Layout();

                // The labels from UiStrings, in the row's order, inside the row's 9.3 box.
                Assert.That(presenter.VisibleLabels(), Is.EqualTo(row.Buttons.Select(id => hud.Text.Get(HudTextIds.ShortcutLabel(id))).ToArray()));
                Assert.That(presenter.ArrowText, Is.EqualTo(">"));
                var box = new Rect(927 - 0.5f, 676 - 0.5f, 344 + 1f, 32 + 1f);
                foreach (string id in row.Buttons)
                {
                    var bounds = presenter.ButtonFor(id)!.worldBound;
                    Assert.That(box.Contains(bounds.min) && box.Contains(bounds.max), Is.True, $"{id} {bounds} is outside the shortcut row");
                }
                Assert.That(presenter.Arrow.worldBound.xMax, Is.LessThanOrEqualTo(presenter.ButtonFor(row.Buttons[0])!.worldBound.xMin + 0.5f),
                    "the arrow sits left of the first button");

                // A click opens the window and marks its button; a second click closes it.
                string first = row.Buttons[0];
                var button = presenter.ButtonFor(first)!;
                panel.Click(button);
                Assert.That(windows[first].Open, Is.True, "a click opens the window");
                Assert.That(windows.Where(w => w.Key != first).All(w => !w.Value.Open), Is.True, "only that window");
                Assert.That(button.resolvedStyle.borderTopColor, Is.EqualTo(hud.Theme.Accent), "the open window's button is marked");
                Assert.That(presenter.CapturesPointer, Is.True, "the mouse buttons are not attacks over the row");
                Assert.That(presenter.TooltipText, Is.EqualTo(hud.Text.Get(HudTextIds.ShortcutName(first), new TextArg("key", "X"))));
                panel.Click(button);
                Assert.That(windows[first].Open, Is.False, "a second click closes it");
                foreach (string id in row.Buttons.Skip(1))
                {
                    panel.Click(presenter.ButtonFor(id)!);
                    Assert.That(windows[id].Open, Is.True, id + " opens");
                }

                // The arrow hides every button and only itself remains; the state is remembered.
                panel.Click(presenter.Arrow);
                Assert.That(presenter.VisibleLabels(), Is.Empty);
                Assert.That(presenter.ArrowText, Is.EqualTo("<"));
                Assert.That(settings.GetBool(LocalSettingKeys.ShortcutRowCollapsed, false), Is.True);
                Assert.That(new ShortcutRowViewModel(settings, row.Buttons, row.Collapsible).Collapsed, Is.True, "collapsed after a restart");
                panel.Click(presenter.Arrow);
                Assert.That(presenter.VisibleLabels().Length, Is.EqualTo(row.Buttons.Length));
                Assert.That(settings.GetBool(LocalSettingKeys.ShortcutRowCollapsed, true), Is.False);

                panel.PointAt(new Vector2(640, 360)); // away from the row
                Assert.That(presenter.CapturesPointer, Is.False);
                Assert.That(presenter.TooltipText, Is.Empty);
            }
            finally
            {
                presenter?.Dispose();
                panel.Dispose();
            }
        }

        [Test]
        public void A_row_that_is_not_collapsible_never_collapses_and_buttons_without_a_window_are_left_out()
        {
            var hud = Committed();
            var model = new ShortcutRowViewModel(new MemoryLocalSettings(), new[] { "inventory", "character" }, collapsible: false);
            var presenter = new ShortcutRowPresenter(hud, new HudView(hud), model,
                Targets(new Dictionary<string, FakeWindow> { ["inventory"] = new FakeWindow() }));
            presenter.Bind();

            model.ToggleCollapsed();

            Assert.That(model.Collapsed, Is.False);
            Assert.That(presenter.ButtonFor("character"), Is.Null);
            Assert.That(presenter.VisibleLabels(), Is.EqualTo(new[] { "INV" }));
            presenter.Dispose();
        }
    }
}
