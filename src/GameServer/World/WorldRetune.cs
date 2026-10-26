using GameServer.AI;
using GameServer.Combat;
using Shared.Simulation.Actions;

namespace GameServer.World;

/// <summary>
/// The rules a dev hot reload (spec 11.7) prepared off the tick thread, from tables that passed every check: what
/// <see cref="GameWorld.ApplyRetune"/> swaps in at a tick boundary. Built from the same <c>ConfigSet</c>, so the action
/// catalog, combat rules, projectile rules and monster AI rules agree with each other.
/// </summary>
public sealed class RetunePlan
{
    private readonly TaskCompletionSource<uint> _applied = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RetunePlan(ActionCatalog actions, CombatRules combat, ProjectileRules? projectiles, MonsterAiRules? monsterAi,
        ulong hash, IReadOnlyList<string> changedTables)
    {
        Actions = actions;
        Combat = combat;
        Projectiles = projectiles;
        MonsterAi = monsterAi;
        Hash = hash;
        ChangedTables = changedTables;
    }

    public ActionCatalog Actions { get; }

    public CombatRules Combat { get; }

    public ProjectileRules? Projectiles { get; }

    public MonsterAiRules? MonsterAi { get; }

    /// <summary><c>Shared.Data.TableHash</c> of the Hot table files the plan was built from.</summary>
    public ulong Hash { get; }

    public IReadOnlyList<string> ChangedTables { get; }

    /// <summary>Completes with the tick the world swapped the plan in at (its first tick with the new rules).</summary>
    public Task<uint> Applied => _applied.Task;

    /// <summary>World thread: no I/O, the continuation runs on the thread pool.</summary>
    internal void MarkApplied(uint tick) => _applied.TrySetResult(tick);

    /// <summary>A newer plan replaced this one before the world took it.</summary>
    internal void MarkSuperseded() => _applied.TrySetCanceled();
}

/// <summary>
/// One slot between the reload thread and the world thread (spec 11.7): the reload posts its plan, the world takes it at
/// the top of its next tick. A newer plan replaces one the world has not taken yet. Without a plan, a tick pays one
/// volatile read.
/// </summary>
public sealed class RetuneMailbox
{
    private RetunePlan? _pending;

    /// <summary>Any thread.</summary>
    public void Post(RetunePlan plan) => Interlocked.Exchange(ref _pending, plan)?.MarkSuperseded();

    /// <summary>World thread, at a tick boundary.</summary>
    internal RetunePlan? Take() => Volatile.Read(ref _pending) == null ? null : Interlocked.Exchange(ref _pending, null);
}
