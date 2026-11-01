using Dapper;
using GameServer.Events;
using GameServer.Items;
using Npgsql;
using Shared.Data;
using Shared.Data.Tables;

/// <summary>
/// Dev only (wave 1 QA F6: the m3-loop load test's item seed, <c>BotScenario.seedItemIds</c>): gives an offline character
/// one item in the local dev realm database, the way <c>tools/qa/Set-BagItem.ps1</c> and <c>Set-BagInstance.ps1</c> do: a
/// stack, or a new instance with the id the game server would make, in the first free slot of the container that receives
/// the item's category (stacks the Normal bag, tools and molds the Safe bag's equipment area), with its
/// <c>inventory_ops</c> row (reason <c>Admin</c>, source <c>dev-seed</c>) and its <c>audit_log</c> add row (R-7.1-06: the
/// ledger still accounts for it). Its operation key is fixed per character and item, so a second run gives nothing more.
/// It goes around <c>InventoryService</c> (the only item writer in the game, rule 4) on purpose, as test data. Never for a
/// shared or real database: <c>DbMigrate</c> takes it only with <c>--dev</c>, and only a Debug build has the <c>--give</c>
/// argument (Release compiles this class for the tests but has no path to it).
/// </summary>
internal static class DevItems
{
    public const string Source = "dev-seed";

    public enum Outcome { Given, AlreadyGiven, NoCharacter, UnknownItem, BadCount, NoContainer, NoFreeSlot }

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
        var container = Rows<ContainerDef>(tables).FirstOrDefault(c => c.ReceivesNewItems && c.Accept(item.Category));
        if (container == null)
            return Outcome.NoContainer;

        await using var connection = await realm.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        long? characterId = await connection.QuerySingleOrDefaultAsync<long?>(
            "SELECT character_id FROM characters WHERE name = @characterName AND deleted_at IS NULL FOR UPDATE",
            new { characterName }, transaction);
        if (characterId is not { } id)
            return Outcome.NoCharacter;
        string opKey = OpKey(id, itemId);
        if (await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM inventory_ops WHERE op_key = @opKey)", new { opKey }, transaction))
            return Outcome.AlreadyGiven;
        int? slot = await connection.QuerySingleOrDefaultAsync<int?>("""
            SELECT g FROM generate_series(0, @slots - 1) g
            WHERE NOT EXISTS (SELECT 1 FROM item_stacks s WHERE s.character_id = @id AND s.container = @code AND s.slot = g)
              AND NOT EXISTS (SELECT 1 FROM item_instances i WHERE i.character_id = @id AND i.container = @code AND i.slot = g)
            ORDER BY g LIMIT 1
            """, new { slots = container.Slots, id, code = (short)container.Code }, transaction);
        if (slot is not { } free)
            return Outcome.NoFreeSlot;

        var saveId = Guid.NewGuid();
        long? instanceId = null;
        if (instance)
        {
            long seq = await connection.ExecuteScalarAsync<long>(
                "UPDATE characters SET item_instance_seq = item_instance_seq + 1 WHERE character_id = @id RETURNING item_instance_seq",
                new { id }, transaction);
            instanceId = ItemInstanceIds.Make(id, seq);
            await connection.ExecuteAsync("""
                INSERT INTO item_instances (instance_id, character_id, container, slot, def_id, rarity, stolen, non_tradable, properties,
                    created_at, updated_at)
                VALUES (@instanceId, @id, @code, @free, @itemId, 0, false, false, CAST(@properties AS jsonb), now(), now())
                """, new { instanceId, id, code = (short)container.Code, free = (short)free, itemId, properties = ItemInstanceIds.NewProperties(id) },
                transaction);
        }
        else
        {
            await connection.ExecuteAsync("""
                INSERT INTO item_stacks (character_id, container, slot, def_id, rarity, stolen, count, created_at, updated_at)
                VALUES (@id, @code, @free, @itemId, 0, false, @count, now(), now())
                """, new { id, code = (short)container.Code, free = (short)free, itemId, count }, transaction);
        }
        await connection.ExecuteAsync("""
            INSERT INTO inventory_ops (op_key, character_id, reason, source, save_id, created_at)
            VALUES (@opKey, @id, @reason, @Source, @saveId, now())
            """, new { opKey, id, reason = InventoryReason.Admin.ToString(), Source, saveId }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO audit_log (op_key, seq, character_id, kind, container, slot, def_id, rarity, stolen, count_delta, count_after, save_id,
                created_at, instance_id)
            VALUES (@opKey, 0, @id, 0, @code, @free, @itemId, 0, false, @count, @count, @saveId, now(), @instanceId)
            """, new { opKey, id, code = (short)container.Code, free = (short)free, itemId, count, saveId, instanceId }, transaction);
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

    private static IEnumerable<T> Rows<T>(ConfigSet tables) =>
        tables.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>() : Enumerable.Empty<T>();
}
