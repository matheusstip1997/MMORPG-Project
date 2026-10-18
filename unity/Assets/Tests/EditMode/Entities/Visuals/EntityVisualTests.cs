#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Game.Gameplay.Entities;
using Game.Gameplay.Entities.Visuals;
using Game.Gameplay.Entities.Visuals.Editor;
using NUnit.Framework;
using Shared.Data.Tables;
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
        public void The_assist_holds_the_wand_right_and_the_shield_left()
        {
            var visual = Build("assist");

            Assert.IsNotNull(Bone(visual, HumanBodyBones.RightHand).Find("Wand"));
            Assert.IsNotNull(Bone(visual, HumanBodyBones.LeftHand).Find("RoundShield"));
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
