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
        ActionPhase cancelFrom = ActionPhase.Startup, ActionPhase[]? superArmor = null, int? stamina = null, int? mp = null,
        string chainNext = "", int chainFrom = 0, int chainLink = 0) => new()
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
        // over its Active phase, and only skills cost MP (R-3.1-04; 2, within any class pool of these tests).
        StaminaCost = stamina ?? (kind == ActionKind.Parry ? 0 : 10),
        MpCost = mp ?? (kind == ActionKind.Skill ? 2 : 0),
        ChargePool = ActionDef.PoolOf(kind),
        IFramesStartMs = kind == ActionKind.Dodge ? startup + channel : 0,
        IFramesMs = kind == ActionKind.Dodge ? active : 0,
        ChainNext = chainNext,
        ChainFromMs = chainFrom,
        ChainLinkMs = chainLink,
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
        DamagePercent = ActionEffectDef.DealsDamage(kind) || kind == EffectKind.GroundArea ? 100 : 0,
        DamageKind = ActionEffectDef.DealsDamage(kind) || kind == EffectKind.GroundArea ? EffectDamageKind.Physical : EffectDamageKind.None,
        // R-3.1-07: ground areas hit enemies; heals and status effects default to the caster (no shapes needed).
        Targets = kind == EffectKind.GroundArea ? [EffectTarget.Enemies]
            : ActionEffectDef.IsFiltered(kind) ? [EffectTarget.Self] : [],
        HealPercent = kind == EffectKind.Heal ? 100 : 0,
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
                Id = "skill.circle.area", ActionId = "skill.circle", Kind = EffectKind.GroundArea, Phase = ActionPhase.Active, Targets = [EffectTarget.Enemies],
                DurationMs = 5000, TickIntervalMs = 500, PoiseDamage = 5, DamagePercent = 40, DamageKind = EffectDamageKind.Magic,
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

    /// <summary>
    /// R-3.1-10, a chain of 4 attacks bound to left click. hit1: 300/100/300 ms = 6/2/6 ticks, chain window from tick 6
    /// (Active), link at tick 10 (100 ms into Recovery). hit2, hit3: 250/100/300 = 5/2/6, from tick 5, link at tick 9.
    /// hit4 (ends the chain): 400/150/450 = 8/3/9. Every hit: buffer 150 ms, a strike at the start of Active, the dodge
    /// cancels it from Recovery. Slot 1: skill.cut (dodge cancels it from Recovery). Dodge and parry as in <see cref="Catalog"/>.
    /// </summary>
    public static ActionCatalog ChainCatalog()
    {
        string[] dodge = ["dodge"];
        var actions = new[]
        {
            Action("hit1", 1, ActionKind.Attack, 300, 100, 300, buffer: 150, cancelableBy: dodge, cancelFrom: ActionPhase.Recovery,
                chainNext: "hit2", chainFrom: 300, chainLink: 500),
            Action("hit2", 11, ActionKind.Attack, 250, 100, 300, buffer: 150, cancelableBy: dodge, cancelFrom: ActionPhase.Recovery,
                chainNext: "hit3", chainFrom: 250, chainLink: 450),
            Action("hit3", 12, ActionKind.Attack, 250, 100, 300, buffer: 150, cancelableBy: dodge, cancelFrom: ActionPhase.Recovery,
                chainNext: "hit4", chainFrom: 250, chainLink: 450),
            Action("hit4", 13, ActionKind.Attack, 400, 150, 450, buffer: 150, cancelableBy: dodge, cancelFrom: ActionPhase.Recovery),
            Action("dodge", 2, ActionKind.Dodge, 50, 250, 200, buffer: 150),
            Action("parry", 3, ActionKind.Parry, 50, 200, 250),
            Action("skill.cut", 101, ActionKind.Skill, 250, 150, 350, cooldown: 3000, buffer: 150, cancelableBy: dodge, cancelFrom: ActionPhase.Recovery),
        };
        var effects = new[]
        {
            Effect("hit1.strike", "hit1", EffectKind.Strike, ActionPhase.Active, 0),
            Effect("hit2.strike", "hit2", EffectKind.Strike, ActionPhase.Active, 0),
            Effect("hit3.strike", "hit3", EffectKind.Strike, ActionPhase.Active, 0),
            Effect("hit4.strike", "hit4", EffectKind.Strike, ActionPhase.Active, 0),
            Effect("skill.cut.strike", "skill.cut", EffectKind.Strike, ActionPhase.Active, 0),
        };
        var bindings = new ActionBindings { NormalAttack = "hit1", Dodge = "dodge", Parry = "parry", SkillSlots = ["skill.cut"] };
        return new ActionCatalog(TickRateHz, actions, effects, bindings, DodgePool, ParryPool);
    }

    public static ActionInput Input(InputActions held = InputActions.None, byte slot = 0, ushort aim = 0, ushort sequence = 0) =>
        new(true, held, slot, aim, sequence);

    public static ActionInput NoInput(ushort aim = 0) => new(false, InputActions.None, 0, aim, 0);

    /// <summary>One step with a fresh effect buffer; stamina is unlimited unless <paramref name="resources"/> says otherwise.</summary>
    public static ActionStepResult Step(ActionMachineState state, in ActionInput input, IActionResources? resources = null) =>
        ActionMachine.Step(state, input, resources ?? UnlimitedActionResources.Instance, new FiredEffect[ActionMachine.MaxFiredPerStep]);

    /// <summary>
    /// Stamina and MP meters without regeneration: spends both while they have enough (stamina checked first, R-3.1-04),
    /// and records every request.
    /// </summary>
    public sealed class FakeResources : IActionResources
    {
        public FakeResources(int stamina, int mp = 1_000)
        {
            Stamina = stamina;
            Mp = mp;
        }

        public int Stamina { get; set; }
        public int Mp { get; set; }

        /// <summary>The stamina cost of every request.</summary>
        public List<int> Requests { get; } = [];

        /// <summary>The MP cost of every request.</summary>
        public List<int> MpRequests { get; } = [];

        public ActionReason TrySpend(int staminaCost, int mpCost)
        {
            Requests.Add(staminaCost);
            MpRequests.Add(mpCost);
            if (Stamina < staminaCost)
                return ActionReason.NoStamina;
            if (Mp < mpCost)
                return ActionReason.NoMp;
            Stamina -= staminaCost;
            Mp -= mpCost;
            return ActionReason.None;
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
