using Game.Gameplay.Entities;
using GameServer.Entities;
using GameServer.Net;
using GameServer.Social;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using static GameServer.Tests.Support.TestWorlds;

namespace GameServer.Tests.Net;

/// <summary>
/// Which entity states a full snapshot carries (spec 1.7 priority, user decision 2026-11-16, option A; protocol 54). Interest
/// used to be cut in id order, so the same highest ids were left out of every snapshot and the client took them as gone.
/// Now the own entity goes first, then the critical ones (an action within the telegraph radius, the lock target, party
/// members, a state at the stale limit), then the rest by age x kind weight x distance weight; the client keeps an entity it
/// does not hear about, and only a despawn, repeated until acknowledged, removes it.
/// </summary>
public sealed class SnapshotPriorityTests
{
    /// <summary>A datagram with room for the observer and this many idle states (no own resources: no combat rules here).</summary>
    private static int RoomFor(int others) => Snapshot.SizeFor(despawns: 0, spawns: 0, entities: 1 + others);

    private static SnapshotPriorityRules Rules(int telegraphRadiusCm = 0, int nearRadiusCm = 1_000, int farWeightPercent = 25,
        int maxStaleMs = 60_000) =>
        SnapshotPriorityRules.Create(new SnapshotPriority
        {
            TelegraphRadiusCm = telegraphRadiusCm,
            NearRadiusCm = nearRadiusCm,
            FarWeightPercent = farWeightPercent,
            MaxStaleMs = maxStaleMs,
        }, [], tickRateHz: 20);

    [Fact]
    public void Every_state_goes_out_in_turn_when_interest_outgrows_the_datagram_and_nothing_reads_as_gone()
    {
        // 12 others, room for 4 states: before this change the 8 highest ids were never sent.
        var harness = new WorldHarness();
        var players = Enumerable.Range(1, 13).Select(peer => harness.Join(peer)).ToList();
        var observer = players[0];
        harness.Transport.PeerMaxUnreliableBytes = RoomFor(4);
        harness.TicksAcknowledged(20); // every spawn goes out and is confirmed

        var steady = Steady(harness, observer, ticks: 30);

        foreach (var other in players.Skip(1))
            Assert.True(LongestGap(steady, other.Id) <= 3, $"{other.Id} waited {LongestGap(steady, other.Id)} snapshots");
        Assert.All(steady, s => Assert.Empty(s.Despawns));
        Assert.All(steady, s => Assert.Equal(observer.Id, s.Entities[0].EntityId)); // the own entity stays first
        Assert.True(harness.World.SnapshotStatesDeferred > 0);
        Assert.Equal(0, harness.Transport.UnreliableTooLarge);
    }

    [Fact]
    public void Near_states_go_out_more_often_than_far_ones_and_none_waits_past_the_stale_limit()
    {
        var harness = new WorldHarness(snapshotPriority: Rules(nearRadiusCm: 1_000, farWeightPercent: 25, maxStaleMs: 400));
        var players = Enumerable.Range(1, 13).Select(peer => harness.Join(peer)).ToList();
        var observer = players[0];
        var far = players.Skip(7).ToList();
        foreach (var player in far)
            Place(harness, player, 2_500, 0); // inside the 3000 cm interest square, outside the 1000 cm near radius
        harness.Transport.PeerMaxUnreliableBytes = RoomFor(4);
        harness.TicksAcknowledged(20);

        var steady = Steady(harness, observer, ticks: 40);

        double nearRate = players.Skip(1).Take(6).Average(p => steady.Count(s => Carries(s, p.Id)));
        double farRate = far.Average(p => steady.Count(s => Carries(s, p.Id)));
        Assert.True(nearRate > 2 * farRate, $"near {nearRate} vs far {farRate} states in 40 snapshots");
        // 400 ms at 20 Hz is 8 ticks: an entity at the stale limit becomes critical, so none waits longer.
        foreach (var player in far)
            Assert.True(LongestGap(steady, player.Id) <= 8, $"{player.Id} waited {LongestGap(steady, player.Id)} snapshots");
    }

    [Fact]
    public void The_lock_target_goes_out_in_every_snapshot()
    {
        var harness = new WorldHarness(snapshotPriority: Rules());
        var players = Enumerable.Range(1, 11).Select(peer => harness.Join(peer)).ToList();
        var observer = players[0];
        var target = players[^1];
        Place(harness, target, 2_500, 0); // far and one of many: it would wait its turn
        harness.Transport.PeerMaxUnreliableBytes = RoomFor(2);
        harness.TicksAcknowledged(20);

        observer.Inputs.LockTargetId = target.Id;
        var steady = Steady(harness, observer, ticks: 20);

        Assert.All(steady, s => Assert.True(Carries(s, target.Id), $"tick {s.Header.ServerTick} left out the lock target"));
    }

    [Fact]
    public void Party_members_go_out_in_every_snapshot()
    {
        var harness = new WorldHarness(snapshotPriority: Rules());
        var players = Enumerable.Range(1, 11).Select(peer => harness.Join(peer)).ToList();
        var observer = players[0];
        var member = players[^1];
        Place(harness, member, 2_500, 0);
        harness.World.Snapshots.Parties = new PairParty(observer.CharacterId, member.CharacterId);
        harness.Transport.PeerMaxUnreliableBytes = RoomFor(2);
        harness.TicksAcknowledged(20);

        var steady = Steady(harness, observer, ticks: 20);

        Assert.All(steady, s => Assert.True(Carries(s, member.Id), $"tick {s.Header.ServerTick} left out the party member"));
    }

    [Fact]
    public void An_attacker_within_the_telegraph_radius_goes_out_in_every_snapshot_of_its_action()
    {
        var harness = new WorldHarness(actions: TestActions.Catalog(), snapshotPriority: Rules(telegraphRadiusCm: 1_000));
        var players = Enumerable.Range(1, 11).Select(peer => harness.Join(peer)).ToList();
        var observer = players[0];
        var attacker = players[^1];
        harness.Transport.PeerMaxUnreliableBytes = RoomFor(2) + 8; // room for the action fields too
        harness.TicksAcknowledged(20);

        uint start = harness.LastSnapshot(observer.PeerId).Header.ServerTick;
        harness.SendInputs(attacker.PeerId, Frame(1, actions: InputActions.Attack));
        harness.TicksAcknowledged(8);

        var during = harness.Transport.SnapshotsFor(attacker.PeerId).Where(s => s.Header.ServerTick > start)
            .Where(s => s.Self.State != (byte)ActionState.Idle).Select(s => s.Header.ServerTick).ToHashSet();
        Assert.True(during.Count >= 4, $"the attack lasted {during.Count} snapshots");
        foreach (var snapshot in harness.Transport.SnapshotsFor(observer.PeerId).Where(s => during.Contains(s.Header.ServerTick)))
            Assert.True(Carries(snapshot, attacker.Id), $"tick {snapshot.Header.ServerTick} left out the attacker");
    }

    [Fact]
    public void A_despawn_repeats_until_acknowledged_and_a_return_cancels_it()
    {
        var harness = new WorldHarness();
        var observer = harness.Join(1);
        var other = harness.Join(2);
        harness.TicksAcknowledged(3);

        Place(harness, other, 5_000, 0); // out of the 3000 cm interest square
        harness.Ticks(3); // no acknowledgement: the client may have lost them all
        var unconfirmed = harness.Transport.SnapshotsFor(observer.PeerId).TakeLast(3).ToList();
        Assert.All(unconfirmed, s => Assert.Contains(other.Id, s.Despawns));

        harness.TicksAcknowledged(2); // the client has one that carried it
        Assert.DoesNotContain(other.Id, harness.LastSnapshot(observer.PeerId).Despawns);

        Place(harness, other, 5_000, 0);
        harness.Ticks(1);
        Place(harness, other, 0, 0); // back before the despawn was confirmed
        harness.Ticks(1);
        var back = harness.LastSnapshot(observer.PeerId);
        Assert.DoesNotContain(other.Id, back.Despawns);
        Assert.Contains(back.Spawns, s => s.EntityId == other.Id);
    }

    [Fact]
    public void The_client_keeps_every_entity_of_a_crowd_larger_than_one_datagram()
    {
        // 80 players in one interest square at the real 1000-byte cap: about 62 idle states fit.
        var harness = new WorldHarness(snapshotPriority: Rules(nearRadiusCm: 1_500, maxStaleMs: 500));
        harness.Transport.PeerMaxUnreliableBytes = 1_000;
        var players = Enumerable.Range(1, 80).Select(peer => harness.Join(peer)).ToList();
        var observer = players[0];
        var registry = new EntityRegistry(Game.Gameplay.Prediction.PredictionSettings.From(ServerTestEnvironment.LoadTables()));
        var spawned = new List<uint>();
        var despawned = new List<uint>();
        registry.Spawned += e => spawned.Add(e.Id);
        registry.Despawned += e => despawned.Add(e.Id);
        int read = 0;
        for (int tick = 0; tick < 60; tick++)
        {
            var sent = harness.Transport.Sent.Where(s => s.PeerId == observer.PeerId && s.Channel == Channel.UnreliableSequenced && s.Data[0] == (byte)HotMessageId.Snapshot).ToList();
            for (; read < sent.Count; read++)
                Assert.True(registry.ApplySnapshot(sent[read].Data, out _, out _, out _));
            registry.Update(registry.NewestSnapshotTick);
            if (registry.NewestSnapshotTick != 0)
                observer.Inputs.NoteSnapshotAck(registry.NewestSnapshotTick);
            harness.Ticks(1);
        }

        Assert.Equal(players.Skip(1).Select(p => p.Id).OrderBy(id => id), registry.Entities.Keys.OrderBy(id => id));
        Assert.Empty(despawned);
        Assert.Equal(spawned.Distinct().Count(), spawned.Count); // no flicker: each spawned once
        Assert.All(registry.Entities.Values, e => Assert.NotEqual(EntityKind.None, e.Kind));
        Assert.True(harness.World.SnapshotStatesDeferred > 0, "the crowd did not outgrow the datagram");
        Assert.True(harness.World.LongestSnapshotStateWaitTicks <= 10);
        Assert.Equal(0, harness.Transport.UnreliableTooLarge);
    }

    [Fact]
    public void The_order_is_the_same_for_the_same_world()
    {
        static List<byte[]> Run()
        {
            var harness = new WorldHarness(snapshotPriority: Rules());
            var players = Enumerable.Range(1, 13).Select(peer => harness.Join(peer)).ToList();
            Place(harness, players[^1], 2_000, 500);
            harness.Transport.PeerMaxUnreliableBytes = RoomFor(3);
            harness.TicksAcknowledged(30);
            return harness.Transport.Sent.Where(s => s.PeerId == 1 && s.Channel == Channel.UnreliableSequenced && s.Data[0] == (byte)HotMessageId.Snapshot).Select(s => s.Data).ToList();
        }

        var first = Run();
        var second = Run();
        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
            Assert.Equal(first[i], second[i]);
    }

    /// <summary>The observer's snapshots of the next <paramref name="ticks"/> ticks, every client acknowledging each tick.</summary>
    private static List<DecodedSnapshot> Steady(WorldHarness harness, Player observer, int ticks)
    {
        int from = harness.Transport.SnapshotsFor(observer.PeerId).Count;
        harness.TicksAcknowledged(ticks);
        return harness.Transport.SnapshotsFor(observer.PeerId).Skip(from).ToList();
    }

    private static bool Carries(DecodedSnapshot snapshot, uint entityId) => snapshot.Entities.Any(e => e.EntityId == entityId);

    /// <summary>The most snapshots in a row between two that carried the entity's state (from the window's start and to its end).</summary>
    private static int LongestGap(List<DecodedSnapshot> snapshots, uint entityId)
    {
        int longest = 0, since = 0;
        foreach (var snapshot in snapshots)
        {
            since++;
            if (!Carries(snapshot, entityId))
                continue;
            longest = Math.Max(longest, since);
            since = 0;
        }
        return Math.Max(longest, since + 1);
    }

    private static void Place(WorldHarness harness, Player player, int xCm, int yCm)
    {
        player.Movement = new MovementState(FixVec2.FromCm(xCm, yCm), player.Movement.Facing);
        harness.World.Grid.Update(player);
    }

    /// <summary>Two characters in one party, nobody else in any.</summary>
    private sealed class PairParty(long a, long b) : IPartyQuery
    {
        public long PartyIdOf(long characterId) => characterId == a || characterId == b ? 1 : 0;

        public bool SameParty(long characterA, long characterB) =>
            characterA != characterB && PartyIdOf(characterA) == 1 && PartyIdOf(characterB) == 1;

        public long LeaderOf(long partyId) => partyId == 1 ? a : 0;

        public int MembersOf(long partyId, Span<long> into)
        {
            if (partyId != 1 || into.Length < 2)
                return 0;
            into[0] = a;
            into[1] = b;
            return 2;
        }

        public int MemberCount(long partyId) => partyId == 1 ? 2 : 0;
    }
}
