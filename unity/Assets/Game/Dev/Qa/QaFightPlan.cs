#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Shared.Data;
using Shared.Data.Tables;

namespace Game.Dev.Qa
{
    /// <summary>One action of the class's kit as the kill step sees it, all from the config tables (backlog E).</summary>
    public sealed class QaKitAction
    {
        public QaKitAction(ActionDef def, int slot, string button, int reachCm, int damagePercent, bool healsSelf)
        {
            Def = def;
            Slot = slot;
            Button = button;
            ReachCm = reachCm;
            DamagePercent = damagePercent;
            HealsSelf = healsSelf;
        }

        public ActionDef Def { get; }

        public string Id => Def.Id;

        /// <summary>The ActionBindings input slot (1-11), or 0 for the normal attack (left click).</summary>
        public int Slot { get; }

        /// <summary>The GameInput action that presses it ("Attack" or "Hotbar<n>").</summary>
        public string Button { get; }

        /// <summary>How far its hits reach from the attacker's center: strike shapes, ground area shapes, projectile range.</summary>
        public int ReachCm { get; }

        /// <summary>The skill multipliers of its damaging effects added up (Multi Shot: 3 x 70); 0 for no damage.</summary>
        public int DamagePercent { get; }

        /// <summary>It has a Heal effect whose targets include the caster.</summary>
        public bool HealsSelf { get; }

        public bool Damages => DamagePercent > 0;
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
    }

    public readonly struct QaFightChoice
    {
        public QaFightChoice(QaFightMove move, QaKitAction? action)
        {
            Move = move;
            Action = action;
        }

        public QaFightMove Move { get; }
        public QaKitAction? Action { get; }

        public override string ToString() => Action != null ? $"{Move} {Action.Id}" : Move.ToString();
    }

    /// <summary>The player's side of the fight when choosing: snapshot meters and the predicted cooldowns.</summary>
    public readonly struct QaFightState
    {
        public QaFightState(int hp, int maxHp, int mp, int stamina, double distanceCm, Func<QaKitAction, bool> ready)
        {
            Hp = hp;
            MaxHp = maxHp;
            Mp = mp;
            Stamina = stamina;
            DistanceCm = distanceCm;
            Ready = ready;
        }

        public int Hp { get; }
        public int MaxHp { get; }
        public int Mp { get; }
        public int Stamina { get; }

        /// <summary>From the player's center to the demon's.</summary>
        public double DistanceCm { get; }

        /// <summary>The client's predicted action machine: idle and the action off cooldown.</summary>
        public Func<QaKitAction, bool> Ready { get; }

        public bool HpBelow(int percent) => MaxHp > 0 && (long)Hp * 100 < (long)MaxHp * percent;

        public bool CanPay(QaKitAction action) => Stamina >= action.Def.StaminaCost && Mp >= action.Def.MpCost;
    }

    /// <summary>
    /// How the QA kill step fights with a class's kit (user decision 2026-10-25), the same for every class: its
    /// ActionBindings row and the effect tables say what each action does, the snapshot meters and the client's predicted
    /// cooldowns say what is usable. Below <see cref="QaTimeouts.HealBelowPercent"/> HP it uses a ready heal on itself;
    /// below <see cref="QaTimeouts.BackOffBelowPercent"/> with no heal ready it backs off; otherwise it uses the ready
    /// damaging action in reach with the biggest multiplier (the kit's skills when they are ready, the normal attack in
    /// between). Pure: no Unity, testable in EditMode.
    /// </summary>
    public static class QaFightPlan
    {
        /// <summary>The normal attack and every bound skill of <paramref name="bindings"/>, in input order.</summary>
        public static List<QaKitAction> Kit(ConfigSet config, ActionBindings bindings)
        {
            var kit = new List<QaKitAction> { Describe(config, bindings.NormalAttack, 0, "Attack") };
            for (int i = 0; i < bindings.SkillSlots.Length; i++)
            {
                if (bindings.SkillSlots[i].Length > 0)
                    kit.Add(Describe(config, bindings.SkillSlots[i], i + 1, "Hotbar" + (i + 1).ToString(CultureInfo.InvariantCulture)));
            }
            return kit;
        }

        public static QaKitAction Describe(ConfigSet config, string actionId, int slot, string button)
        {
            var def = ActionDef.Find(config, actionId) ?? throw new QaFailure("no ActionDef " + actionId);
            var effects = Rows<ActionEffectDef>(config).Where(e => e.ActionId == actionId).ToList();
            int damage = effects.Where(DealsDamage).Sum(e => e.DamagePercent);
            bool heals = effects.Any(e => e.Kind == EffectKind.Heal && e.HasTarget(EffectTarget.Self));
            return new QaKitAction(def, slot, button, ReachCm(config, effects), damage, heals);
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

        public static QaFightChoice Choose(IReadOnlyList<QaKitAction> kit, in QaFightState state)
        {
            var self = state;
            bool Usable(QaKitAction a) => self.CanPay(a) && self.Ready(a);
            if (state.HpBelow(QaTimeouts.HealBelowPercent))
            {
                var heal = kit.FirstOrDefault(a => a.HealsSelf && Usable(a));
                if (heal != null)
                    return new QaFightChoice(QaFightMove.Use, heal);
                if (state.HpBelow(QaTimeouts.BackOffBelowPercent))
                    return new QaFightChoice(QaFightMove.BackOff, null);
            }
            QaKitAction? best = null;
            foreach (var action in kit)
            {
                if (!action.Damages || action.ReachCm < state.DistanceCm || !Usable(action))
                    continue;
                if (best == null || action.DamagePercent > best.DamagePercent)
                    best = action;
            }
            return best != null ? new QaFightChoice(QaFightMove.Use, best) : new QaFightChoice(QaFightMove.Wait, null);
        }

        private static IEnumerable<T> Rows<T>(ConfigSet config) =>
            config.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>() : Enumerable.Empty<T>();
    }
}
