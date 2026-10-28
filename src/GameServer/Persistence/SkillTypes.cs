using Shared.Protocol;

namespace GameServer.Persistence;

/// <summary>A learned node and its level (<c>character_skills</c>, R-3.5-04).</summary>
public readonly record struct LearnedSkill(string NodeId, int Level);

/// <summary>
/// One committed skill operation (<c>skill_ops</c>): the audit row and, with <see cref="OpKey"/>, the idempotency key
/// (rule 5). <see cref="AdvanceTier"/> is the new class's tier for an advance, null otherwise.
/// </summary>
public sealed record SkillOpRecord(string OpKey, SkillTreeOp Op, string NodeId, string FromNodeId, int FromLevel, int ToLevel,
    int PointsBefore, int PointsAfter, string ClassFrom, string ClassTo, int? AdvanceTier);

/// <summary>
/// A character's skill changes since its last save (WP-M2-5): the whole learned set and hotbar as they are now (absolute,
/// so a retried save writes the same), the class when it changed, and the operations with their audit rows. Written in
/// the same transaction as the character row, which carries the unspent points, so points and nodes commit together.
/// </summary>
public sealed record SkillBatch(
    IReadOnlyList<LearnedSkill> Learned,
    string HotbarJson,
    string? ClassId,
    IReadOnlyList<SkillOpRecord> Ops);

/// <summary>The skill state a login reads besides the character row: the learned nodes and the saved hotbar (null: not set yet).</summary>
public sealed record SkillRecord(IReadOnlyList<LearnedSkill> Learned, string? HotbarJson)
{
    public static readonly SkillRecord Empty = new(Array.Empty<LearnedSkill>(), null);
}

/// <summary>
/// Hands a character's pending skill batch to the save being issued and learns how that save ended. Implemented by
/// <c>Skills/SkillTreeService</c>; called on the world thread by <see cref="SaveScheduler"/>.
/// </summary>
public interface ISkillBatchSource
{
    /// <summary>The changes not yet in a save, now part of save <paramref name="saveId"/>; null when there are none.</summary>
    SkillBatch? TakeBatch(long characterId, Guid saveId);

    /// <summary>Save <paramref name="saveId"/> committed: its batch is in the database.</summary>
    void Committed(long characterId, Guid saveId);
}
