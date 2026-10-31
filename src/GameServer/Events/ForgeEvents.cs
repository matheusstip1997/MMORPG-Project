using GameServer.Persistence;

namespace GameServer.Events;

/// <summary>
/// A forge operation committed (WP-M3-6, spec 5.3, 8.1): published after its save, next to the job activity record
/// (<see cref="Crafted"/>, <see cref="Refined"/> or <see cref="Converted"/>), for telemetry (<c>ForgeOp</c>: the seed of
/// every random number of the operation, rule 6; anti-bot and the economy report). <see cref="Seed"/> is also in
/// <c>craft_ops</c>. Not a job activity: it pays no XP.
/// </summary>
public readonly record struct ForgeCompleted(long CharacterId, uint EntityId, CraftOpKind Kind, int DefNetId, int OutputItemNetId,
    int OutputCount, int Quality, byte Rarity, bool Success, ulong Seed, string OpKey) : IGameEvent;
