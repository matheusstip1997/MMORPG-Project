using System;
using System.Collections.Generic;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>Who sends a reliable message. Only <see cref="ClientToServer"/> ids are hostile input to the server.</summary>
    public enum ReliableDirection : byte
    {
        ClientToServer = 1,
        ServerToClient = 2,
    }

    /// <summary>
    /// One <see cref="ReliableMessageId"/>: its direction, and either the MemoryPack body type with its guard layout or,
    /// for the hand-written messages, only the largest size (their own reader checks the rest).
    /// </summary>
    public sealed class ReliableMessageSpec
    {
        internal ReliableMessageSpec(ReliableMessageId id, ReliableDirection direction, Type? bodyType, MemoryPackLayout? layout,
            int maxBytes)
        {
            Id = id;
            Direction = direction;
            BodyType = bodyType;
            Layout = layout;
            MaxBytes = maxBytes;
        }

        public ReliableMessageId Id { get; }

        public ReliableDirection Direction { get; }

        /// <summary>The one MemoryPack type the id decodes to (never a sender-chosen type); null for a hand-written message.</summary>
        public Type? BodyType { get; }

        /// <summary>The guard layout of the MemoryPack body; null for a hand-written message.</summary>
        public MemoryPackLayout? Layout { get; }

        /// <summary>The largest message the protocol allows, id byte included (the bound of <see cref="Layout"/> plus one).</summary>
        public int MaxBytes { get; }
    }

    /// <summary>
    /// The only place a reliable MemoryPack body is deserialized (R-2.5-07, spec 11.2). Every <see cref="ReliableMessageId"/>
    /// has a <see cref="ReliableMessageSpec"/> here; a message is read only when its id is known, its size is within the cap
    /// (the caller's config cap and the protocol bound), and <see cref="MemoryPackGuard"/> accepted the whole body against
    /// the id's layout, so MemoryPack never sees a string header or count it could trust into reading past the buffer.
    /// The server runs <see cref="IsWellFormed"/> with the <c>ReliableMessageCaps</c> cap on every reliable message before
    /// routing it (<c>InputRouter</c>); the codecs (<see cref="ReliableMessages"/>, <see cref="ChatCodec"/>) read through
    /// <see cref="TryRead{T}"/>, which runs the guard again, so no path reaches MemoryPack unguarded.
    /// <para>
    /// A new reliable message adds its id to <see cref="ReliableMessageId"/>, a spec in <see cref="Build"/>, and, when a
    /// client sends it, a <c>ReliableMessageCaps</c> row; tests fail for an id without a spec, and the DataValidator for a
    /// client id without a row.
    /// </para>
    /// </summary>
    public static class ReliableMessageReader
    {
        private static readonly MemoryPackSerializerOptions Options = MemoryPackSerializerOptions.Utf8;

        private static readonly ReliableMessageSpec?[] ById = Build();

        private static readonly List<ReliableMessageSpec> AllSpecs = Collect();

        /// <summary>Every registered message, in id order.</summary>
        public static IReadOnlyList<ReliableMessageSpec> All => AllSpecs;

        public static bool TryGetSpec(ReliableMessageId id, out ReliableMessageSpec spec)
        {
            spec = ById[(byte)id]!;
            return spec != null;
        }

        /// <summary>
        /// The guard alone, allocation free and linear in the message size: the id is registered, the message is at most
        /// <paramref name="maxBytes"/> and the protocol bound, and a MemoryPack body matches its layout exactly. Hand-written
        /// messages are only size-checked here; their readers check the layout.
        /// </summary>
        public static bool IsWellFormed(ReadOnlySpan<byte> message, int maxBytes)
        {
            if (message.Length < 2 || message.Length > maxBytes)
                return false;
            var spec = ById[message[0]];
            if (spec == null || message.Length > spec.MaxBytes)
                return false;
            return spec.Layout == null || MemoryPackGuard.Matches(message.Slice(1), spec.Layout);
        }

        /// <summary>
        /// Reads a message of <paramref name="id"/> as its registered body type <typeparamref name="T"/>, or returns false
        /// without throwing for anything malformed (R-2.5-04). Nothing is allocated for a refused message.
        /// </summary>
        public static bool TryRead<T>(ReadOnlySpan<byte> message, ReliableMessageId id, ref T? value) where T : class
        {
            value = null;
            if (message.Length < 2 || message[0] != (byte)id)
                return false;
            var spec = ById[(byte)id];
            if (spec == null || spec.BodyType != typeof(T) || !IsWellFormed(message, spec.MaxBytes))
                return false;
            var body = message.Slice(1);
            try
            {
                int read = MemoryPackSerializer.Deserialize(body, ref value, Options);
                return read == body.Length && value != null;
            }
            catch (MemoryPackSerializationException)
            {
                value = null;
                return false;
            }
        }

        /// <summary>The spec of every id (append a row with each new id; members in declaration order).</summary>
        private static ReliableMessageSpec?[] Build()
        {
            var specs = new ReliableMessageSpec?[256];
            HandWritten(specs, ReliableMessageId.ActionResult, ReliableDirection.ServerToClient, ActionResult.Size);
            HandWritten(specs, ReliableMessageId.EntityIdentity, ReliableDirection.ServerToClient, EntityIdentity.MaxSize);

            // Backlog G (LootMessages.cs).
            MemoryPack<PickupRequest>(specs, ReliableMessageId.PickupRequest, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32));
            MemoryPack<PickupResult>(specs, ReliableMessageId.PickupResult, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.Fixed(sizeof(PickupReason)), MemoryPackLayout.U16));
            var slot = MemoryPackLayout.Object(MemoryPackLayout.U8, MemoryPackLayout.U16, MemoryPackLayout.U8,
                MemoryPackLayout.Boolean, MemoryPackLayout.U16);
            MemoryPack<InventoryContents>(specs, ReliableMessageId.InventoryContents, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U8,
                    MemoryPackLayout.Array(slot, ReliableMessages.MaxInventorySlots)));

            // Local chat (ChatMessages.cs): at most 4 UTF-8 bytes per character.
            MemoryPack<ChatSendMessage>(specs, ReliableMessageId.ChatSend, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.String(4 * ChatText.MaxTextChars)));
            MemoryPack<ChatLineMessage>(specs, ReliableMessageId.ChatMessage, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.String(4 * ChatText.MaxNameChars),
                    MemoryPackLayout.String(4 * ChatText.MaxTextChars)));
            MemoryPack<ChatRefusedMessage>(specs, ReliableMessageId.ChatRefused, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.Fixed(sizeof(ChatReason))));
            return specs;
        }

        private static void HandWritten(ReliableMessageSpec?[] specs, ReliableMessageId id, ReliableDirection direction, int maxBytes) =>
            Add(specs, new ReliableMessageSpec(id, direction, null, null, maxBytes));

        private static void MemoryPack<T>(ReliableMessageSpec?[] specs, ReliableMessageId id, ReliableDirection direction,
            MemoryPackLayout layout) where T : class =>
            Add(specs, new ReliableMessageSpec(id, direction, typeof(T), layout, 1 + layout.MaxBytes));

        private static void Add(ReliableMessageSpec?[] specs, ReliableMessageSpec spec)
        {
            if (spec.Id == ReliableMessageId.None || specs[(byte)spec.Id] != null)
                throw new InvalidOperationException($"Reliable message {spec.Id} is registered twice or is None.");
            specs[(byte)spec.Id] = spec;
        }

        private static List<ReliableMessageSpec> Collect()
        {
            var all = new List<ReliableMessageSpec>();
            foreach (var spec in ById)
            {
                if (spec != null)
                    all.Add(spec);
            }
            return all;
        }
    }
}
