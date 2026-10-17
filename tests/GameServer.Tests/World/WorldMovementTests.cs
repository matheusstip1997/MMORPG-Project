using GameServer.Telemetry;
using GameServer.Tests.Support;
using GameServer.World;
using GameServer.World.Map;
using Shared.Simulation;
using Shared.World;
using static GameServer.Tests.Support.TestWorlds;
using ProtoKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>Backlog C2/C3 in the world loop: spawn marker, collision, height and region in phase 2, speed check.</summary>
public class WorldMovementTests
{
    private const int StepCm = 20; // 400 cm/s at 20 Hz

    // 20 x 10 cells of 100 cm from (0, 0); column 10 is a wall; columns 15+ are region 2 and 50 cm higher.
    private static WorldMap WalledMap(int spawnXCm = 850, int spawnYCm = 450)
    {
        var grid = TestMaps.Grid(0, 0, 100, 20, 10, baseHeightCm: 1_000,
            walkable: (x, _) => x != 10,
            heightOffset: (x, _) => (short)(x >= 15 ? 50 : 0),
            region: (x, _) => (byte)(x >= 15 ? 2 : 1));
        return TestMaps.Map(grid, [TestMaps.SpawnMarker(spawnXCm, spawnYCm, facing: 16384)],
        [
            new WorldRegion(1, "test.city", "normalCity", new RegionFlags(PvpMode.Off, true, DeathDrop.Off)),
            new WorldRegion(2, "test.combat", "openWorld", TestMaps.OpenFlags),
        ]);
    }

    private static void Walk(WorldHarness harness, int peerId, ProtoKeys keys, int ticks, ref ushort sequence)
    {
        for (int i = 0; i < ticks; i++)
        {
            harness.SendInputs(peerId, Frame(++sequence, keys));
            harness.Ticks(1);
        }
    }

    [Fact]
    public void Players_spawn_at_the_marker_with_its_facing_height_and_region()
    {
        var harness = new WorldHarness(map: WalledMap());

        var player = harness.Join(1);

        Assert.Equal((850, 450, 1_000), (player.XCm, player.YCm, player.HeightCm));
        Assert.Equal(16384, player.Movement.Facing);
        Assert.Equal("test.city", harness.World.Map.RegionByIndex(player.RegionIndex)!.Id);
    }

    [Fact]
    public void Walking_into_a_wall_stops_at_it_and_the_client_predicts_the_same_position()
    {
        var map = WalledMap();
        var harness = new WorldHarness(map: map);
        var player = harness.Join(1);
        ushort sequence = 0;

        Walk(harness, 1, ProtoKeys.D, 20, ref sequence); // 4 m, the wall is 1.5 m away

        Assert.Equal(1_000 * FixedPoint.RawPerCm - 1, player.Movement.Position.X);
        // The client runs the same shared step with the same grid.
        var predicted = new MovementState(FixVec2.FromCm(850, 450), 0);
        for (int i = 0; i < 20; i++)
            predicted = MovementSim.Step(predicted, new MovementInput(MoveKeys.D, 0), false, 400, 20, map.Grid);
        Assert.Equal(predicted.Position, player.Movement.Position);
    }

    [Fact]
    public void Diagonal_input_into_a_wall_slides_along_it()
    {
        var harness = new WorldHarness(map: WalledMap());
        var player = harness.Join(1);
        ushort sequence = 0;

        Walk(harness, 1, ProtoKeys.D | ProtoKeys.W, 20, ref sequence);

        Assert.Equal(1_000 * FixedPoint.RawPerCm - 1, player.Movement.Position.X);
        Assert.True(player.YCm > 450 + 200, $"slid to y {player.YCm}");
    }

    [Fact]
    public void Height_and_region_follow_the_grid_as_the_player_moves()
    {
        var harness = new WorldHarness(map: WalledMap(spawnXCm: 1_450));
        var player = harness.Join(1);
        ushort sequence = 0;
        Assert.Equal(1, player.RegionIndex);

        Walk(harness, 1, ProtoKeys.D, 5, ref sequence); // 1 m: crosses x = 1500

        Assert.Equal(1_550, player.XCm);
        Assert.Equal(1_050, player.HeightCm);
        Assert.Equal("test.combat", harness.World.Map.RegionByIndex(player.RegionIndex)!.Id);
        Assert.True(harness.World.Map.TryGetFlags(player.Movement.Position, out var flags));
        Assert.Equal(PvpMode.ArmedOnly, flags.PvpMode);
    }

    [Fact]
    public void Normal_walking_never_counts_a_speed_violation()
    {
        var violations = new RecordingViolations();
        var harness = new WorldHarness(violations: violations);
        harness.Join(1);
        ushort sequence = 0;

        Walk(harness, 1, ProtoKeys.W | ProtoKeys.D, 40, ref sequence);
        Walk(harness, 1, ProtoKeys.S, 40, ref sequence);

        Assert.Equal(0, harness.World.SpeedViolations);
        Assert.Empty(violations.Items);
    }

    [Fact]
    public void A_move_made_outside_MovementSim_is_counted_once_and_reported()
    {
        var violations = new RecordingViolations();
        var harness = new WorldHarness(violations: violations);
        var player = harness.Join(1, accountId: 77);

        // Something other than phase 2 moved the player 5 m between ticks.
        player.Movement = new MovementState(FixVec2.FromCm(500, 0), 0);
        harness.Ticks(1);
        harness.Ticks(1);

        Assert.Equal(1, harness.World.SpeedViolations);
        var item = Assert.Single(violations.Items);
        Assert.Equal((player.Id, 77L, 500L, 22L), (item.EntityId, item.AccountId, item.DistanceCm, item.AllowedCm));
    }

    [Fact]
    public void The_telemetry_sink_records_SpeedViolation()
    {
        var queue = new TelemetryQueue(16);
        var sink = new TelemetryMovementViolationSink(worldId: 1, queue, new FakeClock());

        sink.SpeedViolation(serverTick: 9, entityId: 4, accountId: 77, distanceCm: 500, allowedCm: 22);

        var records = new TelemetryRecord[4];
        Assert.Equal(1, queue.DrainTo(records));
        var r = records[0];
        Assert.Equal((TelemetryKind.SpeedViolation, 1, 77L, 4L, 500L, 22L, 9L), (r.Kind, r.WorldId, r.AccountId, r.Subject, r.V0, r.V1, r.V2));
    }

    [Fact]
    public void A_world_without_the_configured_spawn_marker_cannot_be_built()
    {
        var map = TestMaps.Open();
        var settings = Settings() with { PlayerSpawnMarkerId = "missing" };

        Assert.Throws<ArgumentException>(() => new WorldHarness(settings, map: map));
    }

    private sealed class RecordingViolations : IMovementViolationSink
    {
        public List<(uint Tick, uint EntityId, long AccountId, long DistanceCm, long AllowedCm)> Items { get; } = [];

        public void SpeedViolation(uint serverTick, uint entityId, long accountId, long distanceCm, long allowedCm) =>
            Items.Add((serverTick, entityId, accountId, distanceCm, allowedCm));
    }
}
