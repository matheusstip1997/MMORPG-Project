using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using static GameServer.Tests.Support.AreaFixture;

namespace GameServer.Tests.Combat;

/// <summary>
/// The Burn build-up (spec 3.8, backlog D6; repository <c>BuildUpDef</c>: decay 10 per second after 3 s, resistance capped
/// at -50..75, repeat penalty 50% then 25% for 10 s) on hits through the real pipeline: gain, decay, resistance, the trigger
/// that applies <c>burn</c> credited to the hitter (R-3.8-09), the full meter until Burn ends, the repeat penalty, dodge and
/// parry (R-3.8-07), immunity, kills, reductions (R-3.8-04) and death (R-3.8-09). The test strike raises Burn by 25.
/// </summary>
public class BuildUpSystemTests
{
    private const uint Start = 10;
    private const string Strike = "attack.warrior.strike";
    private const string Lowering = "attack.archer.projectile";

    private static AreaFixture Fixture() =>
        new(tune: set => AddChanges(set, (Strike, 25), (Lowering, -10)));

    /// <summary>A hit that deals almost no damage (1% of the strike) but carries the strike's build-up change.</summary>
    private static HitOutcome Hit(AreaFixture f, Entity attacker, Entity target, uint tick, string effect = Strike) =>
        f.F.Hit(attacker, target, effect, tick, damagePercent: 1);

    private static StatusInstance BurnOn(Entity target)
    {
        var statuses = target.Combatant!.Statuses;
        for (int i = 0; i < statuses.Count; i++)
        {
            if (statuses[i].Definition.Id == "burn")
                return statuses[i];
        }
        throw new InvalidOperationException("no burn");
    }

    [Fact]
    public void Hits_fill_the_meter_and_the_one_that_fills_it_applies_Burn_credited_to_its_hitter()
    {
        var f = Fixture();
        var warrior = f.Player(1, classId: "warrior");
        var demon = f.Monster(2, hp: 100_000);

        for (uint i = 0; i < 3; i++)
        {
            Hit(f, warrior, demon, Start + i);
            Assert.Equal(25 * (int)(i + 1), f.Burn(demon, Start + i));
        }
        Assert.False(demon.Combatant!.Statuses.Has("burn"));
        Hit(f, warrior, demon, Start + 3);

        Assert.Equal(100, f.Burn(demon, Start + 3));
        var burn = BurnOn(demon);
        Assert.Equal((warrior.Id, warrior.CharacterId), (burn.SourceEntityId, burn.SourceCharacterId));
        Assert.True(f.Rules.Statuses.TryGet("burn", out var definition));
        var events = f.Dispatch();
        int triggered = events.FindIndex(e => e is BuildupTriggered);
        Assert.Equal(new BuildupTriggered(demon.Id, warrior.Id, 1, definition.NetId), events[triggered]);
        Assert.IsType<EffectApplied>(events[triggered + 1]);
    }

    [Fact]
    public void The_meter_stays_full_while_Burn_lasts_then_resets_and_the_next_gains_are_halved()
    {
        var f = Fixture();
        var warrior = f.Player(1, classId: "warrior");
        var demon = f.Monster(2, hp: 100_000);
        for (uint i = 0; i < 4; i++)
            Hit(f, warrior, demon, Start + i);
        uint applied = Start + 3;
        Assert.True(f.Rules.Statuses.TryGet("burn", out var definition));
        uint ends = applied + (uint)definition.DurationTicks;

        Hit(f, warrior, demon, applied + 1); // a locked meter gains nothing
        f.Run(applied + 1, ends - 1);
        Assert.Equal(100, f.Burn(demon, ends - 1));
        Assert.True(demon.Combatant!.Statuses.Has("burn"));

        f.Tick(ends); // phase 6 removes Burn: the meter resets (R-3.8-02)
        Assert.False(demon.Combatant.Statuses.Has("burn"));
        Assert.Equal(0, f.Burn(demon, ends));

        Hit(f, warrior, demon, ends + 1);
        Assert.Equal(13, f.Burn(demon, ends + 1)); // 25 x 50% = 12.5 points, shown rounded up (R-3.8-05)
        var damaged = f.F.Dispatch().OfType<Damaged>().Where(d => d.ActionId == 0).ToList(); // Burn's periodic ticks
        Assert.Equal(definition.DurationTicks / definition.TickIntervalTicks, damaged.Count);
        Assert.All(damaged, d => Assert.Equal(warrior.Id, d.SourceId)); // R-3.8-09
    }

    [Fact]
    public void A_second_trigger_inside_the_window_deepens_the_penalty()
    {
        var f = Fixture();
        var warrior = f.Player(1, classId: "warrior");
        var demon = f.Monster(2, hp: 100_000);
        for (uint i = 0; i < 4; i++)
            Hit(f, warrior, demon, Start + i);
        Assert.True(f.Rules.Statuses.TryGet("burn", out var definition));
        uint ends = Start + 3 + (uint)definition.DurationTicks;
        f.Run(Start + 4, ends);

        // 12.5 points per hit at level 1: the 8th hit fills the meter inside the 10 s window.
        uint tick = ends + 1;
        for (int i = 0; i < 8; i++)
            Hit(f, warrior, demon, tick++);
        Assert.Equal(100, f.Burn(demon, tick - 1));
        Assert.Equal(2, demon.Combatant!.BuildUps[0].PenaltyLevel);
    }

    [Fact]
    public void The_meter_decays_after_the_delay_without_a_gain()
    {
        var f = Fixture();
        var warrior = f.Player(1, classId: "warrior");
        var demon = f.Monster(2, hp: 100_000);
        Hit(f, warrior, demon, Start);

        Assert.Equal(25, f.Burn(demon, Start + 60));  // decayDelayMs 3000
        Assert.Equal(15, f.Burn(demon, Start + 80));  // then 10 points per second
        Assert.Equal(0, f.Burn(demon, Start + 120));
    }

    [Theory]
    [InlineData(40, 15)]   // 25 x 60%
    [InlineData(90, 7)]    // capped at 75%: 6.25, rounded up
    [InlineData(-80, 38)]  // weakness capped at -50%: 37.5
    public void Gains_are_scaled_by_the_fire_resistance(int fireRes, int points)
    {
        var f = Fixture();
        var warrior = f.Player(1, classId: "warrior");
        var demon = f.Monster(2, hp: 100_000);
        var config = f.Rules.Config;
        demon.Combatant!.Stats.SetBase(config.ElementResistStat(config.ElementIndex(Element.Fire)), fireRes);

        Hit(f, warrior, demon, Start);

        Assert.Equal(points, f.Burn(demon, Start));
    }

    [Fact]
    public void A_target_immune_to_Burn_gains_no_meter()
    {
        var f = Fixture();
        var warrior = f.Player(1, classId: "warrior");
        var boss = f.Monster(2, hp: 100_000);
        boss.Combatant!.StatusImmuneTags = ["fire"]; // the burn status's tags (R-3.7-02)
        for (uint i = 0; i < 4; i++)
            Hit(f, warrior, boss, Start + i);
        Assert.Equal(0, f.Burn(boss, Start + 3));
        Assert.False(boss.Combatant.Statuses.Has("burn"));
    }

    [Fact]
    public void A_dodged_hit_and_a_successful_parry_apply_no_build_up_and_an_uncolored_parry_halves_it()
    {
        var f = Fixture();
        var monster = f.Monster(1);
        var dodger = f.Player(2);
        f.F.Step(dodger, InputActions.Dodge, Start - 1);
        f.F.Step(dodger, InputActions.Dodge, Start);
        Assert.Equal(HitOutcome.Dodged, Hit(f, monster, dodger, Start));
        Assert.Equal(0, f.Burn(dodger, Start));

        var reduced = f.Player(3);
        f.F.Step(reduced, InputActions.Parry, Start - 1, sequence: 7);
        f.F.Step(reduced, InputActions.Parry, Start, sequence: 8);
        Assert.Equal(HitOutcome.Damaged, Hit(f, monster, reduced, Start));
        Assert.Equal(13, f.Burn(reduced, Start)); // uncolored: 50% off, like the damage (R-3.8-07)

        f.F.Catalog.Find("attack.warrior")!.Def.ParryClassPve = ParryColor.Green;
        var parrier = f.Player(4);
        f.F.Step(parrier, InputActions.Parry, Start - 1, sequence: 7);
        f.F.Step(parrier, InputActions.Parry, Start, sequence: 8);
        Assert.Equal(HitOutcome.Parried, Hit(f, monster, parrier, Start));
        Assert.Equal(0, f.Burn(parrier, Start));
    }

    [Fact]
    public void A_killing_hit_triggers_nothing_and_death_empties_the_meters()
    {
        var f = Fixture();
        var warrior = f.Player(1, classId: "warrior");
        var demon = f.Monster(2, hp: 100_000);
        for (uint i = 0; i < 3; i++)
            Hit(f, warrior, demon, Start + i);
        demon.Combatant!.Meters.Set(Shared.Simulation.Stats.ResourceKind.Hp, 1, f.F.NowMs(Start + 3));

        Assert.Equal(HitOutcome.Killed, Hit(f, warrior, demon, Start + 3));

        Assert.Equal(0, f.Burn(demon, Start + 3));
        Assert.False(demon.Combatant.Statuses.Has("burn"));
        Assert.DoesNotContain(f.Dispatch(), e => e is BuildupTriggered);
    }

    [Fact]
    public void A_negative_change_and_a_cleanse_lower_the_meter_and_a_cleansed_Burn_resets_it()
    {
        var f = Fixture();
        var monster = f.Monster(1);
        var player = f.Player(2);
        for (uint i = 0; i < 3; i++)
            Hit(f, monster, player, Start + i);
        Assert.Equal(75, f.Burn(player, Start + 2));

        Hit(f, monster, player, Start + 3, Lowering); // Burn -10 on this hit, not scaled by resistance
        Assert.Equal(65, f.Burn(player, Start + 3));

        f.Statuses.Queue.Add(player, f.Effect(Cleanse), Start + 4); // Simple Cleanse: every meter -40 (R-3.8-04)
        f.Statuses.Run(Start + 4, f.Entities);
        Assert.Equal(25, f.Burn(player, Start + 4));

        for (uint i = 5; i < 8; i++)
            Hit(f, monster, player, Start + i);
        Assert.True(player.Combatant!.Statuses.Has("burn"));
        Assert.Equal(100, f.Burn(player, Start + 7));
        f.Statuses.Queue.Add(player, f.Effect(Cleanse), Start + 8); // a locked meter is not lowered
        f.Statuses.Run(Start + 8, f.Entities);
        Assert.Equal(100, f.Burn(player, Start + 8));

        f.Statuses.Queue.Add(player, f.Effect(CleanseDebuffs), Start + 9); // cleansing Burn ends it: the meter resets
        f.Statuses.Run(Start + 9, f.Entities);
        Assert.False(player.Combatant.Statuses.Has("burn"));
        Assert.Equal(0, f.Burn(player, Start + 9));
        Assert.False(player.Combatant.BuildUps[0].Locked);
    }

    [Fact]
    public void Death_and_a_leash_reset_empty_the_meters()
    {
        var f = Fixture();
        var warrior = f.Player(1, classId: "warrior");
        var demon = f.Monster(2, hp: 100_000);
        var other = f.Monster(3, hp: 100_000);
        Hit(f, warrior, demon, Start);
        Hit(f, warrior, other, Start);

        demon.Combatant!.Die(Start + 1);
        other.Combatant!.RestoreFull(f.F.NowMs(Start + 1));

        Assert.Equal(0, f.Burn(demon, Start + 1));
        Assert.Equal(0, f.Burn(other, Start + 1));
    }

    [Fact]
    public void A_fire_circle_burns_whoever_stands_in_it_on_its_fifth_tick()
    {
        var f = new AreaFixture();
        var mage = f.Player(1);
        var demon = f.Monster(2, 0, 300, hp: 100_000); // the circle: 3 m radius, 3 m in front (north)
        var area = f.Place(mage, FireArea, Start)!;
        Assert.Equal((100, 10), (area.DurationTicks, area.IntervalTicks)); // 5000 / 500 ms

        f.Run(Start, Start + 39);
        Assert.Equal(80, f.Burn(demon, Start + 39));
        Assert.False(demon.Combatant!.Statuses.Has("burn"));
        f.Tick(Start + 40);
        Assert.True(demon.Combatant.Statuses.Has("burn"));
        Assert.Equal(mage.Id, BurnOn(demon).SourceEntityId);
    }
}
