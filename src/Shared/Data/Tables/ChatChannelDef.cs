using System;
using System.Collections.Generic;
using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// One chat channel and its per-character rate limit (R-6.5-01 "rate limits", spec 9.12; WP-M4-2): a token bucket of
    /// <see cref="Burst"/> lines at once, then <see cref="MessagesPerPeriod"/> per <see cref="PeriodMs"/>, kept apart per
    /// channel. The ids are the camelCase names of <see cref="ChatChannel"/>; every channel has exactly one row
    /// (<see cref="CheckCoverage"/>). Every number is a placeholder.
    /// </summary>
    [Table("ChatChannelDef", 1)]
    public sealed class ChatChannelDef : IValidatableRow
    {
        /// <summary>The channel, for example <c>party</c> (<see cref="ChatChannel"/> in camelCase).</summary>
        [TableId] public string Channel { get; set; } = string.Empty;

        /// <summary>Lines a character may send on the channel per <see cref="PeriodMs"/> once its burst is used up.</summary>
        [Range(1, 1_000)] public int MessagesPerPeriod { get; set; }

        [Range(100, 3_600_000)] public int PeriodMs { get; set; }

        /// <summary>Lines a character may send at once after being quiet on the channel (the bucket's capacity).</summary>
        [Range(1, 1_000)] public int Burst { get; set; }

        public void Validate(RowErrors errors)
        {
            if (!TryParse(Channel, out _))
                errors.Add(nameof(Channel), "must name a chat channel (ChatChannel in camelCase: local, party, whisper)");
            // A bucket smaller than one period's refill could never reach the configured rate.
            if (Burst < MessagesPerPeriod)
                errors.Add(nameof(Burst), "must be at least MessagesPerPeriod");
        }

        /// <summary>The channel whose camelCase name is <paramref name="id"/>.</summary>
        public static bool TryParse(string id, out ChatChannel channel)
        {
            foreach (ChatChannel value in Enum.GetValues(typeof(ChatChannel)))
            {
                if (string.Equals(ConfigTableReader.CamelCase(value.ToString()), id, StringComparison.Ordinal))
                {
                    channel = value;
                    return true;
                }
            }
            channel = default;
            return false;
        }

        /// <summary>One error per channel with no row. Empty when the table is not loaded (the loader reports that).</summary>
        public static List<ValidationError> CheckCoverage(ConfigSet set)
        {
            var errors = new List<ValidationError>();
            if (!set.TryGet(typeof(ChatChannelDef), out var table))
                return errors;
            var seen = new HashSet<ChatChannel>();
            foreach (ChatChannelDef row in table.Rows)
            {
                if (TryParse(row.Channel, out var channel))
                    seen.Add(channel);
            }
            var tableName = TableInfo.For(typeof(ChatChannelDef)).Name;
            foreach (ChatChannel channel in Enum.GetValues(typeof(ChatChannel)))
            {
                if (!seen.Contains(channel))
                    errors.Add(new ValidationError(ValidationCodes.RuleBroken, tableName, null, nameof(Channel),
                        $"no row for chat channel '{ConfigTableReader.CamelCase(channel.ToString())}'"));
            }
            return errors;
        }

        /// <summary>
        /// The rows by <see cref="ChatChannel"/> value, for the server's rate limits. Throws when a channel has no row; call
        /// it on tables that passed validation.
        /// </summary>
        public static ChatChannelDef[] ByChannel(ConfigSet set)
        {
            var coverage = CheckCoverage(set);
            if (!set.TryGet(typeof(ChatChannelDef), out var table) || coverage.Count > 0)
                throw new InvalidOperationException("ChatChannelDef is missing or incomplete:\n" + string.Join("\n", coverage));
            var rows = new ChatChannelDef[(int)ChatCodec.LastChannel + 1];
            foreach (ChatChannelDef row in table.Rows)
            {
                if (TryParse(row.Channel, out var channel))
                    rows[(int)channel] = row;
            }
            return rows;
        }
    }
}
