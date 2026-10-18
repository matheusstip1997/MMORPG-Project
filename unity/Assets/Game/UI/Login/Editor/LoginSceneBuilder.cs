#nullable enable
using System.Collections.Generic;
using Game.UI.Login;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Editor
{
    /// <summary>
    /// Builds the login scene (<c>Assets/Game/UI/Login/Login.unity</c>, backlog B5): a camera, and a UIDocument with its
    /// PanelSettings (scale with screen size, matched on height) and the <see cref="LoginRoot"/>. It also puts the
    /// login scene first in the build settings, followed by the world scene. Menu: MMORPG / UI / Rebuild Login Scene.
    /// Batch mode: <c>Unity.exe -batchmode -projectPath unity -executeMethod Game.UI.Editor.LoginSceneBuilder.Build -quit</c>.
    /// The scene is generated so nobody edits scene YAML by hand; edit this builder instead.
    /// </summary>
    public static class LoginSceneBuilder
    {
        public const string Folder = "Assets/Game/UI/Login";
        public const string ScenePath = Folder + "/Login.unity";
        private const string PanelSettingsPath = Folder + "/LoginPanelSettings.asset";
        private const string ThemePath = Folder + "/LoginTheme.tss";
        private const string StylesPath = Folder + "/Login.uss";

        /// <summary>The world scene of M1 (LoginSettings.WorldScene), listed after the login scene in the build.</summary>
        private const string WorldScenePath = "Assets/Game/Dev/PredictionDev.unity";

        // Defaults for the asset only; LoginRoot applies LoginSettings at runtime (spec 9.10: 1280 x 720, fit-height).
        private static readonly Vector2Int ReferenceResolution = new Vector2Int(1280, 720);

        [MenuItem("MMORPG/UI/Rebuild Login Scene")]
        public static void Build()
        {
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            var styles = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylesPath);
            if (theme == null || styles == null)
            {
                Debug.LogError($"[LoginSceneBuilder] missing {ThemePath} or {StylesPath}");
                return;
            }

            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(panel, PanelSettingsPath);
            }
            panel.themeStyleSheet = theme;
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = ReferenceResolution;
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 1f;
            EditorUtility.SetDirty(panel);
            AssetDatabase.SaveAssets();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;

            var login = new GameObject("Login", typeof(UIDocument), typeof(LoginRoot));
            login.GetComponent<UIDocument>().panelSettings = panel;
            var root = new SerializedObject(login.GetComponent<LoginRoot>());
            root.FindProperty("styles").objectReferenceValue = styles;
            root.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddToBuildSettings();
            AssetDatabase.SaveAssets();
            Debug.Log($"[LoginSceneBuilder] wrote {ScenePath}");
        }

        private static void AddToBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>
            {
                new EditorBuildSettingsScene(ScenePath, true),
                new EditorBuildSettingsScene(WorldScenePath, true),
            };
            foreach (var existing in EditorBuildSettings.scenes)
            {
                if (existing.path != ScenePath && existing.path != WorldScenePath)
                    scenes.Add(existing);
            }
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
