using System;
using Shared.Protocol;

namespace Shared.Simulation.Items
{
    /// <summary>
    /// The pure rules of a death loot bag (spec 4.3 R-4.3-03, 4.4 R-4.4-01; M4 decision 8; WP-M4-9). Times are UTC milliseconds
    /// from <c>IClock</c> (rule 6): the owner window and the expiry are fixed at the death and run on through a restart.
    /// </summary>
    public static class LootBagRules
    {
        /// <summary>When the owner window ends: <paramref name="diedAtMs"/> + <c>OwnerWindows.ownerWindowMs</c>.</summary>
        public static long WindowUntil(long diedAtMs, int ownerWindowMs) => diedAtMs + Math.Max(0, ownerWindowMs);

        /// <summary>When the bag expires and what is left is destroyed: <paramref name="diedAtMs"/> + <c>BagExpiry.expiryMs</c>.</summary>
        public static long ExpiresAt(long diedAtMs, int expiryMs) => diedAtMs + Math.Max(0, expiryMs);

        /// <summary>The bag is past its expiry (the expiry job destroys it; nobody may take from it).</summary>
        public static bool IsExpired(long nowMs, long expiresAtMs) => nowMs >= expiresAtMs;

        /// <summary>
        /// What <paramref name="viewer"/> may do with <paramref name="owner"/>'s bag at <paramref name="nowMs"/>: the owner always
        /// takes their own; inside the window a member of the owner's party (asked at this moment, user answer 2026-11-13)
        /// collects for the owner and anyone else is locked out; after the window every non-owner, party members included, takes
        /// with the stolen mark.
        /// </summary>
        public static LootBagAccess Access(long viewer, long owner, bool sameParty, long nowMs, long windowUntilMs)
        {
            if (viewer == owner)
                return LootBagAccess.Own;
            if (nowMs < windowUntilMs)
                return sameParty ? LootBagAccess.CollectForOwner : LootBagAccess.Locked;
            return LootBagAccess.TakeStolen;
        }

        /// <summary>
        /// The stack a non-owner's take gives (R-4.4-01): marked stolen from <paramref name="owner"/> at <paramref name="nowMs"/>.
        /// A stack that already carries a mark keeps it (its original owner is the first one it was taken from); a stolen stack
        /// whose mark is not known (flagged before WP-M4-9) gets this one.
        /// </summary>
        public static ItemStack Mark(in ItemStack stack, long owner, long nowMs) =>
            stack.Stolen && stack.StolenFrom != 0 ? stack : stack.WithMark(owner, nowMs);

        /// <summary>How many mails <paramref name="rows"/> rows need at <paramref name="perMail"/> rows a mail (0 for none).</summary>
        public static int MailCount(int rows, int perMail)
        {
            if (perMail <= 0)
                throw new ArgumentOutOfRangeException(nameof(perMail));
            return rows <= 0 ? 0 : (rows + perMail - 1) / perMail;
        }

        /// <summary>The rows of mail <paramref name="mail"/> (0-based) when <paramref name="rows"/> rows go <paramref name="perMail"/> a mail, in order.</summary>
        public static (int Start, int Count) MailSlice(int rows, int perMail, int mail)
        {
            if (mail < 0 || mail >= MailCount(rows, perMail))
                throw new ArgumentOutOfRangeException(nameof(mail));
            int start = mail * perMail;
            return (start, Math.Min(perMail, rows - start));
        }

        /// <summary>Milliseconds from <paramref name="nowMs"/> until <paramref name="untilMs"/>, 0 once past (for the wire).</summary>
        public static uint MsLeft(long nowMs, long untilMs)
        {
            long left = untilMs - nowMs;
            return left <= 0 ? 0u : left >= uint.MaxValue ? uint.MaxValue : (uint)left;
        }
    }
}
