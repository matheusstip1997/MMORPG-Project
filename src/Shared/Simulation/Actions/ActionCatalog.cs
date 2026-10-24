using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Simulation.Actions
{
    /// <summary>
    /// The action tables turned into ticks once, for the <see cref="ActionMachine"/> (server phase 3 and client prediction
    /// build it from the same tables, so both run the same timings). Read-only after construction; shared by every
    /// entity of a world.
    /// <para>
    /// Rounding: each phase lasts its ms rounded up to whole ticks, so a phase with any ms lasts at least one tick
    /// (a 500 ms dodge is 10 ticks at 20 Hz, a 30 ms phase is 1 tick). An effect fires on the first tick at or after its
    /// offset, kept inside its phase. Cooldowns, buffer windows and charge recharge times round up too; the i-frame window
    /// starts at its start ms rounded up and lasts its ms rounded up, kept inside the action.
    /// </para>
    /// </summary>
    public sealed class ActionCatalog
    {
        /// <summary>No actions and no bindings: presses do nothing (worlds and tests that do not load the action tables).</summary>
        public static readonly ActionCatalog Empty = new ActionCatalog(1, Array.Empty<ActionDef>(), Array.Empty<ActionEffectDef>(),
            Array.Empty<ActionBindings>());

        private readonly ActionTiming[] _actions;
        private readonly Dictionary<int, int> _indexByNetId = new Dictionary<int, int>();
        private readonly ActionLoadout[] _loadouts;

        /// <summary>A catalog with one bindings row (tests, tools); <paramref name="bindings"/> null binds nothing.</summary>
        public ActionCatalog(int tickRateHz, IReadOnlyList<ActionDef> actions, IReadOnlyList<ActionEffectDef> effects,
            ActionBindings? bindings, ChargePoolConfig dodgeCharges = default, ChargePoolConfig parryCharges = default)
            : this(tickRateHz, actions, effects, bindings != null ? new[] { bindings } : Array.Empty<ActionBindings>(),
                dodgeCharges, parryCharges)
        {
        }

        /// <param name="effects">Every effect row; rows of actions not in <paramref name="actions"/> are ignored.</param>
        /// <param name="bindings">Which action each input starts, one row per class (backlog E).</param>
        /// <param name="dodgeCharges">The dodge pool (R-3.3-02); required when an action uses it.</param>
        /// <param name="parryCharges">The parry pool (R-3.3-03); required when an action uses it.</param>
        public ActionCatalog(int tickRateHz, IReadOnlyList<ActionDef> actions, IReadOnlyList<ActionEffectDef> effects,
            IReadOnlyList<ActionBindings> bindings, ChargePoolConfig dodgeCharges = default, ChargePoolConfig parryCharges = default)
        {
            if (tickRateHz <= 0)
                throw new ArgumentOutOfRangeException(nameof(tickRateHz), "must be positive");
            TickRateHz = tickRateHz;
            DodgeCharges = dodgeCharges;
            ParryCharges = parryCharges;
            foreach (var action in actions)
            {
                if (action.ChargePool != ChargePool.None && PoolConfig(action.ChargePool).Max <= 0)
                    throw new ArgumentException($"'{action.Id}' uses the {action.ChargePool} pool, which has no charges.", nameof(actions));
            }
            _actions = new ActionTiming[actions.Count];
            var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < actions.Count; i++)
            {
                indexById.Add(actions[i].Id, i);
                _indexByNetId.Add(actions[i].NetId, i);
            }
            for (int i = 0; i < actions.Count; i++)
                _actions[i] = new ActionTiming(this, i, actions[i], EffectsOf(actions[i].Id, effects), indexById);

            _loadouts = new ActionLoadout[bindings.Count];
            for (int i = 0; i < bindings.Count; i++)
                _loadouts[i] = new ActionLoadout(bindings[i], indexById);
        }

        /// <summary>Builds the catalog from validated tables: ActionDef, ActionEffectDef, ActionBindings, StartingValuesM1.</summary>
        public static ActionCatalog From(ConfigSet set)
        {
            var actions = Rows<ActionDef>(set);
            var effects = Rows<ActionEffectDef>(set);
            var values = set.Single<StartingValuesM1>();
            return new ActionCatalog(values.TickRateHz, actions, effects, Rows<ActionBindings>(set),
                new ChargePoolConfig(values.DodgeCharges, values.DodgeRechargeMs),
                new ChargePoolConfig(values.ParryCharges, values.ParryRechargeMs));
        }

        public int TickRateHz { get; }

        /// <summary>Size and recharge time of the dodge pool (spec 12.7: 3 charges, 1 per 4 s).</summary>
        public ChargePoolConfig DodgeCharges { get; }

        /// <summary>Size and recharge time of the parry pool (spec 12.7: 3 charges, 1 per 5 s).</summary>
        public ChargePoolConfig ParryCharges { get; }

        public ChargePoolConfig PoolConfig(ChargePool pool) => pool switch
        {
            ChargePool.Dodge => DodgeCharges,
            ChargePool.Parry => ParryCharges,
            _ => default,
        };

        /// <summary>A full meter of a pool, in ticks of this catalog.</summary>
        public ChargeMeter NewMeter(ChargePool pool)
        {
            var config = PoolConfig(pool);
            return new ChargeMeter(Math.Max(0, config.Max), TicksFromMs(config.RechargeMs));
        }

        public int Count => _actions.Length;

        public ActionTiming this[int index] => _actions[index];

        /// <summary>
        /// The bindings of a class (its <c>ActionBindings</c> row, spec 9.6): the validated tables have one per
        /// <c>ClassDef</c> row. A catalog with a single row (tests, tools) gives it to every class and to
        /// <paramref name="classId"/> null; otherwise an unknown class gets <see cref="ActionLoadout.None"/>.
        /// </summary>
        public ActionLoadout Loadout(string? classId)
        {
            foreach (var loadout in _loadouts)
            {
                if (classId != null && loadout.ClassId == classId)
                    return loadout;
            }
            return _loadouts.Length == 1 ? _loadouts[0] : ActionLoadout.None;
        }

        public bool TryGetIndex(int netId, out int index) => _indexByNetId.TryGetValue(netId, out index);

        /// <summary>The action with this <see cref="ActionDef.Id"/>, or null.</summary>
        public ActionTiming? Find(string id)
        {
            foreach (var action in _actions)
            {
                if (action.Id == id)
                    return action;
            }
            return null;
        }

        /// <summary>Ms rounded up to whole ticks.</summary>
        public int TicksFromMs(int ms) => ms <= 0 ? 0 : (int)(((long)ms * TickRateHz + 999) / 1000);

        /// <summary>Whole ticks in ms (for visuals: phase lengths as the simulation runs them).</summary>
        public int MsFromTicks(int ticks) => (int)((long)ticks * 1000 / TickRateHz);

        private static List<ActionEffectDef> EffectsOf(string actionId, IReadOnlyList<ActionEffectDef> effects)
        {
            var list = new List<ActionEffectDef>();
            foreach (var effect in effects)
            {
                if (effect.ActionId == actionId)
                    list.Add(effect);
            }
            if (list.Count > ActionEffectDef.MaxEffectsPerAction)
                throw new ArgumentException($"'{actionId}' has more than {ActionEffectDef.MaxEffectsPerAction} effects.", nameof(effects));
            return list;
        }

        private static List<T> Rows<T>(ConfigSet set)
        {
            var list = new List<T>();
            if (set.TryGet(typeof(T), out var table))
            {
                foreach (var row in table.Rows)
                    list.Add((T)row);
            }
            return list;
        }
    }

    /// <summary>Size and recharge time of a charge pool (spec 3.3, from <c>StartingValuesM1</c>).</summary>
    public readonly struct ChargePoolConfig
    {
        public ChargePoolConfig(int max, int rechargeMs)
        {
            Max = max;
            RechargeMs = rechargeMs;
        }

        public int Max { get; }

        /// <summary>Ms per charge; 0 keeps the pool full.</summary>
        public int RechargeMs { get; }
    }

    /// <summary>One action of an <see cref="ActionCatalog"/> in ticks. Phase ends are ticks since the action started.</summary>
    public sealed class ActionTiming
    {
        private readonly int[] _cancelableBy;
        private readonly byte _superArmorMask;

        internal ActionTiming(ActionCatalog catalog, int index, ActionDef def, List<ActionEffectDef> effects,
            Dictionary<string, int> indexById)
        {
            Index = index;
            Def = def;
            StartupEnd = catalog.TicksFromMs(def.StartupMs);
            ChannelEnd = StartupEnd + catalog.TicksFromMs(def.ChannelMs);
            ActiveEnd = ChannelEnd + catalog.TicksFromMs(def.ActiveMs);
            TotalTicks = ActiveEnd + catalog.TicksFromMs(def.RecoveryMs);
            CooldownTicks = catalog.TicksFromMs(def.CooldownMs);
            BufferWindowTicks = catalog.TicksFromMs(def.BufferWindowMs);
            if (def.IFramesMs > 0 && TotalTicks > 0)
            {
                IFramesStart = Math.Min(catalog.TicksFromMs(def.IFramesStartMs), TotalTicks - 1);
                IFramesEnd = Math.Min(IFramesStart + catalog.TicksFromMs(def.IFramesMs), TotalTicks);
            }

            var cancels = new List<int>();
            foreach (var id in def.CancelableBy)
            {
                if (indexById.TryGetValue(id, out int other))
                    cancels.Add(other);
            }
            _cancelableBy = cancels.ToArray();
            foreach (var phase in def.SuperArmorPhases)
                _superArmorMask |= (byte)(1 << (int)phase);

            Effects = new EffectTiming[effects.Count];
            for (int i = 0; i < effects.Count; i++)
                Effects[i] = new EffectTiming(catalog, this, i, effects[i]);
        }

        /// <summary>Position in its catalog.</summary>
        public int Index { get; }

        public ActionDef Def { get; }

        public string Id => Def.Id;

        public int NetId => Def.NetId;

        public ActionKind Kind => Def.Kind;

        public int StartupEnd { get; }

        public int ChannelEnd { get; }

        public int ActiveEnd { get; }

        /// <summary>Ticks the action lasts; it is over when its elapsed ticks reach this.</summary>
        public int TotalTicks { get; }

        public int CooldownTicks { get; }

        public int BufferWindowTicks { get; }

        /// <summary>Stamina spent when the action starts (R-3.1-02).</summary>
        public int StaminaCost => Def.StaminaCost;

        /// <summary>MP spent when the action starts (R-3.1-04).</summary>
        public int MpCost => Def.MpCost;

        /// <summary>The pool one charge is spent from when the action starts (R-3.3-02, R-3.3-03).</summary>
        public ChargePool ChargePool => Def.ChargePool;

        /// <summary>First tick of the i-frame window (ticks since the start); equal to <see cref="IFramesEnd"/> when none.</summary>
        public int IFramesStart { get; }

        /// <summary>The tick after the last i-frame tick (ticks since the start).</summary>
        public int IFramesEnd { get; }

        /// <summary>R-3.3-02: <paramref name="elapsed"/> ticks after the start is inside the i-frame window.</summary>
        public bool HasIFramesAt(int elapsed) => elapsed >= IFramesStart && elapsed < IFramesEnd;

        /// <summary>In table order.</summary>
        public EffectTiming[] Effects { get; }

        /// <summary>The phase <paramref name="elapsed"/> ticks after the start (below <see cref="TotalTicks"/>).</summary>
        public ActionState PhaseAt(int elapsed)
        {
            if (elapsed < StartupEnd) return ActionState.Startup;
            if (elapsed < ChannelEnd) return ActionState.Channel;
            if (elapsed < ActiveEnd) return ActionState.Active;
            return ActionState.Recovery;
        }

        /// <summary>First tick of a phase (ticks since the start); equal to <see cref="PhaseEnd"/> when the phase is skipped.</summary>
        public int PhaseStart(ActionState phase) => phase switch
        {
            ActionState.Startup => 0,
            ActionState.Channel => StartupEnd,
            ActionState.Active => ChannelEnd,
            ActionState.Recovery => ActiveEnd,
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };

        /// <summary>The tick after the last tick of a phase (ticks since the start).</summary>
        public int PhaseEnd(ActionState phase) => phase switch
        {
            ActionState.Startup => StartupEnd,
            ActionState.Channel => ChannelEnd,
            ActionState.Active => ActiveEnd,
            ActionState.Recovery => TotalTicks,
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };

        /// <summary>R-3.1-01 cancel rule: <paramref name="other"/> is in CancelableBy and the action is at or past CancelFrom.</summary>
        public bool CanBeCancelledBy(int other, ActionState phase)
        {
            if ((byte)phase < (byte)Def.CancelFrom)
                return false;
            foreach (int index in _cancelableBy)
            {
                if (index == other)
                    return true;
            }
            return false;
        }

        /// <summary>R-3.3-08: the phase ignores flinches.</summary>
        public bool HasSuperArmor(ActionState phase) => (_superArmorMask & (1 << (int)phase)) != 0;
    }

    /// <summary>One effect of an action in ticks.</summary>
    public readonly struct EffectTiming
    {
        internal EffectTiming(ActionCatalog catalog, ActionTiming action, int index, ActionEffectDef def)
        {
            Index = index;
            Def = def;
            var phase = (ActionState)(byte)def.Phase;
            int start = action.PhaseStart(phase);
            int end = action.PhaseEnd(phase);
            int fire = start + catalog.TicksFromMs(def.OffsetMs);
            // Validated: the offset is inside the phase; rounding up must not push it into the next phase.
            FireTick = end > start ? Math.Min(fire, end - 1) : start;
            if (def.Kind == EffectKind.Dash)
            {
                // Validated to end inside the action in ms; after rounding it still must, or the last steps are lost.
                DashTicks = Math.Max(1, Math.Min(catalog.TicksFromMs(def.DurationMs), action.TotalTicks - FireTick));
                DashDistanceRaw = FixedPoint.FromCm(def.DistanceCm);
                DashAngleOffset = AngleFromDegrees(def.DirectionOffsetDeg);
            }
            else
            {
                DashTicks = 0;
                DashDistanceRaw = 0;
                DashAngleOffset = 0;
            }
        }

        /// <summary>Whole degrees (0-359) as a 16-bit binary angle, rounded to the nearest step.</summary>
        public static ushort AngleFromDegrees(int degrees) => (ushort)((((long)degrees * 65536) + 180) / 360);

        /// <summary>Position among its action's effects (bit of <c>ActionMachineState.FiredEffects</c>).</summary>
        public int Index { get; }

        public ActionEffectDef Def { get; }

        public EffectKind Kind => Def.Kind;

        /// <summary>Ticks after the action start when the effect fires.</summary>
        public int FireTick { get; }

        /// <summary>Dash only: ticks the move is spread over, from <see cref="FireTick"/>.</summary>
        public int DashTicks { get; }

        /// <summary>Dash only: total distance in Q24.8 cm.</summary>
        public int DashDistanceRaw { get; }

        /// <summary>
        /// Dash only: the move's direction relative to the locked facing, as a binary angle (0 forward, 32768 backward:
        /// the Archer's Back Step).
        /// </summary>
        public ushort DashAngleOffset { get; }
    }
}
