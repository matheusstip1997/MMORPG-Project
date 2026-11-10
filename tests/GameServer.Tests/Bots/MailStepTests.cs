using BotClient;
using BotClient.Brain;
using BotClient.Loop;
using BotClient.Loop.Steps;
using BotClient.World;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Util;

namespace GameServer.Tests.Bots;

/// <summary>
/// The loop's mail step (WP-M4-3, spec 4.5, 11.5) against a step context over the repository's tables, with the server's
/// answers applied to the kept <c>ClientMail</c> as the world client does: <c>mail:claim</c> waits for the count, sends one
/// claim-all request, waits for the committed answer and claims again while mails are open; a full bag is done (the mail
/// waits), nothing to claim is done in a loop and failed in a script, a refusal fails with its reason.
/// </summary>
public class MailStepTests
{
    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    [Fact]
    public void Claim_sends_claim_all_and_waits_for_the_committed_answer_while_mails_are_open()
    {
        var bot = new Bot();
        var step = new MailStep("claim");
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // no count yet: it comes at enter world
        Assert.Equal(0, bot.SentCount);

        bot.Count(2);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(ReliableMessages.TryRead(bot.Raw(0), out MailClaimRequest request));
        Assert.Equal(0, request.MailId); // claim all
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // the answer comes after the commit
        Assert.Equal(1, bot.SentCount);

        bot.Answer(request.RequestId, MailReason.None, emptied: 2, gold: 50);
        bot.Count(0);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(BotStepStatus.Done, bot.Tick(step)); // nothing open any more
        Assert.Equal((2L, 50L), (bot.Counters.Loop["mail.claimed"], bot.Counters.Loop["mail.gold"]));
    }

    [Fact]
    public void A_full_bag_is_done_and_the_mail_waits()
    {
        var bot = new Bot();
        var step = new MailStep();
        step.Begin(bot.Context);
        bot.Count(1);
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
        refused.Tick(step);
        Assert.True(ReliableMessages.TryRead(refused.Raw(0), out MailClaimRequest request));
        refused.Answer(request.RequestId, MailReason.MailRateLimited);
        Assert.Equal(BotStepStatus.Failed, refused.Tick(step));
        Assert.Equal(1, refused.Counters.Loop["mail.refused.MailRateLimited"]);
    }

    /// <summary>A warrior's step context in the city with a mail client, nothing connected.</summary>
    private sealed class Bot
    {
        private readonly List<byte[]> _sent = [];

        public Bot(string behaviour = "m3-loop")
        {
            var data = Data.Value;
            var position = data.Markers["m1.npc.bench"];
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

        public void Count(int open) =>
            Assert.True(Context.Mail!.Apply(ReliableMessages.Write(ReliableMessageId.MailCount, new MailCount { Open = (ushort)open, Cap = 50 }),
                out _));

        public void Answer(uint requestId, MailReason reason, int emptied = 0, long gold = 0) =>
            Assert.True(Context.Mail!.Apply(ReliableMessages.Write(ReliableMessageId.MailResult, new MailResult
            {
                RequestId = requestId, Reason = reason, MailsEmptied = (ushort)emptied, GoldTaken = gold,
            }), out _));

        public byte[] Raw(int index) => _sent[index];
    }
}
