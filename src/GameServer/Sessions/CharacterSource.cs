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
}

/// <summary><see cref="ICharacterSource"/> over the realm's <see cref="CharacterRepository"/> and <see cref="InventoryRepository"/>.</summary>
public sealed class RepositoryCharacterSource(CharacterRepository characters, InventoryRepository? items = null) : ICharacterSource
{
    public async Task<CharacterRecord?> FindBySlotAsync(long accountId, int slot)
    {
        // At most 4 rows per account (R-2.1-05), so listing is as cheap as a dedicated query.
        var list = await characters.ListByAccountAsync(accountId);
        return list.FirstOrDefault(c => c.Slot == slot);
    }

    public Task<CharacterRecord?> LoadAsync(long characterId) => characters.LoadAsync(characterId);

    public Task<InventoryLoad> LoadItemsAsync(long characterId) =>
        items?.LoadInventoryAsync(characterId) ?? Task.FromResult(InventoryLoad.Empty);
}
