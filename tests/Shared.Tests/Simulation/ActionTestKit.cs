using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Shared.Tests.Simulation;

/// <summary>Small action tables written out in code, so table tuning never changes these tests (20 Hz: 50 ms per tick).</summary>
internal static class ActionTestKit
{
    public const int TickRateHz = 20;

    public static readonly ChargePoolConfig DodgePool = new(3, 4000);
    public static readonly ChargePoolConfig ParryPool = new(3, 5000);

    public static ActionDef Action(string id, int netId, ActionKind kind, int startup, int active, int recovery,
        int channel = 0, int cooldown = 0, int buffer = 0, string[]? cancelableBy = null,
        ActionPhase cancelFrom = ActionPhase.Startup, ActionPhase[]? superArmor = null, int? stamina = null) => new()
    {
        Id = id,
        NetId = netId,
        Kind = kind,
        StartupMs = startup,
        ChannelMs = channel,
        ActiveMs = active,
        RecoveryMs = recovery,
        CooldownMs = cooldown,
        BufferWindowMs = buffer,
        CancelableBy = cancelableBy ?? [],
        CancelFrom = cancelFrom,
        SuperArmorPhases = superArmor ?? [],
        // Valid by default: attacks, skills and dodges cost stamina, dodge and parry use their pools, a dodge has i-frames
        // over its Active phase.
        StaminaCost = stamina ?? (kind == ActionKind.Parry ? 0 : 10),
        ChargePool = ActionDef.PoolOf(kind),
        IFramesStartMs = kind == ActionKind.Dodge ? startup + channel : 0,
        IFramesMs = kind == ActionKind.Dodge ? active : 0,
    };

    public static ActionEffectDef Effect(string id, string actionId, EffectKind kind, ActionPhase phase, int offset,
        int distanceCm = 0, int durationMs = 0) => new()
    {
        Id = id,
        ActionId = actionId,
        Kind = kind,
        Phase = phase,
        OffsetMs = offset,
        DistanceCm = distanceCm,
        DurationMs = durationMs,
        PoiseDamage = kind == EffectKind.Strike ? 10 : 0,
        FlinchPower = kind == EffectKind.Strike ? 10 : 0,
        FlinchMs = kind == EffectKind.Strike ? 300 : 0,
        DamagePercent = ActionEffectDef.DealsDamage(kind) ? 100 : 0,
        DamageKind = ActionEffectDef.DealsDamage(kind) ? EffectDamageKind.Physical : EffectDamageKind.None,
    };

    /// <summary>
    /// attack: 300/100/300 ms = 6/2/6 ticks, buffer 150 ms (3 ticks), dodge cancels it from Recovery, strike in Active,
    /// an effect at 100 ms into Recovery. dodge: 50/250/200 = 1/5/4 ticks. parry: 50/200/250. skill slot 1: 3 s cooldown.
    /// slot 2: channel 200/1000/100/300 then a ground area in Active. slot 3: dash 500 cm over 300 ms from 100 ms into
    /// Startup, then a strike. slot 4: super armor in Startup. Slot 5 is empty.
    /// Charges (spec 12.7): dodge 3, one per 4 s (80 ticks); parry 3, one per 5 s (100 ticks).
    /// </summary>
    public static ActionCatalog Catalog()
    {
        var actions = new[]
        {
            Action("attack", 1, ActionKind.Attack, 300, 100, 300, buffer: 150, cancelableBy: ["dodge"], cancelFrom: ActionPhase.Recovery),
            Action("dodge", 2, ActionKind.Dodge, 50, 250, 200, buffer: 150),
            Action("parry", 3, ActionKind.Parry, 50, 200, 250),
            Action("skill.cut", 101, ActionKind.Skill, 250, 150, 350, cooldown: 3000, buffer: 150, cancelableBy: ["dodge"], cancelFrom: ActionPhase.Recovery),
            Action("skill.circle", 102, ActionKind.Skill, 200, 100, 300, channel: 1000),
            Action("skill.charge", 103, ActionKind.Skill, 400, 150, 350),
            Action("skill.armored", 104, ActionKind.Skill, 300, 100, 300, superArmor: [ActionPhase.Startup]),
        };
        var effects = new[]
        {
            Effect("attack.strike", "attack", EffectKind.Strike, ActionPhase.Active, 0),
            Effect("attack.late", "attack", EffectKind.Heal, ActionPhase.Recovery, 100),
            Effect("skill.cut.strike", "skill.cut", EffectKind.Strike, ActionPhase.Active, 0),
            new ActionEffectDef
            {
                Id = "skill.circle.area", ActionId = "skill.circle", Kind = EffectKind.GroundArea, Phase = ActionPhase.Active,
                DurationMs = 5000, TickIntervalMs = 500, PoiseDamage = 5,
            },
            Effect("skill.charge.dash", "skill.charge", EffectKind.Dash, ActionPhase.Startup, 100, distanceCm: 500, durationMs: 300),
            Effect("skill.charge.strike", "skill.charge", EffectKind.Strike, ActionPhase.Active, 0),
        };
        var bindings = new ActionBindings
        {
            NormalAttack = "attack",
            Dodge = "dodge",
            Parry = "parry",
            SkillSlots = ["skill.cut", "skill.circle", "skill.charge", "skill.armored", ""],
        };
        return new ActionCatalog(TickRateHz, actions, effects, bindings, DodgePool, ParryPool);
    }

    public static ActionInput Input(InputActions held = InputActions.None, byte slot = 0, ushort aim = 0, ushort sequence = 0) =>
        new(true, held, slot, aim, sequence);

    public static ActionInput NoInput(ushort aim = 0) => new(false, InputActions.None, 0, aim, 0);

    /// <summary>One step with a fresh effect buffer; stamina is unlimited unless <paramref name="resources"/> says otherwise.</summary>
    public static ActionStepResult Step(ActionMachineState state, in ActionInput input, IActionResources? resources = null) =>
        ActionMachine.Step(state, input, resources ?? UnlimitedActionResources.Instance, new FiredEffect[ActionMachine.MaxFiredPerStep]);

    /// <summary>A stamina meter without regeneration: spends while it has enough, and records every request.</summary>
    public sealed class FakeStamina : IActionResources
    {
        public FakeStamina(int stamina) => Stamina = stamina;

        public int Stamina { get; set; }
        public List<int> Requests { get; } = [];

        public bool TrySpendStamina(int cost)
        {
            Requests.Add(cost);
            if (Stamina < cost)
                return false;
            Stamina -= cost;
            return true;
        }
    }

    /// <summary>Steps with nothing held until the state is idle; returns the steps taken.</summary>
    public static int RunToIdle(ActionMachineState state, int limit = 1000)
    {
        int steps = 0;
        while (state.IsBusy && steps < limit)
        {
            Step(state, Input());
            steps++;
        }
        return steps;
    }
}
