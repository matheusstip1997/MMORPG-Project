using System.Text.Json;
using GameServer.Persistence;
using GameServer.Progression;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Skills;

namespace GameServer.Skills;

/// <summary>What one skill request asks for, as the world reads it in phase 1.</summary>
public readonly record struct SkillTreeAsk(uint RequestId, SkillTreeOp Op, string Target, int Slot, string OpKey, int Level, bool Blocked);

/// <summary>An operation whose save committed: its audit row and the answer the requesting connection gets.</summary>
public sealed record CommittedSkillOp(SkillOpRecord Record, SkillTreeResult Answer, int Connection);

/// <summary>
/// The only writer of the skill tree state of every character held by this world (spec 3.5, R-3.5-01/04; WP-M2-5;
/// architecture rule 4 in spirit): learned nodes, the hotbar layout and the class a character advanced into, in memory on
/// the world thread. Points are spent and refunded through <see cref="ProgressionStore"/>, which stays the only writer of
/// the counter.
/// <para>
/// Transactions (rule 5): learn, upgrade, evolve, respec and advance are checked by <see cref="SkillTreeRules"/>, applied to
/// the working state at once (so a second request sees them), and ask for a save at once. That save carries the points in
/// the row and this service's batch (the learned set, the hotbar, the class and the operations with their audit rows) in
/// one transaction. Only once it committed does the character see the change (<see cref="Visible"/>: loadout, stats, the
/// client's state) and get its answer. An op key seen before gets the first answer (double submit); a crash before the
/// commit loses the change in memory and in the database alike. Hotbar changes are save class 2 and visible at once.
/// </para>
/// </summary>
public sealed class SkillTreeService : ISkillBatchSource, ISkillStatSource
{
    private readonly SkillRules _rules;
    private readonly ProgressionStore _progression;
    private readonly Dictionary<long, CharacterSkills> _characters = new();

    public SkillTreeService(SkillRules rules, ProgressionStore progression)
    {
        _rules = rules;
        _progression = progression;
    }

    public SkillRules Rules => _rules;

    /// <summary>Marks a character class 2 dirty with its save scheduler (set by the SessionManager).</summary>
    public Action<long>? MarkClass2 { get; set; }

    /// <summary>Asks for a save at the next tick (a value operation, R-7.1-05; set by the SessionManager).</summary>
    public Action<long, SaveReason>? RequestSave { get; set; }

    /// <summary>A save committed a value operation of this character (set by the world: events, answer, loadout).</summary>
    public Action<long, CommittedSkillOp>? OpCommitted { get; set; }

    /// <summary>The character's visible state changed (a commit or a hotbar change; set by the world).</summary>
    public Action<long>? VisibleChanged { get; set; }

    public int Count => _characters.Count;

    public bool IsAttached(long characterId) => _characters.ContainsKey(characterId);

    /// <summary>
    /// Starts holding a character's skills as loaded at login. Nodes the tables no longer have, or that are off the class
    /// line, are dropped (TODO(open): give their points back). Without a saved hotbar the learned skills go on the default
    /// layout (spec 9.6), saved with the next batch.
    /// </summary>
    public void Attach(long characterId, string classId, SkillRecord record)
    {
        if (_characters.ContainsKey(characterId))
            throw new InvalidOperationException($"character {characterId} is already attached");
        var state = new SkillState(classId);
        foreach (var skill in record.Learned)
        {
            var node = _rules.Node(skill.NodeId);
            if (node != null && _rules.OnLine(classId, node.ClassId))
                state.SetLevel(skill.NodeId, Math.Min(skill.Level, node.MaxLevel));
        }
        bool dirty = false;
        if (record.HotbarJson != null)
            ReadHotbar(state, record.HotbarJson);
        else
        {
            foreach (var node in _rules.Nodes)
            {
                if (!state.IsLearned(node.Id))
                    continue;
                int slot = SkillTreeRules.DefaultSlot(_rules, state, node.Id, 0);
                if (slot != 0)
                    state.Place(slot, node.Id);
            }
            dirty = true;
        }
        _characters.Add(characterId, new CharacterSkills(state) { Dirty = dirty });
    }

    public void Detach(long characterId) => _characters.Remove(characterId);

    /// <summary>
    /// What the character has as far as the game is concerned: the committed nodes and class, and the current hotbar. The
    /// loadout, the passives, the usable weapons and the client's state are built from it. Null when not held.
    /// </summary>
    public SkillState? Visible(long characterId) => _characters.TryGetValue(characterId, out var c) ? c.Visible : null;

    /// <summary>The working state, with operations not yet committed (checks and tests).</summary>
    public SkillState? Working(long characterId) => _characters.TryGetValue(characterId, out var c) ? c.State : null;

    /// <summary>Operations applied but not yet in a committed save.</summary>
    public int WaitingCount(long characterId) => _characters.TryGetValue(characterId, out var c) ? c.Waiting.Count : 0;

    /// <summary>
    /// One request (phase 1). Returns the answer to send now (a refusal, a hotbar change, or a repeat of an answered key),
    /// or null when the operation was applied and waits for its save (answered through <see cref="OpCommitted"/>) or is a
    /// repeat of such a waiting key.
    /// </summary>
    public SkillTreeResult? Handle(long characterId, in SkillTreeAsk ask, int connection)
    {
        if (!_characters.TryGetValue(characterId, out var c) || !_progression.TryGetSnapshot(characterId, out var progress))
            return Answer(ask, SkillTreeReason.Unavailable, 0);
        if (c.Seen.TryGetValue(ask.OpKey, out var seen))
            return seen;
        if (c.Waiting.Contains(ask.OpKey))
            return null;

        var state = c.State;
        string target = ask.Target; // (an in parameter cannot be used in the lambdas)
        int points = progress.SkillPoints;
        SkillTreeReason reason;
        int cost;
        switch (ask.Op)
        {
            case SkillTreeOp.Learn:
                reason = SkillTreeRules.CheckLearn(_rules, state, ask.Target, ask.Level, points, out cost);
                if (reason != SkillTreeReason.None)
                    return Refuse(c, ask, reason);
                return Apply(characterId, c, ask, connection, cost, refund: 0, () => SkillTreeRules.ApplyLearn(_rules, state, target),
                    nodeId: ask.Target, fromNode: string.Empty, fromLevel: 0);
            case SkillTreeOp.Upgrade:
                reason = SkillTreeRules.CheckUpgrade(_rules, state, ask.Target, points, out cost);
                if (reason != SkillTreeReason.None)
                    return Refuse(c, ask, reason);
                int before = state.LevelOf(ask.Target);
                return Apply(characterId, c, ask, connection, cost, refund: 0, () => SkillTreeRules.ApplyUpgrade(state, target),
                    nodeId: ask.Target, fromNode: ask.Target, fromLevel: before);
            case SkillTreeOp.Evolve:
                reason = SkillTreeRules.CheckEvolve(_rules, state, ask.Target, ask.Level, points, ask.Blocked, out cost);
                if (reason != SkillTreeReason.None)
                    return Refuse(c, ask, reason);
                var node = _rules.Node(ask.Target)!;
                int baseLevel = state.LevelOf(node.EvolvesFrom);
                return Apply(characterId, c, ask, connection, cost, refund: 0, () => SkillTreeRules.ApplyEvolve(_rules, state, target),
                    nodeId: ask.Target, fromNode: node.EvolvesFrom, fromLevel: baseLevel);
            case SkillTreeOp.Respec:
                reason = SkillTreeRules.CheckRespec(_rules, state, ask.Blocked, out int refund);
                if (reason != SkillTreeReason.None)
                    return Refuse(c, ask, reason);
                return Apply(characterId, c, ask, connection, cost: 0, refund, () => SkillTreeRules.ApplyRespec(state),
                    nodeId: string.Empty, fromNode: string.Empty, fromLevel: state.LearnedCount); // fromLevel: the nodes cleared
            case SkillTreeOp.Advance:
                reason = SkillTreeRules.CheckAdvance(_rules, state, ask.Target, ask.Level, ask.Blocked);
                if (reason != SkillTreeReason.None)
                    return Refuse(c, ask, reason);
                return Apply(characterId, c, ask, connection, cost: 0, refund: 0, () => SkillTreeRules.ApplyAdvance(state, target),
                    nodeId: string.Empty, fromNode: string.Empty, fromLevel: 0);
            case SkillTreeOp.AssignSlot:
                // The hotbar names committed skills only: a skill waiting for its save is placed once it is in (rule 5).
                reason = SkillTreeRules.CheckAssign(_rules, c.Visible, ask.Slot, ask.Target);
                if (reason != SkillTreeReason.None)
                    return Refuse(c, ask, reason);
                state.Place(ask.Slot, ask.Target);
                return HotbarChanged(characterId, c, ask);
            case SkillTreeOp.ClearSlot:
                reason = SkillTreeRules.CheckClear(_rules, ask.Slot);
                if (reason != SkillTreeReason.None)
                    return Refuse(c, ask, reason);
                state.ClearSlot(ask.Slot);
                return HotbarChanged(characterId, c, ask);
            default:
                return Refuse(c, ask, SkillTreeReason.Unavailable);
        }
    }

    public string? ClassOf(long characterId) => Visible(characterId)?.ClassId;

    public bool GetPassives(long characterId, List<SkillPassive> into)
    {
        if (Visible(characterId) is not { } state)
            return false;
        foreach (var pair in state.Learned)
        {
            var node = _rules.Node(pair.Key);
            if (node == null || node.Kind != SkillNodeKind.Passive || _rules.Skill(pair.Key) is not { } skill)
                continue;
            foreach (var modifier in skill.Modifiers)
                into.Add(new SkillPassive(modifier, pair.Value));
        }
        return true;
    }

    public ArmorClass? EffectiveArmor(long characterId) =>
        Visible(characterId) is { } state ? SkillTreeRules.EffectiveArmor(_rules, state) : null;

    /// <summary>R-3.5-07: the weapon types the character may equip (class line and learned unlocks); null when not held.</summary>
    public uint? UsableWeapons(long characterId) =>
        Visible(characterId) is { } state ? SkillTreeRules.UsableWeapons(_rules, state) : null;

    public SkillBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var c) || (!c.Dirty && c.Pending.Count == 0))
            return null;
        if (c.InFlight != null)
            throw new InvalidOperationException($"character {characterId} already has a skill batch in flight");
        var learned = new List<LearnedSkill>(c.State.LearnedCount);
        foreach (var pair in c.State.Learned) // sorted by node id: rows in a fixed order (rule 6)
            learned.Add(new LearnedSkill(pair.Key, pair.Value));
        var batch = new SkillBatch(learned, HotbarJson(c.State), c.State.ClassId, c.Pending.Select(p => p.Record).ToList());
        c.InFlight = new InFlightBatch(saveId, batch, c.Pending.ToList(), c.State.Clone());
        c.Pending.Clear();
        c.Dirty = false;
        return batch;
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var c) || c.InFlight is not { } inFlight || inFlight.SaveId != saveId)
            return; // a save without a batch, or a stale completion: idempotent
        c.InFlight = null;
        // The committed nodes and class become visible; the hotbar stays the current one (it may have changed since).
        var hotbar = c.Visible.Clone();
        c.Visible.CopyFrom(inFlight.Snapshot);
        CopyHotbar(c.State, c.Visible);
        foreach (var op in inFlight.Ops)
        {
            c.Waiting.Remove(op.Record.OpKey);
            c.Seen[op.Record.OpKey] = op.Answer;
            OpCommitted?.Invoke(characterId, new CommittedSkillOp(op.Record, op.Answer, op.Connection));
        }
        if (inFlight.Ops.Count > 0 || !hotbar.SameAs(c.Visible))
            VisibleChanged?.Invoke(characterId);
    }

    /// <summary>The hotbar as stored in <c>characters.hotbar</c>: <c>{"v":1,"slots":{"7":"warrior.slash"}}</c>.</summary>
    public static string HotbarJson(SkillState state)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteStartObject("slots");
            for (int slot = 1; slot <= ActionBindings.MaxSkillSlots; slot++)
            {
                string node = state.NodeAt(slot);
                if (node.Length != 0)
                    writer.WriteString(slot.ToString(System.Globalization.CultureInfo.InvariantCulture), node);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private void ReadHotbar(SkillState state, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Object)
                return;
            foreach (var slot in slots.EnumerateObject())
            {
                if (int.TryParse(slot.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number)
                    && number >= 1 && number <= _rules.Settings.HotbarSlots && slot.Value.ValueKind == JsonValueKind.String)
                {
                    string node = slot.Value.GetString() ?? string.Empty;
                    if (state.IsLearned(node) && _rules.Node(node)?.IsHotbarSkill() == true)
                        state.Place(number, node);
                }
            }
        }
        catch (JsonException)
        {
            // A hotbar that cannot be read is an empty one; the player places the skills again.
        }
    }

    private SkillTreeResult? Apply(long characterId, CharacterSkills c, in SkillTreeAsk ask, int connection, int cost, int refund,
        Action apply, string nodeId, string fromNode, int fromLevel)
    {
        int pointsBefore = _progression.Snapshot(characterId).SkillPoints;
        if (cost > 0 && !_progression.TrySpendSkillPoints(characterId, cost))
            return Refuse(c, ask, SkillTreeReason.NotEnoughPoints);
        if (refund > 0)
            _progression.RefundSkillPoints(characterId, refund);
        // R-3.4-08 (user decision 2026-10-29): a respec also gives every placed attribute point back, in the same save.
        if (ask.Op == SkillTreeOp.Respec)
            _progression.ResetAttributes(characterId, ask.OpKey);
        string classFrom = c.State.ClassId;
        apply();
        int pointsAfter = _progression.Snapshot(characterId).SkillPoints;
        int level = nodeId.Length != 0 ? c.State.LevelOf(nodeId) : 0;
        int? tier = ask.Op == SkillTreeOp.Advance ? _rules.Class(ask.Target)?.Tier : null;
        var record = new SkillOpRecord(ask.OpKey, ask.Op, nodeId, fromNode, fromLevel, level, pointsBefore, pointsAfter, classFrom,
            c.State.ClassId, tier);
        c.Pending.Add(new PendingOp(record, Answer(ask, SkillTreeReason.None, level), connection));
        c.Waiting.Add(ask.OpKey);
        c.Dirty = true;
        MarkClass2?.Invoke(characterId);
        RequestSave?.Invoke(characterId, SaveReason.SkillTree);
        return null;
    }

    private SkillTreeResult HotbarChanged(long characterId, CharacterSkills c, in SkillTreeAsk ask)
    {
        CopyHotbar(c.State, c.Visible);
        c.Dirty = true;
        MarkClass2?.Invoke(characterId);
        VisibleChanged?.Invoke(characterId);
        var answer = Answer(ask, SkillTreeReason.None, 0);
        c.Seen[ask.OpKey] = answer;
        return answer;
    }

    private static SkillTreeResult Refuse(CharacterSkills c, in SkillTreeAsk ask, SkillTreeReason reason)
    {
        var answer = Answer(ask, reason, 0);
        c.Seen[ask.OpKey] = answer;
        return answer;
    }

    private static SkillTreeResult Answer(in SkillTreeAsk ask, SkillTreeReason reason, int level) => new()
    {
        RequestId = ask.RequestId,
        Op = ask.Op,
        Reason = reason,
        Target = ask.Target,
        Level = (byte)Math.Clamp(level, 0, byte.MaxValue),
    };

    /// <summary>The visible hotbar is the working one, minus the nodes not committed yet.</summary>
    private static void CopyHotbar(SkillState from, SkillState to)
    {
        for (int slot = 1; slot <= ActionBindings.MaxSkillSlots; slot++)
        {
            string node = from.NodeAt(slot);
            if (node.Length != 0 && to.IsLearned(node))
                to.Place(slot, node);
            else
                to.ClearSlot(slot);
        }
    }

    private sealed record PendingOp(SkillOpRecord Record, SkillTreeResult Answer, int Connection);

    private sealed record InFlightBatch(Guid SaveId, SkillBatch Batch, List<PendingOp> Ops, SkillState Snapshot);

    private sealed class CharacterSkills(SkillState state)
    {
        public SkillState State { get; } = state;
        public SkillState Visible { get; } = state.Clone();
        public bool Dirty { get; set; }
        public List<PendingOp> Pending { get; } = new();
        public HashSet<string> Waiting { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SkillTreeResult> Seen { get; } = new(StringComparer.Ordinal);
        public InFlightBatch? InFlight { get; set; }
    }
}
