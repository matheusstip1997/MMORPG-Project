using System;
using System.Collections.Generic;
using MemoryPack;
using Shared.Simulation.Stats;

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
            // Instance id, non-tradable flag and the other containers: protocol 16 (WP-M2-1). Reserved flag and typed properties:
            // protocol 26 (WP-M3-2).
            var craftedStat = MemoryPackLayout.Object(MemoryPackLayout.U8, MemoryPackLayout.Fixed(sizeof(int)), MemoryPackLayout.Fixed(sizeof(int)));
            var slot = MemoryPackLayout.Object(MemoryPackLayout.U8, MemoryPackLayout.U16, MemoryPackLayout.U8,
                MemoryPackLayout.Boolean, MemoryPackLayout.U16, MemoryPackLayout.Fixed(sizeof(long)), MemoryPackLayout.Boolean,
                MemoryPackLayout.Boolean, MemoryPackLayout.U8, MemoryPackLayout.U16, MemoryPackLayout.Fixed(sizeof(long)),
                MemoryPackLayout.Boolean, MemoryPackLayout.Array(MemoryPackLayout.U16, ReliableMessages.MaxGems),
                MemoryPackLayout.Array(craftedStat, ReliableMessages.MaxCraftedStats));
            var slots = MemoryPackLayout.Array(slot, ReliableMessages.MaxInventorySlots);
            var container = MemoryPackLayout.Object(MemoryPackLayout.U8, MemoryPackLayout.U8, slots);
            MemoryPack<InventoryContents>(specs, ReliableMessageId.InventoryContents, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U8, slots,
                    MemoryPackLayout.Array(container, ReliableMessages.MaxInventoryContainers)));
            // WP16 inventory window (InventoryMessages.cs, protocol 13; the two container codes protocol 16, WP-M2-1; the split
            // count protocol 24, WP-M2-11).
            MemoryPack<InventoryMoveRequest>(specs, ReliableMessageId.InventoryMoveRequest, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U8, MemoryPackLayout.U8, MemoryPackLayout.U8,
                    MemoryPackLayout.U8, MemoryPackLayout.U16));
            MemoryPack<InventoryMoveResult>(specs, ReliableMessageId.InventoryMoveResult, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.Fixed(sizeof(InventoryMoveReason)),
                    MemoryPackLayout.Fixed(sizeof(InventoryMoveOutcome))));

            // Local chat (ChatMessages.cs): at most 4 UTF-8 bytes per character.
            MemoryPack<ChatSendMessage>(specs, ReliableMessageId.ChatSend, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.String(4 * ChatText.MaxTextChars)));
            MemoryPack<ChatLineMessage>(specs, ReliableMessageId.ChatMessage, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.String(4 * ChatText.MaxNameChars),
                    MemoryPackLayout.String(4 * ChatText.MaxTextChars)));
            MemoryPack<ChatRefusedMessage>(specs, ReliableMessageId.ChatRefused, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.Fixed(sizeof(ChatReason))));

            // Outposts, selling and vendors (OutpostMessages.cs, WP-M2-4, protocol 17).
            var reason = MemoryPackLayout.Fixed(sizeof(OutpostReason));
            var interaction = MemoryPackLayout.Fixed(sizeof(Shared.Data.Tables.NpcInteraction));
            var gold = MemoryPackLayout.Fixed(sizeof(long));
            MemoryPack<OutpostInteractRequest>(specs, ReliableMessageId.OutpostInteractRequest, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U32, interaction));
            MemoryPack<OutpostInteractResult>(specs, ReliableMessageId.OutpostInteractResult, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U32, interaction, reason));
            MemoryPack<SellQuoteRequest>(specs, ReliableMessageId.SellQuoteRequest, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U32, MemoryPackLayout.U8, MemoryPackLayout.U16));
            MemoryPack<SellQuoteResult>(specs, ReliableMessageId.SellQuoteResult, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, reason, MemoryPackLayout.U8, MemoryPackLayout.U16, gold,
                    MemoryPackLayout.U32, MemoryPackLayout.U32));
            MemoryPack<SellRequest>(specs, ReliableMessageId.SellRequest, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U32, MemoryPackLayout.U8, MemoryPackLayout.U16, gold));
            MemoryPack<SellResult>(specs, ReliableMessageId.SellResult, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, reason, MemoryPackLayout.U16, gold));
            MemoryPack<VendorBuyRequest>(specs, ReliableMessageId.VendorBuyRequest, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U32, MemoryPackLayout.U16, MemoryPackLayout.U16, gold));
            MemoryPack<VendorBuyResult>(specs, ReliableMessageId.VendorBuyResult, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, reason, MemoryPackLayout.U16, MemoryPackLayout.U16, gold));
            MemoryPack<GoldBalance>(specs, ReliableMessageId.GoldBalance, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(gold));
            // Exp, level and rank (ProgressionMessages.cs, WP-M2-3).
            MemoryPack<ProgressionState>(specs, ReliableMessageId.ProgressionState, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.Fixed(sizeof(long)), MemoryPackLayout.Fixed(sizeof(long)),
                    MemoryPackLayout.Fixed(sizeof(Rank)), MemoryPackLayout.U32, MemoryPackLayout.U32,
                    MemoryPackLayout.Fixed(sizeof(ProgressionCause)), MemoryPackLayout.U32, MemoryPackLayout.U32, MemoryPackLayout.U32,
                    MemoryPackLayout.U32, MemoryPackLayout.U32));
            // Free attribute points (ProgressionMessages.cs, WP-M2-13, protocol 25).
            MemoryPack<AttributeSpendRequest>(specs, ReliableMessageId.AttributeSpendRequest, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U8));
            MemoryPack<AttributeSpendResult>(specs, ReliableMessageId.AttributeSpendResult, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U8, MemoryPackLayout.Fixed(sizeof(AttributeReason))));
            // Own stats (StatsMessages.cs, WP-M2-2, protocol 19).
            MemoryPack<OwnStats>(specs, ReliableMessageId.OwnStats, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.Array(MemoryPackLayout.Fixed(sizeof(int)), StatsCodec.MaxStatCount),
                    MemoryPackLayout.Fixed(sizeof(int)), MemoryPackLayout.Fixed(sizeof(int))));
            // Skill tree (SkillTreeMessages.cs, WP-M2-5, protocol 20). Node and class ids are ASCII (SkillTree.MaxIdBytes).
            var skillId = MemoryPackLayout.String(Shared.Data.Tables.SkillTree.MaxIdBytes);
            var skillOp = MemoryPackLayout.Fixed(sizeof(SkillTreeOp));
            MemoryPack<SkillTreeState>(specs, ReliableMessageId.SkillTreeState, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, skillId,
                    MemoryPackLayout.Array(MemoryPackLayout.Object(skillId, MemoryPackLayout.U8), ReliableMessages.MaxSkillNodes),
                    MemoryPackLayout.Array(MemoryPackLayout.Object(MemoryPackLayout.U8, skillId), Shared.Data.Tables.ActionBindings.MaxSkillSlots),
                    MemoryPackLayout.Fixed(sizeof(SkillTreeCause))));
            MemoryPack<SkillTreeRequest>(specs, ReliableMessageId.SkillTreeRequest, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32, skillOp, skillId, MemoryPackLayout.U8));
            MemoryPack<SkillTreeResult>(specs, ReliableMessageId.SkillTreeResult, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, skillOp, MemoryPackLayout.Fixed(sizeof(SkillTreeReason)), skillId,
                    MemoryPackLayout.U8));
            // The Bystial book (BookMessages.cs, WP-M2-8, protocol 21).
            var bookReason = MemoryPackLayout.Fixed(sizeof(BookReason));
            var card = MemoryPackLayout.Object(MemoryPackLayout.U16, MemoryPackLayout.Fixed(sizeof(Shared.Data.Tables.BystialTier)),
                MemoryPackLayout.U8, MemoryPackLayout.Fixed(sizeof(long)), MemoryPackLayout.Fixed(sizeof(long)));
            MemoryPack<BookState>(specs, ReliableMessageId.BookState, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.Fixed(sizeof(BookCause)), MemoryPackLayout.U8,
                    MemoryPackLayout.Array(card, BookLimits.MaxCards)));
            MemoryPack<BookChangeRequest>(specs, ReliableMessageId.BookChangeRequest, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U16, MemoryPackLayout.U8));
            MemoryPack<BookChangeResult>(specs, ReliableMessageId.BookChangeResult, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, bookReason));
            // Outpost quests and runs (QuestMessages.cs, WP-M2-6, protocol 22).
            var kind = MemoryPackLayout.Fixed(sizeof(Shared.Data.Tables.QuestKind));
            var action = MemoryPackLayout.Fixed(sizeof(QuestRunAction));
            var exp = MemoryPackLayout.Fixed(sizeof(long));
            var offer = MemoryPackLayout.Object(MemoryPackLayout.U8, kind, MemoryPackLayout.U16, MemoryPackLayout.U16,
                MemoryPackLayout.U16, exp, MemoryPackLayout.U16);
            MemoryPack<QuestBoard>(specs, ReliableMessageId.QuestBoard, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U32, MemoryPackLayout.Fixed(sizeof(int)),
                    MemoryPackLayout.Array(offer, QuestMessages.MaxOffers)));
            MemoryPack<QuestRunRequest>(specs, ReliableMessageId.QuestRunRequest, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U32, action, MemoryPackLayout.U32, MemoryPackLayout.U8,
                    MemoryPackLayout.Array(MemoryPackLayout.U8, QuestMessages.MaxRunSteps)));
            MemoryPack<QuestRunResult>(specs, ReliableMessageId.QuestRunResult, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, action, reason, MemoryPackLayout.U8, MemoryPackLayout.U16, exp));
            // Consumable use (spec 3.4, 9.13; WP-M3-3, protocol 29).
            MemoryPack<UseItemRequest>(specs, ReliableMessageId.UseItemRequest, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U8, MemoryPackLayout.U8));
            MemoryPack<UseItemResult>(specs, ReliableMessageId.UseItemResult, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U16, MemoryPackLayout.Fixed(sizeof(UseItemReason))));
            // Harvesting (spec 5.2, 9.13; WP-M3-5, protocol 30).
            MemoryPack<HarvestRequest>(specs, ReliableMessageId.HarvestRequest, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U32));
            MemoryPack<HarvestResult>(specs, ReliableMessageId.HarvestResult, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U32, MemoryPackLayout.U16, MemoryPackLayout.U16,
                    MemoryPackLayout.Fixed(sizeof(HarvestReason))));
            var step = MemoryPackLayout.Object(kind, MemoryPackLayout.U16, MemoryPackLayout.U16, MemoryPackLayout.U16,
                MemoryPackLayout.U16, exp, MemoryPackLayout.Fixed(sizeof(RunStepState)));
            MemoryPack<RunStatus>(specs, ReliableMessageId.RunStatus, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.Fixed(sizeof(RunState)), MemoryPackLayout.U16,
                    MemoryPackLayout.U16, exp, MemoryPackLayout.Array(step, QuestMessages.MaxRunSteps)));
            // Jobs (JobMessages.cs, WP-M3-1, protocol 28).
            var jobSlot = MemoryPackLayout.Fixed(sizeof(Shared.Simulation.Jobs.JobSlot));
            var jobEntry = MemoryPackLayout.Object(MemoryPackLayout.U8, jobSlot, MemoryPackLayout.U16, MemoryPackLayout.U32,
                MemoryPackLayout.U32, MemoryPackLayout.U16);
            MemoryPack<JobState>(specs, ReliableMessageId.JobState, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.Array(jobEntry, ReliableMessages.MaxJobEntries),
                    MemoryPackLayout.Fixed(sizeof(JobCause))));
            MemoryPack<JobRequest>(specs, ReliableMessageId.JobRequest, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U32, MemoryPackLayout.U8, jobSlot));
            MemoryPack<JobResult>(specs, ReliableMessageId.JobResult, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, jobSlot, MemoryPackLayout.U8, MemoryPackLayout.Fixed(sizeof(JobReason))));
            // The forge engine (ForgeMessages.cs, WP-M3-6, protocol 31).
            var forgeKind = MemoryPackLayout.Fixed(sizeof(ForgeKind));
            var forgeReason = MemoryPackLayout.Fixed(sizeof(ForgeReason));
            var forgeInput = MemoryPackLayout.Object(MemoryPackLayout.U8, MemoryPackLayout.U8);
            MemoryPack<ForgeStart>(specs, ReliableMessageId.ForgeStart, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U32, forgeKind, MemoryPackLayout.U16,
                    MemoryPackLayout.Array(forgeInput, ReliableMessages.MaxForgeInputs)));
            MemoryPack<ForgeAction>(specs, ReliableMessageId.ForgeAction, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U8));
            MemoryPack<ForgeFinish>(specs, ReliableMessageId.ForgeFinish, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32));
            MemoryPack<ForgeCancel>(specs, ReliableMessageId.ForgeCancel, ReliableDirection.ClientToServer,
                MemoryPackLayout.Object(MemoryPackLayout.U32));
            MemoryPack<ForgeState>(specs, ReliableMessageId.ForgeState, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, MemoryPackLayout.U32, forgeReason, MemoryPackLayout.Fixed(sizeof(ForgePhase)),
                    forgeKind, MemoryPackLayout.U16, MemoryPackLayout.U32, MemoryPackLayout.Array(MemoryPackLayout.U8, ReliableMessages.MaxForgeActions),
                    MemoryPackLayout.U8, MemoryPackLayout.U16, MemoryPackLayout.U16, MemoryPackLayout.U8, MemoryPackLayout.U8));
            MemoryPack<ForgeResult>(specs, ReliableMessageId.ForgeResult, ReliableDirection.ServerToClient,
                MemoryPackLayout.Object(MemoryPackLayout.U32, forgeReason, forgeKind, MemoryPackLayout.U16, MemoryPackLayout.U16,
                    MemoryPackLayout.U16, MemoryPackLayout.U8, MemoryPackLayout.U16, MemoryPackLayout.Fixed(sizeof(long)), MemoryPackLayout.Boolean, MemoryPackLayout.Boolean));
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
