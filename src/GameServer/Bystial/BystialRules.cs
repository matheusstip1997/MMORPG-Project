using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Bystial;
using Shared.Simulation.Stats;

namespace GameServer.Bystial;

/// <summary>One monster type's card (spec 14.2): its kill thresholds and bonus per tier, normal first.</summary>
public sealed class CardLadder
{
    internal CardLadder(MonsterDef monster, int[] thresholds, BystialBonus[] bonuses)
    {
        MonsterId = monster.Id;
        NetId = (ushort)monster.NetId;
        Family = monster.Family;
        Boss = monster.Boss;
        Thresholds = thresholds;
        Bonuses = bonuses;
    }

    public string MonsterId { get; }
    public ushort NetId { get; }
    public MonsterFamily Family { get; }
    public bool Boss { get; }

    /// <summary>Kills of normal, rare, ... in order (strictly rising, the table's rule).</summary>
    public IReadOnlyList<int> Thresholds { get; }

    /// <summary>The bonus of each tier, normal first.</summary>
    public IReadOnlyList<BystialBonus> Bonuses { get; }

    public BystialTier TopTier => (BystialTier)Thresholds.Count;

    /// <summary>The bonus a placed card of <paramref name="tier"/> gives.</summary>
    public BystialBonus BonusAt(BystialTier tier) => Bonuses[(int)tier - 1];
}

/// <summary>
/// The Bystial tables in runtime form (spec 14.2, WP-M2-8): each carded monster's ladder, the slots per rank, the family
/// set bonuses and the caps, with stat and element names resolved to <see cref="StatRules"/> indices. Built once from
/// validated tables (their reload class is Restart).
/// </summary>
public sealed class BystialRules
{
    private readonly Dictionary<string, CardLadder> _byMonster;
    private readonly Dictionary<int, CardLadder> _byNetId;
    private readonly int[] _slotsByRank;

    private BystialRules(List<CardLadder> ladders, int[] slotsByRank, FamilySetBonus[] setBonuses, BystialCaps caps, int combatStateMs)
    {
        CombatStateMs = combatStateMs;
        Ladders = ladders;
        _byMonster = ladders.ToDictionary(l => l.MonsterId, StringComparer.Ordinal);
        _byNetId = ladders.ToDictionary(l => (int)l.NetId);
        _slotsByRank = slotsByRank;
        SetBonuses = setBonuses;
        Caps = caps;
    }

    /// <summary>Every carded monster, in monster id order (rule 6).</summary>
    public IReadOnlyList<CardLadder> Ladders { get; }

    public IReadOnlyList<FamilySetBonus> SetBonuses { get; }

    public BystialCaps Caps { get; }

    /// <summary>The combat state of spec 12.7 (<c>StartingValuesM1.combatStateMs</c>): the book changes only out of it (R-14.2-06).</summary>
    public int CombatStateMs { get; }

    public bool TryGetLadder(string monsterId, out CardLadder ladder) => _byMonster.TryGetValue(monsterId, out ladder!);

    public bool TryGetLadder(int monsterNetId, out CardLadder ladder) => _byNetId.TryGetValue(monsterNetId, out ladder!);

    /// <summary>Book slots unlocked at <paramref name="rank"/> (R-14.2-04).</summary>
    public int SlotsFor(Rank rank) => (uint)rank < (uint)_slotsByRank.Length ? _slotsByRank[(int)rank] : 0;

    /// <summary>
    /// From tables that passed validation; null when the Bystial tables are not loaded (worlds built without them) or there
    /// are no combat rules to resolve the stats against.
    /// </summary>
    public static BystialRules? From(ConfigSet set, CombatConfig? combat)
    {
        if (combat == null || !set.TryGet(typeof(BystialCardDef), out var cards) || !set.TryGet(typeof(BystialSlots), out var slots)
            || !set.TryGet(typeof(BystialStatCap), out var statCaps) || !set.TryGet(typeof(BystialEffectCap), out var effectCaps))
            return null;

        var byMonster = new SortedDictionary<string, List<BystialCardDef>>(StringComparer.Ordinal);
        foreach (var row in cards.Rows)
        {
            var card = (BystialCardDef)row;
            if (!byMonster.TryGetValue(card.MonsterId, out var list))
                byMonster.Add(card.MonsterId, list = new List<BystialCardDef>());
            list.Add(card);
        }
        var ladders = new List<CardLadder>(byMonster.Count);
        foreach (var (monsterId, rows) in byMonster)
        {
            var monster = MonsterDef.Find(set, monsterId)
                          ?? throw new InvalidOperationException($"BystialCardDef names unknown monster '{monsterId}'");
            rows.Sort((a, b) => a.Tier.CompareTo(b.Tier));
            ladders.Add(new CardLadder(monster, rows.Select(r => r.KillsRequired).ToArray(),
                rows.Select(r => Resolve(combat, r.StatId, r.Flat, r.Percent, r.EffectKind, r.EffectElement, r.EffectValue)).ToArray()));
        }

        var slotsByRank = new int[Ranks.Count];
        foreach (var row in slots.Rows)
        {
            var rankSlots = (BystialSlots)row;
            slotsByRank[(int)rankSlots.Rank] = Math.Min(rankSlots.Slots, BookLimits.MaxSlots);
        }

        var setBonuses = new List<FamilySetBonus>();
        if (set.TryGet(typeof(BystialFamilyBonus), out var families))
        {
            foreach (var row in families.Rows)
            {
                var bonus = (BystialFamilyBonus)row;
                setBonuses.Add(new FamilySetBonus(bonus.Family, bonus.CardsPlaced,
                    Resolve(combat, bonus.StatId, bonus.Flat, bonus.Percent, bonus.EffectKind, bonus.EffectElement, bonus.EffectValue)));
            }
        }

        int statCount = combat.Stats.Count;
        var maxFlat = new int[statCount];
        var maxPercent = new int[statCount];
        foreach (var row in statCaps.Rows)
        {
            var cap = (BystialStatCap)row;
            if (combat.Stats.TryGetId(cap.StatId, out int statId))
                (maxFlat[statId], maxPercent[statId]) = (cap.MaxFlat, cap.MaxPercent);
        }
        var maxEffect = new int[BystialEffectKinds.ArrayLength];
        foreach (var row in effectCaps.Rows)
        {
            var cap = (BystialEffectCap)row;
            maxEffect[(int)cap.Kind] = cap.MaxValue;
        }
        return new BystialRules(ladders, slotsByRank, setBonuses.ToArray(), new BystialCaps(maxFlat, maxPercent, maxEffect),
            set.Single<StartingValuesM1>().CombatStateMs);
    }

    /// <summary>A row's bonus with its stat and its effect's stat as <see cref="StatRules"/> indices (-1 for none).</summary>
    private static BystialBonus Resolve(CombatConfig combat, string statId, int flat, int percent, BystialEffectKind kind,
        Element element, int value)
    {
        int stat = statId.Length > 0 && combat.Stats.TryGetId(statId, out int id) ? id : -1;
        int effectStat = kind switch
        {
            BystialEffectKind.ElementResistance when combat.ElementIndex(element) is var index and >= 0 => combat.ElementResistStat(index),
            BystialEffectKind.BuildUpResistance => combat.Ids.BuildUpResistPercent,
            _ => -1,
        };
        return new BystialBonus(stat, flat, percent, kind, effectStat, value);
    }
}
