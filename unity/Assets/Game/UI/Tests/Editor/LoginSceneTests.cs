using System.IO;
using System.Text.RegularExpressions;
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

        [TestCase(".server-cell")]
        [TestCase(".slot-card .login-text")]
        public void Labels_inside_row_and_card_buttons_are_left_aligned(string selector)
        {
            // QA wave 3 F7: Buttons center their text and the labels inside inherit it, so server cells sat off their
            // headers and a card's name line looked indented. The style sheet must set the alignment for them.
            string uss = File.ReadAllText(StyleSheetPath);
            var rule = Regex.Match(uss, "(^|\\})\\s*" + Regex.Escape(selector) + "\\s*\\{(?<body>[^}]*)\\}", RegexOptions.Multiline);
            Assert.That(rule.Success, Is.True, $"no rule '{selector}' in {StyleSheetPath}");
            Assert.That(rule.Groups["body"].Value, Does.Match("-unity-text-align:\\s*middle-left;"), selector);
        }

        private const string StyleSheetPath = "Assets/Game/UI/Login/Login.uss";
    }
}
