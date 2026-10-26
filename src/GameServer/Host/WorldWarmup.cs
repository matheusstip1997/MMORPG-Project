using GameServer.Entities;
using GameServer.Net;
using GameServer.World;
using GameServer.World.Map;
using Shared.Protocol;
using Shared.Util;

namespace GameServer.Host;

/// <summary>
/// Runs the per-player paths once before the real world starts (QA wave 5 G3, rule 12): a throwaway world on the same
/// tables and map, where one player of each class enters, moves and attacks for a few snapshot intervals, then the world
/// is dropped. The first player of a cold process otherwise pays the JIT of spawning, the combatant, interest,
/// snapshots, the identity and inventory messages (MemoryPack) and the action machine inside one tick: about 21 ms of
/// the 50 ms budget, measured. Nothing leaves the process (a transport that sends nothing), no session, event bus,
/// telemetry or database is attached, and the real world's state, ids and random streams are untouched.
/// </summary>
public static class WorldWarmup
{
    /// <summary>Snapshot intervals the warm-up runs; enough for spawn, interest, snapshots and an attack to run through.</summary>
    private const int SnapshotIntervals = 4;

    /// <summary>Peer and character ids of the warm-up players; they exist only in the throwaway world.</summary>
    private const int FirstPeerId = 1;

    /// <returns>How many players entered (one per class).</returns>
    public static int Run(ServerConfig config, WorldMap map, IClock clock)
    {
        var settings = WorldSettings.From(config.StartingValues, config.ServerLoop, config.NetSecurity);
        var transport = new SilentTransport();
        var world = new GameWorld(settings, map, new WorldInbox(), transport, actions: config.Actions, combat: config.Combat,
            clock: clock, projectiles: config.Projectiles, items: config.Items, runId: "warmup", chat: config.Chat);

        var players = new List<(Player Player, InputQueue Inputs)>();
        var classes = config.Combat?.Classes ?? [];
        int count = Math.Max(1, classes.Count);
        var (movement, heightCm) = world.DefaultSpawn();
        for (int i = 0; i < count; i++)
        {
            long characterId = i + 1;
            var classDef = i < classes.Count ? classes[i] : null;
            world.Inventory?.Attach(characterId, Array.Empty<Persistence.ItemStackRow>());
            var player = world.SpawnPlayer(accountId: characterId, characterId, movement, heightCm, classDef, name: "warmup");
            var inputs = new InputQueue(config.ServerLoop.MaxQueuedInputFrames);
            world.BindPeer(player, FirstPeerId + i, inputs);
            players.Add((player, inputs));
        }

        int ticks = SnapshotIntervals * Math.Max(1, settings.SnapshotIntervalTicks);
        for (int t = 0; t < ticks; t++)
        {
            ushort sequence = (ushort)(t + 1);
            foreach (var (_, inputs) in players)
            {
                // Walk, and press the normal attack every other tick (a press is a change of the held buttons).
                inputs.Enqueue(new InputFrame
                {
                    Sequence = sequence,
                    ClientTick = (uint)sequence,
                    Keys = Shared.Protocol.MoveKeys.W,
                    Actions = t % 2 == 0 ? InputActions.Attack : InputActions.None,
                });
            }
            world.Tick();
        }

        foreach (var (player, _) in players)
            world.RemovePlayer(player);
        world.Tick();
        return players.Count;
    }

    /// <summary>Encodes like the real transport's callers, sends nothing.</summary>
    private sealed class SilentTransport : ITransport
    {
        public int LocalPort => 0;

        public int ConnectedPeers => 0;

        /// <summary>No peer limit: the senders write up to their own buffers, as for a peer with a large MTU.</summary>
        public int MaxUnreliableBytes(int peerId) => int.MaxValue;

        public long UnreliableTooLarge => 0;

        public TooLargeSend LastUnreliableTooLarge => default;

        public void Start(int port)
        {
        }

        public void Stop()
        {
        }

        public void PollEvents()
        {
        }

        public void Send(int peerId, Channel channel, ReadOnlySpan<byte> data)
        {
        }

        public void Disconnect(int peerId, ConnectRejectReason reason = ConnectRejectReason.None)
        {
        }

        public void Dispose()
        {
        }
    }
}
