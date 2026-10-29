#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Game.Gameplay.Entities;
using UnityEngine;

namespace Game.UI.HUD
{
    /// <summary>
    /// The tutorial prompts a character has seen (R-12.3-03), kept on this machine: one file per character key under
    /// <c>Application.persistentDataPath/tutorial/</c>, one <c>UnlockSchedule</c> row id per line, appended the moment a prompt
    /// is first shown. A file that cannot be read or written only means prompts may show again (presentation, never game
    /// state), so failures are logged, not raised. User-approved WP-M2-10 plan: client-side, not a server flag.
    /// </summary>
    public sealed class TutorialSeenStore : ITutorialSeenStore
    {
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly string _path;

        public TutorialSeenStore(string directory, string characterKey)
        {
            _path = Path.Combine(directory, Safe(characterKey) + ".txt");
            try
            {
                if (File.Exists(_path))
                {
                    foreach (var line in File.ReadAllLines(_path))
                    {
                        if (line.Length > 0)
                            _seen.Add(line);
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[Tutorial] cannot read {_path}: {e.Message}");
            }
        }

        /// <summary>
        /// Set by <c>tools/qa/Run-QaSmoke.ps1</c> to the run's folder. Read in the editor only: a smoke run on its own
        /// databases (<c>-DbSuffix</c>) reuses character ids, so its seen prompts go under the run folder instead of being
        /// shared through <c>r&lt;realm&gt;c&lt;characterId&gt;</c> with another database's character (QA M2 wave 2 D4).
        /// </summary>
        public const string QaRunDirectoryVariable = "MMORPG_QA_RUN_DIR";

        /// <summary>The store of a character on this machine.</summary>
        public static TutorialSeenStore For(string characterKey)
        {
#if UNITY_EDITOR
            string? qaRun = Environment.GetEnvironmentVariable(QaRunDirectoryVariable);
#else
            string? qaRun = null; // a player build: one database per realm, so the key is unique
#endif
            return new TutorialSeenStore(DirectoryFor(qaRun, Application.persistentDataPath), characterKey);
        }

        /// <summary>
        /// <c>&lt;qaRunDirectory&gt;/tutorial</c> in a QA smoke run, otherwise <c>&lt;persistentDataPath&gt;/tutorial</c>.
        /// </summary>
        public static string DirectoryFor(string? qaRunDirectory, string persistentDataPath) =>
            Path.Combine(string.IsNullOrWhiteSpace(qaRunDirectory) ? persistentDataPath : qaRunDirectory!.Trim(), "tutorial");

        public string FilePath => _path;

        public bool IsSeen(string promptRowId) => _seen.Contains(promptRowId);

        public void MarkSeen(string promptRowId)
        {
            if (!_seen.Add(promptRowId))
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, promptRowId + "\n");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[Tutorial] cannot write {_path}: {e.Message}");
            }
        }

        /// <summary>A file name from a character key: letters, digits and '-' only.</summary>
        private static string Safe(string key)
        {
            var chars = key.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-')
                    chars[i] = '_';
            }
            return chars.Length == 0 ? "_" : new string(chars);
        }
    }
}
