#nullable enable
using System.Linq;
using Game.Gameplay.Entities.Visuals;
using Game.Gameplay.Entities.Visuals.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests.EditMode.Entities.Visuals
{
    /// <summary>
    /// The clips of the visual prefab builder and of the animation sets it made: every take it names exists in its model,
    /// every clip a committed set references is an asset of a model, and the directional slots hold the placeholder mapping
    /// (humanoids: the forward loops reversed for backward, the sides left to the leg turn; monsters: forward only).
    /// </summary>
    public class VisualBuilderClipTests
    {
        private static readonly string[] Humanoids = { "warrior", "archer", "mage", "assist" };
        private static readonly string[] Monsters = { "demon", "alien", "cyclopsBoss" };

        [OneTimeSetUp]
        public void ImportFailedModelsAgain() => EntityVisualPrefabBuilder.ReimportFailedModels();

        [Test]
        public void Every_take_the_builder_names_exists_in_its_model()
        {
            var takes = EntityVisualPrefabBuilder.ReferencedTakes().Distinct().ToList();
            Assert.Greater(takes.Count, 0);
            foreach (var (model, take) in takes)
                Assert.DoesNotThrow(() => EntityVisualPrefabBuilder.Clip(model, take), $"{model}: {take}");
        }

        [Test]
        public void Every_clip_a_committed_set_references_is_a_model_asset()
        {
            foreach (var set in LocomotionGroundingTests.CommittedSets())
            {
                Assert.IsNotNull(set.idle, $"{set.name}: idle");
                Assert.IsNotNull(set.walk, $"{set.name}: walk");
                foreach (var clip in set.LocomotionClips().Concat(set.ActionLayerClips()))
                {
                    string path = AssetDatabase.GetAssetPath(clip);
                    Assert.IsNotEmpty(path, $"{set.name}: {clip.name} is not an asset");
                    Assert.IsTrue(AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Contains(clip),
                        $"{set.name}: {clip.name} is not in {path}");
                }
            }
        }

        [Test]
        public void Humanoids_back_up_on_their_forward_loops_reversed_and_turn_their_legs_for_the_sides()
        {
            foreach (var id in Humanoids)
            {
                var set = Set(id);
                Assert.AreSame(set.walk, set.walkBackward.clip, $"{id}: walk backward");
                Assert.AreSame(set.run, set.runBackward.clip, $"{id}: run backward");
                Assert.IsTrue(set.walkBackward.reverse && set.runBackward.reverse, $"{id}: the backward loops play in reverse");
                foreach (var side in new[] { set.walkLeft, set.walkRight, set.runLeft, set.runRight })
                    Assert.IsFalse(side.HasClip, $"{id}: the Standard packs have no side loops");
                Assert.Greater(set.legTurnMaxDeg, 0f, $"{id}: the leg turn covers the sides");
            }
        }

        [Test]
        public void Every_class_dodges_on_its_own_clip_not_the_attack_swing()
        {
            // Without an entry FindAction("dodge") fell back to the default action, so the dodge swung the weapon (PLACEHOLDER
            // roll since fix/floating-2). Its clip is a humanoid one, so the root-Y bake guard covers it.
            foreach (var id in Humanoids)
            {
                var set = Set(id);
                var dodge = set.actions.Find(a => a.actionId == "dodge");
                Assert.IsNotNull(dodge, $"{id}: no dodge entry");
                Assert.IsNotNull(dodge!.clip, $"{id}: the dodge has no clip");
                Assert.AreNotSame(set.defaultAction.clip, dodge.clip, $"{id}: the dodge plays the default action");
                Assert.IsTrue(dodge.clip!.humanMotion, $"{id}: the dodge clip is not humanoid");
                Assert.Less(dodge.startupEnd, dodge.activeEnd, $"{id}: the dodge's phase marks");
                Assert.AreSame(dodge, set.FindAction("dodge"), id);
            }
        }

        [Test]
        public void Monsters_are_forward_only()
        {
            foreach (var id in Monsters)
            {
                var set = Set(id);
                for (int direction = 1; direction < LocomotionProfile.Directions; direction++)
                {
                    Assert.IsFalse(set.Slot(true, direction)!.HasClip, $"{id}: walk {(LocomotionDirection)direction}");
                    Assert.IsFalse(set.Slot(false, direction)!.HasClip, $"{id}: run {(LocomotionDirection)direction}");
                }
                Assert.AreEqual(0f, set.legTurnMaxDeg, id);
            }
        }

        private static AnimationSet Set(string id)
        {
            Assert.IsTrue(LocomotionGroundingTests.Config().Catalog.TryGet(id, out var def), id);
            return EditorPrefabSource.Find(def.PrefabAddress)!.GetComponent<EntityVisualRig>().AnimationSet!;
        }
    }
}
