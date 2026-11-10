#nullable enable
using System;
using System.Collections.Generic;
using Shared.Protocol;

namespace Game.UI.ViewModels.Mail
{
    /// <summary>
    /// The mail window (spec 4.5, 9.3, 9.13; WP-M4-3): the last mailbox list the server sent (oldest first), the open count and
    /// the cap of the M badge (only ever the server's <see cref="MailCount"/>: a claim waits for the next count, never counts
    /// down here), the selected mail and its content, and the one claim waiting for its answer. Display only: the server owns
    /// every mailbox and decides every claim; a refusal comes back as its reason code.
    /// </summary>
    public sealed class MailWindowViewModel : ViewModel
    {
        private MailHeader[] _mails = Array.Empty<MailHeader>();

        public bool Open { get; private set; }

        /// <summary>The listed mails, oldest first; empty before the first list.</summary>
        public IReadOnlyList<MailHeader> Mails => _mails;

        /// <summary>Every open mail, listed or not.</summary>
        public int Total { get; private set; }

        public bool HasList { get; private set; }

        /// <summary>
        /// The last list request was refused (rate limit, the mailbox still loading): the window says so; the rows, if any, are
        /// the last ones listed. The binder asks again once.
        /// </summary>
        public bool ListRefused { get; private set; }

        /// <summary>Open mails for the M badge (the server's <see cref="MailCount"/>); 0 before the first count.</summary>
        public int OpenCount { get; private set; }

        public int Cap { get; private set; }

        /// <summary>At or over the cap: new mail sources wait while settlements still arrive.</summary>
        public bool IsFull => Cap > 0 && OpenCount >= Cap;

        /// <summary>The selected mail's id; 0 for none.</summary>
        public long Selected { get; private set; }

        /// <summary>The selected mail's content as last sent; null before it came.</summary>
        public MailContent? Content { get; private set; }

        /// <summary>The claim waiting for its answer (after its commit); 0 for none, one at a time.</summary>
        public uint PendingClaim { get; private set; }

        public void SetOpen(bool open)
        {
            if (Open == open)
                return;
            Open = open;
            NotifyChanged();
        }

        public void Toggle() => SetOpen(!Open);

        public MailHeader? SelectedMail => Find(Selected);

        public bool CanClaim => PendingClaim == 0 && SelectedMail != null;

        public bool CanClaimAll => PendingClaim == 0 && _mails.Length > 0;

        /// <summary>The server's open count and cap (at enter world and on every change).</summary>
        public void SetCount(MailCount count)
        {
            if (OpenCount == count.Open && Cap == count.Cap)
                return;
            OpenCount = count.Open;
            Cap = count.Cap;
            NotifyChanged();
        }

        public void SetPendingClaim(uint requestId)
        {
            PendingClaim = requestId;
            NotifyChanged();
        }

        /// <summary>A list answer: the mailbox now. Returns the reason code of a refusal, else null. A selection whose mail is gone is cleared.</summary>
        public string? SetList(MailList list)
        {
            if (list.Reason != MailReason.None)
            {
                ListRefused = true;
                NotifyChanged();
                return list.Reason.ToString();
            }
            ListRefused = false;
            _mails = list.Mails;
            Total = list.Total;
            HasList = true;
            if (Selected != 0 && Find(Selected) == null)
            {
                Selected = 0;
                Content = null;
            }
            NotifyChanged();
            return null;
        }

        /// <summary>Selects a mail of the list (a second click clears it). True when a mail is now selected (the binder asks for its content).</summary>
        public bool Select(long mailId)
        {
            Selected = Selected == mailId || Find(mailId) == null ? 0 : mailId;
            Content = null;
            NotifyChanged();
            return Selected != 0;
        }

        /// <summary>A mail's content; kept when it is the selected one. Returns the reason code of a refusal, else null.</summary>
        public string? SetContent(MailContent content)
        {
            if (content.Reason != MailReason.None)
                return content.Reason.ToString();
            if (content.Header.MailId == Selected)
            {
                Content = content;
                NotifyChanged();
            }
            return null;
        }

        /// <summary>
        /// The answer to the pending claim: done. Returns the reason code of a refusal, else null; <paramref name="applied"/> for a
        /// claim that moved something (the list is asked for again; the count comes from the server).
        /// </summary>
        public string? OnResult(MailResult result, out bool applied)
        {
            applied = false;
            if (PendingClaim == 0 || result.RequestId != PendingClaim)
                return null;
            PendingClaim = 0;
            NotifyChanged();
            if (result.Reason != MailReason.None)
                return result.Reason.ToString();
            applied = true;
            return null;
        }

        /// <summary>Redraws the window (the expiry times count down from the client clock while it is open).</summary>
        public void Redraw() => NotifyChanged();

        /// <summary>The connection ended: nothing is known until the next count and list.</summary>
        public void Clear()
        {
            _mails = Array.Empty<MailHeader>();
            Total = 0;
            HasList = false;
            ListRefused = false;
            OpenCount = 0;
            Cap = 0;
            Selected = 0;
            Content = null;
            PendingClaim = 0;
            Open = false;
            NotifyChanged();
        }

        private MailHeader? Find(long mailId)
        {
            if (mailId == 0)
                return null;
            foreach (var mail in _mails)
            {
                if (mail.MailId == mailId)
                    return mail;
            }
            return null;
        }
    }
}
