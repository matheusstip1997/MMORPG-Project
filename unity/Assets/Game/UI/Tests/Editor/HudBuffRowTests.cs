#nullable enable
using System.Linq;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Protocol;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.UI.Tests
{
    /// <summary>
    /// The buff row (spec 9.3 <c>buff_row</c>, 3.7 "M2 status list"; WP-M2-11) on a real panel: at most
    /// <c>HudSettings.buffRowMaxIcons</c> square icons, flags first, buff and debuff borders, the initials while the icon is a
    /// placeholder, the hover text with the time left counting down, and a status gone from the snapshot leaves the row.
    /// </summary>
    public sealed class HudBuffRowTests
    {
        private const ushort StaminaBuff = 1, Burn = 4, Weakened = 5;

        private static HudConfig Committed()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return HudConfig.From(set);
        }

        [Test]
        public void Initials_take_two_words_or_two_letters()
        {
            Assert.That(BuffRowPresenter.Initials("Magic Barrier"), Is.EqualTo("MB"));
            Assert.That(BuffRowPresenter.Initials("Weak"), Is.EqualTo("We"));
            Assert.That(BuffRowPresenter.Initials("AR"), Is.EqualTo("AR"));
            Assert.That(BuffRowPresenter.Initials(""), Is.EqualTo(""));
        }

        [Test]
        public void Seconds_left_round_up_and_count_down_on_the_display_clock()
        {
            var config = Committed();
            var model = new BuffRowViewModel();
            model.SetClock(100, 1_000, 50);
            model.Set(new[] { new OwnStatus { NetId = Burn, Stacks = 1, RemainingTicks = 40 } }, 100, EntityFlags.None, config);

            Assert.That(model.SecondsLeft(0, 1_000), Is.EqualTo(2)); // 40 ticks of 50 ms
            Assert.That(model.SecondsLeft(0, 1_500), Is.EqualTo(2)); // 1.5 s rounds up
            Assert.That(model.SecondsLeft(0, 2_100), Is.EqualTo(1));
            Assert.That(model.SecondsLeft(0, 3_000), Is.EqualTo(0));
        }

        [UnityTest]
        public System.Collections.IEnumerator Ten_icons_at_most_flags_first_with_borders_hover_and_expiry_on_a_real_panel()
        {
            var hud = Committed();
            Assert.That(hud.Settings.BuffRowMaxIcons, Is.EqualTo(10)); // spec 3.7 "M2 status list"
            var model = new BuffRowViewModel();
            var panel = new HudTestPanel("buff-row-test");
            BuffRowPresenter? presenter = null;
            try
            {
                yield return null;
                panel.Ready(hud);
                var view = new HudView(hud);
                panel.Root.Add(view.Root);
                presenter = new BuffRowPresenter(hud, view, model);
                presenter.Bind();

                // Twelve statuses (the snapshot carries up to 15) and the armed flag: the flag and nine statuses show.
                var statuses = Enumerable.Range(0, 12).Select(i => new OwnStatus
                {
                    NetId = i % 3 == 0 ? StaminaBuff : i % 3 == 1 ? Burn : Weakened,
                    Stacks = (byte)(i == 0 ? 3 : 1),
                    RemainingTicks = (ushort)(40 + i),
                }).ToArray();
                model.SetClock(100, 0, 50);
                model.Set(statuses, 100, EntityFlags.Armed, hud);
                panel.Layout();

                var names = presenter.ChipTexts();
                Assert.That(names.Length, Is.EqualTo(10), "buffRowMaxIcons icons, the rest stay active but are not shown");
                Assert.That(names[0], Is.EqualTo(hud.Text.Get(HudTextIds.FlagArmed)), "flags first");
                Assert.That(presenter.ChipLetters()[1], Is.EqualTo(BuffRowPresenter.Initials(hud.Text.Get("status.staminaBuff"))),
                    "the initials while the icon is a placeholder");

                // Square icons, the buff and debuff borders (shape and color, spec 9.8), centered on the row's 9.3 place.
                var buff = presenter.ChipElement(1);
                Assert.That(buff.worldBound.width, Is.EqualTo(buff.worldBound.height).Within(0.5f));
                Assert.That(buff.resolvedStyle.borderTopColor, Is.EqualTo(hud.Theme.BuffBorder));
                int debuffChip = System.Array.IndexOf(names, hud.Text.Get("status.burn"));
                Assert.That(presenter.ChipElement(debuffChip).resolvedStyle.borderTopColor, Is.EqualTo(hud.Theme.DebuffBorder));
                float middle = (presenter.ChipElement(0).worldBound.xMin + presenter.ChipElement(9).worldBound.xMax) / 2f;
                Assert.That(middle, Is.EqualTo(640f).Within(1f), "the row stays centered over the hotbar");

                // Hover: the name, stacks and seconds left, counting down on the display clock.
                panel.PointTo(buff);
                Assert.That(presenter.TooltipText, Is.EqualTo(hud.Text.Get(HudTextIds.BuffTooltipStacks,
                    new TextArg("name", names[1]), new TextArg("stacks", "3"), new TextArg("seconds", "2"))));
                presenter.Animate(1_200);
                Assert.That(presenter.TooltipText, Does.EndWith("1 s left"));
                panel.PointAt(new Vector2(640, 100));
                Assert.That(presenter.TooltipText, Is.Empty);

                // The next snapshot without the statuses (they expired on the server): the icons leave the row.
                model.SetClock(150, 2_500, 50);
                model.Set(new[] { new OwnStatus { NetId = Weakened, Stacks = 1, RemainingTicks = 20 } }, 150, EntityFlags.None, hud);
                panel.Layout();
                Assert.That(presenter.ChipTexts(), Is.EqualTo(new[] { hud.Text.Get("status.weakened") }));
                model.Set(System.Array.Empty<OwnStatus>(), 151, EntityFlags.None, hud);
                Assert.That(presenter.ChipTexts(), Is.Empty);
            }
            finally
            {
                presenter?.Dispose();
                panel.Dispose();
            }
        }
    }
}
