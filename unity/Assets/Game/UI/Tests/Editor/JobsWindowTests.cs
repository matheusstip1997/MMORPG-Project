#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Jobs;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Jobs;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Jobs;

namespace Game.UI.Tests
{
    /// <summary>
    /// The Jobs window (spec 5.1, 9.13; WP-M3-1): the view model's held jobs and pick rules (the server's own
    /// <see cref="JobRules.CheckPick"/>), the confirmation step and the one pending pick; the binder's open at the job master,
    /// request, refusal and news toasts; the presenter's slot lines, pick rows and confirmation; and the committed texts and key.
    /// </summary>
    public sealed class JobsWindowTests
    {
        private const byte Blacksmith = 1, Jeweler = 2;
        private const uint Master = 77;

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static JobEntry Entry(byte job, JobSlot slot, ushort level = 1, uint xp = 0, uint next = 100, ushort points = 0) =>
            new JobEntry { JobNetId = job, Slot = slot, Level = level, Xp = xp, XpToNext = next, Points = points };

        private static JobState State(uint version, params JobEntry[] jobs) => new JobState { Version = version, Jobs = jobs };

        private static JobDef Job(JobsWindowViewModel model, byte netId) => model.Jobs.Single(j => j.NetId == netId);

        // ---- view model

        [Test]
        public void At_the_job_master_the_main_comes_first_from_the_jobs_level_and_a_pick_asks_for_confirmation()
        {
            var model = new JobsWindowViewModel(JobsConfig.From(Tables()).Rules);
            model.SetState(State(1));
            model.SetLevel(11);
            model.OpenAt(Master);

            Assert.That(model.Check(Job(model, Blacksmith), JobSlot.Main), Is.EqualTo(JobPickCheck.LevelTooLow));
            model.SetLevel(12);
            Assert.That(model.CanPick(Job(model, Blacksmith), JobSlot.Main), Is.True);
            Assert.That(model.Check(Job(model, Jeweler), JobSlot.Secondary), Is.EqualTo(JobPickCheck.MainFirst));

            Assert.That(model.AskConfirm(Job(model, Blacksmith), JobSlot.Main), Is.True);
            Assert.That(model.Confirming, Is.EqualTo(((byte, JobSlot)?)(Blacksmith, JobSlot.Main)));
            Assert.That(model.CanPick(Job(model, Jeweler), JobSlot.Main), Is.False); // one at a time
            model.CancelConfirm();
            Assert.That(model.Confirming, Is.Null);
        }

        [Test]
        public void A_held_main_job_is_final_and_the_secondary_must_differ()
        {
            var model = new JobsWindowViewModel(JobsConfig.From(Tables()).Rules);
            model.SetLevel(30);
            model.SetState(State(1, Entry(Blacksmith, JobSlot.Main)));
            model.OpenAt(Master);

            Assert.That(model.Check(Job(model, Jeweler), JobSlot.Main), Is.EqualTo(JobPickCheck.AlreadyChosen));
            Assert.That(model.Check(Job(model, Blacksmith), JobSlot.Secondary), Is.EqualTo(JobPickCheck.SameAsMain));
            Assert.That(model.CanPick(Job(model, Jeweler), JobSlot.Secondary), Is.True);
        }

        [Test]
        public void Opened_by_the_key_the_window_offers_no_pick_and_leaving_the_master_drops_the_section()
        {
            var model = new JobsWindowViewModel(JobsConfig.From(Tables()).Rules);
            model.SetLevel(30);
            model.SetState(State(1));
            model.Toggle();
            Assert.That((model.Open, model.AtJobMaster, model.CanPick(Job(model, Blacksmith), JobSlot.Main)), Is.EqualTo((true, false, false)));

            model.OpenAt(Master);
            model.AskConfirm(Job(model, Blacksmith), JobSlot.Main);
            model.LeaveJobMaster();
            Assert.That((model.Open, model.AtJobMaster, model.Confirming), Is.EqualTo((true, false, ((byte, JobSlot)?)null)));
        }

        [Test]
        public void Job_level_ups_are_news_but_the_first_state_is_not()
        {
            var model = new JobsWindowViewModel(JobsConfig.From(Tables()).Rules);
            Assert.That(model.SetState(State(1, Entry(Blacksmith, JobSlot.Main))), Is.Empty);
            var news = model.SetState(State(2, Entry(Blacksmith, JobSlot.Main, level: 3), Entry(Jeweler, JobSlot.Secondary)));
            Assert.That(news.Select(n => (n.JobNetId, n.Level)), Is.EqualTo(new[] { (Blacksmith, 3) }));
        }

        // ---- binder

        private sealed class FakeJobRequests : IJobRequests
        {
            public List<(uint Npc, byte Job, JobSlot Slot)> Sent { get; } = new List<(uint, byte, JobSlot)>();

            public uint RequestJob(uint npcEntityId, byte jobNetId, JobSlot slot)
            {
                Sent.Add((npcEntityId, jobNetId, slot));
                return (uint)Sent.Count;
            }

            public uint RequestJobTree(JobTreeOp op, byte jobNetId, string nodeId) => 0; // the tree's tests: JobTreeTabTests
        }

        private static WorldClientFeed Feed(ConfigSet set)
        {
            var settings = PredictionSettings.From(set);
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
            var catalog = ActionCatalog.From(set);
            return new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                null, null, settings.TickMs);
        }

        private sealed class Rig
        {
            public Rig()
            {
                var set = Tables();
                Hud = HudConfig.From(set);
                Config = JobsConfig.From(set);
                Model = new JobsWindowViewModel(Config.Rules);
                Feed = Feed(set);
                Feed.JobRequests = Requests;
                Binder = new JobsBinder(Config, Hud.Text, Model, Toast, 2_000, () => 0, readToggleKey: false);
                Binder.Bind(Feed);
                Feed.RaiseProgression(new ProgressionState { Level = 12 });
                Feed.RaiseJobs(State(1));
            }

            public HudConfig Hud { get; }
            public JobsConfig Config { get; }
            public WorldClientFeed Feed { get; }
            public FakeJobRequests Requests { get; } = new FakeJobRequests();
            public JobsWindowViewModel Model { get; }
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public JobsBinder Binder { get; }

            public int Row(byte netId) => Model.Jobs.ToList().FindIndex(j => j.NetId == netId);

            public void OpenAtMaster() =>
                Feed.RaiseOutpostInteracted(new OutpostInteractResult { RequestId = 1, NpcEntityId = Master, Kind = NpcInteraction.Jobs, Reason = OutpostReason.None });
        }

        [Test]
        public void The_job_masters_yes_opens_the_pick_and_a_confirmed_pick_sends_one_request()
        {
            var rig = new Rig();
            rig.OpenAtMaster();
            Assert.That((rig.Model.Open, rig.Model.NpcEntityId), Is.EqualTo((true, Master)));

            rig.Binder.Pick(rig.Row(Blacksmith), JobSlot.Main);
            Assert.That(rig.Requests.Sent, Is.Empty); // the confirmation step first
            rig.Binder.ConfirmPick();
            rig.Binder.ConfirmPick(); // nothing left to confirm
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { (Master, Blacksmith, JobSlot.Main) }));
            Assert.That(rig.Model.Pending, Is.EqualTo(1u));

            rig.Feed.RaiseJobAnswered(new JobResult { RequestId = 1, Slot = JobSlot.Main, JobNetId = Blacksmith, Reason = JobReason.None });
            Assert.That((rig.Toast.Visible, rig.Toast.TextId, rig.Toast.ArgValue), Is.EqualTo((true, JobsTextIds.ToastPicked, "Blacksmith")));
            Assert.That(rig.Model.Pending, Is.EqualTo(0u));
        }

        [Test]
        public void A_refusal_shows_its_reason_and_another_npcs_answer_does_not_open_the_window()
        {
            var rig = new Rig();
            rig.Feed.RaiseOutpostInteracted(new OutpostInteractResult { RequestId = 1, NpcEntityId = 5, Kind = NpcInteraction.Quests, Reason = OutpostReason.None });
            Assert.That(rig.Model.Open, Is.False);

            rig.OpenAtMaster();
            rig.Binder.Pick(rig.Row(Blacksmith), JobSlot.Main);
            rig.Binder.ConfirmPick();
            rig.Feed.RaiseJobAnswered(new JobResult { RequestId = 1, Slot = JobSlot.Main, JobNetId = Blacksmith, Reason = JobReason.InCombat });

            Assert.That((rig.Toast.Visible, rig.Toast.ReasonCode), Is.EqualTo((true, "InCombat")));
        }

        [Test]
        public void A_disconnect_clears_the_window()
        {
            var rig = new Rig();
            rig.OpenAtMaster();
            rig.Feed.RaiseDisconnected("Disconnected");
            Assert.That((rig.Model.Open, rig.Model.State, rig.Model.AtJobMaster), Is.EqualTo((false, (JobState?)null, false)));
        }

        // ---- presenter

        private static JobsWindowPresenter Presenter(out Rig rig)
        {
            rig = new Rig();
            var presenter = new JobsWindowPresenter(rig.Hud, new HudView(rig.Hud), rig.Config, rig.Model, rig.Binder);
            presenter.Bind();
            return presenter;
        }

        [Test]
        public void The_window_shows_the_slots_and_at_the_master_the_rows_with_their_reasons_and_the_confirmation()
        {
            var presenter = Presenter(out var rig);
            Assert.That(presenter.IsShown, Is.False);

            rig.Feed.RaiseJobs(State(2, Entry(Blacksmith, JobSlot.Main, level: 2, xp: 30, next: 150, points: 1)));
            rig.Model.Toggle();
            Assert.That(presenter.IsShown, Is.True);
            Assert.That(presenter.SlotText(JobSlot.Main), Is.EqualTo("Main job: Blacksmith   Job level 2/20   30/150 XP   Job points: 1"));
            Assert.That(presenter.SlotText(JobSlot.Secondary), Is.EqualTo("Secondary job: Not chosen yet"));
            Assert.That(presenter.PickShown, Is.False);

            rig.OpenAtMaster();
            Assert.That(presenter.PickShown, Is.True);
            int blacksmith = rig.Row(Blacksmith), jeweler = rig.Row(Jeweler);
            Assert.That(presenter.PickButton(blacksmith, JobSlot.Main).enabledSelf, Is.False);
            Assert.That(presenter.RowReason(blacksmith), Is.EqualTo("That is already your main job."));
            Assert.That(presenter.PickButton(jeweler, JobSlot.Secondary).enabledSelf, Is.True);
            Assert.That(presenter.RowReason(jeweler), Is.Empty);

            rig.Binder.Pick(jeweler, JobSlot.Secondary);
            Assert.That((presenter.ConfirmShown, presenter.PickShown), Is.EqualTo((true, false)));
            Assert.That(presenter.ConfirmText, Is.EqualTo("Pick Jeweler as your Secondary job? Jobs cannot be changed later."));
        }

        // ---- committed texts

        [Test]
        public void Committed_ui_strings_cover_every_jobs_text_id_and_reason_and_the_toggle_key_is_bound()
        {
            var set = Tables();
            var ui = new HashSet<string>(set.TryGet(typeof(UiStrings), out var table) ? table.Rows.Cast<UiStrings>().Select(r => r.Id) : new string[0]);
            var reasons = new HashSet<string>(set.TryGet(typeof(ReasonCodeStrings), out var codes) ? codes.Rows.Cast<ReasonCodeStrings>().Select(r => r.Code) : new string[0]);

            Assert.That(JobsTextIds.All.Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(System.Enum.GetNames(typeof(JobReason)).Where(n => n != "None" && !reasons.Contains(n)), Is.Empty);
            Assert.That(JobsConfig.From(set).Rules!.Jobs.Select(j => j.NameId).Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(JobsConfig.From(set).ToggleBinding, Is.EqualTo("<Keyboard>/o"));
            Assert.That(ui.Contains(OutpostTextIds.PromptOf(NpcKind.JobMaster)), Is.True);
        }
    }
}
