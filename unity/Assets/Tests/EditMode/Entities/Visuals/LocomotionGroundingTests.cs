#nullable enable
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.Gameplay.Entities.Visuals;
using Game.Gameplay.Entities.Visuals.Editor;
using NUnit.Framework;
using Shared.Data.Tables;
using UnityEngine;

namespace Game.Tests.EditMode.Entities.Visuals
{
    /// <summary>
    /// The real prefabs on the ground while they move (first play session P1, "the character floats"): the stance foot
    /// keeps up with the ground at the player's move speed, it touches the ground, and the reference speeds follow the
    /// row's scale. Driven through <see cref="EntityVisual"/> like <see cref="EntityViews"/> drives it, frame by frame.
    /// </summary>
    public class LocomotionGroundingTests
    {
        private const float TickMs = 50f;
        private const float FrameS = 1f / 60f;

        // A foot is in its stance within this of its lowest point; its world speed there is its slide.
        private const float StanceToleranceM = 0.02f;

        // The stance foot may slide at most this share of the body's speed (placeholder clips, see the test).
        private const float MaxSlidePercentOfSpeed = 25f;

        private readonly List<Object> _created = new List<Object>();
        private readonly List<EntityVisual> _visuals = new List<EntityVisual>();

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
                    Object.DestroyImmediate(created);
            }
            _created.Clear();
        }

        private static ConfigSetView Config()
        {
            Assert.IsTrue(ClientConfig.TryLoad(out var config, out var error), error);
            return new ConfigSetView(EntityVisualCatalog.From(config), config.Single<StartingValuesM1>().MoveSpeedCmPerSec);
        }

        private readonly struct ConfigSetView
        {
            public ConfigSetView(EntityVisualCatalog catalog, int moveSpeedCmPerSec)
            {
                Catalog = catalog;
                MoveSpeedCmPerSec = moveSpeedCmPerSec;
            }

            public EntityVisualCatalog Catalog { get; }
            public int MoveSpeedCmPerSec { get; }
        }

        private EntityVisual Build(EntityVisualCatalog catalog, string id, out Transform root)
        {
            Assert.IsTrue(catalog.TryGet(id, out var def), id);
            root = new GameObject(id).transform;
            _created.Add(root.gameObject);
            Assert.AreEqual(PrefabState.Ready, new EntityVisualFactory(new EditorPrefabSource(), TickMs).TryCreate(def, root, out var visual), id);
            _visuals.Add(visual!);
            return visual!;
        }

        private static Transform Sole(Animator animator, HumanBodyBones toes, HumanBodyBones foot)
        {
            var bone = animator.GetBoneTransform(toes);
            return bone != null ? bone : animator.GetBoneTransform(foot);
        }

        [TestCase("warrior")]
        [TestCase("archer")]
        [TestCase("mage")]
        [TestCase("assist")]
        public void At_the_move_speed_a_humanoids_stance_foot_stays_planted_on_the_ground(string id)
        {
            var config = Config();
            var visual = Build(config.Catalog, id, out var root);
            var animator = visual.Instance.GetComponent<EntityVisualRig>().Animator!;
            Assert.IsTrue(animator.isHuman, id);
            var feet = new[]
            {
                Sole(animator, HumanBodyBones.LeftToes, HumanBodyBones.LeftFoot),
                Sole(animator, HumanBodyBones.RightToes, HumanBodyBones.RightFoot),
            };

            // Standing: how high the soles are when the body rests on the ground.
            for (int f = 0; f < 30; f++)
                visual.Update(FrameS, 0f, f / 3.0);
            float standing = Mathf.Min(feet[0].position.y, feet[1].position.y);

            // Moving along +Z at the player's speed (StartingValuesM1) for 3 s; the first second settles the blend.
            float speed = config.MoveSpeedCmPerSec;
            const int frames = 180;
            var height = new float[2, frames];
            var groundStep = new float[2, frames];
            var previous = new[] { feet[0].position, feet[1].position };
            for (int f = 0; f < frames; f++)
            {
                root.position += new Vector3(0f, 0f, speed / 100f * FrameS);
                visual.Update(FrameS, speed, f / 3.0);
                for (int i = 0; i < 2; i++)
                {
                    var step = feet[i].position - previous[i];
                    step.y = 0f;
                    height[i, f] = feet[i].position.y;
                    groundStep[i, f] = step.magnitude;
                    previous[i] = feet[i].position;
                }
            }

            float lowest = float.MaxValue;
            double slideM = 0, stanceS = 0;
            for (int i = 0; i < 2; i++)
            {
                float footLowest = float.MaxValue;
                for (int f = 60; f < frames; f++)
                    footLowest = Mathf.Min(footLowest, height[i, f]);
                lowest = Mathf.Min(lowest, footLowest);
                for (int f = 61; f < frames; f++)
                {
                    if (height[i, f] > footLowest + StanceToleranceM || height[i, f - 1] > footLowest + StanceToleranceM)
                        continue;
                    slideM += groundStep[i, f];
                    stanceS += FrameS;
                }
            }
            Assert.LessOrEqual(lowest, standing + 0.01f, $"{id}: the feet never come down to the ground while moving (lowest sole {lowest * 100f:0.0} cm, standing {standing * 100f:0.0} cm)");
            Assert.Greater(stanceS, 0.1, $"{id}: no stance found");
            float slideCmPerSec = (float)(slideM / stanceS) * 100f;
            // Known placeholder limit (orchestrator, 2026-10-26): about 65 cm/s at 400 cm/s remain from cross-fading the walk
            // and jog takes; before the P1 fix the stance foot slid at 230-320 cm/s.
            Assert.Less(slideCmPerSec, MaxSlidePercentOfSpeed / 100f * speed, $"{id}: the stance foot slides at {slideCmPerSec:0} cm/s while the body moves at {speed} cm/s");
            Assert.AreEqual(Vector3.zero, animator.transform.localPosition, $"{id}: root motion moved the body off the entity root");
        }

        [Test]
        public void A_dash_clip_with_root_motion_never_moves_the_body_off_the_entity_root()
        {
            // The Archer's Dash plays Roll_RM, a take with forward root motion; the Dash effect alone moves the entity.
            var visual = Build(Config().Catalog, "archer", out _);
            var body = visual.Instance.GetComponent<EntityVisualRig>().Animator!.transform;
            visual.Update(0f, 0f, nowTick: 0);
            visual.OnActionPhase("skill.archer.dash", ActionPresentationPhase.Active, 0, 200);

            for (int f = 0; f < 12; f++)
                visual.Update(FrameS, 0f, f / 3.0);

            Assert.IsTrue(visual.State!.IsActionPlaying);
            Assert.AreEqual(Vector3.zero, body.localPosition);
            Assert.AreEqual(Quaternion.identity, body.localRotation);
        }

        [TestCase("cyclopsBoss")]
        [TestCase("demon")]
        public void The_reference_speeds_scale_with_the_rows_scale(string id)
        {
            // A body drawn at 170% covers 1.7 times the ground per step: at 1.7 times the set's walk speed the walk plays
            // at its own rate.
            var catalog = Config().Catalog;
            Assert.IsTrue(catalog.TryGet(id, out var def));
            var visual = Build(catalog, id, out _);
            var set = visual.Instance.GetComponent<EntityVisualRig>().AnimationSet!;
            float scale = def.ScalePercent / 100f;
            Assert.AreNotEqual(1f, scale, "the row is scaled");

            visual.Update(FrameS, set.walkReferenceSpeedCmPerSec * scale, 0);

            Assert.AreEqual(1f, visual.State!.Locomotion.Walk + visual.State.Locomotion.Run, 1e-4f);
            Assert.AreEqual(1f, visual.State.Locomotion.GaitRate, 1e-4f);
        }

        [Test]
        public void Each_humanoid_set_has_measured_reference_speeds()
        {
            // The builder measures them on the avatar (EntityVisualPrefabBuilder.StanceSpeed); the run is faster than the walk.
            var catalog = Config().Catalog;
            foreach (var id in new[] { "warrior", "archer", "mage", "assist" })
            {
                Assert.IsTrue(catalog.TryGet(id, out var def));
                var rig = EditorPrefabSource.Find(def.PrefabAddress)!.GetComponent<EntityVisualRig>();
                var set = rig.AnimationSet!;
                var model = PrefabSourceModel(rig);
                Assert.AreEqual(EntityVisualPrefabBuilder.StanceSpeed(model, set.walk!, 0f), set.walkReferenceSpeedCmPerSec, 1f, $"{id} walk");
                Assert.AreEqual(EntityVisualPrefabBuilder.StanceSpeed(model, set.run!, 0f), set.runReferenceSpeedCmPerSec, 1f, $"{id} run");
                Assert.Greater(set.runReferenceSpeedCmPerSec, set.walkReferenceSpeedCmPerSec, id);
            }
        }

        /// <summary>The animation set of every committed body (characters and monsters).</summary>
        private static List<AnimationSet> CommittedSets()
        {
            var catalog = Config().Catalog;
            var sets = new List<AnimationSet>();
            foreach (var id in new[] { "warrior", "archer", "mage", "assist", "demon", "alien", "cyclopsBoss" })
            {
                Assert.IsTrue(catalog.TryGet(id, out var def), id);
                var set = EditorPrefabSource.Find(def.PrefabAddress)!.GetComponent<EntityVisualRig>().AnimationSet!;
                if (!sets.Contains(set))
                    sets.Add(set);
            }
            return sets;
        }

        [Test]
        public void Every_humanoid_clip_the_sets_use_has_root_Y_baked_into_its_pose()
        {
            // VisualAnimator plays with root motion off; a humanoid clip without "Bake Into Pose" root Y loses its body's
            // height curve and the feet float (P1). A replacement pack must bake it too, or go through the builder.
            int checkedClips = 0;
            foreach (var set in CommittedSets())
            {
                var clips = new List<AnimationClip> { set.idle!, set.walk! };
                if (set.run != null)
                    clips.Add(set.run);
                clips.AddRange(set.ActionLayerClips());
                foreach (var clip in clips)
                {
                    if (!clip.humanMotion)
                        continue; // generic rigs (the monsters) have no humanoid root to bake
                    string path = UnityEditor.AssetDatabase.GetAssetPath(clip);
                    var importer = UnityEditor.AssetImporter.GetAtPath(path) as UnityEditor.ModelImporter;
                    Assert.IsNotNull(importer, $"{set.name}: {clip.name} does not come from a model");
                    var settings = System.Array.Find(importer!.clipAnimations, c => c.name == clip.name);
                    Assert.IsNotNull(settings, $"{set.name}: {path} has no explicit settings for {clip.name} (run the visual prefab builder)");
                    Assert.IsTrue(settings!.lockRootHeightY, $"{set.name}: {clip.name} in {path} does not bake root Y into the pose");
                    checkedClips++;
                }
            }
            Assert.Greater(checkedClips, 0);
        }

        [Test]
        public void Baking_root_Y_again_changes_nothing()
        {
            foreach (var library in EntityVisualPrefabBuilder.HumanoidLibraries)
                Assert.IsFalse(EntityVisualPrefabBuilder.BakeRootHeight(library), library);
        }

        [Test]
        public void Every_sets_reference_speeds_follow_their_rule()
        {
            foreach (var set in CommittedSets())
                Assert.IsNull(set.ReferenceSpeedError());
        }

        /// <summary>The avatar model the character prefab nests (its Animator's source prefab).</summary>
        private static GameObject PrefabSourceModel(EntityVisualRig rig)
        {
            var model = UnityEditor.PrefabUtility.GetCorrespondingObjectFromOriginalSource(rig.Animator!.gameObject);
            Assert.IsNotNull(model, rig.name);
            return model;
        }
    }
}
