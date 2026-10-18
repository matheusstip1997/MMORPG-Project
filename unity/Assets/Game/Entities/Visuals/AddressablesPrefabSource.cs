#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>
    /// Loads visual prefabs through Addressables (spec 11.1), asynchronously, once per address, and keeps them until
    /// disposed. An address with no Addressables entry becomes <see cref="PrefabState.Missing"/> without an exception
    /// (the locations are looked up first).
    /// In the editor the default play mode script reads the AssetDatabase, so no content build is needed.
    /// TODO(build): player builds need the Addressables content built (Build > New Build > Default Build Script) before
    /// the player build; add it to the build pipeline with the client build job.
    /// </summary>
    public sealed class AddressablesPrefabSource : IVisualPrefabSource, IDisposable
    {
        private sealed class Entry
        {
            public PrefabState State = PrefabState.Loading;
            public GameObject? Prefab;
            public AsyncOperationHandle<GameObject> Handle;
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public event Action<string>? Finished;

        public PrefabState Get(string address, out GameObject? prefab)
        {
            if (!_entries.TryGetValue(address, out var entry))
            {
                entry = new Entry();
                _entries.Add(address, entry);
                Locate(address, entry);
            }
            prefab = entry.Prefab;
            return entry.State;
        }

        public void Dispose()
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.Handle.IsValid())
                    Addressables.Release(entry.Handle);
            }
            _entries.Clear();
        }

        private void Locate(string address, Entry entry)
        {
            var locate = Addressables.LoadResourceLocationsAsync(address, typeof(GameObject));
            locate.Completed += handle =>
            {
                IResourceLocation? location = handle.Status == AsyncOperationStatus.Succeeded && handle.Result.Count > 0 ? handle.Result[0] : null;
                Addressables.Release(handle);
                if (location == null)
                {
                    Finish(address, entry, null, $"no Addressables entry for '{address}'");
                    return;
                }
                entry.Handle = Addressables.LoadAssetAsync<GameObject>(location);
                entry.Handle.Completed += loaded => Finish(address, entry, loaded.Status == AsyncOperationStatus.Succeeded ? loaded.Result : null,
                    $"'{address}' failed to load: {loaded.OperationException?.Message}");
            };
        }

        private void Finish(string address, Entry entry, GameObject? prefab, string error)
        {
            if (!_entries.TryGetValue(address, out var current) || current != entry)
                return; // disposed meanwhile
            entry.Prefab = prefab;
            entry.State = prefab != null ? PrefabState.Ready : PrefabState.Missing;
            if (prefab == null)
                Debug.LogWarning($"[Visuals] {error}; showing the capsule placeholder.");
            Finished?.Invoke(address);
        }
    }
}
