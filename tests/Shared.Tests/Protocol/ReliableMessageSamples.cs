using System.Text;
using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>
/// One valid message generator per <see cref="ReliableMessageId"/> and every reliable reader, for the R-2.5-07 tests.
/// A new id needs a generator here: <see cref="ReliableMessageReaderTests"/> fails without one, so every id is fuzzed.
/// </summary>
internal static class ReliableMessageSamples
{
    public static readonly IReadOnlyDictionary<ReliableMessageId, Func<Random, byte[]>> Valid =
        new Dictionary<ReliableMessageId, Func<Random, byte[]>>
        {
            [ReliableMessageId.ActionResult] = random =>
            {
                var buffer = new byte[ActionResult.Size];
                ActionResult.Write(buffer, (ushort)random.Next(65536), (ushort)random.Next(65536), (byte)random.Next(16),
                    (ActionReason)random.Next(1, (int)ActionResult.LastReason + 1));
                return buffer;
            },
            [ReliableMessageId.EntityIdentity] = random =>
            {
                var buffer = new byte[EntityIdentity.MaxSize];
                int length = EntityIdentity.Write(buffer, (uint)random.Next(), "N" + Text(random, 20));
                return buffer.AsSpan(0, length).ToArray();
            },
            [ReliableMessageId.PickupRequest] = random =>
                ReliableMessages.Write(ReliableMessageId.PickupRequest, new PickupRequest { DropEntityId = (uint)random.Next() }),
            [ReliableMessageId.PickupResult] = random => ReliableMessages.Write(ReliableMessageId.PickupResult, new PickupResult
            {
                DropEntityId = (uint)random.Next(),
                Reason = (PickupReason)random.Next(0, (int)PickupReason.PickupWhileDead + 1),
                Taken = (ushort)random.Next(65536),
            }),
            [ReliableMessageId.InventoryContents] = random => ReliableMessages.Write(ReliableMessageId.InventoryContents,
                Inventory(random, random.Next(0, 31), 30)),
            [ReliableMessageId.InventoryMoveRequest] = random => ReliableMessages.Write(ReliableMessageId.InventoryMoveRequest,
                new InventoryMoveRequest { RequestId = (uint)random.Next(), FromSlot = (byte)random.Next(256), ToSlot = (byte)random.Next(256) }),
            [ReliableMessageId.InventoryMoveResult] = random => ReliableMessages.Write(ReliableMessageId.InventoryMoveResult,
                random.Next(2) == 0
                    ? new InventoryMoveResult
                    {
                        RequestId = (uint)random.Next(),
                        Reason = (InventoryMoveReason)random.Next(1, (int)InventoryMoveReason.InventoryUnavailable + 1),
                    }
                    : new InventoryMoveResult
                    {
                        RequestId = (uint)random.Next(),
                        Outcome = (InventoryMoveOutcome)random.Next(1, (int)InventoryMoveOutcome.Swapped + 1),
                    }),
            [ReliableMessageId.ChatSend] = random =>
                ChatCodec.WriteSend(new ReliableMessageWriter(), new ChatSendMessage { Text = Text(random, 40) }).ToArray(),
            [ReliableMessageId.ChatMessage] = random => ChatCodec.WriteLine(new ReliableMessageWriter(), new ChatLineMessage
            {
                SenderEntityId = (uint)random.Next(),
                SenderName = "N" + Text(random, 10),
                Text = "T" + Text(random, 40),
            }).ToArray(),
            [ReliableMessageId.ChatRefused] = random => ChatCodec.WriteRefused(new ReliableMessageWriter(),
                new ChatRefusedMessage { Reason = (ChatReason)random.Next(1, (int)ChatCodec.LastReason + 1) }).ToArray(),
        };

    /// <summary>A bag of <paramref name="slotCount"/> slots with <paramref name="filled"/> distinct occupied ones.</summary>
    public static InventoryContents Inventory(Random random, int filled, int slotCount)
    {
        var slots = Enumerable.Range(0, slotCount).OrderBy(_ => random.Next()).Take(filled).OrderBy(s => s)
            .Select(s => new InventorySlotEntry
            {
                Slot = (byte)s,
                ItemNetId = (ushort)random.Next(1, 65536),
                Rarity = (byte)random.Next(256),
                Stolen = random.Next(2) == 1,
                Count = (ushort)random.Next(1, 65536),
            }).ToArray();
        return new InventoryContents { Revision = (uint)random.Next(), SlotCount = (byte)slotCount, Slots = slots };
    }

    /// <summary>
    /// Reads <paramref name="message"/> with every reliable reader (none may throw) and, when one accepts it, writes the
    /// value again. Returns the re-encoded bytes of the accepting reader, or null when all refuse.
    /// </summary>
    public static byte[]? ReadAllAndReencode(byte[] message)
    {
        byte[]? again = null;
        var writer = new ReliableMessageWriter();
        ReliableMessageReader.IsWellFormed(message, int.MaxValue);
        if (ActionResult.TryRead(message, out ushort sequence, out ushort actionId, out byte slot, out ActionReason reason))
        {
            again = new byte[ActionResult.Size];
            ActionResult.Write(again, sequence, actionId, slot, reason);
        }
        if (EntityIdentity.TryRead(message, out uint entityId, out string name))
        {
            var buffer = new byte[EntityIdentity.MaxSize];
            again = buffer.AsSpan(0, EntityIdentity.Write(buffer, entityId, name)).ToArray();
        }
        if (ReliableMessages.TryRead(message, out PickupRequest request))
            again = ReliableMessages.Write(ReliableMessageId.PickupRequest, request);
        if (ReliableMessages.TryRead(message, out PickupResult result))
            again = ReliableMessages.Write(ReliableMessageId.PickupResult, result);
        if (ReliableMessages.TryRead(message, out InventoryContents contents))
            again = ReliableMessages.Write(ReliableMessageId.InventoryContents, contents);
        if (ReliableMessages.TryRead(message, out InventoryMoveRequest move))
            again = ReliableMessages.Write(ReliableMessageId.InventoryMoveRequest, move);
        if (ReliableMessages.TryRead(message, out InventoryMoveResult moveResult))
            again = ReliableMessages.Write(ReliableMessageId.InventoryMoveResult, moveResult);
        if (ChatCodec.TryReadSend(message, out var text))
            again = ChatCodec.WriteSend(writer, new ChatSendMessage { Text = text }).ToArray();
        if (ChatCodec.TryReadLine(message, out var line))
            again = ChatCodec.WriteLine(writer, line).ToArray();
        if (ChatCodec.TryReadRefused(message, out var chatReason))
            again = ChatCodec.WriteRefused(writer, new ChatRefusedMessage { Reason = chatReason }).ToArray();
        return again;
    }

    /// <summary>Letters, spaces, accented and 4-byte characters (no control characters, so the text itself is valid).</summary>
    public static string Text(Random random, int maxChars)
    {
        int count = random.Next(0, maxChars + 1);
        var builder = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            builder.Append(random.Next(4) switch
            {
                0 => "a",
                1 => "é",
                2 => "😀",
                _ => (char)('A' + random.Next(26)),
            });
        }
        return builder.ToString().Trim();
    }
}
