using System.Text.Json;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Progression;
using Shared.Simulation.Stats;

namespace GameServer.Progression;

/// <summary>A character's level, exp, rank and unspent skill points as the world holds them now.</summary>
public readonly record struct ProgressSnapshot(int Level, long Exp, Rank Rank, int SkillPoints);

/// <summary>
/// A character's free attribute points as committed (R-3.4-08, WP-M2-13): the unspent points and the points placed on STR,
/// DEX, STA and INT. A placement or reset shows here only once its save committed (rule 5); a level-up's points at once.
/// </summary>
public readonly record struct AttributeSnapshot(int Unspent, int Str, int Dex, int Sta, int Int)
{
    /// <summary>Points placed on attribute <paramref name="attribute"/> (<c>Attributes.Str</c> ... <c>Attributes.Int</c>).</summary>
    public int Placed(int attribute) => attribute switch
    {
        Attributes.Str => Str,
        Attributes.Dex => Dex,
        Attributes.Sta => Sta,
        Attributes.Int => Int,
        _ => 0,
    };
}

/// <summary>
/// The only writer of progression state (spec 3.6, WP-M2-3; architecture rule 4 in spirit): level, exp, rank, skill
/// points, the achievement counters and the kill counts per monster type (R-14.2-01) of every character held by this world,
/// in memory on the world thread. <see cref="ExpService"/>, <see cref="AchievementService"/> and <see cref="RankService"/>
/// change it through here.
/// <para>
/// Saving (R-7.1-01 class 2): a change marks the character class 2 dirty; the next character save writes level, exp, rank
/// and skill points in the row (every save captures them) and takes this store's batch (<see cref="IProgressionBatchSource"/>):
/// the counters when they changed, the kill counts that changed as absolute values, and the rank-ups with their audit rows,
/// all in the save's transaction. A crash loses at most the last batch, never duplicates (R-7.1-02).
/// </para>
/// <para>
/// Free attribute points (R-3.4-08, WP-M2-13): the store is also the only writer of the unspent and placed attribute points.
/// A level from 2 adds unspent points; a placement (<see cref="PlaceAttribute"/>) or a respec's reset
/// (<see cref="ResetAttributes"/>) changes the working values at once, asks for a save, and becomes visible
/// (<see cref="TryGetAttributes"/>: stats, the client) only when that save committed, with its <c>attribute_ops</c> row.
/// </para>
/// </summary>
public sealed class ProgressionStore : IProgressionBatchSource
{
    private readonly ProgressionRules _rules;
    private readonly Dictionary<long, CharacterProgress> _characters = new();

    public ProgressionStore(ProgressionRules rules)
    {
        _rules = rules;
    }

    public ProgressionRules Rules => _rules;

    /// <summary>Marks a character class 2 dirty with its save scheduler (set by the SessionManager).</summary>
    public Action<long>? MarkClass2 { get; set; }

    /// <summary>Asks for a save at the next tick (level-up and rank-up, R-7.1-05; set by the SessionManager).</summary>
    public Action<long, SaveReason>? RequestSave { get; set; }

    /// <summary>The character's progression changed: its client should hear of it (set by the world).</summary>
    public Action<long, ProgressionCause>? Changed { get; set; }

    /// <summary>A save committed a rank-up of this character (rule 5: only now is it announced; set by the world).</summary>
    public Action<long, RankUpRecord>? RankUpCommitted { get; set; }

    /// <summary>A save committed an attribute placement or reset of this character (rule 5: only now is it visible; set by the world).</summary>
    public Action<long, AttributeOpRecord>? AttributeOpCommitted { get; set; }

    public int Count => _characters.Count;

    public bool IsAttached(long characterId) => _characters.ContainsKey(characterId);

    /// <summary>Starts holding a character's progression as loaded at login (the row's level, exp, rank and points, plus the counters).</summary>
    public void Attach(long characterId, in CharacterState state, ProgressionRecord record)
    {
        if (_characters.ContainsKey(characterId))
            throw new InvalidOperationException($"character {characterId} is already attached");
        var progress = new CharacterProgress(characterId)
        {
            Level = Math.Max(1, Math.Min(state.Level, _rules.Curves.MaxLevel)),
            Exp = state.Exp,
            Rank = state.Rank,
            SkillPoints = state.SkillPoints,
            AttributePoints = Math.Max(0, state.AttributePoints),
            VisibleAttributePoints = Math.Max(0, state.AttributePoints),
        };
        progress.Placed[Attributes.Str] = Math.Max(0, state.StrPlaced);
        progress.Placed[Attributes.Dex] = Math.Max(0, state.DexPlaced);
        progress.Placed[Attributes.Sta] = Math.Max(0, state.StaPlaced);
        progress.Placed[Attributes.Int] = Math.Max(0, state.IntPlaced);
        Array.Copy(progress.Placed, progress.VisiblePlaced, Attributes.Count);
        if (progress.Level >= _rules.Curves.MaxLevel)
            progress.Exp = 0;
        foreach (var kill in record.KillCounts)
        {
            if (kill.Count > 0)
                progress.Kills[kill.MonsterDefId] = kill.Count;
        }
        ReadCounters(progress, record.AchievementCountersJson);
        _characters.Add(characterId, progress);
    }

    /// <summary>Drops a character (it left the world). Unsaved changes of a faulted character are lost with it.</summary>
    public void Detach(long characterId) => _characters.Remove(characterId);

    public ProgressSnapshot Snapshot(long characterId)
    {
        var p = Get(characterId);
        return new ProgressSnapshot(p.Level, p.Exp, p.Rank, p.SkillPoints);
    }

    public bool TryGetSnapshot(long characterId, out ProgressSnapshot snapshot)
    {
        snapshot = default;
        if (!_characters.TryGetValue(characterId, out var p))
            return false;
        snapshot = new ProgressSnapshot(p.Level, p.Exp, p.Rank, p.SkillPoints);
        return true;
    }

    /// <summary>The character's committed free attribute points (R-3.4-08); false when the character is not held.</summary>
    public bool TryGetAttributes(long characterId, out AttributeSnapshot attributes)
    {
        attributes = default;
        if (!_characters.TryGetValue(characterId, out var p))
            return false;
        attributes = new AttributeSnapshot(p.VisibleAttributePoints, p.VisiblePlaced[Attributes.Str], p.VisiblePlaced[Attributes.Dex],
            p.VisiblePlaced[Attributes.Sta], p.VisiblePlaced[Attributes.Int]);
        return true;
    }

    /// <summary>
    /// The working values every save writes in the character row (<c>SessionManager.Capture</c>): the unspent and placed
    /// attribute points including the operations not committed yet.
    /// </summary>
    public AttributeSnapshot WorkingAttributes(long characterId)
    {
        var p = Get(characterId);
        return new AttributeSnapshot(p.AttributePoints, p.Placed[Attributes.Str], p.Placed[Attributes.Dex], p.Placed[Attributes.Sta],
            p.Placed[Attributes.Int]);
    }

    /// <summary>The character's level (also the level of its attributes, read by <c>Stats/StatsService</c>); 0 when not held.</summary>
    public int GetLevel(long characterId) => _characters.TryGetValue(characterId, out var p) ? p.Level : 0;

    /// <summary>Kills of one monster type (R-14.2-01: what the Bystial book reads, by <c>MonsterDef.id</c>).</summary>
    public long KillCount(long characterId, string monsterDefId) =>
        Get(characterId).Kills.TryGetValue(monsterDefId, out var count) ? count : 0;

    /// <summary>The kill counts of a character by monster id (ordinal order).</summary>
    public IReadOnlyDictionary<string, long> KillCounts(long characterId) => Get(characterId).Kills;

    /// <summary>The value of an achievement counter (R-3.6-05), as <see cref="RankService"/> checks it.</summary>
    public long CounterValue(long characterId, AchievementDef counter) => Value(Get(characterId), counter);

    /// <summary>
    /// Adds exp and takes the level-ups it pays for (R-3.6-01; several at once allowed), with
    /// <c>skillPointsPerLevel</c> points each (R-3.5-04) and <c>attributePointsPerLevel</c> free attribute points each
    /// (R-3.4-08: every level gained is a level from 2). Returns the level before.
    /// </summary>
    public int AddExp(long characterId, long gain, out int levelsGained)
    {
        var p = Get(characterId);
        int before = p.Level;
        int level = p.Level;
        long exp = p.Exp;
        levelsGained = _rules.Curves.Apply(ref level, ref exp, gain);
        if (level == p.Level && exp == p.Exp)
            return before;
        p.Level = level;
        p.Exp = exp;
        p.SkillPoints += levelsGained * _rules.Curves.SkillPointsPerLevel;
        int attributePoints = levelsGained * _rules.Curves.AttributePointsPerLevel;
        p.AttributePoints += attributePoints;
        p.VisibleAttributePoints += attributePoints; // a gain, shown with the level-up like the skill points
        MarkClass2?.Invoke(characterId);
        if (levelsGained > 0)
            RequestSave?.Invoke(characterId, SaveReason.LevelUp);
        Changed?.Invoke(characterId, levelsGained > 0 ? ProgressionCause.LevelUp : ProgressionCause.Exp);
        return before;
    }

    /// <summary>Counts a monster kill: the total, the type's count (R-14.2-01) and, for a boss, the boss kills.</summary>
    public void RecordKill(long characterId, string monsterDefId, bool boss)
    {
        var p = Get(characterId);
        p.Kills[monsterDefId] = (p.Kills.TryGetValue(monsterDefId, out var count) ? count : 0) + 1;
        p.DirtyKills.Add(monsterDefId);
        Add(p, AchievementCategory.MonsterKills, null);
        if (boss)
            Add(p, AchievementCategory.BossKills, null);
        MarkClass2?.Invoke(characterId);
    }

    /// <summary>
    /// Counts an achievement event of <paramref name="category"/> (runs claimed, outposts visited; R-3.6-05). A distinct
    /// counter counts <paramref name="key"/> once. Returns whether a counter changed.
    /// </summary>
    public bool Record(long characterId, AchievementCategory category, string key)
    {
        var p = Get(characterId);
        if (!Add(p, category, key))
            return false;
        MarkClass2?.Invoke(characterId);
        return true;
    }

    /// <summary>
    /// Applies a checked rank-up (only <see cref="RankService"/> calls it): one rank up, the op recorded for the next save
    /// with the counters it was checked against, and that save asked for at once.
    /// </summary>
    internal Rank ApplyRankUp(long characterId, string opKey)
    {
        var p = Get(characterId);
        var from = p.Rank;
        p.Rank = from + 1;
        p.PendingRankUps.Add(new RankUpRecord(opKey, from, p.Rank, CountersJson(p)));
        MarkClass2?.Invoke(characterId);
        RequestSave?.Invoke(characterId, SaveReason.RankUp);
        return p.Rank;
    }

    /// <summary>
    /// Spends unspent skill points for a checked skill-tree operation (only <c>Skills/SkillTreeService</c> calls it, WP-M2-5;
    /// R-3.5-04). The points column goes into the same save as the skill batch, which the service asks for at once, so
    /// points and nodes commit together. False and nothing spent when the character has fewer points.
    /// </summary>
    internal bool TrySpendSkillPoints(long characterId, int points)
    {
        var p = Get(characterId);
        if (points < 0 || p.SkillPoints < points)
            return false;
        p.SkillPoints -= points;
        MarkClass2?.Invoke(characterId);
        return true;
    }

    /// <summary>Gives skill points back (a respec, R-3.5-04: free; only <c>Skills/SkillTreeService</c> calls it).</summary>
    internal void RefundSkillPoints(long characterId, int points)
    {
        if (points < 0)
            throw new ArgumentOutOfRangeException(nameof(points));
        Get(characterId).SkillPoints += points;
        MarkClass2?.Invoke(characterId);
    }

    /// <summary>
    /// Places one free attribute point on <paramref name="attribute"/> (R-3.4-08, WP-M2-13; the world's request handler is the
    /// only caller). <paramref name="blocked"/> is "in combat or during an action", <paramref name="baseValue"/> the
    /// attribute's class base and growth, <paramref name="statMax"/> its <c>StatDef</c> max. Returns the answer to send now:
    /// a refusal, or the first answer of a repeated <paramref name="opKey"/>; null when the placement was applied (or is
    /// already waiting) and is answered once its save committed (rule 5). The save is asked for at once.
    /// </summary>
    internal AttributeReason? PlaceAttribute(long characterId, int attribute, string opKey, bool blocked, int baseValue, int statMax)
    {
        if (!_characters.TryGetValue(characterId, out var p))
            return AttributeReason.AttributeUnavailable;
        if (p.SeenAttributeOps.TryGetValue(opKey, out var seen))
            return seen;
        if (p.WaitingAttributeOps.Contains(opKey))
            return null;
        int placed = attribute >= 0 && attribute < Attributes.Count ? p.Placed[attribute] : 0;
        var reason = AttributePoints.Check(true, attribute, blocked, p.AttributePoints, (long)baseValue + placed, statMax);
        if (reason != AttributeReason.None)
        {
            p.SeenAttributeOps[opKey] = reason;
            return reason;
        }
        var before = (int[])p.Placed.Clone();
        int pointsBefore = p.AttributePoints;
        p.AttributePoints--;
        p.Placed[attribute]++;
        p.PendingAttributeOps.Add(new AttributeOpRecord(opKey, AttributeOpKind.Place, attribute, 1, pointsBefore, p.AttributePoints,
            before, (int[])p.Placed.Clone()));
        p.WaitingAttributeOps.Add(opKey);
        MarkClass2?.Invoke(characterId);
        RequestSave?.Invoke(characterId, SaveReason.AttributePoints);
        return null;
    }

    /// <summary>
    /// Gives every placed attribute point back, unspent (R-3.4-08, user decision 2026-10-29: together with the skill respec,
    /// free). Only <c>Skills/SkillTreeService</c> calls it, inside a respec it applies: the reset rides on the save the respec
    /// asks for, so both commit in one transaction. Returns the points given back (0: nothing placed, no row written).
    /// Its <c>attribute_ops</c> key is <see cref="AttributeResetKey"/> of the respec's skill op key.
    /// </summary>
    internal int ResetAttributes(long characterId, string respecOpKey)
    {
        string opKey = AttributeResetKey(respecOpKey);
        var p = Get(characterId);
        int total = 0;
        for (int a = 0; a < Attributes.Count; a++)
            total += p.Placed[a];
        if (total == 0)
            return 0;
        var before = (int[])p.Placed.Clone();
        int pointsBefore = p.AttributePoints;
        p.AttributePoints += total;
        Array.Clear(p.Placed, 0, Attributes.Count);
        p.PendingAttributeOps.Add(new AttributeOpRecord(opKey, AttributeOpKind.Reset, -1, total, pointsBefore, p.AttributePoints,
            before, new int[Attributes.Count]));
        MarkClass2?.Invoke(characterId);
        return total;
    }

    /// <summary>The idempotency key of the attribute reset a skill respec carries (R-3.4-08: the respec's own key, suffixed).</summary>
    public static string AttributeResetKey(string respecOpKey) => respecOpKey + ":attributes";

    /// <summary>True once the rank-up <paramref name="opKey"/> is in a committed save (rule 5: then it may be confirmed).</summary>
    public bool IsRankUpCommitted(long characterId, string opKey)
    {
        if (!_characters.TryGetValue(characterId, out var p) || !p.SeenRankOps.ContainsKey(opKey))
            return false;
        foreach (var op in p.PendingRankUps)
        {
            if (op.OpKey == opKey)
                return false;
        }
        if (p.InFlight is { } inFlight)
        {
            foreach (var op in inFlight.Batch.RankUps)
            {
                if (op.OpKey == opKey)
                    return false;
            }
        }
        return true;
    }

    internal bool TryGetSeenRankOp(long characterId, string opKey, out RankUpResult result) =>
        Get(characterId).SeenRankOps.TryGetValue(opKey, out result);

    internal void RememberRankOp(long characterId, string opKey, RankUpResult result) =>
        Get(characterId).SeenRankOps[opKey] = result;

    public ProgressionBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var p)
            || (!p.CountersDirty && p.DirtyKills.Count == 0 && p.PendingRankUps.Count == 0 && p.PendingAttributeOps.Count == 0))
            return null;
        if (p.InFlight != null)
            throw new InvalidOperationException($"character {characterId} already has a progression batch in flight");
        var kills = new List<MonsterKillCount>(p.DirtyKills.Count);
        foreach (var id in p.DirtyKills) // sorted: rows in a fixed order (rule 6)
            kills.Add(new MonsterKillCount(id, p.Kills[id]));
        var batch = new ProgressionBatch(p.CountersDirty ? CountersJson(p) : null, kills, p.PendingRankUps.ToList(),
            p.PendingAttributeOps.ToList());
        p.InFlight = new InFlightBatch(saveId, batch);
        p.CountersDirty = false;
        p.DirtyKills.Clear();
        p.PendingRankUps.Clear();
        p.PendingAttributeOps.Clear();
        return batch;
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var p) || p.InFlight is not { } inFlight || inFlight.SaveId != saveId)
            return; // a save without a batch, or a stale completion: idempotent
        p.InFlight = null;
        foreach (var rankUp in inFlight.Batch.RankUps)
            RankUpCommitted?.Invoke(characterId, rankUp);
        foreach (var op in inFlight.Batch.AttributeOpList)
        {
            // The visible values move by the op's delta: a level-up's points added meanwhile are already in them.
            if (op.Kind == AttributeOpKind.Place)
            {
                p.VisibleAttributePoints -= op.Points;
                p.VisiblePlaced[op.Attribute] += op.Points;
                p.WaitingAttributeOps.Remove(op.OpKey);
                p.SeenAttributeOps[op.OpKey] = AttributeReason.None;
            }
            else
            {
                p.VisibleAttributePoints += op.Points;
                Array.Clear(p.VisiblePlaced, 0, Attributes.Count);
            }
            AttributeOpCommitted?.Invoke(characterId, op);
        }
    }

    /// <summary>The counters as stored in <c>characters.achievement_counters</c>: <c>{"v":1,"counts":{...},"keys":{...}}</c>.</summary>
    internal static string CountersJson(CharacterProgress p)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteStartObject("counts");
            for (int c = 0; c < p.Counts.Length; c++)
            {
                if (p.Counts[c] != 0)
                    writer.WriteNumber(CategoryName((AchievementCategory)c), p.Counts[c]);
            }
            writer.WriteEndObject();
            writer.WriteStartObject("keys");
            foreach (var (category, keys) in p.Keys) // sorted by category
            {
                writer.WriteStartArray(CategoryName(category));
                foreach (var key in keys)
                    writer.WriteStringValue(key);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void ReadCounters(CharacterProgress p, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("counts", out var counts) && counts.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in counts.EnumerateObject())
            {
                if (TryCategory(property.Name, out var category) && property.Value.TryGetInt64(out var value))
                    p.Counts[(int)category] = value;
            }
        }
        if (root.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in keys.EnumerateObject())
            {
                if (!TryCategory(property.Name, out var category) || property.Value.ValueKind != JsonValueKind.Array)
                    continue;
                var set = KeysOf(p, category);
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        set.Add(item.GetString()!);
                }
            }
        }
    }

    /// <summary>Adds one to a counter; for a distinct counter only when <paramref name="key"/> is new. True when it changed.</summary>
    private bool Add(CharacterProgress p, AchievementCategory category, string? key)
    {
        var counter = _rules.Counter(category);
        if (counter == null)
            return false; // the table has no counter for it: nothing to count
        if (counter.Kind == AchievementKind.Distinct)
        {
            if (category == AchievementCategory.MonsterTypesKilled || key == null || !KeysOf(p, category).Add(key))
                return false; // types come from the kill counts
        }
        else
        {
            p.Counts[(int)category]++;
        }
        p.CountersDirty = true;
        return true;
    }

    private static long Value(CharacterProgress p, AchievementDef counter)
    {
        if (counter.Category == AchievementCategory.MonsterTypesKilled)
            return p.Kills.Count;
        if (counter.Kind == AchievementKind.Distinct)
            return p.Keys.TryGetValue(counter.Category, out var keys) ? keys.Count : 0;
        return p.Counts[(int)counter.Category];
    }

    private static SortedSet<string> KeysOf(CharacterProgress p, AchievementCategory category)
    {
        if (!p.Keys.TryGetValue(category, out var set))
        {
            set = new SortedSet<string>(StringComparer.Ordinal);
            p.Keys.Add(category, set);
        }
        return set;
    }

    /// <summary>camelCase category names, as the config tables spell them; stored in the database, never renamed.</summary>
    private static string CategoryName(AchievementCategory category)
    {
        var name = category.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static bool TryCategory(string name, out AchievementCategory category)
    {
        category = default;
        return name.Length > 0 && Enum.TryParse(char.ToUpperInvariant(name[0]) + name[1..], ignoreCase: false, out category)
               && Enum.IsDefined(category);
    }

    private CharacterProgress Get(long characterId) =>
        _characters.TryGetValue(characterId, out var p)
            ? p
            : throw new KeyNotFoundException($"character {characterId} has no progression attached");

    internal sealed record InFlightBatch(Guid SaveId, ProgressionBatch Batch);

    internal sealed class CharacterProgress(long characterId)
    {
        public long CharacterId { get; } = characterId;
        public int Level { get; set; }
        public long Exp { get; set; }
        public Rank Rank { get; set; }
        public int SkillPoints { get; set; }

        /// <summary>Counts of the count-kind categories, by <see cref="AchievementCategory"/>.</summary>
        public long[] Counts { get; } = new long[ProgressionRules.CategoryCount];

        /// <summary>The keys of the distinct-kind categories (outposts visited), sorted.</summary>
        public SortedDictionary<AchievementCategory, SortedSet<string>> Keys { get; } = new();

        /// <summary>Kills per monster type (R-14.2-01), by <c>MonsterDef.id</c>.</summary>
        public SortedDictionary<string, long> Kills { get; } = new(StringComparer.Ordinal);

        public SortedSet<string> DirtyKills { get; } = new(StringComparer.Ordinal);
        public bool CountersDirty { get; set; }
        public List<RankUpRecord> PendingRankUps { get; } = new();
        public InFlightBatch? InFlight { get; set; }

        /// <summary>Rank-up keys accepted since the character was attached, with their answer (rule 5: a repeat gets the first).</summary>
        public Dictionary<string, RankUpResult> SeenRankOps { get; } = new(StringComparer.Ordinal);

        /// <summary>Unspent free attribute points with the operations not committed yet (R-3.4-08; saved in the row).</summary>
        public int AttributePoints { get; set; }

        /// <summary>Points placed on STR, DEX, STA, INT with the operations not committed yet (saved in the row).</summary>
        public int[] Placed { get; } = new int[Attributes.Count];

        /// <summary>The committed unspent points (what the client and stats see; rule 5).</summary>
        public int VisibleAttributePoints { get; set; }

        /// <summary>The committed placed points (the <c>Attribute</c> stat source).</summary>
        public int[] VisiblePlaced { get; } = new int[Attributes.Count];

        public List<AttributeOpRecord> PendingAttributeOps { get; } = new();

        /// <summary>Placement keys applied and not committed yet: a repeat meanwhile is not answered (the commit answers it).</summary>
        public HashSet<string> WaitingAttributeOps { get; } = new(StringComparer.Ordinal);

        /// <summary>Placement keys answered since the character was attached (a repeat gets the first answer).</summary>
        public Dictionary<string, AttributeReason> SeenAttributeOps { get; } = new(StringComparer.Ordinal);
    }
}
