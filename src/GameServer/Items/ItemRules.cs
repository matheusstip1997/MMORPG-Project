using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>A monster type's loot table in roll form (R-4.3-01).</summary>
public sealed record MonsterLoot(string TableId, int Rolls, int NothingWeight, IReadOnlyList<LootChoice> Choices);

/// <summary>
/// The item and loot tables in runtime form (backlog G): item definitions by id and net id, the Normal bag, each monster
/// type's loot table, and the ground drop settings. Built once from validated tables.
/// </summary>
public sealed class ItemRules
{
    private readonly Dictionary<string, ItemDef> _byId;
    private readonly Dictionary<int, ItemDef> _byNetId;
    private readonly Dictionary<int, MonsterLoot> _lootByMonsterNetId;

    public ItemRules(IReadOnlyList<ItemDef> items, ContainerDef normalBag, IReadOnlyDictionary<int, MonsterLoot> lootByMonsterNetId,
        LootSettings settings, int lootLockMs, int dropExpiryMs)
    {
        _byId = items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        _byNetId = items.ToDictionary(i => i.NetId);
        _lootByMonsterNetId = new Dictionary<int, MonsterLoot>(lootByMonsterNetId);
        NormalBag = normalBag;
        Settings = settings;
        LootLockMs = lootLockMs;
        DropExpiryMs = dropExpiryMs;
    }

    /// <summary>The only container in M1 (R-4.1-02).</summary>
    public ContainerDef NormalBag { get; }

    public LootSettings Settings { get; }

    /// <summary>R-4.3-02 window in which a drop is locked to its owner (M1: the killer); <c>partyLootLockMs</c>.</summary>
    public int LootLockMs { get; }

    /// <summary>A ground drop nobody takes disappears after this (spec 12.7 <c>bagExpiryMs</c>).</summary>
    public int DropExpiryMs { get; }

    public bool TryGetItem(string id, out ItemDef item) => _byId.TryGetValue(id, out item!);

    public bool TryGetItem(int netId, out ItemDef item) => _byNetId.TryGetValue(netId, out item!);

    /// <summary>The loot table of a monster type by <c>MonsterDef.netId</c> (what <c>Killed.VictimDefId</c> carries).</summary>
    public bool TryGetLoot(int monsterNetId, out MonsterLoot loot) => _lootByMonsterNetId.TryGetValue(monsterNetId, out loot!);

    /// <summary>From tables that passed validation; null when the item tables are not loaded (worlds built without them).</summary>
    public static ItemRules? From(ConfigSet set)
    {
        if (!set.TryGet(typeof(ItemDef), out _) || !set.TryGet(typeof(LootSettings), out _))
            return null;
        var items = Rows<ItemDef>(set);
        var bag = ContainerDef.Find(set, ContainerDef.NormalBag)
                  ?? throw new InvalidOperationException($"ContainerDef has no '{ContainerDef.NormalBag}' row (R-4.1-02).");
        var rarities = Rows<RarityDef>(set).ToDictionary(r => r.Id, StringComparer.Ordinal);
        var monsters = Rows<MonsterDef>(set).ToDictionary(m => m.Id, StringComparer.Ordinal);
        var itemsById = items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var entries = Rows<LootTableEntry>(set);

        var loot = new Dictionary<int, MonsterLoot>();
        foreach (var table in Rows<LootTableDef>(set))
        {
            if (!monsters.TryGetValue(table.MonsterId, out var monster))
                continue;
            var choices = entries.Where(e => e.TableId == table.Id) // file order: the roll order (rule 6)
                .Select(e => new LootChoice((ushort)itemsById[e.ItemId].NetId, (byte)rarities[e.RarityId].Tier, e.Weight,
                    e.CountMin, e.CountMax))
                .ToList();
            loot.Add(monster.NetId, new MonsterLoot(table.Id, table.Rolls, table.NothingWeight, choices));
        }

        var starting = set.Single<StartingValuesM1>();
        return new ItemRules(items, bag, loot, set.Single<LootSettings>(), starting.PartyLootLockMs, starting.BagExpiryMs);
    }

    private static List<T> Rows<T>(ConfigSet set) where T : class
    {
        var rows = new List<T>();
        if (set.TryGet(typeof(T), out var table))
        {
            foreach (var row in table.Rows)
                rows.Add((T)row);
        }
        return rows;
    }
}
