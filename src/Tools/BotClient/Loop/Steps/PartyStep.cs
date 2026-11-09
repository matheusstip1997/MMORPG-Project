using BotClient.Brain;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's party step (WP-M4-2; spec 6.5 "M4 party", 11.5), one of a pair's steps (WP-M4-12). The argument names the part:
/// <list type="bullet">
/// <item><c>party:invite</c>: invites the partner by its display name; done once the committed <c>PartyState</c> lists both.</item>
/// <item><c>party:accept</c>: waits for the partner's <c>PartyInvite</c> (the server's message, never a board), accepts it, and is
/// done on the same <c>PartyState</c>.</item>
/// <item><c>party:leave</c>: leaves the party; done once the state shows none (or at once when there is none).</item>
/// <item><c>party:chat</c>: sends a party line and is done on its echo; a party line from someone else counts
/// <c>party.chatReceived</c> while the step runs.</item>
/// </list>
/// Everything comes from ClientCore's <c>ClientParty</c> (<c>context.Party</c>) and the server's answers; a refusal fails the step
/// with <c>party.refused.&lt;reason&gt;</c>. Counters: <c>party.invited</c>, <c>party.joined</c>, <c>party.left</c>,
/// <c>party.chatSent</c>, <c>party.chatReceived</c>. Without a partner (<see cref="PairPartner"/>) the invite and the accept
/// are "not available: partner needed".
/// </summary>
public sealed class PartyStep : IBotStep
{
    private readonly string _part;
    private uint _pending;
    private bool _echoed;
    private bool _sent;

    public PartyStep(string argument = "")
    {
        _part = argument is "invite" or "accept" or "leave" or "chat"
            ? argument : throw new ArgumentException("party step: the argument is invite, accept, leave or chat");
    }

    public string Id => "party";

    public void Begin(BotStepContext context)
    {
        _pending = 0;
        _echoed = false;
        _sent = false;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) => _part switch
    {
        "invite" => Invite(context),
        "accept" => Accept(context),
        "leave" => Leave(context),
        _ => Chat(context),
    };

    public void OnReliable(BotStepContext context, ReliableMessageId id, ReadOnlySpan<byte> message)
    {
        if (id != ReliableMessageId.ChatMessage || !ChatCodec.TryReadLine(message, out var line) || line.Channel != ChatChannel.Party)
            return;
        if (line.SenderEntityId == context.Entities.OwnEntityId)
            _echoed = true;
        else
            context.Count("party.chatReceived");
    }

    private BotStepStatus Invite(BotStepContext context)
    {
        if (PairPartner.Name(context) is not { } partner)
            return context.NotAvailable(Id, "partner needed");
        if (HasMember(context, partner))
        {
            context.Count("party.joined");
            return BotStepStatus.Done;
        }
        if (_pending == 0)
        {
            context.SendReliable(context.Party.Request(PartyAction.Invite, out _pending, targetName: partner));
            return BotStepStatus.Running;
        }
        if (Refused(context) is { } failed)
            return failed;
        if (context.Party.LastResult is { RequestId: var answered, Reason: PartyReason.None } && answered == _pending && !_sent)
        {
            _sent = true;
            context.Count("party.invited");
        }
        return BotStepStatus.Running; // the partner's accept forms the party
    }

    private BotStepStatus Accept(BotStepContext context)
    {
        if (PairPartner.Name(context) is not { } partner)
            return context.NotAvailable(Id, "partner needed");
        if (HasMember(context, partner))
        {
            context.Count("party.joined");
            return BotStepStatus.Done;
        }
        if (_pending != 0)
            return Refused(context) ?? BotStepStatus.Running;
        if (context.Party.Invite(context.View.NowMs) is { } invite
            && string.Equals(invite.InviterName, partner, StringComparison.OrdinalIgnoreCase))
            context.SendReliable(context.Party.Request(PartyAction.Accept, out _pending, inviteId: invite.InviteId));
        return BotStepStatus.Running;
    }

    private BotStepStatus Leave(BotStepContext context)
    {
        if (!context.Party.InParty)
        {
            if (_pending != 0)
                context.Count("party.left");
            return BotStepStatus.Done;
        }
        if (_pending == 0)
        {
            context.SendReliable(context.Party.Request(PartyAction.Leave, out _pending));
            return BotStepStatus.Running;
        }
        return Refused(context) ?? BotStepStatus.Running;
    }

    private BotStepStatus Chat(BotStepContext context)
    {
        if (!context.Party.InParty)
            return context.Failed("party:chat outside a party");
        if (_echoed)
        {
            context.Count("party.chatSent");
            return BotStepStatus.Done;
        }
        if (!_sent)
        {
            _sent = context.SendReliable(ChatCodec.WriteSend(new ReliableMessageWriter(), new ChatSendMessage
            {
                Text = $"party check from bot {context.BotIndex}", Channel = ChatChannel.Party,
            }).ToArray());
        }
        return BotStepStatus.Running;
    }

    /// <summary>The failed status when the pending request was refused; null while it waits or after a yes.</summary>
    private BotStepStatus? Refused(BotStepContext context)
    {
        if (context.Party.LastResult is not { } answer || answer.RequestId != _pending || answer.Reason == PartyReason.None)
            return null;
        context.Count("party.refused." + answer.Reason);
        _pending = 0;
        return context.Failed($"party:{_part} refused: {answer.Reason}");
    }

    private static bool HasMember(BotStepContext context, string name)
    {
        if (!context.Party.InParty)
            return false;
        foreach (var member in context.Party.State!.Members)
        {
            if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
