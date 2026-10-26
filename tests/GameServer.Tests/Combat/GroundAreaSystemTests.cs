using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;
using static GameServer.Tests.Support.AreaFixture;

namespace GameServer.Tests.Combat;

/// <summary>
/// Ground areas (spec 3.1 <c>GroundArea</c>, R-3.1-07, backlog D6) on their own with the real hit pipeline: ticks at the
/// edges of the duration, the target filter, damage and poise without a flinch (R-3.3-09), heals per tick, dodge i-frames
/// and no parry (user decision 2026-10-24), the caps and the pool, and the owner rules (user decision 2026-10-24).
/// </summary>
public class GroundAreaSystemTests
{
    private const uint Start = 10;

    private static int HitsOn(AreaFixture f, Entity target, HitKind kind = HitKind.Damaged) =>
        f.F.Resolver.Notices.Count(n => n.TargetId == target.Id && n.Kind == kind);

    [Fact]
    public void An_area_ticks_on_the_tick_it_is_placed_then_every_interval_and_ends_after_its_duration()
    {
        // 1000 ms / 300 ms at 20 Hz: 20 ticks, a tick every 6: at Start, +6, +12, +18. The duration is not a multiple of the
        // interval, so there is no partial tick at +20.
        var f = new AreaFixture();
        var mage = f.Player(1);
        var monster = f.Monster(2, 0, 100, hp: 100_000);
        var area = f.Place(mage, EnemyArea, Start)!;
        Assert.Equal((20, 6), (area.DurationTicks, area.IntervalTicks));

        var pulses = new List<uint>();
        for (uint tick = Start; tick <= Start + 25; tick++)
        {
            int before = HitsOn(f, monster);
            f.Tick(tick);
            if (HitsOn(f, monster) > before)
                pulses.Add(tick);
        }

        Assert.Equal([Start, Start + 6, Start + 12, Start + 18], pulses);
        Assert.Equal(4, area.Pulses);
        Assert.False(area.IsLive);
        Assert.Equal((GroundAreaDespawnReason.Expired, Start + 20), (area.Reason, area.EndTick));
    }

    [Fact]
    public void Each_tick_hits_through_the_pipeline_with_damage_and_poise_but_never_flinches()
    {
        var f = new AreaFixture();
        var effect = f.Effect(EnemyArea);
        effect.FlinchPower = 1_000; // the validator forbids it (R-3.3-09); the pipeline must ignore it anyway
        effect.FlinchMs = 500;
        var mage = f.Player(1);
        var monster = f.Monster(2, 0, 100, hp: 100_000);
        int hp = monster.Combatant!.Hp(0);
        f.Place(mage, EnemyArea, Start);

        f.Tick(Start);

        Assert.True(monster.Combatant.Hp(f.F.NowMs(Start)) < hp);
        Assert.Equal(monster.Combatant.Poise.Max - 5, monster.Combatant.Poise.Current);
        Assert.Equal(ActionState.Idle, monster.Actions.State);
        f.F.Dispatch();
        Assert.Contains(typeof(Damaged), f.F.Log.Kinds);
        Assert.DoesNotContain(typeof(Flinched), f.F.Log.Kinds);
        var damaged = (Damaged)f.F.Log.Events.First(e => e is Damaged);
        Assert.Equal((mage.Id, monster.Id, TestActionNetId), (damaged.SourceId, damaged.TargetId, damaged.ActionId));
    }

    [Fact]
    public void An_area_on_enemies_hits_the_enemies_inside_it_only()
    {
        var f = new AreaFixture();
        var mage = f.Player(1);
        var ally = f.Player(2, 100, 0);
        var inside = f.Monster(3, 0, 300, hp: 100_000);   // body radius 50 touches the 3 m circle
        var outside = f.Monster(4, 0, 400, hp: 100_000);  // 4 m: out of reach
        f.Place(mage, EnemyArea, Start);

        f.Tick(Start);

        Assert.Equal(1, HitsOn(f, inside));
        Assert.Equal(0, HitsOn(f, outside));
        Assert.Equal(0, HitsOn(f, ally));
        Assert.Equal(0, HitsOn(f, mage));
    }

    [Fact]
    public void An_area_on_self_and_allies_heals_them_each_tick_with_the_Matk_read_when_placed()
    {
        var f = new AreaFixture();
        var mage = f.Player(1);
        var ally = f.Player(2, 200, 0);
        var away = f.Player(3, 2_000, 0);
        var monster = f.Monster(4, 0, 100, hp: 1_000);
        foreach (var hurt in new Entity[] { mage, ally, away, monster })
            hurt.Combatant!.Meters.Drain(ResourceKind.Hp, 60, 0);
        int matk = mage.Combatant!.Stats.Get(f.Rules.Config.Ids.Matk);
        int heal = matk * 50 / 100;
        var area = f.Place(mage, HealArea, Start)!;
        Assert.Equal(heal, area.HealAmount);
        mage.Combatant.Stats.SetBase(f.Rules.Config.Ids.Matk, matk * 10); // read at placing time only

        f.Run(Start, Start + 20);

        // 1000 ms / 500 ms: ticks at Start and Start + 10.
        Assert.Equal(2, f.F.Resolver.Notices.Count(n => n.TargetId == mage.Id && n.Kind == HitKind.Healed));
        Assert.Equal(2, f.F.Resolver.Notices.Count(n => n.TargetId == ally.Id && n.Kind == HitKind.Healed));
        Assert.All(f.F.Resolver.Notices.Where(n => n.Kind == HitKind.Healed), n => Assert.Equal(heal, n.Amount));
        Assert.Equal(0, f.F.Resolver.Notices.Count(n => n.TargetId == away.Id));
        Assert.Equal(0, f.F.Resolver.Notices.Count(n => n.TargetId == monster.Id)); // an enemy is not healed
        var healed = f.Dispatch().OfType<Healed>().ToList();
        Assert.Equal(4, healed.Count);
        Assert.All(healed, h => Assert.Equal((mage.Id, TestActionNetId, 0), (h.SourceId, h.ActionId, h.StatusId)));
    }

    [Fact]
    public void A_mixed_area_damages_enemies_and_heals_allies_in_the_same_tick()
    {
        var f = new AreaFixture();
        var mage = f.Player(1);
        var ally = f.Player(2, 100, 0);
        ally.Combatant!.Meters.Drain(ResourceKind.Hp, 30, 0);
        var monster = f.Monster(3, 0, 100, hp: 100_000);
        f.Place(mage, MixedArea, Start);

        f.Tick(Start);

        Assert.Equal(1, HitsOn(f, monster));
        Assert.Equal(1, HitsOn(f, ally, HitKind.Healed));
        Assert.Equal(0, HitsOn(f, mage, HitKind.Healed)); // allies, not self
    }

    [Fact]
    public void Dodge_i_frames_avoid_a_tick_of_a_monsters_area()
    {
        var f = new AreaFixture();
        var monster = f.Monster(1);
        var player = f.Player(2, 0, 100);
        int hp = player.Combatant!.Hp(0);
        f.F.Step(player, InputActions.Dodge, Start - 1);
        f.F.Step(player, InputActions.Dodge, Start);
        Assert.True(player.Actions.InIFrames);
        f.Place(monster, EnemyArea, Start);

        f.Tick(Start); // a monster's tick on a player is a pending hit; with no view lag it lands in the same phase 5

        Assert.Equal(1, HitsOn(f, player, HitKind.Dodged));
        Assert.Equal(hp, player.Combatant.Hp(f.F.NowMs(Start)));
    }

    [Fact]
    public void A_parry_is_not_judged_against_a_tick_which_lands_in_full()
    {
        var f = new AreaFixture();
        var monster = f.Monster(1);
        var player = f.Player(2, 0, 100);
        f.F.Step(player, InputActions.Parry, Start - 1, sequence: 7);
        f.F.Step(player, InputActions.Parry, Start, sequence: 8);
        Assert.Equal((ActionKind.Parry, ActionState.Active), (player.Actions.Action!.Kind, player.Actions.State));
        int hp = player.Combatant!.Hp(0);
        f.Place(monster, EnemyArea, Start);

        f.Tick(Start);

        Assert.Equal(1, HitsOn(f, player));
        var a = monster.Combatant!;
        var t = player.Combatant;
        var input = DamageCalc.Gather(f.Rules.Config, a.Stats, a.Rank, t.Stats, t.Rank, DamageKind.Physical, 50, 0, DamageCalc.NoElement);
        int full = DamageCalc.ComputeWithRoll(input, f.Rules.Config, NoCrit).Damage;
        Assert.Equal(hp - full, t.Hp(f.F.NowMs(Start)));
        Assert.Equal(0, player.ActionResults.Count);
        f.F.Dispatch();
        Assert.DoesNotContain(typeof(ParryAttempted), f.F.Log.Kinds);
    }

    [Fact]
    public void Areas_are_capped_per_owner_and_by_the_pool()
    {
        var f = new AreaFixture(new GroundAreaSettings { MaxPerOwner = 2, MaxActive = 3, EventRepeatTicks = 3 });
        var a = f.Player(1);
        var b = f.Player(2, 5_000, 0);
        Assert.NotNull(f.Place(a, EnemyArea, Start));
        Assert.NotNull(f.Place(a, EnemyArea, Start));
        Assert.Null(f.Place(a, EnemyArea, Start));
        Assert.Equal(1, f.Areas.RefusedOwnerCap);
        Assert.NotNull(f.Place(b, EnemyArea, Start));
        Assert.Null(f.Place(b, EnemyArea, Start));
        Assert.Equal(1, f.Areas.RefusedPoolFull);
        Assert.Equal(3, f.Areas.Spawned);

        // They expire at Start + 20 and go back to the pool once their despawn went out eventRepeatTicks times.
        f.Run(Start, Start + 20);
        Assert.Equal(0, f.Areas.LiveCount);
        f.Areas.ReleaseEnded(Start + 20);
        f.Areas.ReleaseEnded(Start + 21);
        Assert.Equal(3, f.Areas.Active.Count);
        f.Areas.ReleaseEnded(Start + 22);
        Assert.Empty(f.Areas.Active);
        Assert.NotNull(f.Place(b, EnemyArea, Start + 23));
    }

    [Fact]
    public void An_area_ends_when_its_owner_dies_or_leaves_the_world()
    {
        var f = new AreaFixture();
        var mage = f.Player(1);
        var other = f.Player(2);
        var monster = f.Monster(3, 0, 100, hp: 100_000);
        var died = f.Place(mage, EnemyArea, Start)!;
        var gone = f.Place(other, EnemyArea, Start)!;
        f.Tick(Start);
        Assert.Equal(2, HitsOn(f, monster));

        mage.Combatant!.Die(Start + 3);
        f.Areas.OwnerRemoved(other, Start + 4);
        f.Run(Start + 4, Start + 20);

        Assert.Equal((GroundAreaDespawnReason.OwnerDied, Start + 4), (died.Reason, died.EndTick));
        Assert.Equal((GroundAreaDespawnReason.OwnerGone, Start + 4), (gone.Reason, gone.EndTick));
        Assert.Equal(2, HitsOn(f, monster)); // nothing after the first tick
    }

    [Fact]
    public void Placing_publishes_the_spawn_on_whole_cm_and_the_wire_facing()
    {
        var f = new AreaFixture();
        var mage = f.Player(1, 123, -45);
        mage.Movement = new Shared.Simulation.MovementState(mage.Movement.Position, 1_000);
        var area = f.Place(mage, EnemyArea, Start)!;

        Assert.Equal(Quantize.RoundTripAngle(1_000, GroundAreaMessages.FacingBits), area.Facing);
        var spawn = area.ToSpawn();
        Assert.Equal((area.Id, (ushort)TestActionNetId, (byte)0, 123, -45, Start), (spawn.AreaId, spawn.ActionId, spawn.EffectIndex, spawn.XCm, spawn.YCm, spawn.StartTick));
        Assert.Equal(new GroundAreaSpawned(area.Id, mage.Id, TestActionNetId, 0, Start), f.Dispatch().OfType<GroundAreaSpawned>().Single());
    }
}
