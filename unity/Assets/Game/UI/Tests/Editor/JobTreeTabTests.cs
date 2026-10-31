#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>
    /// The job tree tab of the Jobs window (spec 5.1, 9.13; WP-M3-7; <c>docs/design/job-trees.md</c> decision 8), on the
    /// committed tables: one tab per held job; node states from the server's own rules (row gates, prerequisites, max, points);
    /// Learn sends one request; Respec asks for confirmation first; the summary shows the capped bonuses; the answers toast;
    /// the presenter's nodes, gates, links, details and the window's layout row. Placeholders read: Prospector +4 % per level
    /// (cap 25), rows at job levels 1/5/10/15, Master Molds T2 and T3.
    /// </summary>
    public sealed class JobTreeTabTests
    {
        private const byte Blacksmith = 1, Jeweler = 2;
        private const string Prospector = "blacksmith.prospector";
        private const string ThemePath = "Assets/Game/UI/Login/LoginTheme.tss"; // the theme HudSceneInstaller gives the HUD

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static JobEntry Entry(byte job, JobSlot slot, ushort level, ushort points, params (string Node, byte Level)[] nodes) =>
            new JobEntry
            {
                JobNetId = job, Slot = slot, Level = level, Xp = 0, XpToNext = 100, Points = points,
                Nodes = nodes.Select(n => new JobNodeEntry { NodeId = n.Node, Level = n.Level }).ToArray(),
            };

        private static JobState State(uint version, params JobEntry[] jobs) => new JobState { Version = version, Jobs = jobs };

        // ---- view model and tree view

        [Test]
        public void Each_held_job_has_a_tab_and_its_nodes_follow_the_servers_rules()
        {
            var model = new JobsWindowViewModel(JobsConfig.From(Tables()).Rules);
            model.SetState(State(1, Entry(Blacksmith, JobSlot.Main, 1, 1)));
            model.SelectTab(Jeweler); // not held: no tab
            Assert.That(model.Tab, Is.EqualTo((byte)0));
            Assert.That(model.HeldJobs.Select(j => j.JobNetId), Is.EqualTo(new[] { Blacksmith }));

            model.SelectTab(Blacksmith);
            var tree = model.Tree!;
            Assert.That((tree.Nodes.Count, tree.RowCount, tree.RowGate(1), tree.RowGate(3)), Is.EqualTo((9, 4, 5, 15)));
            Assert.That(tree.StateOf(tree.Nodes.Single(n => n.Id == Prospector)), Is.EqualTo(JobNodeState.Open));
            Assert.That(tree.StateOf(tree.Nodes.Single(n => n.Id == "blacksmith.keenEye")), Is.EqualTo(JobNodeState.RowLocked));
            Assert.That(tree.ReasonCode("blacksmith.keenEye"), Is.EqualTo(nameof(JobReason.JobRowLocked)));
            Assert.That(tree.Links().Select(l => (l.From.Id, l.To.Id)), Does.Contain((Prospector, "blacksmith.keenEye")));

            model.SelectNode(Prospector);
            Assert.That(model.CanLearn, Is.True);
            model.SelectNode("jeweler.gemCutter"); // another job's node: ignored
            Assert.That(model.SelectedNode, Is.EqualTo(Prospector));
            Assert.That(model.CanRespec, Is.False, "nothing learned");
        }

        [Test]
        public void At_level_5_the_link_gates_keen_eye_until_prospector_is_learned()
        {
            var model = new JobsWindowViewModel(JobsConfig.From(Tables()).Rules);
            model.SetState(State(1, Entry(Blacksmith, JobSlot.Main, 5, 2)));
            model.SelectTab(Blacksmith);
            var keenEye = model.Tree!.Nodes.Single(n => n.Id == "blacksmith.keenEye");
            Assert.That(model.Tree!.StateOf(keenEye), Is.EqualTo(JobNodeState.Locked));

            model.SetState(State(2, Entry(Blacksmith, JobSlot.Main, 5, 1, (Prospector, 1))));
            Assert.That(model.Tree!.StateOf(keenEye), Is.EqualTo(JobNodeState.Open));
            Assert.That(model.Tree!.Check(keenEye.Id), Is.EqualTo(JobTreeCheck.Ok));
        }

        [Test]
        public void The_summary_shows_capped_bonuses_per_activity_and_the_mold_tiers()
        {
            var model = new JobsWindowViewModel(JobsConfig.From(Tables()).Rules);
            model.SetState(State(1, Entry(Blacksmith, JobSlot.Main, 20, 0, (Prospector, 5), ("blacksmith.masterMolds", 1)),
                Entry(Jeweler, JobSlot.Secondary, 5, 0, ("jeweler.gemCutter", 2), ("jeweler.fineSetting", 5))));
            model.SelectTab(Blacksmith);
            var lines = model.Tree!.Summary();
            Assert.That(lines.Select(l => (l.Kind, l.Value, l.Cap)), Does.Contain((JobBonusKind.YieldPercent, 20, 25)));
            var molds = lines.Single(l => l.Kind == JobBonusKind.UnlockMoldTiers);
            Assert.That((JobTreeMath.HasTier(molds.Tiers, 2), JobTreeMath.HasTier(molds.Tiers, 3)), Is.EqualTo((true, true)));

            model.SelectTab(Jeweler);
            var quality = model.Tree!.Summary().Where(l => l.Kind == JobBonusKind.QualityBonus).ToList();
            Assert.That(quality.Select(l => (l.Value, l.Activities.Single())), Is.EquivalentTo(new[] { (4, JobActivity.Refine), (10, JobActivity.Craft) }));
        }

        [Test]
        public void Losing_the_tabs_job_goes_back_to_the_list()
        {
            var model = new JobsWindowViewModel(JobsConfig.From(Tables()).Rules);
            model.SetState(State(1, Entry(Blacksmith, JobSlot.Main, 1, 1)));
            model.SelectTab(Blacksmith);
            model.SetState(State(2));
            Assert.That((model.Tab, model.Tree), Is.EqualTo(((byte)0, (JobTreeView?)null)));
        }

        // ---- binder

        private sealed class FakeRequests : IJobRequests
        {
            public List<(JobTreeOp Op, byte Job, string Node)> Sent { get; } = new List<(JobTreeOp, byte, string)>();

            public uint RequestJob(uint npcEntityId, byte jobNetId, JobSlot slot) => 0;

            public uint RequestJobTree(JobTreeOp op, byte jobNetId, string nodeId)
            {
                Sent.Add((op, jobNetId, nodeId));
                return (uint)Sent.Count;
            }
        }

        private sealed class Rig
        {
            public Rig()
            {
                var set = Tables();
                Hud = HudConfig.From(set);
                Config = JobsConfig.From(set);
                Model = new JobsWindowViewModel(Config.Rules);
                var settings = PredictionSettings.From(set);
                Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
                var catalog = ActionCatalog.From(set);
                Feed = new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                    null, null, settings.TickMs) { JobRequests = Requests };
                Binder = new JobsBinder(Config, Hud.Text, Model, Toast, 2_000, () => 0, readToggleKey: false);
                Binder.Bind(Feed);
                Feed.RaiseProgression(new ProgressionState { Level = 12 });
                Feed.RaiseJobs(State(1, Entry(Blacksmith, JobSlot.Main, 1, 1)));
                Model.Toggle();
                Binder.SelectTab(Blacksmith);
            }

            public HudConfig Hud { get; }
            public JobsConfig Config { get; }
            public WorldClientFeed Feed { get; }
            public FakeRequests Requests { get; } = new FakeRequests();
            public JobsWindowViewModel Model { get; }
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public JobsBinder Binder { get; }
        }

        [Test]
        public void Learn_sends_one_request_and_its_answer_toasts_the_node()
        {
            var rig = new Rig();
            rig.Binder.SelectNode(Prospector);
            rig.Binder.Learn();
            rig.Binder.Learn(); // one at a time
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { (JobTreeOp.Learn, Blacksmith, Prospector) }));
            Assert.That(rig.Model.TreePending, Is.EqualTo(1u));

            rig.Feed.RaiseJobTreeAnswered(new JobTreeResult { RequestId = 1, Op = JobTreeOp.Learn, JobNetId = Blacksmith, NodeId = Prospector, Level = 1 });
            Assert.That((rig.Toast.Visible, rig.Toast.TextId, rig.Toast.ArgValue), Is.EqualTo((true, JobsTextIds.ToastLearned, "Prospector")));
            Assert.That(rig.Model.TreePending, Is.EqualTo(0u));
        }

        [Test]
        public void Respec_asks_for_confirmation_first_and_a_refusal_shows_its_reason()
        {
            var rig = new Rig();
            rig.Feed.RaiseJobs(State(2, Entry(Blacksmith, JobSlot.Main, 2, 1, (Prospector, 1))));
            rig.Binder.Respec();
            Assert.That((rig.Model.ConfirmingRespec, rig.Requests.Sent.Count), Is.EqualTo((true, 0)));
            rig.Binder.CancelRespec();
            rig.Binder.ConfirmRespec(); // nothing to confirm
            Assert.That(rig.Requests.Sent, Is.Empty);

            rig.Binder.Respec();
            rig.Binder.ConfirmRespec();
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { (JobTreeOp.Respec, Blacksmith, "") }));
            rig.Feed.RaiseJobTreeAnswered(new JobTreeResult { RequestId = 1, Op = JobTreeOp.Respec, JobNetId = Blacksmith, Reason = JobReason.InCombat });
            Assert.That((rig.Toast.Visible, rig.Toast.ReasonCode), Is.EqualTo((true, "InCombat")));

            rig.Binder.Respec();
            rig.Binder.ConfirmRespec();
            rig.Feed.RaiseJobTreeAnswered(new JobTreeResult { RequestId = 2, Op = JobTreeOp.Respec, JobNetId = Blacksmith, Points = 2 });
            Assert.That((rig.Toast.TextId, rig.Toast.ArgValue), Is.EqualTo((JobsTextIds.ToastRespecced, "Blacksmith")));
        }

        // ---- presenter

        private static JobsWindowPresenter Presenter(Rig rig)
        {
            var presenter = new JobsWindowPresenter(rig.Hud, new HudView(rig.Hud), rig.Config, rig.Model, rig.Binder);
            presenter.Bind();
            return presenter;
        }

        [Test]
        public void The_tab_shows_the_nodes_gates_details_points_and_the_respec_confirmation()
        {
            var rig = new Rig();
            var presenter = Presenter(rig);
            Assert.That((presenter.TreeShown, presenter.PickShown), Is.EqualTo((true, false)));
            Assert.That(presenter.TabButton(Blacksmith)!.text, Is.EqualTo("Blacksmith tree"));
            Assert.That(presenter.NodeText(Prospector), Is.EqualTo("Prospector\n0/5   Cost 1"));
            Assert.That(presenter.RowGateText(2), Is.EqualTo("Job level 10"));
            Assert.That(presenter.LinkCount, Is.EqualTo(3));
            Assert.That(presenter.TreePointsText, Is.EqualTo("Job points: 1"));
            Assert.That(presenter.LearnButton.enabledSelf, Is.False, "nothing selected");
            Assert.That(presenter.SummaryText, Is.EqualTo("No bonuses yet"));

            rig.Binder.SelectNode(Prospector);
            Assert.That(presenter.LearnButton.enabledSelf, Is.True);
            Assert.That(presenter.DetailsText, Does.Contain("A bigger harvest yield when mining."));
            Assert.That(presenter.DetailsText, Does.Contain("Next level: Harvest yield +4 %"));

            rig.Feed.RaiseJobs(State(2, Entry(Blacksmith, JobSlot.Main, 1, 0, (Prospector, 1))));
            Assert.That(presenter.NodeText(Prospector), Is.EqualTo("Prospector\n1/5   Cost 1"));
            Assert.That(presenter.DetailsText, Does.Contain("Now: Harvest yield +4 %"));
            Assert.That(presenter.DetailsText, Does.Contain("Not enough job points."));
            Assert.That(presenter.SummaryText, Is.EqualTo("Harvest yield +4 % (harvest; cap 25)"));
            Assert.That(presenter.RespecButton.enabledSelf, Is.True);

            rig.Binder.Respec();
            Assert.That((presenter.RespecConfirmShown, presenter.RespecConfirmTitle), Is.EqualTo((true, "Reset the Blacksmith tree?")));
            Assert.That(presenter.RespecButton.enabledSelf, Is.False);

            rig.Binder.SelectTab(0);
            Assert.That((presenter.TreeShown, presenter.SlotText(JobSlot.Main).StartsWith("Main job: Blacksmith")), Is.EqualTo((false, true)));
        }

        // ---- committed texts

        [Test]
        public void Committed_ui_strings_cover_every_node_bonus_kind_and_activity()
        {
            var set = Tables();
            var ui = new HashSet<string>(set.TryGet(typeof(UiStrings), out var table) ? table.Rows.Cast<UiStrings>().Select(r => r.Id) : new string[0]);
            var tree = JobsConfig.From(set).Rules!.Tree!;
            foreach (var job in JobsConfig.From(set).Rules!.Jobs)
            {
                foreach (var node in tree.Nodes(job.Id))
                    Assert.That(ui.Contains(node.NameId) && ui.Contains(node.TextId), Is.True, node.Id);
            }
            Assert.That(System.Enum.GetValues(typeof(JobBonusKind)).Cast<JobBonusKind>().Where(k => k != JobBonusKind.None)
                .Select(JobsTextIds.Bonus).Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(System.Enum.GetValues(typeof(JobActivity)).Cast<JobActivity>().Where(a => a != JobActivity.None && a != JobActivity.ItemUse)
                .Select(JobsTextIds.Activity).Where(id => !ui.Contains(id)), Is.Empty);
        }

        // ---- layout

        [UnityTest]
        public System.Collections.IEnumerator The_window_fits_its_layout_row_and_the_tree_sits_inside_it()
        {
            var rig = new Rig();
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            settings.scaleMode = PanelScaleMode.ConstantPixelSize;
            var target = new RenderTexture(1280, 720, 24);
            settings.targetTexture = target;
            var host = new GameObject("jobs-tree-layout-test");
            JobsWindowPresenter? presenter = null;
            try
            {
                var document = host.AddComponent<UIDocument>();
                document.panelSettings = settings;
                yield return null;
                var root = document.rootVisualElement;
                Assert.That(root, Is.Not.Null, "the UIDocument made no panel");
                root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(Game.UI.Editor.HudSceneInstaller.StylesPath));
                root.style.unityFontDefinition = FontDefinition.FromFont(AssetDatabase.LoadAssetAtPath<Font>(Game.UI.Editor.HudSceneInstaller.FontPath));
                root.style.fontSize = rig.Hud.Theme.Tokens.FontSizeBody;
                var view = new HudView(rig.Hud);
                root.Add(view.Root);
                presenter = new JobsWindowPresenter(rig.Hud, view, rig.Config, rig.Model, rig.Binder);
                presenter.Bind();
                rig.Feed.RaiseJobs(State(2, Entry(Blacksmith, JobSlot.Main, 20, 3, (Prospector, 2), ("blacksmith.masterMolds", 1))));
                rig.Binder.SelectNode(Prospector);
                Layout(root);
                Layout(root); // the grid's size is known after the first pass: the nodes are placed on the second

                var window = presenter.Window;
                Assert.That(rig.Hud.TryGetLayout(HudElementIds.JobsWindow, out var row), Is.True);
                Assert.That((window.layout.width, window.layout.height), Is.EqualTo(((float)row.Size[0], (float)row.Size[1])),
                    "the window keeps the size of its HudLayout row");
                var inner = Inner(window);
                foreach (var node in rig.Model.Tree!.Nodes)
                {
                    var button = presenter.NodeButton(node.Id)!;
                    AssertInside(button.worldBound, inner, node.Id);
                    Assert.That(button.resolvedStyle.fontSize, Is.GreaterThanOrEqualTo((float)rig.Hud.Theme.Tokens.FontSizeBody), node.Id);
                }
                AssertInside(presenter.LearnButton.worldBound, inner, "Learn");
                AssertInside(presenter.RespecButton.worldBound, inner, "Respec");
                // Row 0 above row 1, and a row's nodes left to right: Prospector (col 1) left of Firm Hammer (col 3).
                var prospector = presenter.NodeButton(Prospector)!.worldBound;
                var hammer = presenter.NodeButton("blacksmith.firmHammer")!.worldBound;
                var keenEye = presenter.NodeButton("blacksmith.keenEye")!.worldBound;
                Assert.That(prospector.y, Is.EqualTo(hammer.y).Within(0.5f));
                Assert.That(prospector.xMax, Is.LessThanOrEqualTo(hammer.xMin + 0.5f));
                Assert.That(prospector.yMax, Is.LessThanOrEqualTo(keenEye.yMin + 0.5f));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                presenter?.Dispose();
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(settings);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        private static void Layout(VisualElement element)
        {
            var update = element.panel.GetType().GetMethod("UpdateWithoutRepaint",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, System.Type.EmptyTypes, null);
            Assert.That(update, Is.Not.Null, "UI Toolkit's panel has no UpdateWithoutRepaint");
            update!.Invoke(element.panel, null);
        }

        private static Rect Inner(VisualElement element)
        {
            var bounds = element.worldBound;
            var style = element.resolvedStyle;
            return Rect.MinMaxRect(bounds.xMin + style.borderLeftWidth, bounds.yMin + style.borderTopWidth,
                bounds.xMax - style.borderRightWidth, bounds.yMax - style.borderBottomWidth);
        }

        private static void AssertInside(Rect rect, Rect box, string what)
        {
            const float Tolerance = 0.5f; // layout rounding
            Assert.That(rect.xMin >= box.xMin - Tolerance && rect.yMin >= box.yMin - Tolerance
                && rect.xMax <= box.xMax + Tolerance && rect.yMax <= box.yMax + Tolerance, Is.True,
                $"{what} {rect} is inside {box}");
        }
    }
}
