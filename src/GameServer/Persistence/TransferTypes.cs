namespace GameServer.Persistence;

/// <summary>What a <c>transfer_ops</c> row records. Stored in the database: append only, never renumber.</summary>
public enum TransferKind : short
{
    /// <summary>A trade window swap (spec 4.5, WP-M4-4).</summary>
    Trade = 1,
}

/// <summary>
/// One transfer between two characters (spec 4.5, 7.1; WP-M4-4): its idempotency key (the <c>transfer_ops</c> primary key),
/// its kind, the two characters (<see cref="CharacterA"/> &lt; <see cref="CharacterB"/>, the lock order of the transaction) and
/// a JSON summary of what moved each way.
/// </summary>
public sealed record TransferRecord(string OpKey, TransferKind Kind, long CharacterA, long CharacterB, string SummaryJson)
{
    public static TransferRecord Create(string opKey, TransferKind kind, long first, long second, string summaryJson)
    {
        if (first == second)
            throw new ArgumentException("a transfer needs two characters", nameof(second));
        return new TransferRecord(opKey, kind, Math.Min(first, second), Math.Max(first, second), summaryJson);
    }
}

/// <summary>
/// A save of two characters in one database transaction (spec 7.1 R-7.1-02 over two characters; WP-M4-4): each side is an
/// ordinary <see cref="SaveJob"/> with every pending batch of that character, plus the <see cref="TransferRecord"/>. A retry
/// resends the identical job (both save ids), so a pair that committed is recognised.
/// </summary>
public readonly record struct TransferSaveJob(SaveJob A, SaveJob B, TransferRecord Transfer);

/// <summary>The result of a <see cref="TransferSaveJob"/>: one status per side. Either both were written or neither.</summary>
public readonly record struct TransferSaveResult(SaveResult A, SaveResult B)
{
    public bool Saved => A.Status == SaveStatus.Saved && B.Status == SaveStatus.Saved;
}

/// <summary>
/// A store that writes a whole <see cref="SaveJob"/> (every batch, escrow included) and a two-character
/// <see cref="TransferSaveJob"/> in one transaction; implemented by <see cref="CharacterRepository"/>. The worker uses it
/// when its store implements it (test stores of <see cref="ICharacterSaveStore"/> need not).
/// </summary>
public interface ITransferSaveStore
{
    Task<SaveResult> SaveJobAsync(SaveJob job, CancellationToken ct = default);

    /// <summary>
    /// Writes both sides and the <c>transfer_ops</c> row in one transaction, or nothing. A retry with the same save ids after an
    /// unknown outcome returns <see cref="SaveStatus.Saved"/> for both when the first attempt committed.
    /// </summary>
    Task<TransferSaveResult> SaveTransferAsync(TransferSaveJob job, CancellationToken ct = default);
}

/// <summary>Where the scheduler sends two-character jobs. Must never block the calling (tick) thread.</summary>
public interface ITransferJobSink
{
    void EnqueueTransfer(in TransferSaveJob job);
}
