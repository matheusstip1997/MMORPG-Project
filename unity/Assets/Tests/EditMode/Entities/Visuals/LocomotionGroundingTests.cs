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

        internal static ConfigSetView Config()
        {
            Assert.IsTrue(ClientConfig.TryLoad(out var config, out var error), error);
            return new ConfigSetView(EntityVisualCatalog.From(config), config.Single<StartingValuesM1>().MoveSpeedCmPerSec);
        }

        internal readonly struct ConfigSetView
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

        // Yaw of a bone's forward axis, degrees clockwise from +Z (VRoid bones are world-aligned in the rest pose).
        private static float Yaw(Transform bone) => Mathf.Atan2(bone.forward.x, bone.forward.z) * Mathf.Rad2Deg;

        // The legs may point this far off where the leg turn puts them, and the chest this far off the facing (the clips sway).
        private const float TurnToleranceDeg = 25f;

        /// <summary>
        /// The body faces +Z (the mouse) and moves toward <paramref name="direction"/> of it (WASD, R-2.2-01): forward and
        /// backward walk along the facing (the backward loop replays the forward one in reverse), the sides turn the legs
        /// toward the movement while the chest keeps the facing. In every direction the stance foot stays planted.
        /// </summary>
        [TestCase("warrior", LocomotionDirection.Forward)]
        [TestCase("archer", LocomotionDirection.Forward)]
        [TestCase("mage", LocomotionDirection.Forward)]
        [TestCase("assist", LocomotionDirection.Forward)]
        [TestCase("warrior", LocomotionDirection.Backward)]
        [TestCase("archer", LocomotionDirection.Backward)]
        [TestCase("mage", LocomotionDirection.Backward)]
        [TestCase("assist", LocomotionDirection.Backward)]
        [TestCase("warrior", LocomotionDirection.Right)]
        [TestCase("archer", LocomotionDirection.Right)]
        [TestCase("mage", LocomotionDirection.Right)]
        [TestCase("assist", LocomotionDirection.Right)]
        [TestCase("warrior", LocomotionDirection.Left)]
        [TestCase("assist", LocomotionDirection.Left)]
        public void At_the_move_speed_a_humanoids_stance_foot_stays_planted_on_the_ground(string id, LocomotionDirection direction)
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
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var chest = animator.GetBoneTransform(HumanBodyBones.UpperChest) ?? animator.GetBoneTransform(HumanBodyBones.Chest);

            // Standing: how high the soles are when the body rests on the ground, and where the hips and chest point.
            for (int f = 0; f < 30; f++)
                visual.Update(FrameS, 0f, f / 3.0);
            float standing = Mathf.Min(feet[0].position.y, feet[1].position.y);
            float hipsStanding = Yaw(hips), chestStanding = Yaw(chest);

            // Moving at the player's speed (StartingValuesM1) for 3 s, the root facing +Z; the first second settles the blend.
            float speed = config.MoveSpeedCmPerSec;
            var move = direction switch
            {
                LocomotionDirection.Backward => new Vector2(-speed, 0f),
                LocomotionDirection.Right => new Vector2(0f, speed),
                LocomotionDirection.Left => new Vector2(0f, -speed),
                _ => new Vector2(speed, 0f),
            }; // (forward, right)
            const int frames = 180;
            var height = new float[2, frames];
            var groundStep = new float[2, frames];
            var previous = new[] { feet[0].position, feet[1].position };
            for (int f = 0; f < frames; f++)
            {
                root.position += new Vector3(move.y, 0f, move.x) / 100f * FrameS;
                visual.Update(FrameS, move.x, move.y, f / 3.0);
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
            Assert.Less(slideCmPerSec, MaxSlidePercentOfSpeed / 100f * speed, $"{id} {direction}: the stance foot slides at {slideCmPerSec:0} cm/s while the body moves at {speed} cm/s");
            Assert.AreEqual(Vector3.zero, animator.transform.localPosition, $"{id}: root motion moved the body off the entity root");

            // The legs turn toward a side movement (clockwise to the right), the chest stays on the facing.
            float legTurn = direction == LocomotionDirection.Right ? 90f : direction == LocomotionDirection.Left ? -90f : 0f;
            Assert.AreEqual(legTurn, visual.State!.LegTurnDeg, 1f, $"{id} {direction}: leg turn");
            Assert.AreEqual(0f, Mathf.DeltaAngle(hipsStanding + legTurn, Yaw(hips)), TurnToleranceDeg, $"{id} {direction}: the hips point along the legs");
            Assert.AreEqual(0f, Mathf.DeltaAngle(chestStanding, Yaw(chest)), TurnToleranceDeg, $"{id} {direction}: the chest keeps the facing");
        }

        // Baked meshes of the VRoid avatars sit up to this far from the contact points under the foot bones (shoe soles and
        // toe tips are mesh, not bones), so the lowest vertex may exceed the bone-based tolerance by this much.
        private const float SoleMeshMarginM = 0.01f;

        /// <summary>
        /// "The character floats" (second report, fix/floating-2): the Standard jog's authored flight lifted the lowest
        /// sole up to 18 cm above the ground at the player's speed, in about half of the frames. With the set's ground
        /// contact the lowest point of the body (the baked mesh) stays within the set's tolerance of the ground in every
        /// frame, at the walk and at the move speed (StartingValuesM1), in every direction.
        /// </summary>
        [TestCase("warrior")]
        [TestCase("archer")]
        [TestCase("mage")]
        [TestCase("assist")]
        public void While_moving_the_lowest_sole_stays_within_the_ground_contact_tolerance(string id)
        {
            var config = Config();
            float speed = config.MoveSpeedCmPerSec;
            float diagonal = speed * Mathf.Sqrt(0.5f);
            var moves = new (string Name, Vector2 Move)[] // (forward, right), cm/s
            {
                ("walk forward", new Vector2(150f, 0f)),
                ("jog forward", new Vector2(speed, 0f)),
                ("jog backward", new Vector2(-speed, 0f)),
                ("jog right", new Vector2(0f, speed)),
                ("jog left", new Vector2(0f, -speed)),
                ("jog forward-right", new Vector2(diagonal, diagonal)),
                ("jog backward-left", new Vector2(-diagonal, -diagonal)),
            };
            var mesh = new Mesh();
            _created.Add(mesh);
            var vertices = new List<Vector3>();
            foreach (var (name, move) in moves)
            {
                var visual = Build(config.Catalog, id, out var root);
                var set = visual.Instance.GetComponent<EntityVisualRig>().AnimationSet!;
                Assert.IsTrue(set.groundContact, $"{id}: {set.name} has ground contact off");
                var renderers = visual.Instance.GetComponentsInChildren<SkinnedMeshRenderer>();
                float tolerance = set.groundContactToleranceCm / 100f + SoleMeshMarginM;

                float highest = float.MinValue;
                int floating = 0;
                const int frames = 180;
                for (int f = 0; f < 60 + frames; f++)
                {
                    root.position += new Vector3(move.y, 0f, move.x) / 100f * FrameS;
                    visual.Update(FrameS, move.x, move.y, f / 3.0);
                    if (f < 60)
                        continue; // the blend settles
                    float sole = LowestVertex(renderers, mesh, vertices) - root.position.y;
                    highest = Mathf.Max(highest, sole);
                    if (sole > tolerance)
                        floating++;
                }
                Assert.AreEqual(0, floating,
                    $"{id} {name}: the lowest sole is more than {tolerance * 100f:0.0} cm above the ground in {floating} of {frames} frames (highest {highest * 100f:0.0} cm)");
                foreach (var visualToDispose in _visuals)
                    visualToDispose.Dispose();
                _visuals.Clear();
            }
        }

        private static float LowestVertex(SkinnedMeshRenderer[] renderers, Mesh mesh, List<Vector3> vertices)
        {
            float lowest = float.MaxValue;
            foreach (var renderer in renderers)
            {
                renderer.BakeMesh(mesh, true);
                mesh.GetVertices(vertices);
                var transform = renderer.transform;
                foreach (var vertex in vertices)
                    lowest = Mathf.Min(lowest, (transform.position + transform.rotation * vertex).y);
            }
            return lowest;
        }

        [Test]
        public void Ground_contact_leaves_actions_their_own_motion()
        {
            // The pull is weighted by the locomotion layer: once an action hides locomotion (the Archer's Dash rolls with
            // both feet off the ground), the hips are where its clip puts them, exactly as with ground contact off.
            var config = Config();
            Assert.IsTrue(config.Catalog.TryGet("archer", out var def));
            var prefab = EditorPrefabSource.Find(def.PrefabAddress)!;
            var rolls = new List<Transform>();
            foreach (bool groundContact in new[] { true, false })
            {
                var instance = Object.Instantiate(prefab);
                _created.Add(instance);
                var rig = instance.GetComponent<EntityVisualRig>();
                var set = Object.Instantiate(rig.AnimationSet!);
                _created.Add(set);
                set.groundContact = groundContact;
                rig.Configure(rig.Animator, set);
                var visual = new EntityVisual(instance, TickMs, 1f);
                _visuals.Add(visual);
                visual.Update(0f, 0f, nowTick: 0);
                visual.OnActionPhase("skill.archer.dash", ActionPresentationPhase.Active, 0, 600);
                rolls.Add(rig.Animator!.GetBoneTransform(HumanBodyBones.Hips));
            }

            for (int f = 0; f < 30; f++)
            {
                foreach (var visual in _visuals)
                    visual.Update(FrameS, 0f, f / 3.0);
                if (_visuals[0].State!.LocomotionWeight > 0f)
                    continue; // still fading in
                Assert.AreEqual(rolls[1].position.y, rolls[0].position.y, 1e-4f, $"frame {f}: ground contact moved the hips during the Dash");
            }
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
                for (int direction = 1; direction < LocomotionProfile.Directions; direction++)
                {
                    foreach (bool walk in new[] { true, false })
                    {
                        var slot = set.Slot(walk, direction)!;
                        if (!slot.HasClip)
                            continue;
                        float measured = EntityVisualPrefabBuilder.StanceSpeed(model, slot.clip!, 0f, (LocomotionDirection)direction, slot.reverse);
                        Assert.AreEqual(measured, slot.referenceSpeedCmPerSec, 1f, $"{id} {(walk ? "walk" : "run")} {(LocomotionDirection)direction}");
                    }
                }
            }
        }

        [Test]
        public void A_forward_loop_replayed_in_reverse_plants_at_the_forward_speed_when_backing()
        {
            var catalog = Config().Catalog;
            Assert.IsTrue(catalog.TryGet("warrior", out var def));
            var rig = EditorPrefabSource.Find(def.PrefabAddress)!.GetComponent<EntityVisualRig>();
            var set = rig.AnimationSet!;
            var model = PrefabSourceModel(rig);

            float backing = EntityVisualPrefabBuilder.StanceSpeed(model, set.walk!, 0f, LocomotionDirection.Backward, reverse: true);

            Assert.AreEqual(set.walkReferenceSpeedCmPerSec, backing, 1f);
            // Played forward while backing, the feet would go the wrong way: nothing plants.
            Assert.AreEqual(0f, EntityVisualPrefabBuilder.StanceSpeed(model, set.walk!, 0f, LocomotionDirection.Backward), 0f);
        }

        [Test]
        public void Only_bodies_with_humanoid_hips_and_spine_turn_their_legs()
        {
            var catalog = Config().Catalog;
            foreach (var id in new[] { "warrior", "archer", "mage", "assist", "demon", "alien", "cyclopsBoss" })
            {
                Assert.IsTrue(catalog.TryGet(id, out var def), id);
                var rig = EditorPrefabSource.Find(def.PrefabAddress)!.GetComponent<EntityVisualRig>();
                var set = rig.AnimationSet!;
                if (set.legTurnMaxDeg <= 0f)
                    continue;
                var animator = rig.Animator!;
                Assert.IsTrue(animator.isHuman, $"{id}: {set.name} turns the legs but the body is not a humanoid");
                Assert.IsNotNull(animator.GetBoneTransform(HumanBodyBones.Hips), $"{id}: no hips");
                Assert.IsNotNull(animator.GetBoneTransform(HumanBodyBones.Spine), $"{id}: no spine");
            }
        }

        /// <summary>The animation set of every committed body (characters and monsters).</summary>
        internal static List<AnimationSet> CommittedSets()
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

        /// <summary>
        /// Every animation set in the project: the committed bodies' and any other set asset (a new pack, a test set), so a
        /// guard over them covers a clip whatever set or library it comes from.
        /// </summary>
        internal static List<AnimationSet> AllSets()
        {
            var sets = CommittedSets();
            foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:" + nameof(AnimationSet)))
            {
                var set = UnityEditor.AssetDatabase.LoadAssetAtPath<AnimationSet>(UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                if (set != null && !sets.Contains(set))
                    sets.Add(set);
            }
            return sets;
        }

        [Test]
        public void Every_humanoid_clip_the_sets_use_has_root_Y_baked_into_its_pose()
        {
            // VisualAnimator plays with root motion off; a humanoid clip without "Bake Into Pose" root Y loses its body's
            // height curve and the feet float (P1). A replacement pack must bake it too, or go through the builder. Every
            // clip any set references counts (locomotion slots, reversed or not, actions, channel loops, reactions), from a
            // model's import settings or, for a standalone clip asset (a mirrored or edited copy), from its own settings.
            int checkedClips = 0;
            foreach (var set in AllSets())
            {
                var clips = set.LocomotionClips();
                clips.AddRange(set.ActionLayerClips());
                foreach (var clip in clips)
                {
                    if (!clip.humanMotion)
                        continue; // generic rigs (the monsters) have no humanoid root to bake
                    string path = UnityEditor.AssetDatabase.GetAssetPath(clip);
                    if (UnityEditor.AssetImporter.GetAtPath(path) is UnityEditor.ModelImporter importer)
                    {
                        var settings = System.Array.Find(importer.clipAnimations, c => c.name == clip.name);
                        Assert.IsNotNull(settings, $"{set.name}: {path} has no explicit settings for {clip.name} (run the visual prefab builder)");
                        Assert.IsTrue(settings!.lockRootHeightY, $"{set.name}: {clip.name} in {path} does not bake root Y into the pose");
                    }
                    else
                    {
                        // "Bake Into Pose" for root Y is loopBlendPositionY in a clip asset's own settings.
                        Assert.IsTrue(UnityEditor.AnimationUtility.GetAnimationClipSettings(clip).loopBlendPositionY,
                            $"{set.name}: the clip asset {clip.name} ({path}) does not bake root Y into the pose");
                    }
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
        public void Every_sets_locomotion_follows_its_rules()
        {
            foreach (var set in AllSets())
                Assert.IsNull(set.LocomotionError());
        }

        [TestCase(true, 2f, 30f, true)]
        [TestCase(false, 0f, 0f, true)]
        [TestCase(true, 2f, 0.5f, false)] // on, but it could not correct anything
        [TestCase(true, -1f, 30f, false)]
        [TestCase(true, 11f, 30f, false)]
        [TestCase(false, 2f, 51f, false)]
        public void Ground_contact_follows_its_rules(bool on, float toleranceCm, float maxCm, bool valid)
        {
            var set = Object.Instantiate(CommittedSets()[0]);
            _created.Add(set);
            set.groundContact = on;
            set.groundContactToleranceCm = toleranceCm;
            set.groundContactMaxCm = maxCm;

            string? error = set.LocomotionError();

            if (valid)
                Assert.IsNull(error);
            else
                StringAssert.Contains("groundContact", error);
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
