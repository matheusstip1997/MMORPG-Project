using System;
using MemoryPack;
using Shared.Data.Tables;

namespace Shared.Protocol
{
    /// <summary>What a <see cref="SkillTreeRequest"/> asks for (spec 3.5, R-3.5-01/04; WP-M2-5). Append only.</summary>
    public enum SkillTreeOp : byte
    {
        None = 0,
        /// <summary>Learn a node at level 1 (not an evolution).</summary>
        Learn = 1,
        /// <summary>One more level of a learned node.</summary>
        Upgrade = 2,
        /// <summary>Learn an evolution: its base is replaced and the base's points are lost (R-3.5-04).</summary>
        Evolve = 3,
        /// <summary>Forget every node and get the points of the learned ones back (free, R-3.5-04).</summary>
        Respec = 4,
        /// <summary>Advance into the class named by the target (R-3.5-01: permanent).</summary>
        Advance = 5,
        /// <summary>Put a learned active or buff on a hotbar slot (spec 9.6).</summary>
        AssignSlot = 6,
        /// <summary>Empty a hotbar slot.</summary>
        ClearSlot = 7,
    }

    /// <summary>
    /// Why a skill-tree request was refused (spec 3.5 and 9.7 reason codes; text from <c>ReasonCodeStrings</c>). Append only;
    /// <see cref="ReliableMessages.LastSkillTreeReason"/> is the highest.
    /// </summary>
    public enum SkillTreeReason : byte
    {
        None = 0,
        /// <summary>The character's skills are not loaded (entering or leaving the world).</summary>
        Unavailable = 1,
        UnknownNode = 2,
        UnknownClass = 3,
        /// <summary>Upgrade, evolve from or place a node that is not learned.</summary>
        SkillNotLearned = 4,
        /// <summary>The node has no behaviour yet (no <c>SkillDef</c> row; the tier-1 trees beyond Swordman and Knight).</summary>
        SkillUnavailable = 5,
        /// <summary>The node's tree is not on the character's class line (R-3.5-04).</summary>
        WrongClassLine = 6,
        AlreadyLearned = 7,
        MaxLevelReached = 8,
        /// <summary>The character is below the node's minimum level (R-3.5-04).</summary>
        CharacterLevelTooLow = 9,
        NotEnoughPoints = 10,
        /// <summary>A required node is not learned at its required level (R-3.5-04).</summary>
        PrerequisiteMissing = 11,
        /// <summary>The node evolves another skill: it is learned with an evolve.</summary>
        EvolveRequired = 12,
        NotAnEvolution = 13,
        /// <summary>Another evolution of the same base skill was taken on this line (R-3.5-04: one of them).</summary>
        EvolutionTaken = 14,
        /// <summary>Evolving is blocked in combat, instances, boss fights and during an action (spec 3.5 evolve flow).</summary>
        EvolveBlockedCombat = 15,
        /// <summary>R-3.5-04: respec is blocked in combat, instances, boss fights (and during an action).</summary>
        RespecBlockedCombat = 16,
        NothingToRespec = 17,
        /// <summary>R-3.5-01: the character is below the class's unlock level.</summary>
        AdvanceLevelTooLow = 18,
        /// <summary>The class does not advance from the character's class (R-3.5-01).</summary>
        NotNextClass = 19,
        /// <summary>Advancing is blocked in combat and during an action (user decision 2026-10-27, WP-M2-5 plan).</summary>
        AdvanceBlockedCombat = 20,
        HotbarSlotInvalid = 21,
        /// <summary>A passive or a weapon unlock has no hotbar slot (R-3.5-03).</summary>
        NotForHotbar = 22,
    }

    /// <summary>Why a <see cref="SkillTreeState"/> was sent (the client toasts committed changes). Append only.</summary>
    public enum SkillTreeCause : byte
    {
        /// <summary>Entering the world (or a reconnect): no toast.</summary>
        Initial = 0,
        /// <summary>A committed learn, upgrade, evolve, respec or advance, or a hotbar change.</summary>
        Changed = 1,
    }

    /// <summary>One learned node and its level.</summary>
    [MemoryPackable]
    public sealed partial class SkillNodeEntry
    {
        public string NodeId { get; set; } = string.Empty;

        public byte Level { get; set; }
    }

    /// <summary>One filled hotbar slot (1-based, spec 9.6) and its node.</summary>
    [MemoryPackable]
    public sealed partial class HotbarEntry
    {
        public byte Slot { get; set; }

        public string NodeId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Server to client, reliable (spec 3.5, 9.6; WP-M2-5, protocol 20): the observer's class, learned nodes and hotbar. The
    /// client builds its predicted loadout from it with the same <c>LoadoutBuilder</c> as the server. Sent at enter world
    /// and after each committed change (rule 5), never in the hot snapshot. The unspent points come in
    /// <see cref="ProgressionState"/>. MemoryPack after the id byte; append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class SkillTreeState
    {
        /// <summary>Counts per connection; a newer state replaces an older one.</summary>
        public uint Version { get; set; }

        public string ClassId { get; set; } = string.Empty;

        public SkillNodeEntry[] Nodes { get; set; } = Array.Empty<SkillNodeEntry>();

        public HotbarEntry[] Hotbar { get; set; } = Array.Empty<HotbarEntry>();

        public SkillTreeCause Cause { get; set; }
    }

    /// <summary>
    /// Client to server, reliable (WP-M2-5, protocol 20): one skill-tree operation. <see cref="Target"/> is the node (learn,
    /// upgrade, evolve, assign) or the class (advance); empty for respec and clear. <see cref="Slot"/> is the hotbar slot for
    /// assign and clear. <see cref="RequestId"/> counts per connection: a repeat gets the first answer and changes nothing.
    /// Capped by its <c>ReliableMessageCaps</c> row.
    /// </summary>
    [MemoryPackable]
    public sealed partial class SkillTreeRequest
    {
        public uint RequestId { get; set; }

        public SkillTreeOp Op { get; set; }

        public string Target { get; set; } = string.Empty;

        public byte Slot { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M2-5, protocol 20): the answer to a <see cref="SkillTreeRequest"/>. A refusal comes at
    /// once; an accepted learn, upgrade, evolve, respec or advance only once its save committed, right before the
    /// <see cref="SkillTreeState"/> that shows it. <see cref="Level"/> is the node's level after it (0 otherwise).
    /// </summary>
    [MemoryPackable]
    public sealed partial class SkillTreeResult
    {
        public uint RequestId { get; set; }

        public SkillTreeOp Op { get; set; }

        public SkillTreeReason Reason { get; set; }

        public string Target { get; set; } = string.Empty;

        public byte Level { get; set; }
    }

    public static partial class ReliableMessages
    {
        /// <summary>Learned nodes a state carries at most (four tiers of trees).</summary>
        public const int MaxSkillNodes = 96;

        public const SkillTreeOp LastSkillTreeOp = SkillTreeOp.ClearSlot;
        public const SkillTreeReason LastSkillTreeReason = SkillTreeReason.NotForHotbar;
        public const SkillTreeCause LastSkillTreeCause = SkillTreeCause.Changed;

        public static bool TryRead(ReadOnlySpan<byte> message, out SkillTreeState state)
        {
            state = null!;
            if (!TryBody(message, ReliableMessageId.SkillTreeState, out SkillTreeState? value) || value == null
                || value.Cause > LastSkillTreeCause || value.ClassId == null || value.Nodes == null || value.Hotbar == null)
                return false;
            foreach (var node in value.Nodes)
            {
                if (node == null || node.Level == 0 || !SkillTree.IsAsciiId(node.NodeId ?? string.Empty))
                    return false;
            }
            foreach (var slot in value.Hotbar)
            {
                if (slot == null || slot.Slot == 0 || slot.Slot > ActionBindings.MaxSkillSlots || !SkillTree.IsAsciiId(slot.NodeId ?? string.Empty))
                    return false;
            }
            state = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out SkillTreeRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.SkillTreeRequest, out SkillTreeRequest? value) || value == null
                || value.Op == SkillTreeOp.None || value.Op > LastSkillTreeOp || value.Target == null
                || (value.Target.Length != 0 && !SkillTree.IsAsciiId(value.Target)))
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out SkillTreeResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.SkillTreeResult, out SkillTreeResult? value) || value == null
                || value.Op > LastSkillTreeOp || value.Reason > LastSkillTreeReason || value.Target == null)
                return false;
            result = value;
            return true;
        }
    }
}
