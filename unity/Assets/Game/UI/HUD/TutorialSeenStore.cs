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

        /// <summary>The store of a character on this machine.</summary>
        public static TutorialSeenStore For(string characterKey) =>
            new TutorialSeenStore(Path.Combine(Application.persistentDataPath, "tutorial"), characterKey);

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
