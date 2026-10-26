using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Persistence;
using GameServer.Sessions;
using GameServer.Tests.Combat;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

namespace GameServer.Tests.World;

/// <summary>
/// The four starting kits (backlog E, docs/design/skills-m1-proposal.md) through the whole tick with the committed
/// tables: each class plays its own ActionBindings row, each damaging kit action hits the demon through the shared hit
/// pipeline (strikes in phase 5, projectiles in phase 4), Wind Cut pierces with falloff, the heals and buffs land on the
/// caster and the allies their circle covers (phase 6) and reach their clients (hit kind Healed, the own-status block),
/// the dashes move the caster (Back Step backward), and a skill without enough MP is refused with NoMp (R-3.1-04). The
/// player stands on the spawn marker at the origin facing north (facing 0 is +Y); demons stand in front of it. Their
/// aggro range is cut to 1 cm so they wait for the first hit.
/// </summary>
public class KitWorldTests
{
    private const ushort North = 0;
    private const int NoCritRoll = 99;
    private const int CritRoll = 0;

    /// <summary>A hurt ally (40 HP) standing next to the caster, or farther away.</summary>
    private static readonly CharacterState Ally = new(1, 0, SessionManager.MainWorldKey, 0, 0, 0, 0, Hp: 40, Mp: 0, Stamina: 0);

    private sealed class Setup
    {
        /// <param name="monsterYCm">One demon per entry, straight north of the player.</param>
        public Setup(string classId, params int[] monsterYCm)
        {
            Tables = TestCombat.Tables();
            Assert.True(Tables.TryGet(typeof(MonsterDef), out var monsters));
            monsters.Rows.Cast<MonsterDef>().Single(m => m.Id == TestCombat.MonsterId).AggroRangeCm = 1;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Log = CombatEventLog.Attach(Bus);
            Statuses = StatusEventLog.Attach(Bus);
            var markers = monsterYCm.Select((y, i) => TestCombat.MonsterMarker(0, y, id: $"test.monster.{i}")).ToArray();
            Harness = new WorldHarness(
                map: TestCombat.OpenMap(markers),
                events: new EventDispatchHook(Bus), bus: Bus,
                actions: ActionCatalog.From(Tables),
                combat: TestCombat.Rules(Tables, markers.Select(m => TestCombat.Point(markerId: m.Id)).ToArray()),
                projectiles: GameServer.Combat.ProjectileRules.From(Tables));
            Harness.Characters.AutoCreateClassId = classId;
            Player = Harness.Join(1); // the join tick also spawns the monsters
            Monsters = Harness.World.Entities.OfType<Monster>().OrderBy(m => m.YCm).ToArray();
            Assert.Equal(monsterYCm.Length, Monsters.Length);
        }

        public ConfigSet Tables { get; }
        public EventBus Bus { get; }
        public CombatEventLog Log { get; }
        public StatusEventLog Statuses { get; }
        public WorldHarness Harness { get; }
        public Player Player { get; }
        public Monster[] Monsters { get; }
        public Monster Monster => Monsters[0];

        public ActionCatalog Catalog => Harness.World.Actions;

        public CombatRules Rules => Harness.World.Combat!;

        public long NowMs => Harness.World.NowMs;

        public StatusDefinition Status(string id)
        {
            Assert.True(Rules.Statuses.TryGet(id, out var status));
            return status;
        }

        /// <summary>Joins another player (a warrior) as peer <paramref name="peerId"/> with <see cref="Ally"/> at <paramref name="xCm"/>.</summary>
        public Player JoinAlly(int peerId, int xCm)
        {
            Harness.Characters.Add(peerId, 0, Ally with { PosXCm = xCm });
            return Harness.Join(peerId);
        }

        public int NetId(string actionId) => Catalog.Find(actionId)!.NetId;

        /// <summary>The hotbar slot of <paramref name="actionId"/> in the class's row, or 0 for its normal attack.</summary>
        public byte SlotOf(string actionId)
        {
            var loadout = Player.Actions.Loadout;
            if (Catalog[loadout.AttackIndex].Id == actionId)
                return 0;
            for (byte slot = 1; slot <= ActionBindings.MaxSkillSlots; slot++)
            {
                if (loadout.SlotIndex(slot) >= 0 && Catalog[loadout.SlotIndex(slot)].Id == actionId)
                    return slot;
            }
            throw new InvalidOperationException($"{actionId} is not in the {loadout.ClassId} kit");
        }

        /// <summary>Presses the action (its slot, or left click for the normal attack) in the frame the next tick applies.</summary>
        public void Press(string actionId, ushort sequence = 1)
        {
            byte slot = SlotOf(actionId);
            var frame = slot == 0
                ? TestWorlds.Frame(sequence, facing: North, actions: InputActions.Attack)
                : TestWorlds.Frame(sequence, facing: North, skillSlot: slot);
            Harness.SendInputs(1, frame);
        }

        public List<Damaged> DamageOnMonster() => DamageOn(Monster);

        public List<Damaged> DamageOn(Monster monster) =>
            Log.Events.OfType<Damaged>().Where(d => d.SourceId == Player.Id && d.TargetId == monster.Id).ToList();

        /// <summary>Presses <paramref name="actionId"/> and ticks until its effects fired (the action is past Active).</summary>
        public void Cast(string actionId)
        {
            Press(actionId);
            var action = Catalog.Find(actionId)!;
            Harness.Ticks(action.TotalTicks + 1);
        }

        public void TickUntil(Func<bool> done, int limit)
        {
            for (int i = 0; i < limit && !done(); i++)
                Harness.World.Tick();
            Assert.True(done(), $"not reached in {limit} ticks: player {Player.Movement.Position}, monster {Monster.Movement.Position}");
        }
    }

    [Theory]
    // Warrior (melee): each strike's shape reaches a demon (body radius 50 cm) this far in front.
    [InlineData("warrior", "attack.warrior", 150)]
    [InlineData("warrior", "skill.warrior.slash", 150)]
    [InlineData("warrior", "skill.warrior.headCut", 250)]
    [InlineData("warrior", "skill.warrior.piercingAttack", 300)]
    [InlineData("warrior", "skill.warrior.jumpStrike", 450)] // 4 m dash, then the 1.5 m circle at the landing point
    // Archer (bow): 10 m away, well inside the 20 m arrows.
    [InlineData("archer", "attack.archer", 1_000)]
    [InlineData("archer", "skill.archer.piercingShot", 1_000)]
    [InlineData("archer", "skill.archer.heavyShot", 1_000)] // after its 1 s channel
    // Mage (staff): 10 m away, inside the spells' 14-16 m; each spell lands after its channel.
    [InlineData("mage", "attack.mage", 1_000)]
    [InlineData("mage", "skill.mage.fireSpark", 1_000)]
    [InlineData("mage", "skill.mage.iceShard", 1_000)]
    [InlineData("mage", "skill.mage.windCut", 1_000)]
    // Assist (staff): the 2 m cone, the 6 m beam, the 3 m circle.
    [InlineData("assist", "attack.assist", 150)]
    [InlineData("assist", "skill.assist.holyStrike", 550)]
    [InlineData("assist", "skill.assist.flash", 300)]
    public void A_kit_action_damages_the_demon_in_front(string classId, string actionId, int monsterYCm)
    {
        var s = new Setup(classId, monsterYCm);
        Assert.Equal(classId, s.Player.Actions.Loadout.ClassId);
        int hp = s.Monster.Combatant!.Hp(s.Harness.World.NowMs);

        s.Press(actionId);
        s.TickUntil(() => s.DamageOnMonster().Count > 0, limit: 60);

        var hit = Assert.Single(s.DamageOnMonster());
        Assert.Equal(s.NetId(actionId), hit.ActionId);
        Assert.True(hit.Amount > 0);
        Assert.Equal(hp - hit.Amount, s.Monster.Combatant.Hp(s.Harness.World.NowMs));
    }

    [Theory]
    [InlineData("warrior", "attack.warrior", 150)]
    [InlineData("archer", "attack.archer", 1_000)]
    [InlineData("mage", "attack.mage", 1_000)]
    [InlineData("assist", "attack.assist", 150)]
    public void Mashing_left_click_runs_the_whole_attack_chain_on_the_demon(string classId, string hit1, int monsterYCm)
    {
        // R-3.1-10: four hits, each its own action id in ActionStarted and in the damage, each paying its stamina.
        var s = new Setup(classId, monsterYCm);
        var started = new ActionStartedLog();
        s.Bus.Subscribe<ActionStarted>(started);
        string[] chain = [hit1, hit1 + ".2", hit1 + ".3", hit1 + ".4"];
        int staminaBefore = s.Player.Combatant!.Meters.Current(ResourceKind.Stamina, s.NowMs);
        int staminaAtHit4 = -1;
        ushort sequence = 1;
        for (int tick = 0; tick < 120 && started.Mine(s.Player.Id).Count < 4; tick++)
        {
            // A press every other frame, as a player mashing the button.
            s.Harness.SendInputs(1, TestWorlds.Frame(sequence++, facing: North, actions: tick % 2 == 0 ? InputActions.Attack : InputActions.None));
            s.Harness.Ticks(1);
            if (started.Mine(s.Player.Id).Count == 4)
                staminaAtHit4 = s.Player.Combatant.Meters.Current(ResourceKind.Stamina, s.NowMs);
        }
        s.TickUntil(() => s.DamageOnMonster().Count == 4, limit: 60);

        Assert.Equal(chain.Select(s.NetId), started.Mine(s.Player.Id));
        Assert.Equal(chain.Select(s.NetId), s.DamageOnMonster().Select(d => d.ActionId));
        Assert.All(s.DamageOnMonster(), d => Assert.True(d.Amount > 0));
        Assert.Equal(staminaBefore - 4 * s.Tables.Single<StartingValuesM1>().NormalAttackStaminaCost, staminaAtHit4);
        Assert.Empty(s.Harness.Transport.ActionResultsFor(1)); // nothing refused
    }

    private sealed class ActionStartedLog : IEventConsumer<ActionStarted>
    {
        private readonly List<ActionStarted> _events = [];

        public List<int> Mine(uint entityId) => _events.Where(e => e.EntityId == entityId).Select(e => e.ActionId).ToList();

        public void Handle(in EventEnvelope envelope, in ActionStarted payload) => _events.Add(payload);
    }

    [Fact]
    public void Multi_shot_spawns_three_arrows_that_each_hit_the_demon()
    {
        // Three Projectile effects at 0, 150 and 300 ms of Active (ticks 0, 3 and 6 of it), within the per-owner cap of 8.
        var s = new Setup("archer", 1_000);
        var projectiles = s.Harness.World.Projectiles!;
        long spawned = projectiles.Spawned;
        s.Press("skill.archer.multiShot");
        s.TickUntil(() => s.DamageOnMonster().Count == 3, limit: 60);

        Assert.Equal(3, projectiles.Spawned - spawned);
        Assert.All(s.DamageOnMonster(), d => Assert.Equal(s.NetId("skill.archer.multiShot"), d.ActionId));
    }

    [Theory]
    [InlineData("skill.archer.backStep", -400)] // 180 degrees from the facing
    [InlineData("skill.archer.dash", -500)] // no movement key held: backward (the dodge's rule, R-3.3-02)
    public void The_archers_dashes_move_it_their_distance_and_direction(string actionId, int expectedYCm)
    {
        var s = new Setup("archer", 3_000);
        var start = s.Player.Movement.Position;
        var action = s.Catalog.Find(actionId)!;
        s.Press(actionId);
        s.Harness.Ticks(action.TotalTicks + 1);

        Assert.Equal(start + FixVec2.FromCm(0, expectedYCm), s.Player.Movement.Position);
        Assert.Equal(3, s.Player.Actions.Charges(ChargePool.Dodge)); // the Dash uses no dodge charge
        Assert.Empty(s.DamageOnMonster());
    }

    [Fact]
    public void A_skill_without_enough_mp_is_refused_with_NoMp_and_spends_nothing()
    {
        // R-3.1-04: Slash costs 20 stamina and 3 MP; the warrior keeps 2 of its 30 MP.
        var s = new Setup("warrior", 3_000);
        var meters = s.Player.Combatant!.Meters;
        long now = s.Harness.World.NowMs;
        Assert.Equal(SpendResult.Ok, meters.TrySpend(ResourceKind.Mp, meters.Current(ResourceKind.Mp, now) - 2, now));
        int stamina = meters.Current(ResourceKind.Stamina, now);

        s.Press("skill.warrior.slash", sequence: 5);
        s.Harness.Ticks(1);

        Assert.Equal([((ushort)5, (ushort)s.NetId("skill.warrior.slash"), (byte)7, ActionReason.NoMp)], s.Harness.Transport.ActionResultsFor(1));
        Assert.False(s.Player.Actions.IsBusy);
        now = s.Harness.World.NowMs;
        Assert.Equal(stamina, meters.Current(ResourceKind.Stamina, now)); // full already, and nothing was spent
        Assert.Equal(2, meters.Current(ResourceKind.Mp, now));
        Assert.Equal(0, s.Player.Actions.CooldownTicks(s.Catalog.Find("skill.warrior.slash")!.Index));
    }

    [Fact]
    public void Wind_cut_pierces_three_demons_in_a_line_each_twenty_percent_weaker()
    {
        // R-3.2-01: ProjectileDef windCut, maxHits 3, falloff 20% per target already hit: 120% wind x 100, 80, 60%. A fourth
        // demon behind them takes nothing.
        var s = new Setup("mage", 400, 700, 1_000, 1_200);
        s.Press("skill.mage.windCut");
        s.TickUntil(() => s.DamageOn(s.Monsters[2]).Count > 0, limit: 60);
        s.Harness.Ticks(10);

        var effect = s.Catalog.Find("skill.mage.windCut")!.Effects.Single().Def;
        for (int i = 0; i < 3; i++)
        {
            var hit = Assert.Single(s.DamageOn(s.Monsters[i]));
            Assert.Equal(Expected(s, s.Monsters[i], effect, falloffPercent: 100 - 20 * i, hit.Crit), hit.Amount);
        }
        Assert.Empty(s.DamageOn(s.Monsters[3]));
    }

    /// <summary>The damage formula (R-3.4-04) for a hit of <paramref name="effect"/> by the player on <paramref name="target"/>.</summary>
    private static int Expected(Setup s, Monster target, ActionEffectDef effect, int falloffPercent, bool crit)
    {
        var a = s.Player.Combatant!;
        var t = target.Combatant!;
        var kind = effect.DamageKind == EffectDamageKind.Magic ? DamageKind.Magic : DamageKind.Physical;
        var input = DamageCalc.Gather(s.Rules.Config, a.Stats, a.Rank, t.Stats, t.Rank, kind, effect.DamagePercent * falloffPercent / 100,
            0, s.Rules.Config.ElementIndex(effect.Element));
        return DamageCalc.ComputeWithRoll(input, s.Rules.Config, crit ? CritRoll : NoCritRoll).Damage;
    }

    [Fact]
    public void Heal_wave_heals_the_hurt_allies_in_its_circle_and_tells_their_clients()
    {
        // R-3.1-07: self and allies within 5 m; heal 150% of the caster's Matk (spec 3.1 D6). The caster is at full HP (no
        // event for it); the ally 3 m away gains the heal and sees a Healed hit; the one 9 m away gets nothing.
        var s = new Setup("assist", 3_000);
        var near = s.JoinAlly(2, 300);
        var far = s.JoinAlly(3, 900);
        Assert.Equal(40, near.Combatant!.Hp(s.NowMs));
        int amount = s.Player.Combatant!.Stats.Get(s.Rules.Config.Ids.Matk) * 150 / 100;
        Assert.True(amount > 0);

        s.Cast("skill.assist.healWave");

        // Both allies also regenerate (hpRegen) during the channel: the heal is what the event and the hit carry.
        var healed = Assert.Single(s.Statuses.Events.OfType<Healed>());
        Assert.Equal((s.Player.Id, near.Id, s.NetId("skill.assist.healWave"), 0, amount),
            (healed.SourceId, healed.TargetId, healed.ActionId, healed.StatusId, healed.Amount));
        Assert.True(near.Combatant.Hp(s.NowMs) >= healed.HpAfter && healed.HpAfter > 40 + amount - 1);
        Assert.True(far.Combatant!.Hp(s.NowMs) < 40 + amount, "the far ally was not healed");
        var hit = Assert.Single(s.Harness.Transport.HitsFor(2), h => h.Hit.Kind == HitKind.Healed).Hit;
        Assert.Equal((s.Player.Id, near.Id, amount), (hit.AttackerId, hit.TargetId, (int)hit.Amount));
        Assert.DoesNotContain(s.Harness.Transport.HitsFor(3), h => h.Hit.TargetId == far.Id);
    }

    [Theory]
    // R-3.7-01: each kit buff lands on the caster, shows in its own snapshot (protocol 7) and ends on time.
    [InlineData("warrior", "skill.warrior.staminaBuff", "staminaBuff")]
    [InlineData("mage", "skill.mage.magicBarrier", "magicBarrier")]
    [InlineData("assist", "skill.assist.lifeRegeneration", "lifeRegeneration")]
    public void A_kit_buff_lands_on_the_caster_shows_in_its_snapshot_and_expires(string classId, string actionId, string statusId)
    {
        var s = new Setup(classId, 3_000);
        var status = s.Status(statusId);
        var statuses = s.Player.Combatant!.Statuses;
        s.Press(actionId);
        s.TickUntil(() => statuses.Has(statusId), limit: 40);

        Assert.Equal((ushort)status.NetId, Assert.Single(s.Harness.LastSnapshot(1).OwnStatuses).NetId);
        s.Harness.Ticks(status.DurationTicks);
        Assert.False(statuses.Has(statusId));
        Assert.Empty(s.Harness.LastSnapshot(1).OwnStatuses);
        Assert.Contains(new EffectRemoved(s.Player.Id, status.NetId, StatusEndReason.Expired), s.Statuses.Events);
    }

    [Fact]
    public void Magic_barrier_cuts_the_damage_the_mage_takes_and_raises_its_flinch_resist()
    {
        // StatusModifierDef magicBarrier: damage taken -20 (step 7, R-3.4-04), flinch resist +20.
        var s = new Setup("mage", 3_000);
        var ids = s.Rules.Config.Ids;
        var stats = s.Player.Combatant!.Stats;
        int taken = stats.Get(ids.DamageTakenPercent), resist = stats.Get(ids.FlinchResist);
        s.Cast("skill.mage.magicBarrier");
        Assert.Equal((taken - 20, resist + 20), (stats.Get(ids.DamageTakenPercent), stats.Get(ids.FlinchResist)));
    }

    [Fact]
    public void Life_regeneration_reaches_the_allies_in_its_circle_and_heals_them_every_second()
    {
        // R-3.1-07: self and allies within 6 m. The ally sees the status in its own snapshot and Healed hits from its ticks.
        var s = new Setup("assist", 3_000);
        var near = s.JoinAlly(2, 500);
        var far = s.JoinAlly(3, 900);
        var status = s.Status("lifeRegeneration");
        s.Press("skill.assist.lifeRegeneration");
        s.TickUntil(() => near.Combatant!.Statuses.Has("lifeRegeneration"), limit: 40);

        Assert.True(s.Player.Combatant!.Statuses.Has("lifeRegeneration"));
        Assert.False(far.Combatant!.Statuses.Has("lifeRegeneration"));
        Assert.Equal((ushort)status.NetId, Assert.Single(s.Harness.LastSnapshot(2).OwnStatuses).NetId);
        int hp = near.Combatant!.Hp(s.NowMs);
        s.Harness.Ticks(status.TickIntervalTicks);
        Assert.True(near.Combatant.Hp(s.NowMs) > hp, "a tick healed the ally");
        Assert.Contains(s.Statuses.Events.OfType<Healed>(), h => h.TargetId == near.Id && h.StatusId == status.NetId);
        Assert.Contains(s.Harness.Transport.HitsFor(2), h => h.Hit.Kind == HitKind.Healed && h.Hit.TargetId == near.Id);
    }

    [Fact]
    public void Fire_spark_raises_the_demons_burn()
    {
        // Spec 3.8 (WP11): EffectBuildUpDef skill.mage.fireSpark.projectile.burn, +25 per hit before the fire resistance.
        var s = new Setup("mage", 1_000);
        var buildUps = s.Harness.World.BuildUps!;
        s.Press("skill.mage.fireSpark");
        s.TickUntil(() => s.DamageOnMonster().Count > 0, limit: 60);

        Assert.InRange(buildUps.PointsOf(s.Monster, "burn", s.Harness.World.ServerTick), 1, 25);
    }

    [Fact]
    public void The_flamethrower_burns_the_demon_in_its_cone_tick_after_tick()
    {
        // A ground area cone of 3.5 m for 2 s, a tick every 250 ms: 25% fire magic damage and Burn +5 per tick (WP11).
        var s = new Setup("mage", 250);
        var buildUps = s.Harness.World.BuildUps!;
        var action = s.Catalog.Find("skill.mage.flamethrower")!;
        s.Press("skill.mage.flamethrower");
        s.Harness.Ticks(action.TotalTicks + 2_000 / 50 + 2);

        var ticks = s.DamageOnMonster();
        Assert.True(ticks.Count >= 2, $"{ticks.Count} ticks hit the demon");
        Assert.All(ticks, d => Assert.Equal(s.NetId("skill.mage.flamethrower"), d.ActionId));
        Assert.True(buildUps.PointsOf(s.Monster, "burn", s.Harness.World.ServerTick) > 0, "Burn rose");
    }

    [Fact]
    public void Simple_cleanse_lowers_the_build_ups_of_the_allies_in_its_circle_by_40()
    {
        // R-3.8-04: a RemoveStatus with buildUpReduction 40 on self and allies within 4 m (spec 3.7, 3.8).
        var s = new Setup("assist", 3_000);
        var near = s.JoinAlly(2, 300);
        var far = s.JoinAlly(3, 900);
        var rule = s.Rules.BuildUps[0];
        uint tick = s.Harness.World.ServerTick;
        near.Combatant!.BuildUps[0].Gain(rule, 60, 0, tick); // true only when it triggers
        far.Combatant!.BuildUps[0].Gain(rule, 60, 0, tick);
        var buildUps = s.Harness.World.BuildUps!;

        s.Cast("skill.assist.simpleCleanse");

        tick = s.Harness.World.ServerTick;
        Assert.Equal(20, buildUps.PointsOf(near, rule.Id, tick)); // within the meter's decay delay
        Assert.Equal(60, buildUps.PointsOf(far, rule.Id, tick));
    }
}
