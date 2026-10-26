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
    /// Where UI icon textures come from, by address (<c>ItemIconRegistry.iconAddress</c>, WP16). The game uses
    /// <see cref="AddressablesTextureSource"/>; tests use an in-memory source.
    /// </summary>
    public interface ITextureSource
    {
        /// <summary>Raised once per address when its load finishes (ready or missing). May be raised inside <see cref="Get"/>.</summary>
        event Action<string>? Finished;

        /// <summary>The texture's state; the first call for an address starts loading it.</summary>
        PrefabState Get(string address, out Texture2D? texture);
    }

    /// <summary>
    /// Loads icon textures through Addressables (spec 11.1), asynchronously, once per address, and keeps them until
    /// disposed, like <see cref="AddressablesPrefabSource"/> does for prefabs. An address with no Addressables entry becomes
    /// <see cref="PrefabState.Missing"/> without an exception, and the window keeps its placeholder icon.
    /// </summary>
    public sealed class AddressablesTextureSource : ITextureSource, IDisposable
    {
        private sealed class Entry
        {
            public PrefabState State = PrefabState.Loading;
            public Texture2D? Texture;
            public AsyncOperationHandle<Texture2D> Handle;
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public event Action<string>? Finished;

        public PrefabState Get(string address, out Texture2D? texture)
        {
            if (!_entries.TryGetValue(address, out var entry))
            {
                entry = new Entry();
                _entries.Add(address, entry);
                Locate(address, entry);
            }
            texture = entry.Texture;
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
            var locate = Addressables.LoadResourceLocationsAsync(address, typeof(Texture2D));
            locate.Completed += handle =>
            {
                IResourceLocation? location = handle.Status == AsyncOperationStatus.Succeeded && handle.Result.Count > 0 ? handle.Result[0] : null;
                Addressables.Release(handle);
                if (location == null)
                {
                    Finish(address, entry, null, $"no Addressables entry for '{address}'");
                    return;
                }
                entry.Handle = Addressables.LoadAssetAsync<Texture2D>(location);
                entry.Handle.Completed += loaded => Finish(address, entry, loaded.Status == AsyncOperationStatus.Succeeded ? loaded.Result : null,
                    $"'{address}' failed to load: {loaded.OperationException?.Message}");
            };
        }

        private void Finish(string address, Entry entry, Texture2D? texture, string error)
        {
            if (!_entries.TryGetValue(address, out var current) || current != entry)
                return; // disposed meanwhile
            entry.Texture = texture;
            entry.State = texture != null ? PrefabState.Ready : PrefabState.Missing;
            if (texture == null)
                Debug.LogWarning($"[UI] {error}; showing the placeholder icon.");
            Finished?.Invoke(address);
        }
    }
}
