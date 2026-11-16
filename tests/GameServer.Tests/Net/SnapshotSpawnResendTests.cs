using GameServer.Tests.Support;
using Shared.Protocol;

namespace GameServer.Tests.Net;

/// <summary>
/// Spawns survive a lost or full datagram (spec 1.7, protocol 53). A spawn used to go out in one unreliable snapshot only:
/// when that datagram was lost, or the spawn did not fit, the client had the entity with no kind or definition for good
/// (CI runs 43898297010 and 43911530794: a city NPC the client never learned). Now a spawn repeats in every snapshot until
/// the client acknowledges a snapshot that carried it, one that does not fit waits for a later snapshot, and no entity's
/// state goes out before its spawn.
/// </summary>
public sealed class SnapshotSpawnResendTests
{
    private readonly Dictionary<int, ushort> _sequences = new();

    [Fact]
    public void A_spawn_repeats_in_every_snapshot_until_the_client_acknowledges_one_that_carried_it()
    {
        var harness = new WorldHarness();
        harness.Join(1);
        var second = harness.Join(2);
        harness.Ticks(2);

        // Whichever of these datagrams is lost, the next one still names the second player.
        var carrying = harness.Transport.SnapshotsFor(1).SkipWhile(s => !HasSpawn(s, second.Id)).ToList();
        Assert.Equal(3, carrying.Count);
        Assert.All(carrying, s => Assert.True(HasSpawn(s, second.Id), $"tick {s.Header.ServerTick} lost the spawn"));

        // The client got the middle one: the run that started at the first carried it, so the spawn is confirmed.
        Ack(harness, 1, carrying[1].Header.ServerTick);
        harness.Ticks(1);

        var after = harness.LastSnapshot(1);
        Assert.DoesNotContain(after.Spawns, s => s.EntityId == second.Id);
        Assert.Contains(after.Entities, e => e.EntityId == second.Id);
    }

    [Fact]
    public void An_acknowledgement_confirms_nothing_the_client_cannot_have_seen()
    {
        var harness = new WorldHarness();
        harness.Join(1);
        uint beforeSecond = harness.LastSnapshot(1).Header.ServerTick;
        var second = harness.Join(2);

        // Older than the first snapshot that carried the spawn: that snapshot never named the second player.
        Ack(harness, 1, beforeSecond);
        harness.Ticks(1);
        Assert.True(HasSpawn(harness.LastSnapshot(1), second.Id));

        // Newer than any snapshot sent: the client's word for a tick it cannot have, so it confirms nothing (rule 1).
        Ack(harness, 1, harness.LastSnapshot(1).Header.ServerTick + 100);
        harness.Ticks(1);
        Assert.True(HasSpawn(harness.LastSnapshot(1), second.Id));
    }

    [Fact]
    public void Spawns_that_do_not_fit_wait_for_a_later_snapshot_and_no_entity_comes_before_its_spawn()
    {
        // About 8 spawns fill this datagram: a late joiner's 20 spawns (its own included) need several snapshots.
        var harness = new WorldHarness();
        harness.Transport.PeerMaxUnreliableBytes = Snapshot.SizeFor(despawns: 0, spawns: 6, entities: 1);
        var players = Enumerable.Range(1, 20).Select(peer => harness.Join(peer)).ToList();
        var late = players[^1];

        for (int i = 0; i < 10; i++)
        {
            Ack(harness, late.PeerId, harness.LastSnapshot(late.PeerId).Header.ServerTick);
            harness.Ticks(1);
        }

        var snapshots = harness.Transport.SnapshotsFor(late.PeerId);
        var named = new HashSet<uint>();
        foreach (var snapshot in snapshots)
        {
            foreach (var spawn in snapshot.Spawns)
                named.Add(spawn.EntityId);
            Assert.Equal(late.Id, snapshot.Entities[0].EntityId); // the observer stays first
            foreach (var entity in snapshot.Entities.Skip(1))
                Assert.True(named.Contains(entity.EntityId), $"entity {entity.EntityId} came before its spawn at tick {snapshot.Header.ServerTick}");
        }
        Assert.Equal(players.Select(p => p.Id).ToHashSet(), named);
        Assert.True(harness.World.SnapshotsTruncated > 0);
        Assert.Equal(0, harness.Transport.UnreliableTooLarge);
    }

    [Fact]
    public void The_router_keeps_the_newest_acknowledgement_even_from_a_packet_of_repeated_frames()
    {
        var harness = new WorldHarness();
        var player = harness.Join(1);
        var frame = new InputFrame { Sequence = 1, ClientTick = 1 };

        Send(harness, 1, frame with { SnapshotAck = 5 });
        Send(harness, 1, frame with { SnapshotAck = 9 }); // the same frame again: redundant, its acknowledgement is not
        Assert.Equal(9u, player.Inputs.SnapshotAck);
        Assert.Equal(1, harness.Router.RedundantFrames);

        Send(harness, 1, frame with { SnapshotAck = 7 }); // older news never moves it back
        Send(harness, 1, frame with { SnapshotAck = 0 });
        Assert.Equal(9u, player.Inputs.SnapshotAck);
    }

    private static void Send(WorldHarness harness, int peerId, InputFrame frame)
    {
        var buffer = new byte[InputPacket.MaxSize];
        int length = InputPacket.Write(buffer, [frame]);
        harness.Router.OnDataReceived(peerId, Channel.UnreliableSequenced, buffer.AsSpan(0, length));
    }

    private static bool HasSpawn(DecodedSnapshot snapshot, uint entityId) =>
        snapshot.Spawns.Any(s => s.EntityId == entityId && s.Kind == EntityKind.Player);

    /// <summary>An input packet whose only news is the newest snapshot tick the client has (protocol 53).</summary>
    private void Ack(WorldHarness harness, int peerId, uint snapshotTick)
    {
        ushort sequence = (ushort)(_sequences.GetValueOrDefault(peerId) + 1);
        _sequences[peerId] = sequence;
        var buffer = new byte[InputPacket.MaxSize];
        int length = InputPacket.Write(buffer, [new InputFrame { Sequence = sequence, ClientTick = sequence, SnapshotAck = snapshotTick }]);
        harness.Router.OnDataReceived(peerId, Channel.UnreliableSequenced, buffer.AsSpan(0, length));
    }
}
