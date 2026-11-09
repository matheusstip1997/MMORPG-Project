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

namespace GameServer.Tests.Bots;

/// <summary>
/// The loop's party and whisper steps (WP-M4-2, spec 6.5, 11.5) against a step context over the repository's tables, with the
/// server's answers applied to the kept <c>ClientParty</c> as the world client does: the inviter asks once and is done when the
/// committed state lists the partner; the invitee accepts only the partner's invite; a refusal fails with its reason; a party
/// line and a whisper are done on their echo; without a partner the pair steps are not available.
/// </summary>
public class PartyStepTests
{
    private const string Partner = "c2_0";
    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    [Fact]
    public void The_inviter_asks_once_by_name_and_is_done_when_the_state_lists_the_partner()
    {
        var bot = new Bot(Partner);
        var step = new PartyStep("invite");
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var ask = bot.Sent<PartyRequest>(0);
        Assert.Equal((PartyAction.Invite, Partner), (ask.Action, ask.TargetName));
        bot.Apply(ReliableMessageId.PartyResult, new PartyResult { RequestId = ask.RequestId, Action = PartyAction.Invite });
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // waits for the partner's accept
        Assert.Equal(1, bot.SentCount);

        bot.State(1, "c1_0", Partner);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal((1L, 1L), (bot.Counters.Loop["party.invited"], bot.Counters.Loop["party.joined"]));
    }

    [Fact]
    public void The_invitee_accepts_only_the_partners_invite_and_a_refusal_fails_the_step()
    {
        var bot = new Bot(Partner);
        var step = new PartyStep("accept");
        step.Begin(bot.Context);
        bot.Apply(ReliableMessageId.PartyInvite, new PartyInvite { InviteId = 4, InviterName = "stranger", TimeoutMs = 60_000 });
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(0, bot.SentCount);

        bot.Apply(ReliableMessageId.PartyInvite, new PartyInvite { InviteId = 5, InviterName = Partner, TimeoutMs = 60_000 });
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var accept = bot.Sent<PartyRequest>(0);
        Assert.Equal((PartyAction.Accept, 5u), (accept.Action, accept.InviteId));

        bot.Apply(ReliableMessageId.PartyResult, new PartyResult { RequestId = accept.RequestId, Action = PartyAction.Accept, Reason = PartyReason.PartyFull });
        Assert.Equal(BotStepStatus.Failed, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["party.refused.PartyFull"]);
    }

    [Fact]
    public void A_party_line_and_a_whisper_are_done_on_their_echo_and_count_what_the_partner_sent()
    {
        var bot = new Bot(Partner);
        bot.State(1, "c1_0", Partner);
        var chat = new PartyStep("chat");
        chat.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(chat));
        Assert.Equal(ChatChannel.Party, bot.SentChat(0).Channel);
        bot.Line(chat, ChatChannel.Party, bot.OwnId);
        bot.Line(chat, ChatChannel.Party, 77);
        Assert.Equal(BotStepStatus.Done, bot.Tick(chat));
        Assert.Equal((1L, 1L), (bot.Counters.Loop["party.chatSent"], bot.Counters.Loop["party.chatReceived"]));

        var whisper = new WhisperStep();
        whisper.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(whisper));
        Assert.Equal((ChatChannel.Whisper, Partner), (bot.SentChat(1).Channel, bot.SentChat(1).TargetName));
        bot.Line(whisper, ChatChannel.Whisper, 77);
        bot.Line(whisper, ChatChannel.Whisper, bot.OwnId, Partner);
        Assert.Equal(BotStepStatus.Done, bot.Tick(whisper));
        Assert.Equal((1L, 1L), (bot.Counters.Loop["whisper.sent"], bot.Counters.Loop["whisper.received"]));
    }

    [Fact]
    public void Leave_is_done_at_once_outside_a_party_and_the_pair_steps_need_a_partner()
    {
        var bot = new Bot(partner: null);
        var leave = new PartyStep("leave");
        leave.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Done, bot.Tick(leave));

        foreach (IBotStep step in new IBotStep[] { new PartyStep("invite"), new PartyStep("accept"), new WhisperStep() })
        {
            step.Begin(bot.Context);
            Assert.Equal(BotStepStatus.NotAvailable, bot.Tick(step));
        }
        Assert.Equal(0, bot.SentCount);
        Assert.Throws<ArgumentException>(() => new PartyStep("disband"));
    }

    /// <summary>A warrior's step context with nothing connected and its own entity known; requests are kept, answers applied.</summary>
    /// <summary>A pair link whose partner posted <paramref name="partner"/> as its name (WP-M4-12); null for a bot that plays alone.</summary>
    private static PairLink? WithPartner(string? partner)
    {
        if (partner == null)
            return null;
        var sides = MemoryPairBoard.NewPair();
        new PairLink(sides[1], isFirst: false).SayName(partner);
        return new PairLink(sides[0], isFirst: true);
    }

    private sealed class Bot
    {
        private readonly List<byte[]> _sent = [];
        private uint _version;

        public Bot(string? partner)
        {
            var data = Data.Value;
            var position = data.Markers["m1.npc.jobMaster"];
            var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), data.Behaviour("m2-loop"), data.Grid, position, 200,
                new Dictionary<int, int>(), new Pcg32Rng(1));
            Context = new BotStepContext(0, data, brain,
                new BotPerception { Self = position, Confirmed = position, NowMs = 1_000_000, ActionIdle = true },
                new EntityRegistry(PredictionSettings.From(data.Tables)), new ClientInventory(data.Tables), new BotOwnState(),
                new BotCounters(), BotStepRegistry.Default(), message => { _sent.Add(message); return true; }, _ => { })
            {
                ClassId = "warrior",
                Pair = WithPartner(partner), // WP-M4-12: the partner posts its name on the pair board
            };
            Own();
        }

        /// <summary>The own player in the client's registry from a snapshot (the first entity is the own one).</summary>
        private void Own()
        {
            int x = (int)GridPath.Cm(Context.View.Self.X), y = (int)GridPath.Cm(Context.View.Self.Y);
            var buffer = new byte[256];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 100, OriginXCm = x, OriginZCm = y });
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = 1, Kind = EntityKind.Player, DefId = 1 }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = 1, XCm = x, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            int size = writer.Finish();
            Assert.True(Context.Entities.ApplySnapshot(buffer.AsSpan(0, size), out _, out _, out _));
        }

        public BotStepContext Context { get; }

        public BotCounters Counters => Context.Counters;

        public int SentCount => _sent.Count;

        /// <summary>The own entity id (null: no snapshot applied, so any sender id is someone else's).</summary>
        public uint OwnId => Context.Entities.OwnEntityId ?? 0;

        public BotStepStatus Tick(IBotStep step)
        {
            var command = Context.Brain.Hold();
            return step.Tick(Context, ref command);
        }

        public void Apply<T>(ReliableMessageId id, T message) where T : class =>
            Assert.True(Context.Party.Apply(ReliableMessages.Write(id, message), Context.View.NowMs, out _));

        public void State(long partyId, params string[] names) => Apply(ReliableMessageId.PartyState, new PartyState
        {
            Version = ++_version, PartyId = partyId, LeaderEntityId = 1,
            Members = names.Select((name, i) => new PartyMemberState { EntityId = (uint)(i + 1), Name = name, Online = true, HpPercent = 100 }).ToArray(),
        });

        /// <summary>A chat line delivered to the step as the world client forwards it.</summary>
        public void Line(IBotStep step, ChatChannel channel, uint senderId, string target = "")
        {
            var bytes = ChatCodec.WriteLine(new ReliableMessageWriter(), new ChatLineMessage
            {
                SenderEntityId = senderId, SenderName = "x", Text = "hi", Channel = channel, TargetName = target,
            }).ToArray();
            step.OnReliable(Context, ReliableMessageId.ChatMessage, bytes);
        }

        public T Sent<T>(int index) where T : class
        {
            T? value = null;
            Assert.True(ReliableMessageReader.TryRead(_sent[index], ReliableMessages.PeekId(_sent[index]), ref value));
            return value!;
        }

        public ChatSendMessage SentChat(int index)
        {
            Assert.True(ChatCodec.TryReadSendMessage(_sent[index], out var message));
            return message;
        }
    }
}
