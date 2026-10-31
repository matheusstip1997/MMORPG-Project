using System.Globalization;
using System.Text;
using System.Text.Json;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Jobs;

namespace GameServer.Jobs;

/// <summary>What one job pick asks for, as the world reads it in phase 1 (<see cref="InReach"/>: the job master gate passed).</summary>
public readonly record struct JobAsk(uint RequestId, byte JobNetId, JobSlot Slot, string OpKey, int Level, bool Blocked, bool InReach);

/// <summary>
/// What one job tree request asks for, as the world reads it in phase 1 (WP-M3-7): <see cref="Blocked"/> in combat or an
/// action, <see cref="InSession"/> during a harvest or forge session (<see cref="IJobActivityGate"/>); both refuse only a respec,
/// a session first (a harvest channel is an action too).
/// </summary>
public readonly record struct JobTreeAsk(uint RequestId, JobTreeOp Op, byte JobNetId, string NodeId, string OpKey, int Level, bool Blocked,
    bool InSession);

/// <summary>
/// An operation whose save committed: its audit row and the answer the requesting connection gets (a pick's
/// <see cref="JobResult"/>, or a job tree learn's or respec's <see cref="JobTreeResult"/>).
/// </summary>
public sealed record CommittedJobOp(JobOpRecord Record, JobResult? Answer, int Connection)
{
    public JobTreeResult? TreeAnswer { get; init; }
}

/// <summary>
/// One job a character holds, as the Jobs window shows it: <see cref="XpToNext"/> on the slot's curve, the points as the
/// committed tree leaves them and the committed learned nodes in node id order (WP-M3-7).
/// </summary>
public readonly record struct JobView(JobDef Def, JobSlot Slot, int Level, long Xp, int XpToNext, int Points, IReadOnlyList<JobNodeRow> Nodes);

/// <summary>
/// The only writer of job progress of every character held by this world (spec 5.1, R-5.1-01..04; WP-M3-1; architecture
/// rule 4): the job records (slot, level, XP, points, the diminishing-returns window), in memory on the world thread.
/// <para>
/// The pick (user decision 2026-10-30): the main job, then the secondary, each written once. It is checked by
/// <see cref="JobRules.CheckPick"/>, applied to the working records at once (so a second request in the same tick sees it),
/// grants the job's starter items through <see cref="InventoryService.TryAdd"/> (key <c>jobStart:{character}:{job}:{n}</c>)
/// and asks for a save at once. That save carries the record, the <c>job_ops</c> audit row and the items in one
/// transaction (rule 5); only once it committed does the job count (<see cref="IJobQuery"/>) and the player get its answer.
/// An op key seen before gets the first answer (double submit); a crash before the commit loses the pick in memory and in
/// the database alike.
/// </para>
/// <para>
/// Job XP (R-5.1-04) comes from the activity events (<see cref="IJobActivityEvent"/>, phase 11): the weights of the
/// activity, the tier factor and the diminishing return for repeats of the same node or recipe (<see cref="JobMath"/>).
/// Class 2 (spec 7.1): applied at once, saved with the next batch. Idempotent: a re-dispatched envelope (its watermark) and
/// a repeated operation key (<see cref="RecentOpKeys"/>) add nothing. The secondary job levels on its own, slower curve and
/// never above the main job, banking XP while capped (R-5.1-03 as changed on 2026-10-30, decision 7; <see cref="JobMath.AddSecondaryXp"/>).
/// </para>
/// <para>
/// The job skill trees (R-5.1-04, WP-M3-7; <c>docs/design/job-trees.md</c>): learn and respec are checked by
/// <see cref="JobTreeRules"/>, applied to the working record at once and saved at once with their <c>job_ops</c> row, like
/// the pick; only once the save committed are the nodes what <see cref="IJobBonusQuery"/> reads and the player answered. A
/// job starts with one level's points at its pick (user answer 2026-10-30).
/// </para>
/// </summary>
public sealed class JobService : IJobBatchSource, IJobQuery, IJobBonusQuery,
    IEventConsumer<Harvested>, IEventConsumer<Refined>, IEventConsumer<Crafted>, IEventConsumer<Converted>,
    IEventConsumer<Enchanted>, IEventConsumer<GemSocketed>, IEventConsumer<GemRemoved>, IEventConsumer<ItemUsed>
{
    private readonly JobRules _rules;
    private readonly InventoryService? _inventory;
    private readonly EventBus? _events;
    private readonly Dictionary<long, CharacterJobs> _characters = new();
    private readonly RecentOpKeys _recentOps;
    private readonly EventWatermark[] _watermarks = new EventWatermark[byte.MaxValue + 1];

    public JobService(JobRules rules, InventoryService? inventory, EventBus? events)
    {
        _rules = rules;
        _inventory = inventory;
        _events = events;
        _recentOps = new RecentOpKeys(rules.Settings.RecentOpKeys);
    }

    public JobRules Rules => _rules;

    /// <summary>Marks a character class 2 dirty with its save scheduler (set by the SessionManager).</summary>
    public Action<long>? MarkClass2 { get; set; }

    /// <summary>Asks for a save at the next tick (a pick, R-7.1-05; set by the SessionManager).</summary>
    public Action<long, SaveReason>? RequestSave { get; set; }

    /// <summary>A save committed a pick of this character (set by the world: the event and the answer).</summary>
    public Action<long, CommittedJobOp>? OpCommitted { get; set; }

    /// <summary>What the character sees of its jobs changed (set by the world: the client's state in phase 10).</summary>
    public Action<long, JobCause>? Changed { get; set; }

    /// <summary>The player entity of a character, 0 when it has none here (for the events; set by the world).</summary>
    public Func<long, uint>? EntityOf { get; set; }

    /// <summary>Activity events that paid no XP: the character is not here, or it does not hold the event's job.</summary>
    public long IgnoredEvents { get; private set; }

    public int Count => _characters.Count;

    public bool IsAttached(long characterId) => _characters.ContainsKey(characterId);

    public void Subscribe(EventBus events)
    {
        events.Subscribe<Harvested>(this);
        events.Subscribe<Refined>(this);
        events.Subscribe<Crafted>(this);
        events.Subscribe<Converted>(this);
        events.Subscribe<Enchanted>(this);
        events.Subscribe<GemSocketed>(this);
        events.Subscribe<GemRemoved>(this);
        events.Subscribe<ItemUsed>(this);
    }

    /// <summary>Starts holding a character's job records as loaded at login (they count as committed).</summary>
    public void Attach(long characterId, JobRecord record)
    {
        if (_characters.ContainsKey(characterId))
            throw new InvalidOperationException($"character {characterId} is already attached");
        var c = new CharacterJobs();
        foreach (var row in record.Jobs)
        {
            // A job the tables no longer have keeps its slot (picks are permanent) but shows nowhere and earns nothing.
            int level = Math.Clamp(row.Level, 1, _rules.MaxLevel);
            var job = new JobProgress(row.JobId, row.Slot, level, level >= _rules.MaxLevel ? 0 : Math.Max(0, row.Xp),
                Math.Max(0, row.Points), ReadWindow(row.RecentJson)) { Committed = true };
            foreach (var node in row.Nodes)
            {
                // A node the tree no longer has is dropped, as the class tree does. TODO(open): give its points back.
                if (_rules.Tree?.Node(node.NodeId) is { } def && def.JobId == row.JobId && node.Level > 0)
                    job.Nodes[node.NodeId] = Math.Min(node.Level, def.MaxLevel);
            }
            job.CopyNodesToCommitted();
            c.Jobs[row.JobId] = job;
        }
        _characters.Add(characterId, c);
    }

    public void Detach(long characterId)
    {
        _characters.Remove(characterId);
        _recentOps.Forget(characterId);
    }

    public bool HasJob(long characterId, string jobId) => Committed(characterId, jobId) != null;

    public int JobLevel(long characterId, string jobId) => Committed(characterId, jobId)?.Level ?? 0;

    public int Repeats(long characterId, string jobId, JobActivity activity, int subjectNetId, int lastN) =>
        Committed(characterId, jobId)?.Window.Repeats(activity, subjectNetId, lastN) ?? 0;

    public string? JobIn(long characterId, JobSlot slot)
    {
        if (!_characters.TryGetValue(characterId, out var c))
            return null;
        foreach (var job in c.Jobs.Values)
        {
            if (job.Committed && job.Slot == slot)
                return job.JobId;
        }
        return null;
    }

    /// <summary>The committed jobs the tables know, main first; null when the character is not held.</summary>
    public IReadOnlyList<JobView>? View(long characterId)
    {
        if (!_characters.TryGetValue(characterId, out var c))
            return null;
        var list = new List<JobView>(c.Jobs.Count);
        foreach (var job in c.Jobs.Values.OrderBy(j => j.Slot))
        {
            if (job.Committed && _rules.Find(job.JobId) is { } def)
                list.Add(new JobView(def, job.Slot, job.Level, job.Xp, _rules.XpToNext(job.Level, job.Slot),
                    Math.Max(0, job.Points + job.UncommittedSpent), NodeRows(job.CommittedNodes)));
        }
        return list;
    }

    public int Get(long characterId, string jobId, JobBonusKind kind, JobActivity activity) =>
        _rules.Tree is { } tree && Committed(characterId, jobId) is { } job ? tree.Bonus(jobId, job.CommittedNodes, kind, activity) : 0;

    public bool MoldTierUnlocked(long characterId, string jobId, int tier) =>
        _rules.Tree is { } tree && Committed(characterId, jobId) is { } job && JobTreeMath.HasTier(tree.MoldTiers(jobId, job.CommittedNodes), tier);

    /// <summary>The learned nodes of a working record (tests and checks); null when absent.</summary>
    public IReadOnlyDictionary<string, int>? WorkingNodes(long characterId, string jobId) =>
        _characters.TryGetValue(characterId, out var c) && c.Jobs.TryGetValue(jobId, out var job) ? job.Nodes : null;

    /// <summary>
    /// One job tree learn or respec (phase 1; WP-M3-7). Returns the answer to send now (a refusal, or a repeat of an answered
    /// key), or null when the operation was applied and waits for its save (answered through <see cref="OpCommitted"/>) or
    /// repeats such a waiting key. Learning works any time (decision 10); a respec is refused in combat or an action and
    /// during a harvest or forge session (decision 6).
    /// </summary>
    public JobTreeResult? Handle(long characterId, in JobTreeAsk ask, int connection)
    {
        if (!_characters.TryGetValue(characterId, out var c) || _rules.Tree is not { } tree)
            return TreeAnswer(ask, JobReason.JobsUnavailable, 0, 0);
        if (c.SeenTree.TryGetValue(ask.OpKey, out var seen))
            return seen;
        if (c.Waiting.Contains(ask.OpKey))
            return null;
        if (_rules.FindByNetId(ask.JobNetId) is not { } def)
            return RefuseTree(c, ask, JobReason.UnknownJob);
        c.Jobs.TryGetValue(def.Id, out var job);
        bool held = job != null && job.Committed;
        IReadOnlyDictionary<string, int> learned = job != null ? job.Nodes : EmptyNodes;
        int pointsBefore = job?.Points ?? 0;
        JobOpRecord record;
        byte level = 0;
        switch (ask.Op)
        {
            case JobTreeOp.Learn:
            {
                var check = tree.CheckLearn(def.Id, held, job?.Level ?? 0, learned, ask.NodeId, pointsBefore, out int cost);
                if (check != JobTreeCheck.Ok)
                    return RefuseTree(c, ask, JobTreeRules.ReasonOf(check));
                int from = job!.Nodes.TryGetValue(ask.NodeId, out int have) ? have : 0;
                job.Nodes[ask.NodeId] = from + 1;
                job.Points -= cost;
                job.UncommittedSpent += cost;
                level = (byte)(from + 1);
                record = new JobOpRecord(ask.OpKey, JobOp.Learn, def.Id, job.Slot, ask.Level, Array.Empty<string>())
                {
                    NodeId = ask.NodeId, FromLevel = from, ToLevel = from + 1, PointsBefore = pointsBefore, PointsAfter = job.Points,
                };
                break;
            }
            case JobTreeOp.Respec:
            {
                var check = tree.CheckRespec(def.Id, held, learned, out int refund);
                var reason = JobTreeRules.ReasonOf(check);
                // Decision 6, R-5.1-04: a harvest or forge session names itself before combat or an action, since a harvest
                // channel is an action too (ActionKind.Harvest) and the player is mining, not fighting.
                if (reason == JobReason.None && ask.InSession)
                    reason = JobReason.JobActivityRunning;
                if (reason == JobReason.None && ask.Blocked)
                    reason = JobReason.InCombat;
                if (reason != JobReason.None)
                    return RefuseTree(c, ask, reason);
                int cleared = job!.Nodes.Values.Sum();
                job.Nodes.Clear();
                job.Points += refund;
                job.UncommittedSpent -= refund;
                record = new JobOpRecord(ask.OpKey, JobOp.Respec, def.Id, job.Slot, ask.Level, Array.Empty<string>())
                {
                    FromLevel = cleared, PointsBefore = pointsBefore, PointsAfter = job.Points,
                };
                break;
            }
            default:
                return RefuseTree(c, ask, JobReason.UnknownJobNode);
        }
        c.Pending.Add(new PendingOp(record, null, TreeAnswer(ask, JobReason.None, level, job.Points), connection));
        c.Waiting.Add(ask.OpKey);
        c.Dirty = true;
        MarkClass2?.Invoke(characterId);
        RequestSave?.Invoke(characterId, SaveReason.JobTree);
        return null;
    }

    /// <summary>XP, level and points of a working record (tests and checks); null when absent.</summary>
    public (int Level, long Xp, int Points, bool Committed)? Working(long characterId, string jobId) =>
        _characters.TryGetValue(characterId, out var c) && c.Jobs.TryGetValue(jobId, out var job)
            ? (job.Level, job.Xp, job.Points, job.Committed)
            : null;

    /// <summary>Picks applied but not yet in a committed save.</summary>
    public int WaitingCount(long characterId) => _characters.TryGetValue(characterId, out var c) ? c.Waiting.Count : 0;

    /// <summary>
    /// One pick (phase 1). Returns the answer to send now (a refusal, or a repeat of an answered key), or null when the
    /// pick was applied and waits for its save (answered through <see cref="OpCommitted"/>) or repeats such a waiting key.
    /// </summary>
    public JobResult? Handle(long characterId, in JobAsk ask, int connection)
    {
        if (!_characters.TryGetValue(characterId, out var c))
            return Answer(ask, JobReason.JobsUnavailable);
        if (c.Seen.TryGetValue(ask.OpKey, out var seen))
            return seen;
        if (c.Waiting.Contains(ask.OpKey))
            return null;
        if (!ask.InReach)
            return Refuse(c, ask, JobReason.JobMasterOutOfReach);
        if (_rules.FindByNetId(ask.JobNetId) is not { } job || ask.Slot == JobSlot.None || ask.Slot > ReliableMessages.LastJobSlot)
            return Refuse(c, ask, JobReason.UnknownJob);

        string? main = null, secondary = null;
        foreach (var held in c.Jobs.Values) // the working records: a pick waiting for its save fills its slot already
        {
            if (held.Slot == JobSlot.Main)
                main = held.JobId;
            else if (held.Slot == JobSlot.Secondary)
                secondary = held.JobId;
        }
        var reason = ReasonOf(JobRules.CheckPick(job, ask.Slot, ask.Level, main, secondary));
        if (reason == JobReason.None && ask.Blocked)
            reason = JobReason.InCombat;
        if (reason != JobReason.None)
            return Refuse(c, ask, reason);
        if (!TryResolveStarterItems(characterId, job, out var starters, out reason))
            return Refuse(c, ask, reason);

        // A job starts with one level's job points (user answer 2026-10-30, WP-M3-7 plan): row 0 of its tree opens at once.
        c.Jobs[job.Id] = new JobProgress(job.Id, ask.Slot, 1, 0, _rules.Settings.PointsPerLevel, new JobRecentWindow(_rules.WindowCapacity));
        for (int i = 0; i < starters.Count; i++)
        {
            var added = _inventory!.TryAdd(characterId, starters[i], 0, 1, StarterOpKey(characterId, job.Id, i), InventoryReason.JobStart,
                $"job:{job.Id}");
            if (added.Added != 1 && !added.Repeated)
                throw new InvalidOperationException($"starter item {starters[i].Id} of job {job.Id} did not fit after the room check");
        }
        var record = new JobOpRecord(ask.OpKey, JobOp.Pick, job.Id, ask.Slot, ask.Level, job.StarterItemIds);
        c.Pending.Add(new PendingOp(record, Answer(ask, JobReason.None), null, connection));
        c.Waiting.Add(ask.OpKey);
        c.Dirty = true;
        MarkClass2?.Invoke(characterId);
        RequestSave?.Invoke(characterId, SaveReason.JobPick);
        return null;
    }

    /// <summary>The inventory key of a pick's <paramref name="index"/>-th starter item (spec 5.1: <c>jobStart:{character}:{job}:{n}</c>).</summary>
    public static string StarterOpKey(long characterId, string jobId, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"jobStart:{characterId}:{jobId}:{index}");

    public JobBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var c) || (!c.Dirty && c.Pending.Count == 0))
            return null;
        if (c.InFlight != null)
            throw new InvalidOperationException($"character {characterId} already has a job batch in flight");
        var rows = new List<JobProgressRow>(c.Jobs.Count);
        var snapshot = new Dictionary<string, JobNodeRow[]>(c.Jobs.Count, StringComparer.Ordinal);
        foreach (var job in c.Jobs.Values.OrderBy(j => j.Slot)) // a fixed order (rule 6)
        {
            var nodes = NodeRows(job.Nodes);
            snapshot[job.JobId] = nodes;
            rows.Add(new JobProgressRow(job.JobId, job.Slot, job.Level, job.Xp, job.Points, WindowJson(job.Window)) { Nodes = nodes });
        }
        var batch = new JobBatch(rows, c.Pending.Select(p => p.Record).ToList());
        c.InFlight = new InFlightBatch(saveId, c.Pending.ToList(), snapshot);
        c.Pending.Clear();
        c.Dirty = false;
        return batch;
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var c) || c.InFlight is not { } inFlight || inFlight.SaveId != saveId)
            return; // a save without a batch, or a stale completion: idempotent
        c.InFlight = null;
        // The learned nodes in this save are now what the bonus query reads (rule 5).
        foreach (var pair in inFlight.Nodes)
        {
            if (c.Jobs.TryGetValue(pair.Key, out var job))
                job.SetCommittedNodes(pair.Value);
        }
        bool picked = false, tree = false;
        foreach (var op in inFlight.Ops)
        {
            if (c.Jobs.TryGetValue(op.Record.JobId, out var job))
            {
                job.Committed = true;
                job.UncommittedSpent -= op.Record.PointsBefore - op.Record.PointsAfter;
            }
            c.Waiting.Remove(op.Record.OpKey);
            if (op.TreeAnswer != null)
            {
                c.SeenTree[op.Record.OpKey] = op.TreeAnswer;
                tree = true;
            }
            else if (op.Answer != null)
            {
                c.Seen[op.Record.OpKey] = op.Answer;
                picked = true;
            }
            OpCommitted?.Invoke(characterId, new CommittedJobOp(op.Record, op.Answer, op.Connection) { TreeAnswer = op.TreeAnswer });
        }
        if (picked)
            Changed?.Invoke(characterId, JobCause.Picked);
        if (tree)
            Changed?.Invoke(characterId, JobCause.TreeChanged);
    }

    public void Handle(in EventEnvelope envelope, in Harvested payload) => OnActivity(envelope, payload);

    public void Handle(in EventEnvelope envelope, in Refined payload) => OnActivity(envelope, payload);

    public void Handle(in EventEnvelope envelope, in Crafted payload) => OnActivity(envelope, payload);

    public void Handle(in EventEnvelope envelope, in Converted payload) => OnActivity(envelope, payload);

    public void Handle(in EventEnvelope envelope, in Enchanted payload) => OnActivity(envelope, payload);

    public void Handle(in EventEnvelope envelope, in GemSocketed payload) => OnActivity(envelope, payload);

    public void Handle(in EventEnvelope envelope, in GemRemoved payload) => OnActivity(envelope, payload);

    public void Handle(in EventEnvelope envelope, in ItemUsed payload) => OnActivity(envelope, payload);

    /// <summary>One activity event: its job XP, with diminishing returns, and the level-ups it pays for (R-5.1-04).</summary>
    private void OnActivity<T>(in EventEnvelope envelope, in T payload) where T : struct, IJobActivityEvent
    {
        var activity = payload.Activity;
        if (!_watermarks[(byte)activity].TryAccept(envelope))
            return; // a re-dispatched event counts once
        long characterId = payload.CharacterId;
        if (_rules.WeightsOf(activity) is not { } weights || payload.JobId.Length == 0)
            return; // no job XP for this activity (consumable use in M3)
        if (!_characters.TryGetValue(characterId, out var c) || Committed(c, payload.JobId) is not { } job)
        {
            IgnoredEvents++;
            return;
        }
        if (!_recentOps.TryAccept(characterId, payload.OpKey))
            return; // the same operation again: nothing more (architecture rule 3)

        int subject = payload.SubjectNetId;
        int repeats = job.Window.Repeats(activity, subject, weights.RepeatWindow);
        int amount = JobMath.XpFor(weights.BaseXp, JobMath.TierPercent(weights.TierPercent, payload.Tier),
            JobMath.RepeatPercent(repeats, weights.RepeatStepPercent, weights.RepeatFloorPercent));
        // The job tree's jobXpPercent, read when the XP is applied (user answer 2026-10-30) from the committed nodes.
        int xpBonus = Get(characterId, job.JobId, JobBonusKind.JobXpPercent, activity);
        if (xpBonus > 0)
            amount = (int)Math.Min(int.MaxValue, (long)amount * (100 + xpBonus) / 100);
        job.Window.Add(activity, subject);
        uint entityId = EntityOf?.Invoke(characterId) ?? 0;
        int fromLevel = job.Level;
        int gained = job.Slot == JobSlot.Secondary ? AddSecondaryXp(c, job, amount) : AddMainXp(job, amount);
        c.Dirty = true;
        MarkClass2?.Invoke(characterId);

        _events?.Publish(new JobXpGained(characterId, entityId, job.JobId, activity, amount, repeats, payload.OpKey), characterId,
            envelope.RegionId);
        if (gained > 0)
            _events?.Publish(new JobLevelUp(characterId, entityId, job.JobId, fromLevel, job.Level, gained * _rules.Settings.PointsPerLevel),
                characterId, envelope.RegionId);
        // Decision 7c: a main level-up applies the secondary job's banked XP at once.
        if (gained > 0 && job.Slot == JobSlot.Main && InSlot(c, JobSlot.Secondary) is { } secondary)
        {
            int secondaryFrom = secondary.Level;
            int caughtUp = AddSecondaryXp(c, secondary, 0);
            if (caughtUp > 0)
                _events?.Publish(new JobLevelUp(characterId, entityId, secondary.JobId, secondaryFrom, secondary.Level,
                    caughtUp * _rules.Settings.PointsPerLevel), characterId, envelope.RegionId);
        }
        Changed?.Invoke(characterId, JobCause.Progress);
    }

    /// <summary>Main job XP on the job level curve; returns the levels gained and pays their points.</summary>
    private int AddMainXp(JobProgress job, long amount)
    {
        int level = job.Level;
        long xp = job.Xp;
        int gained = JobMath.AddXp(ref level, ref xp, amount, _rules.XpToNextByLevel, _rules.MaxLevel);
        job.Level = level;
        job.Xp = xp;
        job.Points += gained * _rules.Settings.PointsPerLevel;
        return gained;
    }

    /// <summary>
    /// Secondary job XP (R-5.1-03 as changed on 2026-10-30, decision 7): its slower curve, never above the main's level,
    /// banked while capped. Returns the levels gained and pays their points.
    /// </summary>
    private int AddSecondaryXp(CharacterJobs c, JobProgress job, long amount)
    {
        int mainLevel = InSlot(c, JobSlot.Main)?.Level ?? 1;
        int level = job.Level;
        long xp = job.Xp;
        int gained = JobMath.AddSecondaryXp(ref level, ref xp, amount, _rules.SecondaryXpToNextByLevel, _rules.MaxLevel, mainLevel,
            _rules.Settings.SecondaryBankLevels);
        job.Level = level;
        job.Xp = xp;
        job.Points += gained * _rules.Settings.PointsPerLevel;
        return gained;
    }

    private static JobProgress? InSlot(CharacterJobs c, JobSlot slot)
    {
        foreach (var job in c.Jobs.Values)
        {
            if (job.Slot == slot)
                return job;
        }
        return null;
    }

    private JobProgress? Committed(long characterId, string jobId) =>
        _characters.TryGetValue(characterId, out var c) ? Committed(c, jobId) : null;

    private static JobProgress? Committed(CharacterJobs c, string jobId) =>
        c.Jobs.TryGetValue(jobId, out var job) && job.Committed ? job : null;

    /// <summary>
    /// The starter items of <paramref name="job"/>, and whether they all fit now (user decision 2026-10-30: no room refuses
    /// the pick). Instance items need an empty slot each in the container that receives their category.
    /// </summary>
    private bool TryResolveStarterItems(long characterId, JobDef job, out List<ItemDef> items, out JobReason reason)
    {
        items = new List<ItemDef>(job.StarterItemIds.Length);
        reason = JobReason.None;
        if (job.StarterItemIds.Length == 0)
            return true;
        if (_inventory == null)
        {
            reason = JobReason.JobsUnavailable;
            return false;
        }
        foreach (var id in job.StarterItemIds)
        {
            if (!_inventory.Rules.TryGetItem(id, out var item) || !_inventory.Accepts(item))
            {
                reason = JobReason.JobsUnavailable;
                return false;
            }
            items.Add(item);
        }
        // Items of one category go to one container: each needs its own slot there (they are instances, JobDef validator).
        foreach (var group in items.GroupBy(i => i.Category))
        {
            if (_inventory.SpaceFor(characterId, group.First(), 0) < group.Count())
            {
                reason = JobReason.BagFull;
                return false;
            }
        }
        return true;
    }

    private static JobReason ReasonOf(JobPickCheck check) => check switch
    {
        JobPickCheck.Ok => JobReason.None,
        JobPickCheck.AlreadyChosen => JobReason.JobAlreadyChosen,
        JobPickCheck.MainFirst => JobReason.JobMainFirst,
        JobPickCheck.SameAsMain => JobReason.JobSameAsMain,
        JobPickCheck.NotSecondary => JobReason.JobNotSecondary,
        JobPickCheck.LevelTooLow => JobReason.JobLevelTooLow,
        _ => JobReason.UnknownJob,
    };

    private static JobResult Refuse(CharacterJobs c, in JobAsk ask, JobReason reason)
    {
        var answer = Answer(ask, reason);
        c.Seen[ask.OpKey] = answer;
        return answer;
    }

    private static JobResult Answer(in JobAsk ask, JobReason reason) => new()
    {
        RequestId = ask.RequestId,
        Slot = ask.Slot,
        JobNetId = ask.JobNetId,
        Reason = reason,
    };

    private static JobTreeResult RefuseTree(CharacterJobs c, in JobTreeAsk ask, JobReason reason)
    {
        var answer = TreeAnswer(ask, reason, 0, 0);
        c.SeenTree[ask.OpKey] = answer;
        return answer;
    }

    private static JobTreeResult TreeAnswer(in JobTreeAsk ask, JobReason reason, byte level, int points) => new()
    {
        RequestId = ask.RequestId,
        Op = ask.Op,
        JobNetId = ask.JobNetId,
        NodeId = ask.NodeId,
        Reason = reason,
        Level = level,
        Points = (ushort)Math.Clamp(points, 0, ushort.MaxValue),
    };

    private static readonly IReadOnlyDictionary<string, int> EmptyNodes = new Dictionary<string, int>();

    /// <summary>Learned nodes as rows in node id order (rule 6: a fixed order).</summary>
    private static JobNodeRow[] NodeRows(Dictionary<string, int> nodes)
    {
        if (nodes.Count == 0)
            return Array.Empty<JobNodeRow>();
        var rows = new JobNodeRow[nodes.Count];
        int i = 0;
        foreach (var pair in nodes)
            rows[i++] = new JobNodeRow(pair.Key, pair.Value);
        Array.Sort(rows, (a, b) => string.CompareOrdinal(a.NodeId, b.NodeId));
        return rows;
    }

    /// <summary>The window as stored in <c>job_progress.recent</c>: <c>{"v":1,"recent":[[1,12],[1,12]]}</c>, oldest first.</summary>
    public static string WindowJson(JobRecentWindow window)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteStartArray("recent");
            for (int i = 0; i < window.Count; i++)
            {
                var (activity, subject) = window.At(i);
                writer.WriteStartArray();
                writer.WriteNumberValue((int)activity);
                writer.WriteNumberValue(subject);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>A saved window; its newest entries when the capacity shrank, an empty one when it cannot be read.</summary>
    private JobRecentWindow ReadWindow(string json)
    {
        var window = new JobRecentWindow(_rules.WindowCapacity);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("recent", out var recent) || recent.ValueKind != JsonValueKind.Array)
                return window;
            foreach (var entry in recent.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() == 2 && entry[0].TryGetInt32(out int activity)
                    && entry[1].TryGetInt32(out int subject) && activity > 0 && activity <= byte.MaxValue)
                    window.Add((JobActivity)activity, subject);
            }
        }
        catch (JsonException)
        {
            // A window that cannot be read is an empty one: the diminishing returns start again.
        }
        return window;
    }

    private sealed class JobProgress(string jobId, JobSlot slot, int level, long xp, int points, JobRecentWindow window)
    {
        public string JobId { get; } = jobId;
        public JobSlot Slot { get; } = slot;
        public int Level { get; set; } = level;
        public long Xp { get; set; } = xp;
        public int Points { get; set; } = points;
        public JobRecentWindow Window { get; } = window;

        /// <summary>The pick is in a committed save (records loaded at login are).</summary>
        public bool Committed { get; set; }

        /// <summary>The job tree's learned levels by node id, with the operations not yet committed (WP-M3-7).</summary>
        public Dictionary<string, int> Nodes { get; } = new(StringComparer.Ordinal);

        /// <summary>The learned levels of the last committed save: what <see cref="IJobBonusQuery"/> reads and the window shows.</summary>
        public Dictionary<string, int> CommittedNodes { get; } = new(StringComparer.Ordinal);

        /// <summary>Points spent minus refunded by tree operations not yet committed (the window shows the committed points).</summary>
        public int UncommittedSpent { get; set; }

        public void CopyNodesToCommitted()
        {
            CommittedNodes.Clear();
            foreach (var pair in Nodes)
                CommittedNodes[pair.Key] = pair.Value;
        }

        public void SetCommittedNodes(JobNodeRow[] rows)
        {
            CommittedNodes.Clear();
            foreach (var row in rows)
                CommittedNodes[row.NodeId] = row.Level;
        }
    }

    private sealed record PendingOp(JobOpRecord Record, JobResult? Answer, JobTreeResult? TreeAnswer, int Connection);

    private sealed record InFlightBatch(Guid SaveId, List<PendingOp> Ops, Dictionary<string, JobNodeRow[]> Nodes);

    private sealed class CharacterJobs
    {
        public Dictionary<string, JobProgress> Jobs { get; } = new(StringComparer.Ordinal);
        public bool Dirty { get; set; }
        public List<PendingOp> Pending { get; } = new();
        public HashSet<string> Waiting { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JobResult> Seen { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JobTreeResult> SeenTree { get; } = new(StringComparer.Ordinal);
        public InFlightBatch? InFlight { get; set; }
    }
}
