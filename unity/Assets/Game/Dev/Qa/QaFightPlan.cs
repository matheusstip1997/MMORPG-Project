#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.Dev.Qa
{
    /// <summary>One action of the class's kit as the kill step sees it, all from the config tables (backlog E).</summary>
    public sealed class QaKitAction
    {
        public QaKitAction(ActionDef def, int slot, string button, int reachCm, int damagePercent, bool healsSelf,
            int minReachCm = 0, string selfStatusId = "", bool opensFight = false, int dashCm = 0, int dashOffsetDeg = 0,
            IReadOnlyList<ActionDef>? chain = null, bool dashFollowsKeys = false)
        {
            Def = def;
            DashFollowsKeys = dashFollowsKeys;
            Chain = chain ?? Array.Empty<ActionDef>();
            Slot = slot;
            Button = button;
            ReachCm = reachCm;
            DamagePercent = damagePercent;
            HealsSelf = healsSelf;
            MinReachCm = minReachCm;
            SelfStatusId = selfStatusId;
            OpensFight = opensFight;
            DashCm = dashCm;
            DashOffsetDeg = dashOffsetDeg;
        }

        /// <summary>
        /// The normal attack's later hits (R-3.1-10: hits 2 to 4, from <c>chainNext</c>); empty for every other action. The
        /// kill step presses through them (<see cref="QaFightPlan.ChainPressDue"/>).
        /// </summary>
        public IReadOnlyList<ActionDef> Chain { get; }

        /// <summary>The whole chain's length in ms (this action and its later hits), as configured.</summary>
        public int ChainTotalMs => Def.TotalMs() + Chain.Sum(hit => hit.TotalMs());

        /// <summary>
        /// The stamina the whole chain costs (each hit pays its own, R-3.1-10): the kill step starts the normal attack only when
        /// the snapshot stamina pays all of it, so a later hit is not refused with NoStamina (smoke attack-chain-warrior).
        /// </summary>
        public int ChainStaminaCost => Def.StaminaCost + Chain.Sum(hit => hit.StaminaCost);

        /// <summary>How far its Dash effects move the caster in all (Back Step: 400 cm); 0 without a dash.</summary>
        public int DashCm { get; }

        /// <summary>The dash's direction from the locked facing (<c>directionOffsetDeg</c>: 0 forward, 180 backward).</summary>
        public int DashOffsetDeg { get; }

        /// <summary>
        /// A move skill (M3 decision 15: a <c>moveInput</c> dash, the dodge and the Archer's Dash and Back Step): it goes along
        /// the keys held at the press, so the QA run holds the key toward where it should go; otherwise the dash follows the
        /// facing the press locks (the mouse aim).
        /// </summary>
        public bool DashFollowsKeys { get; }

        /// <summary>
        /// Only moves the caster (the Archer's Dash and Back Step): no damage, heal or status. The kill step never needs
        /// one, so walk-to-demon presses each once and checks the move (QA wave 6 F3).
        /// </summary>
        public bool Moves => !Damages && !HealsSelf && SelfStatusId.Length == 0 && DashCm > 0;

        /// <summary>
        /// Neither damage, a heal, an opening buff nor a move (the Assist's Simple Cleanse, which lowers build-up meters):
        /// walk-to-demon presses it once and checks that it fires (QA wave 6 F3).
        /// </summary>
        public bool Utility => !Damages && !HealsSelf && !OpensFight && DashCm == 0;

        public ActionDef Def { get; }

        public string Id => Def.Id;

        /// <summary>
        /// The ActionBindings input slot (1-11), 0 for the normal attack (left click), or <see cref="QaFightPlan.DodgeSlot"/>
        /// for the dodge.
        /// </summary>
        public int Slot { get; }

        /// <summary>The GameInput action that presses it ("Attack" or "Hotbar<n>").</summary>
        public string Button { get; }

        /// <summary>
        /// How far its hits reach from the attacker's center: strike shapes, ground area shapes, projectile range, plus the
        /// forward dash that carries the caster before its damage (Jump Strike: 400 cm, then a 150 cm circle).
        /// </summary>
        public int ReachCm { get; }

        /// <summary>
        /// The nearest a target may stand to be hit: the forward dash minus the shape's reach (0 without a dash). Closer than
        /// this, the dash carries the caster past it (QA wave 5 H3).
        /// </summary>
        public int MinReachCm { get; }

        /// <summary>The skill multipliers of its damaging effects added up (Multi Shot: 3 x 70); 0 for no damage.</summary>
        public int DamagePercent { get; }

        /// <summary>
        /// It heals the caster: a Heal effect whose targets include the caster, or a status on the caster that heals over
        /// time (<c>tickHealPercent</c>: the Assist's Life Regeneration, QA wave 5 H1).
        /// </summary>
        public bool HealsSelf { get; }

        /// <summary>The status it applies to the caster (an ApplyStatus with target self); empty for none.</summary>
        public string SelfStatusId { get; }

        /// <summary>A buff on the caster that does not heal (Stamina Buff, Magic Barrier): cast when ready as the fight starts.</summary>
        public bool OpensFight { get; }

        public bool Damages => DamagePercent > 0;

        /// <summary>The distance it is best cast from: the middle of its reach window.</summary>
        public int PreferredCm => (MinReachCm + ReachCm) / 2;

        public bool InReach(double distanceCm) => distanceCm >= MinReachCm && distanceCm <= ReachCm;
    }

    /// <summary>What the kill step does next.</summary>
    public enum QaFightMove
    {
        /// <summary>Nothing usable now (stamina, MP or cooldowns): wait a frame.</summary>
        Wait = 0,
        /// <summary>Press <see cref="QaFightChoice.Action"/>.</summary>
        Use = 1,
        /// <summary>Low HP and no heal ready: walk away from the demon for a while.</summary>
        BackOff = 2,
        /// <summary>
        /// <see cref="QaFightChoice.Action"/> is the best ready action but the demon is too close for it (a forward dash
        /// would carry the caster past it): step back to its <see cref="QaKitAction.PreferredCm"/>, then press it.
        /// </summary>
        StepBack = 3,
        /// <summary>
        /// Low HP and <see cref="QaFightChoice.Action"/>, a heal off cooldown, waits only for stamina: spend none (each spend
        /// restarts <c>staminaRegenDelayMs</c>) for <see cref="QaFightChoice.WaitMs"/>, then heal (QA wave 6 F2).
        /// </summary>
        WaitForStamina = 4,
        /// <summary>
        /// <see cref="QaFightChoice.Action"/> is a damaging skill not used yet in this run and the demon is beyond its reach
        /// (the Mage's 350 cm Flamethrower): walk toward the demon until it is in reach (QA wave 6 F3).
        /// </summary>
        CloseIn = 5,
    }

    public readonly struct QaFightChoice
    {
        public QaFightChoice(QaFightMove move, QaKitAction? action, int waitMs = 0)
        {
            Move = move;
            Action = action;
            WaitMs = waitMs;
        }

        public QaFightMove Move { get; }
        public QaKitAction? Action { get; }

        /// <summary><see cref="QaFightMove.WaitForStamina"/>: how long until the stamina pays for the heal (from the tables).</summary>
        public int WaitMs { get; }

        public override string ToString() => Action != null ? $"{Move} {Action.Id}" : Move.ToString();
    }

    /// <summary>
    /// How stamina comes back (spec 12.7, <c>StartingValuesM1</c>): <see cref="PerSec"/> once <see cref="DelayMs"/> has
    /// passed since the last spend. The base rate: a stamina buff only brings it sooner.
    /// </summary>
    public readonly struct QaStaminaRegen
    {
        public QaStaminaRegen(int perSec, int delayMs)
        {
            PerSec = perSec;
            DelayMs = delayMs;
        }

        public int PerSec { get; }
        public int DelayMs { get; }

        public static QaStaminaRegen From(ConfigSet config)
        {
            var starting = config.Single<StartingValuesM1>();
            return new QaStaminaRegen(starting.StaminaRegenPerSec, starting.StaminaRegenDelayMs);
        }

        /// <summary>
        /// Milliseconds until <paramref name="stamina"/> reaches <paramref name="cost"/> when nothing is spent, the last spend
        /// <paramref name="sinceSpendMs"/> ago; <see cref="int.MaxValue"/> when stamina does not come back.
        /// </summary>
        public int MsUntil(int stamina, int cost, long sinceSpendMs)
        {
            if (stamina >= cost)
                return 0;
            if (PerSec <= 0)
                return int.MaxValue;
            long delay = Math.Max(0L, DelayMs - Math.Max(0L, sinceSpendMs));
            long refill = ((long)(cost - stamina) * 1000 + PerSec - 1) / PerSec;
            return (int)Math.Min(int.MaxValue, delay + refill);
        }
    }

    /// <summary>The player's side of the fight when choosing: snapshot meters and the predicted cooldowns.</summary>
    public readonly struct QaFightState
    {
        public QaFightState(int hp, int maxHp, int mp, int stamina, double distanceCm, Func<QaKitAction, bool> ready,
            Func<QaKitAction, bool>? opening = null, QaStaminaRegen regen = default, long sinceSpendMs = long.MaxValue,
            Func<QaKitAction, bool>? unused = null)
        {
            Hp = hp;
            MaxHp = maxHp;
            Mp = mp;
            Stamina = stamina;
            DistanceCm = distanceCm;
            Ready = ready;
            Opening = opening;
            Regen = regen;
            SinceSpendMs = sinceSpendMs;
            Unused = unused;
        }

        /// <summary>The kit actions this run has not fired yet (QA wave 6 F3: every skill fires once); null: no such rule.</summary>
        public Func<QaKitAction, bool>? Unused { get; }

        public int Hp { get; }
        public int MaxHp { get; }
        public int Mp { get; }
        public int Stamina { get; }

        /// <summary>From the player's center to the demon's.</summary>
        public double DistanceCm { get; }

        /// <summary>The client's predicted action machine: idle and the action off cooldown.</summary>
        public Func<QaKitAction, bool> Ready { get; }

        /// <summary>The opening buffs still to cast in this fight (<see cref="QaKitAction.OpensFight"/>); null: none.</summary>
        public Func<QaKitAction, bool>? Opening { get; }

        /// <summary>The stamina regeneration of the tables.</summary>
        public QaStaminaRegen Regen { get; }

        /// <summary>Milliseconds since the player last spent stamina (an action press).</summary>
        public long SinceSpendMs { get; }

        public bool HpBelow(int percent) => MaxHp > 0 && (long)Hp * 100 < (long)MaxHp * percent;

        public bool CanPay(QaKitAction action) => Stamina >= action.ChainStaminaCost && Mp >= action.Def.MpCost;

        public bool Usable(QaKitAction action) => CanPay(action) && Ready(action);

        /// <summary>Ready and its MP paid: only its stamina is missing, and waiting brings that.</summary>
        public bool WantsOnlyStamina(QaKitAction action) =>
            Ready(action) && Mp >= action.Def.MpCost && Stamina < action.ChainStaminaCost;

        public int StaminaWaitMs(QaKitAction action) => Regen.MsUntil(Stamina, action.ChainStaminaCost, SinceSpendMs);
    }

    /// <summary>What a timed defence (parry, dodge) does on this frame: wait for its point, press, or skip a hit come too close.</summary>
    public enum QaTimedPress
    {
        Wait = 0,
        Press = 1,
        TooLate = 2,
    }

    /// <summary>
    /// The gear the smoke's Blacksmith forges and wears (QA wave 2 F2): the sword for a class that can wield one (R-3.5-07,
    /// <c>ClassGearDef.weaponTypes</c>), else the chest armor, which has no class gate. Everything else comes from the mold's rows.
    /// </summary>
    public sealed class QaCraftedGear
    {
        public QaCraftedGear(string moldId, string itemId, int materialSlots, int equipCode)
        {
            MoldId = moldId;
            ItemId = itemId;
            MaterialSlots = materialSlots;
            EquipCode = equipCode;
        }

        public string MoldId { get; }
        public string ItemId { get; }

        /// <summary>The mold's free slots: the step fills each with an ingot, so it refines that many.</summary>
        public int MaterialSlots { get; }

        /// <summary>The <c>EquipSlotDef.code</c> of its slot: its slot index in the Equipped container.</summary>
        public int EquipCode { get; }
    }

    /// <summary>
    /// How the QA kill step fights with a class's kit (user decision 2026-10-25), the same for every class: its
    /// ActionBindings row and the effect tables say what each action does, the snapshot meters and the client's predicted
    /// cooldowns say what is usable. Below <see cref="QaTimeouts.HealBelowPercent"/> HP it uses a ready heal on itself;
    /// below <see cref="QaTimeouts.BackOffBelowPercent"/> with no heal ready it backs off; between the two, a heal that only
    /// lacks stamina makes it spend none until the heal is paid for (QA wave 6 F2); at the start of the fight it
    /// casts its ready self-buffs; otherwise it uses the ready damaging action in reach with the biggest multiplier (the
    /// kit's skills when they are ready, the normal attack in between), stepping back first for one whose dash needs room.
    /// Pure: no Unity, testable in EditMode.
    /// </summary>
    public static class QaFightPlan
    {
        /// <summary>The <see cref="QaKitAction.Slot"/> of the dodge (its own button, not a hotbar slot).</summary>
        public const int DodgeSlot = -1;

        /// <summary>
        /// The class's dodge (first play session P2): a move of <c>StartingValuesM1.dodgeDistanceCm</c> (its Dash effect),
        /// pressed with no movement key held, so it goes along the facing turned by its offset whatever its dashDirection.
        /// </summary>
        public static QaKitAction Dodge(ConfigSet config, ActionBindings bindings) => Describe(config, bindings.Dodge, DodgeSlot, "Dodge");

        /// <summary>
        /// The normal attack and every bound skill of <paramref name="bindings"/>, in input order; with <paramref name="learned"/>
        /// (the predicted loadout, WP-M2-5) only the kit skills it binds: a new character learns its skills.
        /// </summary>
        public static List<QaKitAction> Kit(ConfigSet config, ActionBindings bindings, Shared.Simulation.Actions.ActionLoadout? learned = null)
        {
            var kit = new List<QaKitAction> { Describe(config, bindings.NormalAttack, 0, "Attack") };
            for (int i = 0; i < bindings.SkillSlots.Length; i++)
            {
                if (bindings.SkillSlots[i].Length > 0 && (learned == null || learned.SlotIndex(i + 1) >= 0))
                    kit.Add(Describe(config, bindings.SkillSlots[i], i + 1, "Hotbar" + (i + 1).ToString(CultureInfo.InvariantCulture)));
            }
            return kit;
        }

        public static QaKitAction Describe(ConfigSet config, string actionId, int slot, string button)
        {
            var def = ActionDef.Find(config, actionId) ?? throw new QaFailure("no ActionDef " + actionId);
            var effects = Rows<ActionEffectDef>(config).Where(e => e.ActionId == actionId).ToList();
            int damage = effects.Where(DealsDamage).Sum(e => e.DamagePercent);
            var selfStatus = effects
                .Where(e => e.Kind == EffectKind.ApplyStatus && e.HasTarget(EffectTarget.Self))
                .Select(e => StatusEffectDef.Find(config, e.StatusId))
                .FirstOrDefault(s => s != null);
            bool heals = effects.Any(e => e.Kind == EffectKind.Heal && e.HasTarget(EffectTarget.Self))
                         || selfStatus is { TickHealPercent: > 0 };
            bool opens = selfStatus is { Kind: StatusEffectKind.Buff, TickHealPercent: 0 } && damage == 0;
            int shapeReach = ReachCm(config, effects);
            int dash = damage > 0 ? ForwardDashCm(effects) : 0;
            int minReach = dash > 0 ? Math.Max(0, dash - shapeReach) : 0;
            var dashes = effects.Where(e => e.Kind == EffectKind.Dash).ToList();
            return new QaKitAction(def, slot, button, shapeReach + dash, damage, heals, minReach, selfStatus?.Id ?? "", opens,
                dashes.Sum(e => e.DistanceCm), dashes.Select(e => e.DirectionOffsetDeg).FirstOrDefault(),
                ActionDef.ChainFrom(config, actionId, out _).Skip(1).Select(id => ActionDef.Find(config, id)!).ToList(),
                dashes.Any(e => e.DashDirection == DashDirection.MoveInput));
        }

        /// <summary>Strikes and projectiles, and ground areas once they carry damage (WP11).</summary>
        public static bool DealsDamage(ActionEffectDef effect) =>
            effect.DamagePercent > 0
            && (effect.Kind == EffectKind.Strike || effect.Kind == EffectKind.Projectile || effect.Kind == EffectKind.GroundArea);

        /// <summary>The farthest its damaging effects reach: hit shapes (HitShapeDef) or projectile range (ProjectileDef).</summary>
        public static int ReachCm(ConfigSet config, IEnumerable<ActionEffectDef> effects)
        {
            int reach = 0;
            foreach (var effect in effects)
            {
                if (effect.Kind == EffectKind.Projectile)
                {
                    reach = Math.Max(reach, ProjectileDef.Find(config, effect.ProjectileId)?.RangeCm ?? 0);
                    continue;
                }
                foreach (var shape in Rows<HitShapeDef>(config).Where(s => s.EffectId == effect.Id))
                    reach = Math.Max(reach, shape.ForwardOffsetCm + Math.Max(shape.RangeCm, shape.LengthCm + shape.RadiusCm));
            }
            return reach;
        }

        /// <summary>
        /// How far forward the caster dashes before its first damaging effect fires: Dash effects along the facing
        /// (<c>directionOffsetDeg</c> 0) in an earlier phase, or the same phase at an earlier offset.
        /// </summary>
        public static int ForwardDashCm(IReadOnlyList<ActionEffectDef> effects)
        {
            var first = effects.Where(DealsDamage).OrderBy(e => e.Phase).ThenBy(e => e.OffsetMs).FirstOrDefault();
            if (first == null)
                return 0;
            return effects
                .Where(e => e.Kind == EffectKind.Dash && e.DirectionOffsetDeg == 0
                            && (e.Phase < first.Phase || e.Phase == first.Phase && e.OffsetMs < first.OffsetMs))
                .Sum(e => e.DistanceCm);
        }

        /// <summary>
        /// R-3.1-10: the next attack press is due: the predicted machine runs a hit with a next one, inside its chain window
        /// (<see cref="ActionTiming.ChainFromTick"/>), and this hit (catalog index) has not been pressed for yet.
        /// </summary>
        public static bool ChainPressDue(ActionMachineState actions, int pressedForIndex)
        {
            var running = actions.Action;
            return running != null && running.ChainNextIndex >= 0 && actions.Elapsed >= running.ChainFromTick
                   && actions.ActionIndex != pressedForIndex;
        }


        /// <summary>
        /// QA wave 2 F1: a press with a wind-up of <paramref name="windUpTicks"/> reaches its Active before the strike drawn on
        /// the target lands: no strike coming (null), a hit already past, or one at least the wind-up plus
        /// <see cref="QaTimeouts.WindUpMarginTicks"/> away.
        /// </summary>
        public static bool WindUpFits(double renderTick, double? nextHitTick, double windUpTicks) =>
            nextHitTick is not { } hit || hit <= renderTick || hit - renderTick >= windUpTicks + QaTimeouts.WindUpMarginTicks;

        /// <summary>
        /// QA wave 2 F1: the machine really holds <paramref name="actionId"/> and its facing is that press's aim. <c>IsBusy</c> is
        /// also true in a flinch, which holds no action: the "152.2 degrees" read from an attack that never started.
        /// </summary>
        public static bool AimReadable(ActionMachineState actions, string actionId) =>
            actions.Action?.Id == actionId
            && (actions.State == ActionState.Startup || actions.State == ActionState.Channel || actions.State == ActionState.Active);

        /// <summary>The first hotbar slot of <paramref name="learned"/> holding a ground skill (ActionDef aim ground: Ice Pike); null without one.</summary>
        public static QaKitAction? LearnedGroundSkill(ConfigSet config, ActionCatalog catalog, ActionLoadout? learned)
        {
            if (learned == null)
                return null;
            for (int slot = 1; slot <= ActionBindings.MaxSkillSlots; slot++)
            {
                int index = learned.SlotIndex(slot);
                if (index >= 0 && catalog[index].Aim == ActionAim.Ground)
                    return Describe(config, catalog[index].Id, slot, "Hotbar" + slot.ToString(CultureInfo.InvariantCulture));
            }
            return null;
        }

        /// <summary>
        /// QA wave 2 F2: what a class of <paramref name="classId"/> forges and wears in the crafting steps: the sword when it
        /// wields one, otherwise the Blacksmith's chest of its armor tier (R-4.1-05, WP-M4-13: the class row's tier; the smoke
        /// character learns no armor passive).
        /// </summary>
        public static QaCraftedGear CraftedGearFor(ConfigSet config, string classId)
        {
            var sword = ItemDef.Find(config, "m3.forgedSword") ?? throw new QaFailure("no ItemDef m3.forgedSword");
            var gear = ClassGearDef.Find(config, classId) ?? throw new QaFailure("no ClassGearDef row " + classId);
            var tier = ClassDef.Find(config, classId)?.ArmorClass ?? throw new QaFailure("no ClassDef row " + classId);
            string moldId = gear.CanUse(sword.WeaponType)
                ? "m3.moldSword"
                : Rows<MoldDef>(config).FirstOrDefault(m => m.JobId == "blacksmith" && ItemDef.Find(config, m.OutputItemId) is { } output
                      && output.ArmorTier == tier && output.EquipSlots.Contains("chest"))?.Id
                  ?? throw new QaFailure($"no Blacksmith chest mold of the {tier} tier");
            var mold = MoldDef.Find(config, moldId) ?? throw new QaFailure("no MoldDef " + moldId);
            var item = ItemDef.Find(config, mold.OutputItemId) ?? throw new QaFailure("no ItemDef " + mold.OutputItemId);
            var slot = Rows<EquipSlotDef>(config).FirstOrDefault(s => s.Id == item.EquipSlots[0])
                       ?? throw new QaFailure("no EquipSlotDef " + item.EquipSlots[0]);
            return new QaCraftedGear(moldId, item.Id, Rows<MoldSlotDef>(config).Count(s => s.MoldId == moldId), slot.Code);
        }

        /// <summary>
        /// A parry or a dodge against the drawn hit (see <see cref="QaTimeouts.ParryLeadTicks"/>): any frame inside the band
        /// presses, an attack first seen past it is skipped.
        /// </summary>
        public static QaTimedPress TimedPress(double renderTick, double hitTick) =>
            renderTick < hitTick - QaTimeouts.ParryLeadTicks ? QaTimedPress.Wait
            : renderTick > hitTick - QaTimeouts.ParryLatestLeadTicks ? QaTimedPress.TooLate
            : QaTimedPress.Press;

        /// <summary>QA wave 2 F3: this character attempts the boss's parry step (see <see cref="QaTimeouts.ParryBossMinLevel"/>).</summary>
        public static bool AttemptsBossParry(ArmorClass armor, int level) =>
            armor == ArmorClass.Heavy || level >= QaTimeouts.ParryBossMinLevel;

        /// <summary>
        /// Fixer 3 smoke r3 (mage at level 13): the lock-demon step's damage counts toward the kill step's max HP only when the
        /// kill step killed that same demon; a lock-demon that killed its demon leaves the kill step a fresh one.
        /// </summary>
        public static int CarriedLockDamage(uint lockDemonId, int lockDamage, uint killedId) =>
            lockDemonId != 0 && lockDemonId == killedId ? lockDamage : 0;

        /// <summary>
        /// Fixer 3 smoke r3 (warrior at level 1): the HUD shows the stagger marker while the render clock is inside the stagger
        /// (<c>NamePlate.ShowsStagger</c>), so the marker is due hidden only once the render clock passed its end; the newest
        /// snapshot leaves the stagger an interpolation delay earlier.
        /// </summary>
        public static bool StaggerOverOnRenderClock(double renderTick, uint startTick, int ticks) =>
            renderTick >= unchecked(startTick + (uint)Math.Max(0, ticks));

        /// <summary>A killer's name for a step's text, by what it is (QA wave 2 F3: "(the demon)" named the boss); empty when unknown.</summary>
        public static string MonsterIdOf(ConfigSet config, int netId) =>
            Rows<MonsterDef>(config).FirstOrDefault(m => m.NetId == netId)?.Id ?? "";

        public static QaFightChoice Choose(IReadOnlyList<QaKitAction> kit, in QaFightState state)
        {
            var self = state;
            if (state.HpBelow(QaTimeouts.HealBelowPercent))
            {
                // An instant heal first; a heal over time (a status) when none is ready.
                var heal = kit.Where(a => a.HealsSelf && self.Usable(a)).OrderBy(a => a.SelfStatusId.Length > 0 ? 1 : 0).FirstOrDefault();
                if (heal != null)
                    return new QaFightChoice(QaFightMove.Use, heal);
                if (state.HpBelow(QaTimeouts.BackOffBelowPercent))
                    return new QaFightChoice(QaFightMove.BackOff, null);
                // QA wave 6 F2: a heal that only lacks stamina comes first; every attack would spend that stamina and
                // restart its regeneration delay. The heal that is paid for soonest.
                var waiting = kit.Where(a => a.HealsSelf && self.WantsOnlyStamina(a))
                    .OrderBy(a => self.StaminaWaitMs(a)).FirstOrDefault();
                if (waiting != null)
                    return new QaFightChoice(QaFightMove.WaitForStamina, waiting, state.StaminaWaitMs(waiting));
            }
            if (state.Opening != null)
            {
                var buff = kit.FirstOrDefault(a => a.OpensFight && self.Opening!(a) && self.Usable(a));
                if (buff != null)
                    return new QaFightChoice(QaFightMove.Use, buff);
            }
            // QA wave 6 F3: the strongest damaging skill this run has not fired goes first, so every skill of the kit lands at
            // least once in the fight: used in reach, after a step back when its dash needs room (Jump Strike), or after
            // closing in when it reaches less far (the Mage's Flamethrower). Not below the heal line.
            if (state.Unused != null && !state.HpBelow(QaTimeouts.HealBelowPercent))
            {
                var fresh = kit.Where(a => a.Damages && self.Unused!(a) && self.Usable(a)).OrderByDescending(a => a.DamagePercent)
                    .FirstOrDefault();
                if (fresh != null)
                {
                    var move = fresh.InReach(state.DistanceCm) ? QaFightMove.Use
                        : state.DistanceCm < fresh.MinReachCm ? QaFightMove.StepBack
                        : QaFightMove.CloseIn;
                    return new QaFightChoice(move, fresh);
                }
            }
            QaKitAction? best = null;
            QaKitAction? needsRoom = null;
            foreach (var action in kit)
            {
                if (!action.Damages || action.ReachCm < state.DistanceCm || !state.Usable(action))
                    continue;
                if (state.DistanceCm < action.MinReachCm)
                {
                    if (needsRoom == null || action.DamagePercent > needsRoom.DamagePercent)
                        needsRoom = action;
                    continue;
                }
                if (best == null || action.DamagePercent > best.DamagePercent)
                    best = action;
            }
            if (needsRoom != null && !state.HpBelow(QaTimeouts.HealBelowPercent)
                && (best == null || needsRoom.DamagePercent > best.DamagePercent))
                return new QaFightChoice(QaFightMove.StepBack, needsRoom);
            return best != null ? new QaFightChoice(QaFightMove.Use, best) : new QaFightChoice(QaFightMove.Wait, null);
        }

        /// <summary>
        /// After a back-off: fight again once HP is back to <see cref="QaTimeouts.ReengageAbovePercent"/> or a heal of the
        /// kit is usable (QA wave 5 H2), so it does not walk back at the HP that sent it away.
        /// </summary>
        public static bool Recovered(IReadOnlyList<QaKitAction> kit, in QaFightState state)
        {
            var self = state;
            return !state.HpBelow(QaTimeouts.ReengageAbovePercent) || kit.Any(a => a.HealsSelf && self.Usable(a));
        }

        /// <summary>
        /// QA wave 1 F2: the demon died in the tick of our first hit on it (<paramref name="ourHits"/> in arrival order), so
        /// no snapshot ever showed it damaged and alive: its HP bucket and the HP bar of a damaged plate cannot be seen.
        /// </summary>
        public static bool DiedOnFirstHit(IReadOnlyList<QaHit> ourHits)
        {
            if (ourHits.Count == 0)
                return false;
            uint first = ourHits[0].Tick;
            return ourHits.Any(hit => hit.Entry.Kind == HitKind.Killed && hit.Tick == first);
        }

        /// <summary>
        /// QA wave 1 F1: milliseconds from the last hit <paramref name="ownId"/> dealt or took (any hit but a heal marks the
        /// combat state, R-2.1-07) to <paramref name="nowTick"/>; null when the run saw none.
        /// </summary>
        public static double? MsSinceCombat(IReadOnlyList<QaHit> hits, uint ownId, uint nowTick, double tickMs)
        {
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                var entry = hits[i].Entry;
                if (entry.Kind != HitKind.Healed && (entry.AttackerId == ownId || entry.TargetId == ownId))
                    return unchecked((int)(nowTick - hits[i].Tick)) * tickMs;
            }
            return null;
        }

        private static IEnumerable<T> Rows<T>(ConfigSet config) =>
            config.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>() : Enumerable.Empty<T>();
    }

    /// <summary>
    /// QA wave 1 F2, fixer 3 smoke r3: the HP buckets of the demon the kill step fights, from its first sighting alive. A
    /// demon the step retargets to (the lock-demon step killed the one before) starts its own count; a dead or unseen one
    /// counts nothing.
    /// </summary>
    public sealed class QaDemonBuckets
    {
        public uint DemonId { get; private set; }
        public byte FirstBucket { get; private set; }
        public byte LowestBucket { get; private set; }

        public void Note(uint id, byte bucket, bool alive)
        {
            if (id == 0 || !alive || bucket == 0)
                return;
            if (id != DemonId)
            {
                DemonId = id;
                FirstBucket = LowestBucket = bucket;
                return;
            }
            if (bucket < LowestBucket)
                LowestBucket = bucket;
        }

        public bool WentDown => DemonId != 0 && LowestBucket < FirstBucket;
    }
}
