using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items.Mail;
using GameServer.Net;
using GameServer.Outposts;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.World;

/// <summary>
/// System mail in the world (spec 4.5 M4 mail, 9.3; WP-M4-3; claimed at a mailbox, WP-M4-14): <see cref="MailService"/> keeps the attached characters'
/// mailboxes; the world reads the mail requests in phase 1 (rate limited by <c>MailSettings</c>, spec 7.5), answers a list or a
/// mail's content at once (or after a read of the mail worker), applies the worker's reads and expired mails, publishes
/// <c>MailSent</c>, <c>MailClaimed</c> and <c>MailExpired</c> once committed, and sends the answers and the M badge's count in
/// phase 10. Without <see cref="EnableMail"/> (worlds built for tests, a config without <c>MailSettings</c>) there is no mail.
/// </summary>
public sealed partial class GameWorld
{
    private MailService? _mail;
    private IMailJobs? _mailJobs;
    private MailSender? _mailSender;
    private readonly Dictionary<long, List<(int Connection, uint RequestId)>> _mailListWaiting = new();
    private readonly HashSet<long> _mailReloads = new();

    /// <summary>A read refused because mail writes of the character wait for their save: asked again once they committed.</summary>
    private readonly HashSet<long> _mailReloadAfterSave = new();

    /// <summary>A read failed (the database): the box may be stale, so the next list or arrival reads again, never a loop.</summary>
    private readonly HashSet<long> _mailStale = new();

    /// <summary>The mailboxes of the world's characters (WP-M4-3); null until <see cref="EnableMail"/>.</summary>
    public MailService? Mail => _mail;

    /// <summary>
    /// Turns mail on (before the <c>SessionManager</c> is built, which wires its saves): <paramref name="jobs"/> reads mailboxes
    /// and runs the expiry job off the tick (null: lists answer from memory and nothing expires here). Needs the item tables.
    /// </summary>
    public void EnableMail(MailRules rules, IMailJobs? jobs)
    {
        if (_inventory == null)
            return;
        _mailJobs = jobs;
        _mailSender = new MailSender(_transport);
        _mail = new MailService(rules, _inventory, () => _clock.UtcNowMs)
        {
            ClaimCommitted = OnMailClaimCommitted,
            SendCommitted = OnMailSendCommitted,
            CountChanged = OnMailCountChanged,
            ReloadWanted = RequestMailReload,
            Settled = OnMailSettled,
        };
        if (_escrow != null)
            _escrow.ReleasedToMail = RequestMailReload; // an escrow released to mail (WP-M4-4): an online recipient reads again
        if (Outposts != null && !Outposts.IsRegistered(NpcInteraction.Mail))
            Outposts.Register(NpcInteraction.Mail, new MailboxInteraction()); // WP-M4-14: the mailbox opens the mail window
    }

    /// <summary>Phase 1: a mail request of <paramref name="player"/>; false when the request is not a mail request.</summary>
    private bool HandleMailRequest(Player player, in ClientRequest request)
    {
        bool open = request.Kind == ReliableMessageId.MailOpenRequest;
        if (!open && request.Kind != ReliableMessageId.MailClaimRequest)
            return false;
        long now = _clock.UtcNowMs;
        if (_mail == null || !_mail.IsAttached(player.CharacterId))
        {
            Refuse(player, open, request, MailReason.InventoryUnavailable);
            return true;
        }
        var settings = _mail.Rules.Settings;
        if (!player.MailBucket.TryTake(now, settings.Burst, settings.RequestsPerPeriod, settings.PeriodMs))
        {
            Refuse(player, open, request, MailReason.MailRateLimited);
            return true;
        }
        if (open)
        {
            if (request.InstanceId == 0)
                ListMail(player, request.RequestId);
            else
                player.MailMessages.Add(ReliableMessages.Write(ReliableMessageId.MailContent,
                    MailViews.Content(_mail, player.CharacterId, request.RequestId, request.InstanceId, now)));
            return true;
        }
        // A claim never interrupts combat (M4 plan risk "souls-like feel"), nor an action under way.
        bool blocked = _inventory == null || CombatState.IsInCombat(player.LastCombatAtMs, now, _inventory.Rules.CombatStateMs)
                       || player.Actions.IsBusy;
        // R-4.5-03 (M4 decision 6a, WP-M4-14): the attachments are taken at a mailbox the player sees and stands in reach of,
        // checked at every claim (walking away after the mailbox opened the window refuses it); the list is read anywhere.
        bool atMailbox = Outposts != null
                         && Outposts.CheckReach(player, request.DropEntityId, NpcInteraction.Mail, out _) == OutpostReason.None;
        var ask = new MailClaimAsk(request.RequestId, request.InstanceId, blocked, player.Connection,
            $"mail:{_runId}:{player.Id}:{player.Connection}:{request.RequestId}", atMailbox);
        if (_mail.Claim(player.CharacterId, ask) is { } answer)
            player.MailMessages.Add(ReliableMessages.Write(ReliableMessageId.MailResult, answer));
        return true;
    }

    private static void Refuse(Player player, bool open, in ClientRequest request, MailReason reason)
    {
        if (!open)
            player.MailMessages.Add(ReliableMessages.Write(ReliableMessageId.MailResult,
                new MailResult { RequestId = request.RequestId, MailId = request.InstanceId, Reason = reason }));
        else if (request.InstanceId == 0)
            player.MailMessages.Add(ReliableMessages.Write(ReliableMessageId.MailList,
                new MailList { RequestId = request.RequestId, Reason = reason }));
        else
            player.MailMessages.Add(ReliableMessages.Write(ReliableMessageId.MailContent,
                new MailContent { RequestId = request.RequestId, Reason = reason }));
    }

    /// <summary>The list: from memory when every open mail is loaded, otherwise after a read of the mail worker.</summary>
    private void ListMail(Player player, uint requestId)
    {
        if ((_mail!.IsComplete(player.CharacterId) && !_mailStale.Contains(player.CharacterId)) || _mailJobs == null)
        {
            player.MailMessages.Add(ReliableMessages.Write(ReliableMessageId.MailList, MailViews.List(_mail, player.CharacterId, requestId)));
            return;
        }
        if (!_mailListWaiting.TryGetValue(player.CharacterId, out var waiting))
            _mailListWaiting[player.CharacterId] = waiting = new List<(int, uint)>();
        waiting.Add((player.Connection, requestId));
        RequestMailReload(player.CharacterId);
    }

    /// <summary>Asks the mail worker for a fresh read of an attached character's mailbox (one at a time per character).</summary>
    private void RequestMailReload(long characterId)
    {
        if (_mail == null || !_mail.IsAttached(characterId))
            return;
        if (_mailJobs == null)
        {
            AnswerWaitingLists(characterId);
            return;
        }
        if (_mailReloads.Add(characterId))
            _mailJobs.RequestReload(characterId, _mail.Version(characterId));
    }

    /// <summary>Phase 1 (after the commands): the mail worker's reads and the mails its expiry job closed.</summary>
    private void DrainMail()
    {
        if (_mail == null || _mailJobs == null)
            return;
        while (_mailJobs.TryTakeReloaded(out var reloaded))
        {
            long id = reloaded.CharacterId;
            _mailReloads.Remove(id);
            if (reloaded.Record == null)
            {
                // The read failed: lists answer from memory now; the next list request or arrival reads again (bounded by the
                // requests themselves, so a dead database is not hammered).
                if (_mail.IsAttached(id))
                    _mailStale.Add(id);
                AnswerWaitingLists(id);
                continue;
            }
            if (_mail.ApplyReload(id, reloaded.Record, reloaded.Version))
            {
                _mailStale.Remove(id);
                AnswerWaitingLists(id);
            }
            else if (_mail.IsAttached(id))
            {
                // Refused: a claim or send waits for its save (the read may predate it: ask again once it committed), or the box
                // changed since the read was asked (ask again now). A list waiting for it keeps waiting.
                if (_mail.IsSaving(id))
                    _mailReloadAfterSave.Add(id);
                else
                    RequestMailReload(id);
            }
            else
                _mailListWaiting.Remove(id);
        }
        while (_mailJobs.TryTakeExpired(out var expired))
        {
            // Published once, by the world whose job closed it, whether the recipient is online or not (a sink).
            _events?.Publish(new MailExpired(expired.RecipientId, expired.MailId, expired.Kind, _mail.Counts(expired.Items), expired.Gold),
                expired.RecipientId);
            _mail.ApplyExpired(expired);
        }
    }

    /// <summary>A save with mail writes of the character committed: a read refused while it was pending is asked again.</summary>
    private void OnMailSettled(long characterId)
    {
        if (_mailReloadAfterSave.Remove(characterId))
            RequestMailReload(characterId);
    }

    /// <summary>A stand-alone send (the admin panel) wrote a mail: an online recipient's mailbox reads again.</summary>
    private void OnMailArrived(long characterId) => RequestMailReload(characterId);

    private void AnswerWaitingLists(long characterId)
    {
        if (!_mailListWaiting.Remove(characterId, out var waiting) || _mail == null)
            return;
        if (!_playersByCharacter.TryGetValue(characterId, out var player))
            return;
        foreach (var (connection, requestId) in waiting)
        {
            if (connection == player.Connection)
                player.MailMessages.Add(ReliableMessages.Write(ReliableMessageId.MailList, MailViews.List(_mail, characterId, requestId)));
        }
    }

    /// <summary>A save committed a claim (rule 5): the bus, then the answer to the connection that asked.</summary>
    private void OnMailClaimCommitted(long characterId, CommittedMailClaim claim)
    {
        _playersByCharacter.TryGetValue(characterId, out var player);
        foreach (var claimed in claim.Claims)
            _events?.Publish(claimed, characterId, player?.RegionIndex ?? 0);
        if (player != null && player.Connection == claim.Connection)
            player.MailMessages.Add(ReliableMessages.Write(ReliableMessageId.MailResult, claim.Answer));
    }

    /// <summary>A save committed a send: the bus (the recipient as the actor).</summary>
    private void OnMailSendCommitted(MailSent sent) => _events?.Publish(sent, sent.ToCharacterId);

    private void OnMailCountChanged(long characterId)
    {
        if (_playersByCharacter.TryGetValue(characterId, out var player))
            player.MailCountDirty = true;
    }
}
