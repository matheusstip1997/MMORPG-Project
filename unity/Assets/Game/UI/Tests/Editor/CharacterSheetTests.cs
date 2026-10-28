#nullable enable
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Character;
using Game.UI.ViewModels.Character;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>
    /// The character sheet (spec 9.13 M2, WP-M2-11): every stat of <c>OwnStats</c> in its section and unit, the class,
    /// level, rank, exp and points from the feed; the layout on a real panel; the committed texts and key.
    /// </summary>
    public sealed class CharacterSheetTests
    {
        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static WorldClientFeed Feed(ConfigSet set)
        {
            var settings = PredictionSettings.From(set);
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
            var catalog = ActionCatalog.From(set);
            return new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                null, null, settings.TickMs);
        }

        /// <summary>Own stats as the server sends them: StatDef row order, value = 10 x (row index + 1).</summary>
        private static ClientStats Stats(ConfigSet set)
        {
            Assert.That(set.TryGet(typeof(StatDef), out var table), Is.True);
            var message = new OwnStats { Version = 3, Values = Enumerable.Range(1, table.Rows.Count).Select(i => i * 10).ToArray() };
            var stats = new ClientStats(set);
            Assert.That(stats.Apply(StatsCodec.Write(new ReliableMessageWriter(), message), 0), Is.True);
            Assert.That(stats.HasValues, Is.True);
            return stats;
        }

        private static string Expected(ConfigSet set, ClientStats stats, string statId, StatUnit unit) =>
            HudConfig.From(set).Text.Get(CharacterSheetTextIds.Value(unit), new TextArg("value", NumberText.Of(stats.Get(statId))));

        [Test]
        public void The_feed_fills_the_sheet_with_every_stat_the_level_rank_exp_and_points()
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var sheet = CharacterSheetConfig.From(set);
            var model = new CharacterSheetViewModel();
            var binder = new CharacterSheetBinder(sheet, model, readToggleKey: false);
            var presenter = new CharacterSheetPresenter(hud, new HudView(hud), sheet, model, binder.Close);
            presenter.Bind();
            var feed = Feed(set);
            binder.Bind(feed);
            model.Toggle();
            Assert.That((presenter.IsShown, presenter.WaitingShown), Is.EqualTo((true, true)));

            var stats = Stats(set);
            feed.RaiseStats(stats);
            feed.RaiseProgression(new ProgressionState { Level = 12, Exp = 1240, ExpToNext = 3000, Rank = Rank.E, SkillPoints = 2, BandPercent = 100 });

            Assert.That(presenter.WaitingShown, Is.False);
            Assert.That(set.TryGet(typeof(StatDef), out var statTable), Is.True);
            Assert.That(sheet.Lines.Count, Is.EqualTo(statTable.Rows.Count), "one line per stat");
            foreach (var line in sheet.Lines)
                Assert.That(presenter.ValueText(line.StatId), Is.EqualTo(Expected(set, stats, line.StatId, line.Unit)), line.StatId);
            Assert.That(presenter.ValueText("critRate"), Does.EndWith("%"));
            Assert.That(presenter.ValueText("hpRegen"), Does.EndWith("/s"));
            Assert.That((presenter.LevelText, presenter.ExpText, presenter.PointsText),
                Is.EqualTo(("Level 12   Rank E", "Exp 1240 / 3000", "Skill points: 2")));

            feed.RaiseDisconnected("Disconnected");
            Assert.That((presenter.WaitingShown, presenter.LevelText), Is.EqualTo((true, "")));
            binder.Close();
            Assert.That(presenter.IsShown, Is.False);
            presenter.Dispose();
            binder.Dispose();
        }

        [Test]
        public void The_lines_follow_the_sections_and_their_order_and_attributes_come_first()
        {
            var sheet = CharacterSheetConfig.From(Tables());

            Assert.That(sheet.Lines.Select(l => (int)l.Section), Is.Ordered);
            Assert.That(sheet.Lines.Take(4).Select(l => l.StatId), Is.EqualTo(new[] { "str", "dex", "sta", "int" }));
            Assert.That(sheet.Lines.Select(l => l.StatId), Is.Unique);
        }

        [UnityTest]
        public System.Collections.IEnumerator The_sheet_fits_its_layout_row_and_its_stats_scroll_inside_it_on_a_real_panel()
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var sheet = CharacterSheetConfig.From(set);
            var model = new CharacterSheetViewModel();
            var panel = new HudTestPanel("character-sheet-test");
            CharacterSheetPresenter? presenter = null;
            try
            {
                yield return null;
                panel.Ready(hud);
                var view = new HudView(hud);
                panel.Root.Add(view.Root);
                presenter = new CharacterSheetPresenter(hud, view, sheet, model, () => model.SetOpen(false));
                presenter.Bind();
                var stats = Stats(set);
                model.SetStats(sheet.Lines.Select(l => l.StatId), stats.Get);
                model.SetProgression(new ProgressionState { Level = 30, Exp = 10, ExpToNext = 900, Rank = Rank.F, SkillPoints = 0 });
                model.SetClass("warrior");
                model.SetOpen(true);
                panel.Layout();

                var window = presenter.Window;
                Assert.That(hud.TryGetLayout(HudElementIds.CharacterSheet, out var row), Is.True);
                Assert.That((window.layout.width, window.layout.height), Is.EqualTo(((float)row.Size[0], (float)row.Size[1])),
                    "the sheet keeps the size of its HudLayout row");
                var scroll = window.Q<ScrollView>("char_stats");
                Assert.That(scroll.worldBound.yMax, Is.LessThanOrEqualTo(window.worldBound.yMax + 0.5f), "the stats scroll inside the sheet");
                Assert.That(presenter.ClassText, Is.EqualTo(hud.Text.Get("class.warrior")));
                var atk = presenter.ValueLabel("atk")!;
                Assert.That(atk.resolvedStyle.fontSize, Is.GreaterThanOrEqualTo((float)hud.Theme.Tokens.FontSizeBody), "spec 9.8: 12 px text");
                Assert.That(atk.worldBound.xMax, Is.LessThanOrEqualTo(window.worldBound.xMax + 0.5f), "the value is inside the sheet");
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                presenter?.Dispose();
                panel.Dispose();
            }
        }

        [Test]
        public void Committed_ui_strings_cover_the_sheet_its_sections_units_and_every_stat_and_the_key_is_bound()
        {
            var set = Tables();
            var text = HudConfig.From(set).Text;
            var ids = CharacterSheetTextIds.All
                .Concat(System.Enum.GetValues(typeof(CharacterSheetSection)).Cast<CharacterSheetSection>().Select(CharacterSheetTextIds.Section))
                .Concat(System.Enum.GetValues(typeof(StatUnit)).Cast<StatUnit>().Select(CharacterSheetTextIds.Value))
                .Concat(CharacterSheetConfig.From(set).Lines.Select(l => UiStringCoverage.StatId(l.StatId)));

            Assert.That(ids.Where(id => !text.Has(id)), Is.Empty);
            Assert.That(CharacterSheetConfig.From(set).ToggleBinding, Is.EqualTo("<Keyboard>/p"));
        }
    }
}
