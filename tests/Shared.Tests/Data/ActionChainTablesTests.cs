using Shared.Data;
using Shared.Data.Tables;
using static Shared.Tests.Simulation.ActionTestKit;

namespace Shared.Tests.Data;

/// <summary>R-3.1-10: validator rules of attack chains (ActionDef chain fields, ActionBindings chain walk, UI strings).</summary>
public class ActionChainTablesTests
{
    /// <summary>A two-hit warrior chain (attack, attack.2) and an archer with a single shot, plus dodge and parry.</summary>
    private static ActionDef[] Actions() =>
    [
        Action("attack", 1, ActionKind.Attack, 300, 100, 300, buffer: 150, cancelableBy: ["dodge"], cancelFrom: ActionPhase.Recovery,
            chainNext: "attack.2", chainFrom: 300, chainLink: 500),
        Action("attack.2", 11, ActionKind.Attack, 400, 150, 450, buffer: 150, cancelableBy: ["dodge"], cancelFrom: ActionPhase.Recovery),
        Action("shot", 4, ActionKind.Attack, 350, 100, 300),
        Action("dodge", 2, ActionKind.Dodge, 50, 250, 200),
        Action("parry", 3, ActionKind.Parry, 50, 200, 250),
    ];

    private static ActionEffectDef[] Effects() =>
    [
        Effect("attack.strike", "attack", EffectKind.Strike, ActionPhase.Active, 0),
        Effect("attack.2.strike", "attack.2", EffectKind.Strike, ActionPhase.Active, 0),
    ];

    private static ActionBindings Bindings(string classId, string attack) => new()
    {
        ClassId = classId, NormalAttack = attack, Dodge = "dodge", Parry = "parry", SkillSlots = [],
    };

    private static ActionBindings[] AllBindings() => [Bindings("warrior", "attack"), Bindings("archer", "shot")];

    private static List<ValidationError> Validate(ActionDef[]? actions = null, ActionEffectDef[]? effects = null,
        ActionBindings[]? bindings = null, params ConfigTable[] extra)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ActionDef), 2, actions ?? Actions()));
        set.Add(new ConfigTable(typeof(ActionEffectDef), 2, effects ?? Effects()));
        set.Add(new ConfigTable(typeof(ActionBindings), 2, bindings ?? AllBindings()));
        set.Add(new ConfigTable(typeof(StartingValuesM1), 1, [new StartingValuesM1
        {
            DodgeDurationMs = 500, ParryWindowMs = 200, DodgeIFramesMs = 250, DodgeStaminaCost = 10, NormalAttackStaminaCost = 10,
        }]));
        foreach (var table in extra)
            set.Add(table);
        // Only the action tables' errors (StartingValuesM1 is mostly zeros; ClassDef and the monster's own refs are not loaded).
        return TableValidator.Validate(set)
            .Where(e => e.Table is "ActionDef" or "ActionEffectDef" || (e.Table == "ActionBindings" && e.Field != nameof(ActionBindings.ClassId)))
            .ToList();
    }

    private static ActionDef Row(ActionDef[] actions, string id) => actions.Single(a => a.Id == id);

    private static ValidationError SingleError(List<ValidationError> errors, string table, string field)
    {
        var error = Assert.Single(errors);
        Assert.Equal((table, field), (error.Table, error.Field));
        return error;
    }

    [Fact]
    public void The_sample_chain_is_valid() => Assert.Empty(Validate());

    [Fact]
    public void Only_attacks_chain_and_never_into_themselves()
    {
        var actions = Actions();
        Row(actions, "dodge").ChainNext = "attack.2";
        Row(actions, "dodge").ChainLinkMs = 400;
        Assert.Contains(Validate(actions), e => e.Field == nameof(ActionDef.ChainNext) && e.Message.Contains("only attacks"));

        actions = Actions();
        Row(actions, "attack").ChainNext = "attack";
        Assert.Contains(Validate(actions), e => e.Field == nameof(ActionDef.ChainNext) && e.Message.Contains("into itself"));
    }

    [Theory]
    [InlineData(300, 700)] // the link at the end of the action (700 ms)
    [InlineData(510, 500)] // the window opens after the link
    [InlineData(560, 600)] // opens after the 150 ms buffer window does (550 ms)
    public void The_chain_window_lies_inside_the_action(int from, int link)
    {
        var actions = Actions();
        Row(actions, "attack").ChainFromMs = from;
        Row(actions, "attack").ChainLinkMs = link;
        Assert.Single(Validate(actions), e => e.Table == "ActionDef");
    }

    [Fact]
    public void Without_a_next_hit_the_window_is_zero()
    {
        var actions = Actions();
        Row(actions, "shot").ChainLinkMs = 400;
        SingleError(Validate(actions), "ActionDef", nameof(ActionDef.ChainFromMs));
    }

    [Fact]
    public void The_link_comes_after_every_effect_of_the_hit()
    {
        var actions = Actions();
        Row(actions, "attack").ChainLinkMs = 300; // the strike fires at 300 ms (start of Active)
        Assert.Contains("'attack.strike'", SingleError(Validate(actions), "ActionDef", nameof(ActionDef.ChainLinkMs)).Message);
    }

    [Fact]
    public void A_chain_must_end()
    {
        var actions = Actions();
        var second = Row(actions, "attack.2");
        (second.ChainNext, second.ChainFromMs, second.ChainLinkMs) = ("attack", 400, 600);
        Assert.Contains("never ends", SingleError(Validate(actions), "ActionBindings", nameof(ActionBindings.NormalAttack)).Message);
    }

    [Fact]
    public void A_chain_belongs_to_a_class_normal_attack()
    {
        // The archer's shot chains, but no class uses it as its normal attack any more.
        var actions = Actions();
        var shot = Row(actions, "shot");
        (shot.ChainNext, shot.ChainFromMs, shot.ChainLinkMs, shot.BufferWindowMs) = ("attack.2", 350, 500, 150);
        var bindings = AllBindings();
        bindings[1].NormalAttack = "attack";
        Assert.Contains(Validate(actions, bindings: bindings), e => e.Field == nameof(ActionDef.ChainNext) && e.Message.Contains("not on the chain"));
    }

    [Fact]
    public void A_hit_after_the_first_belongs_to_one_class_only()
    {
        var actions = Actions();
        var shot = Row(actions, "shot");
        (shot.ChainNext, shot.ChainFromMs, shot.ChainLinkMs, shot.BufferWindowMs) = ("attack.2", 350, 500, 150);
        var errors = Validate(actions);
        Assert.Equal(2, errors.Count(e => e.Table == "ActionBindings" && e.Message.Contains("'attack.2' is also on the chain")));

        // A single-hit attack may be shared (no chain follows it).
        Assert.Empty(Validate(bindings: [Bindings("warrior", "attack"), Bindings("archer", "shot"), Bindings("mage", "shot")]));
    }

    [Fact]
    public void Every_hit_is_an_attack_without_cooldown_or_mp_and_costs_the_normal_attack_stamina()
    {
        var actions = Actions();
        Row(actions, "attack.2").StaminaCost = 7;
        Assert.Contains("'attack.2' costs 7 stamina", SingleError(Validate(actions), "ActionBindings", nameof(ActionBindings.NormalAttack)).Message);

        actions = Actions();
        Row(actions, "attack.2").CooldownMs = 1000;
        Assert.Contains("cooldown", SingleError(Validate(actions), "ActionBindings", nameof(ActionBindings.NormalAttack)).Message);

        actions = Actions();
        Row(actions, "attack.2").Kind = ActionKind.Skill;
        Row(actions, "attack.2").MpCost = 2;
        Assert.Contains(Validate(actions), e => e.Table == "ActionBindings" && e.Message.Contains("expected Attack"));
    }

    [Fact]
    public void Monsters_do_not_chain()
    {
        var monsters = new ConfigTable(typeof(MonsterDef), 2, [new MonsterDef { Id = "m1.demon", Attacks = ["attack.2"] }]);
        var errors = Validate(extra: monsters);
        Assert.Contains("MonsterDef 'm1.demon'", SingleError(errors, "ActionDef", nameof(ActionDef.ChainNext)).Message);
    }

    [Fact]
    public void Every_hit_of_a_chain_needs_its_ui_string()
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ActionDef), 2, Actions()));
        set.Add(new ConfigTable(typeof(ActionBindings), 2, AllBindings()));
        set.Add(new ConfigTable(typeof(UiStrings), 1, new object[]
        {
            new UiStrings { Id = "action.attack", Text = "Sword Attack" }, new UiStrings { Id = "action.shot", Text = "Shot" },
            new UiStrings { Id = "action.dodge", Text = "Dodge" }, new UiStrings { Id = "action.parry", Text = "Parry" },
        }));
        var missing = Assert.Single(UiStringCoverage.Check(set));
        Assert.Contains("'action.attack.2'", missing.Message);
    }
}
