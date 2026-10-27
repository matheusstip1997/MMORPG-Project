using Game.Gameplay.Prediction;
using GameServer.Combat;
using GameServer.Entities;
using GameServer.Tests.Support;
using GameServer.World;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using static GameServer.Tests.Support.TestWorlds;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// R-2.2-07 (user decision 2026-10-27) through the tick with the committed tables: monsters and the boss block movement
/// (walks slide round a body, dashes and the dodge stop at it), monsters never push a player, players pass through each
/// other, and client prediction agrees with the server over a 100 ms link. Warrior 40 cm, demon 50 cm, boss 120 cm,
/// tolerance 2 cm: contact is 88 cm from a demon, 158 cm from the boss.
/// </summary>
public class BodyCollisionWorldTests
{
    private const ushort North = 0;

    /// <summary>Monsters never see players: they stay where they spawned, idle.</summary>
    private sealed class PlayersUnseen : IVisibilityQuery
    {
        public bool CanSee(Entity observer, Entity target) => !(observer is Monster && target is Player);
    }

    private sealed class Setup
    {
        /// <param name="monsters">Monster spawn markers (each gets a spawn point of <paramref name="monsterId"/>).</param>
        /// <param name="aggro">False: monsters never see players and stay idle.</param>
        public Setup((string Id, int XCm, int YCm)[] monsters, string monsterId = TestCombat.MonsterId, bool aggro = false,
            int playerXCm = 0, int playerYCm = 0)
        {
            Tables = TestCombat.Tables();
            Catalog = ActionCatalog.From(Tables);
            Grid = TestMaps.Open().Grid;
            var markers = new List<WorldMarker> { TestMaps.SpawnMarker(playerXCm, playerYCm) };
            markers.AddRange(monsters.Select(m => TestCombat.MonsterMarker(m.XCm, m.YCm, id: m.Id)));
            Harness = new WorldHarness(map: TestMaps.Map(Grid, markers), visibility: aggro ? null : new PlayersUnseen(),
                actions: Catalog, combat: TestCombat.Rules(Tables, monsters.Select(m => TestCombat.Point(m.Id, monsterId)).ToArray()),
                projectiles: ProjectileRules.From(Tables));
        }

        public ConfigSet Tables { get; }
        public ActionCatalog Catalog { get; }
        public Shared.World.WorldGrid Grid { get; }
        public WorldHarness Harness { get; }
        public GameWorld World => Harness.World;
        public List<Monster> Monsters => World.Entities.OfType<Monster>().ToList();

        public long ContactRaw(Entity a, Entity b) =>
            (long)a.Combatant!.BodyRadiusRaw + b.Combatant!.BodyRadiusRaw - FixedPoint.FromCm(Tables.Single<BodyCollisionSettings>().OverlapToleranceCm);

        public void Place(Entity entity, int xCm, int yCm)
        {
            var position = FixVec2.FromCm(xCm, yCm);
            entity.Movement = new MovementState(position, entity.Movement.Facing);
            entity.SpeedCheckedPosition = position;
            World.Grid.Update(entity);
        }
    }

    private static long DistanceSquared(Entity a, Entity b)
    {
        long dx = (long)a.Movement.Position.X - b.Movement.Position.X;
        long dy = (long)a.Movement.Position.Y - b.Movement.Position.Y;
        return dx * dx + dy * dy;
    }

    private static long Distance(Entity a, Entity b) => (long)Math.Sqrt(DistanceSquared(a, b));

    [Theory]
    [InlineData(MoveKeys.W, 0, 300)]
    [InlineData(MoveKeys.S, 0, -300)]
    [InlineData(MoveKeys.D, 300, 0)]
    [InlineData(MoveKeys.A, -300, 0)]
    [InlineData(MoveKeys.W | MoveKeys.D, 212, 212)]
    [InlineData(MoveKeys.S | MoveKeys.A, -212, -212)]
    public void A_player_walking_into_a_demon_stops_at_its_body(MoveKeys keys, int demonXCm, int demonYCm)
    {
        var s = new Setup([("test.demon", demonXCm, demonYCm)]);
        var player = s.Harness.Join(1);
        var demon = Assert.Single(s.Monsters);
        var demonAt = demon.Movement.Position;
        long contact = s.ContactRaw(player, demon);

        for (ushort i = 1; i <= 40; i++)
        {
            s.Harness.SendInputs(1, Frame(i, keys, North));
            s.Harness.Ticks(1);
            Assert.True(DistanceSquared(player, demon) >= contact * contact, $"inside the demon at tick {i}: {player}");
        }

        Assert.InRange(Distance(player, demon), contact, contact + FixedPoint.FromCm(1));
        Assert.Equal(demonAt, demon.Movement.Position); // the mover yields: nothing pushed the demon
        Assert.Equal(0, s.World.SpeedViolations);
    }

    [Fact]
    public void A_walk_past_a_demon_slides_round_it()
    {
        var s = new Setup([("test.demon", 30, 300)]);
        var player = s.Harness.Join(1);
        var demon = Assert.Single(s.Monsters);
        long contact = s.ContactRaw(player, demon);

        for (ushort i = 1; i <= 50; i++)
        {
            s.Harness.SendInputs(1, Frame(i, MoveKeys.W, North));
            s.Harness.Ticks(1);
            Assert.True(DistanceSquared(player, demon) >= contact * contact, $"inside the demon at tick {i}");
        }

        Assert.True(player.Movement.Position.Y > demon.Movement.Position.Y, $"did not get round: {player}");
        Assert.Equal(0, s.World.SpeedViolations);
    }

    [Fact]
    public void A_chasing_demon_never_pushes_a_player_nor_enters_its_body()
    {
        // The demon aggroes, closes in, claws and pounces (a 300 cm dash): it never moves the player that stands still.
        var s = new Setup([("test.demon", 0, 600)], aggro: true);
        var player = s.Harness.Join(1);
        var demon = Assert.Single(s.Monsters);
        var standing = player.Movement.Position;
        long contact = s.ContactRaw(player, demon);
        long closest = long.MaxValue;

        for (int tick = 0; tick < 120 && player.Combatant!.IsAlive; tick++)
        {
            s.Harness.Ticks(1);
            Assert.Equal(standing, player.Movement.Position);
            Assert.True(DistanceSquared(player, demon) >= contact * contact, $"inside the player at tick {tick}");
            closest = Math.Min(closest, Distance(player, demon));
        }
        Assert.True(closest < FixedPoint.FromCm(300), "the demon never closed in");
    }

    [Fact]
    public void A_dodge_into_the_boss_stops_at_its_body_and_keeps_its_timing_and_i_frames()
    {
        // R-2.2-07: no dodging through the boss. The dodge goes along W (R-3.3-02) and would carry the player 400 cm.
        var s = new Setup([("test.boss", 0, 400)], TestCombat.BossId);
        var player = s.Harness.Join(1);
        var boss = Assert.Single(s.Monsters);
        var dodge = s.Catalog.Find("dodge")!;
        long contact = s.ContactRaw(player, boss);

        s.Harness.SendInputs(1, Frame(1, MoveKeys.W, North, InputActions.Dodge));
        s.Harness.Ticks(1);
        int busyTicks = player.Actions.IsBusy ? 1 : 0, iFrameTicks = player.Actions.InIFrames ? 1 : 0;
        for (ushort i = 2; i < dodge.TotalTicks + 4; i++)
        {
            s.Harness.SendInputs(1, Frame(i, facing: North));
            s.Harness.Ticks(1);
            busyTicks += player.Actions.IsBusy ? 1 : 0;
            iFrameTicks += player.Actions.InIFrames ? 1 : 0;
            Assert.True(DistanceSquared(player, boss) >= contact * contact, $"inside the boss at tick {i}");
        }

        Assert.Equal(0, player.Movement.Position.X);
        Assert.InRange(boss.Movement.Position.Y - (long)player.Movement.Position.Y, contact, contact + 2);
        Assert.Equal(dodge.TotalTicks, busyTicks); // the dash ended early, the action did not
        Assert.Equal(dodge.Effects.Single(e => e.Kind == EffectKind.Dash).DashTicks, iFrameTicks);
        Assert.Equal(0, s.World.SpeedViolations);
    }

    [Fact]
    public void Two_demons_chasing_one_player_do_not_overlap()
    {
        var s = new Setup([("test.demon.a", -60, 500), ("test.demon.b", 60, 500)], aggro: true);
        var player = s.Harness.Join(1);
        var demons = s.Monsters;
        Assert.Equal(2, demons.Count);
        long apart = s.ContactRaw(demons[0], demons[1]);

        for (int tick = 0; tick < 100 && player.Combatant!.IsAlive; tick++)
        {
            s.Harness.Ticks(1);
            Assert.True(DistanceSquared(demons[0], demons[1]) >= apart * apart, $"demons overlap at tick {tick}");
        }
    }

    [Fact]
    public void Players_pass_through_each_other()
    {
        var s = new Setup([]);
        var walker = s.Harness.Join(1);
        var other = s.Harness.Join(2);
        s.Place(other, 0, 100);

        for (ushort i = 1; i <= 15; i++)
        {
            s.Harness.SendInputs(1, Frame(i, MoveKeys.W, North));
            s.Harness.Ticks(1);
        }

        Assert.Equal(FixVec2.FromCm(0, 300), walker.Movement.Position); // 15 steps of 20 cm, straight through
        Assert.Equal(FixVec2.FromCm(0, 100), other.Movement.Position);
    }

    [Fact]
    public void Dead_monsters_and_other_players_are_not_gathered()
    {
        var s = new Setup([("test.demon", 0, 300)]);
        var player = s.Harness.Join(1);
        var other = s.Harness.Join(2);
        var demon = Assert.Single(s.Monsters);
        var blockers = new BodyBlockers(s.World.Grid);
        var bodies = new BodyList();

        blockers.Gather(player.Id, EntityKind.Player, player.Movement.Position, FixedPoint.FromCm(1000), bodies);
        Assert.Equal(1, bodies.Count);
        Assert.Equal(demon.Id, bodies[0].Id);

        blockers.Gather(demon.Id, EntityKind.Monster, demon.Movement.Position, FixedPoint.FromCm(1000), bodies);
        Assert.Equal(new[] { player.Id, other.Id }.OrderBy(id => id), new[] { bodies[0].Id, bodies[1].Id });

        demon.Combatant!.Die(s.World.ServerTick);
        blockers.Gather(player.Id, EntityKind.Player, player.Movement.Position, FixedPoint.FromCm(1000), bodies);
        Assert.Equal(0, bodies.Count);
    }

    [Fact]
    public void A_demon_spawned_on_a_player_pushes_nobody_and_both_can_part()
    {
        // D6: placement is never blocked; overlapping bodies may move apart, never deeper.
        var s = new Setup([("test.demon", 0, 0)]);
        var player = s.Harness.Join(1);
        var demon = Assert.Single(s.Monsters);
        Assert.Equal(demon.Movement.Position, player.Movement.Position);

        ushort sequence = 0;
        for (int i = 0; i < 10; i++)
        {
            s.Harness.SendInputs(1, Frame(++sequence, MoveKeys.W, North));
            s.Harness.Ticks(1);
        }
        Assert.Equal(FixVec2.FromCm(0, 200), player.Movement.Position); // out, unhindered
        Assert.Equal(FixVec2.Zero, demon.Movement.Position);

        for (int i = 0; i < 15; i++)
        {
            s.Harness.SendInputs(1, Frame(++sequence, MoveKeys.S, North));
            s.Harness.Ticks(1);
        }
        long contact = s.ContactRaw(player, demon);
        Assert.InRange(Distance(player, demon), contact, contact + 2); // back in: stopped at contact
        Assert.Equal(0, s.World.SpeedViolations);
    }

    // --- Client prediction over a 100 ms link (50 ms each way) ---

    private static WorldActionsTests.Client Client(Setup s)
    {
        var client = new WorldActionsTests.Client(s.Harness, s.Grid, s.Catalog, linkTicks: 1, bodyTables: s.Tables);
        client.Predictor.SetLoadout(s.Catalog.Loadout("warrior"));
        for (int i = 0; i < 4; i++)
            client.Tick(MoveKeys.None, North, InputActions.None, 0); // the registry learns the monsters
        return client;
    }

    [Fact]
    public void Prediction_stops_at_a_still_demon_like_the_server()
    {
        var s = new Setup([("test.demon", 0, 300)]);
        var client = Client(s);

        for (int i = 0; i < 40; i++)
            client.Tick(MoveKeys.W, North, InputActions.None, 0);
        for (int i = 0; i < 4; i++)
            client.Tick(MoveKeys.None, North, InputActions.None, 0);

        var demon = Assert.Single(s.Monsters);
        Assert.Equal(0, client.Predictor.Corrections);
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
        Assert.InRange(Distance(client.Server, demon), s.ContactRaw(client.Server, demon), s.ContactRaw(client.Server, demon) + FixedPoint.FromCm(1));
    }

    [Fact]
    public void Prediction_slides_round_a_still_demon_like_the_server()
    {
        var s = new Setup([("test.demon", 30, 300)]);
        var client = Client(s);

        for (int i = 0; i < 50; i++)
            client.Tick(MoveKeys.W, North, InputActions.None, 0);
        for (int i = 0; i < 4; i++)
            client.Tick(MoveKeys.None, North, InputActions.None, 0);

        Assert.Equal(0, client.Predictor.Corrections);
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
        Assert.True(client.Server.Movement.Position.Y > FixedPoint.FromCm(300));
    }

    [Fact]
    public void Prediction_stops_a_dodge_at_the_boss_like_the_server()
    {
        var s = new Setup([("test.boss", 0, 400)], TestCombat.BossId);
        var client = Client(s);

        client.Tick(MoveKeys.W, North, InputActions.Dodge, 0);
        for (int i = 0; i < 20; i++)
            client.Tick(MoveKeys.None, North, InputActions.None, 0);

        var boss = Assert.Single(s.Monsters);
        Assert.Equal(0, client.Predictor.Corrections);
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
        Assert.InRange(boss.Movement.Position.Y - (long)client.Server.Movement.Position.Y, s.ContactRaw(client.Server, boss),
            s.ContactRaw(client.Server, boss) + 2);
    }

    [Fact]
    public void Walking_into_a_moving_demon_corrects_by_no_more_than_its_walk_over_the_round_trip()
    {
        // D8: prediction collides with the newest snapshot positions, the server with where the demon stands now. A demon
        // walking into contact therefore gives corrections; they stay within what both walk over the round trip and the
        // snapshot cadence (here 100 ms + 50 ms), and the two sides agree again once both stand still.
        var s = new Setup([("test.demon", 0, 700)], aggro: true);
        var client = Client(s);
        var demon = Assert.Single(s.Monsters);
        Assert.True(s.Tables.TryGet(typeof(MonsterDef), out var monsters));
        int demonSpeed = monsters.Rows.Cast<MonsterDef>().Single(m => m.Id == TestCombat.MonsterId).MoveSpeedCmPerSec;
        int playerSpeed = s.Tables.Single<StartingValuesM1>().MoveSpeedCmPerSec;
        double boundCm = (demonSpeed + playerSpeed) * 0.150;

        for (int i = 0; i < 30 && client.Server.Combatant!.IsAlive; i++)
            client.Tick(MoveKeys.W, North, InputActions.None, 0);

        var corrected = client.Results.Where(r => r.Outcome == ReconcileOutcome.Corrected).ToList();
        Assert.All(corrected, r => Assert.True(r.ErrorCm <= boundCm, $"a {r.ErrorCm:F1} cm correction (bound {boundCm:F0} cm)"));
        Assert.True(DistanceSquared(client.Server, demon) >= s.ContactRaw(client.Server, demon) * s.ContactRaw(client.Server, demon));
    }
}
