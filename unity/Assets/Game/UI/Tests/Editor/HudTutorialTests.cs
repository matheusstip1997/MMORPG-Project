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

        // ---- the M3 rows (spec 12.3 R-12.3-02, WP-M3-11)

        private void SeeAllBut(ITutorialSeenStore store, params string[] ids)
        {
            foreach (var row in Schedule().Where(r => !ids.Contains(r.Id)))
                store.MarkSeen(row.Id);
        }

        [Test]
        public void The_jobs_prompt_waits_for_the_job_state_and_never_shows_to_a_job_holder()
        {
            SeeAllBut(_seen, "jobs");
            var tutorial = ClientTutorial.From(_set, _seen);
            tutorial.SetLevel(12);

            tutorial.Frame(0, false, false, false);
            Assert.That(tutorial.Current, Is.Null, "no job state yet: a returning job holder must not get a flash of it");

            tutorial.SetJobs(known: true, held: true);
            tutorial.Frame(1, false, false, false);
            Assert.That(tutorial.Current, Is.Null, "user decision 2026-10-30: not shown to a character holding a job");
            Assert.That(_seen.IsSeen("jobs"), Is.False);

            var jobless = ClientTutorial.From(_set, _seen);
            jobless.SetLevel(11);
            jobless.SetJobs(known: true, held: false);
            jobless.Frame(2, false, false, false);
            Assert.That(jobless.Current, Is.Null, "below its level");
            jobless.SetLevel(12);
            jobless.Frame(3, false, false, false);
            Assert.That(jobless.Current!.Id, Is.EqualTo("jobs"));
        }

        [Test]
        public void The_jobs_prompt_closes_when_the_pick_commits_and_leaves_the_queue_too()
        {
            SeeAllBut(_seen, "jobs", "leave");
            var queued = ClientTutorial.From(_set, _seen);
            queued.SetLevel(12);
            queued.SetJobs(true, false);
            queued.Frame(0, false, false, false);
            Assert.That(queued.Current!.Id, Is.EqualTo("leave"), "level 10 first, the jobs prompt queued behind it");
            queued.SetJobs(true, true);
            queued.Dismiss();
            queued.Frame(1, false, false, false);
            Assert.That(queued.Current, Is.Null, "the queued prompt is gone");

            var store = new MemoryTutorialSeenStore();
            SeeAllBut(store, "jobs");
            var up = ClientTutorial.From(_set, store);
            up.SetLevel(12);
            up.SetJobs(true, false);
            up.Frame(0, false, false, false);
            Assert.That(up.Current!.Id, Is.EqualTo("jobs"));
            up.SetJobs(true, true);
            Assert.That(up.Frame(1, false, false, false), Is.True, "the prompt closes");
            Assert.That(up.Current, Is.Null);
        }

        [Test]
        public void The_gathering_prompt_waits_for_its_level_and_a_node_in_reach()
        {
            SeeAllBut(_seen, "gather");
            var tutorial = ClientTutorial.From(_set, _seen);
            tutorial.SetJobs(true, true);
            tutorial.SetLevel(11);
            tutorial.Frame(0, false, false, false, nodeInReach: true);
            Assert.That(tutorial.Current, Is.Null, "below its level");

            tutorial.SetLevel(12);
            tutorial.Frame(1, false, false, false);
            Assert.That(tutorial.Current, Is.Null, "no node in reach");
            tutorial.Frame(2, false, false, false, nodeInReach: true);
            Assert.That(tutorial.Current!.Id, Is.EqualTo("gather"));
        }

        [Test]
        public void The_crafting_prompt_waits_for_a_station_and_a_job_and_stays_unseen_before_them()
        {
            SeeAllBut(_seen, "craft");
            var tutorial = ClientTutorial.From(_set, _seen);
            tutorial.SetLevel(Schedule().Single(r => r.Id == "craft").Level);
            tutorial.SetJobs(true, false);

            tutorial.Frame(0, false, false, false, stationInReach: true);
            Assert.That(tutorial.Current, Is.Null, "a station is no use without a job");
            Assert.That(_seen.IsSeen("craft"), Is.False, "so the prompt is not burned");

            tutorial.SetJobs(true, true);
            tutorial.Frame(1, false, false, false);
            Assert.That(tutorial.Current, Is.Null, "no station in reach");
            tutorial.Frame(2, false, false, false, stationInReach: true);
            Assert.That(tutorial.Current!.Id, Is.EqualTo("craft"));
        }

        [Test]
        public void The_crafting_prompt_never_shows_over_an_open_station_window_and_stays_unseen_for_the_next_visit()
        {
            // #156 review: queued behind earlier prompts, it came up after the player had opened the window.
            SeeAllBut(_seen, "gather", "craft");
            var tutorial = ClientTutorial.From(_set, _seen);
            tutorial.SetLevel(Schedule().Single(r => r.Id == "craft").Level);
            tutorial.SetJobs(true, true);

            tutorial.Frame(0, false, false, false, nodeInReach: true, stationInReach: true); // gather shows, craft waits behind it
            Assert.That(tutorial.Current!.Id, Is.EqualTo("gather"));
            tutorial.Dismiss();
            tutorial.Frame(1, false, false, false, stationInReach: true, stationWindowOpen: true);
            Assert.That(tutorial.Current, Is.Null, "the window is open: the prompt to open it left the queue");
            Assert.That(_seen.IsSeen("craft"), Is.False, "not burned");

            tutorial.Frame(2, false, false, false, stationInReach: true);
            Assert.That(tutorial.Current!.Id, Is.EqualTo("craft"), "the window closed, the station still in reach");
            tutorial.Frame(3, false, false, false, stationInReach: true, stationWindowOpen: true);
            Assert.That(tutorial.Current, Is.Null, "opening the window ends the prompt on screen");
        }

        [Test]
        public void The_binder_gives_the_schedule_the_job_state_and_the_kind_of_the_station_in_reach()
        {
            SeeAllBut(_seen, "jobs", "craft");
            Level(12);
            Frame();
            Assert.That(_model.PromptId, Is.Empty, "no JobState yet");

            _feed.RaiseJobs(new JobState { Version = 1, Jobs = new JobEntry[0] });
            Frame();
            Assert.That(_model.PromptId, Is.EqualTo("tutorial.prompt.jobs"));

            _feed.RaiseJobs(new JobState { Version = 2, Jobs = new[] { new JobEntry { JobNetId = 1, Slot = 0, Level = 1 } } });
            Frame();
            Assert.That(_model.PromptId, Is.Empty, "the pick committed: the prompt closes");

            Assert.That(_set.TryGet(typeof(NpcDef), out var npcs), Is.True);
            _feed.SetNpcTarget(1, (ushort)npcs.Rows.Cast<NpcDef>().First(n => n.Kind == NpcKind.JewelerBench).NetId);
            Frame();
            Assert.That(_model.PromptId, Is.EqualTo("tutorial.prompt.craft"));
            Assert.That(_presenter.Text, Does.Contain("Press T"), "{hotkey.interact} is the T of Hotkeys");
        }

        [Test]
        public void The_first_mailbox_in_reach_shows_the_mail_prompt()
        {
            // WP-M4-14 (R-4.5-03): read anywhere with M, taken at a mailbox. Level 1 is a placeholder (TODO(open) decision 14).
            SeeAllBut(_seen, "mail");
            Level(1);
            Frame();
            Assert.That(_model.PromptId, Is.Empty, "no mailbox in reach yet");

            Assert.That(_set.TryGet(typeof(NpcDef), out var npcs), Is.True);
            _feed.SetNpcTarget(1, (ushort)npcs.Rows.Cast<NpcDef>().Single(n => n.Kind == NpcKind.Mailbox).NetId);
            Frame();
            Assert.That(_model.PromptId, Is.EqualTo("tutorial.prompt.mail"));
            Assert.That(_presenter.Text, Does.Contain("with M").And.Contain("press T"), "{hotkey.toggleMail} and {hotkey.interact}");
        }

        [Test]
        public void The_binder_gives_the_schedule_a_node_of_an_own_job_when_no_npc_keeps_the_key()
        {
            SeeAllBut(_seen, "gather");
            Level(12);
            _feed.RaiseJobs(new JobState { Version = 1, Jobs = new[] { new JobEntry { JobNetId = 1, Slot = 0, Level = 1 } } });
            _feed.SetNodeTarget(7, 1);
            _feed.SetNpcTarget(1, 1);
            Frame();
            Assert.That(_model.PromptId, Is.Empty, "the NPC in reach keeps the key");

            _feed.SetNpcTarget(0, 0);
            Frame();
            Assert.That(_model.PromptId, Is.EqualTo("tutorial.prompt.gather"));
            Assert.That(_presenter.Text, Is.EqualTo(Text("tutorial.prompt.gather")));
            Assert.That(_presenter.Text, Does.Contain("press T"), "{hotkey.interact} is the T of Hotkeys");
        }

        [Test]
        public void The_binder_reads_jobs_that_came_before_the_first_level()
        {
            SeeAllBut(_seen, "jobs");
            _feed.RaiseJobs(new JobState { Version = 1, Jobs = new JobEntry[0] });
            Level(12);
            Frame();
            Assert.That(_model.PromptId, Is.EqualTo("tutorial.prompt.jobs"));
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

        [Test]
        public void A_qa_smoke_run_keeps_its_seen_prompts_in_its_run_folder_and_a_player_in_persistent_data()
        {
            // QA M2 wave 2 D4: smoke runs on their own databases reuse character ids, so r1c2 of one run must not hide the
            // prompts of r1c2 in the next; a player's file stays where it was.
            string root = Path.Combine(Application.temporaryCachePath, "tutorial-qa-test-" + System.Guid.NewGuid().ToString("N"));
            string runA = Path.Combine(root, "run-a"), runB = Path.Combine(root, "run-b"), persistent = Path.Combine(root, "persistent");
            try
            {
                Assert.That(TutorialSeenStore.DirectoryFor(null, persistent), Is.EqualTo(Path.Combine(persistent, "tutorial")));
                Assert.That(TutorialSeenStore.DirectoryFor("  ", persistent), Is.EqualTo(Path.Combine(persistent, "tutorial")));
                Assert.That(TutorialSeenStore.DirectoryFor(runA, persistent), Is.EqualTo(Path.Combine(runA, "tutorial")));

                new TutorialSeenStore(TutorialSeenStore.DirectoryFor(runA, persistent), "r1c2").MarkSeen("move");
                Assert.That(new TutorialSeenStore(TutorialSeenStore.DirectoryFor(runB, persistent), "r1c2").IsSeen("move"), Is.False,
                    "the same character id in another run's databases");
                Assert.That(new TutorialSeenStore(TutorialSeenStore.DirectoryFor(null, persistent), "r1c2").IsSeen("move"), Is.False,
                    "a player's own file");
                Assert.That(new TutorialSeenStore(TutorialSeenStore.DirectoryFor(runA, persistent), "r1c2").IsSeen("move"), Is.True);

                string? before = System.Environment.GetEnvironmentVariable(TutorialSeenStore.QaRunDirectoryVariable);
                try
                {
                    System.Environment.SetEnvironmentVariable(TutorialSeenStore.QaRunDirectoryVariable, runA);
                    Assert.That(TutorialSeenStore.For("r1c2").IsSeen("move"), Is.True, "For reads the smoke run's folder");
                }
                finally
                {
                    System.Environment.SetEnvironmentVariable(TutorialSeenStore.QaRunDirectoryVariable, before);
                }
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        // ---- the HUD by level

        [Test]
        public void The_committed_schedule_shows_the_exp_bar_and_minimap_from_level_1()
        {
            // User decision 2026-10-28 after play: no HUD element waits for a level in M2.
            Assert.That(_config.Unlocks.LevelOf(HudElementIds.ExpBar), Is.EqualTo(0));
            Assert.That(_config.Unlocks.LevelOf(HudElementIds.Minimap), Is.EqualTo(0));

            Level(1);
            Assert.That(_view.IsLocked(HudElementIds.ExpBar), Is.False);
        }

        [Test]
        public void An_element_a_row_lists_is_hidden_below_its_level_and_appears_at_it()
        {
            // The hook with a schedule of its own: the committed one gates nothing in M2.
            _config.Unlocks = new HudUnlocks(new[]
            {
                new UnlockSchedule { Id = "t", Level = 10, HudElements = new[] { HudElementIds.ExpBar, HudElementIds.ExpNote } },
            });
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
