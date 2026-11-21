using BotClient.Brain;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The friend step (spec 6.5 R-6.5-04, WP-M4-16), a pair step (QA M4 wave 2 gap a: the smoke's <c>friend-request</c> sees the SO
/// badge count a pending request): <c>friend:partner</c> asks the partner for friendship by its display name (a
/// <c>SocialRequest</c> <c>AddFriend</c> through ClientCore's <c>ClientSocial</c>, <c>context.Social</c>) and is done once the
/// server answered it without a refusal (the request is saved and answered after its commit). It never waits for the partner's
/// answer: the other side accepts, declines or leaves it. A refusal fails the step with <c>friend.refused.&lt;reason&gt;</c>.
/// Without a partner (<see cref="PairPartner"/>): "not available: partner needed". Counter: <c>friend.requested</c>.
/// </summary>
public sealed class FriendStep : IBotStep
{
    private uint _pending;

    public FriendStep(string argument = "")
    {
        if (argument != "partner")
            throw new ArgumentException("friend step: the argument is partner");
    }

    public string Id => "friend";

    public void Begin(BotStepContext context) => _pending = 0;

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (PairPartner.Name(context) is not { } partner)
            return context.NotAvailable(Id, "partner needed");
        if (_pending == 0)
        {
            context.SendReliable(context.Social.Request(SocialAction.AddFriend, 0, partner, out _pending));
            return BotStepStatus.Running;
        }
        if (context.Social.ResultFor(_pending) is not { } answer)
            return BotStepStatus.Running;
        if (answer.Reason != SocialReason.None)
        {
            context.Count("friend.refused." + answer.Reason);
            return context.Failed($"friend request to {partner} refused: {answer.Reason}");
        }
        context.Count("friend.requested");
        return BotStepStatus.Done;
    }

    public void OnReliable(BotStepContext context, ReliableMessageId id, ReadOnlySpan<byte> message)
    {
    }
}
