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
    internal static readonly string[] M4StepIds = ["meet", "serve", "party", "whisper", "trade", "mail", "list", "stall", "die", "recoverBag", "contract"];

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
        // is the partner. FindInView and TryFind share one rule, the lowest entity id (WP-M4-2's /trade, #176).
        bot.Context.Identities.Set(PairBot.PartnerId - 1, "Botbeta");
        Assert.True(bot.Context.TryPartnerEntity(out id, out _));
        Assert.Equal(PairBot.PartnerId, id);
        Assert.True(bot.Context.Identities.TryFind("Botbeta", out uint lowest));
        Assert.Equal(PairBot.PartnerId - 1, lowest);
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
        var (a, b) = Pair();
        var bot = new PairBot(a);
        var step = new MeetStep("start");
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // no name or position: holds
        b.SayName("Botbeta");
        var far = bot.Context.View.Self;
        b.PostPosition((int)GridPath.Cm(far.X) + 2_000, (int)GridPath.Cm(far.Y));
        var command = bot.Context.Brain.Hold();
        Assert.Equal(BotStepStatus.Running, step.Tick(bot.Context, ref command));
        Assert.NotEqual(bot.Context.Brain.Hold().Keys, command.Keys); // walks to the posted position
        Assert.False(b.Board.TryReadPartner("meet.start", out _));

        // Beside the partner: arrives, then waits for it.
        b.PostPosition((int)GridPath.Cm(far.X) + 100, (int)GridPath.Cm(far.Y));
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(b.Board.TryReadPartner("meet.start", out string arrived));
        Assert.Equal("1", arrived);
        b.Arrive("start");
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Context.Counters.Loop["meet.met"]);
    }

    [Fact]
    public void Meet_fails_when_the_walk_to_the_partner_gets_no_closer_for_pairWaitMs()
    {
        // Review finding 7: a partner the bot can never reach (here the bot does not move) ends the step with a reason.
        var (a, b) = Pair();
        var bot = new PairBot(a);
        b.SayName("Botfar");
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
    [InlineData("list:m1.demonAsh:15", "WP-M4-7")]
    [InlineData("buy:listing:m1.demonAsh", "WP-M4-7")]
    [InlineData("stall:open:m1.demonAsh:20", "WP-M4-8")]
    [InlineData("buy:stall", "WP-M4-8")]
    [InlineData("contract:craft", "WP-M4-10")]
    public void An_M4_placeholder_is_not_yet_naming_its_WP(string entry, string wp)
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
        string[] m4Placeholders = ["list", "stall", "contract"];
        foreach (var outcome in played.Outcomes)
        {
            string entry = outcome.Entry;
            BotBehaviour.TryParseLoopStep(entry, out string id, out string argument);
            bool m4 = m4Placeholders.Contains(id) || entry == "buy:stall" || argument.StartsWith(BuyStep.ListingPrefix, StringComparison.Ordinal);
            if (m4)
                Assert.True(outcome.Outcome == StepOutcome.NotYet && outcome.Reason.Contains("WP-M4-", StringComparison.Ordinal), $"{entry}: {outcome}");
            else if (entry == "pickup:roll") // WP-M4-6 (real): nothing connected forms no party, failed in a script
                Assert.True(outcome.Outcome == StepOutcome.Failed && outcome.Reason.Contains("no party", StringComparison.Ordinal), $"{entry}: {outcome}");
            else if (id is "mail") // WP-M4-3 (real): nothing connected holds no mail (this context has no mail client)
                Assert.True(outcome.Outcome is StepOutcome.NotYet or StepOutcome.Failed && outcome.Reason.Length > 0, $"{entry}: {outcome}");
            else if (id is "trade") // WP-M4-4 (real): the partner is never in the snapshots with nothing connected
                Assert.True(outcome.Outcome == StepOutcome.NotYet && outcome.Reason.Contains("partner needed", StringComparison.Ordinal), $"{entry}: {outcome}");
            else if (id is "party") // WP-M4-2 (real): the partner posted its name, nothing connected answers the invite
                Assert.Equal(StepOutcome.TimedOut, outcome.Outcome);
            else if (id is "die" or "recoverBag") // WP-M4-9 (real): this context has no loot bag client
                Assert.True(outcome.Outcome == StepOutcome.NotYet && outcome.Reason.Contains("no loot bag client", StringComparison.Ordinal), $"{entry}: {outcome}");
            else if (entry == "sell:stolen") // WP-M4-9 (real): nothing was stolen with nothing connected
                Assert.True(outcome.Outcome == StepOutcome.Failed && outcome.Reason.Contains("no stolen", StringComparison.Ordinal), $"{entry}: {outcome}");
            else if (id is "meet" or "level" or "takeJob" or "fight" or "pickup" or "sell")
                Assert.True(outcome.Outcome == StepOutcome.Done, $"{entry}: {outcome}");
            else if (id == "gather") // the real step (WP-M3-5): no JobState with nothing connected, failed in a script
                Assert.True(outcome.Outcome == StepOutcome.Failed && outcome.Reason.Contains("gather.noJob", StringComparison.Ordinal), $"{entry}: {outcome}");
            else if (id == "buy")
                Assert.True(outcome.Outcome is StepOutcome.Failed or StepOutcome.NotYet && outcome.Reason.Length > 0, $"{entry}: {outcome}");
            else
                Assert.Fail($"{entry}: no expectation ({outcome})");
        }
        Assert.Equal(4, bots[index].Context.Counters.Loop["meet.met"]); // meet:start, meet:trade, meet:bag and meet:stolen (WP-M4-9)
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
        foreach (var leg in new[] { "party:invite", "party:accept", "pickup:roll", "trade:", "list:", "buy:listing:", "stall:open:", "buy:stall",
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

        public PairBot(PairLink? pair, BotStepRegistry? registry = null)
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
        public void PlayersInSnapshot(int partnerOffsetCm)
        {
            int x = (int)GridPath.Cm(Context.View.Self.X), y = (int)GridPath.Cm(Context.View.Self.Y);
            var buffer = new byte[512];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 100, OriginXCm = x, OriginZCm = y });
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = OwnId, Kind = EntityKind.Player, DefId = 1 }));
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = PartnerId, Kind = EntityKind.Player, DefId = 2 }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = OwnId, XCm = x, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = PartnerId, XCm = x + partnerOffsetCm, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            int size = writer.Finish();
            Assert.True(Context.Entities.ApplySnapshot(buffer.AsSpan(0, size), out _, out _, out _));
        }
    }
}
