#nullable enable
using Game.Gameplay.Dev;
using Game.Gameplay.Entities;
using NUnit.Framework;
using Shared.Data.Tables;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Tests.EditMode.Dev
{
    /// <summary>
    /// The dev tools of the dev scene (spec 11.7, backlog J): their own object next to the client, never under the HUD,
    /// hidden at start, linked to the client, and toggled by the dev rows of the Hotkeys table.
    /// </summary>
    public sealed class DevToolsSceneTests
    {
        private const string DevScene = "Assets/Game/Dev/PredictionDev.unity";

        [Test]
        public void The_dev_keys_come_from_the_hotkeys_table()
        {
            Assert.That(ClientConfig.TryLoad(out var config, out var error), Is.True, error);
            foreach (var (id, binding) in new[]
            {
                (Hotkeys.DevWorldGeometry, "<Keyboard>/f3"), (Hotkeys.DevHitboxes, "<Keyboard>/f4"),
                (Hotkeys.DevTimeline, "<Keyboard>/f5"), (Hotkeys.DevTickTiming, "<Keyboard>/f6"),
            })
            {
                using var key = DevKey.From(config, id);
                Assert.That(key.Binding, Is.EqualTo(binding), id);
            }
        }

        [Test]
        public void Committed_dev_scene_has_the_dev_tools_linked_and_hidden_outside_the_hud()
        {
            var previous = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.OpenScene(DevScene, OpenSceneMode.Single);
                var tools = GameObject.Find("Dev Tools");
                Assert.That(tools, Is.Not.Null, "Dev Tools in " + DevScene + " (rebuild it with DevSceneBuilder)");
                Assert.That(tools!.transform.parent, Is.Null, "the dev tools are not part of the HUD");
                var client = Object.FindAnyObjectByType<PredictionDevClient>();
                Assert.That(client, Is.Not.Null);

                AssertLinked<DevTelemetryFeed>(tools, "client", client!);
                AssertLinked<HitboxGizmoView>(tools, "client", client!);
                AssertLinked<ActionTimelineView>(tools, "client", client!);
                AssertLinked<DevTableReload>(tools, "client", client!);
                AssertLinked<TickTimingView>(tools, "client", client!);
                Assert.That(tools.GetComponent<HitboxGizmoView>().Shown, Is.False);
                Assert.That(tools.GetComponent<ActionTimelineView>().Shown, Is.False);
                Assert.That(tools.GetComponent<TickTimingView>().Shown, Is.False);
            }
            finally
            {
                if (previous.Length > 0)
                    EditorSceneManager.RestoreSceneManagerSetup(previous);
            }
        }

        private static void AssertLinked<T>(GameObject tools, string property, Object target) where T : Component
        {
            var component = tools.GetComponent<T>();
            Assert.That(component, Is.Not.Null, typeof(T).Name + " on Dev Tools");
            var link = new SerializedObject(component).FindProperty(property);
            Assert.That(link?.objectReferenceValue, Is.SameAs(target), typeof(T).Name + "." + property);
        }
    }
}
