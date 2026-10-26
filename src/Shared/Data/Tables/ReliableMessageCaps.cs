using System;
using System.Collections.Generic;
using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Network security (spec 2.5, R-2.5-03 packet size, R-2.5-07): the largest message of each reliable id a client may
    /// send, id byte included. The server's <c>InputRouter</c> refuses a larger message, or one whose id has no row, before
    /// <see cref="ReliableMessageReader"/> walks it, so a client's message costs at most its row's bytes of guard work.
    /// One row per <see cref="ReliableDirection.ClientToServer"/> id (<see cref="CheckCoverage"/>), at most the protocol bound
    /// (<see cref="ReliableMessageSpec.MaxBytes"/>) and <see cref="NetSecurity.MaxReliableMessageBytes"/>. The ids are the
    /// camelCase names of <see cref="ReliableMessageId"/>. Values in data/tables/ReliableMessageCaps.json are placeholders.
    /// </summary>
    [Table("ReliableMessageCaps", 1)]
    public sealed class ReliableMessageCaps : IValidatableRow, ISetValidatableRow
    {
        /// <summary>The message, for example <c>chatSend</c> (<see cref="ReliableMessageId"/> in camelCase).</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>The largest message a client may send under this id, in bytes, id byte included.</summary>
        [Range(2, 1_048_576)] public int MaxBytes { get; set; }

        public void Validate(RowErrors errors)
        {
            if (!TryParseId(Id, out var id) || !ReliableMessageReader.TryGetSpec(id, out var spec))
            {
                errors.Add(nameof(Id), "must name a reliable message (ReliableMessageId in camelCase)");
                return;
            }
            if (spec.Direction != ReliableDirection.ClientToServer)
                errors.Add(nameof(Id), $"'{Id}' is sent by the server; only client messages have a cap");
            else if (MaxBytes > spec.MaxBytes)
                errors.Add(nameof(MaxBytes), $"must not be larger than the protocol bound of '{Id}' ({spec.MaxBytes} bytes)");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (set.TryGet(typeof(NetSecurity), out var net) && net.Rows.Count == 1
                && MaxBytes > ((NetSecurity)net.Rows[0]).MaxReliableMessageBytes)
                errors.Add(nameof(MaxBytes), "must not be larger than NetSecurity.maxReliableMessageBytes");
            // A cap below the longest line ChatSettings allows would drop valid lines as malformed.
            if (TryParseId(Id, out var id) && id == ReliableMessageId.ChatSend
                && set.TryGet(typeof(ChatSettings), out var chat) && chat.Rows.Count == 1)
            {
                int needed = ChatSendBytes(((ChatSettings)chat.Rows[0]).MaxChars);
                if (MaxBytes < needed)
                    errors.Add(nameof(MaxBytes), $"must be at least {needed}: ChatSettings.maxChars characters of up to 4 UTF-8 bytes");
            }
        }

        /// <summary>The largest <see cref="ChatSendMessage"/> with <paramref name="maxChars"/> characters: id, object and string headers, 4 bytes per character.</summary>
        public static int ChatSendBytes(int maxChars) => 1 + 1 + 8 + 4 * maxChars;

        /// <summary>The message whose camelCase name is <paramref name="id"/>; never <see cref="ReliableMessageId.None"/>.</summary>
        public static bool TryParseId(string id, out ReliableMessageId messageId)
        {
            foreach (ReliableMessageId value in Enum.GetValues(typeof(ReliableMessageId)))
            {
                if (value != ReliableMessageId.None && string.Equals(ConfigTableReader.CamelCase(value.ToString()), id, StringComparison.Ordinal))
                {
                    messageId = value;
                    return true;
                }
            }
            messageId = ReliableMessageId.None;
            return false;
        }

        /// <summary>One error per client message with no row (R-2.5-07). Empty when the table is not loaded (the loader reports that).</summary>
        public static List<ValidationError> CheckCoverage(ConfigSet set)
        {
            var errors = new List<ValidationError>();
            if (!set.TryGet(typeof(ReliableMessageCaps), out var table))
                return errors;
            var seen = new HashSet<ReliableMessageId>();
            foreach (ReliableMessageCaps row in table.Rows)
            {
                if (TryParseId(row.Id, out var id))
                    seen.Add(id);
            }
            var tableName = TableInfo.For(typeof(ReliableMessageCaps)).Name;
            foreach (var spec in ReliableMessageReader.All)
            {
                if (spec.Direction == ReliableDirection.ClientToServer && !seen.Contains(spec.Id))
                    errors.Add(new ValidationError(ValidationCodes.RuleBroken, tableName, null, nameof(Id),
                        $"no row for client message '{ConfigTableReader.CamelCase(spec.Id.ToString())}' (R-2.5-07)"));
            }
            return errors;
        }

        /// <summary>
        /// The caps by id byte for the server's router: a row's <see cref="MaxBytes"/> for each client message, 0 for every
        /// other id (refused). Throws when a client message has no row; call it on tables that passed validation.
        /// </summary>
        public static int[] ById(ConfigSet set)
        {
            var coverage = CheckCoverage(set);
            if (!set.TryGet(typeof(ReliableMessageCaps), out var table) || coverage.Count > 0)
                throw new InvalidOperationException("ReliableMessageCaps is missing or incomplete:\n" + string.Join("\n", coverage));
            var caps = new int[256];
            foreach (ReliableMessageCaps row in table.Rows)
            {
                if (TryParseId(row.Id, out var id) && ReliableMessageReader.TryGetSpec(id, out var spec)
                    && spec.Direction == ReliableDirection.ClientToServer)
                    caps[(byte)id] = Math.Min(row.MaxBytes, spec.MaxBytes);
            }
            return caps;
        }

        /// <summary>The protocol bound of every client message, for routers built without config (tests and tools).</summary>
        public static int[] ProtocolBounds()
        {
            var caps = new int[256];
            foreach (var spec in ReliableMessageReader.All)
            {
                if (spec.Direction == ReliableDirection.ClientToServer)
                    caps[(byte)spec.Id] = spec.MaxBytes;
            }
            return caps;
        }
    }
}
