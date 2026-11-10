using BotClient.Brain;
using Game.Gameplay.Entities;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's mail step (WP-M4-3; spec 4.5 M4 mail, 11.5): <c>mail:claim</c> claims every open mail into the bag through
/// ClientCore's <see cref="ClientMail"/>, one claim-all request at a time (the server takes the gold and what fits, oldest mail
/// first), while the open count it holds (<see cref="MailCount"/>, at enter world and on every change) is above 0. A claim is
/// answered after its commit: <c>mail.claimed</c> (mails emptied), <c>mail.gold</c>, <c>mail.units</c>, and <c>mail.partial</c>
/// when the bag left units in a mail; done when no open mail is left or the bag is full (<c>mail.bagFull</c>, not a failure:
/// the mail waits). Nothing to claim: <c>mail.none</c>, done in a loop and failed in a script, so an exit entry never passes
/// without a mail. Any other refusal fails with <c>mail.refused.&lt;reason&gt;</c>. Out of combat only (a claim never interrupts
/// combat). The argument must be <c>claim</c> (player mail is deferred: TODO(open)).
/// </summary>
public sealed class MailStep : IBotStep
{
    private readonly string _argument;
    private uint _pending;
    private int _claimed;

    public MailStep(string argument = "claim")
    {
        _argument = argument.Length == 0 ? "claim" : argument;
    }

    public string Id => "mail";

    /// <summary>The last committed answer, for the logs and tests.</summary>
    public MailResult? Answer { get; private set; }

    public void Begin(BotStepContext context)
    {
        _pending = 0;
        _claimed = 0;
        Answer = null;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_argument != "claim")
            return context.Failed($"mail:{_argument}: only mail:claim is known");
        if (context.Mail is not { } mail)
            return context.NotAvailable(Id, "no mail client");
        if (_pending != 0)
            return mail.ResultFor(_pending) is { } answer ? OnAnswer(context, answer) : BotStepStatus.Running;
        if (mail.Count == null)
            return BotStepStatus.Running; // the count comes at enter world
        if (mail.OpenCount == 0)
        {
            if (_claimed > 0)
                return BotStepStatus.Done;
            context.Count(Id + ".none");
            return context.Behaviour.LoopOnce ? context.Failed("no open mail to claim") : BotStepStatus.Done;
        }
        if (context.InCombat || context.View.Dead)
            return BotStepStatus.Running; // a claim never interrupts combat
        context.SendReliable(mail.ClaimRequest(0, out _pending));
        return BotStepStatus.Running;
    }

    private BotStepStatus OnAnswer(BotStepContext context, MailResult answer)
    {
        _pending = 0;
        Answer = answer;
        if (answer.Reason == MailReason.BagFull)
        {
            context.Count(Id + ".bagFull");
            context.Log("mail: the bag is full, the mail waits");
            return BotStepStatus.Done;
        }
        if (answer.Reason != MailReason.None)
        {
            context.Count(Id + ".refused." + answer.Reason);
            return context.Failed($"mail claim refused: {answer.Reason}");
        }
        _claimed += answer.MailsEmptied;
        context.Count(Id + ".claimed", answer.MailsEmptied);
        context.Count(Id + ".gold", (int)Math.Min(int.MaxValue, answer.GoldTaken));
        context.Count(Id + ".units", answer.Taken.Sum(t => t.Count));
        context.Log($"mail: claimed {answer.MailsEmptied} mails, {answer.GoldTaken} gold, {answer.Taken.Sum(t => t.Count)} units");
        if (answer.ItemsLeft > 0 || answer.GoldLeft > 0)
        {
            context.Count(Id + ".partial");
            return BotStepStatus.Done; // the bag is full: the rest waits in the mail
        }
        return BotStepStatus.Running; // the next tick claims the next mails while the count says some are open
    }
}
