using Dapper;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using Npgsql;
using Shared.Data;
using Shared.Data.Tables;

/// <summary>
/// Dev only (wave 1 QA F6: the m3-loop load test's item seed, <c>BotScenario.seedItemIds</c>): gives an offline character
/// one item in the local dev realm database. The item goes through <see cref="InventoryService"/>, the only item writer
/// (rule 4; wave 2 fixer): the character's items are loaded and attached as at a login, <c>TryAdd</c> picks the container
/// that receives the item's category (stacks the Normal bag, merging into a stack of it; tools and molds the Safe bag's
/// equipment area), makes the instance id and records the operation (reason <c>Admin</c>, source <c>dev-seed</c>) with its
/// audit rows (R-7.1-06), and the batch it hands out is written by the same <see cref="InventoryRepository.WriteAsync"/> a
/// character save uses, in one transaction. Its operation key is fixed per character and item, so a second run gives
/// nothing more. Never for a shared or real database: <c>DbMigrate</c> takes it only with <c>--dev</c>, and only a Debug
/// build has the <c>--give</c> argument (Release compiles this class for the tests but has no path to it).
/// </summary>
internal static class DevItems
{
    public const string Source = "dev-seed";

    public enum Outcome { Given, AlreadyGiven, NoCharacter, UnknownItem, BadCount, NoContainer, NoFreeSlot, Unloadable }

    /// <summary>The operation key of one character's seed of one item: a second run finds it and gives nothing.</summary>
    public static string OpKey(long characterId, string itemId) => $"{Source}:{characterId}:{itemId}";

    /// <summary>Gives <paramref name="count"/> of <paramref name="itemId"/> (an instance item: exactly 1) to the character.</summary>
    public static async Task<Outcome> GiveAsync(NpgsqlDataSource realm, ConfigSet tables, string characterName, string itemId, int count)
    {
        if (ItemDef.Find(tables, itemId) is not { } item)
            return Outcome.UnknownItem;
        bool instance = ItemCategories.IsInstance(item.Category);
        if (count < 1 || count > item.MaxStack || (instance && count != 1))
            return Outcome.BadCount;
        if (ItemRules.From(tables) is not { } rules || rules.ReceiverOf(item.Category) == null)
            return Outcome.NoContainer;

        await using var connection = await realm.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // The character row stays locked to the commit, so no world save of it runs between the load and the write.
        long? characterId = await connection.QuerySingleOrDefaultAsync<long?>(
            "SELECT character_id FROM characters WHERE name = @characterName AND deleted_at IS NULL FOR UPDATE",
            new { characterName }, transaction);
        if (characterId is not { } id)
            return Outcome.NoCharacter;
        string opKey = OpKey(id, itemId);
        if (await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM inventory_ops WHERE op_key = @opKey)", new { opKey }, transaction))
            return Outcome.AlreadyGiven;

        var inventory = new InventoryService(rules, events: null);
        if (!inventory.Attach(id, await new InventoryRepository(realm).LoadInventoryAsync(id), "", 1))
            return Outcome.Unloadable;
        if (inventory.TryAdd(id, item, 0, count, opKey, InventoryReason.Admin, Source).Added < count)
            return Outcome.NoFreeSlot; // a partial add is never written: the service is dropped with it
        var saveId = Guid.NewGuid();
        var batch = inventory.TakeBatch(id, saveId) ?? throw new InvalidOperationException("an add left no batch");
        await InventoryRepository.WriteAsync(connection, transaction, id, batch, saveId, DateTime.UtcNow, CancellationToken.None);
        // The row's version moves, as --set-level's: a world that still holds the character fails its save instead of
        // writing its own bag over the seed.
        await connection.ExecuteAsync("UPDATE characters SET version = version + 1, updated_at = now() WHERE character_id = @id",
            new { id }, transaction);
        await transaction.CommitAsync();
        return Outcome.Given;
    }

    /// <summary>The command line's --give: every (name, item, count) in order, printed; exit code 0, or 1 on any refusal.</summary>
    public static async Task<int> RunAsync(string realmConnection, IReadOnlyList<(string Name, string Item, int Count)> gives)
    {
        var loaded = GameServer.Host.Config.ConfigLoader.LoadAndValidate(Path.Combine("data", "tables"), ConfigTables.All);
        if (!loaded.Ok)
        {
            Console.Error.WriteLine("--give needs the config tables (run it from the repository root).");
            return 2;
        }
        await using var realm = NpgsqlDataSource.Create(realmConnection);
        int exit = 0;
        foreach (var (name, item, count) in gives)
        {
            var outcome = await GiveAsync(realm, loaded.Set, name, item, count);
            bool ok = outcome is Outcome.Given or Outcome.AlreadyGiven;
            (ok ? Console.Out : Console.Error).WriteLine($"--give {name} {item} {count}: {outcome}");
            if (!ok)
                exit = 1;
        }
        return exit;
    }
}
