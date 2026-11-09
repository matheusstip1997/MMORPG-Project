using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// System mail rules (spec 4.5 M4 mail, M4 decision 4; spec 7.5 rate limits; WP-M4-3). The server writes mails with
    /// items and gold attached; the player claims them into the Normal bag from anywhere as one class 1 operation; open
    /// mails expire after <see cref="ExpiryDays"/> and their attachments are destroyed (a sink). <see cref="MailboxCap"/>
    /// gates new mail sources (a listing, a stall, a contract proposal, an admin compensation are refused
    /// <c>MailboxFull</c>); settlement mails always arrive (user answer 2026-11-09). Every number is a placeholder (balance
    /// is deferred).
    /// </summary>
    [Table("MailSettings", 1)]
    [Singleton]
    public sealed class MailSettings : IValidatableRow
    {
        /// <summary>Open mails at which new mail sources are refused (<c>MailboxFull</c>); settlements still arrive.</summary>
        [Range(1, 1_000)] public int MailboxCap { get; set; }

        /// <summary>Days after the send when an open mail expires and its attachments are destroyed.</summary>
        [Range(1, 365)] public int ExpiryDays { get; set; }

        /// <summary>Attachments (item stacks or instances) one mail carries at most.</summary>
        [Range(1, ReliableMessages.MaxMailAttachments)] public int AttachmentsPerMail { get; set; }

        /// <summary>Mails one claim-all request empties at most (one class 1 operation).</summary>
        [Range(1, 100)] public int ClaimAllMax { get; set; }

        /// <summary>Headers a list answer carries (oldest first, the first to expire).</summary>
        [Range(1, ReliableMessages.MaxMailHeaders)] public int MaxListed { get; set; }

        /// <summary>How often the expiry job runs (a background worker, never the tick).</summary>
        [Range(1_000, 86_400_000)] public int ExpiryJobIntervalMs { get; set; }

        /// <summary>Mails one expiry run closes at most (the rest wait for the next run).</summary>
        [Range(1, 10_000)] public int ExpiryBatchSize { get; set; }

        /// <summary>
        /// The world refuses a claim at the expiry time, the job expires a mail only this much later, so a claim save in
        /// flight never meets an expiry (a claim saved past it faults the character and duplicates nothing).
        /// </summary>
        [Range(10_000, 86_400_000)] public int ExpiryGraceMs { get; set; }

        /// <summary>Token bucket of the list, open and claim requests per character (spec 7.5 rate limits on mail).</summary>
        [Range(1, 1_000)] public int RequestsPerPeriod { get; set; }

        [Range(100, 3_600_000)] public int PeriodMs { get; set; }

        [Range(1, 1_000)] public int Burst { get; set; }

        /// <summary>A session's mail requests waiting for the next tick (bounds a flooding client; the bucket refuses earlier).</summary>
        [Range(1, 256)] public int MaxQueuedRequests { get; set; }

        /// <summary>
        /// How long the world remembers a committed send to a recipient who is not on it, to merge into the mailbox when
        /// that character attaches (a login that read the database before the send committed).
        /// </summary>
        [Range(1_000, 3_600_000)] public int RecentSendsKeepMs { get; set; }

        public long ExpiryMs => ExpiryDays * 86_400_000L;

        public void Validate(RowErrors errors)
        {
            if (Burst < RequestsPerPeriod)
                errors.Add(nameof(Burst), "must be at least RequestsPerPeriod");
            if (ExpiryJobIntervalMs > ExpiryGraceMs)
                errors.Add(nameof(ExpiryJobIntervalMs), "must not be longer than ExpiryGraceMs");
        }

        public static MailSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(MailSettings), out var table) && table.Rows.Count == 1 ? (MailSettings)table.Rows[0] : null;
    }
}
