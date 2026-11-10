using GameServer.Entities;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Sessions;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace GameServer.Tests.Social;

/// <summary>
/// One player command path (spec 9.12; WP-M4-2 part B, PR #170 review): the client reads <c>/invite</c>, <c>/w</c>, <c>/p</c> and
/// <c>/trade</c> (<c>ChatCommandParser</c>) and sends their requests, so a release build, a dev build, a dev account and any
/// other account behave the same. On the server the trade invite is a plain <see cref="TradeRequest"/> with no dev-tools gate;
/// a raw <c>/trade</c> chat line is no longer read by the server and goes the dev console's way like every other <c>/</c> line.
/// </summary>
public class PlayerCommandPathTests
{
    private static WorldHarness Harness()
    {
        var tables = TestCombat.Tables();
        return new WorldHarness(map: TestCombat.OpenMap(TestCombat.MonsterMarker(2_000, 0)), actions: ActionCatalog.From(tables),
            combat: TestCombat.Rules(tables, TestCombat.Point()),
            items: ItemRules.From(tables)!, chat: TestWorlds.Chat(), party: TestWorlds.Party(), trade: TradeSettings.Find(tables)!);
    }

    private static Player JoinAt(WorldHarness harness, int peerId, int xCm)
    {
        harness.Characters.Add(peerId, 0, new CharacterState(1, 0, SessionManager.MainWorldKey, xCm, 0, 0, 0, 100, 10, 100));
        return harness.Join(peerId);
    }

    private static List<T> Read<T>(WorldHarness h, int peerId, ReliableMessageId id) where T : class
    {
        var read = new List<T>();
        foreach (var (peer, _, data) in h.Transport.Sent)
        {
            if (peer != peerId || data[0] != (byte)id)
                continue;
            T? value = null;
            Assert.True(ReliableMessageReader.TryRead(data, id, ref value));
            read.Add(value!);
        }
        return read;
    }

    [Fact]
    public void A_trade_invite_from_a_player_with_no_dev_account_and_no_dev_console_reaches_its_target()
    {
        var h = Harness(); // no dev console in this world: as a release build, or MMORPG_DEV_TOOLS off
        JoinAt(h, 1, 0);
        var target = JoinAt(h, 2, 200);
        h.Ticks(2);

        h.Router.OnDataReceived(1, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.TradeRequest,
            new TradeRequest { RequestId = 1, Action = TradeAction.Invite, TargetEntityId = target.Id }));
        h.Ticks(2);

        var answer = Assert.Single(Read<TradeResult>(h, 1, ReliableMessageId.TradeResult), r => r.RequestId == 1);
        Assert.Equal(TradeReason.None, answer.Reason);
        Assert.Contains(Read<TradeState>(h, 2, ReliableMessageId.TradeState), s => s.Phase == TradePhase.Invited);
    }

    [Fact]
    public void A_raw_trade_chat_line_is_not_read_by_the_server_and_goes_the_dev_console_way()
    {
        var h = Harness();
        JoinAt(h, 1, 0);
        JoinAt(h, 2, 200);
        h.Ticks(2);

        h.SendChat(1, "/trade c2_0");
        h.Ticks(2);

        Assert.Equal(DevReason.DevToolsOff, Assert.Single(Read<DevCommandResult>(h, 1, ReliableMessageId.DevCommandResult)).Reason);
        Assert.Empty(Read<TradeState>(h, 2, ReliableMessageId.TradeState));
        Assert.Empty(h.Transport.ChatMessagesFor(2)); // never broadcast as chat either
    }
}
