using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Combat;

/// <summary>
/// WP-M2-9 in the server's status and hit pipeline (spec 3.7 "M2 status list", 3.8 "M2 numbers"; repository rows):
/// on-hit statuses riding on hits (a dodge and any parry stop them: user answer 2026-10-28), the stun that holds the action
/// machine, cleanse and strip newest first with a count (R-3.7-03), the bursts of Cold and Blood loss with the boss cap
/// (R-3.8-08), the Bystial bonus captured for periodic damage (R-14.2-05, user answer 2026-10-28), statuses that end with
/// their caster's phase (Concentration), and the blinded attacker whose strikes hit nothing.
/// </summary>
public class StatusMechanicsTests
{
    private const uint Start = 10;
    private const string Strike = "attack.warrior.strike";

    private static StatusDefinition Status(AreaFixture f, string id)
    {
        Assert.True(f.Rules.Statuses.TryGet(id, out var definition));
        return definition;
    }

    /// <summary>A fixture whose warrior strike carries <paramref name="onHit"/> as its on-hit status.</summary>
    private static AreaFixture WithOnHit(string onHit)
    {
        var f = new AreaFixture();
        f.Effect(Strike).StatusId = onHit;
        return f;
    }

    private static int StacksOf(Entity target, string statusId)
    {
        var statuses = target.Combatant!.Statuses;
        for (int i = 0; i < statuses.Count; i++)
        {
            if (statuses[i].Definition.Id == statusId)
                return statuses[i].Stacks;
        }
        return 0;
    }

    [Fact]
    public void An_on_hit_status_lands_with_each_hit_and_stacks_to_its_cap()
    {
        var f = WithOnHit("defenceDown");
        var warrior = f.Player(1, classId: "warrior");
        var demon = f.Monster(2, hp: 100_000);
        int def = demon.Combatant!.Stats.Get(f.Rules.Config.Ids.Def);

        for (uint i = 0; i < 4; i++)
            Assert.Equal(HitOutcome.Damaged, f.F.Hit(warrior, demon, Strike, Start + i, damagePercent: 1));

        Assert.Equal(3, StacksOf(demon, "defenceDown")); // maxStacks 3: the stat debuff cap (StatusSettings)
        Assert.Equal(def * 70 / 100, demon.Combatant.Stats.Get(f.Rules.Config.Ids.Def)); // -10 % per stack
        var applied = f.Dispatch().OfType<EffectApplied>().ToList();
        Assert.Equal(4, applied.Count);
        Assert.All(applied, e => Assert.Equal(warrior.Id, e.SourceId));
    }

    [Fact]
    public void A_dodge_any_parry_and_a_killing_hit_apply_no_on_hit_status()
    {
        var f = new AreaFixture();
        f.Effect("monster.demon.claw.strike").StatusId = "stunned";
        var demon = f.Monster(1);
        var dodger = f.Player(2);
        f.F.Step(dodger, InputActions.Dodge, Start - 1);
        f.F.Step(dodger, InputActions.Dodge, Start);
        Assert.Equal(HitOutcome.Dodged, f.F.Hit(demon, dodger, "monster.demon.claw.strike", Start));
        Assert.Equal(0, StacksOf(dodger, "stunned"));

        var reduced = f.Player(3);
        f.F.Step(reduced, InputActions.Parry, Start - 1, sequence: 7);
        f.F.Step(reduced, InputActions.Parry, Start, sequence: 8);
        Assert.Equal(HitOutcome.Damaged, f.F.Hit(demon, reduced, "monster.demon.claw.strike", Start));
        Assert.Equal(0, StacksOf(reduced, "stunned")); // an uncolored (Reduced) parry blocks it too

        var plain = f.Player(4);
        Assert.Equal(HitOutcome.Damaged, f.F.Hit(demon, plain, "monster.demon.claw.strike", Start));
        Assert.Equal(1, StacksOf(plain, "stunned"));
        Assert.Equal(ActionState.Stunned, plain.Actions.State);

        var warrior = f.Player(5, classId: "warrior");
        f.Effect(Strike).StatusId = "defenceDown";
        var dying = f.Monster(6);
        dying.Combatant!.Meters.Set(ResourceKind.Hp, 1, f.F.NowMs(Start));
        Assert.Equal(HitOutcome.Killed, f.F.Hit(warrior, dying, Strike, Start));
        Assert.Equal(0, dying.Combatant.Statuses.Count);
    }

    [Fact]
    public void An_immune_target_takes_no_on_hit_status()
    {
        var f = WithOnHit("stunned");
        var warrior = f.Player(1, classId: "warrior");
        var boss = f.Monster(2, hp: 100_000);
        boss.Combatant!.StatusImmuneTags = ["stun"]; // R-3.7-02
        f.F.Hit(warrior, boss, Strike, Start, damagePercent: 1);
        Assert.Equal(0, StacksOf(boss, "stunned"));
        Assert.NotEqual(ActionState.Stunned, boss.Actions.State);
    }

    [Fact]
    public void A_stun_holds_the_machine_for_its_duration_and_ends_with_the_status()
    {
        var f = new AreaFixture();
        var warrior = f.Player(1, classId: "warrior");
        var demon = f.Monster(2, hp: 100_000);
        var stunned = Status(f, "stunned");
        Assert.True(f.Statuses.Apply(warrior, demon, stunned, Start));
        Assert.Equal((ActionState.Stunned, 10), (demon.Actions.State, demon.Actions.InterruptTicks)); // 500 ms
        Assert.True(demon.Actions.IsBusy); // no movement and no attack (MonsterAi.Move, ActionMachine)

        // A leash reset ends every status, the stun with it.
        f.Statuses.EndAll(demon, StatusEndReason.Reset, Start + 1);
        Assert.Equal(ActionState.Idle, demon.Actions.State);

        // Expiry: removed in phase 6 of its end tick, and the machine is let go at the latest then.
        Assert.True(f.Statuses.Apply(warrior, demon, stunned, Start + 2));
        f.Run(Start + 3, Start + 2 + (uint)stunned.DurationTicks);
        Assert.False(demon.Combatant!.Statuses.Has("stunned"));
        Assert.NotEqual(ActionState.Stunned, demon.Actions.State);
    }

    [Fact]
    public void A_cleanse_with_a_count_removes_the_newest_dispellable_debuffs_first_and_a_refresh_counts_as_new()
    {
        var f = new AreaFixture();
        f.Effect(AreaFixture.CleanseDebuffs).RemoveCount = 2; // Simple Cleanse 2 (spec 3.7 M2 status list)
        var player = f.Player(1);
        var demon = f.Monster(2);
        f.Statuses.Apply(demon, player, Status(f, "weakened"), Start);
        f.Statuses.Apply(demon, player, Status(f, "burn"), Start);
        f.Statuses.Apply(demon, player, Status(f, "defenceDown"), Start);
        f.Statuses.Apply(demon, player, Status(f, "stunned"), Start); // not dispellable: never cleansed by kind
        f.Statuses.Apply(demon, player, Status(f, "weakened"), Start + 1); // the refresh makes it the newest

        f.Statuses.Queue.Add(player, f.Effect(AreaFixture.CleanseDebuffs), Start + 2);
        f.Statuses.Run(Start + 2, f.Entities);

        Assert.Equal(["burn", "stunned"], Ids(player));
        Assert.Equal(
            [Status(f, "weakened").NetId, Status(f, "defenceDown").NetId],
            f.Dispatch().OfType<EffectCleansed>().Select(e => e.StatusId));

        // A full cleanse (count 0) takes every dispellable debuff.
        f.Effect(AreaFixture.CleanseDebuffs).RemoveCount = 0;
        f.Statuses.Apply(demon, player, Status(f, "weakened"), Start + 3);
        f.Statuses.Apply(demon, player, Status(f, "poison"), Start + 3);
        f.Statuses.Queue.Add(player, f.Effect(AreaFixture.CleanseDebuffs), Start + 4);
        f.Statuses.Run(Start + 4, f.Entities);
        Assert.Equal(["stunned"], Ids(player));
    }

    [Fact]
    public void A_strip_with_a_count_removes_the_newest_buffs_first()
    {
        var f = new AreaFixture();
        var strip = f.Effect(AreaFixture.CleanseDebuffs);
        strip.StatusKind = StatusEffectKind.Buff;
        strip.RemoveCount = 1;
        var player = f.Player(1);
        f.Statuses.Apply(player, player, Status(f, "staminaBuff"), Start);
        f.Statuses.Apply(player, player, Status(f, "focus"), Start);

        f.Statuses.Queue.Add(player, strip, Start + 1);
        f.Statuses.Run(Start + 1, f.Entities);

        Assert.Equal(["staminaBuff"], Ids(player));
    }

    [Fact]
    public void Blood_loss_takes_15_percent_of_max_hp_once_and_a_boss_at_most_its_cap()
    {
        var f = new AreaFixture();
        var warrior = f.Player(1, classId: "warrior");
        var demon = f.Monster(2, hp: 10_000);
        var boss = f.Monster(3, hp: 10_000);
        boss.Combatant!.MaxHpEffectCapPercent = 2; // MonsterDef.maxHpEffectCapPercent (R-3.8-08)
        var bloodLoss = Status(f, "bloodLoss");
        f.Statuses.Apply(warrior, demon, bloodLoss, Start);
        f.Statuses.Apply(warrior, boss, bloodLoss, Start);
        Assert.Equal(10_000, demon.Combatant!.Hp(f.F.NowMs(Start))); // the burst waits for the next phase 6

        f.Run(Start + 1, Start + 1 + (uint)bloodLoss.DurationTicks);

        Assert.Equal(8_500, demon.Combatant.Hp(f.F.NowMs(Start + 1)));
        Assert.Equal(9_800, boss.Combatant.Hp(f.F.NowMs(Start + 1)));
        var damaged = f.F.Dispatch().OfType<Damaged>().Where(d => d.ActionId == 0).ToList();
        Assert.Equal([(demon.Id, 1_500), (boss.Id, 200)], damaged.Select(d => (d.TargetId, d.Amount)));
        Assert.All(damaged, d => Assert.Equal(warrior.Id, d.SourceId)); // R-3.8-09
    }

    [Fact]
    public void Cold_bursts_once_and_slows_while_it_lasts()
    {
        var f = new AreaFixture();
        var mage = f.Player(1, classId: "mage");
        var demon = f.Monster(2, hp: 100_000);
        int speedStat = f.Rules.Config.Ids.MoveSpeedPercent;
        var cold = Status(f, "cold");
        f.Statuses.Apply(mage, demon, cold, Start);
        Assert.Equal(-30, demon.Combatant!.Stats.Get(speedStat));

        f.Run(Start + 1, Start + 3);
        Assert.Single(f.F.Dispatch().OfType<Damaged>().Where(d => d.ActionId == 0)); // one burst, no ticks
        f.Run(Start + 4, Start + (uint)cold.DurationTicks);
        Assert.Equal(0, demon.Combatant.Stats.Get(speedStat));
    }

    [Fact]
    public void Periodic_damage_captures_the_book_bonus_against_the_target_when_applied()
    {
        var plain = new AreaFixture();
        var booked = new AreaFixture();
        booked.F.Resolver.CardBonus = new FixedBonus(50);
        long Total(AreaFixture f)
        {
            var mage = f.Player(1, classId: "mage");
            var demon = f.Monster(2, hp: 100_000);
            f.Statuses.Apply(mage, demon, Status(f, "burn"), Start);
            if (f.F.Resolver.CardBonus is FixedBonus bonus)
                bonus.Percent = 0; // captured when applied: a later change does not reach the ticks
            f.Run(Start + 1, Start + 1 + (uint)Status(f, "burn").DurationTicks);
            return f.F.Dispatch().OfType<Damaged>().Where(d => d.ActionId == 0).Sum(d => (long)d.Amount);
        }

        long without = Total(plain), with = Total(booked);
        Assert.True(with > without, $"{with} with the book, {without} without");
    }

    [Fact]
    public void A_status_that_ends_with_its_phase_is_removed_when_the_casters_action_leaves_it()
    {
        var f = new AreaFixture();
        var mage = f.Player(1, classId: "mage");
        var concentration = Status(f, "concentration");
        f.F.Step(mage, InputActions.Attack, Start);
        Assert.Equal(ActionState.Startup, mage.Actions.State);
        Assert.True(f.Statuses.Apply(mage, mage, concentration, Start, mage.Actions.ActionIndex, ActionState.Startup));
        int regen = mage.Combatant!.Stats.Get(f.Rules.Config.Ids.MpRegen);

        uint tick = Start;
        while (mage.Actions.State == ActionState.Startup)
        {
            f.Statuses.Run(tick, f.Entities);
            Assert.True(mage.Combatant.Statuses.Has("concentration"));
            f.F.Step(mage, InputActions.None, ++tick);
        }
        f.Statuses.Run(tick, f.Entities);

        Assert.False(mage.Combatant.Statuses.Has("concentration"));
        Assert.True(mage.Combatant.Stats.Get(f.Rules.Config.Ids.MpRegen) < regen);
        Assert.Contains(new EffectRemoved(mage.Id, concentration.NetId, StatusEndReason.ActionEnded), f.Dispatch().OfType<EffectRemoved>());
    }

    [Fact]
    public void A_blinded_attackers_strike_hits_nothing()
    {
        var f = new AreaFixture();
        var warrior = f.Player(1, classId: "warrior");
        var demon = f.Monster(2, yCm: 100, hp: 100_000);
        var strikes = new StrikeResolver(f.Grid, f.Rules, f.F.Resolver);
        var queue = new StrikeQueue();
        f.Statuses.Apply(demon, warrior, Status(f, "blinded"), Start);

        queue.Add(warrior, f.Effect(Strike), Start + 1);
        strikes.Resolve(queue);
        Assert.Equal(1, strikes.BlindedStrikes);
        Assert.Equal(100_000, demon.Combatant!.Hp(f.F.NowMs(Start + 1)));

        f.Run(Start + 1, Start + 1 + (uint)Status(f, "blinded").DurationTicks);
        queue.Add(warrior, f.Effect(Strike), Start + 30);
        strikes.Resolve(queue);
        Assert.True(demon.Combatant.Hp(f.F.NowMs(Start + 30)) < 100_000); // the blind ran out
    }

    private static List<string> Ids(Entity entity)
    {
        var statuses = entity.Combatant!.Statuses;
        var ids = new List<string>();
        for (int i = 0; i < statuses.Count; i++)
            ids.Add(statuses[i].Definition.Id);
        return ids;
    }

    private sealed class FixedBonus(int percent) : ICardCombatBonus
    {
        public int Percent { get; set; } = percent;

        public int DamagePercent(Entity attacker, Entity target) => Percent;
    }
}
