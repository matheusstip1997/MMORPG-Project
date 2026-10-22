using GameServer.AI;
using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Tests.Support;
using GameServer.World;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;
using Shared.World;

namespace GameServer.Tests.AI;

/// <summary>
/// The generic monster AI through the tick (backlog F), with the repository's tables at 20 Hz: the demon (aggro 800 cm,
/// leash 1500 cm, 350 cm/s) claws (cone 200 cm, 600 ms startup) and pounces (a 300 cm dash, then a capsule, 800 ms
/// startup, 6 s cooldown); the alien spits (a 1200 cm projectile) and slaps. Decisions of 2026-10-22: aggro on the nearest
/// visible player, retaliation, leash home with a heal, monsters never enter cities, players respawn after 5 s.
/// </summary>
public class MonsterAiTests
{
    private const int ClawNetId = 201;
    private const int PounceNetId = 202;
    private const int SpitNetId = 204;

    private sealed class Recorder :
        IEventConsumer<ActionStarted>, IEventConsumer<Damaged>, IEventConsumer<Died>, IEventConsumer<Respawned>
    {
        public List<ActionStarted> Started { get; } = [];
        public List<Damaged> Damage { get; } = [];
        public List<Died> Deaths { get; } = [];
        public List<Respawned> Respawns { get; } = [];
        public List<long> RespawnActors { get; } = [];

        public void Handle(in EventEnvelope envelope, in ActionStarted payload) => Started.Add(payload);
        public void Handle(in EventEnvelope envelope, in Damaged payload) => Damage.Add(payload);
        public void Handle(in EventEnvelope envelope, in Died payload) => Deaths.Add(payload);

        public void Handle(in EventEnvelope envelope, in Respawned payload)
        {
            Respawns.Add(payload);
            RespawnActors.Add(envelope.ActorCharacterId);
        }
    }

    private sealed class NobodySeesPlayers : IVisibilityQuery
    {
        public bool CanSee(Entity observer, Entity target) => !(observer is Monster && target is Player);
    }

    private sealed class Setup
    {
        public Setup(int monsterXCm = 0, int monsterYCm = 600, string monsterId = TestCombat.MonsterId, WorldMap? map = null,
            IVisibilityQuery? visibility = null, Action<ConfigSet>? tune = null, int players = 1)
        {
            var tables = TestCombat.Tables();
            tune?.Invoke(tables);
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Events = new Recorder();
            Bus.Subscribe<ActionStarted>(Events);
            Bus.Subscribe<Damaged>(Events);
            Bus.Subscribe<Died>(Events);
            Bus.Subscribe<Respawned>(Events);
            Harness = new WorldHarness(
                map: map ?? TestCombat.OpenMap(TestCombat.MonsterMarker(monsterXCm, monsterYCm)),
                visibility: visibility, events: new EventDispatchHook(Bus), bus: Bus,
                actions: ActionCatalog.From(tables),
                combat: TestCombat.Rules(tables, TestCombat.Point(monsterId: monsterId)),
                projectiles: ProjectileRules.From(tables));
            for (int peer = 1; peer <= players; peer++)
                Players.Add(Harness.Join(peer)); // the first join tick also spawns the monster
            Monster = Assert.Single(World.Entities.OfType<Monster>());
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public Recorder Events { get; }
        public List<Player> Players { get; } = [];
        public Player Player => Players[0];
        public Monster Monster { get; }
        public GameWorld World => Harness.World;
        public long NowMs => World.NowMs;
        public MonsterBrain Brain => Monster.Brain;

        public void Place(Entity entity, int xCm, int yCm)
        {
            var position = FixVec2.FromCm(xCm, yCm);
            entity.Movement = new MovementState(position, entity.Movement.Facing);
            entity.SpeedCheckedPosition = position;
            entity.RegionIndex = World.Map.Grid.RegionIndexAt(position);
            World.Grid.Update(entity);
        }

        /// <summary>Ticks until <paramref name="done"/> holds; fails after <paramref name="limit"/> ticks.</summary>
        public int TickUntil(Func<bool> done, int limit = 600)
        {
            for (int ticks = 0; ticks < limit; ticks++)
            {
                if (done())
                    return ticks;
                World.Tick();
            }
            Assert.Fail($"not reached in {limit} ticks: monster {Monster} mode {Brain.Mode}, state {Monster.Actions}");
            return limit;
        }

        public long MonsterDistanceSqCm(Entity other) => MonsterAi.DistanceSqCm(Monster.Movement.Position, other.Movement.Position);
    }

    private static WorldMap CityMap(int playerXCm, int monsterXCm)
    {
        // ±50 m, 1 m cells; everything west of x = 0 is a city (region 2, area type normalCity).
        const int Half = 5_000, Cell = 100, Cells = 2 * Half / Cell;
        var grid = TestMaps.Grid(-Half, -Half, Cell, Cells, Cells, 0, (_, _) => true,
            region: (x, _) => x < Cells / 2 ? (byte)2 : (byte)1);
        var regions = new[]
        {
            new WorldRegion(1, TestMaps.OpenRegionId, "openWorld", TestMaps.OpenFlags),
            new WorldRegion(2, "test.city", "normalCity", new RegionFlags(PvpMode.Off, honorEffects: true, DeathDrop.Off)),
        };
        return TestMaps.Map(grid, [TestMaps.SpawnMarker(playerXCm, 0), TestCombat.MonsterMarker(monsterXCm, 0)], regions);
    }

    // --- Aggro (R-2.4-03, user decision 2026-10-22) ---

    [Fact]
    public void An_idle_monster_aggroes_a_visible_player_inside_its_aggro_range()
    {
        var s = new Setup(monsterYCm: 600);

        s.TickUntil(() => s.Brain.Mode == MonsterMode.Chase, limit: 10); // one scan every 250 ms

        Assert.Same(s.Player, s.Brain.Target);
    }

    [Fact]
    public void A_player_outside_the_aggro_range_is_left_alone()
    {
        var s = new Setup(monsterYCm: 1_000);
        var home = s.Monster.Movement.Position;

        s.Harness.Ticks(60);

        Assert.Equal(MonsterMode.Idle, s.Brain.Mode);
        Assert.Equal(home, s.Monster.Movement.Position);
        Assert.Empty(s.Events.Started);
    }

    [Fact]
    public void A_player_the_monster_cannot_see_is_not_aggroed()
    {
        var s = new Setup(monsterYCm: 300, visibility: new NobodySeesPlayers());

        s.Harness.Ticks(60);

        Assert.Equal(MonsterMode.Idle, s.Brain.Mode);
        Assert.Empty(s.Events.Started);
    }

    [Fact]
    public void The_nearest_player_is_aggroed_and_equal_distances_go_to_the_lower_entity_id()
    {
        var tie = new Setup(monsterYCm: 600, players: 2); // both joined at the spawn: the same distance
        tie.TickUntil(() => tie.Brain.Mode == MonsterMode.Chase, limit: 10);
        Assert.Same(tie.Players.MinBy(p => p.Id), tie.Brain.Target);

        var nearer = new Setup(monsterYCm: 600, players: 2);
        nearer.Place(nearer.Players[1], 0, 300);
        nearer.TickUntil(() => nearer.Brain.Mode == MonsterMode.Chase, limit: 10);
        Assert.Same(nearer.Players[1], nearer.Brain.Target);
    }

    [Fact]
    public void A_dead_player_is_not_aggroed()
    {
        var s = new Setup(monsterYCm: 300);
        s.Player.Combatant!.Die(s.World.ServerTick);
        ActionMachine.Kill(s.Player.Actions);

        s.Harness.Ticks(20);

        Assert.Equal(MonsterMode.Idle, s.Brain.Mode);
    }

    [Fact]
    public void A_player_who_damages_an_idle_monster_from_outside_its_aggro_range_becomes_its_target()
    {
        // 15 m north: outside the demon's 8 m aggro range, inside the arrow's 20 m (hotbar slot 7, facing 0 = +Y).
        var s = new Setup(monsterYCm: 1_500);
        s.Harness.Ticks(10);
        Assert.Equal(MonsterMode.Idle, s.Brain.Mode);

        s.Harness.SendInputs(1, TestWorlds.Frame(1, facing: 0, skillSlot: 7));
        s.TickUntil(() => s.Events.Damage.Any(d => d.TargetId == s.Monster.Id), limit: 40);
        s.TickUntil(() => s.Brain.Mode == MonsterMode.Chase, limit: 10);

        Assert.Same(s.Player, s.Brain.Target);
    }

    [Fact]
    public void Damage_from_a_player_the_monster_cannot_see_does_not_aggro_it()
    {
        var s = new Setup(monsterYCm: 1_500, visibility: new NobodySeesPlayers());
        s.Harness.Ticks(2);

        s.Bus.Publish(new Damaged(s.Player.Id, s.Monster.Id, 107, 10, 150, false), s.Player.CharacterId);
        s.Harness.Ticks(20);

        Assert.Equal(MonsterMode.Idle, s.Brain.Mode);
        Assert.Equal(0u, s.Brain.RetaliateId);
    }

    // --- Chase and attack (backlog F "attack by action table") ---

    [Fact]
    public void The_monster_walks_to_its_target_at_its_speed_and_stops_at_the_reach_of_an_attack()
    {
        var s = new Setup(monsterYCm: 700, tune: t => MonsterRow(t, "m1.demon").Attacks = ["monster.demon.claw"]);
        s.TickUntil(() => s.Brain.Mode == MonsterMode.Chase, limit: 10);
        long before = s.MonsterDistanceSqCm(s.Player);

        s.Harness.Ticks(2); // phase 7 decides, the next phase 2 walks: 350 cm/s is 17.5 cm per tick
        long after = s.MonsterDistanceSqCm(s.Player);
        Assert.True(after < before);

        s.TickUntil(() => s.Events.Started.Count > 0, limit: 60);
        // It started the claw when the claw's cone (200 cm) reached the warrior's body circle.
        Assert.Equal(ClawNetId, s.Events.Started[0].ActionId);
        long distance = (long)Math.Sqrt(s.MonsterDistanceSqCm(s.Player));
        Assert.InRange(distance, 150, 200 + s.Player.Combatant!.BodyRadiusCm);
    }

    [Fact]
    public void The_first_ready_attack_that_reaches_is_chosen_by_distance()
    {
        // Up close the claw reaches; from 7 m the demon walks until the pounce (dash, then strike) reaches, and pounces.
        var close = new Setup(monsterYCm: 220);
        close.TickUntil(() => close.Events.Started.Count > 0, limit: 20);
        Assert.Equal(ClawNetId, close.Events.Started[0].ActionId);

        var far = new Setup(monsterYCm: 700);
        far.TickUntil(() => far.Events.Started.Count > 0, limit: 80);
        Assert.Equal(PounceNetId, far.Events.Started[0].ActionId);
        Assert.InRange((long)Math.Sqrt(far.MonsterDistanceSqCm(far.Player)), 300, 560);
    }

    [Fact]
    public void An_attack_locks_the_facing_and_its_strike_lands_through_hit_resolution()
    {
        var s = new Setup(monsterYCm: 220);
        var hp = s.Player.Combatant!.Hp(s.NowMs);
        s.TickUntil(() => s.Events.Started.Count > 0, limit: 20);
        ushort locked = s.Monster.Movement.Facing;
        Assert.Equal(locked, s.Events.Started[0].Facing);
        Assert.Equal(ActionState.Startup, s.Monster.Actions.State);

        // The player steps aside during the 600 ms startup: the monster keeps its facing (R-3.1-01) and does not walk.
        s.Place(s.Player, 60, 0);
        var position = s.Monster.Movement.Position;
        s.Harness.Ticks(5);
        Assert.Equal(locked, s.Monster.Movement.Facing);
        Assert.Equal(position, s.Monster.Movement.Position);

        s.TickUntil(() => s.Events.Damage.Any(d => d.SourceId == s.Monster.Id), limit: 20);
        Assert.True(s.Player.Combatant.Hp(s.NowMs) < hp);
    }

    [Fact]
    public void The_alien_spits_from_range_and_the_projectile_hits()
    {
        var s = new Setup(monsterYCm: 900, monsterId: "m1.alien");

        s.TickUntil(() => s.Events.Started.Count > 0, limit: 20);
        Assert.Equal(SpitNetId, s.Events.Started[0].ActionId);
        s.TickUntil(() => s.World.Projectiles!.Active.Count > 0, limit: 30);
        s.TickUntil(() => s.Events.Damage.Any(d => d.SourceId == s.Monster.Id), limit: 40);
    }

    // --- Leash (user decision 2026-10-22) ---

    [Fact]
    public void Past_its_leash_the_monster_walks_home_ignoring_players_and_heals_there()
    {
        var s = new Setup(monsterYCm: 600);
        var home = s.Monster.Movement.Position;
        ushort homeFacing = s.Monster.Movement.Facing;
        s.TickUntil(() => s.Brain.Mode == MonsterMode.Chase, limit: 10);
        s.Monster.Combatant!.Meters.Drain(ResourceKind.Hp, 50, s.NowMs);

        // The player runs far north: the demon follows until it is more than 15 m from home.
        s.Place(s.Player, 0, 4_000);
        s.TickUntil(() => s.Brain.Mode == MonsterMode.Return, limit: 200);
        Assert.Null(s.Brain.Target);
        Assert.True(s.Monster.Combatant.Hp(s.NowMs) < s.Monster.Combatant.MaxHp(s.NowMs));

        // On the way home it ignores a player right next to it.
        int started = s.Events.Started.Count;
        s.Place(s.Player, s.Monster.XCm + 100, s.Monster.YCm);
        s.Harness.Ticks(20);
        Assert.Equal(MonsterMode.Return, s.Brain.Mode);
        Assert.Equal(started, s.Events.Started.Count);
        s.Place(s.Player, 0, 4_000);

        s.TickUntil(() => s.Brain.Mode == MonsterMode.Idle, limit: 300);
        Assert.True(MonsterAi.DistanceSqCm(home, s.Monster.Movement.Position) <= 50 * 50);
        Assert.Equal(homeFacing, s.Monster.Movement.Facing);
        Assert.Equal(s.Monster.Combatant.MaxHp(s.NowMs), s.Monster.Combatant.Hp(s.NowMs));
        Assert.Equal(s.Monster.Combatant.Poise.Max, s.Monster.Combatant.Poise.Current);
    }

    [Fact]
    public void A_monster_whose_target_dies_goes_home()
    {
        var s = new Setup(monsterYCm: 600);
        s.TickUntil(() => s.Brain.Mode == MonsterMode.Chase, limit: 10);
        s.Harness.Ticks(10);

        s.Player.Combatant!.Die(s.World.ServerTick);
        ActionMachine.Kill(s.Player.Actions);
        s.Harness.Ticks(1);

        Assert.Equal(MonsterMode.Return, s.Brain.Mode);
    }

    [Fact]
    public void A_monster_stuck_on_its_way_home_is_placed_there_after_the_leash_timeout()
    {
        // A wall across the map at y = 12..13 m; home is south of it, the monster is put north of it.
        const int Half = 10_000, Cell = 100;
        int wallRow = (1_200 + Half) / Cell;
        var grid = TestMaps.Grid(-Half, -Half, Cell, 2 * Half / Cell, 2 * Half / Cell, 0, (_, y) => y != wallRow);
        var map = TestMaps.Map(grid, [TestMaps.SpawnMarker(0, -2_000), TestCombat.MonsterMarker(0, 600)]);
        var s = new Setup(map: map, tune: t => t.Single<MonsterAiSettings>().LeashTimeoutMs = 1_000);
        var home = s.Monster.Movement.Position;
        s.Place(s.Monster, 0, 2_000);

        s.TickUntil(() => s.Brain.Mode == MonsterMode.Return, limit: 10);
        s.TickUntil(() => s.Brain.Mode == MonsterMode.Idle, limit: 200);

        Assert.Equal(home, s.Monster.Movement.Position);
        Assert.True(s.Monster.Combatant!.History.TryGetAt(s.World.ServerTick - 1, out var recorded));
        Assert.Equal(home, recorded);
    }

    // --- The safe city (user decision 2026-10-22) ---

    [Fact]
    public void A_player_inside_a_city_is_never_aggroed_and_a_target_that_enters_one_is_dropped()
    {
        var s = new Setup(map: CityMap(playerXCm: -200, monsterXCm: 300));
        s.Harness.Ticks(40);
        Assert.Equal(MonsterMode.Idle, s.Brain.Mode);

        s.Place(s.Player, 100, 0);
        s.TickUntil(() => s.Brain.Mode == MonsterMode.Chase, limit: 10);
        s.Place(s.Player, -300, 0);
        // The target is dropped (a monster that has not left home is home at once, so it goes straight back to Idle).
        s.TickUntil(() => s.Brain.Target == null && s.Brain.Mode != MonsterMode.Chase, limit: 30);
        Assert.True(s.Monster.XCm >= 0);
    }

    [Fact]
    public void A_monster_move_into_a_city_slides_along_its_border_or_stops()
    {
        var s = new Setup(map: CityMap(playerXCm: -200, monsterXCm: 300));
        var ai = s.World.MonsterAi!;

        // From just outside, a move with a city part keeps only the axis that stays out.
        Assert.Equal(FixVec2.FromCm(50, 30), ai.KeepOut(FixVec2.FromCm(50, 0), FixVec2.FromCm(-20, 30)));
        Assert.Equal(FixVec2.FromCm(50, 0), ai.KeepOut(FixVec2.FromCm(50, 0), FixVec2.FromCm(-20, 0)));
        Assert.Equal(FixVec2.FromCm(60, 10), ai.KeepOut(FixVec2.FromCm(50, 0), FixVec2.FromCm(60, 10)));
    }

    // --- Determinism (architecture rule 6) ---

    [Fact]
    public void The_same_inputs_give_the_same_fight_tick_by_tick()
    {
        static List<string> Run()
        {
            var s = new Setup(monsterYCm: 700);
            var trace = new List<string>();
            for (ushort tick = 1; tick <= 300; tick++)
            {
                // The player walks east and west and swings now and then.
                var keys = tick / 40 % 2 == 0 ? Shared.Protocol.MoveKeys.D : Shared.Protocol.MoveKeys.A;
                var actions = tick % 25 == 0 ? InputActions.Attack : InputActions.None;
                s.Harness.SendInputs(1, TestWorlds.Frame(tick, keys, facing: 0, actions: actions));
                s.World.Tick();
                trace.Add($"{s.Monster.Movement.Position} {s.Monster.Movement.Facing} {s.Monster.Actions} {s.Brain.Mode} "
                    + $"{s.Monster.Combatant!.Hp(s.NowMs)} {s.Player.Movement.Position} {s.Player.Combatant!.Hp(s.NowMs)}");
            }
            return trace;
        }

        var first = Run();
        var second = Run();

        Assert.Equal(first, second);
        Assert.Contains(first, line => line.Contains("Startup")); // the monster did fight
    }

    // --- Player death and respawn (user decision 2026-10-22) ---

    [Fact]
    public void A_player_killed_by_a_monster_respawns_after_5_s_at_the_city_spawn_with_full_meters()
    {
        var s = new Setup(monsterYCm: 220);
        var spawn = s.Player.Movement.Position;
        var combatant = s.Player.Combatant!;
        combatant.Meters.Set(ResourceKind.Hp, 1, s.NowMs);
        combatant.Meters.Set(ResourceKind.Stamina, 0, s.NowMs);
        s.Place(s.Player, 0, 10); // a step away from the spawn, so the respawn visibly moves it

        s.TickUntil(() => !combatant.IsAlive, limit: 60);
        uint diedAt = combatant.DiedAtTick;
        Assert.Equal(ActionState.Dead, s.Player.Actions.State);
        s.TickUntil(() => s.Brain.Target == null, limit: 5); // its target is dead

        s.TickUntil(() => combatant.IsAlive, limit: 200);
        Assert.Equal(diedAt + 100u, s.World.ServerTick - 1); // 5000 ms at 20 Hz, in phase 8
        Assert.Equal(spawn, s.Player.Movement.Position);
        Assert.Equal(combatant.MaxHp(s.NowMs), combatant.Hp(s.NowMs));
        Assert.Equal(combatant.Meters.Max(ResourceKind.Stamina, s.NowMs), combatant.Meters.Current(ResourceKind.Stamina, s.NowMs));
        Assert.Equal(combatant.Poise.Max, combatant.Poise.Current);
        Assert.Equal(ActionState.Idle, s.Player.Actions.State);
        s.Harness.Ticks(1); // phase 11 delivers the event
        var respawned = Assert.Single(s.Events.Respawns);
        Assert.Equal(s.Player.Id, respawned.EntityId);
        Assert.Equal(s.Player.CharacterId, Assert.Single(s.Events.RespawnActors));
        Assert.Equal(0, s.World.SpeedViolations);
    }

    [Fact]
    public void A_character_saved_at_0_HP_logs_in_at_the_city_spawn_with_full_meters()
    {
        var s = new Setup(monsterYCm: 5_000);
        var spawn = s.Player.Movement.Position;
        s.Harness.Characters.Add(accountId: 77, slot: 0,
            new GameServer.Persistence.CharacterState(1, 0, GameServer.Sessions.SessionManager.MainWorldKey, 1_500, -900, 0, 0, 0, 0, 0));

        var dead = s.Harness.Join(77, accountId: 77);

        Assert.Equal(spawn, dead.Movement.Position);
        Assert.True(dead.Combatant!.IsAlive);
        Assert.Equal(dead.Combatant.MaxHp(s.NowMs), dead.Combatant.Hp(s.NowMs));
    }

    private static MonsterDef MonsterRow(ConfigSet tables, string id)
    {
        Assert.True(tables.TryGet(typeof(MonsterDef), out var table));
        return table.Rows.Cast<MonsterDef>().Single(m => m.Id == id);
    }
}
