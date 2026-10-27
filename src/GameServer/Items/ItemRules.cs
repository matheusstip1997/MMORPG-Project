using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>A monster type's loot table in roll form (R-4.3-01).</summary>
public sealed record MonsterLoot(string TableId, int Rolls, int NothingWeight, IReadOnlyList<LootChoice> Choices);

/// <summary>
/// The item and loot tables in runtime form (backlog G; containers and equipment WP-M2-1): item definitions by id and net
/// id, the containers (R-4.1-02) with their rules for the shared <see cref="Inventory"/>, equipment slots, gear stat bonuses
/// and rarity percents (spec 4.1 M2 item model), each class's gear rules (R-3.5-07), each monster type's loot table, and the
/// ground drop settings. Built once from validated tables.
/// </summary>
public sealed class ItemRules : IItemRuleLookup
{
    private static readonly ItemStatBonus[] NoBonuses = Array.Empty<ItemStatBonus>();

    private readonly Dictionary<string, ItemDef> _byId;
    private readonly Dictionary<int, ItemDef> _byNetId;
    private readonly Dictionary<int, ItemRuleInfo> _ruleInfo;
    private readonly Dictionary<int, ItemStatBonus[]> _bonuses;
    private readonly Dictionary<int, MonsterLoot> _lootByMonsterNetId;
    private readonly Dictionary<string, ClassGearDef> _classGear;
    private readonly int[] _statPercentByTier;

    public ItemRules(IReadOnlyList<ItemDef> items, IReadOnlyList<ContainerDef> containers, IReadOnlyList<EquipSlotDef> equipSlots,
        IReadOnlyList<ItemStatBonusDef> bonuses, IReadOnlyList<RarityDef> rarities, IReadOnlyList<ClassGearDef> classGear,
        IReadOnlyDictionary<int, MonsterLoot> lootByMonsterNetId, LootSettings settings, int lootLockMs, int dropExpiryMs,
        int combatStateMs = 0)
    {
        _byId = items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        _byNetId = items.ToDictionary(i => i.NetId);
        _lootByMonsterNetId = new Dictionary<int, MonsterLoot>(lootByMonsterNetId);
        _classGear = classGear.ToDictionary(c => c.Id, StringComparer.Ordinal);

        Containers = containers.OrderBy(c => c.Code).ToList(); // code order: the inventory's container order (rule 6)
        NormalBag = Containers.FirstOrDefault(c => c.Id == ContainerDef.NormalBag)
                    ?? throw new InvalidOperationException($"ContainerDef has no '{ContainerDef.NormalBag}' row (R-4.1-02).");
        Equipped = Containers.FirstOrDefault(c => c.Kind == ContainerKind.Equipped);
        ContainerRules = Containers.Select(c => new ContainerRule((byte)c.Code, c.Slots, c.Accepts, c.Kind == ContainerKind.Equipped))
            .ToArray();
        EquipSlots = equipSlots.OrderBy(s => s.Code).ToList();
        var slotCodes = EquipSlots.ToDictionary(s => s.Id, s => s.Code, StringComparer.Ordinal);
        WeaponSlotMask = (ushort)EquipSlots.Where(s => s.IsWeapon).Aggregate(0, (mask, s) => mask | (1 << s.Code));

        _ruleInfo = items.ToDictionary(i => i.NetId, i => new ItemRuleInfo(i.Category, i.MaxStack,
            (ushort)i.EquipSlots.Aggregate(0, (mask, id) => mask | (slotCodes.TryGetValue(id, out int code) ? 1 << code : 0))));
        _bonuses = bonuses.GroupBy(b => b.ItemId) // file order inside an item (rule 6)
            .Where(g => _byId.ContainsKey(g.Key))
            .ToDictionary(g => _byId[g.Key].NetId, g => g.Select(b => new ItemStatBonus(b.StatId, b.Flat, b.Percent)).ToArray());
        _statPercentByTier = new int[256];
        Array.Fill(_statPercentByTier, 100);
        foreach (var rarity in rarities)
            _statPercentByTier[rarity.Tier] = rarity.StatPercent;

        Settings = settings;
        LootLockMs = lootLockMs;
        DropExpiryMs = dropExpiryMs;
        CombatStateMs = combatStateMs;
    }

    /// <summary>The M1 bag: the old single-bag operations work on it (R-4.1-02).</summary>
    public ContainerDef NormalBag { get; }

    /// <summary>Every container, in code order.</summary>
    public IReadOnlyList<ContainerDef> Containers { get; }

    /// <summary>The containers as rules of the shared <see cref="Inventory"/>, in the same order.</summary>
    public IReadOnlyList<ContainerRule> ContainerRules { get; }

    /// <summary>The Equipped container (spec 4.1 M2 item model); null for tables without one.</summary>
    public ContainerDef? Equipped { get; }

    /// <summary>The equipment slots, in code order (slot index in the Equipped container).</summary>
    public IReadOnlyList<EquipSlotDef> EquipSlots { get; }

    /// <summary>The two weapon slots of R-3.5-07 as a slot mask.</summary>
    public ushort WeaponSlotMask { get; }

    public LootSettings Settings { get; }

    /// <summary>R-4.3-02 window in which a drop is locked to its owner (M1: the killer); <c>partyLootLockMs</c>.</summary>
    public int LootLockMs { get; }

    /// <summary>A ground drop nobody takes disappears after this (spec 12.7 <c>bagExpiryMs</c>).</summary>
    public int DropExpiryMs { get; }

    /// <summary>Spec 12.7 combat state: in combat until this long after the last hit (<c>StartingValuesM1.combatStateMs</c>); gear changes wait for it.</summary>
    public int CombatStateMs { get; }

    public bool TryGetItem(string id, out ItemDef item) => _byId.TryGetValue(id, out item!);

    public bool TryGetItem(int netId, out ItemDef item) => _byNetId.TryGetValue(netId, out item!);

    /// <summary>What the shared inventory rules need of an item. Explicit, so <c>TryGetItem(netId, out var item)</c> stays an <see cref="ItemDef"/>.</summary>
    bool IItemRuleLookup.TryGetItem(ushort itemNetId, out ItemRuleInfo info) => _ruleInfo.TryGetValue(itemNetId, out info);

    /// <summary>The equipment slot mask of an item (0 for non-equipment).</summary>
    public ushort EquipSlotMask(int itemNetId) => _ruleInfo.TryGetValue(itemNetId, out var info) ? info.EquipSlotMask : (ushort)0;

    /// <summary>The container code new items of a category go to (<c>receivesNewItems</c>), or null when none takes them.</summary>
    public ContainerDef? ReceiverOf(ItemCategory category) =>
        Containers.FirstOrDefault(c => c.ReceivesNewItems && c.Accept(category));

    /// <summary>The stat bonuses of a common instance of the item (<see cref="ItemStatBonusDef"/>), file order.</summary>
    public IReadOnlyList<ItemStatBonus> BaseBonuses(int itemNetId) =>
        _bonuses.TryGetValue(itemNetId, out var bonuses) ? bonuses : NoBonuses;

    /// <summary><c>RarityDef.statPercent</c> of a tier (100 for a tier with no row).</summary>
    public int StatPercent(byte tier) => _statPercentByTier[tier];

    /// <summary>The gear rules of a class (R-3.5-07), or null for a class without a row.</summary>
    public ClassGearDef? ClassGear(string classId) => _classGear.TryGetValue(classId, out var gear) ? gear : null;

    /// <summary>The loot table of a monster type by <c>MonsterDef.netId</c> (what <c>Killed.VictimDefId</c> carries).</summary>
    public bool TryGetLoot(int monsterNetId, out MonsterLoot loot) => _lootByMonsterNetId.TryGetValue(monsterNetId, out loot!);

    /// <summary>From tables that passed validation; null when the item tables are not loaded (worlds built without them).</summary>
    public static ItemRules? From(ConfigSet set)
    {
        if (!set.TryGet(typeof(ItemDef), out _) || !set.TryGet(typeof(LootSettings), out _))
            return null;
        var items = Rows<ItemDef>(set);
        var rarities = Rows<RarityDef>(set);
        var rarityById = rarities.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var monsters = Rows<MonsterDef>(set).ToDictionary(m => m.Id, StringComparer.Ordinal);
        var itemsById = items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var entries = Rows<LootTableEntry>(set);

        var loot = new Dictionary<int, MonsterLoot>();
        foreach (var table in Rows<LootTableDef>(set))
        {
            if (!monsters.TryGetValue(table.MonsterId, out var monster))
                continue;
            var choices = entries.Where(e => e.TableId == table.Id) // file order: the roll order (rule 6)
                .Select(e => new LootChoice((ushort)itemsById[e.ItemId].NetId, (byte)rarityById[e.RarityId].Tier, e.Weight,
                    e.CountMin, e.CountMax))
                .ToList();
            loot.Add(monster.NetId, new MonsterLoot(table.Id, table.Rolls, table.NothingWeight, choices));
        }

        var starting = set.Single<StartingValuesM1>();
        return new ItemRules(items, Rows<ContainerDef>(set), Rows<EquipSlotDef>(set), Rows<ItemStatBonusDef>(set), rarities,
            Rows<ClassGearDef>(set), loot, set.Single<LootSettings>(), starting.PartyLootLockMs, starting.BagExpiryMs,
            starting.CombatStateMs);
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
