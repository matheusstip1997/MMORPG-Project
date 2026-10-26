#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.Editor;
using Game.UI.HUD;
using Game.UI.Overlays;
using Game.UI.Presenters;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Stats;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>
    /// The HUD layout against the HudLayout table and the absolute positions of spec 9.3, the presenters (redraw on
    /// Changed, text from the tables), the theme, the telegraph meshes and the dev scene's HUD links (backlog I).
    /// </summary>
    public sealed class HudLayoutAndPresenterTests
    {
        /// <summary>Spec 9.3 element registry: x, y, width, height in the 1280 x 720 reference.</summary>
        private static readonly Dictionary<string, RectInt> Spec93 = new Dictionary<string, RectInt>
        {
            [HudElementIds.ResourcePanel] = new RectInt(8, 10, 330, 112),
            [HudElementIds.BossBar] = new RectInt(440, 16, 400, 44),
            [HudElementIds.Chat] = new RectInt(7, 587, 330, 126),
            [HudElementIds.ActionToast] = new RectInt(440, 440, 400, 30),
            [HudElementIds.CastBar] = new RectInt(490, 479, 300, 30),
            [HudElementIds.BuffRow] = new RectInt(560, 556, 160, 26),
            [HudElementIds.ParryDodge] = new RectInt(380, 598, 52, 76),
            [HudElementIds.HotbarLeft] = new RectInt(440, 590, 140, 92),
            [HudElementIds.Gauge] = new RectInt(590, 590, 100, 92),
            [HudElementIds.HotbarRight] = new RectInt(700, 590, 140, 92),
            [HudElementIds.InventoryWindow] = new RectInt(964, 220, 300, 280), // spec 9.3 row inventory_window (WP16)
        };

        private static HudConfig Committed()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return HudConfig.From(set);
        }

        [Test]
        public void Every_hud_element_has_a_layout_row_and_every_row_an_element()
        {
            var config = Committed();

            Assert.That(config.LayoutRows.Select(r => r.Id), Is.EquivalentTo(HudElementIds.All));
        }

        [Test]
        public void The_layout_rows_place_each_element_at_its_spec_9_3_position()
        {
            var config = Committed();
            var settings = config.Settings;

            foreach (var row in config.LayoutRows)
            {
                var rect = UiLayout.Resolve(row.Anchor, row.Offset, row.Size, settings.ReferenceWidth, settings.ReferenceHeight);
                Assert.That(rect, Is.EqualTo(Spec93[row.Id]), row.Id);
            }
        }

        [Test]
        public void Resolve_matches_apply_for_corner_and_center_anchors()
        {
            Assert.That(UiLayout.Resolve(UiAnchor.BottomRight, new[] { 16, 12 }, new[] { 100, 20 }, 1280, 720),
                Is.EqualTo(new RectInt(1164, 688, 100, 20)));
            Assert.That(UiLayout.Resolve(UiAnchor.Center, new[] { 10, -20 }, new[] { 100, 40 }, 1280, 720),
                Is.EqualTo(new RectInt(600, 320, 100, 40)));
        }

        [Test]
        public void Hud_presenters_bind_with_the_committed_tables_without_errors()
        {
            Build(out _, out var presenters);

            LogAssert.NoUnexpectedReceived();
            foreach (var presenter in presenters)
                presenter.Dispose();
        }

        [Test]
        public void Committed_ui_strings_cover_every_hud_text_id_and_hotbar_key()
        {
            var config = Committed();
            var ids = typeof(HudTextIds).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()).ToList();
            foreach (var row in config.LayoutRows)
                ids.AddRange(row.Slots.Where(s => s != 0).Select(HudTextIds.KeySlot));

            Assert.That(ids.Where(id => !config.Text.Has(id)), Is.Empty);
        }

        [Test]
        public void Theme_tokens_are_all_valid_colors()
        {
            var theme = Committed().Theme;
            var colors = typeof(HudTheme).GetProperties().Where(p => p.PropertyType == typeof(Color)).Select(p => (Color)p.GetValue(theme));

            Assert.That(colors, Has.None.EqualTo(Color.magenta));
            Assert.That(theme.Telegraph(ParryColor.Red), Is.Not.EqualTo(theme.Telegraph(ParryColor.Blue)));
        }

        [Test]
        public void Resource_panel_redraws_on_change_with_the_table_text()
        {
            var models = Build(out var root, out var presenters);
            var panel = presenters.OfType<ResourcePanelPresenter>().Single();

            models.Resources.Set(new OwnResources { Hp = 78, MaxHp = 150, Mp = 30, MaxMp = 30, Stamina = 100, MaxStamina = 100 });

            Assert.That(panel.ValueText(0), Is.EqualTo("78 / 150"));
            Assert.That(panel.ValueText(2), Is.EqualTo("100 / 100"));
            Assert.That(root.Q(HudElementIds.ResourcePanel), Is.Not.Null);
        }

        [Test]
        public void Hotbar_shows_the_action_names_on_their_keys_and_the_gauge_is_locked_with_its_tooltip()
        {
            var models = Build(out var root, out var presenters);
            var right = presenters.OfType<HotbarPresenter>().Single(p => p.SlotText(7) != null);

            models.Hotbar.SetAction(7, "skill.warrior.slash");

            Assert.That(right.SlotText(7), Is.EqualTo("Slash"));
            Assert.That(right.SlotText(1), Is.Null); // slot 1 is on the left side
            var gauge = presenters.OfType<GaugePresenter>().Single();
            Assert.That(gauge.Tooltip.text, Is.EqualTo("Class gauge: unlocks at level 100"));
            Assert.That(root.Q(HudElementIds.Gauge).ClassListContains("hud-gauge--locked"), Is.True);
            Assert.That(root.Q(HudElementIds.HotbarRight).Q("slot_0").ClassListContains("hud-slot--unassigned"), Is.True);
        }

        [Test]
        public void A_cooling_slot_shows_its_seconds_in_place_of_its_name()
        {
            // QA wave 5 V1: the number was drawn over the label ("Pie6ing Attack").
            var models = Build(out _, out var presenters);
            var right = presenters.OfType<HotbarPresenter>().Single(p => p.SlotText(7) != null);
            models.Hotbar.SetAction(7, "skill.warrior.slash");
            models.Hotbar.SetCooldown(7, 0, 3000, 3000, 0);

            right.Animate(0);
            var cooling = right.SlotCooldown(7)!.Value;
            Assert.That(cooling.Seconds, Is.Not.Empty);
            Assert.That(cooling.NameHidden, Is.True);

            right.Animate(3500);
            Assert.That(right.SlotCooldown(7)!.Value, Is.EqualTo(("", false)));
            Assert.That(right.SlotText(7), Is.EqualTo("Slash"));
        }

        [Test]
        public void A_slot_label_shrinks_so_its_longest_word_fits_and_never_grows()
        {
            // QA wave 5 V2: "Flamethr / ower" broke mid-word at the table's 11 px.
            Assert.That(HotbarPresenter.FitLabelSize(11f, 11f, 30f, 42f), Is.EqualTo(11f));
            Assert.That(HotbarPresenter.FitLabelSize(11f, 11f, 66f, 42f), Is.EqualTo(7f));
            Assert.That(HotbarPresenter.FitLabelSize(11f, 7f, 42f, 42f), Is.EqualTo(7f)); // measured at the shrunk size
            Assert.That(HotbarPresenter.FitLabelSize(11f, 5f, 10f, 42f), Is.EqualTo(11f)); // a short word goes back to the base
            Assert.That(HotbarPresenter.FitLabelSize(11f, 11f, 0f, 42f), Is.EqualTo(11f)); // nothing measured yet
        }

        [Test]
        public void Toast_shows_the_reason_text_and_charges_show_their_counts()
        {
            var config = Committed();
            var models = Build(out _, out var presenters);
            var toast = presenters.OfType<ToastPresenter>().Single();
            var charges = presenters.OfType<ParryDodgePresenter>().Single();

            models.Toast.Show("NoStamina", false, 0, 1_000);
            models.Charges.Set(new ChargeView(1, 2, 500, 3_000), new ChargeView(2, 2, 0, 3_000), 50);

            Assert.That(toast.Text, Is.EqualTo(config.Reasons.Text("NoStamina")));
            Assert.That((charges.ParryText, charges.DodgeText), Is.EqualTo(("1", "2")));
            models.Toast.Hide();
            Assert.That(toast.Text, Is.Empty);
        }

        [Test]
        public void A_refused_pickup_shows_its_reason_text_in_the_toast()
        {
            var config = Committed();
            var models = Build(out _, out var presenters);
            var toast = presenters.OfType<ToastPresenter>().Single();

            // HudFeedBinder.OnPickupRefused (backlog G): the PickupReason code, text from ReasonCodeStrings.
            models.Toast.Show(nameof(Shared.Protocol.PickupReason.BagFull), false, 0, 1_000);

            Assert.That(toast.Text, Is.EqualTo(config.Reasons.Text("BagFull")));
            Assert.That(toast.Text, Is.EqualTo("Your bag is full."));
        }

        [Test]
        public void Buff_row_shows_status_names_then_flag_chips_and_the_boss_bar_only_while_engaged()
        {
            var config = Committed();
            var models = Build(out _, out var presenters);
            var buffs = presenters.OfType<BuffRowPresenter>().Single();
            var boss = presenters.OfType<BossBarPresenter>().Single();

            models.BuffRow.Set(new[] { new OwnStatus { NetId = 5, Stacks = 1, RemainingTicks = 10 } }, 1, EntityFlags.Armed, config);

            Assert.That(buffs.ChipTexts(), Is.EqualTo(new[] { "Weak", "AR" }));
            Assert.That(boss.IsShown, Is.False);
            models.BossBar.Engage(9, "m1.cyclopsBoss", Rank.F, 63, 0);
            Assert.That(boss.IsShown, Is.True);
        }

        [Test]
        public void Telegraph_meshes_match_their_shapes()
        {
            var config = Committed();
            // The cyclops stomp: a circle of 4.5 m around the boss (spec 3.1 WP10 table).
            var stomp = config.Actions.Find("monster.cyclops.stomp")!;
            var circle = TelegraphMeshes.Build(config.Shapes(stomp.NetId));
            Assert.That(circle.bounds.size.x, Is.EqualTo(9f).Within(0.05f));
            Assert.That(circle.bounds.size.z, Is.EqualTo(9f).Within(0.05f));
            Assert.That(circle.bounds.size.y, Is.EqualTo(0f));

            // The slam: a capsule 5 m straight ahead with 80 cm radius (rounded ends behind and in front).
            var slam = config.Actions.Find("monster.cyclops.slam")!;
            var box = TelegraphMeshes.Build(config.Shapes(slam.NetId));
            Assert.That(box.bounds.min.z, Is.EqualTo(-0.8f).Within(0.01f));
            Assert.That(box.bounds.max.z, Is.EqualTo(5.8f).Within(0.01f));
            Assert.That(box.bounds.size.x, Is.EqualTo(1.6f).Within(0.01f));

            // The rock throw: a lane of the projectile's range.
            var rock = config.Actions.Find("monster.cyclops.rockThrow")!;
            Assert.That(config.Shapes(rock.NetId).Single().ReachCm, Is.EqualTo(1_500));
            Object.DestroyImmediate(circle);
            Object.DestroyImmediate(box);
        }

        [Test]
        public void The_dev_world_scene_has_the_hud_with_its_links()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Game/Dev/PredictionDev.unity", OpenSceneMode.Single);
            var root = scene.GetRootGameObjects().Select(o => o.GetComponentInChildren<HudRoot>(true)).FirstOrDefault(r => r != null);

            Assert.That(root, Is.Not.Null, "no HudRoot in the dev scene: rebuild it (MMORPG / Dev / Rebuild Prediction Dev Scene)");
            Assert.That(HudSceneInstaller.HasReferences(root!, out var problem), Is.True, problem);
        }

        private static HudViewModels Build(out VisualElement root, out List<HudPresenter> presenters)
        {
            var config = Committed();
            var models = new HudViewModels(4, 4, 3, 20);
            var view = new HudView(config);
            root = view.Root;
            presenters = new List<HudPresenter>
            {
                new ResourcePanelPresenter(config, view, models.Resources),
                new BossBarPresenter(config, view, models.BossBar),
                new ChatPresenter(config, view, models.Chat, _ => { }),
                new ToastPresenter(config, view, models.Toast),
                new CastBarPresenter(config, view, models.CastBar),
                new BuffRowPresenter(config, view, models.BuffRow),
                new ParryDodgePresenter(config, view, models.Charges),
                new HotbarPresenter(config, view, models.Hotbar, HudElementIds.HotbarLeft),
                new GaugePresenter(config, view),
                new HotbarPresenter(config, view, models.Hotbar, HudElementIds.HotbarRight),
            };
            foreach (var presenter in presenters)
                presenter.Bind();
            return models;
        }
    }
}
