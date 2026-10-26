namespace GameServer.Persistence;

/// <summary>
/// The saved, mutable part of a character: what the world copies out on its own thread when a save is due.
/// In schema v1 every save writes all of it (class 2 and volatile fields together, R-7.1-01).
/// </summary>
public readonly record struct CharacterState(
    int Level,
    long Exp,
    string WorldId,
    int PosXCm,
    int PosYCm,
    int PosZCm,
    int Facing,
    int Hp,
    int Mp,
    int Stamina)
{
    /// <summary>
    /// World id of a character that was never placed (a new character). The game server spawns it at the world's player
    /// spawn marker (backlog C2); there is no other spawn point to keep in sync.
    /// </summary>
    public const string NotPlacedWorldId = "";
}

/// <summary>A character row as loaded. Times are UTC milliseconds.</summary>
public sealed record CharacterRecord(
    long CharacterId,
    int RealmId,
    long AccountId,
    int Slot,
    string Name,
    string ClassId,
    CharacterState State,
    long Version,
    long CreatedAtUtcMs,
    long UpdatedAtUtcMs,
    long? DeletedAtUtcMs,
    long? PurgeAfterUtcMs)
{
    /// <summary>Soft deleted and inside the grace period (R-2.1-05).</summary>
    public bool IsPendingDeletion => DeletedAtUtcMs.HasValue;
}

/// <summary>
/// A create request. The caller has already checked the slot count, the name rules and the class
/// (<c>CharacterCreationConfig</c>, spec 9.10); the repository enforces uniqueness only.
/// <paramref name="RequestId"/> is the idempotency key: a double submit returns the same character.
/// </summary>
public sealed record NewCharacter(long AccountId, int Slot, string Name, string ClassId, CharacterState Initial, Guid RequestId);

public enum CreateCharacterStatus
{
    Created,
    /// <summary>Another character in the realm has this name, ignoring case (reason code <c>NameTaken</c>).</summary>
    NameTaken,
    /// <summary>The account already has a character in this slot (reason code <c>NoFreeSlot</c>).</summary>
    SlotTaken,
    /// <summary>The request ID was already used by another account; refused, nothing returned.</summary>
    RequestIdReused,
}

public readonly record struct CreateCharacterResult(CreateCharacterStatus Status, CharacterRecord? Character);

public enum SaveStatus
{
    Saved,
    /// <summary>The row's version is not the expected one: someone else wrote it. Nothing was written (R-7.1-04).</summary>
    VersionConflict,
    NotFound,
    /// <summary>The character is soft deleted; nothing was written.</summary>
    Deleted,
    /// <summary>
    /// An inventory operation key of the batch was applied before (rule 5): another writer applied it, or a bug reused a
    /// key. Nothing was written.
    /// </summary>
    DuplicateOperation,
}

/// <summary><see cref="Version"/> is the new version when saved, otherwise the version found in the database (0 if none).</summary>
public readonly record struct SaveResult(SaveStatus Status, long Version);

public enum DeleteStatus
{
    Deleted,
    /// <summary>No such character on this account (also returned for another account's character).</summary>
    NotFound,
    AlreadyDeleted,
}

/// <summary>What a persistence worker needs to save a character; implemented by <see cref="CharacterRepository"/>.</summary>
public interface ICharacterSaveStore
{
    /// <summary>
    /// Writes <paramref name="state"/> if the row is still at <paramref name="expectedVersion"/>.
    /// Retrying with the same <paramref name="saveId"/> after an unknown outcome is safe: if the first
    /// attempt committed, the result is <see cref="SaveStatus.Saved"/> with the version it wrote.
    /// A non-empty <paramref name="inventory"/> batch is written in the same transaction (rule 5, R-7.1-01 class 2): it
    /// commits with the row or not at all.
    /// </summary>
    Task<SaveResult> SaveAsync(long characterId, CharacterState state, long expectedVersion, Guid saveId,
        InventoryBatch? inventory = null, CancellationToken ct = default);
}
