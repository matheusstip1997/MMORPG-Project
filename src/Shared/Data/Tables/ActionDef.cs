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
    /// TODO(D4): <c>staminaCost</c>, <c>chargeCost</c>, <c>iFrames</c>, <c>parryClassPve/Pvp</c>. TODO(D3): hit shapes
    /// (in Strike effects). TODO(D6): <c>gaugeDelta</c>. TODO(D5): attack/cast speed scales the durations (R-3.1-02).
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
        }

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
