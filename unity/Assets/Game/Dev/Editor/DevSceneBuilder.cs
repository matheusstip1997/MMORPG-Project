#nullable enable
using Game.Gameplay.Entities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Game.Gameplay.Dev.Editor
{
    /// <summary>
    /// Builds the D1 dev scene (<c>Assets/Game/Dev/PredictionDev.unity</c>): light, a ground plane with reference
    /// blocks so movement is visible, the 45-degree camera rig, the <see cref="PredictionDevClient"/> with its
    /// <see cref="EntityViews"/>, and the <see cref="FakeActionPhaseSource"/>.
    /// Menu: MMORPG / Dev / Rebuild Prediction Dev Scene. Batch mode:
    /// <c>Unity.exe -batchmode -projectPath unity -executeMethod Game.Gameplay.Dev.Editor.DevSceneBuilder.Build -quit</c>.
    /// The scene is generated so nobody edits scene YAML by hand; edit this builder instead.
    /// </summary>
    public static class DevSceneBuilder
    {
        public const string Folder = "Assets/Game/Dev";
        public const string ScenePath = Folder + "/PredictionDev.unity";
        private const string GroundMaterialPath = Folder + "/DevGround.mat";
        private const string BlockMaterialPath = Folder + "/DevBlock.mat";

        // Dev scene layout, in metres: a 100 m square with a ring of blocks every 10 m (placeholders).
        private const float GroundSizeM = 100f;
        private const int BlockRings = 4;
        private const float BlockSpacingM = 10f;

        [MenuItem("MMORPG/Dev/Rebuild Prediction Dev Scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            RenderSettings.ambientMode = AmbientMode.Trilight;

            var groundMaterial = Material(GroundMaterialPath, new Color(0.32f, 0.36f, 0.3f));
            var blockMaterial = Material(BlockMaterialPath, new Color(0.75f, 0.72f, 0.65f));

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            // A Unity plane is 10 m wide at scale 1.
            ground.transform.localScale = new Vector3(GroundSizeM / 10f, 1f, GroundSizeM / 10f);
            ground.GetComponent<Renderer>().sharedMaterial = groundMaterial;

            var blocks = new GameObject("Reference Blocks").transform;
            for (int x = -BlockRings; x <= BlockRings; x++)
            {
                for (int z = -BlockRings; z <= BlockRings; z++)
                {
                    if (x == 0 && z == 0)
                        continue;
                    var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    block.name = $"Block {x},{z}";
                    block.transform.SetParent(blocks, false);
                    // Taller toward +z (world north, W at camera yaw 0) so the direction is readable at a glance.
                    float height = 0.5f + (z + BlockRings) * 0.25f;
                    block.transform.localPosition = new Vector3(x * BlockSpacingM, height / 2f, z * BlockSpacingM);
                    block.transform.localScale = new Vector3(1f, height, 1f);
                    block.GetComponent<Renderer>().sharedMaterial = blockMaterial;
                }
            }

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

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[DevSceneBuilder] wrote {ScenePath}");
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
