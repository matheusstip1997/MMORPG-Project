using System;
using System.Collections.Generic;

namespace Shared.Data.Tables
{
    /// <summary>What an action is for (R-3.1-01). Config enum (camelCase names); append only.</summary>
    public enum ActionKind : byte
    {
        /// <summary>A class's normal attack: one action with a single strike (R-3.1-06).</summary>
        Attack = 1,
        Skill = 2,
        /// <summary>Never interrupted by a flinch (R-3.3-07).</summary>
        Dodge = 3,
        Parry = 4,
    }

    /// <summary>
    /// The pool an action spends one charge from when it starts (R-3.3-02, R-3.3-03; spec 3.3 <c>ChargeMeter</c>). Dodge
    /// and parry have separate pools; their sizes and recharge times are in <c>StartingValuesM1</c>. Config enum
    /// (camelCase names); append only.
    /// </summary>
    public enum ChargePool : byte
    {
        None = 0,
        Dodge = 1,
        Parry = 2,
    }

    /// <summary>
    /// The timed phases of an action, in the order they run (R-3.1-01, R-3.1-07): Startup, then Channel (only for
    /// channels), then Active, then Recovery. The values are the wire state codes of
    /// <c>Shared.Simulation.Actions.ActionState</c>. Config enum (camelCase names); never renumber.
    /// </summary>
    public enum ActionPhase : byte
    {
        Startup = 1,
        Channel = 2,
        Active = 3,
        Recovery = 4,
    }

    /// <summary>
    /// One action (spec 3.1 <c>ActionDef</c>, catalog 8.5): attack, skill, dodge, parry. Each phase lasts its ms rounded
    /// up to whole ticks (<c>ActionCatalog</c>); 0 skips the phase. What the action does is its effects
    /// (<see cref="ActionEffectDef"/>, R-3.1-07). Movement keys are ignored and facing is locked for the whole action
    /// (R-3.1-01, R-2.2-02), so there is no <c>movesDuring</c> field: actions that move use a Dash effect.
    /// <para>
    /// Starting an action spends its <see cref="StaminaCost"/> and one charge of its <see cref="ChargePool"/> (backlog D4,
    /// spec 3.1 and 3.3 implementation notes). An attack can carry a parry color, one against monsters' targets (PvE) and
    /// one in PvP (R-3.3-04, R-3.3-06). Hit shapes are in Strike effects (<see cref="HitShapeDef"/>). TODO(D6): <c>gaugeDelta</c>. TODO(D5): attack/cast speed scales the durations (R-3.1-02).
    /// TODO(F): monster attacks need <c>startupMs >= StartingValuesM1.MonsterAttackStartupMinMs</c> (R-3.1-03).
    /// </para>
    /// </summary>
    [Table("ActionDef", 1)]
    public sealed class ActionDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Highest <see cref="NetId"/>: snapshots send it in 12 bits (<c>Snapshot.ActionIdBits</c>).</summary>
        public const int MaxNetId = 4095;

        /// <summary>Stable id, for example <c>skill.crossCut</c>; visuals map it to animations.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Wire id in snapshots and <c>ActionResult</c>; unique, never reused for another action.</summary>
        [Range(1, MaxNetId)] public int NetId { get; set; }

        public ActionKind Kind { get; set; }

        [Range(0, 10_000)] public int StartupMs { get; set; }
        /// <summary>A channel must complete before the following effects fire (R-3.1-07); 0 = no channel.</summary>
        [Range(0, 60_000)] public int ChannelMs { get; set; }
        [Range(0, 10_000)] public int ActiveMs { get; set; }
        [Range(0, 10_000)] public int RecoveryMs { get; set; }

        /// <summary>From the start of the action until it can start again (R-3.1-02); 0 = none.</summary>
        [Range(0, 3_600_000)] public int CooldownMs { get; set; }

        /// <summary>
        /// A press this close to the end of this action is kept and fires when it ends (spec 2.2 input buffering);
        /// the newest press wins. 0 = no buffering.
        /// </summary>
        [Range(0, 10_000)] public int BufferWindowMs { get; set; }

        /// <summary>Actions whose press cancels this one and starts at once (R-3.1-01: which cancels which is data).</summary>
        [Ref(typeof(ActionDef))] public string[] CancelableBy { get; set; } = Array.Empty<string>();

        /// <summary>The cancels in <see cref="CancelableBy"/> are allowed from the start of this phase to the end of the action.</summary>
        public ActionPhase CancelFrom { get; set; } = ActionPhase.Startup;

        /// <summary>Phases where a flinch is ignored; a poise break still staggers (R-3.3-08).</summary>
        public ActionPhase[] SuperArmorPhases { get; set; } = Array.Empty<ActionPhase>();

        /// <summary>
        /// Stamina spent when the action starts (R-3.1-02); the press is refused with <c>NoStamina</c> when the entity has
        /// less. Attacks and skills (R-3.1-04) and dodges (R-3.3-02) must cost some.
        /// </summary>
        [Range(0, 10_000)] public int StaminaCost { get; set; }

        /// <summary>
        /// The pool one charge is spent from when the action starts, even if it later fails (R-3.3-03); the press is
        /// refused with <c>DodgeNoCharge</c> or <c>ParryNoCharge</c> when the pool is empty. A dodge uses <c>dodge</c>, a
        /// parry <c>parry</c>, every other action <c>none</c>.
        /// </summary>
        public ChargePool ChargePool { get; set; }

        /// <summary>Dodge only: start of the i-frame window in ms from the start of the action (R-3.3-02).</summary>
        [Range(0, 10_000)] public int IFramesStartMs { get; set; }

        /// <summary>
        /// Dodge only: length of the i-frame window (R-3.3-02: hits landing inside it are ignored). A dodge has one, and it
        /// lies inside the action. The server judges it on the defender's timeline (R-3.1-03, <c>DefenceTimeline</c>).
        /// </summary>
        [Range(0, 10_000)] public int IFramesMs { get; set; }

        /// <summary>
        /// Parry color of this attack when a monster uses it (R-3.3-04, R-3.3-06); <c>none</c> is uncolored (a parry
        /// reduces it). The telegraph carries it (R-3.1-05). Only attacks and skills with a Strike or Projectile have one.
        /// </summary>
        public ParryColor ParryClassPve { get; set; }

        /// <summary>Parry color of this attack when a player uses it on another player (R-3.3-06).</summary>
        public ParryColor ParryClassPvp { get; set; }

        /// <summary>The color for an attacker: monsters use <see cref="ParryClassPve"/>, players <see cref="ParryClassPvp"/>.</summary>
        public ParryColor ParryColorFor(bool attackerIsMonster) => attackerIsMonster ? ParryClassPve : ParryClassPvp;

        /// <summary>Configured length of the whole action in ms (a method: every public property is a table field).</summary>
        public int TotalMs() => StartupMs + ChannelMs + ActiveMs + RecoveryMs;

        /// <summary>Configured length of a phase in ms.</summary>
        public int PhaseMs(ActionPhase phase) => phase switch
        {
            ActionPhase.Startup => StartupMs,
            ActionPhase.Channel => ChannelMs,
            ActionPhase.Active => ActiveMs,
            ActionPhase.Recovery => RecoveryMs,
            _ => 0,
        };

        /// <summary>Ms from the start of the action to the start of a phase.</summary>
        public int PhaseStartMs(ActionPhase phase)
        {
            int start = 0;
            for (var p = ActionPhase.Startup; p < phase; p++)
                start += PhaseMs(p);
            return start;
        }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (TotalMs() <= 0)
                errors.Add(nameof(StartupMs), "an action must last at least one phase");
            if (BufferWindowMs > TotalMs())
                errors.Add(nameof(BufferWindowMs), "must not be longer than the action");
            if (!Enum.IsDefined(typeof(ActionKind), Kind))
                errors.Add(nameof(Kind), "unknown action kind");
            if (HasDuplicates(CancelableBy))
                errors.Add(nameof(CancelableBy), "lists an action twice");
            var seen = new HashSet<ActionPhase>();
            foreach (var phase in SuperArmorPhases)
            {
                if (!seen.Add(phase))
                    errors.Add(nameof(SuperArmorPhases), $"lists {phase} twice");
                else if (PhaseMs(phase) == 0)
                    errors.Add(nameof(SuperArmorPhases), $"{phase} lasts 0 ms in this action");
            }
            if (Kind == ActionKind.Dodge && SuperArmorPhases.Length > 0)
                errors.Add(nameof(SuperArmorPhases), "a dodge is never flinched anyway (R-3.3-07)");

            // R-3.1-04 (no heavy attacks: attacks and skills cost stamina), R-3.3-02 (so does a dodge).
            if (StaminaCost == 0 && (Kind == ActionKind.Attack || Kind == ActionKind.Skill || Kind == ActionKind.Dodge))
                errors.Add(nameof(StaminaCost), $"a {Kind} action must cost stamina (R-3.1-04, R-3.3-02)");

            // R-3.3-02, R-3.3-03: dodge and parry each spend from their own pool; nothing else spends charges.
            if (!Enum.IsDefined(typeof(ChargePool), ChargePool))
                errors.Add(nameof(ChargePool), "unknown charge pool");
            else if (Enum.IsDefined(typeof(ActionKind), Kind) && ChargePool != PoolOf(Kind))
                errors.Add(nameof(ChargePool), $"a {Kind} action uses the {PoolOf(Kind)} pool");

            // R-3.3-02: only a dodge has i-frames, and the window lies inside the action.
            if (Kind == ActionKind.Dodge)
            {
                if (IFramesMs == 0)
                    errors.Add(nameof(IFramesMs), "a dodge must have an i-frame window (R-3.3-02)");
                else if (IFramesStartMs + IFramesMs > TotalMs())
                    errors.Add(nameof(IFramesMs),
                        $"the i-frame window ends at {IFramesStartMs + IFramesMs} ms, after the action ({TotalMs()} ms)");
            }
            else if (IFramesStartMs != 0 || IFramesMs != 0)
            {
                errors.Add(nameof(IFramesMs), "only a dodge has i-frames (R-3.3-02)");
            }

            // R-3.3-04: colors exist only on attacks.
            CheckColor(nameof(ParryClassPve), ParryClassPve, errors);
            CheckColor(nameof(ParryClassPvp), ParryClassPvp, errors);
        }

        private void CheckColor(string field, ParryColor color, RowErrors errors)
        {
            if (!Enum.IsDefined(typeof(ParryColor), color))
                errors.Add(field, "unknown parry color");
            else if (color != ParryColor.None && Kind != ActionKind.Attack && Kind != ActionKind.Skill)
                errors.Add(field, $"a {Kind} action is not an attack and has no parry color (R-3.3-04)");
        }

        /// <summary>True when the action has a parry color for PvE or PvP.</summary>
        public bool HasParryColor() => ParryClassPve != ParryColor.None || ParryClassPvp != ParryColor.None;

        /// <summary>The charge pool an action of this kind spends from (R-3.3-02, R-3.3-03).</summary>
        public static ChargePool PoolOf(ActionKind kind) => kind switch
        {
            ActionKind.Dodge => ChargePool.Dodge,
            ActionKind.Parry => ChargePool.Parry,
            _ => ChargePool.None,
        };

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (!set.TryGet(typeof(ActionDef), out var table))
                return;
            foreach (var row in table.Rows)
            {
                var other = (ActionDef)row;
                if (!ReferenceEquals(other, this) && other.NetId == NetId)
                {
                    errors.Add(nameof(NetId), $"{NetId} is also used by '{other.Id}'");
                    break;
                }
            }

            // R-3.3-04: a color is parried on a hit, so a colored action must hit (a Strike or a Projectile effect).
            // (Other kinds are refused by the row rule already.)
            if (HasParryColor() && (Kind == ActionKind.Attack || Kind == ActionKind.Skill)
                && set.TryGet(typeof(ActionEffectDef), out var effects))
            {
                bool hits = false;
                foreach (var row in effects.Rows)
                {
                    var effect = (ActionEffectDef)row;
                    if (effect.ActionId == Id && (effect.Kind == EffectKind.Strike || effect.Kind == EffectKind.Projectile))
                    {
                        hits = true;
                        break;
                    }
                }
                if (!hits)
                    errors.Add(nameof(ParryClassPve), "a colored action needs a strike or projectile effect (R-3.3-04)");
            }
        }

        internal static bool HasDuplicates(string[] items)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (!seen.Add(item))
                    return true;
            }
            return false;
        }

        /// <summary>The row with this id, or null.</summary>
        public static ActionDef? Find(ConfigSet set, string id)
        {
            if (!set.TryGet(typeof(ActionDef), out var table))
                return null;
            foreach (var row in table.Rows)
            {
                var action = (ActionDef)row;
                if (action.Id == id)
                    return action;
            }
            return null;
        }
    }
}
