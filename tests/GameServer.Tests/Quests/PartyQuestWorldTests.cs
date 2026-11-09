using GameServer.Entities;
using GameServer.Events;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using static GameServer.Tests.Quests.QuestWorldTests;

namespace GameServer.Tests.Quests;

/// <summary>
/// Runs in a party (spec 5.5 M4 bullet, M4 decision 1; WP-M4-2): the run stays each character's (no shared run,
/// <c>owner_party_id</c> stays null), and a kill by any member counts for each member's active run, a kill step advancing and a
/// return step dropping its quest item into that member's own Quest bag; a member's leave stops it.
/// </summary>
public class PartyQuestWorldTests
{
    private const int Second = 2;

    private static Player JoinSecond(Fixture f)
    {
        var character = f.Harness.Characters.Add(Second, 0, new CharacterState(1, 0, "", 0, 0, 0, 0, 100, 50, 100));
        f.Harness.Characters.Items[character.CharacterId] = [];
        var player = f.Harness.Join(Second);
        f.Harness.Ticks(1);
        return player;
    }

    private static void Accept(Fixture f, int peer, uint epoch)
    {
        f.Harness.SendQuestRun(peer, 50 + (uint)peer, f.NpcId(), QuestRunAction.Accept, epoch, 0, [0]);
        f.Harness.Ticks(1);
    }

    private static void FormParty(Fixture f)
    {
        f.Harness.SendParty(1, 900, PartyAction.Invite, targetName: "c2_0");
        f.Harness.Ticks(1);
        f.Harness.SendParty(Second, 901, PartyAction.Accept, f.Transport.PartyInvitesFor(Second)[^1].InviteId);
        f.Harness.Ticks(1);
    }

    private static int DemonNetId(Fixture f) =>
        f.Harness.World.QuestBoards!.Candidates(f.NpcId()).Single(c => c.Monster.Id == "m1.demon").Monster.NetId;

    [Fact]
    public void One_kill_by_a_member_advances_each_members_own_kill_step()
    {
        var f = new Fixture([Kind(QuestKind.Kill, 3, 3)], party: TestWorlds.Party());
        var second = JoinSecond(f);
        var board = f.OpenBoard();
        Accept(f, 1, board.Epoch);
        f.Harness.SendInteract(Second, 60, f.NpcId(), NpcInteraction.Quests);
        f.Harness.Ticks(1);
        Accept(f, Second, f.Transport.QuestBoardsFor(Second)[^1].Epoch);
        FormParty(f);
        int demon = DemonNetId(f);

        f.Kill(demon); // by the first member

        Assert.Equal(1, f.Runs.Active(f.Player.CharacterId)!.Steps[0].Progress);
        Assert.Equal(1, f.Runs.Active(second.CharacterId)!.Steps[0].Progress);
        Assert.NotEqual(f.Runs.Active(f.Player.CharacterId)!.RunId, f.Runs.Active(second.CharacterId)!.RunId); // two runs

        f.Harness.SendParty(Second, 902, PartyAction.Leave);
        f.Harness.Ticks(1);
        f.Kill(demon);

        Assert.Equal(2, f.Runs.Active(f.Player.CharacterId)!.Steps[0].Progress);
        Assert.Equal(1, f.Runs.Active(second.CharacterId)!.Steps[0].Progress); // no longer a member
    }

    [Fact]
    public void A_return_step_of_a_member_gets_its_quest_item_from_the_others_kill()
    {
        var f = new Fixture([Kind(QuestKind.ReturnItem, 2, 2)], party: TestWorlds.Party());
        var second = JoinSecond(f);
        var board = f.OpenBoard();
        f.Harness.SendInteract(Second, 60, f.NpcId(), NpcInteraction.Quests);
        f.Harness.Ticks(1);
        Accept(f, Second, f.Transport.QuestBoardsFor(Second)[^1].Epoch);
        FormParty(f);
        int sigil = board.Offers[0].ItemNetId;

        f.Kill(DemonNetId(f)); // the first member has no run: the kill still counts for the second's

        var inventory = f.Harness.World.Inventory!;
        Assert.True(inventory.CurrentInventory(second.CharacterId).TryIndex(4, out int questBag));
        var bag = inventory.CurrentInventory(second.CharacterId).BagAt(questBag);
        Assert.Equal(1, Enumerable.Range(0, bag.SlotCount).Where(s => !bag[s].IsEmpty && bag[s].ItemNetId == sigil).Sum(s => bag[s].Count));
        Assert.Null(f.Runs.Active(f.Player.CharacterId));
    }
}
