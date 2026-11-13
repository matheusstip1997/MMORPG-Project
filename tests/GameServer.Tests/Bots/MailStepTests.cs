using BotClient;
using BotClient.Brain;
using BotClient.Loop;
using BotClient.Loop.Steps;
using BotClient.World;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Util;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.Bots;

/// <summary>
/// The loop's mail step (WP-M4-3, WP-M4-14; spec 4.5, R-4.5-03, 11.5) against a step context over the repository's tables, with
/// the server's answers applied to the kept <c>ClientMail</c> as the world client does: <c>mail:claim</c> waits for the count,
/// walks to the mailbox (its marker, then its entity in reach), sends one claim-all request naming it, waits for the committed
/// answer and claims again while mails are open; a full bag is done (the mail waits), nothing to claim is done in a loop and
/// failed in a script, <c>MailboxNotInReach</c> walks on, another refusal fails with its reason. <c>mail:read</c> reads the list
/// and the oldest mail where it stands and, away from a mailbox, proves a claim there is refused.
/// </summary>
public class MailStepTests
{
    private const int OwnId = 1, MailboxId = 2;
    private const ushort MailboxNetId = 6; // NpcDef m1.mailbox
    private const long OldestMail = 40;

    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    [Fact]
    public void Claim_sends_claim_all_at_the_mailbox_and_waits_for_the_committed_answer_while_mails_are_open()
    {
        var bot = new Bot();
        var step = new MailStep("claim");
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // no count yet: it comes at enter world
        Assert.Equal(0, bot.SentCount);

        bot.Count(2);
        bot.Mailbox();
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(ReliableMessages.TryRead(bot.Raw(0), out MailClaimRequest request));
        Assert.Equal((0L, (uint)MailboxId), (request.MailId, request.NpcEntityId)); // claim all, at the mailbox
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // the answer comes after the commit
        Assert.Equal(1, bot.SentCount);

        bot.Answer(request.RequestId, MailReason.None, emptied: 2, gold: 50);
        bot.Count(0);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(BotStepStatus.Done, bot.Tick(step)); // nothing open any more
        Assert.Equal((2L, 50L), (bot.Counters.Loop["mail.claimed"], bot.Counters.Loop["mail.gold"]));
    }

    [Fact]
    public void Claim_walks_to_the_mailbox_marker_first_and_walks_on_after_MailboxNotInReach()
    {
        var bot = new Bot("m3-loop", Data.Value.Markers["m1.spawn.combat.a"]); // in the field
        var step = new MailStep("claim");
        step.Begin(bot.Context);
        bot.Count(1);

        var command = bot.Context.Brain.Hold();
        Assert.Equal(BotStepStatus.Running, step.Tick(bot.Context, ref command));
        Assert.Equal(0, bot.SentCount); // no mailbox in interest: it walks to the map's mailbox marker
        Assert.NotEqual(MoveKeys.None, command.Keys);

        bot.Mailbox();
        bot.Tick(step);
        Assert.True(ReliableMessages.TryRead(bot.Raw(0), out MailClaimRequest first));
        bot.Answer(first.RequestId, MailReason.MailboxNotInReach); // the server had it a step farther
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["mail.refused.MailboxNotInReach"]);
        bot.Tick(step);
        Assert.True(ReliableMessages.TryRead(bot.Raw(1), out MailClaimRequest second)); // asks again
        Assert.NotEqual(first.RequestId, second.RequestId);
    }

    [Fact]
    public void A_full_bag_is_done_and_the_mail_waits()
    {
        var bot = new Bot();
        var step = new MailStep();
        step.Begin(bot.Context);
        bot.Count(1);
        bot.Mailbox();
        bot.Tick(step);
        Assert.True(ReliableMessages.TryRead(bot.Raw(0), out MailClaimRequest request));

        bot.Answer(request.RequestId, MailReason.BagFull);

        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["mail.bagFull"]);
    }

    [Fact]
    public void Nothing_to_claim_is_done_in_a_loop_and_failed_in_a_script_and_a_refusal_fails()
    {
        var loop = new Bot();
        var step = new MailStep();
        step.Begin(loop.Context);
        loop.Count(0);
        Assert.Equal(BotStepStatus.Done, loop.Tick(step));
        Assert.Equal(1, loop.Counters.Loop["mail.none"]);

        var script = new Bot("m3-exit-jeweler");
        step = new MailStep();
        step.Begin(script.Context);
        script.Count(0);
        Assert.Equal(BotStepStatus.Failed, script.Tick(step));

        var refused = new Bot();
        step = new MailStep();
        step.Begin(refused.Context);
        refused.Count(1);
        refused.Mailbox();
        refused.Tick(step);
        Assert.True(ReliableMessages.TryRead(refused.Raw(0), out MailClaimRequest request));
        refused.Answer(request.RequestId, MailReason.MailRateLimited);
        Assert.Equal(BotStepStatus.Failed, refused.Tick(step));
        Assert.Equal(1, refused.Counters.Loop["mail.refused.MailRateLimited"]);

        var unknown = new Bot();
        Assert.Equal(BotStepStatus.Failed, unknown.Tick(Begun(new MailStep("send"), unknown.Context))); // unknown argument
    }

    [Fact]
    public void Read_in_the_field_reads_the_oldest_mail_and_a_claim_there_must_be_refused()
    {
        var bot = new Bot("m3-exit-jeweler", Data.Value.Markers["m1.spawn.combat.a"]);
        var step = Begun(new MailStep("read"), bot.Context);
        bot.Count(2);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(ReliableMessages.TryRead(bot.Raw(0), out MailOpenRequest list));
        Assert.Equal(0, list.MailId);
        bot.List(list.RequestId);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(ReliableMessages.TryRead(bot.Raw(1), out MailOpenRequest open));
        Assert.Equal(OldestMail, open.MailId);
        bot.Content(open.RequestId);
        bot.Context.View.LastCombatAtMs = bot.Context.View.NowMs; // a fight after the read: the probe waits it out
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(2, bot.SentCount);
        bot.Context.View.LastCombatAtMs = long.MinValue / 2;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(ReliableMessages.TryRead(bot.Raw(2), out MailClaimRequest probe)); // no mailbox in reach: the probe
        Assert.Equal((OldestMail, 0u), (probe.MailId, probe.NpcEntityId));

        bot.Answer(probe.RequestId, MailReason.MailboxNotInReach);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal((1L, 1L), (bot.Counters.Loop["mail.read"], bot.Counters.Loop["mail.awayRefused"]));

        // A claim that passes away from a mailbox is a bug the step reports.
        var leak = new Bot("m3-exit-jeweler", Data.Value.Markers["m1.spawn.combat.a"]);
        step = Begun(new MailStep("read"), leak.Context);
        leak.Count(1);
        leak.Tick(step);
        leak.List(1);
        leak.Tick(step);
        leak.Content(2);
        leak.Tick(step);
        leak.Answer(3, MailReason.None, emptied: 1);
        Assert.Equal(BotStepStatus.Failed, leak.Tick(step));
        Assert.Equal(1, leak.Counters.Loop["mail.awayClaimed"]);
    }

    [Fact]
    public void Read_at_a_mailbox_reads_without_the_probe()
    {
        var bot = new Bot();
        var step = Begun(new MailStep("read"), bot.Context);
        bot.Count(1);
        bot.Mailbox();
        bot.Tick(step);
        bot.List(1);
        bot.Tick(step);
        bot.Content(2);

        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(2, bot.SentCount); // the list and the mail, no claim
        Assert.Equal(1, bot.Counters.Loop["mail.readAtMailbox"]);
    }

    private static IBotStep Begun(IBotStep step, BotStepContext context)
    {
        step.Begin(context);
        return step;
    }

    /// <summary>A warrior's step context with a mail client, nothing connected; by the jeweler's bench in the city by default.</summary>
    private sealed class Bot
    {
        private readonly List<byte[]> _sent = [];

        public Bot(string behaviour = "m3-loop", FixVec2? at = null)
        {
            var data = Data.Value;
            var position = at ?? data.Markers["m1.npc.bench"];
            var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), data.Behaviour(behaviour), data.Grid, position, 200,
                new Dictionary<int, int>(), new Pcg32Rng(1));
            Context = new BotStepContext(0, data, brain,
                new BotPerception { Self = position, Confirmed = position, NowMs = 1_000_000, ActionIdle = true },
                new EntityRegistry(PredictionSettings.From(data.Tables)), new ClientInventory(data.Tables), new BotOwnState(),
                new BotCounters(), BotStepRegistry.Default(), message => { _sent.Add(message); return true; }, _ => { })
            {
                ClassId = "warrior",
                Mail = new ClientMail(),
            };
        }

        public BotStepContext Context { get; }

        public BotCounters Counters => Context.Counters;

        public int SentCount => _sent.Count;

        public BotStepStatus Tick(IBotStep step)
        {
            var command = Context.Brain.Hold();
            return step.Tick(Context, ref command);
        }

        /// <summary>A mailbox 50 cm from the bot, in the client's registry from a snapshot.</summary>
        public void Mailbox()
        {
            int x = (int)GridPath.Cm(Context.View.Self.X), y = (int)GridPath.Cm(Context.View.Self.Y);
            var buffer = new byte[512];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 100, OriginXCm = x, OriginZCm = y });
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = OwnId, Kind = EntityKind.Player, DefId = 1 }));
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = MailboxId, Kind = EntityKind.Npc, DefId = MailboxNetId }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = OwnId, XCm = x, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = MailboxId, XCm = x + 50, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            int size = writer.Finish();
            Assert.True(Context.Entities.ApplySnapshot(buffer.AsSpan(0, size), out _, out _, out _));
        }

        public void Count(int open) =>
            Assert.True(Context.Mail!.Apply(ReliableMessages.Write(ReliableMessageId.MailCount, new MailCount { Open = (ushort)open, Cap = 50 }),
                out _));

        public void List(uint requestId) =>
            Assert.True(Context.Mail!.Apply(ReliableMessages.Write(ReliableMessageId.MailList, new MailList
            {
                RequestId = requestId, Total = 2, Mails = [Header(OldestMail), Header(OldestMail + 1)],
            }), out _));

        public void Content(uint requestId) =>
            Assert.True(Context.Mail!.Apply(ReliableMessages.Write(ReliableMessageId.MailContent, new MailContent
            {
                RequestId = requestId, Header = Header(OldestMail),
            }), out _));

        public void Answer(uint requestId, MailReason reason, int emptied = 0, long gold = 0) =>
            Assert.True(Context.Mail!.Apply(ReliableMessages.Write(ReliableMessageId.MailResult, new MailResult
            {
                RequestId = requestId, Reason = reason, MailsEmptied = (ushort)emptied, GoldTaken = gold,
            }), out _));

        public byte[] Raw(int index) => _sent[index];

        private static MailHeader Header(long mailId) =>
            new() { MailId = mailId, Kind = MailKind.AdminCompensation, SubjectId = "mail.subject.adminCompensation", CreatedUtcMs = 1, ExpiresUtcMs = 2 };
    }
}
