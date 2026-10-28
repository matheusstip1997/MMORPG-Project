#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace Game.UI.Config
{
    /// <summary>
    /// Client settings kept on this machine only (spec 9.6 "Client settings (local)": the shortcut row's collapsed state,
    /// later UI scale, opacity, chat tabs, the colorblind palette). Display choices, never game state.
    /// </summary>
    public interface ILocalSettings
    {
        bool GetBool(string key, bool fallback);

        void SetBool(string key, bool value);
    }

    /// <summary>The player's machine: Unity <c>PlayerPrefs</c>, written at once (a few keys, changed by a click).</summary>
    public sealed class PlayerPrefsLocalSettings : ILocalSettings
    {
        public bool GetBool(string key, bool fallback) => PlayerPrefs.GetInt(key, fallback ? 1 : 0) != 0;

        public void SetBool(string key, bool value)
        {
            PlayerPrefs.SetInt(key, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    /// <summary>In memory (tests and the QA harness: no file on the machine changes).</summary>
    public sealed class MemoryLocalSettings : ILocalSettings
    {
        private readonly Dictionary<string, bool> _values = new Dictionary<string, bool>();

        public bool GetBool(string key, bool fallback) => _values.TryGetValue(key, out bool value) ? value : fallback;

        public void SetBool(string key, bool value) => _values[key] = value;
    }

    /// <summary>The keys of the local settings (WP-M2-11).</summary>
    public static class LocalSettingKeys
    {
        /// <summary>The shortcut row is collapsed to its arrow (spec 9.3; default expanded).</summary>
        public const string ShortcutRowCollapsed = "mmorpg.hud.shortcutRow.collapsed";
    }
}
