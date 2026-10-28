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
                Inventory(random, random.Next(0, 31), 30, random.Next(0, 5), 20)),
            [ReliableMessageId.InventoryMoveRequest] = random => ReliableMessages.Write(ReliableMessageId.InventoryMoveRequest,
                new InventoryMoveRequest
                {
                    RequestId = (uint)random.Next(), FromSlot = (byte)random.Next(256), ToSlot = (byte)random.Next(256),
                    FromContainer = (byte)random.Next(256), ToContainer = (byte)random.Next(256),
                }),
            [ReliableMessageId.InventoryMoveResult] = random => ReliableMessages.Write(ReliableMessageId.InventoryMoveResult,
                random.Next(2) == 0
                    ? new InventoryMoveResult
                    {
                        RequestId = (uint)random.Next(),
                        Reason = (InventoryMoveReason)random.Next(1, (int)InventoryMoveReason.BagFull + 1),
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
            // Outposts and vendors (WP-M2-4, protocol 16).
            [ReliableMessageId.OutpostInteractRequest] = random => ReliableMessages.Write(ReliableMessageId.OutpostInteractRequest,
                new OutpostInteractRequest { RequestId = (uint)random.Next(), NpcEntityId = (uint)random.Next(), Kind = Interaction(random) }),
            [ReliableMessageId.OutpostInteractResult] = random => ReliableMessages.Write(ReliableMessageId.OutpostInteractResult,
                new OutpostInteractResult
                {
                    RequestId = (uint)random.Next(), NpcEntityId = (uint)random.Next(), Kind = Interaction(random), Reason = Reason(random),
                }),
            [ReliableMessageId.SellQuoteRequest] = random => ReliableMessages.Write(ReliableMessageId.SellQuoteRequest,
                new SellQuoteRequest
                {
                    RequestId = (uint)random.Next(), NpcEntityId = (uint)random.Next(), Slot = (byte)random.Next(256),
                    Count = (ushort)random.Next(65536),
                }),
            [ReliableMessageId.SellQuoteResult] = random => ReliableMessages.Write(ReliableMessageId.SellQuoteResult,
                new SellQuoteResult
                {
                    RequestId = (uint)random.Next(), Reason = Reason(random), Slot = (byte)random.Next(256), Count = (ushort)random.Next(65536),
                    Total = random.NextInt64(0, long.MaxValue), FirstUnit = random.Next(), LastUnit = random.Next(),
                }),
            [ReliableMessageId.SellRequest] = random => ReliableMessages.Write(ReliableMessageId.SellRequest,
                new SellRequest
                {
                    RequestId = (uint)random.Next(), NpcEntityId = (uint)random.Next(), Slot = (byte)random.Next(256),
                    Count = (ushort)random.Next(65536), QuotedTotal = random.NextInt64(),
                }),
            [ReliableMessageId.SellResult] = random => ReliableMessages.Write(ReliableMessageId.SellResult,
                new SellResult
                {
                    RequestId = (uint)random.Next(), Reason = Reason(random), Count = (ushort)random.Next(65536),
                    Gold = random.NextInt64(0, long.MaxValue),
                }),
            [ReliableMessageId.VendorBuyRequest] = random => ReliableMessages.Write(ReliableMessageId.VendorBuyRequest,
                new VendorBuyRequest
                {
                    RequestId = (uint)random.Next(), NpcEntityId = (uint)random.Next(), ItemNetId = (ushort)random.Next(65536),
                    Count = (ushort)random.Next(65536), QuotedTotal = random.NextInt64(),
                }),
            [ReliableMessageId.VendorBuyResult] = random => ReliableMessages.Write(ReliableMessageId.VendorBuyResult,
                new VendorBuyResult
                {
                    RequestId = (uint)random.Next(), Reason = Reason(random), ItemNetId = (ushort)random.Next(65536),
                    Count = (ushort)random.Next(65536), GoldSpent = random.NextInt64(0, long.MaxValue),
                }),
            [ReliableMessageId.GoldBalance] = random => ReliableMessages.Write(ReliableMessageId.GoldBalance,
                new GoldBalance { Gold = random.NextInt64(0, long.MaxValue) }),
            [ReliableMessageId.ProgressionState] = random => ReliableMessages.Write(ReliableMessageId.ProgressionState, new ProgressionState
            {
                Level = random.Next(1, 131),
                Exp = random.NextInt64(0, long.MaxValue),
                ExpToNext = random.NextInt64(0, long.MaxValue),
                Rank = (Shared.Simulation.Stats.Rank)random.Next(0, 8),
                SkillPoints = random.Next(0, int.MaxValue),
                BandPercent = random.Next(0, 121),
                Cause = (ProgressionCause)random.Next(0, (int)ProgressionCause.SkillPoints + 1),
            }),
            [ReliableMessageId.OwnStats] = random => StatsCodec.Write(new ReliableMessageWriter(), new OwnStats
            {
                Version = (uint)random.Next(),
                Values = Enumerable.Range(0, random.Next(0, StatsCodec.MaxStatCount + 1)).Select(_ => random.Next(int.MinValue, int.MaxValue)).ToArray(),
                ToxicityMilli = random.Next(0, 1_000_000),
                SatietyMilli = random.Next(0, 1_000_000),
            }).ToArray(),
            // Skill tree (WP-M2-5, protocol 20).
            [ReliableMessageId.SkillTreeState] = random => ReliableMessages.Write(ReliableMessageId.SkillTreeState, new SkillTreeState
            {
                Version = (uint)random.Next(),
                ClassId = NodeId(random),
                Nodes = Enumerable.Range(0, random.Next(0, ReliableMessages.MaxSkillNodes + 1))
                    .Select(_ => new SkillNodeEntry { NodeId = NodeId(random), Level = (byte)random.Next(1, 256) }).ToArray(),
                Hotbar = Enumerable.Range(0, random.Next(0, Shared.Data.Tables.ActionBindings.MaxSkillSlots + 1))
                    .Select(_ => new HotbarEntry { Slot = (byte)random.Next(1, Shared.Data.Tables.ActionBindings.MaxSkillSlots + 1), NodeId = NodeId(random) }).ToArray(),
                Cause = (SkillTreeCause)random.Next(0, (int)ReliableMessages.LastSkillTreeCause + 1),
            }),
            [ReliableMessageId.SkillTreeRequest] = random => ReliableMessages.Write(ReliableMessageId.SkillTreeRequest, new SkillTreeRequest
            {
                RequestId = (uint)random.Next(),
                Op = (SkillTreeOp)random.Next(1, (int)ReliableMessages.LastSkillTreeOp + 1),
                Target = random.Next(4) == 0 ? string.Empty : NodeId(random),
                Slot = (byte)random.Next(256),
            }),
            [ReliableMessageId.SkillTreeResult] = random => ReliableMessages.Write(ReliableMessageId.SkillTreeResult, new SkillTreeResult
            {
                RequestId = (uint)random.Next(),
                Op = (SkillTreeOp)random.Next(0, (int)ReliableMessages.LastSkillTreeOp + 1),
                Reason = (SkillTreeReason)random.Next(0, (int)ReliableMessages.LastSkillTreeReason + 1),
                Target = NodeId(random),
                Level = (byte)random.Next(256),
            }),
            // The Bystial book (WP-M2-8): distinct cards, placed ones in distinct slots below the slot count.
            [ReliableMessageId.BookState] = random => ReliableMessages.Write(ReliableMessageId.BookState, BookState(random)),
            [ReliableMessageId.BookChangeRequest] = random => ReliableMessages.Write(ReliableMessageId.BookChangeRequest,
                new BookChangeRequest { RequestId = (uint)random.Next(), MonsterNetId = (ushort)random.Next(65536), Slot = (byte)random.Next(256) }),
            [ReliableMessageId.BookChangeResult] = random => ReliableMessages.Write(ReliableMessageId.BookChangeResult,
                new BookChangeResult { RequestId = (uint)random.Next(), Reason = (BookReason)random.Next(0, (int)BookLimits.LastReason + 1) }),
        };

    /// <summary>
    /// A Normal bag of <paramref name="slotCount"/> slots with <paramref name="filled"/> distinct occupied ones, and
    /// <paramref name="containers"/> other containers (codes 2 and up) of <paramref name="containerSlots"/> slots, all full
    /// (WP-M2-1). Every other entry is an instance (count 1).
    /// </summary>
    public static InventoryContents Inventory(Random random, int filled, int slotCount, int containers = 0, int containerSlots = 0) =>
        new()
        {
            Revision = (uint)random.Next(),
            SlotCount = (byte)slotCount,
            Slots = Slots(random, filled, slotCount),
            Containers = Enumerable.Range(0, containers).Select(i => new InventoryContainerEntry
            {
                Code = (byte)(2 + i), SlotCount = (byte)containerSlots, Slots = Slots(random, containerSlots, containerSlots),
            }).ToArray(),
        };

    private static InventorySlotEntry[] Slots(Random random, int filled, int slotCount) =>
        Enumerable.Range(0, slotCount).OrderBy(_ => random.Next()).Take(filled).OrderBy(s => s)
            .Select(s =>
            {
                bool instance = random.Next(2) == 1;
                return new InventorySlotEntry
                {
                    Slot = (byte)s,
                    ItemNetId = (ushort)random.Next(1, 65536),
                    Rarity = (byte)random.Next(256),
                    Stolen = random.Next(2) == 1,
                    Count = instance ? (ushort)1 : (ushort)random.Next(1, 65536),
                    InstanceId = instance ? random.NextInt64(1, long.MaxValue) : 0,
                    NonTradable = random.Next(2) == 1,
                };
            }).ToArray();

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
        if (ReliableMessages.TryRead(message, out OutpostInteractRequest interact))
            again = ReliableMessages.Write(ReliableMessageId.OutpostInteractRequest, interact);
        if (ReliableMessages.TryRead(message, out OutpostInteractResult interactResult))
            again = ReliableMessages.Write(ReliableMessageId.OutpostInteractResult, interactResult);
        if (ReliableMessages.TryRead(message, out SellQuoteRequest quote))
            again = ReliableMessages.Write(ReliableMessageId.SellQuoteRequest, quote);
        if (ReliableMessages.TryRead(message, out SellQuoteResult quoteResult))
            again = ReliableMessages.Write(ReliableMessageId.SellQuoteResult, quoteResult);
        if (ReliableMessages.TryRead(message, out SellRequest sell))
            again = ReliableMessages.Write(ReliableMessageId.SellRequest, sell);
        if (ReliableMessages.TryRead(message, out SellResult sellResult))
            again = ReliableMessages.Write(ReliableMessageId.SellResult, sellResult);
        if (ReliableMessages.TryRead(message, out VendorBuyRequest buy))
            again = ReliableMessages.Write(ReliableMessageId.VendorBuyRequest, buy);
        if (ReliableMessages.TryRead(message, out VendorBuyResult buyResult))
            again = ReliableMessages.Write(ReliableMessageId.VendorBuyResult, buyResult);
        if (ReliableMessages.TryRead(message, out GoldBalance gold))
            again = ReliableMessages.Write(ReliableMessageId.GoldBalance, gold);
        if (ReliableMessages.TryRead(message, out ProgressionState progression))
            again = ReliableMessages.Write(ReliableMessageId.ProgressionState, progression);
        if (StatsCodec.TryRead(message, out var stats))
            again = StatsCodec.Write(writer, stats).ToArray();
        if (ReliableMessages.TryRead(message, out SkillTreeState skillState))
            again = ReliableMessages.Write(ReliableMessageId.SkillTreeState, skillState);
        if (ReliableMessages.TryRead(message, out SkillTreeRequest skillRequest))
            again = ReliableMessages.Write(ReliableMessageId.SkillTreeRequest, skillRequest);
        if (ReliableMessages.TryRead(message, out SkillTreeResult skillResult))
            again = ReliableMessages.Write(ReliableMessageId.SkillTreeResult, skillResult);
        if (ReliableMessages.TryRead(message, out BookState book))
            again = ReliableMessages.Write(ReliableMessageId.BookState, book);
        if (ReliableMessages.TryRead(message, out BookChangeRequest bookChange))
            again = ReliableMessages.Write(ReliableMessageId.BookChangeRequest, bookChange);
        if (ReliableMessages.TryRead(message, out BookChangeResult bookResult))
            again = ReliableMessages.Write(ReliableMessageId.BookChangeResult, bookResult);
        return again;
    }

    /// <summary>A skill-tree node or class id: ASCII, 1 to <c>SkillTree.MaxIdBytes</c> bytes.</summary>
    public static string NodeId(Random random)
    {
        int count = random.Next(1, Shared.Data.Tables.SkillTree.MaxIdBytes + 1);
        var builder = new StringBuilder(count);
        for (int i = 0; i < count; i++)
            builder.Append((char)random.Next(0x21, 0x7F));
        return builder.ToString();
    }

    /// <summary>A valid book: up to 40 distinct monsters, a slot count up to the cap, placed cards (with a tier) in distinct slots.</summary>
    public static BookState BookState(Random random)
    {
        byte slotCount = (byte)random.Next(0, BookLimits.MaxSlots + 1);
        var slots = Enumerable.Range(0, slotCount).OrderBy(_ => random.Next()).ToList();
        var cards = Enumerable.Range(1, random.Next(0, 41)).Select(netId =>
        {
            var tier = (Shared.Data.Tables.BystialTier)random.Next(0, (int)Shared.Data.Tables.BystialTiers.Last + 1);
            byte slot = BookLimits.NoSlot;
            if (tier != Shared.Data.Tables.BystialTier.None && slots.Count > 0 && random.Next(2) == 0)
            {
                slot = (byte)slots[^1];
                slots.RemoveAt(slots.Count - 1);
            }
            return new BookCard { MonsterNetId = (ushort)netId, Tier = tier, Slot = slot, Kills = random.Next(0, 10_000), NextTierKills = random.Next(0, 10_000) };
        }).ToArray();
        return new BookState { Version = (uint)random.Next(), Cause = (BookCause)random.Next(0, (int)BookLimits.LastCause + 1), SlotCount = slotCount, Cards = cards };
    }

    private static Shared.Data.Tables.NpcInteraction Interaction(Random random) =>
        (Shared.Data.Tables.NpcInteraction)random.Next(0, (int)OutpostMessages.LastInteraction + 1);

    private static OutpostReason Reason(Random random) => (OutpostReason)random.Next(0, (int)OutpostMessages.LastReason + 1);

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
