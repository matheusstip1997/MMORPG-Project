#nullable enable
using System;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;

namespace Game.Gameplay.Entities.Visuals.Editor
{
    /// <summary>
    /// Editor-only <see cref="IVisualPrefabSource"/>: resolves addresses through the Addressables settings and loads the
    /// prefabs synchronously from the AssetDatabase (EditMode tests, editor tools). The game uses
    /// <see cref="AddressablesPrefabSource"/>.
    /// </summary>
    public sealed class EditorPrefabSource : IVisualPrefabSource
    {
        public event Action<string>? Finished;

        public PrefabState Get(string address, out GameObject? prefab)
        {
            prefab = Find(address);
            Finished?.Invoke(address);
            return prefab != null ? PrefabState.Ready : PrefabState.Missing;
        }

        /// <summary>The prefab registered under <paramref name="address"/>, or null.</summary>
        public static GameObject? Find(string address)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return null;
            foreach (var group in settings.groups)
            {
                if (group == null)
                    continue;
                foreach (var entry in group.entries)
                {
                    if (entry.address == address)
                        return AssetDatabase.LoadAssetAtPath<GameObject>(entry.AssetPath);
                }
            }
            return null;
        }
    }
}
