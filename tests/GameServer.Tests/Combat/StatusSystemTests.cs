using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Combat;

/// <summary>Records the status and heal events a bus delivers (spec 3.7, 8.1).</summary>
internal sealed class StatusEventLog :
    IEventConsumer<EffectApplied>, IEventConsumer<EffectRemoved>, IEventConsumer<EffectCleansed>, IEventConsumer<Healed>
{
    public List<IGameEvent> Events { get; } = [];

    public static StatusEventLog Attach(EventBus bus)
    {
        var log = new StatusEventLog();
        bus.Subscribe<EffectApplied>(log);
        bus.Subscribe<EffectRemoved>(log);
        bus.Subscribe<EffectCleansed>(log);
        bus.Subscribe<Healed>(log);
        return log;
    }

    public void Handle(in EventEnvelope envelope, in EffectApplied payload) => Events.Add(payload);

    public void Handle(in EventEnvelope envelope, in EffectRemoved payload) => Events.Add(payload);

    public void Handle(in EventEnvelope envelope, in EffectCleansed payload) => Events.Add(payload);

    public void Handle(in EventEnvelope envelope, in Healed payload) => Events.Add(payload);
}

/// <summary>
/// Tick phase 6 (spec 3.7, backlog D6) on the repository's tables: statuses applied, refreshed, stacked and expired at tick
/// edges, their stat and regeneration changes, immunity, periodic damage and heals with their credit, heals, cleanses and
/// the target filter (R-3.1-07), and what ends statuses besides time (user decisions 2026-10-23).
/// </summary>
public class StatusSystemTests
{
    private const uint Tick = 10;

    private sealed class Setup
    {
        public Setup(ConfigSet? tables = null)
        {
            Fixture = new HitFixture(new FixedRng(99), tables);
            Statuses = new StatusSystem(Grid, Fixture.Rules, Fixture.Resolver, Fixture.Bus);
            Log = StatusEventLog.Attach(Fixture.Bus);
        }

        public HitFixture Fixture { get; }

        public SpatialGrid Grid { get; } = new(1_500);

        public StatusSystem Statuses { get; }

        public StatusEventLog Log { get; }

        public List<Entity> Entities { get; } = [];

        public CombatRules Rules => Fixture.Rules;

        public CombatStatIds Ids => Rules.Config.Ids;

        public Player Player(uint id, int xCm = 0, int yCm = 0, string classId = "warrior")
        {
            var player = Fixture.Player(id, xCm, yCm, classId: classId);
            Track(player);
            return player;
        }

        public Monster Monster(uint id, int xCm = 0, int yCm = 0, string monsterId = TestCombat.MonsterId)
        {
            var monster = Fixture.Monster(id, xCm, yCm, monsterId);
            Track(monster);
            return monster;
        }

        public StatusDefinition Status(string id)
        {
            Assert.True(Rules.Statuses.TryGet(id, out var definition));
            return definition;
        }

        /// <summary>Fires an effect from where its caster stands (as phase 3 would) and runs phase 6.</summary>
        public void Fire(Entity caster, string effectId, uint tick = Tick)
        {
            Statuses.Queue.Add(caster, Fixture.Effect(effectId), tick);
            Statuses.Run(tick, Entities);
        }

        public void Run(uint tick) => Statuses.Run(tick, Entities);

        public void RunThrough(uint from, uint to)
        {
            for (uint tick = from; tick <= to; tick++)
                Statuses.Run(tick, Entities);
        }

        public List<IGameEvent> Dispatch()
        {
            Fixture.Bus.DispatchQueued();
            return Log.Events;
        }

        public long NowMs(uint tick) => Rules.MsAt(tick);

        private void Track(Entity entity)
        {
            Grid.Add(entity);
            Entities.Add(entity);
            Entities.Sort((a, b) => a.Id.CompareTo(b.Id));
        }
    }

    private static int Ticks(Setup s, int ms) => s.Rules.TicksFor(ms);

    [Fact]
    public void A_buff_changes_its_stat_at_once_and_ends_on_the_tick_its_duration_runs_out()
    {
        var s = new Setup();
        var player = s.Player(1);
        var stats = player.Combatant!.Stats;
        int baseRegen = stats.Get(s.Ids.StaminaRegen);
        var buff = s.Status("staminaBuff");
        int duration = Ticks(s, buff.Def.DurationMs);

        s.Fire(player, "skill.haste.apply");

        // Stamina buff: +50% stamina regeneration (placeholder), from phase 6 of the tick it was applied.
        Assert.Equal(baseRegen * 150 / 100, stats.Get(s.Ids.StaminaRegen));
        Assert.True(player.Combatant.Statuses.Has("staminaBuff"));
        s.RunThrough(Tick + 1, Tick + (uint)duration - 1);
        Assert.True(player.Combatant.Statuses.Has("staminaBuff")); // active through phase 5 of Tick + duration

        s.Run(Tick + (uint)duration);
        Assert.False(player.Combatant.Statuses.Has("staminaBuff"));
        Assert.Equal(baseRegen, stats.Get(s.Ids.StaminaRegen));
        Assert.Equal(0, stats.ModifierCount);
        Assert.Equal(
            [new EffectApplied(1, 1, buff.NetId, 1, Tick + duration, false), new EffectRemoved(1, buff.NetId, StatusEndReason.Expired)],
            s.Dispatch());
    }

    [Fact]
    public void A_regeneration_change_starts_exactly_when_the_buff_is_applied()
    {
        var s = new Setup();
        var player = s.Player(1);
        var meters = player.Combatant!.Meters;
        int regen = player.Combatant.Stats.Get(s.Ids.StaminaRegen);
        // Spend at tick 0 and let the 1 s delay pass: from then on stamina comes back at the base rate.
        Assert.Equal(SpendResult.Ok, meters.TrySpend(ResourceKind.Stamina, 80, s.NowMs(0)));
        uint applied = 40; // 2 s
        int before = meters.Current(ResourceKind.Stamina, s.NowMs(applied));

        s.Fire(player, "skill.haste.apply", applied);

        // One more second at +50%.
        int after = meters.Current(ResourceKind.Stamina, s.NowMs(applied + 20));
        Assert.Equal(regen * 150 / 100, after - before);
    }

    [Fact]
    public void Applying_a_status_again_refreshes_its_duration_and_keeps_one_instance()
    {
        var s = new Setup();
        var player = s.Player(1);
        var buff = s.Status("staminaBuff");
        int duration = Ticks(s, buff.Def.DurationMs);

        s.Fire(player, "skill.haste.apply", Tick);
        s.Fire(player, "skill.haste.apply", Tick + 100);

        var statuses = player.Combatant!.Statuses;
        Assert.Equal(1, statuses.Count);
        Assert.Equal(1, statuses[0].Stacks);
        Assert.Equal(Tick + 100 + (uint)duration, statuses[0].EndTick);
        Assert.Equal(1, player.Combatant.Stats.ModifierCount);
        s.Run(Tick + (uint)duration); // the first application would have ended here
        Assert.True(statuses.Count == 1);
        Assert.Equal(new EffectApplied(1, 1, buff.NetId, 1, Tick + 100 + duration, true), s.Dispatch()[1]);
    }

    [Fact]
    public void A_stacking_status_multiplies_its_modifiers_up_to_its_cap()
    {
        var tables = TestCombat.Tables();
        Assert.True(tables.TryGet(typeof(StatusEffectDef), out var statusTable));
        statusTable.Rows.Cast<StatusEffectDef>().Single(r => r.Id == "weakened").MaxStacks = 2;
        var s = new Setup(tables);
        var alien = s.Monster(2, monsterId: "m1.alien");
        var player = s.Player(1, yCm: 100);
        int baseDef = player.Combatant!.Stats.Get(s.Ids.Def);

        s.Fire(alien, "monster.alien.slap.weaken");
        Assert.Equal(baseDef * 80 / 100, player.Combatant.Stats.Get(s.Ids.Def));
        s.Fire(alien, "monster.alien.slap.weaken", Tick + 1);
        Assert.Equal(baseDef * 60 / 100, player.Combatant.Stats.Get(s.Ids.Def));
        s.Fire(alien, "monster.alien.slap.weaken", Tick + 2);
        Assert.Equal(baseDef * 60 / 100, player.Combatant.Stats.Get(s.Ids.Def)); // capped at 2 stacks
        Assert.Equal(2, player.Combatant.Statuses[0].Stacks);
    }

    [Fact]
    public void An_enemy_status_lands_on_the_players_in_its_shape_and_not_on_monsters_or_the_dead()
    {
        // R-3.1-07 enemies; user decision 2026-10-23: not a hit, so no dodge or parry check.
        var s = new Setup();
        var alien = s.Monster(1, monsterId: "m1.alien");
        var inFront = s.Player(2, yCm: 100);
        var behind = s.Player(3, yCm: -100);
        var otherMonster = s.Monster(4, xCm: 30, yCm: 100);
        var dead = s.Player(5, xCm: -30, yCm: 100);
        dead.Combatant!.Die(1);

        s.Fire(alien, "monster.alien.slap.weaken");

        Assert.True(inFront.Combatant!.Statuses.Has("weakened"));
        Assert.False(behind.Combatant!.Statuses.Has("weakened"));
        Assert.False(otherMonster.Combatant!.Statuses.Has("weakened"));
        Assert.False(dead.Combatant.Statuses.Has("weakened"));
        Assert.False(alien.Combatant!.Statuses.Has("weakened"));
    }

    [Fact]
    public void A_self_status_affects_only_the_caster()
    {
        var s = new Setup();
        var caster = s.Player(1);
        var ally = s.Player(2, yCm: 50);

        s.Fire(caster, "skill.haste.apply");

        Assert.True(caster.Combatant!.Statuses.Has("staminaBuff"));
        Assert.False(ally.Combatant!.Statuses.Has("staminaBuff"));
    }

    [Fact]
    public void An_immune_monster_does_not_get_a_status_with_a_matching_tag()
    {
        // R-3.7-02.
        var s = new Setup();
        var alien = s.Monster(1, monsterId: "m1.alien");
        var target = s.Monster(2, yCm: 100);
        target.Combatant!.StatusImmuneTags = ["weaken"];

        Assert.False(s.Statuses.Apply(alien, target, s.Status("weakened"), Tick));
        Assert.True(s.Statuses.Apply(alien, target, s.Status("burn"), Tick));
        Assert.Equal(1, target.Combatant.Statuses.Count);
    }

    [Fact]
    public void A_heal_restores_a_percent_of_the_casters_Matk_to_itself_and_allies_in_its_circle()
    {
        var s = new Setup();
        var caster = s.Player(1, classId: "assist");
        var near = s.Player(2, xCm: 400);
        var far = s.Player(3, xCm: 900);
        var monster = s.Monster(4, yCm: 200);
        var dead = s.Player(5, yCm: -300);
        dead.Combatant!.Die(1);
        long now = s.NowMs(Tick);
        foreach (var entity in new Entity[] { caster, near, far, monster })
            entity.Combatant!.Meters.Drain(ResourceKind.Hp, 50, now);
        var effect = s.Fixture.Effect("skill.heal.heal");
        int amount = caster.Combatant!.Stats.Get(s.Ids.Matk) * effect.HealPercent / 100;
        Assert.True(amount > 0);
        int hpBefore = near.Combatant!.Hp(now);

        s.Fire(caster, "skill.heal.heal");

        Assert.Equal(hpBefore + amount, near.Combatant.Hp(now));
        Assert.Equal(caster.Combatant.MaxHp(now) - 50 + amount, caster.Combatant.Hp(now));
        Assert.Equal(far.Combatant!.MaxHp(now) - 50, far.Combatant.Hp(now));
        Assert.Equal(monster.Combatant!.MaxHp(now) - 50, monster.Combatant.Hp(now));
        Assert.Equal(0, dead.Combatant.Hp(now));
        int actionId = s.Fixture.Resolver.ActionNetIdOf(effect);
        Assert.NotEqual(0, actionId);
        Assert.Equal([new Healed(1, 1, actionId, 0, amount, caster.Combatant.Hp(now)), new Healed(1, 2, actionId, 0, amount, hpBefore + amount)],
            s.Dispatch());
        Assert.Equal([HitKind.Healed, HitKind.Healed], s.Fixture.Resolver.Notices.Select(n => n.Kind));
    }

    [Fact]
    public void A_heal_never_goes_above_the_max_and_a_full_target_gets_no_event()
    {
        var s = new Setup();
        var caster = s.Player(1, classId: "assist");
        s.Fire(caster, "skill.heal.heal");
        Assert.Equal(caster.Combatant!.MaxHp(s.NowMs(Tick)), caster.Combatant.Hp(s.NowMs(Tick)));
        Assert.Empty(s.Dispatch());
        Assert.Empty(s.Fixture.Resolver.Notices);
    }

    [Fact]
    public void Periodic_damage_ticks_every_interval_through_the_end_and_is_credited_to_its_applier()
    {
        var s = new Setup();
        var mage = s.Player(1, classId: "mage");
        var monster = s.Monster(2, yCm: 100);
        var burn = s.Status("burn");
        int interval = burn.TickIntervalTicks, duration = burn.DurationTicks;
        Assert.True(s.Statuses.Apply(mage, monster, burn, Tick));
        var victim = monster.Combatant!;
        long now = s.NowMs(Tick);
        int full = victim.Hp(now);

        // Expected per tick: the mage's Matk x tickDamagePercent through DamageCalc with fire, never a crit.
        var input = DamageCalc.Gather(s.Rules.Config, mage.Combatant!.Stats, mage.Combatant.Rank, victim.Stats, victim.Rank,
            DamageKind.Magic, burn.Def.TickDamagePercent, 0, s.Rules.Config.ElementIndex(Element.Fire));
        input.NoCrit = true;
        int perTick = DamageCalc.ComputeWithRoll(input, s.Rules.Config, 0).Damage;
        Assert.True(perTick > 0);

        s.RunThrough(Tick + 1, Tick + (uint)interval - 1);
        Assert.Equal(full, victim.Hp(now));
        s.RunThrough(Tick + (uint)interval, Tick + (uint)duration);

        int ticks = duration / interval; // 4 s / 500 ms: 8, the last one on the tick it ends
        Assert.Equal(full - ticks * perTick, victim.Hp(now));
        Assert.Equal(0, victim.Statuses.Count);
        var damaged = s.Fixture.Dispatch().OfType<Damaged>().ToList();
        Assert.Equal(ticks, damaged.Count);
        Assert.All(damaged, d => Assert.Equal(new Damaged(1, 2, 0, perTick, d.HpAfter, false), d));
        Assert.Equal(0, ((FixedRng)s.Fixture.Rng).Draws); // no crit roll
    }

    [Fact]
    public void A_killing_tick_is_credited_to_the_applier_and_ends_the_targets_statuses()
    {
        var s = new Setup();
        var mage = s.Player(1, classId: "mage", xCm: 5_000); // far away: credit does not need the applier near
        var monster = s.Monster(2);
        var victim = monster.Combatant!;
        Assert.True(s.Statuses.Apply(mage, monster, s.Status("burn"), Tick));
        Assert.True(s.Statuses.Apply(mage, monster, s.Status("weakened"), Tick));
        long now = s.NowMs(Tick);
        victim.Meters.Drain(ResourceKind.Hp, victim.Hp(now) - 1, now);

        s.RunThrough(Tick + 1, Tick + (uint)s.Status("burn").TickIntervalTicks);

        Assert.False(victim.IsAlive);
        Assert.Equal(0, victim.Statuses.Count);
        var combat = s.Fixture.Dispatch();
        Assert.Contains(new Killed(1, 2, 0, monster.DefId, monster.XCm, monster.YCm), combat);
        Assert.Contains(new Died(2, 1), combat);
        Assert.Equal(2, s.Log.Events.OfType<EffectRemoved>().Count(r => r.Reason == StatusEndReason.Died));
    }

    [Fact]
    public void A_periodic_heal_works_right_after_a_hit()
    {
        // Life Regeneration heals every interval even while the hpRegen stat waits its delay after damage.
        var s = new Setup();
        var assist = s.Player(1, classId: "assist");
        var regen = s.Status("lifeRegeneration");
        Assert.True(s.Statuses.Apply(assist, assist, regen, Tick));
        long now = s.NowMs(Tick + 1);
        assist.Combatant!.Meters.Drain(ResourceKind.Hp, 60, now);
        int hp = assist.Combatant.Hp(now);
        int amount = StatusSystem.HealAmount(assist.Combatant.Stats.Get(s.Ids.Matk), regen.Def.TickHealPercent);

        s.RunThrough(Tick + 1, Tick + (uint)regen.TickIntervalTicks);

        Assert.Equal(hp + amount, assist.Combatant.Hp(s.NowMs(Tick + (uint)regen.TickIntervalTicks)));
        Assert.Contains(new Healed(1, 1, 0, regen.NetId, amount, hp + amount), s.Dispatch());
    }

    [Fact]
    public void A_cleanse_removes_the_casters_dispellable_debuffs_and_keeps_its_buffs()
    {
        var s = new Setup();
        var player = s.Player(1);
        var alien = s.Monster(2, monsterId: "m1.alien");
        s.Statuses.Apply(player, player, s.Status("staminaBuff"), Tick);
        s.Statuses.Apply(alien, player, s.Status("weakened"), Tick);
        s.Statuses.Apply(alien, player, s.Status("burn"), Tick);
        int baseDef = player.Combatant!.Stats.GetBase(s.Ids.Def);

        s.Fire(player, "skill.cleanse.remove", Tick + 1);

        var statuses = player.Combatant.Statuses;
        Assert.Equal(1, statuses.Count);
        Assert.True(statuses.Has("staminaBuff"));
        Assert.Equal(baseDef, player.Combatant.Stats.Get(s.Ids.Def));
        int actionId = s.Fixture.Resolver.ActionNetIdOf(s.Fixture.Effect("skill.cleanse.remove"));
        Assert.Equal(
            [new EffectCleansed(1, 1, s.Status("weakened").NetId, actionId), new EffectCleansed(1, 1, s.Status("burn").NetId, actionId)],
            s.Dispatch().OfType<EffectCleansed>());
    }

    [Fact]
    public void Death_and_a_leash_reset_end_every_status()
    {
        var s = new Setup();
        var player = s.Player(1);
        var monster = s.Monster(2, yCm: 100);
        s.Statuses.Apply(player, player, s.Status("staminaBuff"), Tick);
        s.Statuses.Apply(player, monster, s.Status("weakened"), Tick);

        player.Combatant!.Die(Tick);
        s.Run(Tick + 1);
        Assert.Equal(0, player.Combatant.Statuses.Count);
        Assert.Equal(0, player.Combatant.Stats.ModifierCount);

        s.Statuses.EndAll(monster, StatusEndReason.Reset, Tick + 1);
        Assert.Equal(0, monster.Combatant!.Statuses.Count);
        Assert.Equal(monster.Combatant.Stats.GetBase(s.Ids.Def), monster.Combatant.Stats.Get(s.Ids.Def));
        var removed = s.Dispatch().OfType<EffectRemoved>().ToList();
        Assert.Equal([StatusEndReason.Died, StatusEndReason.Reset], removed.Select(r => r.Reason));
    }

    [Fact]
    public void A_caster_that_died_after_firing_affects_no_one()
    {
        var s = new Setup();
        var caster = s.Player(1);
        s.Statuses.Queue.Add(caster, s.Fixture.Effect("skill.haste.apply"), Tick);
        caster.Combatant!.Die(Tick);
        s.Run(Tick);
        Assert.Equal(0, caster.Combatant.Statuses.Count);
    }

    [Fact]
    public void Magic_Barrier_lowers_the_damage_taken_and_raises_the_flinch_resist()
    {
        var s = new Setup();
        var player = s.Player(1);
        var monster = s.Monster(2, yCm: 100);
        var stats = player.Combatant!.Stats;
        int resist = player.Combatant.FlinchResist;
        s.Statuses.Apply(player, player, s.Status("magicBarrier"), Tick);

        Assert.Equal(-20, stats.Get(s.Ids.DamageTakenPercent));
        Assert.Equal(resist + 20, player.Combatant.FlinchResist);
        var input = DamageCalc.Gather(s.Rules.Config, monster.Combatant!.Stats, monster.Combatant.Rank, stats, player.Combatant.Rank,
            DamageKind.Physical, 100, 0, DamageCalc.NoElement);
        Assert.Equal(-20, input.ExtraPercent);
    }
}
