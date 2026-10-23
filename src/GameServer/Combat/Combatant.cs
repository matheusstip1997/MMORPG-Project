using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Stats;

namespace GameServer.Combat;

/// <summary>Who an entity fights for (spec 3.1 HitResolver filters by faction). Append only.</summary>
public enum Faction : byte
{
    Player = 1,
    Monster = 2,
}

/// <summary>
/// The poise bar of R-3.3-01, in the shape spec 3.3 gives <c>PoiseSystem</c>: <c>{current, max, lastHitTick,
/// immuneUntilTick}</c>. Regeneration is lazy from <see cref="LastHitTick"/>; the rules (break, reset, stagger immunity
/// of <c>StartingValuesM1.staggerImmunityMs</c>) belong to PoiseSystem (backlog D4, WP4), not to this state.
/// </summary>
public struct PoiseState
{
    public int Current;
    public int Max;
    /// <summary>Tick of the last hit that dealt poise damage; 0 = never.</summary>
    public uint LastHitTick;
    /// <summary>No poise break before this tick (no chain stuns).</summary>
    public uint ImmuneUntilTick;

    public static PoiseState Full(int max) => new() { Current = max, Max = max };
}

/// <summary>
/// The combat state of one entity that can fight (backlog D5 wiring): stats, HP/MP/stamina, faction, alive or dead,
/// poise (R-3.3-01), flinch (R-3.3-07) and the position history lag compensation rewinds (R-3.1-03). Built by
/// <see cref="CombatRules"/> from a <c>ClassDef</c> (players) or a <c>MonsterDef</c> (monsters). Only state lives here:
/// HitResolver, PoiseSystem and FlinchSystem (WP4) change it. Owned by the world's simulation thread.
/// <para>
/// Times: the meters run on the world's simulation time in ms (<see cref="CombatRules.MsAt"/>); poise and flinch
/// timers are ticks.
/// </para>
/// </summary>
public sealed class Combatant
{
    private PoiseState _poise;

    internal Combatant(Faction faction, string templateId, Rank rank, ArmorClass armorClass, StatBlock stats, ResourceMeters meters,
        int poiseMax, int flinchResist, int flinchDurationPercent, int flinchImmunityMs, int bodyRadiusCm, int historyCapacity)
    {
        Faction = faction;
        TemplateId = templateId;
        Rank = rank;
        ArmorClass = armorClass;
        Stats = stats;
        Meters = meters;
        _poise = PoiseState.Full(poiseMax);
        FlinchResist = flinchResist;
        FlinchDurationPercent = flinchDurationPercent;
        FlinchImmunityMs = flinchImmunityMs;
        BodyRadiusCm = bodyRadiusCm;
        BodyRadiusRaw = FixedPoint.FromCm(bodyRadiusCm);
        History = new PositionHistory(historyCapacity);
    }

    public Faction Faction { get; }

    /// <summary>The <c>ClassDef</c> or <c>MonsterDef</c> id it was built from.</summary>
    public string TemplateId { get; }

    /// <summary>Steps 3 and 4 of the damage formula. Players: F until characters store a rank (TODO(open), spec 3.6).</summary>
    public Rank Rank { get; }

    /// <summary>
    /// R-3.3-05, R-3.5-08: from the class (players), which parry colors it can parry (R-3.3-04). Monsters are light: they do
    /// not parry in M1. TODO(E): a learned skill-tree passive can set another tier (R-3.5-08).
    /// </summary>
    public ArmorClass ArmorClass { get; }

    public StatBlock Stats { get; }

    /// <summary>HP, MP and stamina (R-3.4-03).</summary>
    public ResourceMeters Meters { get; }

    /// <summary>False from <see cref="Die"/> on. A dead monster is removed in phase 8 (see <c>SpawnController</c>).</summary>
    public bool IsAlive { get; private set; } = true;

    /// <summary>Tick of <see cref="Die"/>; meaningless while alive.</summary>
    public uint DiedAtTick { get; private set; }

    /// <summary>R-3.3-01, changed in place by PoiseSystem.</summary>
    public ref PoiseState Poise => ref _poise;

    /// <summary>R-3.3-07: further flinches are ignored before this tick.</summary>
    public uint FlinchImmuneUntilTick { get; set; }

    /// <summary>R-3.3-07: a hit flinches when its flinch power is at least this.</summary>
    public int FlinchResist { get; }

    /// <summary>R-3.3-07: a flinch lasts the hit's flinch ms times this / 100.</summary>
    public int FlinchDurationPercent { get; }

    /// <summary>R-3.3-07: flinch immunity after a flinch.</summary>
    public int FlinchImmunityMs { get; }

    /// <summary>Where the entity stood in recent ticks (R-3.1-03), recorded at the end of phase 2.</summary>
    public PositionHistory History { get; }

    /// <summary>The body circle hit shapes test against (spec 3.1), from <c>ClassDef</c> or <c>MonsterDef</c>.</summary>
    public int BodyRadiusCm { get; }

    /// <summary><see cref="BodyRadiusCm"/> in Q24.8 raw units (<see cref="FixedPoint"/>).</summary>
    public int BodyRadiusRaw { get; }

    /// <summary>Current HP; 0 while dead, so HP does not regenerate on a corpse (reads go through here, not the meter).</summary>
    public int Hp(long nowMs) => IsAlive ? Meters.Current(ResourceKind.Hp, nowMs) : 0;

    public int MaxHp(long nowMs) => Meters.Max(ResourceKind.Hp, nowMs);

    /// <summary>
    /// Marks the entity dead (HitResolver, when HP reaches 0; it publishes <c>Killed</c> and <c>Died</c>). Idempotent: a
    /// second call keeps the first tick. A dead monster leaves in phase 8; a dead player stays dead (TODO(open): player
    /// respawn, spec 9 "respawn prompt", is not defined yet).
    /// </summary>
    public void Die(uint tick)
    {
        if (!IsAlive)
            return;
        IsAlive = false;
        DiedAtTick = tick;
    }
}
