#nullable enable
using System;
using System.IO;
using Game.Gameplay.Entities;
using Game.Gameplay.Streaming;
using Shared.Simulation;
using Shared.World;
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
    /// <see cref="EntityViews"/>, the <see cref="FakeActionPhaseSource"/>, and the HUD (backlog I, <c>HudSceneInstaller</c>).
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

        // The world the dev scene predicts with (PredictionDevClient.worldId), and how far a reference block keeps from
        // its walls, region borders and markers.
        private const string WorldId = "m1";
        private const float BlockClearanceM = 3f;

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

            // The server's walls and region borders, drawn at runtime from the exported world (QA wave 3 F3).
            var worldView = new SerializedObject(new GameObject("World Geometry").AddComponent<WorldGeometryView>());
            worldView.FindProperty("worldId").stringValue = WorldId;
            worldView.FindProperty("material").objectReferenceValue = blockMaterial;
            worldView.ApplyModifiedPropertiesWithoutUndo();

            var open = OpenGround.Load(WorldId);
            var blocks = new GameObject("Reference Blocks").transform;
            for (int x = -BlockRings; x <= BlockRings; x++)
            {
                for (int z = -BlockRings; z <= BlockRings; z++)
                {
                    // Reference blocks are not in the server's world: keep them off its walls, borders and spawn points,
                    // so none passes for a wall or hides the player at the city spawn.
                    if ((x == 0 && z == 0) || !open.IsClear(x * BlockSpacingM, z * BlockSpacingM))
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

            // The HUD (backlog I) binds to the client's feed.
            Game.UI.Editor.HudSceneInstaller.Install(dev);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[DevSceneBuilder] wrote {ScenePath}");
        }

        /// <summary>Where the exported world has open ground: no blocked cell, no region border and no marker nearby.</summary>
        private sealed class OpenGround
        {
            [Serializable]
            private sealed class Marker
            {
                public int xCm;
                public int yCm;
            }

            [Serializable]
            private sealed class MarkerFile
            {
                public Marker[] markers = Array.Empty<Marker>();
            }

            private readonly WorldGrid _grid;
            private readonly Marker[] _markers;

            private OpenGround(WorldGrid grid, Marker[] markers)
            {
                _grid = grid;
                _markers = markers;
            }

            public static OpenGround Load(string worldId)
            {
                if (!ClientWorld.TryLoadGrid(worldId, out var grid, out var error) || grid == null)
                    throw new InvalidOperationException(error);
                string markers = File.ReadAllText(Path.Combine(ClientWorld.Directory(worldId), WorldFiles.Markers));
                return new OpenGround(grid, JsonUtility.FromJson<MarkerFile>(markers).markers);
            }

            public bool IsClear(float xM, float yM)
            {
                float clearanceCm = BlockClearanceM * WorldUnits.CmPerUnit;
                float xCm = xM * WorldUnits.CmPerUnit, yCm = yM * WorldUnits.CmPerUnit;
                foreach (var marker in _markers)
                {
                    if (Mathf.Abs(marker.xCm - xCm) <= clearanceCm && Mathf.Abs(marker.yCm - yCm) <= clearanceCm)
                        return false;
                }
                int centre = _grid.RegionIndexAt(FixVec2.FromCm((int)xCm, (int)yCm));
                for (float dy = -clearanceCm; dy <= clearanceCm; dy += _grid.CellSizeCm)
                {
                    for (float dx = -clearanceCm; dx <= clearanceCm; dx += _grid.CellSizeCm)
                    {
                        var at = FixVec2.FromCm((int)(xCm + dx), (int)(yCm + dy));
                        if (!_grid.IsWalkable(at) || _grid.RegionIndexAt(at) != centre)
                            return false;
                    }
                }
                return true;
            }
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
