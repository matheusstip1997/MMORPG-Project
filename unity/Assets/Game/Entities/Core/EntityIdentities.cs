#nullable enable
using System;
using System.Collections.Generic;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// Player display names by entity id, from <see cref="EntityIdentity"/> (the server already applied
    /// <c>DisplayIdentity</c>, rule 8). Entity ids are never reused within a world, so a name never goes stale; the reliable
    /// name may arrive before or after the unreliable spawn, so names are kept until the connection ends, not removed on
    /// despawn. Engine free (moved from <c>WorldClientFeed.cs</c> by WP-M4-12 so ClientCore compiles it: the bots find their
    /// pair partner by its display name).
    /// </summary>
    public sealed class EntityIdentities
    {
        private readonly Dictionary<uint, string> _names = new Dictionary<uint, string>();

        public int Count => _names.Count;

        public void Set(uint entityId, string name) => _names[entityId] = name;

        public bool TryGet(uint entityId, out string name)
        {
            if (_names.TryGetValue(entityId, out var found))
            {
                name = found;
                return true;
            }
            name = string.Empty;
            return false;
        }

        /// <summary>
        /// The entity shown with <paramref name="name"/> (ordinal), the newest (highest id) when several were: a player who
        /// left and entered again has a new entity id. False when no entity was named so.
        /// </summary>
        public bool TryFind(string name, out uint entityId)
        {
            entityId = 0;
            bool found = false;
            foreach (var pair in _names)
            {
                if (string.Equals(pair.Value, name, StringComparison.Ordinal) && (!found || pair.Key > entityId))
                {
                    entityId = pair.Key;
                    found = true;
                }
            }
            return found;
        }

        public void Clear() => _names.Clear();
    }
}
