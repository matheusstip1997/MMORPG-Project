using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>
    /// Client to server, reliable (<see cref="ReliableMessageId.ChatSend"/>): a local chat line (spec 9.3 <c>chat</c>,
    /// R-6.5-01). The server checks it with <see cref="ChatText"/> and its rate limit, then sends it as a
    /// <see cref="ChatLineMessage"/> to the players who see the sender. Fields are only ever appended (spec 11.2); a channel
    /// field comes with party and guild chat, and a line without one is local.
    /// </summary>
    [MemoryPackable]
    public sealed partial class ChatSendMessage
    {
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// Server to client, reliable (<see cref="ReliableMessageId.ChatMessage"/>, spec 9.4 <c>ChatMessage</c>): a chat line
    /// from a player the recipient has in interest, or the echo of its own line. <see cref="SenderName"/> already went
    /// through <c>DisplayIdentity(sender, recipient)</c> on the server (architecture rule 8).
    /// </summary>
    [MemoryPackable]
    public sealed partial class ChatLineMessage
    {
        public uint SenderEntityId { get; set; }

        public string SenderName { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;
    }

    /// <summary>Server to client, reliable (<see cref="ReliableMessageId.ChatRefused"/>): the client's line was refused.</summary>
    [MemoryPackable]
    public sealed partial class ChatRefusedMessage
    {
        public ChatReason Reason { get; set; }
    }

    /// <summary>
    /// Writes and reads the chat messages: the <see cref="ReliableMessageId"/> byte, then the MemoryPack body with UTF-8
    /// strings. Readers follow R-2.5-04: the id picks one fixed sealed type (the sender never chooses a type), the size is
    /// capped, <see cref="MemoryPackGuard"/> checks the layout (exact member count, string bounds, strict UTF-8) and that it
    /// covers the whole body before MemoryPack reads anything, and anything malformed returns false without throwing.
    /// </summary>
    public static class ChatCodec
    {
        public const int MaxTextChars = ChatText.MaxTextChars;

        public const int MaxNameChars = ChatText.MaxNameChars;

        /// <summary>The highest <see cref="ChatReason"/> a message may carry.</summary>
        public const ChatReason LastReason = ChatReason.ChatRateLimited;

        // MemoryPack layout: one member-count byte per object; a UTF-8 string is two int headers, then its bytes.
        private const int ObjectHeaderBytes = 1;
        private const int StringHeaderBytes = 8;
        private const int MaxTextBytes = 4 * MaxTextChars;
        private const int MaxNameBytes = 4 * MaxNameChars;

        public const int MaxSendSize = 1 + ObjectHeaderBytes + StringHeaderBytes + MaxTextBytes;

        public const int MaxLineSize = 1 + ObjectHeaderBytes + 4 + StringHeaderBytes + MaxNameBytes + StringHeaderBytes + MaxTextBytes;

        public const int RefusedSize = 1 + ObjectHeaderBytes + 1;

        private static readonly MemoryPackSerializerOptions Options = MemoryPackSerializerOptions.Utf8;

        public static ReadOnlySpan<byte> WriteSend(ReliableMessageWriter writer, ChatSendMessage message) =>
            Write(writer, ReliableMessageId.ChatSend, message);

        public static ReadOnlySpan<byte> WriteLine(ReliableMessageWriter writer, ChatLineMessage message) =>
            Write(writer, ReliableMessageId.ChatMessage, message);

        public static ReadOnlySpan<byte> WriteRefused(ReliableMessageWriter writer, ChatRefusedMessage message) =>
            Write(writer, ReliableMessageId.ChatRefused, message);

        /// <summary>
        /// A client's line, decoded but not yet checked against <c>ChatSettings</c>: the server runs
        /// <see cref="ChatText.Check"/> to pick the refusal reason.
        /// </summary>
        public static bool TryReadSend(ReadOnlySpan<byte> source, out string text)
        {
            text = string.Empty;
            ChatSendMessage? message = null;
            if (!TryRead(source, ReliableMessageId.ChatSend, MaxSendSize, ref message) || message!.Text == null)
                return false;
            text = message.Text;
            return true;
        }

        /// <summary>A line from the server; the name and the text must pass <see cref="ChatText.Check"/> unchanged.</summary>
        public static bool TryReadLine(ReadOnlySpan<byte> source, out ChatLineMessage line)
        {
            line = null!;
            ChatLineMessage? message = null;
            if (!TryRead(source, ReliableMessageId.ChatMessage, MaxLineSize, ref message))
                return false;
            if (!IsClean(message!.SenderName, MaxNameChars) || !IsClean(message.Text, MaxTextChars))
                return false;
            line = message;
            return true;
        }

        public static bool TryReadRefused(ReadOnlySpan<byte> source, out ChatReason reason)
        {
            reason = ChatReason.None;
            ChatRefusedMessage? message = null;
            if (!TryRead(source, ReliableMessageId.ChatRefused, RefusedSize, ref message))
                return false;
            if (message!.Reason == ChatReason.None || message.Reason > LastReason)
                return false;
            reason = message.Reason;
            return true;
        }

        private static ReadOnlySpan<byte> Write<T>(ReliableMessageWriter writer, ReliableMessageId id, T message) where T : class
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            writer.Begin(id);
            MemoryPackSerializer.Serialize(writer, message, Options);
            return writer.Written;
        }

        private static bool TryRead<T>(ReadOnlySpan<byte> source, ReliableMessageId id, int maxSize, ref T? message) where T : class
        {
            if (source.Length < 2 || source.Length > maxSize || source[0] != (byte)id)
                return false;
            var body = source.Slice(1);
            if (!HasLayout(id, body))
                return false;
            try
            {
                int read = MemoryPackSerializer.Deserialize(body, ref message, Options);
                return read == body.Length && message != null;
            }
            catch (MemoryPackSerializationException)
            {
                return false;
            }
        }

        /// <summary>The member layout of each message, in declaration order (append new members here too).</summary>
        private static bool HasLayout(ReliableMessageId id, ReadOnlySpan<byte> body)
        {
            bool valid;
            switch (id)
            {
                case ReliableMessageId.ChatSend:
                    valid = MemoryPackGuard.Object(ref body, 1) && MemoryPackGuard.String(ref body);
                    break;
                case ReliableMessageId.ChatMessage:
                    valid = MemoryPackGuard.Object(ref body, 3) && MemoryPackGuard.Fixed(ref body, sizeof(uint))
                        && MemoryPackGuard.String(ref body) && MemoryPackGuard.String(ref body);
                    break;
                case ReliableMessageId.ChatRefused:
                    valid = MemoryPackGuard.Object(ref body, 1) && MemoryPackGuard.Fixed(ref body, sizeof(ChatReason));
                    break;
                default:
                    valid = false;
                    break;
            }
            return valid && body.IsEmpty;
        }

        private static bool IsClean(string? text, int maxChars) =>
            ChatText.Check(text, maxChars, out var clean) == ChatReason.None && clean.Length == text!.Length;
    }
}
