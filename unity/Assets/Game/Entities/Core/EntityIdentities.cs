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
        /// The entity shown with <paramref name="name"/> (ordinal: the name the partner posted is the server's own spelling),
        /// the lowest entity id when several were (one rule with <see cref="FindInView"/>). False when no entity was named so.
        /// The bots' pair partner (WP-M4-12, <c>BotStepContext.TryPartnerEntity</c>) then needs the entity in the snapshots.
        /// </summary>
        public bool TryFind(string name, out uint entityId)
        {
            entityId = 0;
            bool found = false;
            foreach (var pair in _names)
            {
                if (string.Equals(pair.Value, name, StringComparison.Ordinal) && (!found || pair.Key < entityId))
                {
                    entityId = pair.Key;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>
        /// The entity in <paramref name="inView"/> shown with <paramref name="name"/> (case-insensitive: what a player typed), the
        /// lowest entity id first, leaving out <paramref name="self"/>; 0 for none. Names stay after a despawn, so only entities in
        /// view count (the server checks again, rule 9). WP-M4-2 (the chat's <c>/trade</c>), moved here with the class by WP-M4-12.
        /// </summary>
        public uint FindInView(string name, IReadOnlyDictionary<uint, RemoteEntity> inView, uint self)
        {
            uint found = 0;
            foreach (var pair in _names)
            {
                if (pair.Key != self && (found == 0 || pair.Key < found) && inView.ContainsKey(pair.Key)
                    && string.Equals(pair.Value, name, StringComparison.OrdinalIgnoreCase))
                    found = pair.Key;
            }
            return found;
        }

        public void Clear() => _names.Clear();
    }
}
