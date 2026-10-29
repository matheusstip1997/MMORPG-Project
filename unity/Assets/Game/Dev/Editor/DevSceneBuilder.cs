#nullable enable
using Game.Gameplay.Entities;
using Game.Gameplay.Streaming;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Gameplay.Dev.Editor
{
    /// <summary>
    /// Builds the D1 dev scene (<c>Assets/Game/Dev/PredictionDev.unity</c>): the authored world scene loaded at play
    /// (<see cref="WorldSceneLoader"/>: ground, walls, outposts and light; WP-M2-7), the 45-degree camera rig, the
    /// <see cref="PredictionDevClient"/> with its <see cref="EntityViews"/>, the <see cref="FakeActionPhaseSource"/>, the
    /// HUD (backlog I, <c>HudSceneInstaller</c>), and the dev tools (spec 11.7: hitbox gizmos, action timeline, tick timing, table reload) on their own object.
    /// Menu: MMORPG / Dev / Rebuild Prediction Dev Scene. Batch mode:
    /// <c>Unity.exe -batchmode -projectPath unity -executeMethod Game.Gameplay.Dev.Editor.DevSceneBuilder.Build -quit</c>.
    /// The scene is generated so nobody edits scene YAML by hand; edit this builder instead.
    /// </summary>
    public static class DevSceneBuilder
    {
        public const string Folder = "Assets/Game/Dev";
        public const string ScenePath = Folder + "/PredictionDev.unity";
        private const string BlockMaterialPath = Folder + "/DevBlock.mat";

        // The world the dev scene predicts with (PredictionDevClient.worldId) and the authored scene it shows (WP-M2-7).
        private const string WorldId = "m1";
        private const string WorldScenePath = "Assets/Scenes/M1Sample.unity";

        [MenuItem("MMORPG/Dev/Rebuild Prediction Dev Scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Trilight;

            // The authored world scene (its ground, walls, outposts and light), loaded at play (WP-M2-7). It replaced the dev
            // ground plane and its reference blocks, which only stood in for a world.
            var loader = new SerializedObject(new GameObject("World Scene").AddComponent<WorldSceneLoader>());
            loader.FindProperty("scenePath").stringValue = WorldScenePath;
            loader.ApplyModifiedPropertiesWithoutUndo();

            var blockMaterial = Material(BlockMaterialPath, new Color(0.75f, 0.72f, 0.65f));

            // The server's walls and region borders, drawn at runtime from the exported world (QA wave 3 F3).
            var worldView = new SerializedObject(new GameObject("World Geometry").AddComponent<WorldGeometryView>());
            worldView.FindProperty("worldId").stringValue = WorldId;
            worldView.FindProperty("material").objectReferenceValue = blockMaterial;
            worldView.ApplyModifiedPropertiesWithoutUndo();

            var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener), typeof(TopDownCameraRig));
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 12.7f, -12.7f), Quaternion.Euler(TopDownCameraRig.PitchDegrees, 0f, 0f));

            var client = new GameObject("Prediction Dev Client");
            var views = client.AddComponent<EntityViews>();
            var dev = client.AddComponent<PredictionDevClient>();
            var serialized = new SerializedObject(dev);
            serialized.FindProperty("cameraRig").objectReferenceValue = cameraObject.GetComponent<TopDownCameraRig>();
            serialized.FindProperty("views").objectReferenceValue = views;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // Plays scripted actions and reactions on the local character until D2's action source is wired.
            var fakeActions = new SerializedObject(client.AddComponent<FakeActionPhaseSource>());
            fakeActions.FindProperty("views").objectReferenceValue = views;
            fakeActions.ApplyModifiedPropertiesWithoutUndo();

            // The HUD (backlog I) binds to the client's feed.
            Game.UI.Editor.HudSceneInstaller.Install(dev);

            // Dev tools (spec 11.7, backlog J): hitbox gizmos (F4), action timeline (F5), tick timing (F6) and the client
            // side of the table hot reload. Their own object, never under the HUD; all start hidden.
            var tools = new GameObject("Dev Tools");
            var telemetry = tools.AddComponent<DevTelemetryFeed>();
            Link(telemetry, ("client", dev));
            Link(tools.AddComponent<HitboxGizmoView>(), ("client", dev), ("telemetry", telemetry), ("views", views));
            Link(tools.AddComponent<ActionTimelineView>(), ("client", dev));
            var reload = tools.AddComponent<DevTableReload>();
            Link(reload, ("client", dev), ("telemetry", telemetry));
            Link(tools.AddComponent<TickTimingView>(), ("client", dev), ("telemetry", telemetry), ("reload", reload));

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[DevSceneBuilder] wrote {ScenePath}");
        }

        private static void Link(Component component, params (string Property, UnityEngine.Object Target)[] links)
        {
            var serialized = new SerializedObject(component);
            foreach (var (property, target) in links)
                serialized.FindProperty(property).objectReferenceValue = target;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Material Material(string path, Color color)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                var shader = GraphicsSettings.defaultRenderPipeline != null
                    ? GraphicsSettings.defaultRenderPipeline.defaultShader
                    : Shader.Find("Standard");
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetColor(material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color", color);
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
