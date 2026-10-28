using GameServer.Combat;
using GameServer.Economy;
using GameServer.Entities;
using GameServer.Items;
using GameServer.World.Map;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Util;

namespace GameServer.Quests;

/// <summary>
/// The outpost quest boards (R-5.5-01; spec 5.5 "The board is a cached snapshot"), on the world thread. Each outpost NPC that
/// offers quests has a zone, the monster types whose spawn points lie within <c>QuestBoardSettings.radiusCm</c> of it, found once
/// when the world is built. Its board belongs to an epoch, <c>world clock / refreshMs</c>: the first request of a new epoch
/// generates the board from the demand reward percents of that moment (R-4.2-03) and every later request of the epoch gets the
/// same cached offers, so no timer runs. The generator's stream is seeded from the world seed (logged at start, rule 6), the
/// outpost's marker id and the epoch, so a board can be reproduced.
/// </summary>
public sealed class QuestBoards
{
    private readonly QuestRules _rules;
    private readonly IDemandQuery? _demand;
    private readonly ulong _worldSeed;
    private readonly Dictionary<uint, Zone> _zones = new();

    public QuestBoards(QuestRules rules, IDemandQuery? demand, ulong worldSeed)
    {
        _rules = rules;
        _demand = demand;
        _worldSeed = worldSeed;
    }

    /// <summary>A generated board: the offers an accept of <see cref="Epoch"/> may name.</summary>
    public sealed record Board(uint NpcEntityId, string MarkerId, uint Epoch, IReadOnlyList<BoardOffer> Offers);

    /// <summary>Outposts with a zone.</summary>
    public int Count => _zones.Count;

    /// <summary>Gives <paramref name="npc"/> its zone (once, when the world is built).</summary>
    public void AddOutpost(Npc npc, IReadOnlyList<QuestCandidate> candidates) =>
        _zones.Add(npc.Id, new Zone(npc.MarkerId, candidates));

    /// <summary>The zone's monster types of an outpost (tests and tools).</summary>
    public IReadOnlyList<QuestCandidate> Candidates(uint npcEntityId) =>
        _zones.TryGetValue(npcEntityId, out var zone) ? zone.Candidates : Array.Empty<QuestCandidate>();

    public uint EpochAt(long nowMs) => (uint)(nowMs / _rules.Board.RefreshMs);

    /// <summary>Time from <paramref name="nowMs"/> to the next epoch.</summary>
    public int RefreshInMs(long nowMs) => (int)(_rules.Board.RefreshMs - nowMs % _rules.Board.RefreshMs);

    /// <summary>The board of <paramref name="npcEntityId"/> now, generated when its epoch began; null for an NPC without a zone.</summary>
    public Board? Current(uint npcEntityId, long nowMs)
    {
        if (!_zones.TryGetValue(npcEntityId, out var zone))
            return null;
        uint epoch = EpochAt(nowMs);
        if (zone.Board is { } cached && cached.Epoch == epoch)
            return cached;
        var rng = new Pcg32Rng(_worldSeed ^ Fnv1a(zone.MarkerId), epoch);
        var offers = QuestBoardGenerator.Generate(zone.Candidates, _rules.Kinds, _rules.Board.OffersPerBoard,
            c => _demand?.RewardPercent(c.Monster.NetId, c.RegionIndex) ?? DemandService.Neutral, rng);
        zone.Board = new Board(npcEntityId, zone.MarkerId, epoch, offers);
        return zone.Board;
    }

    /// <summary>The board as the client gets it.</summary>
    public QuestBoard Message(Board board, long nowMs) => new()
    {
        NpcEntityId = board.NpcEntityId,
        Epoch = board.Epoch,
        RefreshInMs = EpochAt(nowMs) == board.Epoch ? RefreshInMs(nowMs) : 0,
        Offers = board.Offers.Select(o => new QuestOffer
        {
            Slot = o.Slot,
            Kind = o.Kind,
            MonsterNetId = (ushort)o.Target.Monster.NetId,
            ItemNetId = (ushort)(o.Item?.NetId ?? 0),
            Count = (ushort)o.Count,
            RewardExp = o.RewardExp,
            RewardPercent = (ushort)Math.Min(o.RewardPercent, ushort.MaxValue),
        }).ToArray(),
    };

    /// <summary>
    /// The zone of an outpost at <paramref name="npc"/>: one candidate per (monster type, region) of the spawn points within the
    /// radius (bosses only with <c>includeBosses</c>), ordered by monster net id then region (rule 6); deliver items are the
    /// stackable non-quest drops of the monster's loot table, by net id; the quest item from <c>QuestItemDrop</c>.
    /// </summary>
    public static IReadOnlyList<QuestCandidate> ZoneOf(Npc npc, QuestRules rules, CombatRules combat, ItemRules items, WorldMap map,
        Func<MonsterDef, int> baseExp)
    {
        long radius = rules.Board.RadiusCm;
        var seen = new HashSet<(int, byte)>();
        var candidates = new List<QuestCandidate>();
        foreach (var point in combat.SpawnPoints)
        {
            if (!map.TryGetMarker(point.MarkerId, out var marker) || !combat.TryGetMonster(point.MonsterId, out var monster)
                || (monster.Boss && !rules.Board.IncludeBosses))
                continue;
            long dx = FixedPoint.ToCmRound(marker.Position.X) - npc.XCm;
            long dy = FixedPoint.ToCmRound(marker.Position.Y) - npc.YCm;
            if (dx * dx + dy * dy > radius * radius)
                continue;
            byte region = map.Grid.RegionIndexAt(marker.Position);
            if (!seen.Add((monster.NetId, region)))
                continue;
            candidates.Add(new QuestCandidate(monster, region, baseExp(monster), DeliverItemsOf(monster, items),
                rules.TryGetQuestItem(monster.Id, out var questItemId) && items.TryGetItem(questItemId, out var questItem) ? questItem : null));
        }
        candidates.Sort((a, b) => a.Monster.NetId != b.Monster.NetId
            ? a.Monster.NetId.CompareTo(b.Monster.NetId)
            : a.RegionIndex.CompareTo(b.RegionIndex));
        return candidates;
    }

    private static IReadOnlyList<ItemDef> DeliverItemsOf(MonsterDef monster, ItemRules items)
    {
        if (!items.TryGetLoot(monster.NetId, out var loot))
            return Array.Empty<ItemDef>();
        var result = new SortedDictionary<int, ItemDef>();
        foreach (var choice in loot.Choices)
        {
            if (items.TryGetItem(choice.ItemNetId, out var item) && ItemCategories.IsStackable(item.Category)
                && item.Category != ItemCategory.Quest && items.ReceiverOf(item.Category) != null)
                result[item.NetId] = item;
        }
        return result.Values.ToList();
    }

    /// <summary>FNV-1a 64 of the marker id: a stable per-outpost part of the seed (string.GetHashCode is per process).</summary>
    private static ulong Fnv1a(string text)
    {
        ulong hash = 14695981039346656037;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 1099511628211;
        }
        return hash;
    }

    private sealed class Zone(string markerId, IReadOnlyList<QuestCandidate> candidates)
    {
        public string MarkerId { get; } = markerId;
        public IReadOnlyList<QuestCandidate> Candidates { get; } = candidates;
        public Board? Board { get; set; }
    }
}
