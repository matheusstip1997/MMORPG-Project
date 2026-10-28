#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>
    /// The tutorial (spec 12.3 R-12.3-02 and R-12.3-03, spec 9.12; WP-M2-10) with the committed tables: the schedule shows one
    /// prompt at a time in level order and never twice for a character, its triggers, the prompt panel's text and dismiss,
    /// the level gate's refusal, and the HUD by level (the exp bar hidden below 10).
    /// </summary>
    public sealed class HudTutorialTests
    {
        private ConfigSet _set = null!;
        private HudConfig _config = null!;
        private WorldClientFeed _feed = null!;
        private HudView _view = null!;
        private TutorialPromptViewModel _model = null!;
        private TutorialBinder _binder = null!;
        private TutorialPromptPresenter _presenter = null!;
        private ExpBarPresenter _expBar = null!;
        private HudViewModels _models = null!;
        private MemoryTutorialSeenStore _seen = null!;
        private double _now;

        [SetUp]
        public void SetUp()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            _set = set!;
            _config = HudConfig.From(_set);
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out error), Is.True, error);
            var settings = PredictionSettings.From(_set);
            var catalog = ActionCatalog.From(_set);
            _feed = new WorldClientFeed(_set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                null, null, settings.TickMs) { CharacterKey = "r1c42" };
            _view = new HudView(_config);
            _models = new HudViewModels(4, 4, 3, 20);
            _expBar = new ExpBarPresenter(_config, _view, _models.Progress);
            _expBar.Bind();
            _model = new TutorialPromptViewModel();
            _seen = new MemoryTutorialSeenStore();
            _binder = new TutorialBinder(_set, _view, _model, _ => _seen, readDismissKey: false);
            _presenter = new TutorialPromptPresenter(_config, _view, _model, _binder);
            _presenter.Bind();
            _binder.Bind(_feed);
            _now = 1_000;
        }

        [TearDown]
        public void TearDown()
        {
            _binder.Dispose();
            _presenter.Dispose();
            _expBar.Dispose();
        }

        private List<UnlockSchedule> Schedule() =>
            _set.TryGet(typeof(UnlockSchedule), out var table) ? table.Rows.Cast<UnlockSchedule>().ToList() : new List<UnlockSchedule>();

        private void Level(int level) =>
            _feed.RaiseProgression(new ProgressionState { Level = level, Exp = 0, ExpToNext = 100, Rank = Rank.F, BandPercent = 100 });

        private void Frame(double advanceMs = 16)
        {
            _now += advanceMs;
            _binder.Frame(_now);
        }

        private string Text(string id) => _config.Text.Get(id, _binder.KeyArgs);

        // ---- the schedule (engine-free core)

        [Test]
        public void Prompts_come_one_at_a_time_in_level_order_and_never_twice()
        {
            var tutorial = ClientTutorial.From(_set, _seen);
            tutorial.SetLevel(3);

            tutorial.Frame(0, false, false, false);
            Assert.That(tutorial.Current!.Id, Is.EqualTo("move"));
            tutorial.Dismiss();
            tutorial.Frame(1, false, false, false);
            Assert.That(tutorial.Current!.Id, Is.EqualTo("dodge")); // level 2 before level 3
            tutorial.Dismiss();
            tutorial.Frame(2, false, false, false);
            Assert.That(tutorial.Current!.Id, Is.EqualTo("parry"));
            tutorial.Dismiss();
            tutorial.Frame(3, false, false, false);
            Assert.That(tutorial.Current, Is.Null);

            // The same character later (a relog): the store remembers every prompt shown.
            var again = ClientTutorial.From(_set, _seen);
            again.SetLevel(3);
            again.Frame(10, false, false, false);
            Assert.That(again.Current, Is.Null);
            Assert.That(_seen.IsSeen("move") && _seen.IsSeen("dodge") && _seen.IsSeen("parry"), Is.True);
        }

        [Test]
        public void Triggered_prompts_wait_for_their_level_and_their_trigger()
        {
            var tutorial = ClientTutorial.From(_set, _seen);
            foreach (var row in Schedule().Where(r => r.Trigger == UnlockTrigger.LevelReached))
                _seen.MarkSeen(row.Id); // only the triggered rows are left
            tutorial.SetLevel(4);
            tutorial.Frame(0, lootInReach: false, outpostInReach: true, vendorInReach: true);
            Assert.That(tutorial.Current, Is.Null, "selling and buying are level 5 prompts");
            tutorial.OnItemSold();
            tutorial.Frame(1, false, false, false);
            Assert.That(tutorial.Current, Is.Null, "a sale before level 5 does not count");

            tutorial.Frame(2, lootInReach: true, false, false);
            Assert.That(tutorial.Current!.Id, Is.EqualTo("loot"));
            tutorial.Dismiss();

            tutorial.SetLevel(5);
            tutorial.Frame(3, false, outpostInReach: true, false);
            Assert.That(tutorial.Current!.Id, Is.EqualTo("sell"));
            tutorial.OnItemSold(); // queued while the sell prompt shows
            tutorial.Frame(4, false, false, false);
            tutorial.Dismiss();
            tutorial.Frame(5, false, false, false);
            Assert.That(tutorial.Current!.Id, Is.EqualTo("board"));
        }

        [Test]
        public void A_prompt_ends_after_its_time_and_the_next_follows()
        {
            int showMs = _set.Single<TutorialSettings>().PromptShowMs;
            var tutorial = ClientTutorial.From(_set, _seen);
            tutorial.SetLevel(2);
            tutorial.Frame(0, false, false, false);
            Assert.That(tutorial.Current!.Id, Is.EqualTo("move"));

            tutorial.Frame(showMs - 1, false, false, false);
            Assert.That(tutorial.Current!.Id, Is.EqualTo("move"));
            tutorial.Frame(showMs, false, false, false);
            Assert.That(tutorial.Current!.Id, Is.EqualTo("dodge"));
        }

        // ---- the prompt panel

        [Test]
        public void The_panel_shows_the_scheduled_prompt_with_its_keys_and_closes_with_its_button()
        {
            Level(1);
            Frame();

            Assert.That(_model.PromptId, Is.EqualTo("tutorial.prompt.move"));
            Assert.That(_presenter.Text, Is.EqualTo(Text("tutorial.prompt.move")));
            Assert.That(Text("tutorial.prompt.skills"), Does.Contain("Press K"), "{hotkey.toggleSkillTree} is the K of Hotkeys");

            Assert.That(_presenter.CloseButton.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            _binder.Dismiss(); // what the close button and the dismiss key call
            Frame();
            Assert.That(_presenter.Text, Is.Empty);
            Assert.That(_seen.IsSeen("move"), Is.True);
        }

        [Test]
        public void The_gate_refusal_takes_the_panel_over_and_the_prompt_returns_after_it()
        {
            Level(1);
            Frame();
            Assert.That(_model.PromptId, Is.EqualTo("tutorial.prompt.move"));

            _model.Set(_model.PromptId, 10, _model.DismissKey); // what the binder sets while the predictor reports a gate stop

            Assert.That(_presenter.Text, Is.EqualTo(_config.Reasons.Text(nameof(ClientReasonCode.RegionLevelTooLow), new TextArg("level", "10"))));
            Assert.That(_presenter.Text, Is.EqualTo("Reach level 10 to leave the tutorial."));
            _binder.Dismiss();
            Assert.That(_model.ShowsGate, Is.True, "the gate's refusal cannot be dismissed");

            _model.Set(_model.PromptId, 0, _model.DismissKey);
            Assert.That(_presenter.Text, Is.EqualTo(Text("tutorial.prompt.move")));
        }

        [Test]
        public void Seen_prompts_survive_in_the_characters_file()
        {
            string directory = Path.Combine(Application.temporaryCachePath, "tutorial-test-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var store = new TutorialSeenStore(directory, "r1c42");
                Assert.That(store.IsSeen("move"), Is.False);
                store.MarkSeen("move");
                store.MarkSeen("move");

                var reopened = new TutorialSeenStore(directory, "r1c42");
                Assert.That(reopened.IsSeen("move"), Is.True);
                Assert.That(File.ReadAllLines(reopened.FilePath), Is.EqualTo(new[] { "move" }));
                Assert.That(new TutorialSeenStore(directory, "r1c43").IsSeen("move"), Is.False, "another character");
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }

        // ---- the HUD by level

        [Test]
        public void The_exp_bar_is_hidden_below_level_10_and_appears_at_10()
        {
            Assert.That(_config.Unlocks.LevelOf(HudElementIds.ExpBar), Is.EqualTo(10));
            Assert.That(_config.Unlocks.LevelOf(HudElementIds.ResourcePanel), Is.EqualTo(0), "not in the schedule: always shown");

            Level(9);
            Assert.That(_view.IsLocked(HudElementIds.ExpBar), Is.True);
            Assert.That(_view.IsLocked(HudElementIds.ExpNote), Is.True);
            Assert.That(_view.IsLocked(HudElementIds.TutorialPrompt), Is.False);
            Assert.That(_view.Root.Q(HudElementIds.ExpBar).style.visibility.value, Is.EqualTo(Visibility.Hidden),
                "the lock is the visibility; the presenter keeps its own display rule");

            Level(10);
            Assert.That(_view.IsLocked(HudElementIds.ExpBar), Is.False);
        }
    }
}
