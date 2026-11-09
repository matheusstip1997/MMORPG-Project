using BotClient.Brain;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's whisper step (WP-M4-2; spec 6.5, 9.12, 11.5), one of a pair's steps (WP-M4-12): whispers one line to the partner by
/// its display name and is done on the echo the server sends back naming the partner; a whisper from the partner counts
/// <c>whisper.received</c> while the step runs. A refusal (<c>ChatTargetNotFound</c> while the partner is not online yet, a rate
/// limit) fails the step with <c>whisper.refused.&lt;reason&gt;</c>. Without a partner (<see cref="PairPartner"/>): "not available:
/// partner needed". Counters: <c>whisper.sent</c>, <c>whisper.received</c>.
/// </summary>
public sealed class WhisperStep : IBotStep
{
    private bool _sent;
    private bool _echoed;
    private ChatReason _refusal;

    public WhisperStep(string argument = "")
    {
    }

    public string Id => "whisper";

    public void Begin(BotStepContext context)
    {
        _sent = false;
        _echoed = false;
        _refusal = ChatReason.None;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (PairPartner.Name(context) is not { } partner)
            return context.NotAvailable(Id, "partner needed");
        if (_refusal != ChatReason.None)
        {
            context.Count("whisper.refused." + _refusal);
            return context.Failed($"whisper to {partner} refused: {_refusal}");
        }
        if (_echoed)
        {
            context.Count("whisper.sent");
            return BotStepStatus.Done;
        }
        if (!_sent)
        {
            _sent = context.SendReliable(ChatCodec.WriteSend(new ReliableMessageWriter(), new ChatSendMessage
            {
                Text = $"whisper from bot {context.BotIndex}", Channel = ChatChannel.Whisper, TargetName = partner,
            }).ToArray());
        }
        return BotStepStatus.Running;
    }

    public void OnReliable(BotStepContext context, ReliableMessageId id, ReadOnlySpan<byte> message)
    {
        if (id == ReliableMessageId.ChatRefused && _sent && !_echoed && ChatCodec.TryReadRefused(message, out var reason))
            _refusal = reason;
        if (id != ReliableMessageId.ChatMessage || !ChatCodec.TryReadLine(message, out var line) || line.Channel != ChatChannel.Whisper)
            return;
        if (line.SenderEntityId == context.Entities.OwnEntityId)
            _echoed = true;
        else
            context.Count("whisper.received");
    }
}
