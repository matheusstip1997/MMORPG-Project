using GameServer.Entities;
using Shared.Simulation;

namespace GameServer.AI;

/// <summary>What a monster's AI is doing (backlog F). Append only.</summary>
public enum MonsterMode : byte
{
    /// <summary>At home, looking for a player to aggro.</summary>
    Idle = 0,

    /// <summary>Has a target: walks to it and attacks it.</summary>
    Chase = 1,

    /// <summary>Lost its target or went past its leash: walks home ignoring players, then heals (leash reset).</summary>
    Return = 2,
}

/// <summary>
/// The AI state of one monster (backlog F), written by <see cref="MonsterAi"/> in phase 7 and read by phases 2 (the move)
/// and 3 (the attack request) of the next tick, the order of spec 1.6. Simulation thread only.
/// </summary>
public sealed class MonsterBrain
{
    public MonsterBrain(FixVec2 home, ushort homeFacing)
    {
        Home = home;
        HomeFacing = homeFacing;
    }

    /// <summary>Its spawn point: the leash is measured from here, and it walks back here.</summary>
    public FixVec2 Home { get; }

    public ushort HomeFacing { get; }

    public MonsterMode Mode { get; internal set; }

    /// <summary>The player it chases; null unless <see cref="MonsterMode.Chase"/>.</summary>
    public Player? Target { get; internal set; }

    /// <summary>
    /// A player that damaged it (entity id, 0 = none), set from the <c>Damaged</c> event; an idle monster takes it as its
    /// target at its next think if it can see that player (user decision 2026-10-22). Ignored while chasing or going home.
    /// </summary>
    public uint RetaliateId { get; internal set; }

    // The intent of the last think, used once by the next tick's phases 2 and 3.

    /// <summary>Phase 2 turns to <see cref="Facing"/>, and walks along it when <see cref="Walks"/>.</summary>
    internal bool Turns { get; set; }

    internal bool Walks { get; set; }

    internal ushort Facing { get; set; }

    /// <summary>Catalog index of the attack phase 3 starts, aimed at <see cref="Aim"/>; -1 for none.</summary>
    internal int RequestedAction { get; set; } = -1;

    internal ushort Aim { get; set; }

    // Return: the closest it got to home (cm squared) and when, for the leash timeout.
    internal long BestHomeDistanceSq { get; set; }

    internal uint LastProgressTick { get; set; }

    internal void ClearIntent()
    {
        Turns = false;
        Walks = false;
        RequestedAction = -1;
    }
}
