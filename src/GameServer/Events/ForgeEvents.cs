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

/// <summary>
/// A round of a session's forge challenge closed (WP-M3-10, spec 5.3 M3 decision 12, 8.1): scored, missed, late, impossible
/// or dropped (a repeated press is not published). Published when it closes, before the session's result, for telemetry
/// (<c>ForgeHit</c>: the score, the press's lag and the outcome; spec 7.3 offline bot scoring). The round's parameters
/// follow from the session's <see cref="Seed"/> (logged again in <see cref="ForgeCompleted"/>) and <see cref="Round"/>.
/// <see cref="OpKey"/> is the session's reservation key, the operation key of its result. Not a job activity.
/// </summary>
public readonly record struct ForgeHitScored(long CharacterId, uint EntityId, int DefNetId, int Round, int Score, long LagMs,
    Shared.Protocol.ForgeHitOutcome Outcome, ulong Seed, string OpKey) : IGameEvent;

/// <summary>
/// A gem operation committed (WP-M3-9, spec 5.3, 8.1): published after its save, next to <see cref="GemSocketed"/> or
/// <see cref="GemRemoved"/>, for telemetry (<c>GemSocketed</c>/<c>GemRemoved</c> kinds: the seed of the socket roll or the
/// removal cost, rule 6). <see cref="Seed"/> is also in <c>craft_ops</c>. Not a job activity: it pays no XP.
/// </summary>
public readonly record struct GemCompleted(long CharacterId, uint EntityId, CraftOpKind Kind, int GemItemNetId, int Tier, long InstanceId,
    bool Success, int CostCount, ulong Seed, string OpKey) : IGameEvent;
