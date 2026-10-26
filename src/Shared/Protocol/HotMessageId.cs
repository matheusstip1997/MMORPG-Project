using System;

namespace Shared.Protocol
{
    /// <summary>
    /// First byte of every hot message on the unreliable channel (spec 1.7, 11.2). The layouts are versioned by
    /// <see cref="ProtocolVersion"/>, which the handshake checks, so hot packets carry no version of their own.
    /// </summary>
    public enum HotMessageId : byte
    {
        None = 0,
        Input = 1,
        Snapshot = 2,
        /// <summary>Projectiles to draw (spec 3.2, backlog D3): <see cref="ProjectileSpawnWriter"/>.</summary>
        ProjectileSpawn = 3,
        /// <summary>The hits of a tick an observer can see (<see cref="HitMessage"/>, backlog D3).</summary>
        Hit = 4,
        /// <summary>Projectiles that ended, with the reason (spec 3.2): <see cref="ProjectileDespawnWriter"/>.</summary>
        ProjectileDespawn = 5,
        /// <summary>Ground areas to draw (spec 3.1, backlog D6): <see cref="GroundAreaSpawnWriter"/>.</summary>
        GroundAreaSpawn = 6,
        /// <summary>Ground areas that ended, with the reason: <see cref="GroundAreaDespawnWriter"/>.</summary>
        GroundAreaDespawn = 7,
    }

    public static class HotMessage
    {
        public const int IdBits = 8;

        public static bool TryPeekId(ReadOnlySpan<byte> source, out HotMessageId id)
        {
            id = source.Length > 0 ? (HotMessageId)source[0] : HotMessageId.None;
            return id == HotMessageId.Input || id == HotMessageId.Snapshot || id == HotMessageId.Hit
                || id == HotMessageId.ProjectileSpawn || id == HotMessageId.ProjectileDespawn
                || id == HotMessageId.GroundAreaSpawn || id == HotMessageId.GroundAreaDespawn;
        }
    }
}
