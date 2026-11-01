namespace GameServer.Persistence;

/// <summary>What a <see cref="CraftOpRecord"/> did (<c>craft_ops.kind</c>). Stored as a smallint: append only (socket and removal: WP-M3-9).</summary>
public enum CraftOpKind : short
{
    /// <summary>A mold craft (R-5.3-01, R-5.3-02).</summary>
    Craft = 1,
    /// <summary>A refine recipe (R-5.3-06).</summary>
    Refine = 2,
    /// <summary>A tier conversion (R-5.2-04), success or failure.</summary>
    Convert = 3,
    /// <summary>A mold recipe: a mold item made (R-5.3-01).</summary>
    Mold = 4,
    /// <summary>An enchant attempt (R-5.3-04, WP-M3-8): the stones consumed, the level from <c>EnchantFrom</c> to <c>EnchantTo</c>.</summary>
    Enchant = 5,
}

/// <summary>
/// One committed forge operation (<c>craft_ops</c>, WP-M3-6): the audit row and, with <see cref="OpKey"/>, the idempotency key
/// (rule 5). <see cref="Inputs"/> are the consumed units as <c>itemId:rarity:count</c>; <see cref="Seed"/> is the logged seed
/// of its random numbers (rule 6); <see cref="RecipientId"/> the character that got the output (spec 5.4: the owner and the
/// recipient differ under a contract, M4); <see cref="AtUtcMs"/> the world clock's time of the operation. <see cref="InputsKept"/>: a failed
/// conversion whose inputs the job tree's keep roll kept (nothing consumed). <see cref="EnchantFrom"/> and <see cref="EnchantTo"/>: an enchant's level before and
/// after (WP-M3-8; <see cref="DefId"/> and <see cref="OutputItemId"/> are the item, <see cref="OutputInstanceId"/> its instance).
/// </summary>
public sealed record CraftOpRecord(string OpKey, CraftOpKind Kind, string DefId, int Tier, IReadOnlyList<string> Inputs,
    string OutputItemId, int OutputCount, long OutputInstanceId, int Quality, int Rarity, bool Success, ulong Seed, long RecipientId,
    long AtUtcMs, bool InputsKept = false, int EnchantFrom = 0, int EnchantTo = 0);

/// <summary>
/// A character's forge operations since its last save (WP-M3-6). Written in the same transaction as the character row and
/// the inventory batch that consumed the inputs and created the output, so they commit together (rule 5).
/// </summary>
public sealed record CraftBatch(IReadOnlyList<CraftOpRecord> Ops);

/// <summary>What a login reads of the forge: the conversions of the last days, for the daily cap (R-5.2-04).</summary>
public sealed record CraftRecord(IReadOnlyList<CraftConversion> Conversions)
{
    public static readonly CraftRecord Empty = new(Array.Empty<CraftConversion>());
}

/// <summary>One committed conversion: its output tier and time (UTC milliseconds).</summary>
public readonly record struct CraftConversion(int ToTier, long AtUtcMs);

/// <summary>
/// Hands a character's pending forge operations to the save being issued and learns how that save ended. Implemented by
/// <c>Crafting/ForgeService</c>; called on the world thread by <see cref="SaveScheduler"/>, after the inventory source.
/// </summary>
public interface ICraftBatchSource
{
    /// <summary>The operations not yet in a save, now part of save <paramref name="saveId"/>; null when there are none.</summary>
    CraftBatch? TakeBatch(long characterId, Guid saveId);

    /// <summary>Save <paramref name="saveId"/> committed: its operations are in the database.</summary>
    void Committed(long characterId, Guid saveId);
}
