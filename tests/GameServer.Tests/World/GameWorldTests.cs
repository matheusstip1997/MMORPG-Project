using GameServer.Tests.Support;
using GameServer.World;
using Shared.Protocol;
using Shared.Simulation;
using static GameServer.Tests.Support.TestWorlds;
using ProtoKeys = Shared.Protocol.MoveKeys;
using SimKeys = Shared.Simulation.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>Backlog C1: the server world loop (spec 1.6, 2.2, 2.3).</summary>
public class GameWorldTests
{
    private static readonly int StepCm = MovementSim.StepPerTick(400, 20) / FixedPoint.RawPerCm; // 20 cm at 4 m/s, 20 Hz

    [Fact]
    public void Every_tick_runs_the_twelve_phases_in_spec_order()
    {
        var harness = new WorldHarness();
        var phases = new List<TickPhase>();
        harness.World.PhaseStarted = phases.Add;

        harness.Ticks(2);

        var expected = Enum.GetValues<TickPhase>().OrderBy(p => (int)p).ToList();
        Assert.Equal(12, expected.Count);
        Assert.Equal(expected.Concat(expected), phases);
    }

    [Fact]
    public void Event_dispatch_and_telemetry_hooks_run_every_tick_with_the_tick_number()
    {
        var events = new RecordingHook();
        var telemetry = new RecordingHook();
        var harness = new WorldHarness(events: events, telemetry: telemetry);

        harness.Ticks(3);

        Assert.Equal([0u, 1u, 2u], events.Ticks);
        Assert.Equal([0u, 1u, 2u], telemetry.Ticks);
        Assert.Equal(3u, harness.World.ServerTick);
    }

    [Fact]
    public void A_connecting_peer_spawns_at_the_spawn_marker_and_leaves_on_disconnect()
    {
        var map = TestMaps.Open(spawnXCm: 1_000, spawnYCm: -2_000, heightCm: 250);
        var harness = new WorldHarness(map: map);

        var player = harness.Join(peerId: 7, accountId: 42);

        Assert.Equal((1_000, -2_000, 250), (player.XCm, player.YCm, player.HeightCm));
        Assert.Equal(42, player.AccountId);
        Assert.Single(harness.World.Entities);

        harness.Leave(7);
        harness.Ticks(1);

        Assert.Empty(harness.World.Entities);
        Assert.False(harness.World.TryGetPlayer(7, out _));
    }

    [Fact]
    public void Entity_ids_grow_and_are_never_reused()
    {
        var harness = new WorldHarness();
        var first = harness.Join(1);
        harness.Leave(1);
        var second = harness.Join(1);

        Assert.True(second.Id > first.Id);
        Assert.Single(harness.World.Entities);
    }

    [Fact]
    public void Movement_from_queued_inputs_matches_MovementSim_replayed_on_the_same_frames()
    {
        // Spec 2.2: the server runs the same MovementSim the client predicts with, one frame per tick.
        var harness = new WorldHarness();
        var player = harness.Join(1);
        var frames = ScriptedFrames(60);

        var expected = player.Movement;
        foreach (var frame in frames)
        {
            harness.SendInputs(1, WithHistory(frames, frame.Sequence));
            harness.Ticks(1);
            // What the client predicts: wire facing, keys as they are (R-2.2-01).
            var input = new MovementInput((SimKeys)(byte)frame.Keys, Quantize.RoundTripAngle(frame.Facing, InputPacket.FacingBits));
            expected = MovementSim.Step(expected, input, actionActive: false, 400, 20);
            Assert.Equal(expected, player.Movement);
        }

        Assert.Equal(frames[^1].Sequence, player.LastInputSequence);
    }

    [Fact]
    public void Two_worlds_fed_the_same_inputs_end_in_the_same_state()
    {
        var a = new WorldHarness();
        var b = new WorldHarness();
        var frames = ScriptedFrames(40);
        a.Join(1);
        a.Join(2);
        b.Join(1);
        b.Join(2);

        foreach (var frame in frames)
        {
            var mirrored = frame with { Keys = (ProtoKeys)((int)frame.Keys ^ 0b1010) };
            foreach (var world in new[] { a, b })
            {
                world.SendInputs(1, frame);
                world.SendInputs(2, mirrored);
                world.Ticks(1);
            }
        }

        Assert.Equal(a.World.Entities.Select(e => (e.Id, e.Movement)), b.World.Entities.Select(e => (e.Id, e.Movement)));
        Assert.Equal(a.Transport.Sent.Select(s => s.Data), b.Transport.Sent.Select(s => s.Data));
    }

    [Fact]
    public void One_frame_is_applied_per_tick_and_a_starved_player_stands_still()
    {
        var harness = new WorldHarness();
        var player = harness.Join(1);

        // Three frames arrive in one packet: they take three ticks, not one.
        harness.SendInputs(1, Frame(3, ProtoKeys.W), Frame(2, ProtoKeys.W), Frame(1, ProtoKeys.W));
        harness.Ticks(1);
        Assert.Equal(StepCm, player.YCm);
        Assert.Equal(1, player.LastInputSequence);
        harness.Ticks(2);
        Assert.Equal(3 * StepCm, player.YCm);
        Assert.Equal(3, player.LastInputSequence);

        // No frames: no movement, no invented input.
        harness.Ticks(5);
        Assert.Equal(3 * StepCm, player.YCm);
        Assert.Equal(3, player.LastInputSequence);
    }

    [Fact]
    public void Redundant_frames_are_applied_once()
    {
        var harness = new WorldHarness();
        var player = harness.Join(1);

        harness.SendInputs(1, Frame(1, ProtoKeys.D));
        harness.SendInputs(1, Frame(2, ProtoKeys.D), Frame(1, ProtoKeys.D));
        harness.SendInputs(1, Frame(3, ProtoKeys.D), Frame(2, ProtoKeys.D), Frame(1, ProtoKeys.D));
        harness.Ticks(10);

        Assert.Equal(3 * StepCm, player.XCm);
        Assert.Equal(3, harness.Router.RedundantFrames);
        Assert.Equal(0, harness.Router.StaleFrames);
    }

    [Fact]
    public void A_healthy_stream_of_redundant_packets_counts_no_stale_frame()
    {
        // QA wave 3 F4: every packet repeats the last 3 inputs (spec 12.7, 2.2), and those copies were counted as stale.
        var harness = new WorldHarness();
        harness.Join(1);

        for (ushort sequence = 3; sequence <= 22; sequence++)
            harness.SendInputs(1, Frame(sequence, ProtoKeys.D), Frame((ushort)(sequence - 1), ProtoKeys.D), Frame((ushort)(sequence - 2), ProtoKeys.D));

        Assert.Equal(0, harness.Router.StaleFrames);
        Assert.Equal(2 * 19, harness.Router.RedundantFrames); // the first packet has nothing to repeat
    }

    [Fact]
    public void A_reordered_packet_counts_only_its_never_queued_frames_as_stale()
    {
        var harness = new WorldHarness();
        harness.Join(1);

        harness.SendInputs(1, Frame(3, ProtoKeys.D), Frame(2, ProtoKeys.D), Frame(1, ProtoKeys.D));
        // The packet carrying 6, 5, 4 overtakes the one carrying 5, 4, 3 (which itself never arrives) ...
        harness.SendInputs(1, Frame(8, ProtoKeys.D), Frame(7, ProtoKeys.D), Frame(6, ProtoKeys.D));
        // ... and the packet carrying 6, 5, 4 arrives last: 6 is a copy, 5 and 4 are too late.
        harness.SendInputs(1, Frame(6, ProtoKeys.D), Frame(5, ProtoKeys.D), Frame(4, ProtoKeys.D));

        Assert.Equal(2, harness.Router.StaleFrames);
        Assert.Equal(1, harness.Router.RedundantFrames);
    }

    [Fact]
    public void A_late_packet_older_than_what_was_queued_is_ignored()
    {
        var harness = new WorldHarness();
        var player = harness.Join(1);

        harness.SendInputs(1, Frame(4, ProtoKeys.D), Frame(3, ProtoKeys.D), Frame(2, ProtoKeys.D));
        harness.SendInputs(1, Frame(3, ProtoKeys.A), Frame(2, ProtoKeys.A), Frame(1, ProtoKeys.A));
        harness.Ticks(10);

        Assert.Equal(3 * StepCm, player.XCm);
        Assert.Equal(4, player.LastInputSequence);
        // 3 and 2 were queued from the first packet; 1 never was and arrived too late.
        Assert.Equal(2, harness.Router.RedundantFrames);
        Assert.Equal(1, harness.Router.StaleFrames);
    }

    [Fact]
    public void Frames_lost_in_every_packet_are_skipped_and_sequences_wrap()
    {
        var harness = new WorldHarness();
        var player = harness.Join(1);

        harness.SendInputs(1, Frame(65_534, ProtoKeys.W));
        harness.Ticks(1);
        // 65535 and 0 never arrive.
        harness.SendInputs(1, Frame(2, ProtoKeys.W), Frame(1, ProtoKeys.W));
        harness.Ticks(5);

        Assert.Equal(3 * StepCm, player.YCm);
        Assert.Equal(2, player.LastInputSequence);
        Assert.Equal(0, harness.Router.StaleFrames);
    }

    [Fact]
    public void A_full_queue_drops_the_oldest_frames()
    {
        var harness = new WorldHarness(Settings(maxQueuedInputFrames: 2));
        var player = harness.Join(1);

        harness.SendInputs(1, Frame(3, ProtoKeys.W), Frame(2, ProtoKeys.W), Frame(1, ProtoKeys.W));
        harness.Ticks(5);

        Assert.Equal(2 * StepCm, player.YCm);
        Assert.Equal(1, player.Inputs.Dropped);
    }

    [Fact]
    public void Malformed_and_reliable_messages_queue_nothing()
    {
        var harness = new WorldHarness();
        var player = harness.Join(1);

        harness.Router.OnDataReceived(1, Channel.UnreliableSequenced, [(byte)HotMessageId.Input, 0xFF, 0x00]);
        harness.Router.OnDataReceived(1, Channel.ReliableOrdered, [1, 2, 3]);
        harness.Router.OnDataReceived(99, Channel.UnreliableSequenced, [1, 2, 3]);

        Assert.Equal(0, player.Inputs.Count);
        Assert.Equal(1, harness.Router.MalformedPackets);
    }

    [Fact]
    public void First_snapshot_spawns_the_observer_itself_without_an_input_ack()
    {
        var harness = new WorldHarness();
        var player = harness.Join(1);

        var snapshot = harness.LastSnapshot(1);

        Assert.Equal(0u, snapshot.Header.ServerTick);
        Assert.False(snapshot.Header.HasInputAck);
        var spawn = Assert.Single(snapshot.Spawns);
        Assert.Equal((player.Id, EntityKind.Player), (spawn.EntityId, spawn.Kind));
        Assert.Equal(player.Id, Assert.Single(snapshot.Entities).EntityId);
        // Besides the snapshot, only the observer's own name goes out (reliable, spec 9.3 name plates).
        Assert.All(harness.Transport.Sent.Where(s => s.Channel == Channel.ReliableOrdered),
            s => Assert.Equal((byte)ReliableMessageId.EntityIdentity, s.Data[0]));
        Assert.Equal([(player.Id, "c1_0")], harness.Transport.IdentitiesFor(1));
    }

    [Fact]
    public void Snapshots_carry_the_input_ack_and_the_authoritative_own_state()
    {
        var harness = new WorldHarness();
        var player = harness.Join(1);

        harness.SendInputs(1, Frame(9, ProtoKeys.W, facing: 16_384), Frame(8, ProtoKeys.W, facing: 16_384));
        harness.Ticks(2);
        var snapshot = harness.LastSnapshot(1);

        Assert.True(snapshot.Header.HasInputAck);
        Assert.Equal(9, snapshot.Header.LastInputSequence);
        Assert.Equal(player.Id, snapshot.Self.EntityId);
        Assert.Equal(2 * StepCm, snapshot.Self.ZCm);
        Assert.Equal(16_384, snapshot.Self.Facing);
        Assert.Empty(snapshot.Spawns);
    }

    [Fact]
    public void Snapshot_axes_put_height_on_Y_and_the_ground_Y_on_Z()
    {
        var harness = new WorldHarness(map: TestMaps.Open(spawnXCm: 1_000, spawnYCm: 2_000, heightCm: 250));
        harness.Join(1);

        var snapshot = harness.LastSnapshot(1);

        Assert.Equal((1_000, 250, 2_000), (snapshot.Self.XCm, snapshot.Self.YCm, snapshot.Self.ZCm));
        Assert.Equal((1_000, 250, 2_000), (snapshot.Header.OriginXCm, snapshot.Header.OriginYCm, snapshot.Header.OriginZCm));
    }

    [Fact]
    public void Players_spawn_for_each_other_with_the_observer_first_and_despawn_when_one_leaves()
    {
        var harness = new WorldHarness();
        var first = harness.Join(1);
        var second = harness.Join(2);

        var forFirst = harness.LastSnapshot(1);
        Assert.Equal(second.Id, Assert.Single(forFirst.Spawns).EntityId);
        Assert.Equal([first.Id, second.Id], forFirst.Entities.Select(e => e.EntityId));

        var forSecond = harness.LastSnapshot(2);
        Assert.Equal([first.Id, second.Id], forSecond.Spawns.Select(s => s.EntityId));
        Assert.Equal([second.Id, first.Id], forSecond.Entities.Select(e => e.EntityId));

        harness.Leave(2);
        harness.Ticks(1);

        var after = harness.LastSnapshot(1);
        Assert.Equal(second.Id, Assert.Single(after.Despawns));
        Assert.Equal([first.Id], after.Entities.Select(e => e.EntityId));
    }

    [Fact]
    public void Snapshots_stay_within_the_peer_s_datagram_limit_with_the_observer_first_and_what_does_not_fit_counted()
    {
        // The WP15 load test: 50 players at the spawn. A late joiner's first snapshot (49 spawns and 50 entities, about
        // 1100 bytes) fit the 1200-byte buffer but not LiteNetLib's 1020 bytes for that peer, which threw on the world thread.
        var harness = new WorldHarness();
        harness.Transport.PeerMaxUnreliableBytes = 1_020;

        var players = Enumerable.Range(1, 50).Select(peer => harness.Join(peer)).ToList();
        harness.Ticks(1);

        Assert.All(harness.Transport.Sent, s => Assert.InRange(s.Data.Length, 1, 1_020));
        Assert.Equal(0, harness.Transport.UnreliableTooLarge);
        Assert.True(harness.World.SnapshotsTruncated > 0);
        foreach (var player in players)
        {
            Assert.All(harness.Transport.SnapshotsFor(player.PeerId), s => Assert.Equal(player.Id, s.Self.EntityId));
            // Once everyone has spawned, 50 idle players fit in one datagram.
            Assert.Equal(50, harness.LastSnapshot(player.PeerId).Entities.Count);
        }
    }

    [Fact]
    public void Spawns_that_would_fill_the_datagram_still_leave_room_for_the_observer_first()
    {
        // About 8 spawns fill this datagram: a late joiner's 19 spawns would push its own entity out.
        var harness = new WorldHarness();
        harness.Transport.PeerMaxUnreliableBytes = Snapshot.SizeFor(despawns: 0, spawns: 6, entities: 1);

        var players = Enumerable.Range(1, 20).Select(peer => harness.Join(peer)).ToList();

        Assert.True(harness.World.SnapshotsTruncated > 0);
        Assert.Equal(0, harness.Transport.UnreliableTooLarge);
        foreach (var player in players)
            Assert.All(harness.Transport.SnapshotsFor(player.PeerId), s => Assert.Equal(player.Id, s.Entities[0].EntityId));
    }

    [Fact]
    public void A_peer_that_is_gone_gets_no_snapshot()
    {
        var harness = new WorldHarness();
        harness.Transport.PeerMaxUnreliableBytes = 0;

        harness.Join(1);

        Assert.Empty(harness.Transport.SnapshotsFor(1));
        Assert.Equal(0, harness.World.SnapshotsTruncated);
    }

    [Fact]
    public void Walking_out_of_interest_despawns_and_walking_back_spawns_again()
    {
        var harness = new WorldHarness();
        harness.Join(1);
        var walker = harness.Join(2);
        ushort sequence = 0;

        // 3000 cm of interest at 20 cm per tick: 151 ticks east puts the walker outside.
        int ticksOut = 3_000 / StepCm + 1;
        for (int i = 0; i < ticksOut; i++)
        {
            harness.SendInputs(2, Frame(++sequence, ProtoKeys.D));
            harness.Ticks(1);
        }
        Assert.True(walker.XCm > 3_000);
        var snapshots = harness.Transport.SnapshotsFor(1);
        Assert.Equal(walker.Id, Assert.Single(snapshots.SelectMany(s => s.Despawns)));
        Assert.DoesNotContain(snapshots[^1].Entities, e => e.EntityId == walker.Id);

        harness.SendInputs(2, Frame(++sequence, ProtoKeys.A));
        harness.Ticks(1);

        Assert.Equal(walker.Id, Assert.Single(harness.LastSnapshot(1).Spawns).EntityId);
    }

    [Fact]
    public void Snapshots_follow_the_snapshot_rate()
    {
        var harness = new WorldHarness(Settings(snapshotRateHz: 10));
        harness.Join(1);
        harness.Ticks(5);

        Assert.Equal([0u, 2u, 4u], harness.Transport.SnapshotsFor(1).Select(s => s.Header.ServerTick));
    }

    [Fact]
    public void Steady_state_ticks_allocate_nothing()
    {
        var harness = new WorldHarness();
        const int players = 20;
        for (int peer = 1; peer <= players; peer++)
            harness.Join(peer);
        harness.Transport.Record = false;
        var packet = new byte[InputPacket.MaxSize];
        var frames = new InputFrame[3];
        ushort sequence = 10; // the history frames of the first packet stay above 0

        void TickWithInputs(int count)
        {
            for (int i = 0; i < count; i++)
            {
                sequence++;
                // Back and forth inside the same cells, so no new grid cell is ever created after warm-up.
                var keys = sequence / 10 % 2 == 0 ? ProtoKeys.W | ProtoKeys.D : ProtoKeys.S | ProtoKeys.A;
                for (int f = 0; f < frames.Length; f++)
                    frames[f] = Frame((ushort)(sequence - f), keys, facing: (ushort)(sequence * 97));
                int length = InputPacket.Write(packet, frames);
                for (int peer = 1; peer <= players; peer++)
                    harness.Router.OnDataReceived(peer, Channel.UnreliableSequenced, packet.AsSpan(0, length));
                harness.World.Tick();
            }
        }

        TickWithInputs(100);
        long before = GC.GetAllocatedBytesForCurrentThread();
        TickWithInputs(200);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(harness.Transport.SendCount > 200 * players);
    }

    /// <summary>Frames with every key combination and a turning facing.</summary>
    private static List<InputFrame> ScriptedFrames(int count)
    {
        var frames = new List<InputFrame>();
        for (int i = 1; i <= count; i++)
            frames.Add(Frame((ushort)i, (ProtoKeys)(i % 16), facing: (ushort)(i * 1_237)));
        return frames;
    }

    /// <summary>The newest frame and up to two before it, as a client sends them (spec 1.7).</summary>
    private static InputFrame[] WithHistory(List<InputFrame> frames, ushort newest) =>
        frames.Where(f => f.Sequence <= newest && f.Sequence > newest - 3).OrderByDescending(f => f.Sequence).ToArray();

    private sealed class RecordingHook : ITickHook
    {
        public List<uint> Ticks { get; } = [];

        public void Run(uint serverTick) => Ticks.Add(serverTick);
    }
}
