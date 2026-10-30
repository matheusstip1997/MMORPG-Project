#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>
    /// The minimap v0 (spec 9.3 "Minimap v0 marker set", 9.12; WP-M2-10b) on the committed m1 world and tables: the map image
    /// with its region tints, the passes and the gate found in the grid, the NPCs from the world's markers, the camp hint of
    /// the active run's kill and return steps, the death spot, and on a real panel the markers at the right pixel, clipped to
    /// the frame, with the arrow turned by the facing.
    /// </summary>
    public sealed class MinimapTests
    {
        private const ushort Imp = 4, Spitter = 5; // MonsterDef netIds of m2.imp and m2.spitter

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static (WorldGrid Grid, WorldIndex Index) World()
        {
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
            Assert.That(ClientWorld.TryLoadIndex("m1", out var index, out error), Is.True, error);
            return (grid!, index!);
        }

        private static MinimapModel Model(ConfigSet set)
        {
            var model = new MinimapModel(MinimapConfig.From(set));
            var (grid, index) = World();
            model.SetWorld(grid, index);
            return model;
        }

        private static List<MinimapPoint> Of(MinimapModel model, string markerId) =>
            model.Points.Where(p => p.Style.Id == markerId).ToList();

        private static void AssertNear(IEnumerable<MinimapPoint> points, int xCm, int yCm, int toleranceCm, string what) =>
            Assert.That(points.Any(p => Mathf.Abs(p.XCm - xCm) <= toleranceCm && Mathf.Abs(p.YCm - yCm) <= toleranceCm), Is.True,
                $"{what}: a marker within {toleranceCm} cm of ({xCm}, {yCm}); have {string.Join(", ", points.Select(p => $"({p.XCm}, {p.YCm})"))}");

        private static RunStatus Run(RunState state, params (QuestKind Kind, ushort Monster, RunStepState State)[] steps) => new RunStatus
        {
            Revision = 1,
            State = state,
            Steps = steps.Select(s => new RunStepEntry { Kind = s.Kind, MonsterNetId = s.Monster, Required = 3, State = s.State }).ToArray(),
        };

        [Test]
        public void The_passes_between_the_rings_and_the_gate_are_found_in_the_grid()
        {
            var model = Model(Tables());

            // Ring 1 / ring 2 (the tutorial gate of spec 12.3): two passes north, two south, three east (FieldZoneGreybox.Ridges).
            var gate = Of(model, "gate");
            Assert.That(gate.Count, Is.EqualTo(7), "ring 1 to ring 2 crossings");
            foreach (var (x, y) in new[] { (11000, 0), (11000, 4500), (11000, -4500), (3500, 8000), (3500, -8000), (8500, 8000), (8500, -8000) })
                AssertNear(gate, x, y, 150, "gate");
            // Ring 2 / ring 3 and ring 3 / ring 4: the road pass and the two side passes at z +-105 m.
            var ring2 = Of(model, "pass.ring2");
            var ring3 = Of(model, "pass.ring3");
            Assert.That((ring2.Count, ring3.Count), Is.EqualTo((3, 3)));
            foreach (var (x, y) in new[] { (19000, 0), (19000, 10500), (19000, -10500) })
                AssertNear(ring2, x, y, 150, "ring 2 pass");
            foreach (var (x, y) in new[] { (29000, 0), (29000, 10500), (29000, -10500) })
                AssertNear(ring3, x, y, 150, "ring 3 pass");
        }

        [Test]
        public void Both_outposts_and_both_vendors_come_from_the_world_markers()
        {
            var model = Model(Tables());

            var outposts = Of(model, "outpost");
            var vendors = Of(model, "vendor");
            Assert.That((outposts.Count, vendors.Count), Is.EqualTo((2, 2)));
            AssertNear(outposts, -1500, 600, 0, "city outpost");
            AssertNear(outposts, 17300, 1100, 0, "mid-zone outpost");
            AssertNear(vendors, -2500, -1000, 0, "city vendor");
            AssertNear(vendors, 17900, 1100, 0, "mid-zone vendor");
        }

        [Test]
        public void The_map_image_tints_each_ring_and_darkens_the_ridges()
        {
            var set = Tables();
            var config = MinimapConfig.From(set);
            var model = Model(set);
            var (grid, index) = World();
            var texture = model.Texture!;
            Assert.That((texture.Width, texture.Height, texture.TexelCm), Is.EqualTo((440, 400, 100)), "1 m texels over 880 x 800 cells of 50 cm");

            foreach (var (x, y, region) in new[] { (10000, 3000, "field1.ring1"), (15000, -15000, "field1.ring2"), (24000, 5000, "field1.ring3"), (34000, -5000, "field1.ring4") })
            {
                var at = FixVec2.FromCm(x, y);
                Assert.That((grid.IsWalkable(at), grid.RegionIndexAt(at)), Is.EqualTo((true, (byte)index.RegionIndexOf(region))), $"({x}, {y}) is open {region}");
                Assert.That(texture.PixelAt(x, y), Is.EqualTo((Color32)config.GroundColor(region)), region);
            }
            // The ring 2 / ring 3 ridge at x 190 m, between its passes.
            Assert.That(texture.PixelAt(19000, 5000), Is.EqualTo((Color32)config.BlockedColor), "a ridge is blocked ground");
        }

        [Test]
        public void An_active_run_hints_every_camp_of_its_kill_and_return_steps_only()
        {
            var model = Model(Tables());
            int revision = model.Revision;

            model.SetRun(Run(RunState.Active, (QuestKind.Kill, Imp, RunStepState.Active)));
            var camps = Of(model, "runCamp");
            Assert.That(model.Revision, Is.GreaterThan(revision), "the marker list changed");
            Assert.That(camps.Count, Is.EqualTo(4), "the four imp camps, each once");
            foreach (var (x, y) in new[] { (7200, 5200), (7200, -5200), (9500, 1800), (9500, -1800) })
                AssertNear(camps, x, y, 100, "imp camp");

            model.SetRun(Run(RunState.Active, (QuestKind.ReturnItem, Spitter, RunStepState.Active), (QuestKind.Deliver, Imp, RunStepState.Active),
                (QuestKind.Kill, Imp, RunStepState.Done)));
            camps = Of(model, "runCamp");
            Assert.That(camps.Count, Is.EqualTo(4), "the spitter camps only: deliver steps and done steps get no hint");
            AssertNear(camps, 15000, 5000, 100, "spitter camp");

            model.SetRun(Run(RunState.Completed, (QuestKind.Kill, Imp, RunStepState.Done)));
            Assert.That(Of(model, "runCamp"), Is.Empty, "a completed run hints nothing");
            model.SetRun(null);
            Assert.That(Of(model, "runCamp"), Is.Empty);
        }

        [Test]
        public void A_death_leaves_the_death_spot_until_it_expires()
        {
            var set = Tables();
            var model = Model(set);
            int deathSpotMs = set.Single<MinimapSettings>().DeathSpotMs;

            model.SetPlayer(5000, -2000, 0);
            model.SetOwnHp(0, 0);
            Assert.That(Of(model, "deathSpot"), Is.Empty, "entering the world dead: the death position is unknown");

            model.SetOwnHp(120, 100);
            model.SetOwnHp(0, 1000);
            model.SetPlayer(-3000, 0, 0); // the respawn moves the player; the spot stays where it fell
            model.SetOwnHp(150, 2000);
            AssertNear(Of(model, "deathSpot"), 5000, -2000, 0, "death spot");

            model.Expire(1000 + deathSpotMs - 1);
            Assert.That(Of(model, "deathSpot").Count, Is.EqualTo(1));
            model.Expire(1000 + deathSpotMs);
            Assert.That(Of(model, "deathSpot"), Is.Empty, "gone after MinimapSettings.deathSpotMs");
        }

        private sealed class Jobs : IOwnJobs
        {
            public string[] Ids = new string[0];
            public int Version { get; set; }
            public bool Has(string jobId) => System.Array.IndexOf(Ids, jobId) >= 0;
        }

        [Test]
        public void The_job_nodes_of_the_own_jobs_come_from_the_resource_node_markers()
        {
            // WP-M3-4 (spec 9.3 job nodes): 12 ore markers and 2 gem veins, mapped by GatherNodes.
            var model = Model(Tables());

            // Before the own jobs are known (WP-M3-1's ClientJobs without a state) no job node shows.
            Assert.That(Of(model, "jobNode.ore").Concat(Of(model, "jobNode.gem")), Is.Empty);

            // A Blacksmith sees every ore and gem vein node.
            model.SetJobs(new Jobs { Ids = new[] { "blacksmith" }, Version = 1 });
            var ore = Of(model, "jobNode.ore");
            var gems = Of(model, "jobNode.gem");
            Assert.That((ore.Count, gems.Count), Is.EqualTo((12, 2)));
            AssertNear(ore, 2000, 7000, 0, "field1.node.ring1.a");
            AssertNear(ore, 38000, -18000, 0, "field1.node.ring4.c");
            AssertNear(gems, 24200, 11500, 0, "field1.node.ring3.gem1");
            AssertNear(gems, 24200, -11500, 0, "field1.node.ring3.gem2");

            // A character without the Blacksmith job sees none of them; picking it shows them again.
            var jobs = new Jobs { Ids = new[] { "jeweler" }, Version = 2 };
            model.SetJobs(jobs);
            Assert.That(Of(model, "jobNode.ore").Concat(Of(model, "jobNode.gem")), Is.Empty);
            int revision = model.Revision;
            jobs.Ids = new[] { "jeweler", "blacksmith" };
            jobs.Version = 3;
            model.SetJobs(jobs);
            Assert.That(model.Revision, Is.GreaterThan(revision));
            Assert.That((Of(model, "jobNode.ore").Count, Of(model, "jobNode.gem").Count), Is.EqualTo((12, 2)));
        }

        [Test]
        public void The_world_loads_from_the_feed_once_its_world_id_is_known()
        {
            var set = Tables();
            var settings = PredictionSettings.From(set);
            var (grid, _) = World();
            var catalog = ActionCatalog.From(set);
            var feed = new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid, catalog),
                null, null, settings.TickMs);
            var model = new MinimapModel(MinimapConfig.From(set));

            model.Update(feed, 0);
            Assert.That(model.HasWorld, Is.False, "no world id yet");

            feed.WorldId = "m1";
            model.Update(feed, 0);
            Assert.That((model.HasWorld, model.WorldError, model.HasPlayer), Is.EqualTo((true, "", false)));
            Assert.That(Of(model, "outpost").Count, Is.EqualTo(2));
        }

        // ---- a real panel (the InventoryWindowTests setup)

        private const string ThemePath = "Assets/Game/UI/Login/LoginTheme.tss";

        [UnityTest]
        public System.Collections.IEnumerator Markers_sit_at_their_world_position_and_the_arrow_turns_with_the_facing()
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            panel.scaleMode = PanelScaleMode.ConstantPixelSize;
            var target = new RenderTexture(1280, 720, 24);
            panel.targetTexture = target;
            var host = new GameObject("minimap-test");
            MinimapPresenter? presenter = null;
            try
            {
                var document = host.AddComponent<UIDocument>();
                document.panelSettings = panel;
                yield return null;
                var root = document.rootVisualElement;
                root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(Game.UI.Editor.HudSceneInstaller.StylesPath));
                var view = new HudView(hud);
                root.Add(view.Root);
                var model = Model(set);
                presenter = new MinimapPresenter(hud, view, model, () => null, icons: null);
                presenter.Bind();
                Layout(root);

                // Spec 9.3: 1024, 56, 240 x 184 of the 1280 x 720 reference.
                Assert.That(hud.TryGetLayout(HudElementIds.Minimap, out var row), Is.True);
                var box = presenter.Frame.parent.worldBound;
                Assert.That((box.x, box.y, box.width, box.height), Is.EqualTo((1024f, 56f, 240f, 184f)));

                // Beside the mid-zone outpost, facing east.
                model.SetPlayer(17000, 1100, 16384);
                Draw(presenter, root);
                var frame = presenter.Frame;
                float width = frame.layout.width, height = frame.layout.height, scale = set.Single<MinimapSettings>().CmPerPx;
                Assert.That(width, Is.GreaterThan(200f));

                var outpost = MarkerAt(presenter, "outpost", 17300, 1100);
                AssertCenter(outpost, frame, width * 0.5f + 300 / scale, height * 0.5f, "the mid-zone outpost, 3 m east");
                var vendor = MarkerAt(presenter, "vendor", 17900, 1100);
                AssertCenter(vendor, frame, width * 0.5f + 900 / scale, height * 0.5f, "the mid-zone vendor, 9 m east");
                Assert.That(MarkerAt(presenter, "vendor", -2500, -1000).resolvedStyle.display, Is.EqualTo(DisplayStyle.None),
                    "the city vendor, 195 m west, is outside the frame");
                AssertCenter(presenter.Arrow, frame, width * 0.5f, height * 0.5f, "the arrow at the center");
                Assert.That(presenter.Arrow.resolvedStyle.rotate.angle.ToDegrees(), Is.EqualTo(90f).Within(0.01f), "facing east");

                // The map image is placed so the player's texel is under the center (north-up: the world's south edge is lower).
                var texture = model.Texture!;
                var map = presenter.Map.worldBound;
                Assert.That(map.x - frame.worldBound.x, Is.EqualTo(width * 0.5f - (17000 - texture.OriginXCm) / scale).Within(0.5f));
                Assert.That(map.yMax - frame.worldBound.y, Is.EqualTo(height * 0.5f + (1100 - texture.OriginYCm) / scale).Within(0.5f));

                // At the ring 1 east pass, facing south-west: the gate shows; north is up.
                model.SetPlayer(10000, 0, 40960);
                Draw(presenter, root);
                var gate = MarkerAt(presenter, "gate", 11000, 0, toleranceCm: 150);
                Assert.That(gate.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
                var gatePoint = model.Points.First(p => p.Style.Id == "gate" && Mathf.Abs(p.XCm - 11000) <= 150 && Mathf.Abs(p.YCm) <= 150);
                AssertCenter(gate, frame, width * 0.5f + (gatePoint.XCm - 10000) / scale, height * 0.5f - gatePoint.YCm / scale, "the gate");
                var northGate = model.Points.First(p => p.Style.Id == "gate" && p.YCm > 4000 && p.XCm > 10000);
                AssertCenter(MarkerAt(presenter, "gate", northGate.XCm, northGate.YCm), frame,
                    width * 0.5f + (northGate.XCm - 10000) / scale, height * 0.5f - northGate.YCm / scale, "the north-east gate is above");
                Assert.That(presenter.Arrow.resolvedStyle.rotate.angle.ToDegrees(), Is.EqualTo(225f).Within(0.01f));

                // A run's camp hints appear with the run.
                model.SetRun(Run(RunState.Active, (QuestKind.Kill, Imp, RunStepState.Active)));
                Draw(presenter, root);
                var camp = model.Points.First(p => p.Style.Id == "runCamp" && p.XCm > 9000 && p.YCm > 0);
                AssertCenter(MarkerAt(presenter, "runCamp", camp.XCm, camp.YCm), frame,
                    width * 0.5f + (camp.XCm - 10000) / scale, height * 0.5f - camp.YCm / scale, "the imp camp at (95, 18) m");
            }
            finally
            {
                presenter?.Dispose();
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(panel);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        private static void Draw(MinimapPresenter presenter, VisualElement root)
        {
            presenter.Draw();
            Layout(root);
            presenter.Draw(); // the frame's size is known after the first layout pass
            Layout(root);
        }

        private static MinimapMarkerElement MarkerAt(MinimapPresenter presenter, string markerId, int xCm, int yCm, int toleranceCm = 0)
        {
            var points = presenter.Model.Points;
            var markers = presenter.Markers.ToList();
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].Style.Id == markerId && Mathf.Abs(points[i].XCm - xCm) <= toleranceCm && Mathf.Abs(points[i].YCm - yCm) <= toleranceCm)
                    return markers[i];
            }
            Assert.Fail($"no {markerId} marker at ({xCm}, {yCm})");
            return null!;
        }

        private static void AssertCenter(VisualElement element, VisualElement frame, float x, float y, string what)
        {
            var center = element.worldBound.center - frame.worldBound.position;
            Assert.That(center.x, Is.EqualTo(x).Within(1f), what + " x");
            Assert.That(center.y, Is.EqualTo(y).Within(1f), what + " y");
        }

        /// <summary>Runs the panel's style and layout pass now (an edit-mode panel is not updated by the player loop).</summary>
        private static void Layout(VisualElement element)
        {
            var update = element.panel.GetType().GetMethod("UpdateWithoutRepaint",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, System.Type.EmptyTypes, null);
            Assert.That(update, Is.Not.Null, "UI Toolkit's panel has no UpdateWithoutRepaint");
            update!.Invoke(element.panel, null);
        }
    }
}
