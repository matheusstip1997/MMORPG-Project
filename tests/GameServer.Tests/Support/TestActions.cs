using Shared.Data.Tables;
using Shared.Simulation.Actions;

namespace GameServer.Tests.Support;

/// <summary>A small action catalog written out in code (20 Hz, 50 ms per tick), so tuning data/tables never changes tests.</summary>
public static class TestActions
{
    public const int AttackNetId = 1;
    public const int DodgeNetId = 2;
    public const int CooldownSkillNetId = 101;
    public const int DashSkillNetId = 102;

    /// <summary>
    /// attack: 100/100/100 ms (2/2/2 ticks), a strike in Active, dodge cancels it from Recovery. dodge: 50/250/200.
    /// parry: 50/200/250. Slot 1: a 300 ms skill with a 1 s cooldown. Slot 2: a dash of 400 cm over 200 ms from the start
    /// of Startup (100 cm per tick on ticks 0-3), then a strike. Slot 3 is empty.
    /// </summary>
    public static ActionCatalog Catalog() => new(20,
        [
            Action("attack", AttackNetId, ActionKind.Attack, 100, 100, 100, cancelableBy: ["dodge"], cancelFrom: ActionPhase.Recovery),
            Action("dodge", DodgeNetId, ActionKind.Dodge, 50, 250, 200),
            Action("parry", 3, ActionKind.Parry, 50, 200, 250),
            Action("skill.cooldown", CooldownSkillNetId, ActionKind.Skill, 100, 100, 100, cooldown: 1000),
            Action("skill.dash", DashSkillNetId, ActionKind.Skill, 300, 100, 100),
        ],
        [
            Strike("attack.strike", "attack"),
            new ActionEffectDef
            {
                Id = "skill.dash.dash", ActionId = "skill.dash", Kind = EffectKind.Dash, Phase = ActionPhase.Startup,
                DistanceCm = 400, DurationMs = 200,
            },
            Strike("skill.dash.strike", "skill.dash"),
        ],
        new ActionBindings
        {
            NormalAttack = "attack", Dodge = "dodge", Parry = "parry", SkillSlots = ["skill.cooldown", "skill.dash", ""],
        });

    private static ActionDef Action(string id, int netId, ActionKind kind, int startup, int active, int recovery,
        int cooldown = 0, string[]? cancelableBy = null, ActionPhase cancelFrom = ActionPhase.Startup) => new()
    {
        Id = id, NetId = netId, Kind = kind, StartupMs = startup, ActiveMs = active, RecoveryMs = recovery,
        CooldownMs = cooldown, CancelableBy = cancelableBy ?? [], CancelFrom = cancelFrom,
    };

    private static ActionEffectDef Strike(string id, string actionId) => new()
    {
        Id = id, ActionId = actionId, Kind = EffectKind.Strike, Phase = ActionPhase.Active,
        PoiseDamage = 10, FlinchPower = 10, FlinchMs = 300,
    };
}
