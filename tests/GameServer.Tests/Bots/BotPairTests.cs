using BotClient;
using BotClient.Brain;
using BotClient.LoadTest;
using BotClient.Loop;
using BotClient.Loop.Steps;
using BotClient.Run;
using BotClient.World;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Util;

namespace GameServer.Tests.Bots;

/// <summary>
/// WP-M4-12: bots in pairs (spec 11.5; contract in src/Tools/BotClient/Loop/Steps/README.md, "Pairs"): the partner's
/// resolution, the meeting primitive on the pair board, the meet and serve steps, the M4 placeholders and scripts, the rows.
/// </summary>
public class BotPairTests
{
    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    /// <summary>The M4 step ids (WP-M4-12) of <see cref="BotBehaviour.LoopStepIds"/>.</summary>
    internal static readonly string[] M4StepIds = ["meet", "serve", "party", "whisper", "trade", "mail", "list", "stall", "offline", "die", "recoverBag", "contract"];

    // ---- the pair board and its meetings --------------------------------------------------------------------------------

    [Fact]
    public void Two_sides_meet_in_either_order_and_again_at_the_next_occurrence()
    {
        var (a, b) = Pair();
        int first = a.Arrive("trade");
        Assert.Equal(1, first);
        Assert.Equal(MeetState.Waiting, a.Check("trade", first));
        int other = b.Arrive("trade");
        Assert.Equal(MeetState.Met, b.Check("trade", other));
        Assert.Equal(MeetState.Met, a.Check("trade", first));

        // The next round meets at occurrence 2; the first meeting does not count for it.
        int second = b.Arrive("trade");
        Assert.Equal(2, second);
        Assert.Equal(MeetState.Waiting, b.Check("trade", second));
        Assert.Equal(MeetState.Met, b.Check("trade", 1));
        a.Arrive("trade");
        Assert.Equal(MeetState.Met, b.Check("trade", second));
        // Another point is its own count.
        Assert.Equal(1, a.Arrive("party"));
    }

    [Fact]
    public void A_side_that_gives_up_or_leaves_stops_the_partner_waiting()
    {
        var (a, b) = Pair();
        int mine = a.Arrive("party");
        a.GiveUpOpen(); // a's step ended without meeting
        int theirs = b.Arrive("party");
        Assert.Equal(MeetState.PartnerGaveUp, b.Check("party", theirs));
        a.GiveUpOpen(); // nothing open any more: no second give-up
        Assert.Equal(MeetState.PartnerGaveUp, b.Check("party", theirs));

        // A met meeting is not given up afterwards.
        int next = a.Arrive("meet");
        b.Arrive("meet");
        Assert.Equal(MeetState.Met, a.Check("meet", next));
        a.GiveUpOpen();
        Assert.False(b.Board.TryReadPartner("gaveup.meet.1", out _));

        // Gone: the partner's script finished before it arrived.
        int late = b.Arrive("contract");
        a.SayGone();
        Assert.Equal(MeetState.PartnerGone, b.Check("contract", late));
        Assert.True(b.PartnerGone);
        _ = mine;
    }

    [Fact]
    public void A_step_that_ends_before_it_arrives_still_advances_its_occurrence_so_the_next_round_meets()
    {
        // Review finding 1: round 1, A arrives at trade 1 while B's step times out on the walk; round 2 must meet at 2.
        var (a, b) = Pair();
        int a1 = a.Open("trade");
        a.Arrive("trade", a1);
        int b1 = b.Open("trade"); // B's meet step began, then timed out while walking
        Assert.Equal((1, 1), (a1, b1));
        b.GiveUpOpen();
        Assert.Equal(MeetState.PartnerGaveUp, a.Check("trade", a1));
        a.GiveUpOpen(); // A's step failed on the give-up

        int a2 = a.Open("trade");
        a.Arrive("trade", a2);
        int b2 = b.Open("trade");
        b.Arrive("trade", b2);
        Assert.Equal((2, 2), (a2, b2));
        Assert.Equal(MeetState.Met, b.Check("trade", b2)); // A's give-up of 1 does not touch 2
        Assert.Equal(MeetState.Met, a.Check("trade", a2));
        // Every later round meets too.
        int a3 = a.Arrive("trade"), b3 = b.Arrive("trade");
        Assert.Equal(MeetState.Met, a.Check("trade", a3));
        Assert.Equal(MeetState.Met, b.Check("trade", b3));
    }

    [Fact]
    public void A_step_that_times_out_after_both_arrived_counts_the_meeting_as_met()
    {
        // Review finding 3: B saw Met; A's step ended (its timeout) before its last Check: no give-up for that occurrence.
        var (a, b) = Pair();
        int n = a.Arrive("party");
        int m = b.Arrive("party");
        Assert.Equal(MeetState.Met, b.Check("party", m));
        a.GiveUpOpen();
        Assert.False(b.Board.TryReadPartner("gaveup.party.1", out _));
        Assert.Equal(MeetState.Met, a.Check("party", n));
        Assert.Equal(MeetState.Met, b.Check("party", m));
    }

    [Fact]
    public void A_given_up_occurrence_is_never_met_afterwards()
    {
        // Review finding 4: the Unity smoke gives up after each step; a later Check of that occurrence must not say Met.
        var (a, b) = Pair();
        int n = a.Arrive("join");
        a.GiveUpOpen();
        Assert.Equal(MeetState.GivenUp, a.Check("join", n));
        b.Arrive("join"); // the partner arrives late
        Assert.Equal(MeetState.GivenUp, a.Check("join", n));
        Assert.Equal(MeetState.PartnerGaveUp, b.Check("join", 1));
    }

    [Fact]
    public void Names_positions_data_and_script_requests_cross_the_board()
    {
        var (a, b) = Pair();
        Assert.Null(b.PartnerName);
        a.SayName("Botalpha");
        Assert.Equal("Botalpha", b.PartnerName);
        a.PostPosition(1_000, -2_000);
        Assert.True(b.TryPartnerPosition(out int x, out int z));
        Assert.Equal((1_000, -2_000), (x, z));
        a.PostPosition(1_100, -2_000, minMoveCm: 200); // moved 100 cm: not posted again
        Assert.True(b.TryPartnerPosition(out x, out _));
        Assert.Equal(1_000, x);
        a.PostPosition(1_300, -2_000, minMoveCm: 200);
        Assert.True(b.TryPartnerPosition(out x, out _));
        Assert.Equal(1_300, x);

        a.Post("trade.offer", "m1.demonAsh");
        Assert.True(b.TryRead("trade.offer", out string offer));
        Assert.Equal("m1.demonAsh", offer);
        Assert.False(a.TryRead("trade.offer", out _)); // a side reads only its partner's posts

        int request = a.Request("join");
        Assert.True(b.TryReadRequest(request, out string script));
        Assert.Equal("join", script);
        b.Answer(request, "done; meet:join done");
        Assert.True(a.TryReadResult(request, out string result));
        Assert.Equal(PairScriptResult.Done, PairScriptResult.Outcome(result));
        Assert.Throws<ArgumentException>(() => a.Post("bad\tkey", "x"));
        Assert.Throws<ArgumentException>(() => a.Post("key", "two\nlines"));
        Assert.Equal("a b c", PairLink.Clean("a\tb\nc"));
    }

    [Fact]
    public void The_file_board_reads_the_other_side_incrementally_and_whole_lines_only()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pair-board-" + Guid.NewGuid().ToString("N"));
        try
        {
            var player = new PairLink(new FilePairBoard(dir, FilePairBoard.PlayerSide, FilePairBoard.BotSide, refreshIntervalMs: 0), isFirst: true);
            var bot = new PairLink(new FilePairBoard(dir, FilePairBoard.BotSide, FilePairBoard.PlayerSide, refreshIntervalMs: 0), isFirst: false);
            Assert.Null(bot.PartnerName); // no player.log yet
            player.SayName("Qaplayer");
            Assert.Equal("Qaplayer", bot.PartnerName);
            // A line still being written (no newline yet) waits for the next read; the newest value of a key wins.
            File.AppendAllText(Path.Combine(dir, "player.log"), "name\tQaRenamed");
            Assert.Equal("Qaplayer", bot.PartnerName);
            File.AppendAllText(Path.Combine(dir, "player.log"), "\n");
            Assert.Equal("QaRenamed", bot.PartnerName);
            int occurrence = bot.Arrive("join");
            player.Arrive("join");
            Assert.Equal(MeetState.Met, bot.Check("join", occurrence));
            Assert.Contains("meet.join\t1", File.ReadAllText(Path.Combine(dir, "bot.log")), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    // ---- the partner's entity -------------------------------------------------------------------------------------------

    [Fact]
    public void The_partner_is_found_by_its_display_name_in_the_snapshots()
    {
        var (a, b) = Pair();
        var bot = new PairBot(a);
        Assert.False(bot.Context.TryPartnerEntity(out _, out _)); // no name posted yet
        b.SayName("Botbeta");
        Assert.False(bot.Context.TryPartnerEntity(out _, out _)); // no EntityIdentity yet
        bot.Context.Identities.Set(PairBot.PartnerId, "Botbeta");
        Assert.False(bot.Context.TryPartnerEntity(out _, out _)); // not in the snapshots yet
        bot.PlayersInSnapshot(partnerOffsetCm: 300);
        Assert.True(bot.Context.TryPartnerEntity(out uint id, out var at));
        Assert.Equal(PairBot.PartnerId, id);
        Assert.Equal(GridPath.Cm(bot.Context.View.Self.X) + 300, GridPath.Cm(at.X), 1);
        // A stale entity of the name (the partner logged out and in again; its old, lower id is out of view): the one in view
        // is the partner. FindInView and TryFind share one rule, the lowest entity id and any case (WP-M4-2's /trade, #176; the server's lookups by name).
        bot.Context.Identities.Set(PairBot.PartnerId - 1, "Botbeta");
        Assert.True(bot.Context.TryPartnerEntity(out id, out _));
        Assert.Equal(PairBot.PartnerId, id);
        Assert.True(bot.Context.Identities.TryFind("Botbeta", out uint lowest));
        Assert.Equal(PairBot.PartnerId - 1, lowest);
        Assert.True(bot.Context.Identities.TryFind("botBETA", out uint anyCase)); // case-insensitive, as the server and FindInView
        Assert.Equal(lowest, anyCase);
        Assert.Equal(PairBot.PartnerId, bot.Context.Identities.FindInView("botbeta", bot.Context.Entities.Entities, PairBot.OwnId));
        Assert.Equal(0u, bot.Context.Identities.FindInView("Botbeta", bot.Context.Entities.Entities, PairBot.PartnerId)); // self left out
    }

    [Fact]
    public void The_bots_world_client_keeps_the_display_names_of_EntityIdentity()
    {
        var data = Data.Value;
        using var bot = new BotWorldClient(0, data, Shared.Simulation.Actions.ActionCatalog.From(data.Tables), _ => throw new InvalidOperationException());
        var buffer = new byte[EntityIdentity.MaxSize];
        int size = EntityIdentity.Write(buffer, 42, "Botgamma");
        bot.OnReliableReceived(buffer.AsSpan(0, size));
        Assert.True(bot.Identities.TryFind("Botgamma", out uint id));
        Assert.Equal(42u, id);
    }

    // ---- a step that addresses the partner (the pattern WP-M4-4's trade step uses; wave 1 copies it) --------------------

    [Theory]
    [InlineData(false)] // loadtest and run: the memory board of a pair
    [InlineData(true)] // the QA partner bot: the file board of --partner-dir
    public void The_trade_step_finds_its_partner_by_the_name_on_the_pair_board_and_the_first_of_the_pair_invites(bool fileBoard)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pair-trade-" + Guid.NewGuid().ToString("N"));
        try
        {
            (PairLink First, PairLink Second) Link(string folder)
            {
                if (!fileBoard)
                    return Pair();
                string board = Path.Combine(dir, folder); // one folder per pair: a pair's two logs
                return (new PairLink(new FilePairBoard(board, FilePairBoard.PlayerSide, FilePairBoard.BotSide, refreshIntervalMs: 0), isFirst: true),
                    new PairLink(new FilePairBoard(board, FilePairBoard.BotSide, FilePairBoard.PlayerSide, refreshIntervalMs: 0), isFirst: false));
            }
            var (first, second) = Link("far");

            // The first of the pair, the partner far away: no name yet holds; once named and in the snapshots it walks there.
            var walker = new PairBot(first);
            var walk = new TradeStep("m1.demonAshx2:nothing");
            walk.Begin(walker.Context);
            Assert.Equal(BotStepStatus.Running, walker.Tick(walk));
            Assert.Empty(walker.Sent);
            second.SayName("Botbeta");
            walker.Context.Identities.Set(PairBot.PartnerId, "Botbeta"); // EntityIdentity, through DisplayIdentity (rule 8)
            walker.PlayersInSnapshot(partnerOffsetCm: 2_000);
            var command = walker.Context.Brain.Hold();
            Assert.Equal(BotStepStatus.Running, walk.Tick(walker.Context, ref command));
            Assert.NotEqual(walker.Context.Brain.Hold().Keys, command.Keys);
            Assert.Empty(walker.Sent);

            // Beside the partner (within half of TradeSettings.rangeCm): one invite to the partner's entity.
            var (near, nearPartner) = Link("near");
            nearPartner.SayName("Botbeta");
            var inviter = new PairBot(near);
            inviter.Context.Identities.Set(PairBot.PartnerId, "Botbeta");
            inviter.PlayersInSnapshot(partnerOffsetCm: 100);
            var invite = new TradeStep("m1.demonAshx2:nothing");
            invite.Begin(inviter.Context);
            Assert.Equal(BotStepStatus.Running, inviter.Tick(invite));
            Assert.Single(inviter.Sent);
            Assert.True(inviter.Context.TryPartnerEntity(out uint partner, out _));
            Assert.Equal(PairBot.PartnerId, partner);

            // The second of the pair never invites: it waits for the partner's invite.
            near.SayName("Botalpha");
            var follower = new PairBot(nearPartner);
            follower.Context.Identities.Set(PairBot.PartnerId, "Botalpha");
            follower.PlayersInSnapshot(partnerOffsetCm: 100);
            var accept = new TradeStep("nothing:m1.demonAshx2");
            accept.Begin(follower.Context);
            Assert.Equal(BotStepStatus.Running, follower.Tick(accept));
            Assert.Empty(follower.Sent);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void The_trade_step_without_a_pair_is_not_available_partner_needed()
    {
        var alone = new PairBot(null);
        var step = new TradeStep("m1.demonAshx2:nothing");
        step.Begin(alone.Context);
        Assert.Equal(BotStepStatus.Running, alone.Tick(step));
        alone.Context.View.NowMs += Data.Value.Tables.Single<TradeSettings>().InviteTimeoutMs + 1;
        Assert.Equal(BotStepStatus.NotAvailable, alone.Tick(step));
        Assert.Contains("partner needed", alone.Context.LastReason, StringComparison.Ordinal);
        Assert.Empty(alone.Sent);
    }

    // ---- the meet step --------------------------------------------------------------------------------------------------

    [Fact]
    public void Meet_fails_without_a_pair()
    {
        var bot = new PairBot(null);
        var step = new MeetStep("start");
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Failed, bot.Tick(step));
        Assert.Contains("no partner", bot.Context.LastReason, StringComparison.Ordinal);
        Assert.Equal(1, bot.Context.Counters.Loop["meet.noPartner"]);
    }

    [Fact]
    public void Meet_walks_to_the_partner_arrives_and_is_done_once_the_partner_arrived()
    {
        var (b, a) = Pair(); // the bot is the second of the pair, which walks (QA M4 wave 1: the first holds)
        var bot = new PairBot(a);
        var step = new MeetStep("start");
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // no name or position: holds
        b.SayName("Botbeta");
        b.Arrive("start"); // the leader arrives where it stands; then the bot walks to it (QA M4 wave 1)
        var far = bot.Context.View.Self;
        b.PostPosition((int)GridPath.Cm(far.X) + 2_000, (int)GridPath.Cm(far.Y));
        var command = bot.Context.Brain.Hold();
        Assert.Equal(BotStepStatus.Running, step.Tick(bot.Context, ref command));
        Assert.NotEqual(bot.Context.Brain.Hold().Keys, command.Keys); // walks to the posted position
        Assert.False(b.Board.TryReadPartner("meet.start", out _));

        // Beside the partner, which arrived already: arrives and is done.
        b.PostPosition((int)GridPath.Cm(far.X) + 100, (int)GridPath.Cm(far.Y));
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.True(b.Board.TryReadPartner("meet.start", out string arrived));
        Assert.Equal("1", arrived);
        Assert.Equal(1, bot.Context.Counters.Loop["meet.met"]);
    }

    [Fact]
    public void The_first_of_the_pair_holds_where_it_stands_and_both_fight_back_a_monster_on_them()
    {
        // QA M4 wave 1 (the m4-exit run): both bots walked to each other, so the warrior done selling in the city walked to the
        // archer still fighting at camp b; the meeting was inside the camp and the trade there was refused TradeInCombat.
        var (a, b) = Pair();
        var leader = new PairBot(a);
        var step = new MeetStep("trade");
        step.Begin(leader.Context);
        b.SayName("Botbeta");
        var self = leader.Context.View.Self;
        b.PostPosition((int)GridPath.Cm(self.X) + 2_000, (int)GridPath.Cm(self.Y));
        var command = leader.Context.Brain.Hold();
        Assert.Equal(BotStepStatus.Running, step.Tick(leader.Context, ref command));
        Assert.Equal(leader.Context.Brain.Hold().Keys, command.Keys); // holds
        Assert.True(b.Board.TryReadPartner("meet.trade", out _)); // arrived where it stands
        b.Arrive("trade");
        Assert.Equal(BotStepStatus.Done, leader.Tick(step));

        // A demon that has the waiting bot inside its aggro range is fought first; the meeting waits.
        var (c, d) = Pair();
        var walker = new PairBot(d);
        var meet = new MeetStep("trade");
        meet.Begin(walker.Context);
        c.SayName("Botalpha");
        var at = walker.Context.View.Self;
        c.PostPosition((int)GridPath.Cm(at.X) + 2_000, (int)GridPath.Cm(at.Y));
        c.Arrive("trade"); // the leader stands there
        var demon = MonsterDef.Find(Data.Value.Tables, "m1.demon")!;
        walker.Context.View.Monsters.Add(new SeenEntity(40, EntityKind.Monster, (ushort)demon.NetId, at + FixVec2.FromCm(demon.AggroRangeCm / 2, 0), true,
            Shared.Simulation.Actions.ActionState.Idle, 0, 0));
        walker.Context.View.HasResources = true;
        walker.Context.View.Ready = _ => true;
        walker.Context.View.Own = new OwnResources { Hp = 150, MaxHp = 150, Mp = 50, MaxMp = 50, Stamina = 100, MaxStamina = 100 };
        Assert.Equal(BotStepStatus.Running, walker.Tick(meet));
        Assert.Equal(40u, walker.Context.Brain.TargetId);
        Assert.False(c.Board.TryReadPartner("meet.trade", out _));
        walker.Context.View.Monsters.Clear(); // the demon is dead: the walk to the partner goes on
        var walk = walker.Context.Brain.Hold();
        Assert.Equal(BotStepStatus.Running, meet.Tick(walker.Context, ref walk));
        Assert.NotEqual(walker.Context.Brain.Hold().Keys, walk.Keys);
    }

    [Fact]
    public void The_walking_side_waits_for_the_leaders_arrival_before_it_walks_and_arrives()
    {
        // QA M4 wave 1 (smoke warrior B, trade-item): after the first trade the player stood at the auctioneer checking windows; the
        // bot's next meeting (the stall) saw it standing still beside it and arrived there, before the player even left.
        var (first, second) = Pair();
        var bot = new PairBot(second);
        first.SayName("Qaplayer");
        bot.Context.Identities.Set(PairBot.PartnerId, "Qaplayer");
        var step = new MeetStep("trade-stall");
        step.Begin(bot.Context);
        bot.PlayersInSnapshot(100); // standing beside it, not at the point yet
        bot.Context.View.NowMs += 2 * MeetStep.StillMs;
        bot.PlayersInSnapshot(100, 140);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.False(first.Board.TryReadPartner("meet.trade-stall", out _), "no arrival before the leader arrived");
        first.Arrive("trade-stall"); // the player stands at the stall now
        bot.Context.View.NowMs += 2 * MeetStep.StillMs;
        bot.PlayersInSnapshot(100, 180);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        bot.Context.View.NowMs += MeetStep.StillMs; // standing there for a second
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.True(first.Board.TryReadPartner("meet.trade-stall", out _));
    }

    [Fact]
    public void The_walking_side_arrives_only_once_the_partner_stood_still()
    {
        // QA M4 wave 1 (the smoke's trade-item): the bot arrived beside the player still walking to the stall, the meeting counted
        // met when the player stopped farther on, and the trade invite there was refused TradeTooFar.
        var (first, second) = Pair();
        var bot = new PairBot(second);
        first.SayName("Botbeta");
        bot.Context.Identities.Set(PairBot.PartnerId, "Botbeta");
        first.Arrive("trade-stall"); // it posted its arrival, but its entity is still on the move
        var step = new MeetStep("trade-stall");
        step.Begin(bot.Context);
        bot.PlayersInSnapshot(100); // within reach, walking
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        bot.Context.View.NowMs += 300;
        bot.PlayersInSnapshot(180, 106); // still walking
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.False(first.Board.TryReadPartner("meet.trade-stall", out _), "no arrival beside a partner on the move");
        bot.Context.View.NowMs += MeetStep.StillMs; // it stopped there
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.True(first.Board.TryReadPartner("meet.trade-stall", out _), "arrived once the partner stood still");
    }

    [Fact]
    public void A_stall_waits_out_the_combat_state_and_asks_again_after_an_InCombat_answer()
    {
        // QA M4 wave 1 (the merged m4-exit run): stall:open:m1.demonAshx1:999 came straight from the ash fight at camp a and was
        // refused InCombat, which failed the entry and the stall:close after it.
        var bot = new PairBot(null, stalls: true);
        var context = bot.Context;
        var self = context.View.Self;
        context.Stalls!.SetPlaces([((int)GridPath.Cm(self.X), (int)GridPath.Cm(self.Y) + 50)], 300);
        var ash = Data.Value.Rows<ItemDef>().First(i => i.Id == "m1.demonAsh");
        Assert.True(context.Inventory.Apply(ReliableMessages.Write(ReliableMessageId.InventoryContents, new InventoryContents
        {
            Revision = 1, SlotCount = 30, Slots = [new InventorySlotEntry { Slot = 2, ItemNetId = (ushort)ash.NetId, Count = 2 }],
        }), out _));
        var step = new StallStep("open:m1.demonAshx1:999");
        step.Begin(context);
        context.View.LastCombatAtMs = context.View.NowMs; // a hit just now
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Empty(bot.Sent); // waits out the combat state
        context.View.NowMs += 60_000;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(ReliableMessages.TryRead(bot.Sent[^1], out StallRequest open));
        Assert.True(context.Stalls.Apply(ReliableMessages.Write(ReliableMessageId.StallResult,
            new StallResult { RequestId = open.RequestId, Action = StallAction.Open, Reason = StallReason.InCombat }), out _));
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // asked again, not failed
        Assert.Equal(1, context.Counters.Loop["stall.retry.InCombat"]);
    }

    [Fact]
    public void A_trade_invite_refused_in_combat_is_asked_again_not_failed()
    {
        // QA M4 wave 1 (the m4-exit run): TradeInCombat failed the warrior's trade entry at once; it is asked again after the
        // cooldown, as TradeBusy is.
        var (a, b) = Pair();
        var bot = new PairBot(a); // the first of the pair invites
        b.SayName("Botbeta");
        bot.PlayersInSnapshot(100);
        bot.Context.Identities.Set(PairBot.PartnerId, "Botbeta");
        var trade = new TradeStep("m1.demonAsh:gold=10");
        trade.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(trade));
        Assert.True(ReliableMessages.TryRead(bot.Sent[^1], out TradeRequest invite));
        Assert.Equal(TradeAction.Invite, invite.Action);
        Assert.True(bot.Context.Trade.Apply(ReliableMessages.Write(ReliableMessageId.TradeResult,
            new TradeResult { RequestId = invite.RequestId, Action = TradeAction.Invite, Reason = TradeReason.TradeInCombat }), out _));
        Assert.Equal(BotStepStatus.Running, bot.Tick(trade));
        Assert.Equal(1, bot.Context.Counters.Loop["trade.retry.TradeInCombat"]);
        int sent = bot.Sent.Count;
        bot.Context.View.NowMs += 10_000; // past the cooldown: invites again
        Assert.Equal(BotStepStatus.Running, bot.Tick(trade));
        Assert.True(bot.Sent.Count > sent);
    }

    [Fact]
    public void Meet_fails_when_the_walk_to_the_partner_gets_no_closer_for_pairWaitMs()
    {
        // Review finding 7: a partner the bot can never reach (here the bot does not move) ends the step with a reason.
        var (b, a) = Pair(); // the bot is the second of the pair, which walks (QA M4 wave 1: the first holds)
        var bot = new PairBot(a);
        b.SayName("Botfar");
        b.Arrive("start"); // the leader stands there
        var self = bot.Context.View.Self;
        b.PostPosition((int)GridPath.Cm(self.X) + 5_000, (int)GridPath.Cm(self.Y));
        var step = new MeetStep("start");
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // walking, closest so far
        bot.Context.View.NowMs += bot.Context.Behaviour.PairWaitMs - 1;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        bot.Context.View.NowMs += 2;
        Assert.Equal(BotStepStatus.Failed, bot.Tick(step));
        Assert.Contains("not reached", bot.Context.LastReason, StringComparison.Ordinal);
        Assert.Equal(1, bot.Context.Counters.Loop["meet.unreachable"]);
        Assert.False(b.Board.TryReadPartner("meet.start", out _)); // it never arrived
    }

    [Fact]
    public void Meet_fails_after_pairWaitMs_and_a_timed_out_step_gives_its_meeting_up()
    {
        var (a, b) = Pair();
        var bot = new PairBot(a);
        b.SayName("Botbeta");
        var self = bot.Context.View.Self;
        b.PostPosition((int)GridPath.Cm(self.X), (int)GridPath.Cm(self.Y));
        var step = new MeetStep("party");
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // arrived, waiting
        bot.Context.View.NowMs += bot.Context.Behaviour.PairWaitMs + 1;
        Assert.Equal(BotStepStatus.Failed, bot.Tick(step));
        Assert.Contains("did not reach 'party'", bot.Context.LastReason, StringComparison.Ordinal);

        // In a loop the ended step's meeting is given up: the partner that comes later is told so, not left waiting.
        var loop = new BotLoop([new MeetStep("trade")], stepTimeoutMs: 1_000, ["meet:trade"], once: true);
        loop.Tick(bot.Context); // arrives at trade 1
        bot.Context.View.NowMs += 2_000;
        loop.Tick(bot.Context); // times out
        Assert.True(loop.Finished);
        Assert.Equal(StepOutcome.TimedOut, loop.Outcomes[0].Outcome);
        int theirs = b.Arrive("trade");
        Assert.Equal(MeetState.PartnerGaveUp, b.Check("trade", theirs));
    }

    // ---- PR #254 review findings 1, 3 and 4 (the meeting's waits and the fight back) -------------------------------------

    [Fact]
    public void A_partner_that_never_stands_still_fails_the_meeting_after_pairWaitMs()
    {
        // PR #254 review 1: a leader that fights back at a camp never stands still, and the walking side waited for it until the
        // step's own timeout (45 minutes in m4-exit), with no counter and no reason.
        var (first, second) = Pair();
        var bot = new PairBot(second);
        first.SayName("Botalpha");
        bot.Context.Identities.Set(PairBot.PartnerId, "Botalpha");
        first.Arrive("trade"); // it posted its arrival, then keeps moving beside the bot
        var step = new MeetStep("trade");
        step.Begin(bot.Context);
        int waitMs = bot.Context.Behaviour.PairWaitMs;
        uint tick = 100;
        bot.PlayersInSnapshot(100, tick);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // beside it, waiting for it to stand still
        bot.Context.View.NowMs += waitMs;
        bot.PlayersInSnapshot(180, tick += 20);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        bot.Context.View.NowMs += 2;
        bot.PlayersInSnapshot(100, tick += 20);
        Assert.Equal(BotStepStatus.Failed, bot.Tick(step));
        Assert.Contains("never stood still", bot.Context.LastReason, StringComparison.Ordinal);
        Assert.Equal(1, bot.Context.Counters.Loop["meet.notStill"]);
        Assert.False(first.Board.TryReadPartner("meet.trade", out _), "it never arrived");
    }

    [Fact]
    public void A_fighting_bot_still_sees_its_partner_leave_or_give_up()
    {
        // PR #254 review 3: the fight back ran before the gone and give-up checks, so a bot with a monster on it never saw that
        // its partner had left.
        var gone = Walker(out var goneLeader, out var goneStep);
        goneLeader.SayGone();
        Assert.Equal(BotStepStatus.Failed, gone.Tick(goneStep));
        Assert.Equal(1, gone.Context.Counters.Loop["meet.gone"]);

        var gaveUp = Walker(out var quitter, out var quitStep);
        quitter.Open("trade");
        quitter.GiveUpOpen();
        Assert.Equal(BotStepStatus.Failed, gaveUp.Tick(quitStep));
        Assert.Equal(1, gaveUp.Context.Counters.Loop["meet.gaveUp"]);
    }

    [Fact]
    public void A_fight_longer_than_pairWaitMs_does_not_fail_the_meeting_afterwards()
    {
        // PR #254 review 3: the wait clock ran during the fight, so a fight longer than pairWaitMs failed the meeting on the next tick.
        var walker = Walker(out _, out var step);
        Assert.Equal(BotStepStatus.Running, walker.Tick(step)); // fighting
        Assert.Equal(40u, walker.Context.Brain.TargetId);
        walker.Context.View.NowMs += walker.Context.Behaviour.PairWaitMs + 1;
        Assert.Equal(BotStepStatus.Running, walker.Tick(step)); // still fighting
        walker.Context.View.Monsters.Clear(); // the demon is dead
        walker.Context.View.NowMs += 50;
        Assert.Equal(BotStepStatus.Running, walker.Tick(step)); // the wait for the leader starts again
        walker.Context.View.NowMs += walker.Context.Behaviour.PairWaitMs + 1;
        Assert.Equal(BotStepStatus.Failed, walker.Tick(step)); // and it is still bounded
        Assert.Equal(1, walker.Context.Counters.Loop["meet.late"]);
    }

    [Fact]
    public void A_monster_behind_a_wall_is_no_threat_until_it_hits_the_bot()
    {
        // PR #254 review 4: the threat test was distance only, so a bot waiting near a camp behind a wall walked off to fight.
        var demon = MonsterDef.Find(Data.Value.Tables, "m1.demon")!;
        var (self, monster) = BlockedWithin(demon.AggroRangeCm)
                              ?? throw new InvalidOperationException("no marker with a wall within a demon's aggro range");
        var bot = new PairBot(null);
        var view = bot.Context.View;
        view.Self = self;
        view.Confirmed = self;
        view.HasResources = true;
        view.Ready = _ => true;
        view.Own = new OwnResources { Hp = 150, MaxHp = 150, Mp = 50, MaxMp = 50, Stamina = 100, MaxStamina = 100 };
        view.Monsters.Add(new SeenEntity(40, EntityKind.Monster, (ushort)demon.NetId, monster, true,
            Shared.Simulation.Actions.ActionState.Idle, 0, 0));
        var command = bot.Context.Brain.Hold();
        Assert.False(bot.Context.FightBack(ref command), "a wall between: no threat");

        view.HitByAtMs[40] = view.NowMs; // it hit the bot (an own Hit entry with the bot as target)
        Assert.True(bot.Context.Threatens(view.Monsters[0]), "a monster that hit the bot is a threat");
        int combatStateMs = Data.Value.Rows<StartingValuesM1>().First().CombatStateMs;
        view.NowMs += combatStateMs;
        Assert.False(bot.Context.Threatens(view.Monsters[0]), "the hit is older than the combat state");

        // In range with a clear walk it still is (the QA M4 wave 1 rule, before the first hit).
        var open = new PairBot(null);
        var at = open.Context.View.Self;
        var near = new SeenEntity(41, EntityKind.Monster, (ushort)demon.NetId, at + FixVec2.FromCm(demon.AggroRangeCm / 2, 0), true,
            Shared.Simulation.Actions.ActionState.Idle, 0, 0);
        Assert.True(GridPath.Clear(Data.Value.Grid, at, near.Position));
        Assert.True(open.Context.Threatens(near));
    }

    /// <summary>A marker and a point within <paramref name="rangeCm"/> of it with a wall between (no clear grid walk).</summary>
    private static (FixVec2 Self, FixVec2 Monster)? BlockedWithin(int rangeCm)
    {
        var grid = Data.Value.Grid;
        foreach (var (_, at) in Data.Value.Markers.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            for (int angle = 0; angle < 360; angle += 15)
            {
                for (int cm = rangeCm / 2; cm <= rangeCm; cm += rangeCm / 4)
                {
                    double radians = angle * Math.PI / 180;
                    var point = at + FixVec2.FromCm((int)(cm * Math.Cos(radians)), (int)(cm * Math.Sin(radians)));
                    if (!GridPath.Clear(grid, at, point))
                        return (at, point);
                }
            }
        }
        return null;
    }

    /// <summary>The walking side of a pair whose leader posted its name and position but has not arrived, a demon in its aggro range.</summary>
    private static PairBot Walker(out PairLink leader, out MeetStep step)
    {
        var (c, d) = Pair();
        leader = c;
        var walker = new PairBot(d);
        step = new MeetStep("trade");
        step.Begin(walker.Context);
        c.SayName("Botalpha");
        var at = walker.Context.View.Self;
        c.PostPosition((int)GridPath.Cm(at.X) + 2_000, (int)GridPath.Cm(at.Y));
        var demon = MonsterDef.Find(Data.Value.Tables, "m1.demon")!;
        walker.Context.View.Monsters.Add(new SeenEntity(40, EntityKind.Monster, (ushort)demon.NetId, at + FixVec2.FromCm(demon.AggroRangeCm / 2, 0), true,
            Shared.Simulation.Actions.ActionState.Idle, 0, 0));
        walker.Context.View.HasResources = true;
        walker.Context.View.Ready = _ => true;
        walker.Context.View.Own = new OwnResources { Hp = 150, MaxHp = 150, Mp = 50, MaxMp = 50, Stamina = 100, MaxStamina = 100 };
        return walker;
    }

    // ---- the serve step (the QA partner bot) ----------------------------------------------------------------------------

    [Fact]
    public void Serve_plays_the_requested_partner_script_and_answers_then_ends_when_the_smoke_is_gone()
    {
        var (botSide, playerSide) = Pair();
        var bot = new PairBot(botSide);
        var step = new ServeStep();
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // stands by
        var self = bot.Context.View.Self;
        playerSide.SayName("Qaplayer");
        playerSide.PostPosition((int)GridPath.Cm(self.X) + 100, (int)GridPath.Cm(self.Y));

        int request = playerSide.Request("join"); // the row qa-partner-join: meet:join
        int occurrence = playerSide.Arrive("join");
        for (int i = 0; i < 5 && !playerSide.TryReadResult(request, out _); i++)
        {
            bot.Context.View.NowMs += 50;
            Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        }
        Assert.Equal(MeetState.Met, playerSide.Check("join", occurrence));
        Assert.True(playerSide.TryReadResult(request, out string result));
        Assert.Equal(PairScriptResult.Done, PairScriptResult.Outcome(result));
        Assert.Contains("meet:join done", result, StringComparison.Ordinal);

        int unknown = playerSide.Request("no-such-script");
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(playerSide.TryReadResult(unknown, out string refused));
        Assert.Equal(PairScriptResult.Failed, PairScriptResult.Outcome(refused));
        Assert.Contains("qa-partner-no-such-script", refused, StringComparison.Ordinal);

        playerSide.SayGone();
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Context.Counters.Loop["serve.scripts"]);
    }

    [Fact]
    public void Serve_stops_a_script_the_smoke_gave_up_answers_cancelled_and_takes_the_next_request()
    {
        // QA M4 wave 1 F2: loot-bag-stolen failed before it posted bag.at, the bot's recoverBag:partner kept waiting for it,
        // and serve never took request 5 (party-loot). A step that ends without its answer now posts cancel.<n>.
        var (botSide, playerSide) = Pair();
        var bot = new PairBot(botSide, lootBags: true);
        var step = new ServeStep();
        step.Begin(bot.Context);
        playerSide.SayName("Qaplayer");
        int stuck = playerSide.Request("loot-bag-stolen-take"); // recoverBag:partner: waits for bag.at, which never comes
        for (int i = 0; i < 5; i++)
        {
            bot.Context.View.NowMs += 50;
            Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        }
        Assert.False(playerSide.TryReadResult(stuck, out string early), "the script still waits for the bag: " + early);

        Assert.Equal([stuck], playerSide.CancelUnanswered());
        Assert.Empty(playerSide.CancelUnanswered()); // posted once
        bot.Context.View.NowMs += 50;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(playerSide.TryReadResult(stuck, out string cancelled));
        Assert.Equal(PairScriptResult.Cancelled, PairScriptResult.Outcome(cancelled));
        Assert.Contains("recoverBag:partner: the player gave the request up", cancelled, StringComparison.Ordinal);

        // The bot is free: the next request is played and answered.
        var self = bot.Context.View.Self;
        playerSide.PostPosition((int)GridPath.Cm(self.X) + 100, (int)GridPath.Cm(self.Y));
        int next = playerSide.Request("join");
        int occurrence = playerSide.Arrive("join");
        for (int i = 0; i < 5 && !playerSide.TryReadResult(next, out _); i++)
        {
            bot.Context.View.NowMs += 50;
            Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        }
        Assert.Equal(MeetState.Met, playerSide.Check("join", occurrence));
        Assert.True(playerSide.TryReadResult(next, out string result));
        Assert.Equal(PairScriptResult.Done, PairScriptResult.Outcome(result));
        Assert.Empty(playerSide.CancelUnanswered()); // every request answered

        // gone while a script runs ends it too, and then serve.
        playerSide.Request("loot-bag-stolen-take");
        bot.Context.View.NowMs += 50;
        bot.Tick(step);
        playerSide.SayGone();
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // answers the script cancelled
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(2, bot.Context.Counters.Loop["serve.cancelled"]);
    }

    [Fact]
    public void A_cancelled_script_is_answered_once_the_server_shows_the_party_gone_or_after_CleanUpMs()
    {
        // PR #254 review 5: "cancelled" was answered right after the party leave was sent, before the server confirmed it, so the
        // smoke's next pair step could be refused for the leftover party.
        var (botSide, playerSide) = Pair();
        var bot = new PairBot(botSide, lootBags: true);
        var step = new ServeStep();
        step.Begin(bot.Context);
        playerSide.SayName("Qaplayer");
        uint version = 0;
        void PartyOf(long partyId) => Assert.True(bot.Context.Party.Apply(ReliableMessages.Write(ReliableMessageId.PartyState, new PartyState
        {
            Version = ++version, PartyId = partyId, LeaderEntityId = 1,
            Members = partyId == 0 ? [] : [new PartyMemberState { EntityId = 1, Name = "Qaplayer", Online = true, HpPercent = 100 }],
        }), bot.Context.View.NowMs, out _));
        PartyOf(5);

        int first = playerSide.Request("loot-bag-stolen-take"); // waits for a bag that never comes
        bot.Context.View.NowMs += 50;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal([first], playerSide.CancelUnanswered());
        int sent = bot.Sent.Count;
        bot.Context.View.NowMs += 50;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(bot.Sent.Count > sent, "the party leave is sent");
        Assert.False(playerSide.TryReadResult(first, out string early), "no answer while the party stands: " + early);
        bot.Context.View.NowMs += 50;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.False(playerSide.TryReadResult(first, out _));
        PartyOf(0); // the server's state: the party is left
        bot.Context.View.NowMs += 50;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(playerSide.TryReadResult(first, out string answered));
        Assert.Equal(PairScriptResult.Cancelled, PairScriptResult.Outcome(answered));
        Assert.DoesNotContain("still open", answered, StringComparison.Ordinal);

        // A leave the server never confirms: answered after CleanUpMs anyway, naming what is still open.
        PartyOf(6);
        int second = playerSide.Request("loot-bag-stolen-take");
        bot.Context.View.NowMs += 50;
        bot.Tick(step);
        Assert.Equal([second], playerSide.CancelUnanswered());
        bot.Context.View.NowMs += 50;
        bot.Tick(step);
        Assert.False(playerSide.TryReadResult(second, out _));
        bot.Context.View.NowMs += ServeStep.CleanUpMs;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(playerSide.TryReadResult(second, out string late));
        Assert.Equal(PairScriptResult.Cancelled, PairScriptResult.Outcome(late));
        Assert.Contains("still open after CleanUpMs", late, StringComparison.Ordinal);
        Assert.Equal(1, bot.Context.Counters.Loop["serve.cleanUpLate"]);
        Assert.Equal(2, bot.Context.Counters.Loop["serve.cancelled"]);
    }

    [Fact]
    public void Cancel_unanswered_returns_only_the_requests_it_gave_up_now()
    {
        // PR #254 review 6: the smoke's FreePartner waited for every request since the first, also one given up earlier that never
        // answered, so each later cancel waited out PartnerCancelSeconds.
        var (_, playerSide) = Pair();
        int stuck = playerSide.Request("a");
        Assert.Equal([stuck], playerSide.CancelUnanswered()); // never answered
        int next = playerSide.Request("b");
        Assert.Equal([next], playerSide.CancelUnanswered());
        Assert.Empty(playerSide.CancelUnanswered());
    }

    [Fact]
    public void Trade_hold_accepts_offers_nothing_and_is_done_when_the_partner_cancels_the_open_trade()
    {
        // QA M4 wave 1: the smoke opens a trade only to see that the left-edge windows and the pickup key at a bag stay shut, then
        // cancels it; for trade:hold that cancel is the end it waits for, not a failure.
        var hold = new TradeStep(TradeStep.Hold);
        Assert.True(hold.Holding);
        Assert.False(new TradeStep("gold=5:m1.demonAsh").Holding);
        var (botSide, _) = Pair();
        var bot = new PairBot(botSide);
        hold.Begin(bot.Context);
        void Phase(TradePhase phase) => Assert.True(bot.Context.Trade.Apply(ReliableMessages.Write(ReliableMessageId.TradeState,
            new TradeState { TradeId = 3, Phase = phase, PartnerEntityId = PairBot.PartnerId, PartnerName = "Qaplayer" }), out _));

        Phase(TradePhase.Invited);
        Assert.Equal(BotStepStatus.Running, bot.Tick(hold));
        Assert.True(ReliableMessages.TryRead(bot.Sent[^1], out TradeRequest accept));
        Assert.Equal(TradeAction.Accept, accept.Action);
        Assert.True(bot.Context.Trade.Apply(ReliableMessages.Write(ReliableMessageId.TradeResult,
            new TradeResult { RequestId = accept.RequestId, Action = TradeAction.Accept, Reason = TradeReason.None }), out _));
        Phase(TradePhase.Open);
        int sent = bot.Sent.Count;
        for (int i = 0; i < 3; i++)
            Assert.Equal(BotStepStatus.Running, bot.Tick(hold));
        Assert.Equal(sent, bot.Sent.Count); // no offer, no lock
        Phase(TradePhase.Cancelled);
        Assert.Equal(BotStepStatus.Done, bot.Tick(hold));
        Assert.Equal(1, bot.Context.Counters.Loop["trade.held"]);
    }

    [Fact]
    public void The_answer_names_the_first_entry_that_was_not_done()
    {
        Assert.Equal("done; meet:join done", ServeStep.Result([new StepResult("meet:join", StepOutcome.Done, "")]));
        string text = ServeStep.Result([new StepResult("meet:x", StepOutcome.Done, ""),
            new StepResult("party:accept", StepOutcome.NotYet, "no party protocol yet, WP-M4-2")]);
        Assert.Equal("not yet; party:accept: no party protocol yet, WP-M4-2; meet:x done; party:accept not yet", text);
        Assert.Equal(PairScriptResult.NotYet, PairScriptResult.Outcome(text));
    }

    // ---- the M4 placeholders and scripts --------------------------------------------------------------------------------

    [Theory]
    [InlineData("contract:craft", "no contract client")] // WP-M4-10: real; without its client (an offline context) it is not yet
    [InlineData("contract:propose:m3.moldSword", "no contract client")]
    public void An_M4_step_without_its_client_is_not_yet(string entry, string wp)
    {
        var bot = new PairBot(null);
        BotBehaviour.TryParseLoopStep(entry, out string id, out string argument);
        var loop = new BotLoop([Create(id, argument)], 60_000, [entry], once: true);
        loop.Tick(bot.Context);
        Assert.True(loop.Finished);
        Assert.Equal(StepOutcome.NotYet, loop.Outcomes[0].Outcome);
        Assert.Contains(wp, loop.Outcomes[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trade_entry_reads_give_and_for_and_a_bad_one_throws_at_the_start()
    {
        var trade = new TradeStep("m1.demonAshx3:gold=10");
        Assert.Equal(new TradeSide("m1.demonAsh", 3, 0), trade.Give);
        Assert.Equal(new TradeSide("", 0, 10), trade.For);
        var back = new TradeStep("gold=10:m1.demonAsh");
        Assert.True(back.Give.IsGold);
        Assert.Equal(1, back.For.Count);
        Assert.True(new TradeStep("m2.potionHpSmall:nothing").For.IsNothing);
        foreach (var bad in new[] { "", "m1.demonAsh", "nothing:nothing", "gold=0:m1.demonAsh", "gold=x:m1.demonAsh", "m1.demonAshx0:gold=1", ":gold=1" })
            Assert.Throws<ArgumentException>(() => new TradeStep(bad));
        Assert.Throws<ArgumentException>(() => new PickupStep("everything"));
    }

    [Theory]
    [InlineData("m4-exit-a")]
    [InlineData("m4-exit-b")]
    public void An_M4_exit_script_meets_its_partner_and_the_M4_entries_are_not_yet_naming_their_WP(string behaviourId)
    {
        // Both scripts of the pair play together with nothing connected (as the M3 exit script test does): meet:start is
        // done, the M4 legs are not yet naming their WP, the M2 and M3 steps play as in that test.
        var (a, b) = Pair();
        var registry = BotStepRegistry.Default();
        var bots = new[] { new PairBot(a, registry), new PairBot(b, registry) };
        var loops = new[] { registry.NewLoop(Data.Value.Behaviour("m4-exit-a")), registry.NewLoop(Data.Value.Behaviour("m4-exit-b")) };
        for (int i = 0; i < 2; i++)
        {
            bots[i].Context.Own.Level = 25; // the m4-exit seed (TODO(open) decision 14)
            bots[i].Context.Pair!.SayName("Bot" + i);
            var self = bots[i].Context.View.Self;
            bots[i].Context.Pair!.PostPosition((int)GridPath.Cm(self.X), (int)GridPath.Cm(self.Y));
        }
        var fightTicks = new int[2];
        for (int tick = 0; tick < 400 && !(loops[0].Finished && loops[1].Finished); tick++)
        {
            for (int i = 0; i < 2; i++)
            {
                var context = bots[i].Context;
                var loop = loops[i];
                context.View.NowMs += 50;
                if (loop.Current?.Id == "fight")
                {
                    context.Counters.Kills++;
                    foreach (var monster in Data.Value.Rows<MonsterDef>())
                        context.Counters.KillsByDef[monster.NetId] = context.Counters.KillsOf(monster.NetId) + 1;
                    if (++fightTicks[i] > 20)
                        context.View.NowMs += context.Behaviour.LoopStepTimeoutMs;
                }
                else
                    fightTicks[i] = 0;
                if (loop.Current?.Id is "party" or "whisper" or "trade") // WP-M4-2, WP-M4-4: real; nothing connected answers
                    context.View.NowMs += context.Behaviour.LoopStepTimeoutMs / 2;
                if (loop.Current?.Id == "pickup")
                    context.View.NowMs += Data.Value.Behaviour("m4-exit-a").LoopPickupIdleMs;
                loop.Tick(context);
            }
        }

        int index = behaviourId == "m4-exit-a" ? 0 : 1;
        var played = loops[index];
        var behaviour = Data.Value.Behaviour(behaviourId);
        Assert.True(played.Finished);
        Assert.Equal(behaviour.LoopSteps, played.Outcomes.Select(o => o.Entry));
        string[] m4Placeholders = []; // WP-M4-10 made the last one (contract) real
        foreach (var outcome in played.Outcomes)
        {
            string entry = outcome.Entry;
            BotBehaviour.TryParseLoopStep(entry, out string id, out string argument);
            bool m4 = m4Placeholders.Contains(id);
            if (m4)
                Assert.True(outcome.Outcome == StepOutcome.NotYet && outcome.Reason.Contains("WP-M4-", StringComparison.Ordinal), $"{entry}: {outcome}");
            else if (entry == "pickup:roll") // WP-M4-6 (real): nothing connected forms no party, failed in a script
                Assert.True(outcome.Outcome == StepOutcome.Failed && outcome.Reason.Contains("no party", StringComparison.Ordinal), $"{entry}: {outcome}");
            else if (id is "contract") // WP-M4-10 (real): this context has no contract client
                Assert.True(outcome.Outcome is StepOutcome.NotYet or StepOutcome.Failed && outcome.Reason.Length > 0, $"{entry}: {outcome}");
            else if (id is "mail") // WP-M4-3 (real): nothing connected holds no mail (this context has no mail client)
                Assert.True(outcome.Outcome is StepOutcome.NotYet or StepOutcome.Failed && outcome.Reason.Length > 0, $"{entry}: {outcome}");
            else if (id is "list") // WP-M4-7 (real): nothing connected has no auctioneer in sight
                Assert.True(outcome.Outcome is StepOutcome.NotYet or StepOutcome.Failed && outcome.Reason.Length > 0, $"{entry}: {outcome}");
            else if (id is "stall" or "offline" || entry.StartsWith("buy:stall", StringComparison.Ordinal)) // WP-M4-8 (real): no stall client or relogin here
                Assert.True(outcome.Outcome is StepOutcome.NotYet or StepOutcome.Failed && outcome.Reason.Length > 0, $"{entry}: {outcome}");
            else if (id is "trade") // WP-M4-4 (real): the partner is never in the snapshots with nothing connected
                Assert.True(outcome.Outcome == StepOutcome.NotYet && outcome.Reason.Contains("partner needed", StringComparison.Ordinal), $"{entry}: {outcome}");
            else if (id is "party") // WP-M4-2 (real): the partner posted its name, nothing connected answers the invite
                Assert.Equal(StepOutcome.TimedOut, outcome.Outcome);
            else if (id is "die" or "recoverBag") // WP-M4-9 (real): this context has no loot bag client
                Assert.True(outcome.Outcome == StepOutcome.NotYet && outcome.Reason.Contains("no loot bag client", StringComparison.Ordinal), $"{entry}: {outcome}");
            else if (entry == "sell:stolen") // WP-M4-9 (real): nothing was stolen with nothing connected
                Assert.True(outcome.Outcome == StepOutcome.Failed && outcome.Reason.Contains("no stolen", StringComparison.Ordinal), $"{entry}: {outcome}");
            else if (entry.StartsWith("fight:until=", StringComparison.Ordinal)) // QA M4 wave 1: nothing connected puts the ash in the bag
                Assert.Equal(StepOutcome.TimedOut, outcome.Outcome);
            else if (id is "meet" or "level" or "takeJob" or "fight" or "pickup" or "sell")
                Assert.True(outcome.Outcome == StepOutcome.Done, $"{entry}: {outcome}");
            else if (id == "gather") // the real step (WP-M3-5): no JobState with nothing connected, failed in a script
                Assert.True(outcome.Outcome == StepOutcome.Failed && outcome.Reason.Contains("gather.noJob", StringComparison.Ordinal), $"{entry}: {outcome}");
            else if (id == "buy")
                Assert.True(outcome.Outcome is StepOutcome.Failed or StepOutcome.NotYet && outcome.Reason.Length > 0, $"{entry}: {outcome}");
            else
                Assert.Fail($"{entry}: no expectation ({outcome})");
        }
        Assert.Equal(7, bots[index].Context.Counters.Loop["meet.met"]); // meet:start, meet:trade, meet:auction, meet:sold, meet:bag, meet:stolen and meet:contract (WP-M4-10)
    }

    // ---- the rows --------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_M4_rows_pair_their_bots_and_name_every_leg()
    {
        var data = Data.Value;
        var exit = data.Scenario("m4-exit");
        Assert.True(exit.Pairs);
        Assert.Equal((2, 25, true), (exit.Bots, exit.SeedLevel, exit.RequireStepsDone));
        Assert.Equal(["m4-exit-a", "m4-exit-b"], exit.BotBehaviourIds);
        Assert.Equal((1, 0, -1), (exit.PartnerOf(0), exit.PartnerOf(1), data.Scenario("m3-exit").PartnerOf(0)));
        Assert.Empty(exit.SeedItemIds);
        var loop = data.Scenario("m4-loop");
        Assert.True(loop.Pairs && loop.Bots % 2 == 0);
        Assert.False(data.Behaviour("m4-loop-a").LoopOnce);
        var ci = data.Scenario("ci-pair");
        Assert.True(ci.Pairs && ci.RequireNoFailedSteps && !ci.RequireStepsDone);
        // WP-M4-14: each CI bot reads its seed mail in the field, is refused the claim there and claims it at the mailbox.
        Assert.Equal(["m1.demonAsh:1:10"], ci.SeedMail);
        foreach (var side in new[] { "ci-pair-a", "ci-pair-b" })
        {
            var steps = data.Behaviour(side).LoopSteps.ToList();
            Assert.True(steps.IndexOf("fight:1") < steps.IndexOf("mail:read") && steps.IndexOf("mail:read") < steps.IndexOf("mail:claim"), side);
        }
        // The seven legs of plan section 1: each one has an entry in a script, named for its WP.
        var entries = data.Behaviour("m4-exit-a").LoopSteps.Concat(data.Behaviour("m4-exit-b").LoopSteps).ToList();
        foreach (var leg in new[] { "party:invite", "party:accept", "pickup:roll", "trade:", "list:", "buy:listing:", "stall:open:", "buy:stall", "offline:", "stall:close",
                     "die:", "recoverBag", "recoverBag:expire", "recoverBag:partner", "sell:stolen", "contract:propose:", "contract:craft", "mail:claim" })
            Assert.Contains(entries, e => e.StartsWith(leg, StringComparison.Ordinal));
        // The QA partner rows: serve stands by; the join script meets.
        Assert.Equal(["serve"], data.Behaviour("qa-partner").LoopSteps);
        Assert.True(data.Behaviour("qa-partner-join").LoopOnce);
        Assert.Equal(ServeStep.ScriptPrefix + "join", "qa-partner-join");
    }

    [Fact]
    public void A_scenario_with_pair_steps_needs_pairs_and_pairs_need_an_even_count()
    {
        var data = Data.Value;
        var row = Copy(data.Scenario("ci-pair"));
        var errors = Validate(row);
        Assert.Empty(errors);
        row.Pairs = false;
        Assert.Contains(Validate(row), e => e.Contains("pairs", StringComparison.Ordinal));
        row.Pairs = true;
        row.Bots = 3;
        Assert.Contains(Validate(row), e => e.Contains("even", StringComparison.Ordinal));
        // Review finding 5: each pair gets its two roles (an even botBehaviourIds list).
        row.Bots = 2;
        row.BotBehaviourIds = ["ci-pair-a", "ci-pair-b", "ci-pair-a"];
        Assert.Contains(Validate(row), e => e.StartsWith("BotBehaviourIds", StringComparison.Ordinal));
        row.BotBehaviourIds = [];
        Assert.Contains(Validate(row), e => e.StartsWith("BotBehaviourIds", StringComparison.Ordinal));
        row.BotBehaviourIds = ["ci-pair-a", "ci-pair-b"];
        Assert.DoesNotContain(Validate(row), e => e.StartsWith("BotBehaviourIds", StringComparison.Ordinal));
        var loop = Copy(data.Scenario("m4-loop"));
        loop.RequireNoFailedSteps = true; // m4-loop is a loop, not scripts
        Assert.Contains(Validate(loop), e => e.Contains("not a script", StringComparison.Ordinal));
        Assert.True(data.Behaviour("m4-exit-b").UsesPairSteps());
        Assert.False(data.Behaviour("m3-exit-jeweler").UsesPairSteps());
    }

    [Fact]
    public void The_loaded_zone_run_is_m2_zone_with_stall_and_bag_bots()
    {
        // WP-M4-12, plan section 1: the 50-bot zone run with stalls and loot bags loaded meets the same tick budget as m2-zone.
        var data = Data.Value;
        var zone = data.Scenario("m4-zone");
        var m2 = data.Scenario("m2-zone");
        Assert.Equal(m2.Bots, zone.Bots);
        Assert.Equal(m2.TickP99MaxPercentOfPeriod, zone.TickP99MaxPercentOfPeriod);
        Assert.Equal(m2.LinkId, zone.LinkId);
        Assert.False(zone.Pairs);
        Assert.Empty(Validate(zone));
        var behaviours = Enumerable.Range(0, zone.Bots).Select(zone.BehaviourOf).ToList();
        Assert.Equal(10, behaviours.Count(b => b == "m4-zone-stall"));
        Assert.Equal(10, behaviours.Count(b => b.StartsWith("m4-zone-bag", StringComparison.Ordinal)));
        var stall = data.Behaviour("m4-zone-stall");
        var bags = new[] { data.Behaviour("m4-zone-bag-a"), data.Behaviour("m4-zone-bag-b") };
        Assert.All(bags.Append(stall), b =>
        {
            Assert.True(b.LoopOnce);
            Assert.False(b.UsesPairSteps());
            Assert.Equal(data.Behaviour("field-zone").HomeMarkerIds, b.HomeMarkerIds);
            Assert.True(b.LoopStepTimeoutMs > zone.DurationMs, "the scripts outlast the run");
            Assert.Equal(b.LoopSteps.Length, BotStepRegistry.Default().NewLoop(b).Steps.Count); // known steps, readable arguments
        });
        Assert.Equal(["sell", "stall:open:m1.demonAshx1:20", "fight:200"], stall.LoopSteps); // run 1: no gold for the fee at level 1
        // Run 1: a level-1 bot at a ring 1 camp was never killed; the bag bots die at ring 2 camps the level seed may enter.
        Assert.All(bags, b => Assert.StartsWith("die:field1.spawn.spitter.", b.LoopSteps[0], StringComparison.Ordinal));
        Assert.All(bags, b => Assert.True(data.CanEnter(data.Markers[b.LoopSteps[0].Substring("die:".Length)], zone.SeedLevel)));
    }

    [Fact]
    public void The_no_failed_step_criterion_lets_not_yet_pass_and_fails_a_failed_or_timed_out_entry()
    {
        BotResult Result(params StepResult[] script) => new(0, 1, "warrior", "warrior", "Bot", "", new BotCounters(), BotActivity.Waiting,
            0, 0, 0, Script: script);
        var pass = LoadTest.NoFailedStepsCriterion([Result(new StepResult("meet:start", StepOutcome.Done, ""), new StepResult("party:invite", StepOutcome.NotYet, "WP-M4-2"))]);
        Assert.True(pass.Pass);
        var failed = LoadTest.NoFailedStepsCriterion([Result(new StepResult("meet:end", StepOutcome.Failed, "partner did not reach 'end'"))]);
        Assert.False(failed.Pass);
        Assert.Contains("partner did not reach", failed.Actual, StringComparison.Ordinal);
        Assert.False(LoadTest.NoFailedStepsCriterion([Result(new StepResult("meet:end", StepOutcome.TimedOut, ""))]).Pass);
        Assert.False(LoadTest.NoFailedStepsCriterion([]).Pass);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    private static (PairLink First, PairLink Second) Pair()
    {
        var sides = MemoryPairBoard.NewPair();
        return (new PairLink(sides[0], isFirst: true), new PairLink(sides[1], isFirst: false));
    }

    private static IBotStep Create(string id, string argument) =>
        BotStepRegistry.Default().NewLoop(new BotBehaviour
        {
            Id = "one", LoopOnce = true, LoopSteps = [argument.Length > 0 ? id + ":" + argument : id], HomeMarkerIds = ["m1.spawn.combat.a"],
        }).Steps[0];

    private static BotScenario Copy(BotScenario source)
    {
        var copy = new BotScenario();
        foreach (var property in typeof(BotScenario).GetProperties().Where(p => p.CanWrite))
            property.SetValue(copy, property.GetValue(source));
        return copy;
    }

    private static List<string> Validate(BotScenario row)
    {
        var errors = new RowErrors();
        row.Validate(errors);
        row.Validate(Data.Value.Tables, errors);
        return errors.Items.Select(e => e.Field + ": " + e.Message).ToList();
    }

    /// <summary>A warrior's step context at the city spawn with nothing connected, on one side of a pair (or none).</summary>
    private sealed class PairBot
    {
        public const uint OwnId = 7;
        public const uint PartnerId = 9;

        public PairBot(PairLink? pair, BotStepRegistry? registry = null, bool lootBags = false, bool stalls = false)
        {
            var data = Data.Value;
            var behaviour = data.Behaviour("m4-exit-a");
            var position = data.Markers["m1.spawn.combat.a"];
            var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), behaviour, data.Grid, position, 200, new Dictionary<int, int>(),
                new Pcg32Rng(1));
            Context = new BotStepContext(0, data, brain,
                new BotPerception { Self = position, Confirmed = position, NowMs = 1_000_000, ActionIdle = true },
                new EntityRegistry(PredictionSettings.From(data.Tables)), new ClientInventory(data.Tables), new BotOwnState(),
                new BotCounters(), registry ?? BotStepRegistry.Default(), message => { Sent.Add(message); return true; }, _ => { })
            {
                ClassId = "warrior",
                Pair = pair,
                Consumables = new ClientConsumables(data.Tables),
                Harvest = new ClientHarvest(data.Tables),
                Nodes = new ClientNodes(data.Tables),
                LootBags = lootBags ? new ClientLootBags() : null,
                Stalls = stalls ? new ClientStalls() : null,
            };
        }

        public BotStepContext Context { get; }

        /// <summary>The reliable requests the steps sent.</summary>
        public List<byte[]> Sent { get; } = [];

        public BotStepStatus Tick(IBotStep step)
        {
            var command = Context.Brain.Hold();
            return step.Tick(Context, ref command);
        }

        /// <summary>The own player and the partner <paramref name="partnerOffsetCm"/> east of it, in the client's registry from a snapshot.</summary>
        public void PlayersInSnapshot(int partnerOffsetCm, uint tick = 100)
        {
            int x = (int)GridPath.Cm(Context.View.Self.X), y = (int)GridPath.Cm(Context.View.Self.Y);
            var buffer = new byte[512];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = tick, OriginXCm = x, OriginZCm = y });
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = OwnId, Kind = EntityKind.Player, DefId = 1 }));
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = PartnerId, Kind = EntityKind.Player, DefId = 2 }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = OwnId, XCm = x, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = PartnerId, XCm = x + partnerOffsetCm, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            int size = writer.Finish();
            Assert.True(Context.Entities.ApplySnapshot(buffer.AsSpan(0, size), out _, out _, out _));
        }
    }
}
