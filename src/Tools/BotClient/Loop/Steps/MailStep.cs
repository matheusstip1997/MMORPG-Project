using BotClient.Brain;
using Game.Gameplay.Entities;
using Shared.Data.Tables;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's mail step (WP-M4-3, WP-M4-14; spec 4.5 M4 mail, R-4.5-03, 11.5), through ClientCore's <see cref="ClientMail"/>.
/// <para>
/// <c>mail:claim</c> claims every open mail into the bag at a mailbox (M4 decision 6a: the list is read anywhere, the attachments
/// are taken at a mailbox): it walks to the nearest mailbox (<see cref="BotStepContext.ApproachNpc"/>, an entity in interest or
/// the map's npc marker), then sends one claim-all request at a time naming it (the server takes the gold and what fits, oldest
/// mail first), while the open count it holds (<see cref="MailCount"/>, at enter world and on every change) is above 0. A claim
/// is answered after its commit: <c>mail.claimed</c> (mails emptied), <c>mail.gold</c>, <c>mail.units</c>, and
/// <c>mail.partial</c> when the bag left units in a mail; done when no open mail is left or the bag is full (<c>mail.bagFull</c>,
/// not a failure: the mail waits). A <c>MailboxNotInReach</c> refusal (the server had the bot a step farther) walks on and asks
/// again. Nothing to claim: <c>mail.none</c>; no mailbox in the world: <c>mail.noMailbox</c>; both done in a loop and failed in
/// a script, so an exit entry never passes without a claim. Any other refusal fails with <c>mail.refused.&lt;reason&gt;</c>. Out
/// of combat only (a claim never interrupts combat).
/// </para>
/// <para>
/// <c>mail:read</c> reads the mail where the bot stands, in the field or the city: the list, then the oldest mail. Away from a
/// mailbox it then sends one claim of that mail, which the server must refuse with <c>MailboxNotInReach</c>
/// (<c>mail.awayRefused</c>; any other answer fails, <c>mail.awayClaimed</c>: a claim must never pass away from a mailbox). At a
/// mailbox it skips that probe (<c>mail.readAtMailbox</c>). Nothing to read: <c>mail.none</c> as for <c>claim</c>.
/// </para>
/// Player mail is deferred: TODO(open).
/// </summary>
public sealed class MailStep : IBotStep
{
    private enum Phase { Start, Listing, Opening, Read, Probing, Done }

    private readonly string _argument;
    private uint _pending;
    private int _claimed;
    private uint _mailbox;
    private Phase _phase;
    private long _readMailId;

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
        _mailbox = 0;
        _phase = Phase.Start;
        _readMailId = 0;
        Answer = null;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_argument is not ("claim" or "read"))
            return context.Failed($"mail:{_argument}: only mail:claim and mail:read are known");
        if (context.Mail is not { } mail)
            return context.NotAvailable(Id, "no mail client");
        return _argument == "read" ? Read(context, mail) : Claim(context, mail, ref command);
    }

    private BotStepStatus Claim(BotStepContext context, ClientMail mail, ref BotCommand command)
    {
        if (_pending != 0)
            return mail.ResultFor(_pending) is { } answer ? OnAnswer(context, answer) : BotStepStatus.Running;
        if (mail.Count == null)
            return BotStepStatus.Running; // the count comes at enter world
        if (mail.OpenCount == 0)
            return _claimed > 0 ? BotStepStatus.Done : Nothing(context, "none", "no open mail to claim");
        if (context.InCombat || context.View.Dead)
            return BotStepStatus.Running; // a claim never interrupts combat
        switch (context.ApproachNpc(npc => npc.Offers(NpcInteraction.Mail), ref command, out _mailbox, out _))
        {
            case NpcApproach.None:
                return Nothing(context, "noMailbox", "no mailbox to claim the mail at");
            case NpcApproach.Walking:
                return BotStepStatus.Running;
        }
        context.SendReliable(mail.ClaimRequest(0, _mailbox, out _pending));
        return BotStepStatus.Running;
    }

    private BotStepStatus Read(BotStepContext context, ClientMail mail)
    {
        switch (_phase)
        {
            case Phase.Start:
                if (mail.Count == null)
                    return BotStepStatus.Running;
                if (mail.OpenCount == 0)
                    return Nothing(context, "none", "no open mail to read");
                context.SendReliable(mail.ListRequest(out _pending));
                _phase = Phase.Listing;
                return BotStepStatus.Running;

            case Phase.Listing:
                if (mail.ListFor(_pending) is not { } list)
                    return BotStepStatus.Running;
                if (list.Reason != MailReason.None)
                    return Refused(context, list.Reason, "the mail list");
                if (list.Mails.Length == 0)
                    return Nothing(context, "none", "no open mail to read");
                _readMailId = list.Mails[0].MailId;
                context.SendReliable(mail.OpenRequest(_readMailId, out _pending));
                _phase = Phase.Opening;
                return BotStepStatus.Running;

            case Phase.Opening:
                if (mail.ContentFor(_pending) is not { } content)
                    return BotStepStatus.Running;
                if (content.Reason != MailReason.None)
                    return Refused(context, content.Reason, "a mail");
                context.Count(Id + ".read");
                context.Log($"mail: read mail {_readMailId}, {content.Items.Length} attachments");
                _phase = Phase.Read;
                goto case Phase.Read;

            case Phase.Read:
                if (context.NpcInReach(npc => npc.Offers(NpcInteraction.Mail)))
                {
                    context.Count(Id + ".readAtMailbox");
                    return BotStepStatus.Done;
                }
                if (context.InCombat || context.View.Dead)
                    return BotStepStatus.Running; // the probe would be refused InCombat first
                context.SendReliable(mail.ClaimRequest(_readMailId, 0, out _pending)); // away: the server must refuse it
                _phase = Phase.Probing;
                return BotStepStatus.Running;

            case Phase.Probing:
                if (mail.ResultFor(_pending) is not { } probe)
                    return BotStepStatus.Running;
                _phase = Phase.Done;
                if (probe.Reason == MailReason.MailboxNotInReach)
                {
                    context.Count(Id + ".awayRefused");
                    context.Log("mail: read in the field, the claim there is refused (MailboxNotInReach)");
                    return BotStepStatus.Done;
                }
                context.Count(Id + ".awayClaimed");
                return context.Failed($"a claim away from a mailbox was answered {probe.Reason}, not MailboxNotInReach");

            default:
                return BotStepStatus.Done;
        }
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
        if (answer.Reason == MailReason.MailboxNotInReach)
        {
            context.Count(Id + ".refused." + answer.Reason);
            return BotStepStatus.Running; // the server had it a step farther: walk on and ask again
        }
        if (answer.Reason != MailReason.None)
            return Refused(context, answer.Reason, "mail claim");
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

    private BotStepStatus Nothing(BotStepContext context, string counter, string why)
    {
        context.Count(Id + "." + counter);
        return context.Behaviour.LoopOnce ? context.Failed(why) : BotStepStatus.Done;
    }

    private BotStepStatus Refused(BotStepContext context, MailReason reason, string what)
    {
        context.Count(Id + ".refused." + reason);
        return context.Failed($"{what} refused: {reason}");
    }
}
