#nullable enable
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Gameplay.Streaming
{
    /// <summary>
    /// Loads the authored world scene (the scene UnityExporter exports, spec 11.6) additively under the play scene, so the
    /// player walks on what the world authors built: the greybox of the first field zone now (WP-M2-7), its art in place
    /// later. Look only: the server simulates the exported files (<see cref="ClientWorld"/>), and the world scene's
    /// authoring components (markers, regions, blockers) do nothing at runtime. The scene brings its own light.
    /// No terrain streaming (R-2.3-04) yet: the zone is one scene; streaming in chunks comes with the main map.
    /// TODO(B4): the scene from the enter-world message's world id instead of the inspector (as <c>worldId</c> elsewhere).
    /// </summary>
    public sealed class WorldSceneLoader : MonoBehaviour
    {
        [Tooltip("Project path of the world scene (in the build settings), for example Assets/Scenes/M1Sample.unity.")]
        [SerializeField] private string scenePath = "Assets/Scenes/M1Sample.unity";

        public string ScenePath => scenePath;

        private void Awake()
        {
            if (string.IsNullOrEmpty(scenePath) || SceneManager.GetSceneByPath(scenePath).isLoaded)
                return;
#if UNITY_EDITOR
            // Works whether or not the scene is in the build settings.
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(scenePath, new LoadSceneParameters(LoadSceneMode.Additive));
#else
            SceneManager.LoadScene(scenePath, LoadSceneMode.Additive);
#endif
        }
    }
}
