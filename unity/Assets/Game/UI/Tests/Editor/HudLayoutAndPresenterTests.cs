#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.Editor;
using Game.UI.HUD;
using Game.UI.Menus.Inventory;
using Game.UI.Overlays;
using Game.UI.Presenters;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Inventory;
using Game.UI.ViewModels.Quests;
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
            [HudElementIds.InventoryWindow] = new RectInt(716, 150, 300, 420), // spec 9.3 row inventory_window (WP16; tabs and equipment strip WP-M2-1; QA M2 wave 2 D3)
            // World-anchored (backlog G): around the drop's projected point, 150x22, centered, its bottom 24 px above it.
            [HudElementIds.PickupPrompt] = new RectInt(-75, -46, 150, 22),
            // World-anchored (R-3.3-01): around the name plate's point, 96x20, centered, its bottom 32 px above it.
            [HudElementIds.StaggerMarker] = new RectInt(-48, -52, 96, 20),
            // WP-M2-4: the gold under the resource panel, the sale and vendor windows on the left edge, the NPC prompt (world).
            [HudElementIds.Gold] = new RectInt(8, 126, 160, 24),
            // The stats readout under the gold, sized by its lines (play finding 2026-10-28).
            [HudElementIds.StatsReadout] = new RectInt(8, 156, 0, 0),
            [HudElementIds.OutpostWindow] = new RectInt(16, 190, 320, 340),
            [HudElementIds.VendorWindow] = new RectInt(16, 165, 320, 390), // the M3 stock of 8 rows (play finding on 6b25b51)
            [HudElementIds.NpcPrompt] = new RectInt(-90, -46, 180, 22),
            // WP-M2-3: the exp bar under the hotbar and its note (spec 9.3 gives the note only its position; 400x14 placeholder).
            [HudElementIds.ExpBar] = new RectInt(440, 702, 400, 6),
            [HudElementIds.ExpNote] = new RectInt(440, 685, 400, 14),
            // The skill tree window (WP-M2-5, placeholder): centered, 30 px above the middle.
            [HudElementIds.SkillTreeWindow] = new RectInt(330, 115, 620, 430),
            // WP-M2-8: the Bystial book window, centered (placeholder size).
            [HudElementIds.BookWindow] = new RectInt(346, 160, 360, 400),
            // WP-M2-6: the quest board in the sale window's place, the run tracker at its spec 9.3 row.
            [HudElementIds.QuestBoard] = new RectInt(16, 150, 360, 420),
            // WP-M3-1, grown for the job tree tab (WP-M3-7, placeholder).
            [HudElementIds.JobsWindow] = new RectInt(16, 152, 600, 430),
            [HudElementIds.ForgeWindow] = new RectInt(16, 150, 360, 420),
            [HudElementIds.RunTracker] = new RectInt(1024, 248, 240, 196),
            // WP-M2-11: the level line in the exp note's box, the shortcut row at its spec 9.3 place (927, 676, up to 344x32).
            [HudElementIds.ExpLabel] = new RectInt(440, 685, 400, 14),
            [HudElementIds.ShortcutRow] = new RectInt(927, 676, 344, 32),
            // WP-M2-11: the character sheet on the left edge (placeholder, the NPC windows' place).
            [HudElementIds.CharacterSheet] = new RectInt(16, 150, 300, 420),
            // WP-M2-10b: the minimap at its spec 9.3 row, above the run tracker.
            [HudElementIds.Minimap] = new RectInt(1024, 56, 240, 184),
            // The tutorial prompt (spec 12.3, 9.12; WP-M2-10; placeholder): top center under the boss bar.
            [HudElementIds.TutorialPrompt] = new RectInt(420, 70, 440, 44),
            // WP-M3-3 (spec 3.4 consumable use; placeholder): the quick-item slot right of the hotbar, mirroring parry_dodge.
            [HudElementIds.QuickItem] = new RectInt(848, 598, 52, 76),
            // WP-M3-5: the harvest prompt at the node in reach (world-anchored like the NPC prompt).
            [HudElementIds.NodePrompt] = new RectInt(-90, -46, 180, 22),
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
                // A world-anchored row is measured from a projected point: a zero-size box.
                bool world = HudElementIds.WorldAnchored.Contains(row.Id);
                var rect = world
                    ? UiLayout.Resolve(row.Anchor, row.Offset, row.Size, 0, 0)
                    : UiLayout.Resolve(row.Anchor, row.Offset, row.Size, settings.ReferenceWidth, settings.ReferenceHeight);
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
            foreach (string button in HudLayout.ShortcutButtonIds) // WP-M2-11: every button the layout may name
                ids.AddRange(new[] { HudTextIds.ShortcutLabel(button), HudTextIds.ShortcutName(button) });

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
            Assert.That(panel.ValueText(ResourcePanelPresenter.Toxicity), Is.Empty); // no own-stats message yet
        }

        [Test]
        public void Resource_panel_shows_TOX_and_SAT_and_the_stats_readout_from_the_own_stats_message()
        {
            // WP-M2-2 (R-3.4-03, spec 9.3): TOX and SAT share the fourth row; hovering shows Atk, Matk, Def, MDef and the attributes.
            var models = Build(out var root, out var presenters);
            var panel = presenters.OfType<ResourcePanelPresenter>().Single();

            models.Resources.SetMeters(12, 100, 40, 100);
            Assert.That(panel.ValueText(ResourcePanelPresenter.Toxicity), Is.Empty, "meters show only with the stats");
            models.Resources.SetStats(new StatsReadout(20, 5, 15, 8, 10, 5, 10, 2));

            Assert.That(panel.ValueText(ResourcePanelPresenter.Toxicity), Is.EqualTo("12 / 100"));
            Assert.That(panel.ValueText(ResourcePanelPresenter.Satiety), Is.EqualTo("40 / 100"));
            Assert.That(panel.ReadoutText, Is.EqualTo("Atk 20   Matk 5   Def 15   MDef 8\nSTR 10   DEX 5   STA 10   INT 2"));
            var labels = root.Q(HudElementIds.ResourcePanel).Query<Label>().ToList().Select(l => l.text).ToList();
            Assert.That(labels, Does.Contain("TOX"));
            Assert.That(labels, Does.Contain("SAT"));
            Assert.That(panel.Tooltip.name, Is.EqualTo(HudElementIds.StatsReadout)); // its own row, under the gold

            models.Resources.SetMeters(11, 100, 40, 100);
            Assert.That(panel.ValueText(ResourcePanelPresenter.Toxicity), Is.EqualTo("11 / 100"));
            models.Resources.Clear();
            Assert.That(panel.ValueText(ResourcePanelPresenter.Satiety), Is.Empty);
        }

        [Test]
        public void The_binder_fills_the_readout_and_lets_the_meters_decay_from_the_own_stats_message()
        {
            var config = Committed();
            var models = Build(out _, out var presenters);
            var panel = presenters.OfType<ResourcePanelPresenter>().Single();
            var feed = HudChatTests.Feed();
            double now = 1_000;
            var binder = new HudFeedBinder(config, models, () => now);
            binder.Bind(feed);

            var stats = new ClientStats(feed.Config);
            var values = new int[StatCount(feed)];
            values[StatIndex(feed, StatIds.Atk)] = 36;
            values[StatIndex(feed, StatIds.Str)] = 14;
            var message = StatsCodec.Write(new ReliableMessageWriter(),
                new OwnStats { Version = 1, Values = values, ToxicityMilli = 20_000 }).ToArray();
            Assert.That(stats.Apply(message, (long)now), Is.True);
            feed.RaiseStats(stats);

            Assert.That(models.Resources.HasStats, Is.True);
            Assert.That(models.Resources.Stats.Atk, Is.EqualTo(36));
            Assert.That(models.Resources.Stats.Str, Is.EqualTo(14));
            Assert.That(models.Resources.Toxicity, Is.EqualTo(20));
            int perMinute = feed.Config.Single<ConsumptionMeterSettings>().ToxicityDecayPerMinute;
            now += 60_000;
            binder.Frame(now);
            Assert.That(models.Resources.Toxicity, Is.EqualTo(System.Math.Max(0, 20 - perMinute)));
            Assert.That(panel.ValueText(ResourcePanelPresenter.Toxicity), Does.StartWith(models.Resources.Toxicity + " / "));

            feed.RaiseDisconnected("");
            Assert.That(models.Resources.HasStats, Is.False);
            binder.Dispose();
        }

        private static int StatCount(WorldClientFeed feed) =>
            feed.Config.TryGet(typeof(StatDef), out var table) ? table.Rows.Count : 0;

        private static int StatIndex(WorldClientFeed feed, string id)
        {
            feed.Config.TryGet(typeof(StatDef), out var table);
            for (int i = 0; i < table!.Rows.Count; i++)
            {
                if (((StatDef)table.Rows[i]).Id == id)
                    return i;
            }
            return -1;
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
        public void A_slot_label_keeps_the_token_size_and_breaks_only_between_words_with_a_short_name_or_an_ellipsis()
        {
            // QA wave 6 F5: the #48 fit shrank "Flamethrower" to about 6 px in one run and wrapped "Flamethrowe / r" in
            // others (spec 9.8 asks for at least 12 px; 9.7 slot labels are 11 px). Now the label never shrinks, and the
            // text is a pure function of the name, the short name and the measured widths: the same in every run.
            float Width(string text) => text.Length * 4f; // a fake font: 4 px per character
            const float slot = 41f;
            Assert.That(HotbarPresenter.FitLabel("Slash", "", Width, slot, 2, "…"), Is.EqualTo("Slash"));
            Assert.That(HotbarPresenter.FitLabel("Piercing Attack", "", Width, slot, 2, "…"), Is.EqualTo("Piercing\nAttack"));
            Assert.That(HotbarPresenter.FitLabel("Flamethrower", "Flamethr.", Width, slot, 2, "…"), Is.EqualTo("Flamethr."));
            Assert.That(HotbarPresenter.FitLabel("Life Regeneration", "Life Regen", Width, slot, 2, "…"), Is.EqualTo("Life Regen"));
            // No short name: the word is cut and ends with the ellipsis, never broken across lines.
            Assert.That(HotbarPresenter.FitLabel("Flamethrower", "", Width, slot, 2, "…"), Is.EqualTo("Flamethro…"));
            Assert.That(HotbarPresenter.FitLabel("Life Regeneration", "", Width, slot, 2, "…"), Is.EqualTo("Life\nRegenerat…"));
            // More lines than the slot holds: the last one ends with the ellipsis.
            Assert.That(HotbarPresenter.FitLabel("Stamina Buff Up Now", "", Width, slot, 2, "…"), Is.EqualTo("Stamina\nBuff Up…"));
            // Same inputs, same text; nothing measured yet leaves the name as it is.
            Assert.That(HotbarPresenter.FitLabel("Flamethrower", "", Width, slot, 2, "…"),
                Is.EqualTo(HotbarPresenter.FitLabel("Flamethrower", "", Width, slot, 2, "…")));
            Assert.That(HotbarPresenter.FitLabel("Flamethrower", "", Width, 0f, 2, "…"), Is.EqualTo("Flamethrower"));

            // The label is drawn at the slot label token, whatever the name.
            var config = Committed();
            var models = Build(out var root, out _);
            models.Hotbar.SetAction(10, "skill.mage.flamethrower");
            var label = root.Q(HudElementIds.HotbarRight).Q("slot_10").Q<Label>(className: "hud-slot__name");
            Assert.That(label.style.fontSize.value.value, Is.EqualTo((float)config.Theme.Tokens.FontSizeSlotLabel));
            Assert.That(config.Text.Has(HudTextIds.ActionShort("skill.mage.flamethrower")), Is.True);
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
        public void Exp_bar_fills_with_the_level_progress_and_the_note_shows_only_below_x1()
        {
            var config = Committed();
            var models = Build(out var root, out var presenters);
            var bar = presenters.OfType<ExpBarPresenter>().Single();
            var panel = presenters.OfType<ResourcePanelPresenter>().Single();
            Assert.That((bar.Fill, bar.NoteText, panel.ProgressText), Is.EqualTo((0f, "", "")));

            models.Progress.Set(new ProgressionState { Level = 3, Exp = 98, ExpToNext = 392, Rank = Rank.E, SkillPoints = 3, BandPercent = 100 });

            Assert.That(bar.Fill, Is.EqualTo(0.25f).Within(0.001f));
            Assert.That(bar.NoteText, Is.Empty);
            Assert.That(panel.ProgressText, Is.EqualTo("Lv 3   Rank E"));
            Assert.That(bar.LabelText, Is.EqualTo("Lv 3   Rank E   25%")); // WP-M2-11: the line above the bar
            Assert.That(bar.TooltipText, Is.Empty);
            Assert.That(root.Q(HudElementIds.ExpBar), Is.Not.Null);

            models.Progress.Set(new ProgressionState { Level = 27, Exp = 0, ExpToNext = 6048, Rank = Rank.F, BandPercent = 36 });

            Assert.That(bar.NoteText, Is.EqualTo(config.Text.Get(HudTextIds.ExpNote, new TextArg("multiplier", "0.36"))));
            Assert.That(bar.NoteText, Is.EqualTo("Exp x0.36, above rank range"));

            models.Progress.Set(new ProgressionState { Level = 130, Exp = 0, ExpToNext = 0, Rank = Rank.SS, BandPercent = 100 });
            Assert.That(bar.Fill, Is.EqualTo(1f)); // full at the level cap
            Assert.That(bar.LabelText, Is.EqualTo("Lv 130   Rank SS   100%"));
        }

        [Test]
        public void A_level_up_and_a_rank_up_show_their_toast_and_a_plain_exp_gain_does_not()
        {
            var config = Committed();
            var models = Build(out _, out var presenters);
            var toast = presenters.OfType<ToastPresenter>().Single();
            var feed = HudChatTests.Feed();
            var binder = new HudFeedBinder(config, models, () => 0);
            binder.Bind(feed);

            feed.RaiseProgression(new ProgressionState { Level = 1, ExpToNext = 172, Cause = ProgressionCause.Initial, BandPercent = 100 });
            feed.RaiseProgression(new ProgressionState { Level = 1, Exp = 20, ExpToNext = 172, Cause = ProgressionCause.Exp, BandPercent = 100 });
            Assert.That(toast.Text, Is.Empty);

            feed.RaiseProgression(new ProgressionState { Level = 3, Exp = 10, ExpToNext = 392, SkillPoints = 3, Cause = ProgressionCause.LevelUp, BandPercent = 100 });
            Assert.That(toast.Text, Is.EqualTo("Level 3!"));
            Assert.That(models.Progress.Level, Is.EqualTo(3));

            feed.RaiseProgression(new ProgressionState { Level = 3, Exp = 10, ExpToNext = 392, Rank = Rank.E, Cause = ProgressionCause.RankUp, BandPercent = 100 });
            Assert.That(toast.Text, Is.EqualTo("Rank E!"));
            binder.Unbind();
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
        public void A_kick_shows_its_reason_text_until_the_world_runs_again()
        {
            var config = Committed();
            var models = Build(out _, out var presenters);
            var toast = presenters.OfType<ToastPresenter>().Single();
            var feed = HudChatTests.Feed();
            var binder = new HudFeedBinder(config, models, () => 0);
            binder.Bind(feed);

            // Spec 9.10: the world client passes the kick's code (DisconnectReasonCode) with the disconnect.
            feed.RaiseDisconnected(nameof(ConnectRejectReason.Kicked));
            binder.Frame(3_600_000);

            Assert.That(toast.Text, Is.EqualTo(config.Reasons.Text(nameof(ConnectRejectReason.Kicked))));
            Assert.That(toast.Text, Is.EqualTo("You were disconnected by an administrator."));

            feed.RaiseSnapshotApplied();
            Assert.That(toast.Text, Is.Empty);

            feed.RaiseDisconnected(""); // the player left: nothing to show
            Assert.That(toast.Text, Is.Empty);
            binder.Dispose();
        }

        [Test]
        public void Buff_row_shows_flag_chips_then_status_names_and_the_boss_bar_only_while_engaged()
        {
            var config = Committed();
            var models = Build(out _, out var presenters);
            var buffs = presenters.OfType<BuffRowPresenter>().Single();
            var boss = presenters.OfType<BossBarPresenter>().Single();

            models.BuffRow.Set(new[] { new OwnStatus { NetId = 5, Stacks = 1, RemainingTicks = 10 } }, 1, EntityFlags.Armed, config);

            Assert.That(buffs.ChipTexts(), Is.EqualTo(new[] { "AR", "Weak" })); // WP-M2-11: flags first, then statuses
            Assert.That(buffs.ChipLetters(), Is.EqualTo(new[] { "AR", "We" }));
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

        /// <summary>The windows a player opens over the HUD (spec 9.3, 9.13).</summary>
        private static readonly string[] Windows =
        {
            HudElementIds.InventoryWindow, HudElementIds.SkillTreeWindow, HudElementIds.BookWindow, HudElementIds.QuestBoard,
            HudElementIds.CharacterSheet, HudElementIds.OutpostWindow, HudElementIds.VendorWindow, HudElementIds.ForgeWindow,
        };

        [UnityTest]
        public System.Collections.IEnumerator No_open_window_covers_the_minimap_or_the_run_tracker_on_a_1280x720_panel()
        {
            // QA M2 wave 2 D3: the minimap (placed after the windows, so drawn over them) hid the inventory window's header,
            // tabs and top bag row. The real minimap, run tracker (with a run) and inventory window (open); the other windows'
            // panels placed by their rows through View.Place, as their presenters place them (none moves its window after).
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            var hud = HudConfig.From(set);
            var inventory = InventoryConfig.From(set);
            var panel = new HudTestPanel("hud-window-layout-test");
            var presenters = new List<HudPresenter>();
            try
            {
                yield return null;
                panel.Ready(hud);
                var view = new HudView(hud);
                panel.Root.Add(view.Root);

                var inventoryModel = new InventoryWindowViewModel(inventory.Containers, inventory.NormalBagCode, inventory.Settings.Columns, inventory);
                var inventoryBinder = new InventoryWindowBinder(inventory, inventoryModel, new ToastViewModel(), 1500, () => 0, readToggleKey: false);
                var inventoryWindow = new InventoryWindowPresenter(hud, view, inventory, inventoryModel, inventoryBinder, icons: null);
                var tracker = new RunTrackerViewModel();
                presenters.Add(inventoryWindow);
                presenters.Add(new RunTrackerPresenter(hud, view, QuestConfig.From(set, hud, inventory), tracker));
                presenters.Add(new MinimapPresenter(hud, view, new MinimapModel(MinimapConfig.From(set)), () => null, icons: null));
                foreach (var presenter in presenters)
                    presenter.Bind();
                foreach (var id in Windows.Where(id => id != HudElementIds.InventoryWindow))
                    view.Place(id, HudWidgets.Panel("layout-test-window", hud.Theme));
                inventoryModel.SetOpen(true);
                inventoryModel.SetContents(new InventoryContents { Revision = 1, SlotCount = 30 });
                tracker.Set(new RunStatus
                {
                    Revision = 1,
                    State = RunState.Active,
                    BonusPercent = 10,
                    Steps = new[]
                    {
                        new RunStepEntry { Kind = QuestKind.Kill, MonsterNetId = 1, Required = 3, Progress = 1, RewardExp = 150, State = RunStepState.Active },
                    },
                });
                panel.Layout();

                var minimap = view.Root.Q(HudElementIds.Minimap).worldBound;
                var runTracker = view.Root.Q(HudElementIds.RunTracker);
                Assert.That((minimap.x, minimap.y, minimap.width, minimap.height), Is.EqualTo((1024f, 56f, 240f, 184f)), "the minimap at its 9.3 place");
                Assert.That(runTracker.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex), "the run tracker shows the run");
                var tracked = runTracker.worldBound;
                Assert.That((tracked.x, tracked.y, tracked.width, tracked.height), Is.EqualTo((1024f, 248f, 240f, 196f)), "the run tracker at its 9.3 place");
                Assert.That(inventoryWindow.Window.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex), "the inventory window is open");
                foreach (var id in Windows)
                {
                    var window = view.Root.Q(id);
                    Assert.That(window.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex), id + " is open");
                    var box = window.worldBound;
                    Assert.That(box.width > 0f && box.height > 0f, Is.True, id + " is laid out");
                    Assert.That(box.Overlaps(minimap), Is.False, $"{id} at {box} covers the minimap at {minimap}");
                    Assert.That(box.Overlaps(tracked), Is.False, $"{id} at {box} covers the run tracker at {tracked}");
                }
            }
            finally
            {
                foreach (var presenter in presenters)
                    presenter.Dispose();
                panel.Dispose();
            }
        }

        private static HudViewModels Build(out VisualElement root, out List<HudPresenter> presenters)
        {
            var config = Committed();
            var models = new HudViewModels(4, 4, 3, 20);
            var view = new HudView(config);
            root = view.Root;
            presenters = new List<HudPresenter>
            {
                new ResourcePanelPresenter(config, view, models.Resources, models.Progress),
                new ExpBarPresenter(config, view, models.Progress),
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
