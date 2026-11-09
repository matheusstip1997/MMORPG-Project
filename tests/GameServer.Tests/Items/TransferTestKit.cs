using Dapper;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;

namespace GameServer.Tests.Items;

/// <summary>
/// Shared helpers of the transfer and escrow integration tests (WP-M4-4): characters seeded through
/// <see cref="InventoryService"/> and a real save, worlds attached from the database, and the conservation check of the M4
/// plan section 1 (every item and gold over the characters, the held escrows and the mail double is unchanged; the move rows
/// of the ledger sum to zero; a character rebuilds from its audit ledger, R-7.1-06).
/// </summary>
internal sealed class TransferTestKit(NpgsqlDataSource realmDb, CharacterRepository characters, FakeClock clock)
{
    public const string Ash = "m1.demonAsh", Sword = "m2.trainingSword";
    public const byte Normal = 1, Safe = 2;

    private static readonly CharacterState Spawn = new(1, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

    public static readonly ItemRules Items = ItemRules.From(TestCombat.Tables())!;

    private readonly InventoryRepository _inventory = new(realmDb);
    private long _nextAccount = 1;

    public FakeClock Clock => clock;

    public static ItemDef Item(string id)
    {
        Assert.True(Items.TryGetItem(id, out var item));
        return item;
    }

    /// <summary>A character holding <paramref name="ash"/> demon ash, <paramref name="swords"/> sword instances and gold, committed.</summary>
    public async Task<CharacterRecord> CreateAsync(int ash = 0, int swords = 0, long gold = 0)
    {
        long account = _nextAccount++;
        var created = await characters.CreateAsync(new NewCharacter(account, 0, $"Trader{account}", "warrior",
            Spawn, Guid.NewGuid()));
        var character = created.Character!;
        var service = await AttachAsync(character.CharacterId);
        if (ash > 0)
            service.TryAdd(character.CharacterId, Item(Ash), 0, ash, $"seed:{account}:ash", InventoryReason.Admin, "test");
        if (swords > 0)
            service.TryAdd(character.CharacterId, Item(Sword), 1, swords, $"seed:{account}:sword", InventoryReason.Admin, "test");
        if (gold > 0)
            service.TryAddGold(character.CharacterId, gold, $"seed:{account}:gold", InventoryReason.Admin, "test");
        if (ash > 0 || swords > 0 || gold > 0)
        {
            var saveId = Guid.NewGuid();
            var batch = service.TakeBatch(character.CharacterId, saveId);
            Assert.Equal(SaveStatus.Saved, (await characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId,
                batch)).Status);
        }
        return (await characters.LoadAsync(character.CharacterId))!;
    }

    /// <summary>A world's service holding the characters as loaded from the database.</summary>
    public async Task<InventoryService> AttachAsync(params long[] ids)
    {
        var service = new InventoryService(Items, events: null) { MaxGold = 1_000_000 };
        foreach (long id in ids)
        {
            Assert.True(service.Attach(id, await _inventory.LoadInventoryAsync(id), "warrior", 20));
            service.AttachGold(id, (await characters.LoadAsync(id))!.Gold);
        }
        return service;
    }

    /// <summary>The slot of the first sword instance of a character in a service.</summary>
    public static Shared.Simulation.Items.InventoryLocation SwordAt(InventoryService service, long id)
    {
        var items = service.CurrentInventory(id);
        items.TryIndex(Safe, out int index);
        var bag = items.BagAt(index);
        for (int slot = 0; slot < bag.SlotCount; slot++)
        {
            if (bag[slot].IsInstance)
                return new Shared.Simulation.Items.InventoryLocation(Safe, slot);
        }
        throw new InvalidOperationException("no sword");
    }

    /// <summary>A save job of everything the character has pending in <paramref name="service"/>.</summary>
    public static SaveJob Job(InventoryService service, CharacterRecord character, SaveReason reason = SaveReason.Transfer,
        EscrowService? escrow = null)
    {
        var saveId = Guid.NewGuid();
        return new SaveJob(character.CharacterId, character.State, character.Version, saveId, reason,
            service.TakeBatch(character.CharacterId, saveId), Escrow: escrow?.TakeBatch(character.CharacterId, saveId));
    }

    public async Task<int> CountAsync(string sql, object? args = null)
    {
        await using var connection = await realmDb.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(sql, args);
    }

    public async Task ExecuteAsync(string sql, object? args = null)
    {
        await using var connection = await realmDb.OpenConnectionAsync();
        await connection.ExecuteAsync(sql, args);
    }

    /// <summary>
    /// Everything of value over <paramref name="ids"/>, the held escrows and the mail double (if it exists): stack units by item
    /// and rarity, instance ids, and gold. Equal before and after a case means nothing was duplicated or lost.
    /// </summary>
    public async Task<string> HoldingsAsync(params long[] ids)
    {
        await using var connection = await realmDb.OpenConnectionAsync();
        var stacks = await connection.QueryAsync<(string, short, long)>("""
            SELECT def_id, rarity, sum(count)::bigint FROM (
                SELECT def_id, rarity, count FROM item_stacks WHERE character_id = ANY(@ids)
                UNION ALL SELECT i.def_id, i.rarity, i.count FROM escrow_items i JOIN escrow e USING (escrow_id) WHERE e.state = 1 AND i.instance_id = 0
                UNION ALL SELECT def_id, rarity, count FROM test_mail_items WHERE instance_id = 0
            ) s GROUP BY def_id, rarity ORDER BY def_id, rarity
            """, new { ids });
        var instances = await connection.QueryAsync<long>("""
            SELECT instance_id FROM item_instances WHERE character_id = ANY(@ids)
            UNION ALL SELECT i.instance_id FROM escrow_items i JOIN escrow e USING (escrow_id) WHERE e.state = 1 AND i.instance_id <> 0
            UNION ALL SELECT instance_id FROM test_mail_items WHERE instance_id <> 0
            ORDER BY 1
            """, new { ids });
        long gold = await connection.ExecuteScalarAsync<long>("""
            SELECT coalesce((SELECT sum(gold) FROM characters WHERE character_id = ANY(@ids)), 0)
                 + coalesce((SELECT sum(gold) FROM escrow WHERE state = 1), 0)
                 + coalesce((SELECT sum(gold) FROM test_mail), 0)
            """, new { ids });
        return $"stacks [{string.Join(", ", stacks)}] instances [{string.Join(", ", instances)}] gold {gold}";
    }

    /// <summary>The mail double of WP-M4-3's <c>MailService.Send</c>: tables written inside the caller's transaction.</summary>
    public Task CreateMailDoubleAsync() => ExecuteAsync("""
        CREATE TABLE IF NOT EXISTS test_mail (op_key text PRIMARY KEY, to_character bigint NOT NULL, kind smallint NOT NULL, gold bigint NOT NULL);
        CREATE TABLE IF NOT EXISTS test_mail_items (op_key text NOT NULL REFERENCES test_mail (op_key), def_id text NOT NULL,
            rarity smallint NOT NULL, count integer NOT NULL, instance_id bigint NOT NULL);
        """);

    /// <summary>
    /// Gold conservation (R-4.5-02, M4 plan section 1): the ledger rows of the operations starting with <paramref name="opPrefix"/>
    /// are moves only, and they sum to zero (over characters and escrow: an escrow hold's minus is held in escrow).
    /// </summary>
    public async Task AssertGoldMovesAsync(string opPrefix, long heldOrMailed = 0)
    {
        await using var connection = await realmDb.OpenConnectionAsync();
        var rows = (await connection.QueryAsync<(long Delta, short Flow)>(
            "SELECT delta, flow FROM gold_ledger WHERE op_key LIKE @prefix", new { prefix = opPrefix + "%" })).ToList();
        Assert.All(rows, r => Assert.Equal((short)GoldFlow.Move, r.Flow));
        Assert.Equal(-heldOrMailed, rows.Sum(r => r.Delta));
    }

    /// <summary>R-7.1-06: a character's stacks, instances and gold rebuilt from its audit ledger and gold ledger equal its tables.</summary>
    public async Task AssertRebuildsFromAuditAsync(long id)
    {
        await using var connection = await realmDb.OpenConnectionAsync();
        var fromAudit = (await connection.QueryAsync<(string, short, long)>("""
            SELECT def_id, rarity, sum(count_delta)::bigint FROM audit_log WHERE character_id = @id AND instance_id IS NULL
            GROUP BY def_id, rarity HAVING sum(count_delta) <> 0 ORDER BY def_id, rarity
            """, new { id })).ToList();
        var stacks = (await connection.QueryAsync<(string, short, long)>("""
            SELECT def_id, rarity, sum(count)::bigint FROM item_stacks WHERE character_id = @id GROUP BY def_id, rarity ORDER BY def_id, rarity
            """, new { id })).ToList();
        Assert.Equal(stacks, fromAudit);
        var heldByAudit = (await connection.QueryAsync<long>("""
            SELECT instance_id FROM audit_log WHERE character_id = @id AND instance_id IS NOT NULL
            GROUP BY instance_id HAVING sum(count_delta) > 0 ORDER BY 1
            """, new { id })).ToList();
        var instances = (await connection.QueryAsync<long>(
            "SELECT instance_id FROM item_instances WHERE character_id = @id ORDER BY 1", new { id })).ToList();
        Assert.Equal(instances, heldByAudit);
        long ledger = await connection.ExecuteScalarAsync<long>("SELECT coalesce(sum(delta), 0) FROM gold_ledger WHERE character_id = @id", new { id });
        long gold = await connection.ExecuteScalarAsync<long>("SELECT gold FROM characters WHERE character_id = @id", new { id });
        Assert.Equal(gold, ledger);
    }

    /// <summary>A trigger that raises for inserts into <paramref name="table"/> of <paramref name="characterId"/>'s rows.</summary>
    public Task CrashOnAsync(string table, long characterId) => ExecuteAsync($"""
        CREATE OR REPLACE FUNCTION test_crash() RETURNS trigger AS $$
        BEGIN IF NEW.character_id = {characterId} THEN RAISE EXCEPTION 'simulated crash'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql;
        CREATE TRIGGER test_crash BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION test_crash();
        """);

    public Task NoCrashAsync(string table) => ExecuteAsync($"DROP TRIGGER test_crash ON {table}");
}

/// <summary>The mail double: a <see cref="IEscrowMailSink"/> that writes the test tables in the caller's transaction.</summary>
internal sealed class TestMailSink : IEscrowMailSink
{
    public async Task WriteAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, long toCharacterId,
        short mailKind, IReadOnlyList<EscrowItemRow> items, long gold, string opKey, DateTime now, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO test_mail (op_key, to_character, kind, gold) VALUES (@opKey, @toCharacterId, @mailKind, @gold)",
            new { opKey, toCharacterId, mailKind, gold }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO test_mail_items (op_key, def_id, rarity, count, instance_id) VALUES (@opKey, @DefId, @Rarity, @Count, @InstanceId)",
            items.Select(i => new { opKey, i.DefId, Rarity = (short)i.Rarity, i.Count, i.InstanceId }), transaction, cancellationToken: ct));
    }
}
