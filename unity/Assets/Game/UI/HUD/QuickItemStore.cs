#nullable enable
using System;
using System.IO;
using Game.Gameplay.Entities;
using UnityEngine;

namespace Game.UI.HUD
{
    /// <summary>
    /// The quick-item binding of a character, kept on this machine (spec 3.4 consumable use, 9.13; WP-M3-3 plan: client only,
    /// like <see cref="TutorialSeenStore"/>): one file per character key under <c>Application.persistentDataPath/quickItem/</c>
    /// holding one <c>ItemDef</c> id. A file that cannot be read or written only means the binding is forgotten (presentation,
    /// never game state), so failures are logged, not raised.
    /// </summary>
    public sealed class QuickItemStore : IQuickItemStore
    {
        private readonly string _path;

        public QuickItemStore(string directory, string characterKey)
        {
            _path = Path.Combine(directory, Safe(characterKey) + ".txt");
        }

        /// <summary>The store of a character on this machine (a QA smoke run keeps it under its run folder, as the tutorial does).</summary>
        public static QuickItemStore For(string characterKey)
        {
#if UNITY_EDITOR
            string? qaRun = Environment.GetEnvironmentVariable(TutorialSeenStore.QaRunDirectoryVariable);
#else
            string? qaRun = null;
#endif
            string root = string.IsNullOrWhiteSpace(qaRun) ? Application.persistentDataPath : qaRun!.Trim();
            return new QuickItemStore(Path.Combine(root, "quickItem"), characterKey);
        }

        public string FilePath => _path;

        public string Load()
        {
            try
            {
                return File.Exists(_path) ? File.ReadAllText(_path).Trim() : string.Empty;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[QuickItem] cannot read {_path}: {e.Message}");
                return string.Empty;
            }
        }

        public void Save(string itemId)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, itemId ?? string.Empty);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[QuickItem] cannot write {_path}: {e.Message}");
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
