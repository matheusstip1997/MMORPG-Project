namespace GameServer.Events;

// Session events (spec 2.1, 8.1), emitted by Net. The envelope's ActorCharacterId is the session's character.

/// <summary>A character entered the world, or reattached to its lingering entity (R-2.1-08).</summary>
public readonly record struct SessionStarted(long AccountId, uint EntityId, bool Reattached) : IGameEvent;

/// <summary>
/// A session ended. On <see cref="SessionEndReason.Disconnected"/> the entity may still linger in the world (R-2.1-08);
/// its removal is not a session event.
/// </summary>
public readonly record struct SessionEnded(long AccountId, uint EntityId, SessionEndReason Reason) : IGameEvent;

/// <summary>Why a session ended. Values are stored in telemetry: append only, never renumber.</summary>
public enum SessionEndReason : byte
{
    /// <summary>The player logged out (allowed only outside combat, R-2.1-07).</summary>
    LoggedOut = 1,
    /// <summary>The connection dropped; the character lingers (R-2.1-08).</summary>
    Disconnected = 2,
    /// <summary>A new login for the same account replaced this session (R-2.1-04).</summary>
    Replaced = 3,
    /// <summary>Graceful shutdown disconnected everyone (R-2.1-09).</summary>
    ServerShutdown = 4,
    /// <summary>
    /// This server lost the character's ownership lock, or a save found another writer (R-2.1-06); the character was
    /// removed without saving.
    /// </summary>
    OwnershipLost = 5,
    /// <summary>An admin kicked the client (spec 7.4). Like a disconnect, the character lingers (R-2.1-08).</summary>
    Kicked = 6,
    // TODO(open): ban reasons arrive with bans (spec 7.4); add them here when they are defined.
}

/// <summary>
/// A sampled position of a moving entity, for telemetry and bot scoring only (spec 2.2, 8.1).
/// Position in whole cm on the 2D movement plane; facing as the 16-bit binary angle of <c>MovementSim</c>.
/// TODO(open): the sampling interval is not in the spec yet; it becomes a config value when the producer is built.
/// </summary>
public readonly record struct Moved(uint EntityId, int XCm, int YCm, ushort Facing) : IGameEvent;
