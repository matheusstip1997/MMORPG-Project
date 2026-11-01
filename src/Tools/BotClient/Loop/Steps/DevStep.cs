using BotClient.Brain;
using Shared.Data.Tables;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// One dev console command (WP-M3-13, spec 11.7): <c>dev:give m3.oreT1 4</c> sends the chat line <c>/give m3.oreT1 4</c>, as
/// a player types it, and waits for the server's <see cref="DevCommandResult"/> (after the commit for a command that
/// changes the character). Done when the command ran, failed with its <see cref="DevReason"/> otherwise; the answer's lines
/// are logged, so a script is a transcript of a dev session. Dev lines go through the chat's rate limit: a
/// <c>ChatRateLimited</c> refusal waits <c>ChatSettings.periodMs / messagesPerPeriod</c> and sends again. Needs a server with
/// the dev console (a DevTools build with <c>MMORPG_DEV_TOOLS=1</c>). Counters: <c>dev.ran</c>, <c>dev.refused.&lt;reason&gt;</c>,
/// <c>dev.rateLimited</c>.
/// </summary>
public sealed class DevStep : IBotStep
{
    private readonly string _line;
    private bool _sent;
    private long _sendAtMs;
    private DevCommandResult? _answer;
    private ChatReason _chatRefusal;

    public DevStep(string argument = "")
    {
        _line = argument.Length > 0 ? "/" + argument : throw new ArgumentException("dev step: the argument is the command, e.g. dev:help");
    }

    public string Id => "dev";

    public void Begin(BotStepContext context)
    {
        _sent = false;
        _sendAtMs = 0;
        _answer = null;
        _chatRefusal = ChatReason.None;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_chatRefusal != ChatReason.None)
        {
            if (_chatRefusal != ChatReason.ChatRateLimited)
                return context.Failed($"{_line}: the chat refused it ({_chatRefusal})");
            context.Count("dev.rateLimited");
            var chat = context.Data.Tables.Single<ChatSettings>();
            _sendAtMs = context.View.NowMs + chat.PeriodMs / Math.Max(1, chat.MessagesPerPeriod);
            _chatRefusal = ChatReason.None;
            _sent = false;
        }
        if (!_sent)
        {
            if (context.View.NowMs < _sendAtMs)
                return BotStepStatus.Running;
            _sent = context.SendReliable(ChatCodec.WriteSend(new ReliableMessageWriter(), new ChatSendMessage { Text = _line }).ToArray());
            if (_sent)
                context.Log("> " + _line);
            return BotStepStatus.Running;
        }
        if (_answer == null)
            return BotStepStatus.Running;
        if (_answer.Reason != DevReason.None)
            context.Log($"  [{_answer.Reason}]");
        foreach (var line in _answer.Lines)
            context.Log("  " + line);
        if (_answer.Reason == DevReason.None)
        {
            context.Count("dev.ran");
            return BotStepStatus.Done;
        }
        context.Count("dev.refused." + _answer.Reason);
        return context.Failed($"{_line}: {_answer.Reason}");
    }

    public void OnReliable(BotStepContext context, ReliableMessageId id, ReadOnlySpan<byte> message)
    {
        if (!_sent || _answer != null)
            return;
        if (id == ReliableMessageId.DevCommandResult && ReliableMessages.TryRead(message, out DevCommandResult result))
            _answer = result;
        else if (id == ReliableMessageId.ChatRefused && ChatCodec.TryReadRefused(message, out var reason))
            _chatRefusal = reason;
    }
}
