using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Quests;
using GameServer.Tests.Economy;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Tests.Quests;

/// <summary>
/// Outpost quests and runs in a world (R-5.5-01..03, spec 5.5; WP-M2-6) with the repository tables, the board narrowed by the
/// test to the kinds it needs: the board at the outpost behind the registry's gate, accept with locked rewards and the caps, kill
/// progress from <see cref="Killed"/> (idempotent), deliveries through <see cref="InventoryService"/> in the same save as the step
/// progress, the 100 % quest drop into the Quest bag, forfeit with the penalty, and the claim paid through <c>ExpService</c> and
/// answered only once its save committed (rule 5).
/// </summary>
public class QuestWorldTests
{
    private const string Outpost = "test.npc.outpost";
    private const string FarOutpost = "test.npc.far";
    private const int Peer = 1;

    internal sealed class Fixture
    {
        public Fixture(OutpostQuestDef[]? kinds = null, IReadOnlyList<ItemStackRow>? items = null)
        {
            var tables = TestCombat.Tables();
            if (kinds != null)
                EconomyFixture.Replace(tables, kinds);
            Quests = QuestRules.From(tables)!;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<QuestStepDone>(Log);
            Bus.Subscribe<QuestForfeited>(Log);
            Bus.Subscribe<RunClaimed>(Log);
            Bus.Subscribe<ItemDelivered>(Log);
            Bus.Subscribe<ExpGained>(Log);
            var map = TestMaps.Map(TestMaps.Open().Grid,
            [
                TestMaps.SpawnMarker(0, 0), TestCombat.MonsterMarker(2_000, 0), EconomyFixture.Npc(Outpost, 200, 0, "m1.outpost"),
                EconomyFixture.Npc(FarOutpost, 5_000, 5_000, "m1.outpost"),
            ]);
            Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus,
                combat: TestCombat.Rules(tables, TestCombat.Point()), items: ItemRules.From(tables)!,
                economy: EconomyRules.From(tables)!, progression: ProgressionRules.From(tables)!, quests: Quests, rngSeed: 7);
            var character = Harness.Characters.Add(1, 0, new CharacterState(1, 0, "", 0, 0, 0, 0, 100, 50, 100));
            Harness.Characters.Items[character.CharacterId] = items ?? [];
            Player = Harness.Join(Peer);
            Harness.Ticks(1);
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public QuestRules Quests { get; }
        public EventLog Log { get; } = new();
        public Player Player { get; }
        public FakeTransport Transport => Harness.Transport;
        public RunStore Runs => Harness.World.Runs!;
        public ProgressionStore Progression => Harness.World.Progression!;

        public uint NpcId(string markerId = Outpost) =>
            Harness.World.Entities.OfType<Npc>().Single(n => n.MarkerId == markerId).Id;

        public QuestBoard OpenBoard(uint requestId = 100)
        {
            Harness.SendInteract(Peer, requestId, NpcId(), NpcInteraction.Quests);
            Harness.Ticks(1);
            return Transport.QuestBoardsFor(Peer)[^1];
        }

        public QuestRunResult Request(uint requestId, QuestRunAction action, uint epoch = 0, byte step = 0, string npc = Outpost,
            params byte[] slots)
        {
            Harness.SendQuestRun(Peer, requestId, NpcId(npc), action, epoch, step, slots);
            Harness.Ticks(1);
            return Transport.QuestResultsFor(Peer).Last(r => r.RequestId == requestId);
        }

        public EventEnvelope Kill(int monsterNetId)
        {
            var envelope = Bus.Publish(new Killed(Player.Id, 99, 0, monsterNetId, 0, 0), Player.CharacterId);
            Harness.Ticks(2); // dispatch, then phase 10
            return envelope;
        }

        /// <summary>The next class 2 batch (every second) and its completion.</summary>
        public void Save()
        {
            Harness.Clock.Advance(1_000);
            Harness.Ticks(2);
        }

        public RunStatus Status => Transport.RunStatusFor(Peer)[^1];
    }

    internal sealed class EventLog : IEventConsumer<QuestStepDone>, IEventConsumer<QuestForfeited>, IEventConsumer<RunClaimed>,
        IEventConsumer<ItemDelivered>, IEventConsumer<ExpGained>
    {
        public List<object> Events { get; } = [];
        public void Handle(in EventEnvelope envelope, in QuestStepDone payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in QuestForfeited payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in RunClaimed payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ItemDelivered payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ExpGained payload) => Events.Add(payload);
    }

    internal static OutpostQuestDef Kind(QuestKind kind, int min = 1, int max = 2, int perUnit = 100, bool scaled = false) => new()
    {
        Id = kind.ToString(), Kind = kind, Weight = 10, CountMin = min, CountMax = max, ExpPercentPerUnit = perUnit, DemandScaled = scaled,
    };

    private static int DemonNetId(Fixture f) =>
        f.Harness.World.QuestBoards!.Candidates(f.NpcId()).Single(c => c.Monster.Id == "m1.demon").Monster.NetId;

    [Fact]
    public void The_board_opens_at_the_outpost_with_six_offers_of_its_zone_and_stays_the_same_within_its_epoch()
    {
        var f = new Fixture();

        var board = f.OpenBoard(1);
        var answer = Assert.Single(f.Transport.InteractResultsFor(Peer));
        Assert.Equal((NpcInteraction.Quests, OutpostReason.None), (answer.Kind, answer.Reason));
        Assert.Equal(6, board.Offers.Length);
        Assert.All(board.Offers, o => Assert.Equal(DemonNetId(f), o.MonsterNetId)); // only the demon spawns in the zone
        Assert.Equal(f.NpcId(), board.NpcEntityId);

        f.Harness.Clock.Advance(1_000);
        var again = f.OpenBoard(2);
        Assert.Equal(board.Epoch, again.Epoch);
        Assert.Equal(board.Offers.Select(o => (o.Kind, o.Count, o.RewardExp)), again.Offers.Select(o => (o.Kind, o.Count, o.RewardExp)));
    }

    [Fact]
    public void A_board_or_run_request_beyond_the_npc_range_is_refused_with_OutOfRange()
    {
        var f = new Fixture();
        f.Harness.SendInteract(Peer, 1, f.NpcId(FarOutpost), NpcInteraction.Quests);
        f.Harness.Ticks(1);

        Assert.Equal(OutpostReason.OutOfRange, Assert.Single(f.Transport.InteractResultsFor(Peer)).Reason);
        Assert.Empty(f.Transport.QuestBoardsFor(Peer));
        Assert.Equal(OutpostReason.OutOfRange, f.Request(2, QuestRunAction.Claim, npc: FarOutpost).Reason);
    }

    [Fact]
    public void Accepting_two_offers_locks_their_rewards_into_a_two_step_run()
    {
        var f = new Fixture([Kind(QuestKind.Kill, 2, 4)]);
        var board = f.OpenBoard();

        var result = f.Request(1, QuestRunAction.Accept, board.Epoch, slots: [3, 1]);

        Assert.Equal(OutpostReason.None, result.Reason);
        var status = f.Status;
        Assert.Equal(RunState.Active, status.State);
        Assert.Equal(new[] { (board.Offers[3].Count, board.Offers[3].RewardExp), (board.Offers[1].Count, board.Offers[1].RewardExp) },
            status.Steps.Select(s => ((ushort)s.Required, s.RewardExp)));
        Assert.All(status.Steps, s => Assert.Equal((RunStepState.Active, (ushort)0), (s.State, s.Progress)));
    }

    [Fact]
    public void Accept_refusals_name_their_reason_and_change_nothing()
    {
        var f = new Fixture();
        var board = f.OpenBoard();

        Assert.Equal(OutpostReason.TooManySteps, f.Request(1, QuestRunAction.Accept, board.Epoch).Reason);
        Assert.Equal(OutpostReason.TooManySteps, f.Request(2, QuestRunAction.Accept, board.Epoch, slots: [0, 1, 2, 3, 4, 5]).Reason);
        Assert.Equal(OutpostReason.TooManySteps, f.Request(3, QuestRunAction.Accept, board.Epoch, slots: [2, 2]).Reason);
        Assert.Equal(OutpostReason.OfferNotFound, f.Request(4, QuestRunAction.Accept, board.Epoch, slots: [6]).Reason);
        int boards = f.Transport.QuestBoardsFor(Peer).Count;
        Assert.Equal(OutpostReason.BoardExpired, f.Request(5, QuestRunAction.Accept, board.Epoch + 1, slots: [0]).Reason);
        Assert.Equal(boards + 1, f.Transport.QuestBoardsFor(Peer).Count); // the current board comes with the refusal
        Assert.Null(f.Runs.Active(f.Player.CharacterId));

        Assert.Equal(OutpostReason.None, f.Request(6, QuestRunAction.Accept, board.Epoch, slots: [0]).Reason);
        Assert.Equal(OutpostReason.RunAlreadyActive, f.Request(7, QuestRunAction.Accept, board.Epoch, slots: [1]).Reason);
    }

    [Fact]
    public void A_board_refreshed_after_its_epoch_refuses_an_accept_of_the_old_one()
    {
        var f = new Fixture();
        var board = f.OpenBoard();
        for (int ms = 0; ms <= board.RefreshInMs; ms += 1_000) // the session's heartbeats keep the character's lock meanwhile
        {
            f.Harness.Clock.Advance(1_000);
            f.Harness.Ticks(1);
        }

        Assert.Equal(OutpostReason.BoardExpired, f.Request(1, QuestRunAction.Accept, board.Epoch, slots: [0]).Reason);
        var fresh = f.Transport.QuestBoardsFor(Peer)[^1];
        Assert.Equal(board.Epoch + 1, fresh.Epoch);
        Assert.Equal(OutpostReason.None, f.Request(2, QuestRunAction.Accept, fresh.Epoch, slots: [0]).Reason);
    }

    [Fact]
    public void Each_kill_advances_one_kill_step_once_per_event_and_the_kills_complete_the_run()
    {
        var f = new Fixture([Kind(QuestKind.Kill, 2, 2)]);
        var board = f.OpenBoard();
        f.Request(1, QuestRunAction.Accept, board.Epoch, slots: [0, 1]);
        int demon = DemonNetId(f);

        var first = f.Kill(demon);
        // A re-dispatched kill (replay after a consumer fault) counts nothing more.
        f.Harness.World.Quests!.Handle(first, new Killed(f.Player.Id, 99, 0, demon, 0, 0));
        // One kill counts once: the first active kill step of the type, in run order.
        Assert.Equal(new[] { 1, 0 }, f.Runs.Active(f.Player.CharacterId)!.Steps.Select(s => s.Progress));
        f.Kill(demon + 1000); // another type: nothing

        f.Kill(demon);
        Assert.Equal(new[] { 2, 0 }, f.Runs.Active(f.Player.CharacterId)!.Steps.Select(s => s.Progress));
        Assert.Single(f.Log.Events.OfType<QuestStepDone>());
        f.Kill(demon);
        f.Kill(demon);

        Assert.Equal(RunState.Completed, f.Status.State);
        Assert.Equal(2, f.Log.Events.OfType<QuestStepDone>().Count());
        Assert.All(f.Status.Steps, s => Assert.Equal((RunStepState.Done, (ushort)2), (s.State, s.Progress)));
    }

    [Fact]
    public void A_claim_pays_the_step_bonus_through_ExpService_and_is_answered_only_after_its_save_commits()
    {
        var f = new Fixture([Kind(QuestKind.Kill, 1, 1)]);
        var board = f.OpenBoard();
        f.Request(1, QuestRunAction.Accept, board.Epoch, slots: [0, 1]);
        Assert.Equal(OutpostReason.RunNotComplete, f.Request(2, QuestRunAction.Claim).Reason);
        f.Kill(DemonNetId(f));
        Assert.Equal(OutpostReason.RunNotComplete, f.Request(9, QuestRunAction.Claim).Reason); // one step done, one active
        f.Kill(DemonNetId(f));
        long expBefore = f.Progression.Snapshot(f.Player.CharacterId).Exp;
        long steps = board.Offers[0].RewardExp + board.Offers[1].RewardExp;
        long expected = steps * 110 / 100; // two completed steps: +10 % (RunBonus)
        Assert.Equal(expected, f.Status.ClaimExp);
        f.Harness.Saves.AutoComplete = false;

        f.Harness.SendQuestRun(Peer, 3, f.NpcId(), QuestRunAction.Claim);
        f.Harness.Ticks(2); // the claim's save is issued, not completed

        Assert.DoesNotContain(f.Transport.QuestResultsFor(Peer), r => r.RequestId == 3);
        Assert.Equal(expBefore + expected, f.Progression.Snapshot(f.Player.CharacterId).Exp);
        var job = f.Harness.Saves.Jobs[^1];
        Assert.Equal(SaveReason.RunClaim, job.Reason);
        var claimAudit = Assert.Single(job.Quests!.Audit, a => a.Action == QuestRunAction.Claim);
        Assert.Equal(expected, claimAudit.Amount);
        Assert.Equal(RunState.Claimed, Assert.Single(job.Quests.Runs).State);
        f.Harness.SendQuestRun(Peer, 3, f.NpcId(), QuestRunAction.Claim); // double submit in flight: no answer, nothing twice
        f.Harness.Ticks(1);
        Assert.Equal(expBefore + expected, f.Progression.Snapshot(f.Player.CharacterId).Exp);

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        var answer = Assert.Single(f.Transport.QuestResultsFor(Peer), r => r.RequestId == 3);
        Assert.Equal((OutpostReason.None, expected), (answer.Reason, answer.Exp));
        var claimed = Assert.Single(f.Log.Events.OfType<RunClaimed>());
        Assert.Equal((2, 0, 10, 0, expected), (claimed.CompletedSteps, claimed.ForfeitedSteps, claimed.BonusPercent,
            claimed.PenaltyPercent, claimed.Exp));
        Assert.Equal(1, f.Progression.CounterValue(f.Player.CharacterId,
            f.Progression.Rules.Counter(AchievementCategory.RunsClaimed)!));
        Assert.Equal(RunState.None, f.Status.State);

        f.Request(3, QuestRunAction.Claim); // the same request after the commit: the first answer again
        Assert.Equal(2, f.Transport.QuestResultsFor(Peer).Count(r => r.RequestId == 3 && r.Exp == expected));
        Assert.Equal(expBefore + expected, f.Progression.Snapshot(f.Player.CharacterId).Exp);
        Assert.Single(f.Harness.Characters.RunAudit, a => a.Action == QuestRunAction.Claim);
    }

    [Fact]
    public void A_forfeited_step_pays_nothing_and_takes_twenty_percent_off_the_run()
    {
        var f = new Fixture([Kind(QuestKind.Kill, 1, 1)]);
        var board = f.OpenBoard();
        f.Request(1, QuestRunAction.Accept, board.Epoch, slots: [0, 1, 2]);

        Assert.Equal(OutpostReason.None, f.Request(2, QuestRunAction.Forfeit, step: 2).Reason);
        Assert.Equal(OutpostReason.StepNotActive, f.Request(3, QuestRunAction.Forfeit, step: 2).Reason);
        Assert.Equal(OutpostReason.StepNotFound, f.Request(4, QuestRunAction.Forfeit, step: 7).Reason);
        f.Kill(DemonNetId(f));
        f.Kill(DemonNetId(f));

        long done = board.Offers[0].RewardExp + board.Offers[1].RewardExp;
        Assert.Equal(RunState.Completed, f.Status.State);
        Assert.Equal((10, 20), ((int)f.Status.BonusPercent, (int)f.Status.PenaltyPercent));
        Assert.Equal(done * 110 * 80 / 10_000, f.Status.ClaimExp);
        Assert.Equal(20, Assert.Single(f.Log.Events.OfType<QuestForfeited>()).PenaltyPercent);
    }

    [Fact]
    public void Forfeiting_every_step_abandons_the_run_and_frees_the_character_for_a_new_one()
    {
        var f = new Fixture([Kind(QuestKind.Kill, 1, 1)]);
        var board = f.OpenBoard();
        f.Request(1, QuestRunAction.Accept, board.Epoch, slots: [0]);

        f.Request(2, QuestRunAction.Forfeit, step: 0);

        Assert.Equal(RunState.None, f.Status.State);
        Assert.Equal(OutpostReason.NoActiveRun, f.Request(3, QuestRunAction.Claim).Reason);
        f.Save();
        Assert.Equal(RunState.Abandoned, Assert.Single(f.Harness.Characters.Runs.Values).State);
        Assert.Equal(OutpostReason.None, f.Request(4, QuestRunAction.Accept, board.Epoch, slots: [1]).Reason);
    }

    [Fact]
    public void A_delivery_removes_the_items_through_InventoryService_in_the_same_save_as_the_step()
    {
        var f = new Fixture([Kind(QuestKind.Deliver, 5, 5)],
            [new ItemStackRow(1, 0, "m1.demonHorn", 0, false, 3), new ItemStackRow(1, 4, "m1.demonHorn", 0, false, 4),
             new ItemStackRow(1, 2, "m1.demonAsh", 0, false, 9)]);
        var board = f.OpenBoard();
        int slot = Array.FindIndex(board.Offers, o => o.ItemNetId == ItemNetId(f, "m1.demonHorn"));
        Assert.True(slot >= 0, "the demon's horn is one of its deliverable drops");
        f.Request(1, QuestRunAction.Accept, board.Epoch, slots: [(byte)slot]);

        var result = f.Request(2, QuestRunAction.Deliver, step: 0);

        Assert.Equal((OutpostReason.None, (ushort)5), (result.Reason, result.Count));
        var bag = f.Harness.World.Inventory!.Current(f.Player.CharacterId);
        Assert.Equal(new (int, int)[] { (2, 9), (4, 2) },
            Enumerable.Range(0, bag.SlotCount).Where(s => !bag[s].IsEmpty).Select(s => (s, (int)bag[s].Count)));
        Assert.Equal(RunState.Completed, f.Status.State);
        var delivered = Assert.Single(f.Log.Events.OfType<ItemDelivered>());
        Assert.Equal(5, delivered.Count);
        Assert.Single(f.Log.Events.OfType<QuestStepDone>());

        f.Save();
        var job = f.Harness.Saves.Jobs.Single(j => j.Quests != null && j.Quests.Audit.Any(a => a.Action == QuestRunAction.Deliver));
        Assert.NotNull(job.Inventory); // the removal rides the same save as the progress
        Assert.All(job.Inventory!.Ops, op => Assert.Equal(InventoryReason.QuestDelivery.ToString(), op.Reason.ToString()));
        Assert.Equal(OutpostReason.StepNotActive, f.Request(3, QuestRunAction.Deliver, step: 0).Reason);
    }

    [Fact]
    public void A_delivery_with_nothing_to_hand_in_or_for_a_kill_step_is_refused()
    {
        var f = new Fixture([Kind(QuestKind.Deliver, 2, 2), Kind(QuestKind.Kill, 1, 1)]);
        var board = f.OpenBoard();
        byte deliver = (byte)Array.FindIndex(board.Offers, o => o.Kind == QuestKind.Deliver);
        byte kill = (byte)Array.FindIndex(board.Offers, o => o.Kind == QuestKind.Kill);
        f.Request(1, QuestRunAction.Accept, board.Epoch, slots: [deliver, kill]);

        Assert.Equal(OutpostReason.NothingToDeliver, f.Request(2, QuestRunAction.Deliver, step: 0).Reason);
        Assert.Equal(OutpostReason.StepNotActive, f.Request(3, QuestRunAction.Deliver, step: 1).Reason);
        Assert.Equal(OutpostReason.StepNotFound, f.Request(4, QuestRunAction.Deliver, step: 2).Reason);
    }

    [Fact]
    public void A_return_step_gets_one_quest_item_per_kill_in_the_Quest_bag_until_it_has_enough_then_delivers_them()
    {
        var f = new Fixture([Kind(QuestKind.ReturnItem, 2, 2)]);
        var board = f.OpenBoard();
        f.Request(1, QuestRunAction.Accept, board.Epoch, slots: [0]);
        int sigil = ItemNetId(f, "m2.questDemonSigil");
        Assert.Equal(sigil, board.Offers[0].ItemNetId);
        int demon = DemonNetId(f);

        f.Kill(demon);
        f.Kill(demon);
        f.Kill(demon); // the bag already holds what the step needs: no third item

        var inventory = f.Harness.World.Inventory!;
        Assert.True(inventory.CurrentInventory(f.Player.CharacterId).TryIndex(4, out int questBag));
        var bag = inventory.CurrentInventory(f.Player.CharacterId).BagAt(questBag);
        Assert.Equal(2, Enumerable.Range(0, bag.SlotCount).Where(s => bag[s].ItemNetId == sigil).Sum(s => bag[s].Count));

        var result = f.Request(2, QuestRunAction.Deliver, step: 0);
        Assert.Equal((OutpostReason.None, (ushort)2), (result.Reason, result.Count));
        Assert.Equal(0, Enumerable.Range(0, bag.SlotCount).Where(s => !bag[s].IsEmpty && bag[s].ItemNetId == sigil).Sum(s => bag[s].Count));
        Assert.Equal(RunState.Completed, f.Status.State);
    }

    [Fact]
    public async Task An_open_run_survives_a_relogin()
    {
        var f = new Fixture([Kind(QuestKind.Kill, 3, 3)]);
        var board = f.OpenBoard();
        f.Request(1, QuestRunAction.Accept, board.Epoch, slots: [0]);
        f.Kill(DemonNetId(f));
        f.Save();

        var run = Assert.Single(f.Harness.Characters.Runs.Values);
        Assert.Equal((RunState.Active, 1), (run.State, run.Steps[0].Progress));
        f.Harness.World.Runs!.Detach(f.Player.CharacterId);
        f.Harness.World.Runs.Attach(f.Player.CharacterId, await f.Harness.Characters.LoadRunsAsync(f.Player.CharacterId));
        var again = f.Runs.Active(f.Player.CharacterId)!;
        Assert.Equal((run.RunId, 1, DemonNetId(f)), (again.RunId, again.Steps[0].Progress, again.Steps[0].MonsterNetId));
    }

    private static int ItemNetId(Fixture f, string id) =>
        f.Harness.World.Inventory!.Rules.TryGetItem(id, out var item) ? item.NetId : 0;
}
