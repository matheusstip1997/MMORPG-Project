using Shared.Data;
using Shared.Data.Tables;

namespace BotClient.Brain;

/// <summary>One action of a class kit as a bot sees it, all from the config tables (backlog E).</summary>
public sealed class KitAction
{
    public KitAction(ActionDef def, int slot, int reachCm, int damagePercent, bool healsSelf)
    {
        Def = def;
        Slot = slot;
        ReachCm = reachCm;
        DamagePercent = damagePercent;
        HealsSelf = healsSelf;
    }

    public ActionDef Def { get; }

    public string Id => Def.Id;

    /// <summary>The ActionBindings input slot (1-15), or 0 for the normal attack.</summary>
    public int Slot { get; }

    /// <summary>How far its hits reach from the attacker's center: hit shapes or projectile range.</summary>
    public int ReachCm { get; }

    /// <summary>The skill multipliers of its damaging effects added up; 0 for no damage.</summary>
    public int DamagePercent { get; }

    /// <summary>It has a Heal effect whose targets include the caster.</summary>
    public bool HealsSelf { get; }

    public bool Damages => DamagePercent > 0;

    public override string ToString() => Id;
}

public enum FightMove
{
    /// <summary>Nothing usable now (stamina, MP, cooldowns, busy): wait.</summary>
    Wait = 0,
    Use = 1,
    /// <summary>Low HP and no heal ready: walk away for a while.</summary>
    BackOff = 2,
}

/// <summary>The bot's side of the fight when choosing: snapshot meters and what the predicted machine says is ready.</summary>
public readonly struct FightState
{
    public FightState(int hp, int maxHp, int mp, int stamina, double distanceCm, Func<KitAction, bool> ready)
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
    public double DistanceCm { get; }
    public Func<KitAction, bool> Ready { get; }

    public bool HpBelow(int percent) => MaxHp > 0 && (long)Hp * 100 < (long)MaxHp * percent;

    public bool CanPay(KitAction action) => Stamina >= action.Def.StaminaCost && Mp >= action.Def.MpCost;
}

/// <summary>
/// A class kit and how a bot fights with it: the QA kill step's rule (<c>QaFightPlan</c>, user decision 2026-10-25), with
/// its thresholds from <see cref="BotBehaviour"/>. Below the heal percent it uses a ready self-heal; below the back-off
/// percent with no heal ready it backs off; otherwise the ready damaging action in reach with the biggest multiplier.
/// Pure: config in, choice out. TODO(J): share one engine-free copy with QA.
/// </summary>
public sealed class BotKit
{
    private BotKit(IReadOnlyList<KitAction> actions)
    {
        Actions = actions;
        NormalAttack = actions[0];
    }

    /// <summary>The normal attack first, then every bound skill in slot order.</summary>
    public IReadOnlyList<KitAction> Actions { get; }

    public KitAction NormalAttack { get; }

    public static BotKit For(ConfigSet config, string classId)
    {
        var bindings = ActionBindings.For(config, classId) ?? throw new ArgumentException($"No ActionBindings row for '{classId}'.");
        var kit = new List<KitAction> { Describe(config, bindings.NormalAttack, 0) };
        for (int i = 0; i < bindings.SkillSlots.Length; i++)
        {
            if (bindings.SkillSlots[i].Length > 0)
                kit.Add(Describe(config, bindings.SkillSlots[i], i + 1));
        }
        return new BotKit(kit);
    }

    public static KitAction Describe(ConfigSet config, string actionId, int slot)
    {
        var def = ActionDef.Find(config, actionId) ?? throw new ArgumentException($"No ActionDef '{actionId}'.");
        var effects = Rows<ActionEffectDef>(config).Where(e => e.ActionId == actionId).ToList();
        int damage = effects.Where(DealsDamage).Sum(e => e.DamagePercent);
        bool heals = effects.Any(e => e.Kind == EffectKind.Heal && e.HasTarget(EffectTarget.Self));
        return new KitAction(def, slot, ReachCm(config, effects), damage, heals);
    }

    /// <summary>How far an action's damaging effects reach (monster attacks too: the bot's dodge trigger).</summary>
    public static int ReachCm(ConfigSet config, string actionId) =>
        ReachCm(config, Rows<ActionEffectDef>(config).Where(e => e.ActionId == actionId));

    public static bool DealsDamage(ActionEffectDef effect) =>
        effect.DamagePercent > 0
        && (effect.Kind == EffectKind.Strike || effect.Kind == EffectKind.Projectile || effect.Kind == EffectKind.GroundArea);

    public FightChoice Choose(in FightState state, BotBehaviour behaviour)
    {
        var self = state;
        bool Usable(KitAction a) => self.CanPay(a) && self.Ready(a);
        if (state.HpBelow(behaviour.HealBelowPercent))
        {
            var heal = Actions.FirstOrDefault(a => a.HealsSelf && Usable(a));
            if (heal != null)
                return new FightChoice(FightMove.Use, heal);
            if (state.HpBelow(behaviour.BackOffBelowPercent))
                return new FightChoice(FightMove.BackOff, null);
        }
        KitAction? best = null;
        foreach (var action in Actions)
        {
            if (!action.Damages || action.ReachCm < state.DistanceCm || !Usable(action))
                continue;
            if (best == null || action.DamagePercent > best.DamagePercent)
                best = action;
        }
        return best != null ? new FightChoice(FightMove.Use, best) : new FightChoice(FightMove.Wait, null);
    }

    private static int ReachCm(ConfigSet config, IEnumerable<ActionEffectDef> effects)
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

    private static IEnumerable<T> Rows<T>(ConfigSet config) =>
        config.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>() : Enumerable.Empty<T>();
}

public readonly struct FightChoice
{
    public FightChoice(FightMove move, KitAction? action)
    {
        Move = move;
        Action = action;
    }

    public FightMove Move { get; }
    public KitAction? Action { get; }

    public override string ToString() => Action != null ? $"{Move} {Action.Id}" : Move.ToString();
}
