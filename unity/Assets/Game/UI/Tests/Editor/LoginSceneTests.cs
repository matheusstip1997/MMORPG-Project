using Game.UI.Editor;
using NUnit.Framework;
using UnityEditor.SceneManagement;

namespace Game.UI.Tests
{
    /// <summary>
    /// The committed login scene keeps its asset links. A scene saved without its PanelSettings shows only
    /// "The UIDocument has no PanelSettings" in Play mode, which no headless logic test notices.
    /// </summary>
    public class LoginSceneTests
    {
        [Test]
        public void Committed_login_scene_has_its_PanelSettings_theme_and_styles()
        {
            var previous = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                Assert.That(LoginSceneBuilder.HasReferences(LoginSceneBuilder.ScenePath, out var problem), Is.True, problem);
            }
            finally
            {
                if (previous.Length > 0)
                    EditorSceneManager.RestoreSceneManagerSetup(previous);
            }
        }
    }
}
