using System;
using System.Collections.Generic;

namespace Shared.Data.Tables
{
    /// <summary>What an action is for (R-3.1-01). Config enum (camelCase names); append only.</summary>
    public enum ActionKind : byte
    {
        /// <summary>A hit of a class's normal attack chain: one action with a single strike or shot (R-3.1-06, R-3.1-10).</summary>
        Attack = 1,
        Skill = 2,
        /// <summary>Never interrupted by a flinch (R-3.3-07).</summary>
        Dodge = 3,
        Parry = 4,

        /// <summary>
        /// Using an item (spec 3.4 "Consumable use", WP-M3-3): drinking or eating. Started by a <c>UseItemRequest</c>, never
        /// from the hotbar; no stamina or MP; interrupted and cancelled like a skill; its one <c>consume</c> effect is the
        /// point the item is used.
        /// </summary>
        UseItem = 5,
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
    /// Starting an action spends its <see cref="StaminaCost"/>, its <see cref="MpCost"/> and one charge of its <see cref="ChargePool"/> (backlog D4,
    /// spec 3.1 and 3.3 implementation notes). An attack can carry a parry color, one against monsters' targets (PvE) and
    /// one in PvP (R-3.3-04, R-3.3-06). Hit shapes are in Strike effects (<see cref="HitShapeDef"/>). TODO(D6): <c>gaugeDelta</c>. TODO(D5): attack/cast speed scales the durations (R-3.1-02).
    /// TODO(F): monster attacks need <c>startupMs >= StartingValuesM1.MonsterAttackStartupMinMs</c> (R-3.1-03).
    /// </para>
    /// </summary>
    [Table("ActionDef", 2)]
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
        /// MP spent when the action starts, together with the stamina (R-3.1-04: skills cost a small amount of MP); the press
        /// is refused with <c>NoMp</c> when the entity has less. Attacks, dodges and parries cost none. A skill on a class's
        /// hotbar costs at least 1 and at most the class's MP pool (<see cref="ActionBindings"/>, spec 12.7).
        /// </summary>
        [Range(0, 10_000)] public int MpCost { get; set; }

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

        /// <summary>
        /// Attack chains (R-3.1-10): the next hit of the class's normal attack chain; empty for none (the last hit, and
        /// every action that is not a chain link). Only attacks have one; <c>ActionBindings</c> checks the chain from the
        /// class's <c>normalAttack</c> (it terminates, every link is an attack of that class only, costs as hit 1).
        /// </summary>
        [Optional, Ref(typeof(ActionDef), Optional = true)] public string ChainNext { get; set; } = string.Empty;

        /// <summary>
        /// Attack chains: from this ms after the start an attack press is a chain press for <see cref="ChainNext"/> (kept
        /// until <see cref="ChainLinkMs"/>); an earlier attack press is dropped. 0 without a <see cref="ChainNext"/>.
        /// </summary>
        [Optional, Range(0, 10_000)] public int ChainFromMs { get; set; }

        /// <summary>
        /// Attack chains: the earliest ms after the start the next hit begins, cutting the rest of this hit (a chain press
        /// later than this starts it at once). After every effect of this hit. 0 without a <see cref="ChainNext"/>.
        /// </summary>
        [Optional, Range(0, 10_000)] public int ChainLinkMs { get; set; }

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
            // R-3.1-04: only skills cost MP; normal attacks, the dodge and the parry cost none.
            if (MpCost != 0 && (Kind == ActionKind.Attack || Kind == ActionKind.Dodge || Kind == ActionKind.Parry))
                errors.Add(nameof(MpCost), $"a {Kind} action costs no MP (R-3.1-04)");
            // M3 plan decision 3 (spec 3.4 "Consumable use"): using an item costs no stamina and no MP.
            if (Kind == ActionKind.UseItem && (StaminaCost != 0 || MpCost != 0))
                errors.Add(nameof(StaminaCost), "a useItem action costs no stamina or MP (spec 3.4 consumable use)");

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

            // R-3.1-10: only attacks chain, and the chain window lies inside the action.
            if (ChainNext.Length == 0)
            {
                if (ChainFromMs != 0 || ChainLinkMs != 0)
                    errors.Add(nameof(ChainFromMs), "chainFromMs and chainLinkMs are 0 without a chainNext (R-3.1-10)");
            }
            else
            {
                if (Kind != ActionKind.Attack)
                    errors.Add(nameof(ChainNext), $"a {Kind} action does not chain; only attacks do (R-3.1-10)");
                if (ChainNext == Id)
                    errors.Add(nameof(ChainNext), "an attack does not chain into itself (R-3.1-10)");
                if (ChainFromMs > ChainLinkMs)
                    errors.Add(nameof(ChainFromMs), $"opens at {ChainFromMs} ms, after the link at {ChainLinkMs} ms");
                if (ChainLinkMs >= TotalMs())
                    errors.Add(nameof(ChainLinkMs), $"links at {ChainLinkMs} ms, not before the end of the action ({TotalMs()} ms)");
                // An attack press in the buffer window is always a chain press, never a buffered hit 1.
                if (ChainFromMs > TotalMs() - BufferWindowMs)
                    errors.Add(nameof(ChainFromMs),
                        $"opens at {ChainFromMs} ms, after the buffer window opens ({TotalMs() - BufferWindowMs} ms)");
            }
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

            // Spec 3.2: projectiles are capped per owner; one action (Multi Shot) must not spawn more than the cap allows,
            // or its last arrows are refused every time.
            if (set.TryGet(typeof(ActionEffectDef), out var allEffects) && set.TryGet(typeof(ProjectileSettings), out var settings)
                && settings.Rows.Count == 1)
            {
                int maxPerOwner = ((ProjectileSettings)settings.Rows[0]).MaxPerOwner;
                int projectiles = 0;
                foreach (var row in allEffects.Rows)
                {
                    var effect = (ActionEffectDef)row;
                    if (effect.ActionId == Id && effect.Kind == EffectKind.Projectile)
                        projectiles++;
                }
                if (projectiles > maxPerOwner)
                    errors.Add(nameof(Id), $"spawns {projectiles} projectiles, ProjectileSettings.MaxPerOwner is {maxPerOwner}");
            }

            if (ChainNext.Length != 0)
                ValidateChainLink(set, errors);

            // Spec 3.4 consumable use (WP-M3-3): a useItem action has exactly one consume effect, its effect point.
            if (Kind == ActionKind.UseItem && set.TryGet(typeof(ActionEffectDef), out var ownEffects))
            {
                int consumes = 0;
                foreach (var row in ownEffects.Rows)
                {
                    var effect = (ActionEffectDef)row;
                    if (effect.ActionId == Id && effect.Kind == EffectKind.Consume)
                        consumes++;
                }
                if (consumes != 1)
                    errors.Add(nameof(Kind), $"a useItem action has exactly one consume effect ({consumes})");
            }
        }

        /// <summary>R-3.1-10: the set rules of a row with a <see cref="ChainNext"/>.</summary>
        private void ValidateChainLink(ConfigSet set, RowErrors errors)
        {
            // The next hit never cuts this hit's strike: every effect fires before the link (ActionCatalog keeps it so
            // after rounding to ticks).
            if (set.TryGet(typeof(ActionEffectDef), out var effects))
            {
                foreach (var row in effects.Rows)
                {
                    var effect = (ActionEffectDef)row;
                    int fireMs = PhaseStartMs(effect.Phase) + effect.OffsetMs;
                    if (effect.ActionId == Id && fireMs >= ChainLinkMs)
                        errors.Add(nameof(ChainLinkMs), $"links at {ChainLinkMs} ms, before '{effect.Id}' fires ({fireMs} ms)");
                }
            }

            // A chain belongs to a class's normal attack (ActionBindings checks each chain); monsters never chain.
            if (set.TryGet(typeof(ActionBindings), out var bindings))
            {
                bool onAChain = false;
                foreach (var row in bindings.Rows)
                {
                    if (ChainFrom(set, ((ActionBindings)row).NormalAttack, out _).Contains(Id))
                    {
                        onAChain = true;
                        break;
                    }
                }
                if (!onAChain)
                    errors.Add(nameof(ChainNext), "is not on the chain of any class's normalAttack (R-3.1-10)");
            }
            if (set.TryGet(typeof(MonsterDef), out var monsters))
            {
                foreach (var row in monsters.Rows)
                {
                    var monster = (MonsterDef)row;
                    if (Array.IndexOf(monster.Attacks, Id) >= 0 || Array.IndexOf(monster.Attacks, ChainNext) >= 0)
                        errors.Add(nameof(ChainNext), $"a chain link is an attack of MonsterDef '{monster.Id}'; monsters do not chain (R-3.1-10)");
                }
            }
        }

        /// <summary>
        /// R-3.1-10: the chain that starts at <paramref name="headId"/> (the head first, then each <see cref="ChainNext"/>),
        /// stopping at a missing row or before an action already in it. <paramref name="cycles"/> is true when a link
        /// names an action already in the chain (the chain would never end).
        /// </summary>
        public static List<string> ChainFrom(ConfigSet set, string headId, out bool cycles)
        {
            var chain = new List<string>();
            cycles = false;
            var action = headId.Length != 0 ? Find(set, headId) : null;
            while (action != null)
            {
                if (chain.Contains(action.Id))
                {
                    cycles = true;
                    break;
                }
                chain.Add(action.Id);
                action = action.ChainNext.Length != 0 ? Find(set, action.ChainNext) : null;
            }
            return chain;
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
