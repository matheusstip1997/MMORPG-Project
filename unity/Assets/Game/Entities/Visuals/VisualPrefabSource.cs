#nullable enable
using System;
using UnityEngine;

namespace Game.Gameplay.Entities.Visuals
{
    public enum PrefabState
    {
        Loading = 0,
        Ready = 1,

        /// <summary>No prefab at that address (or it failed to load): the view keeps the capsule.</summary>
        Missing = 2,
    }

    /// <summary>
    /// Where visual prefabs come from, by address (the <c>EntityVisualDef</c> addresses). The game uses
    /// <see cref="AddressablesPrefabSource"/> (spec 11.1); tests use an in-memory source.
    /// </summary>
    public interface IVisualPrefabSource
    {
        /// <summary>Raised once per address when its load finishes (ready or missing). May be raised inside <see cref="Get"/>.</summary>
        event Action<string>? Finished;

        /// <summary>The prefab's state; the first call for an address starts loading it.</summary>
        PrefabState Get(string address, out GameObject? prefab);
    }

    internal static class VisualObjects
    {
        /// <summary>Destroy in play mode, DestroyImmediate in edit mode (EditMode tests, editor tools).</summary>
        public static void Destroy(UnityEngine.Object target)
        {
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(target);
            else
                UnityEngine.Object.DestroyImmediate(target);
        }
    }
}
