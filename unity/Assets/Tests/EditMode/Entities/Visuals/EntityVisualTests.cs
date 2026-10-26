#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Gameplay.Entities;
using Game.Gameplay.Entities.Visuals;
using Game.Gameplay.Entities.Visuals.Editor;
using NUnit.Framework;
using Shared.Data.Tables;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Tests.EditMode.Entities.Visuals
{
    /// <summary>The EntityVisualDef table, the prefab registry (Addressables) and the capsule fallback.</summary>
    public class EntityVisualTests
    {
        private const float TickMs = 50f;

        /// <summary>In-memory prefab source: addresses map to prefabs; listed addresses stay loading until finished.</summary>
        private sealed class FakeSource : IVisualPrefabSource
        {
            public readonly Dictionary<string, GameObject> Prefabs = new Dictionary<string, GameObject>();
            public readonly HashSet<string> Loading = new HashSet<string>();

            public event Action<string>? Finished;

            public PrefabState Get(string address, out GameObject? prefab)
            {
                prefab = null;
                if (Loading.Contains(address))
                    return PrefabState.Loading;
                return Prefabs.TryGetValue(address, out prefab) ? PrefabState.Ready : PrefabState.Missing;
            }

            public void Finish(string address)
            {
                Loading.Remove(address);
                Finished?.Invoke(address);
            }
        }

        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();
        private readonly List<EntityVisual> _visuals = new List<EntityVisual>();

        /// <summary>
        /// A Library made before <see cref="FailedModelReimporter"/> may still hold a failed VRM or glTF import (the
        /// fresh-Library shader order); import those models again so the real-prefab tests see the art, not a cached failure.
        /// </summary>
        [OneTimeSetUp]
        public void ImportFailedModelsAgain() => EntityVisualPrefabBuilder.ReimportFailedModels();

        [TearDown]
        public void TearDown()
        {
            foreach (var visual in _visuals)
                visual.Dispose();
            _visuals.Clear();
            foreach (var created in _created)
            {
                if (created != null)
                    UnityEngine.Object.DestroyImmediate(created);
            }
            _created.Clear();
        }

        private T Track<T>(T created) where T : UnityEngine.Object
        {
            _created.Add(created);
            return created;
        }

        private static EntityVisualCatalog Committed()
        {
            Assert.IsTrue(ClientConfig.TryLoad(out var config, out var error), error);
            return EntityVisualCatalog.From(config);
        }

        private static EntityVisualDef Row(string id, string prefab, string weapon = "", VisualBone bone = VisualBone.None) => new EntityVisualDef
        {
            Id = id, PrefabAddress = prefab, WeaponAddress = weapon, WeaponBone = bone, ScalePercent = 100,
        };

        // ---------- table and registry ----------

        [Test]
        public void The_committed_table_has_a_visual_per_root_class_monster_and_the_boss()
        {
            var catalog = Committed();

            foreach (var id in new[] { "warrior", "archer", "mage", "assist", "demon", "alien", "cyclopsBoss" })
                Assert.IsTrue(catalog.TryGet(id, out _), id);
            Assert.IsTrue(catalog.TryGet("cyclopsBoss", out var boss));
            Assert.IsTrue(catalog.TryGet("demon", out var demon));
            Assert.Greater(boss.ScalePercent, demon.ScalePercent, "the boss is the Cyclops scaled up");
        }

        [Test]
        public void Unknown_and_empty_visual_ids_are_not_found()
        {
            var catalog = Committed();

            Assert.IsFalse(catalog.TryGet("nobody", out _));
            Assert.IsFalse(catalog.TryGet("", out _));
            Assert.IsFalse(catalog.TryGet(null, out _));
        }

        [Test]
        public void Every_address_in_the_table_is_an_Addressables_entry_with_a_prefab()
        {
            foreach (var address in Committed().Addresses())
                Assert.IsNotNull(EditorPrefabSource.Find(address), $"no Addressables prefab at '{address}' (run MMORPG / Visuals / Rebuild Visual Prefabs)");
        }

        [Test]
        public void Every_avatar_and_weapon_model_the_prefabs_nest_is_imported()
        {
            // A failed import leaves the character and weapon prefabs with a missing nested prefab (no humanoid bones).
            var models = EntityVisualPrefabBuilder.ModelPaths().ToList();
            Assert.AreEqual(9, models.Count, "4 VRoid avatars and 5 KayKit weapons");
            foreach (var path in models)
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(path), $"{path} did not import (see the Editor log)");
        }

        [Test]
        public void Character_and_monster_prefabs_carry_an_animated_rig()
        {
            var catalog = Committed();
            foreach (var id in new[] { "warrior", "archer", "mage", "assist", "demon", "alien", "cyclopsBoss" })
            {
                catalog.TryGet(id, out var def);
                var rig = EditorPrefabSource.Find(def.PrefabAddress)!.GetComponent<EntityVisualRig>();
                Assert.IsNotNull(rig, id);
                Assert.IsNotNull(rig.Animator, id);
                Assert.IsNotNull(rig.AnimationSet, id);
                Assert.IsNotNull(rig.AnimationSet!.idle, id);
                Assert.IsNotNull(rig.AnimationSet.walk, id);
                Assert.IsNotNull(rig.AnimationSet.FindAction("any.action"), id);
                Assert.IsNotNull(rig.AnimationSet.Reaction(ReactionKind.Flinched), id);
                Assert.IsNotNull(rig.AnimationSet.Reaction(ReactionKind.Dead), id);
            }
        }

        // ---------- factory: loading, fallback, scale ----------

        [Test]
        public void A_missing_body_prefab_means_the_capsule()
        {
            var factory = new EntityVisualFactory(new FakeSource(), TickMs);
            var parent = Track(new GameObject("root")).transform;

            var state = factory.TryCreate(Row("ghost", "Visuals/Nothing"), parent, out var visual);

            Assert.AreEqual(PrefabState.Missing, state);
            Assert.IsNull(visual);
            Assert.AreEqual(0, parent.childCount);
        }

        [Test]
        public void The_factory_waits_until_every_prefab_of_the_row_is_loaded()
        {
            var source = new FakeSource();
            source.Prefabs["body"] = Track(new GameObject("Body"));
            source.Prefabs["weapon"] = Track(new GameObject("Weapon"));
            source.Loading.Add("weapon");
            var factory = new EntityVisualFactory(source, TickMs);
            var parent = Track(new GameObject("root")).transform;

            Assert.AreEqual(PrefabState.Loading, factory.TryCreate(Row("x", "body", "weapon", VisualBone.RightHand), parent, out _));
            Assert.AreEqual(0, parent.childCount);
        }

        [Test]
        public void The_row_scale_is_applied_to_the_body()
        {
            var source = new FakeSource();
            source.Prefabs["body"] = Track(new GameObject("Body"));
            var factory = new EntityVisualFactory(source, TickMs);
            var parent = Track(new GameObject("root")).transform;
            var row = Row("big", "body");
            row.ScalePercent = 170;

            Assert.AreEqual(PrefabState.Ready, factory.TryCreate(row, parent, out var visual));

            Assert.AreEqual(1.7f, visual!.Instance.transform.localScale.x, 1e-4f);
            Assert.AreSame(parent, visual.Instance.transform.parent);
            Assert.IsFalse(visual.IsAnimated, "a prefab without a rig is shown static");
        }

        [Test]
        public void A_weapon_on_a_body_without_humanoid_bones_is_dropped_with_a_warning()
        {
            var source = new FakeSource();
            source.Prefabs["body"] = Track(new GameObject("Body"));
            source.Prefabs["weapon"] = Track(new GameObject("Weapon"));
            var factory = new EntityVisualFactory(source, TickMs);
            var parent = Track(new GameObject("root")).transform;

            LogAssert.Expect(LogType.Warning, new Regex("no humanoid RightHand bone"));
            Assert.AreEqual(PrefabState.Ready, factory.TryCreate(Row("blob", "body", "weapon", VisualBone.RightHand), parent, out var visual));

            Assert.AreEqual(0, visual!.Instance.transform.childCount);
        }

        // ---------- weapons on humanoid bones (real prefabs) ----------

        private EntityVisual Build(string id)
        {
            Committed().TryGet(id, out var def);
            var parent = Track(new GameObject(id)).transform;
            var state = new EntityVisualFactory(new EditorPrefabSource(), TickMs).TryCreate(def, parent, out var visual);
            Assert.AreEqual(PrefabState.Ready, state, id);
            _visuals.Add(visual!);
            return visual!;
        }

        private static Transform Bone(EntityVisual visual, HumanBodyBones bone)
        {
            var animator = visual.Instance.GetComponent<EntityVisualRig>().Animator!;
            Assert.IsTrue(animator.isHuman);
            return animator.GetBoneTransform(bone);
        }

        [Test]
        public void The_warrior_holds_the_two_handed_sword_in_the_right_hand()
        {
            var visual = Build("warrior");

            var hand = Bone(visual, HumanBodyBones.RightHand);

            Assert.IsNotNull(hand.Find("Sword2H"), "sword under the right hand bone");
        }

        [Test]
        public void The_assist_holds_a_staff_and_no_shield()
        {
            // The M1 Assist uses a staff (docs/design/skills-m1-proposal.md decision 7).
            var visual = Build("assist");

            Assert.IsNotNull(Bone(visual, HumanBodyBones.RightHand).Find("Staff"));
            Assert.IsNull(Bone(visual, HumanBodyBones.LeftHand).Find("RoundShield"));
        }

        [TestCase("archer", "Crossbow")]
        [TestCase("mage", "Staff")]
        public void Ranged_classes_hold_their_weapon_in_the_right_hand(string id, string weapon)
        {
            Assert.IsNotNull(Bone(Build(id), HumanBodyBones.RightHand).Find(weapon));
        }

        [Test]
        public void Bone_mapping_covers_the_table_bones()
        {
            Assert.AreEqual(HumanBodyBones.RightHand, VisualAttachment.ToHumanBone(VisualBone.RightHand));
            Assert.AreEqual(HumanBodyBones.LeftHand, VisualAttachment.ToHumanBone(VisualBone.LeftHand));
            Assert.IsNull(VisualAttachment.ToHumanBone(VisualBone.None));
        }

        // ---------- the presentation hook on a real visual ----------

        [Test]
        public void An_action_phase_plays_the_classes_clip_time_scaled_and_joined_late()
        {
            var visual = Build("warrior");
            var set = visual.Instance.GetComponent<EntityVisualRig>().AnimationSet!;
            var clip = set.defaultAction.clip!;
            visual.Update(0f, 0f, nowTick: 106);

            // Startup of 300 ms (6 ticks) started at tick 100: the view is exactly at its end.
            visual.OnActionPhase("warrior.normalAttack", ActionPresentationPhase.Startup, 100, 300);

            Assert.IsTrue(visual.State!.IsActionPlaying);
            Assert.AreEqual(set.defaultAction.startupEnd * clip.length, visual.State.ActionTimeS, 1e-3f);
            visual.Dispose();
        }

        [Test]
        public void A_mage_channel_loops_its_channel_clip()
        {
            var visual = Build("mage");
            var set = visual.Instance.GetComponent<EntityVisualRig>().AnimationSet!;
            Assert.IsNotNull(set.defaultAction.channelLoop);
            visual.Update(0f, 0f, nowTick: 0);

            visual.OnActionPhase("mage.channel", ActionPresentationPhase.Channel, 0, 3000);
            visual.Update(0.5f, 0f, nowTick: 10);

            Assert.IsTrue(visual.State!.IsActionPlaying);
            float loop = set.defaultAction.channelLoop!.length;
            Assert.AreEqual(0.5f % loop, visual.State.ActionTimeS, 1e-3f);
            visual.Dispose();
        }

        [Test]
        public void A_flinch_cuts_the_action_on_a_real_visual()
        {
            var visual = Build("demon");
            visual.Update(0f, 0f, nowTick: 0);
            visual.OnActionPhase("demon.bite", ActionPresentationPhase.Startup, 0, 400);
            visual.Update(0.2f, 0f, nowTick: 4);
            int attack = visual.State!.ActionClip;

            visual.OnReaction(ReactionKind.Flinched, 4, 200);

            Assert.AreNotEqual(attack, visual.State.ActionClip);
            Assert.IsTrue(visual.State.IsActionPlaying);
            visual.Dispose();
        }

        // ---------- EntityViews: capsule fallback and swap-in ----------

        [Test]
        public void Views_show_the_capsule_for_an_unknown_visual_id_and_the_art_for_a_known_one()
        {
            var views = Track(new GameObject("Views")).AddComponent<EntityViews>();
            views.UseVisuals(Committed(), new EditorPrefabSource(), TickMs);

            views.SetLocalVisual("nobody");
            LogAssert.Expect(LogType.Warning, new Regex("no EntityVisualDef row 'nobody'"));
            views.ShowLocal(true);
            Assert.IsNotNull(views.LocalView!.Find("Capsule"));

            views.SetLocalVisual("mage");
            Assert.IsNull(views.LocalView.Find("Capsule"));
            Assert.IsNotNull(views.LocalView.Find("mage"));
            views.ShowLocal(false);
        }

        // ---------- definition ids of spawns (protocol 3) ----------

        [Test]
        public void Spawn_definition_ids_name_the_committed_class_and_monster_visuals()
        {
            Assert.IsTrue(ClientConfig.TryLoad(out var config, out var error), error);
            var ids = DefinitionVisualIds.From(config);

            Assert.IsTrue(ids.TryGet(Shared.Protocol.EntityKind.Monster, 1, out var monster));
            Assert.AreEqual("demon", monster);
            Assert.IsTrue(ids.TryGet(Shared.Protocol.EntityKind.Monster, 3, out var boss));
            Assert.AreEqual("cyclopsBoss", boss);
            Assert.IsTrue(ids.TryGet(Shared.Protocol.EntityKind.Player, 3, out var mage));
            Assert.AreEqual("mage", mage);
            Assert.IsFalse(ids.TryGet(Shared.Protocol.EntityKind.Monster, 0, out _));
            Assert.IsFalse(ids.TryGet(Shared.Protocol.EntityKind.Npc, 1, out _));
            Assert.IsFalse(ids.TryGet(Shared.Protocol.EntityKind.Monster, 999, out _));
        }

        [Test]
        public void A_spawned_monster_shows_the_visual_of_its_MonsterDef()
        {
            Assert.IsTrue(ClientConfig.TryLoad(out var config, out var error), error);
            var views = Track(new GameObject("Views")).AddComponent<EntityViews>();
            views.UseVisuals(Committed(), new EditorPrefabSource(), TickMs);
            views.UseDefinitions(DefinitionVisualIds.From(config));
            var registry = new EntityRegistry(Game.Tests.EditMode.Prediction.PredictionTestKit.Settings());
            views.Bind(registry);

            var buffer = new byte[256];
            var writer = new Shared.Protocol.SnapshotWriter(buffer, new Shared.Protocol.SnapshotHeader { ServerTick = 10 });
            Assert.IsTrue(writer.TryAddSpawn(new Shared.Protocol.SpawnEvent
            {
                EntityId = 5, Kind = Shared.Protocol.EntityKind.Monster, DefId = 3, // m1.cyclopsBoss
            }));
            Assert.IsTrue(writer.TryAddEntity(new Shared.Protocol.EntitySnapshot { EntityId = 1, HpBucket = 63 }));
            Assert.IsTrue(writer.TryAddEntity(new Shared.Protocol.EntitySnapshot { EntityId = 5, HpBucket = 63 }));
            Assert.IsTrue(registry.ApplySnapshot(buffer.AsSpan(0, writer.Finish()), out _, out _, out _));

            var root = views.transform.Find("Monster 5");
            Assert.IsNotNull(root);
            Assert.IsNotNull(root.Find("cyclopsBoss"), "the boss row's visual, not the default demon");
            Assert.IsNull(root.Find("Capsule"));
            views.Unbind();
        }

        // ---------- kit actions and their effects (backlog E) ----------

        [Test]
        public void Every_kit_action_has_its_own_entry_in_its_class_animation_set()
        {
            // One entry per action, even where takes repeat, so the final animations replace data and assets only.
            Assert.IsTrue(ClientConfig.TryLoad(out var config, out var error), error);
            var catalog = EntityVisualCatalog.From(config);
            Assert.IsTrue(config.TryGet(typeof(ActionBindings), out var bindings));
            Assert.IsTrue(config.TryGet(typeof(ClassDef), out var classes));
            foreach (ActionBindings row in bindings.Rows)
            {
                var classDef = classes.Rows.Cast<ClassDef>().Single(c => c.Id == row.ClassId);
                Assert.IsTrue(catalog.TryGet(classDef.VisualId, out var def), row.ClassId);
                var set = EditorPrefabSource.Find(def.PrefabAddress)!.GetComponent<EntityVisualRig>().AnimationSet!;
                foreach (var actionId in row.SkillSlots.Where(id => id.Length > 0).Append(row.NormalAttack))
                {
                    var entry = set.actions.SingleOrDefault(a => a.actionId == actionId);
                    Assert.IsNotNull(entry, $"{row.ClassId}: no entry for {actionId}");
                    Assert.IsNotNull(entry!.clip, actionId);
                }
            }
        }

        [TestCase("assist", "skill.assist.healWave", "vfx.healWave")]
        [TestCase("assist", "skill.assist.flash", "vfx.flash")]
        [TestCase("assist", "skill.assist.simpleCleanse", "vfx.simpleCleanse")]
        [TestCase("assist", "skill.assist.lifeRegeneration", "vfx.lifeRegeneration")]
        public void Circle_skills_name_their_effect_and_it_has_a_prefab(string visualId, string actionId, string vfxId)
        {
            var catalog = Committed();
            Assert.IsTrue(catalog.TryGet(visualId, out var body));
            var set = EditorPrefabSource.Find(body.PrefabAddress)!.GetComponent<EntityVisualRig>().AnimationSet!;
            Assert.AreEqual(vfxId, set.FindAction(actionId)!.vfxId);
            Assert.IsTrue(catalog.TryGet(vfxId, out var effect));
            Assert.IsNotNull(EditorPrefabSource.Find(effect.PrefabAddress), effect.PrefabAddress);
        }

        [Test]
        public void An_action_effect_shows_at_the_caster_when_active_starts_and_lasts_its_entrys_lifetime()
        {
            // ActionVfx: Heal Wave's ring appears where the Assist stands, turned to its facing, when Active starts; other
            // phases show nothing. It lasts the animation-set entry's vfxLifetimeMs.
            var views = Track(new GameObject("Views")).AddComponent<EntityViews>();
            views.UseVisuals(Committed(), new EditorPrefabSource(), TickMs);
            views.SetLocalVisual("assist");
            views.ShowLocal(true);
            views.PlaceLocal(new Vector3(3f, 0f, 4f), Quaternion.Euler(0f, 90f, 0f));

            views.LocalPresentation!.OnActionPhase("skill.assist.healWave", ActionPresentationPhase.Channel, 0, 600);
            Assert.AreEqual(0, views.Vfx!.LiveCount);
            views.LocalPresentation.OnActionPhase("skill.assist.healWave", ActionPresentationPhase.Active, 14, 100);

            var effect = views.Vfx.Live.Single();
            Assert.AreEqual("vfx.healWave", effect.name);
            Assert.AreEqual(new Vector3(3f, 0f, 4f), effect.transform.position);
            Assert.AreEqual(90f, effect.transform.eulerAngles.y, 1e-3f);
            float lifetimeS = EditorPrefabSource.Find("Visuals/Characters/Assist")!.GetComponent<EntityVisualRig>().AnimationSet!
                .FindAction("skill.assist.healWave")!.vfxLifetimeMs / 1000f;
            views.Advance(lifetimeS - 0.1f);
            Assert.AreEqual(1, views.Vfx.LiveCount);
            views.Advance(0.2f);
            Assert.AreEqual(0, views.Vfx.LiveCount);
            views.ShowLocal(false);
        }

        [Test]
        public void An_action_effect_lasts_its_entrys_lifetime_and_an_entry_without_one_shows_nothing()
        {
            var source = new FakeSource();
            source.Prefabs["ring"] = Track(new GameObject("Ring"));
            var parent = Track(new GameObject("Effects")).transform;
            var at = Track(new GameObject("Caster")).transform;
            var vfx = new ActionVfx(new EntityVisualCatalog(new[] { Row("vfx.ring", "ring") }), source, parent);

            Assert.IsFalse(vfx.Play(new ActionClipEntry { actionId = "skill.none" }, at));
            Assert.IsFalse(vfx.Play(null, at));
            Assert.IsFalse(vfx.Play(new ActionClipEntry { vfxId = "vfx.nothing" }, at));
            Assert.IsTrue(vfx.Play(new ActionClipEntry { actionId = "skill.ring", vfxId = "vfx.ring", vfxLifetimeMs = 500f }, at));
            Assert.AreEqual(parent, vfx.Live.Single().transform.parent);

            vfx.Advance(0.4f);
            Assert.AreEqual(1, vfx.LiveCount);
            vfx.Advance(0.2f);
            Assert.AreEqual(0, vfx.LiveCount);
        }

        [Test]
        public void Views_show_the_capsule_while_loading_and_swap_in_the_art_when_it_finishes()
        {
            var source = new FakeSource();
            source.Prefabs["body"] = Track(new GameObject("Body"));
            source.Loading.Add("body");
            var views = Track(new GameObject("Views")).AddComponent<EntityViews>();
            views.UseVisuals(new EntityVisualCatalog(new[] { Row("hero", "body") }), source, TickMs);
            views.SetLocalVisual("hero");

            views.ShowLocal(true);
            Assert.IsNotNull(views.LocalView!.Find("Capsule"));

            source.Finish("body");
            views.Advance(0.016f);

            Assert.IsNull(views.LocalView.Find("Capsule"));
            Assert.IsNotNull(views.LocalView.Find("hero"));
            views.ShowLocal(false);
        }
    }
}
