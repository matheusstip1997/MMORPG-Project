using GameServer.Admin;
using GameServer.Events;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Protocol;

namespace GameServer.Tests.Admin;

/// <summary>
/// Admin kick on the world tick (spec 7.4, admin panel v0): posted to the world inbox like a login, executed in phase 1
/// by the SessionManager, outcome reported to the admin command worker. A kick is a disconnect: the character lingers
/// (R-2.1-08; user decision 2026-10-26).
/// </summary>
public class AdminKickTests
{
    private const int LingerMs = 10_000;

    [Fact]
    public void Kicking_an_online_character_disconnects_its_client_with_Kicked_and_it_lingers_like_after_a_disconnect()
    {
        var h = new Harness();
        var player = h.World.Join(1, accountId: 3);

        h.Kick(actionId: 11, player.CharacterId);

        Assert.Equal([(11L, AdminOutcome.Kicked)], h.Outcomes());
        Assert.Equal((1, ConnectRejectReason.Kicked), Assert.Single(h.World.Transport.Kicks));
        Assert.Equal(new SessionEnded(3, player.Id, SessionEndReason.Kicked), h.Events[^1]);
        Assert.Contains(player, h.World.World.Entities);
        Assert.False(player.IsConnected);
        Assert.True(h.World.Sessions.IsLingering(player.CharacterId));

        // The client's own disconnect arrives later and changes nothing.
        h.World.Leave(1);
        h.World.Ticks(1);
        Assert.Single(h.Events, e => e is SessionEnded);
    }

    [Fact]
    public void A_kicked_player_can_log_in_again_and_reattaches_to_its_lingering_character()
    {
        var h = new Harness();
        var player = h.World.Join(1, accountId: 3);
        h.Kick(11, player.CharacterId);

        var again = h.World.Join(2, accountId: 3);

        Assert.Same(player, again);
        Assert.Equal(new SessionStarted(3, player.Id, Reattached: true), h.Events[^1]);
    }

    [Fact]
    public void The_kicked_character_is_saved_and_released_when_its_linger_ends()
    {
        var h = new Harness();
        var player = h.World.Join(1, accountId: 3);
        h.Kick(11, player.CharacterId);

        h.World.Clock.Advance(LingerMs);
        h.World.Ticks(3); // logout save, removal, release

        Assert.Empty(h.World.World.Entities);
        Assert.Null(h.World.Locks.HolderOf(player.CharacterId));
        Assert.Equal(0, h.World.Sessions.HeldCount);
    }

    [Fact]
    public void A_lingering_character_is_not_connected_and_an_unknown_one_is_not_online()
    {
        var h = new Harness();
        var player = h.World.Join(1, accountId: 3);
        h.World.Leave(1);
        h.World.Ticks(1);

        h.Kick(11, player.CharacterId);
        h.Kick(12, characterId: 999_999);

        Assert.Equal([(11L, AdminOutcome.NotConnected), (12L, AdminOutcome.NotOnline)], h.Outcomes());
        Assert.Empty(h.World.Transport.Kicks);
        Assert.True(h.World.Sessions.IsLingering(player.CharacterId));
    }

    [Fact]
    public void Two_kicks_in_one_tick_kick_once()
    {
        var h = new Harness();
        var player = h.World.Join(1, accountId: 3);

        h.World.Inbox.Post(WorldCommand.AdminKick(11, player.CharacterId, h.Results));
        h.World.Inbox.Post(WorldCommand.AdminKick(12, player.CharacterId, h.Results));
        h.World.Ticks(1);

        Assert.Equal([(11L, AdminOutcome.Kicked), (12L, AdminOutcome.NotConnected)], h.Outcomes());
        Assert.Single(h.World.Transport.Kicks);
    }

    private sealed class Harness : IEventConsumer<SessionStarted>, IEventConsumer<SessionEnded>
    {
        public Harness()
        {
            var bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            bus.Subscribe<SessionStarted>(this);
            bus.Subscribe<SessionEnded>(this);
            World = new WorldHarness(events: new EventDispatchHook(bus), bus: bus,
                values: TestSessions.Values(lingerOutOfCombatMs: LingerMs));
        }

        public WorldHarness World { get; }

        public AdminCommandResults Results { get; } = new();

        public List<IGameEvent> Events { get; } = [];

        /// <summary>Posts a kick as the admin command worker does and runs the tick that executes it.</summary>
        public void Kick(long actionId, long characterId)
        {
            World.Inbox.Post(WorldCommand.AdminKick(actionId, characterId, Results));
            World.Ticks(1);
        }

        public List<(long ActionId, AdminOutcome Outcome)> Outcomes()
        {
            var list = new List<(long, AdminOutcome)>();
            while (Results.TryTake(out var result))
                list.Add(result);
            return list;
        }

        public void Handle(in EventEnvelope envelope, in SessionStarted payload) => Events.Add(payload);

        public void Handle(in EventEnvelope envelope, in SessionEnded payload) => Events.Add(payload);
    }
}
