using GameServer.Persistence;

namespace GameServer.Sessions;

/// <summary>Reads characters for logins (spec 2.1). Called from tasks the <see cref="SessionManager"/> starts.</summary>
public interface ICharacterSource
{
    /// <summary>The account's character in <paramref name="slot"/> of this realm (the ticket's slot, R-2.5-01), if any.</summary>
    Task<CharacterRecord?> FindBySlotAsync(long accountId, int slot);

    /// <summary>A fresh read of the row, taken after the ownership lock so it is the latest saved state.</summary>
    Task<CharacterRecord?> LoadAsync(long characterId);

    /// <summary>The character's items (backlog G; instances and the starter flag, WP-M2-1), read after the lock together with the row.</summary>
    Task<InventoryLoad> LoadItemsAsync(long characterId);

    /// <summary>The achievement counters and kill counts (WP-M2-3), read after the lock with the row; none by default.</summary>
    Task<ProgressionRecord> LoadProgressionAsync(long characterId) => Task.FromResult(ProgressionRecord.Empty);

    /// <summary>The learned skill-tree nodes and the hotbar (WP-M2-5), read after the lock with the row; none by default.</summary>
    Task<SkillRecord> LoadSkillsAsync(long characterId) => Task.FromResult(SkillRecord.Empty);
    /// <summary>The Bystial book's cards (WP-M2-8), read after the lock with the row; none by default.</summary>
    Task<BystialRecord> LoadBystialAsync(long characterId) => Task.FromResult(BystialRecord.Empty);
    /// <summary>The open runs (WP-M2-6), read after the lock with the row; none by default.</summary>
    Task<IReadOnlyList<RunRecord>> LoadRunsAsync(long characterId) => Task.FromResult<IReadOnlyList<RunRecord>>(Array.Empty<RunRecord>());
    /// <summary>The job records (WP-M3-1), read after the lock with the row; none by default.</summary>
    Task<JobRecord> LoadJobsAsync(long characterId) => Task.FromResult(JobRecord.Empty);
}

/// <summary><see cref="ICharacterSource"/> over the realm's <see cref="CharacterRepository"/>, <see cref="InventoryRepository"/>, <see cref="ProgressionRepository"/> and <see cref="BystialRepository"/>.</summary>
public sealed class RepositoryCharacterSource(CharacterRepository characters, InventoryRepository? items = null,
    ProgressionRepository? progression = null, SkillRepository? skills = null, BystialRepository? bystial = null,
    QuestRepository? quests = null, JobRepository? jobs = null) : ICharacterSource
{
    public Task<BystialRecord> LoadBystialAsync(long characterId) =>
        bystial?.LoadAsync(characterId) ?? Task.FromResult(BystialRecord.Empty);

    public async Task<CharacterRecord?> FindBySlotAsync(long accountId, int slot)
    {
        // At most 4 rows per account (R-2.1-05), so listing is as cheap as a dedicated query.
        var list = await characters.ListByAccountAsync(accountId);
        return list.FirstOrDefault(c => c.Slot == slot);
    }

    public Task<CharacterRecord?> LoadAsync(long characterId) => characters.LoadAsync(characterId);

    public Task<InventoryLoad> LoadItemsAsync(long characterId) =>
        items?.LoadInventoryAsync(characterId) ?? Task.FromResult(InventoryLoad.Empty);

    public Task<ProgressionRecord> LoadProgressionAsync(long characterId) =>
        progression?.LoadAsync(characterId) ?? Task.FromResult(ProgressionRecord.Empty);

    public Task<SkillRecord> LoadSkillsAsync(long characterId) =>
        skills?.LoadAsync(characterId) ?? Task.FromResult(SkillRecord.Empty);
    public Task<IReadOnlyList<RunRecord>> LoadRunsAsync(long characterId) =>
        quests?.LoadOpenAsync(characterId) ?? Task.FromResult<IReadOnlyList<RunRecord>>(Array.Empty<RunRecord>());

    public Task<JobRecord> LoadJobsAsync(long characterId) =>
        jobs?.LoadAsync(characterId) ?? Task.FromResult(JobRecord.Empty);
}
