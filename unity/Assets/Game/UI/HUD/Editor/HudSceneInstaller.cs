#nullable enable
using Game.UI.HUD;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Editor
{
    /// <summary>
    /// Adds the HUD (backlog I) to a scene being built: a UIDocument with the HUD PanelSettings (scale with screen size,
    /// matched on height), the <see cref="HudRoot"/> with its styles, font and telegraph shader, wired to the world client.
    /// Called by <c>DevSceneBuilder</c>; scene builders own the scene YAML, nobody edits it by hand.
    /// </summary>
    public static class HudSceneInstaller
    {
        public const string Folder = "Assets/Game/UI/HUD";
        public const string PanelSettingsPath = Folder + "/HudPanelSettings.asset";
        public const string StylesPath = Folder + "/Hud.uss";
        public const string FontPath = "Assets/ThirdParty/Fonts/BarlowSemiCondensed/BarlowSemiCondensed-Regular.ttf";
        public const string TelegraphShaderName = "Universal Render Pipeline/Unlit";
        private const string ThemePath = "Assets/Game/UI/Login/LoginTheme.tss";

        // Defaults for the asset only; HudRoot applies HudSettings at runtime (spec 9.7: 1280 x 720, fit-height).
        private static readonly Vector2Int ReferenceResolution = new Vector2Int(1280, 720);

        /// <summary>Creates the HUD object; <paramref name="client"/> is the world client (an <c>IWorldClientFeedSource</c>).</summary>
        public static GameObject Install(MonoBehaviour client)
        {
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            var styles = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylesPath);
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            var shader = Shader.Find(TelegraphShaderName);
            if (theme == null || styles == null || font == null || shader == null)
                Debug.LogError($"[HudSceneInstaller] missing one of {ThemePath}, {StylesPath}, {FontPath}, shader {TelegraphShaderName}");

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

            var hud = new GameObject("HUD", typeof(UIDocument), typeof(HudRoot));
            // Through SerializedObject, as the login builder: the setters did not reach scenes saved in batch runs.
            var document = new SerializedObject(hud.GetComponent<UIDocument>());
            document.FindProperty("m_PanelSettings").objectReferenceValue = panel;
            document.ApplyModifiedPropertiesWithoutUndo();
            var root = new SerializedObject(hud.GetComponent<HudRoot>());
            root.FindProperty("styles").objectReferenceValue = styles;
            root.FindProperty("font").objectReferenceValue = font;
            root.FindProperty("telegraphShader").objectReferenceValue = shader;
            root.FindProperty("client").objectReferenceValue = client;
            root.ApplyModifiedPropertiesWithoutUndo();
            return hud;
        }

        /// <summary>The links the HUD needs, for the scene test (a scene that lost them never passes).</summary>
        public static bool HasReferences(HudRoot root, out string problem)
        {
            var document = root.GetComponent<UIDocument>();
            var serialized = new SerializedObject(root);
            problem = document == null || document.panelSettings == null ? "the HUD UIDocument has no PanelSettings"
                : serialized.FindProperty("styles").objectReferenceValue == null ? "HudRoot has no styles"
                : serialized.FindProperty("font").objectReferenceValue == null ? "HudRoot has no font"
                : serialized.FindProperty("telegraphShader").objectReferenceValue == null ? "HudRoot has no telegraph shader"
                : serialized.FindProperty("client").objectReferenceValue == null ? "HudRoot has no world client"
                : string.Empty;
            return problem.Length == 0;
        }
    }
}
