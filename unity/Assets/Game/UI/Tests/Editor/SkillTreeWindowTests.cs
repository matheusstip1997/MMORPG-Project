#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Skills;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Skills;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Skills;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>
    /// The skill tree window v1 (spec 3.5, 9.13 pattern; WP-M2-5) with the committed tables: node states from the committed
    /// state, the level and the points; the requests its buttons and hotbar drops make, one at a time; the toasts; the
    /// committed texts; the layout on a real panel; and the hotbar's greyed weapon state and drop target.
    /// </summary>
    public sealed class SkillTreeWindowTests
    {
        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static SkillState State(string classId, params (string Node, int Level)[] learned)
        {
            var state = new SkillState(classId);
            foreach (var (node, level) in learned)
                state.SetLevel(node, level);
            return state;
        }

        // ---- view model

        [Test]
        public void A_new_warrior_sees_its_root_tree_with_the_row_0_skills_learnable()
        {
            var model = new SkillTreeWindowViewModel(SkillRules.From(Tables()));
            model.SetProgress(level: 1, points: 1);
            model.SetSkills(State("warrior"));

            Assert.That(model.Tabs.Select(t => t.Id), Is.EqualTo(new[] { "warrior" }));
            var states = model.Nodes.ToDictionary(n => n.Id, n => n.State);
            Assert.That(states["warrior.piercingAttack"], Is.EqualTo(SkillNodeState.Available));
            Assert.That(states["warrior.headCut"], Is.EqualTo(SkillNodeState.Locked)); // row 1: level 5
            Assert.That(model.Nodes.Single(n => n.Id == "warrior.headCut").Missing, Is.EqualTo(SkillTreeReason.CharacterLevelTooLow));
            Assert.That(states["warrior.provoke"], Is.EqualTo(SkillNodeState.Locked)); // row 3: level 13 (its SkillDef since PR 2)
            Assert.That(states.Values, Has.None.EqualTo(SkillNodeState.Unavailable)); // every node has a SkillDef row (PR 2)
            Assert.That(model.Advances, Is.Empty);

            model.Select("warrior.piercingAttack");
            var learn = model.Primary();
            Assert.That(learn.HasValue && learn.Value.Op == SkillTreeOp.Learn && learn.Value.Target == "warrior.piercingAttack", Is.True);
            model.Select("warrior.headCut");
            Assert.That(model.Primary(), Is.Null);
        }

        [Test]
        public void At_level_30_the_roots_children_are_offered_and_after_the_advance_both_trees_are_tabs()
        {
            var model = new SkillTreeWindowViewModel(SkillRules.From(Tables()));
            model.SetProgress(30, 30);
            model.SetSkills(State("warrior", ("warrior.piercingAttack", 1)));
            Assert.That(model.Advances.Select(c => c.Id), Is.EqualTo(new[] { "swordman", "knight" }));
            Assert.That(model.Advance("knight")!.Value.Op, Is.EqualTo(SkillTreeOp.Advance));

            model.SetSkills(State("knight", ("warrior.piercingAttack", 1)));
            Assert.That(model.Tabs.Select(t => t.Id), Is.EqualTo(new[] { "warrior", "knight" }));
            Assert.That(model.ActiveTab, Is.EqualTo("knight"));
            Assert.That(model.Advances, Is.Empty);
            var evolve = model.Nodes.Single(n => n.Id == "knight.piercingSequence");
            Assert.That(evolve.State, Is.EqualTo(SkillNodeState.Available));
            model.Select(evolve.Id);
            Assert.That(model.Primary()!.Value.Op, Is.EqualTo(SkillTreeOp.Evolve));
            model.SelectTab("warrior");
            Assert.That(model.Nodes.Single(n => n.Id == "warrior.piercingAttack").State, Is.EqualTo(SkillNodeState.Upgradable));
        }

        [Test]
        public void A_learned_active_drops_on_a_player_slot_and_a_passive_does_not()
        {
            var model = new SkillTreeWindowViewModel(SkillRules.From(Tables()));
            model.SetProgress(9, 0);
            model.SetSkills(State("warrior", ("warrior.piercingAttack", 1), ("warrior.increaseDef", 1)));
            var drop = model.Drop("warrior.piercingAttack", 3);
            Assert.That(drop.HasValue && drop.Value.Op == SkillTreeOp.AssignSlot && drop.Value.Slot == 3, Is.True);
            Assert.That(model.Drop("warrior.increaseDef", 3), Is.Null);
            Assert.That(model.Drop("warrior.piercingAttack", 12), Is.Null); // keys stop at Z (SkillTreeSettings.hotbarSlots)
            Assert.That(model.Nodes.Single(n => n.Id == "warrior.increaseDef").Draggable, Is.False);
        }

        // ---- binder

        private sealed class FakeRequests : IWorldRequests
        {
            public List<(SkillTreeOp Op, string Target, byte Slot)> Sent { get; } = new List<(SkillTreeOp, string, byte)>();

            public uint RequestInventoryMove(byte fromContainer, byte fromSlot, byte toContainer, byte toSlot, ushort count = 0) => 0;
            public uint RequestOutpostInteract(uint npcEntityId, NpcInteraction kind) => 0;
            public uint RequestSellQuote(uint npcEntityId, byte slot, ushort count) => 0;
            public uint RequestSell(uint npcEntityId, byte slot, ushort count, long quotedTotal) => 0;
            public uint RequestVendorBuy(uint npcEntityId, ushort itemNetId, ushort count, long quotedTotal) => 0;

            public uint RequestSkillTree(SkillTreeOp op, string target, byte slot)
            {
                Sent.Add((op, target, slot));
                return (uint)Sent.Count;
            }
        }

        private sealed class Rig
        {
            public Rig(int dropSlot = -1)
            {
                var set = Tables();
                var hud = HudConfig.From(set);
                Tree = new ClientSkillTree(set);
                Model = new SkillTreeWindowViewModel(Tree.Rules);
                var settings = PredictionSettings.From(set);
                Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
                var catalog = ActionCatalog.From(set);
                Feed = new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                    null, null, settings.TickMs) { Requests = Requests };
                Binder = new SkillTreeBinder(SkillTreeConfig.From(set), hud.Text, Model, Toast, 2_000, () => 0, _ => dropSlot,
                    readToggleKey: false);
                Binder.Bind(Feed);
            }

            public ClientSkillTree Tree { get; }
            public SkillTreeWindowViewModel Model { get; }
            public WorldClientFeed Feed { get; }
            public FakeRequests Requests { get; } = new FakeRequests();
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public SkillTreeBinder Binder { get; }

            private uint _version;

            public void ServerState(string classId, int level, int points, params (string Node, byte Level)[] learned)
            {
                Feed.RaiseProgression(new ProgressionState { Level = level, SkillPoints = points, BandPercent = 100 });
                Assert.That(Tree.Apply(ReliableMessages.Write(ReliableMessageId.SkillTreeState, new SkillTreeState
                {
                    Version = ++_version, ClassId = classId,
                    Nodes = learned.Select(l => new SkillNodeEntry { NodeId = l.Node, Level = l.Level }).ToArray(),
                }), out _), Is.True);
                Feed.RaiseSkillTree(Tree);
            }

            public void Answer(uint requestId, SkillTreeOp op, string target, SkillTreeReason reason = SkillTreeReason.None) =>
                Feed.RaiseSkillTreeAnswered(new SkillTreeResult { RequestId = requestId, Op = op, Target = target, Reason = reason });
        }

        [Test]
        public void Learning_sends_one_request_waits_for_its_answer_and_toasts_the_result()
        {
            var rig = new Rig();
            rig.ServerState("warrior", 1, 1);
            rig.Binder.Select("warrior.piercingAttack");
            rig.Binder.Primary();
            rig.Binder.Primary(); // a second press while the first waits: nothing
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { (SkillTreeOp.Learn, "warrior.piercingAttack", (byte)0) }));
            Assert.That(rig.Model.Pending, Is.EqualTo(1u));

            rig.Answer(1, SkillTreeOp.Learn, "warrior.piercingAttack");
            Assert.That(rig.Model.Pending, Is.EqualTo(0u));
            Assert.That((rig.Toast.TextId, rig.Toast.ArgValue), Is.EqualTo((SkillTreeTextIds.ToastLearned, "Piercing Attack")));
            rig.ServerState("warrior", 1, 0, ("warrior.piercingAttack", 1));
            Assert.That(rig.Model.Nodes.Single(n => n.Id == "warrior.piercingAttack").State, Is.EqualTo(SkillNodeState.Learned));
        }

        [Test]
        public void A_refusal_shows_its_reason_in_the_toast()
        {
            var rig = new Rig();
            rig.ServerState("warrior", 30, 30);
            rig.Binder.Advance("knight");
            Assert.That(rig.Requests.Sent.Single().Op, Is.EqualTo(SkillTreeOp.Advance));
            rig.Answer(1, SkillTreeOp.Advance, "knight", SkillTreeReason.AdvanceBlockedCombat);
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo(nameof(SkillTreeReason.AdvanceBlockedCombat)));
            Assert.That(rig.Model.Pending, Is.EqualTo(0u));
        }

        [Test]
        public void A_drop_on_a_hotbar_slot_assigns_it_and_a_drop_elsewhere_sends_nothing()
        {
            var onSlot = new Rig(dropSlot: 2);
            onSlot.ServerState("warrior", 1, 0, ("warrior.piercingAttack", 1));
            onSlot.Binder.Drop("warrior.piercingAttack", Vector2.zero);
            Assert.That(onSlot.Requests.Sent, Is.EqualTo(new[] { (SkillTreeOp.AssignSlot, "warrior.piercingAttack", (byte)2) }));

            var nowhere = new Rig(dropSlot: -1);
            nowhere.ServerState("warrior", 1, 0, ("warrior.piercingAttack", 1));
            nowhere.Binder.Drop("warrior.piercingAttack", Vector2.zero);
            Assert.That(nowhere.Requests.Sent, Is.Empty);
        }

        // ---- texts

        [Test]
        public void Committed_ui_strings_cover_every_skill_window_text_and_reason()
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var ids = typeof(SkillTreeTextIds).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()).ToList();
            Assert.That(ids.Where(id => !hud.Text.Has(id)), Is.Empty);
            foreach (SkillNodeKind kind in System.Enum.GetValues(typeof(SkillNodeKind)))
                Assert.That(hud.Text.Has(SkillTreeTextIds.Kind(kind)), Is.True, kind.ToString());
            foreach (WeaponType weapon in System.Enum.GetValues(typeof(WeaponType)))
            {
                if (weapon != WeaponType.None)
                    Assert.That(hud.Text.Has(SkillTreeTextIds.Weapon(weapon)), Is.True, weapon.ToString());
            }
            foreach (var reason in System.Enum.GetNames(typeof(SkillTreeReason)).Where(n => n != "None"))
                Assert.That(hud.Reasons.Has(reason), Is.True, reason);
            Assert.That(hud.Reasons.Has(nameof(ActionReason.WrongWeapon)), Is.True);
            Assert.That(SkillTreeConfig.From(set).ToggleBinding, Is.EqualTo("<Keyboard>/k"));
        }

        // ---- hotbar (R-3.5-07)

        [Test]
        public void A_slot_whose_weapon_is_not_equipped_is_drawn_greyed()
        {
            var model = new HotbarViewModel();
            int changes = 0;
            model.Changed += () => changes++;
            model.SetAction(7, "skill.archer.piercingShot");
            model.SetSkill(7, "archer.piercingShot", unusable: true);
            Assert.That(model.Slot(7)!.Unusable, Is.True);
            Assert.That(model.Slot(7)!.NodeId, Is.EqualTo("archer.piercingShot"));
            int before = changes;
            model.SetSkill(7, "archer.piercingShot", unusable: true);
            Assert.That(changes, Is.EqualTo(before), "no change, no event");
            model.SetSkill(7, "archer.piercingShot", unusable: false);
            Assert.That(model.Slot(7)!.Unusable, Is.False);
        }

        // ---- layout measured on a real panel (spec 9.8, 9.13)

        private const string ThemePath = "Assets/Game/UI/Login/LoginTheme.tss";

        [UnityTest]
        public System.Collections.IEnumerator The_window_fits_its_layout_row_and_its_nodes_sit_on_the_grid_inside_it()
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            settings.scaleMode = PanelScaleMode.ConstantPixelSize;
            var target = new RenderTexture(1280, 720, 24);
            settings.targetTexture = target;
            var host = new GameObject("skill-window-layout-test");
            SkillTreeWindowPresenter? presenter = null;
            HotbarPresenter? hotbar = null;
            try
            {
                var document = host.AddComponent<UIDocument>();
                document.panelSettings = settings;
                yield return null;
                var root = document.rootVisualElement;
                Assert.That(root, Is.Not.Null, "the UIDocument made no panel");
                root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(Game.UI.Editor.HudSceneInstaller.StylesPath));
                root.style.unityFontDefinition = FontDefinition.FromFont(AssetDatabase.LoadAssetAtPath<Font>(Game.UI.Editor.HudSceneInstaller.FontPath));
                root.style.fontSize = hud.Theme.Tokens.FontSizeBody;

                var rules = SkillRules.From(set);
                var model = new SkillTreeWindowViewModel(rules);
                var view = new HudView(hud);
                root.Add(view.Root);
                var hotbarModel = new HotbarViewModel();
                hotbar = new HotbarPresenter(hud, view, hotbarModel, HudElementIds.HotbarRight);
                hotbar.Bind();
                var binder = new SkillTreeBinder(SkillTreeConfig.From(set), hud.Text, model, new ToastViewModel(), 1500, () => 0,
                    hotbar.SlotAt, readToggleKey: false);
                presenter = new SkillTreeWindowPresenter(hud, view, model, rules, 6f, binder);
                presenter.Bind();
                model.SetProgress(30, 3);
                model.SetSkills(State("knight", ("warrior.piercingAttack", 1)));
                model.SetOpen(true);
                model.Select("knight.piercingSequence");
                Layout(root);
                Layout(root); // the grid's size is known after the first pass: the nodes are placed on the second

                var window = presenter.Window;
                Assert.That(hud.TryGetLayout(HudElementIds.SkillTreeWindow, out var row), Is.True);
                Assert.That((window.layout.width, window.layout.height), Is.EqualTo(((float)row.Size[0], (float)row.Size[1])),
                    "the window keeps the size of its HudLayout row");
                var inner = Inner(window);
                foreach (var node in model.Nodes)
                {
                    var button = presenter.NodeButton(node.Id)!;
                    AssertInside(button.worldBound, inner, node.Id);
                    Assert.That(button.resolvedStyle.fontSize, Is.GreaterThanOrEqualTo((float)hud.Theme.Tokens.FontSizeBody), node.Id);
                }
                // Same row, columns left to right: Piercing Sequence (col 2) left of Shield Strike (col 5).
                var left = presenter.NodeButton("knight.piercingSequence")!.worldBound;
                var right = presenter.NodeButton("knight.shieldStrike")!.worldBound;
                Assert.That(left.y, Is.EqualTo(right.y).Within(0.5f));
                Assert.That(left.xMax, Is.LessThanOrEqualTo(right.xMin + 0.5f));
                Assert.That(presenter.PrimaryButton.text, Is.EqualTo("Evolve"));
                Assert.That(presenter.DetailsText, Does.Contain("Piercing Attack"));
                // A hotbar slot is a drop target at its own place.
                var q = root.Q("slot_7");
                Assert.That(hotbar.SlotAt(q.worldBound.center), Is.EqualTo(7));
                Assert.That(hotbar.SlotAt(new Vector2(-10, -10)), Is.EqualTo(-1));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                presenter?.Dispose();
                hotbar?.Dispose();
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
