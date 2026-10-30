using Shared.Data;
using Shared.Data.Tables;
using Shared.Tests.Simulation.Progression;

namespace Shared.Tests.Data;

/// <summary>
/// The consumable use tables (spec 3.4 consumable use, R-3.7-05; WP-M3-3): <c>ConsumableDef</c>, the <c>useItem</c> actions
/// with their consume effect and the food status. The committed rows pass; the validator catches the rule breaks.
/// </summary>
public class ConsumableTablesTests
{
    private static readonly ConfigSet Committed = CommittedTables.Load();

    private static List<string> SetErrors(object row, ConfigSet set)
    {
        var errors = new RowErrors();
        if (row is IValidatableRow own)
            own.Validate(errors);
        if (row is ISetValidatableRow cross)
            cross.Validate(set, errors);
        return errors.Items.Select(e => e.Field + ": " + e.Message).ToList();
    }

    [Fact]
    public void Every_committed_consumable_has_one_row_and_the_rows_pass()
    {
        Assert.Empty(ConsumableCoverage.Check(Committed));
        Assert.Empty(TableValidator.Validate(Committed));
        var rows = NetIds.Rows<ConsumableDef>(Committed).Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal);
        Assert.Equal(new[] { "m2.breadLoaf", "m2.potionHpSmall", "m2.potionMpSmall" }, rows);
    }

    [Fact]
    public void A_consumable_item_without_a_row_is_reported()
    {
        var set = CommittedTables.Load();
        set.Add(new ConfigTable(typeof(ConsumableDef), 1,
            NetIds.Rows<ConsumableDef>(Committed).Where(r => r.Id != "m2.breadLoaf").Cast<object>().ToList()));

        var error = Assert.Single(ConsumableCoverage.Check(set));
        Assert.Contains("m2.breadLoaf", error.Message);
    }

    [Fact]
    public void A_potion_adds_toxicity_only_and_food_satiety_only()
    {
        var potion = new ConsumableDef { Id = "m2.potionHpSmall", Kind = ConsumableKind.Potion, ActionId = "use.drink", HealHp = 10, Satiety = 5 };
        var food = new ConsumableDef { Id = "m2.breadLoaf", Kind = ConsumableKind.Food, ActionId = "use.eat", StatusId = "wellFed", Toxicity = 5 };

        Assert.Contains(SetErrors(potion, Committed), e => e.Contains("adds toxicity"));
        Assert.Contains(SetErrors(potion, Committed), e => e.Contains("adds no satiety"));
        Assert.Contains(SetErrors(food, Committed), e => e.Contains("adds satiety"));
        Assert.Contains(SetErrors(food, Committed), e => e.Contains("adds no toxicity"));
    }

    [Fact]
    public void The_action_must_be_a_useItem_action_and_the_status_a_buff_of_its_source()
    {
        var wrongAction = new ConsumableDef { Id = "m2.potionHpSmall", Kind = ConsumableKind.Potion, ActionId = "dodge", HealHp = 10, Toxicity = 5 };
        var wrongStatus = new ConsumableDef { Id = "m2.potionHpSmall", Kind = ConsumableKind.Potion, ActionId = "use.drink", StatusId = "wellFed", Toxicity = 5 };
        var notConsumable = new ConsumableDef { Id = "m1.demonHorn", Kind = ConsumableKind.Potion, ActionId = "use.drink", HealHp = 10, Toxicity = 5 };
        var overCap = new ConsumableDef { Id = "m2.potionHpSmall", Kind = ConsumableKind.Potion, ActionId = "use.drink", HealHp = 10, Toxicity = 101 };
        var noEffect = new ConsumableDef { Id = "m2.potionHpSmall", Kind = ConsumableKind.Potion, ActionId = "use.drink", Toxicity = 5 };

        Assert.Contains(SetErrors(wrongAction, Committed), e => e.Contains("useItem"));
        Assert.Contains(SetErrors(wrongStatus, Committed), e => e.Contains("source Potion")); // wellFed is a food buff
        Assert.Contains(SetErrors(notConsumable, Committed), e => e.Contains("only consumables"));
        Assert.Contains(SetErrors(overCap, Committed), e => e.Contains("toxicityMax"));
        Assert.Contains(SetErrors(noEffect, Committed), e => e.Contains("needs an effect"));
    }

    [Fact]
    public void A_useItem_action_costs_nothing_and_has_one_consume_effect_which_no_other_action_has()
    {
        var costly = new ActionDef { Id = "use.drink", NetId = 237, Kind = ActionKind.UseItem, StartupMs = 600, ActiveMs = 100, StaminaCost = 5 };
        Assert.Contains(SetErrors(costly, Committed), e => e.Contains("no stamina or MP"));

        var noConsume = new ActionDef { Id = "use.other", NetId = 4000, Kind = ActionKind.UseItem, StartupMs = 600, ActiveMs = 100 };
        Assert.Contains(SetErrors(noConsume, Committed), e => e.Contains("exactly one consume effect (0)"));

        var stray = new ActionEffectDef { Id = "attack.consume", ActionId = "use.drink", Kind = EffectKind.Consume, Phase = ActionPhase.Active };
        Assert.DoesNotContain(SetErrors(stray, Committed), e => e.Contains("belongs to a useItem"));
        var onSkill = new ActionEffectDef { Id = "x.consume", ActionId = "dodge", Kind = EffectKind.Consume, Phase = ActionPhase.Active };
        Assert.Contains(SetErrors(onSkill, Committed), e => e.Contains("belongs to a useItem action"));
    }

    [Fact]
    public void A_food_or_potion_source_is_only_for_a_buff()
    {
        var debuff = new StatusEffectDef { Id = "bad", NetId = 99, Kind = StatusEffectKind.Debuff, DurationMs = 1000, MaxStacks = 1, Source = StatusSource.Food };
        Assert.Contains(SetErrors(debuff, Committed), e => e.Contains("only a buff"));
        Assert.Equal(StatusSource.Food, StatusEffectDef.Find(Committed, "wellFed")!.Source);
    }
}
