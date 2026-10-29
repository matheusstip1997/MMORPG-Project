using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Quests;

namespace GameServer.Quests;

/// <summary>One step of a run as the world holds it (spec 5.5 <c>RunStep</c>). Read-only outside <see cref="RunStore"/>.</summary>
public sealed class RunStep
{
    internal RunStep(RunStepRecord record, int monsterNetId, int itemNetId)
    {
        Index = record.Index;
        Kind = record.Kind;
        QuestDefId = record.QuestDefId;
        MonsterDefId = record.MonsterDefId;
        MonsterNetId = monsterNetId;
        ItemDefId = record.ItemDefId;
        ItemNetId = itemNetId;
        Required = record.Required;
        Progress = record.Progress;
        RewardExp = record.RewardExp;
        RewardPercent = record.RewardPercent;
        State = record.State;
    }

    public int Index { get; }
    public QuestKind Kind { get; }
    public string QuestDefId { get; }
    public string MonsterDefId { get; }
    /// <summary>The monster's net id (what <c>Killed</c> carries); 0 when the tables no longer have it.</summary>
    public int MonsterNetId { get; }
    public string? ItemDefId { get; }
    /// <summary>The item to deliver or return; 0 for kill steps or an item the tables no longer have.</summary>
    public int ItemNetId { get; }
    public int Required { get; }
    public int Progress { get; internal set; }
    /// <summary>Locked at acceptance (R-5.5-02).</summary>
    public long RewardExp { get; }
    public int RewardPercent { get; }
    public RunStepState State { get; internal set; }

    public int Missing => Required - Progress;

    internal RunStepRecord ToRecord() => new(Index, Kind, QuestDefId, MonsterDefId, ItemDefId, Required, Progress, RewardExp,
        RewardPercent, State);
}

/// <summary>A run as the world holds it (spec 5.5 <c>Run</c>). Read-only outside <see cref="RunStore"/>.</summary>
public sealed class Run
{
    internal Run(Guid runId, long ownerCharacterId, string outpostMarkerId, RunState state, long startedAtUtcMs, long boardEpoch,
        List<RunStep> steps)
    {
        RunId = runId;
        OwnerCharacterId = ownerCharacterId;
        OutpostMarkerId = outpostMarkerId;
        State = state;
        StartedAtUtcMs = startedAtUtcMs;
        BoardEpoch = boardEpoch;
        Steps = steps;
    }

    public Guid RunId { get; }
    /// <summary>The owner: a character in M2. TODO(M4): party runs (R-5.5-03) belong to the party (<c>owner_party_id</c>).</summary>
    public long OwnerCharacterId { get; }
    public string OutpostMarkerId { get; }
    public RunState State { get; internal set; }
    public long StartedAtUtcMs { get; }
    public long BoardEpoch { get; }
    public long ClaimedExp { get; internal set; }
    public IReadOnlyList<RunStep> Steps { get; }

    public int CompletedSteps => Steps.Count(s => s.State == RunStepState.Done);
    public int ForfeitedSteps => Steps.Count(s => s.State == RunStepState.Forfeited);
    public bool HasActiveStep => Steps.Any(s => s.State == RunStepState.Active);
    public long CompletedRewards => Steps.Where(s => s.State == RunStepState.Done).Sum(s => s.RewardExp);
}

/// <summary>
/// The only writer of run state (spec 5.5; WP-M2-6; architecture rule 4 in spirit): every character's open run held by this world,
/// in memory on the world thread. <see cref="QuestService"/> changes it through here.
/// <para>
/// Saving (R-7.1-01 class 2): a change marks the character class 2 dirty; the next character save takes this store's batch
/// (<see cref="IQuestBatchSource"/>): every run that changed, whole, and the audit rows of accepts, deliveries, forfeits and claims,
/// in the transaction that also writes the exp and the inventory batch holding a delivery's removed items. A crash loses at most the
/// last batch, never duplicates (R-7.1-02). A claim asks for that save at once and is confirmed when it committed (rule 5).
/// </para>
/// </summary>
public sealed class RunStore : IQuestBatchSource
{
    private readonly QuestRules _rules;
    private readonly Func<string, int> _monsterNetId;
    private readonly Func<string, int> _itemNetId;
    private readonly Dictionary<long, CharacterRuns> _characters = new();

    /// <param name="monsterNetId">A monster's net id by its table id; 0 for one the tables no longer have.</param>
    /// <param name="itemNetId">An item's net id by its table id; 0 for one the tables no longer have.</param>
    public RunStore(QuestRules rules, Func<string, int> monsterNetId, Func<string, int> itemNetId)
    {
        _rules = rules;
        _monsterNetId = monsterNetId;
        _itemNetId = itemNetId;
    }

    public QuestRules Rules => _rules;

    /// <summary>Marks a character class 2 dirty with its save scheduler (set by the SessionManager).</summary>
    public Action<long>? MarkClass2 { get; set; }

    /// <summary>Asks for a save at the next tick (a claim, R-7.1-05 style; set by the SessionManager).</summary>
    public Action<long, SaveReason>? RequestSave { get; set; }

    /// <summary>The character's run changed: its client should get a new <see cref="RunStatus"/> (set by the world).</summary>
    public Action<long>? Changed { get; set; }

    /// <summary>A save committed the claim <c>opKey</c> of this character (rule 5: only now is it answered; set by the world).</summary>
    public Action<long, string>? ClaimCommitted { get; set; }

    public int Count => _characters.Count;

    public bool IsAttached(long characterId) => _characters.ContainsKey(characterId);

    /// <summary>Starts holding a character's open runs as loaded at login (at most <c>maxActiveRuns</c>; the newest wins).</summary>
    public void Attach(long characterId, IReadOnlyList<RunRecord> open)
    {
        if (_characters.ContainsKey(characterId))
            throw new InvalidOperationException($"character {characterId} is already attached");
        var runs = new CharacterRuns();
        foreach (var record in open)
        {
            if (record.OwnerCharacterId == characterId && record.State is RunState.Active or RunState.Completed)
                runs.Active = FromRecord(record);
        }
        _characters.Add(characterId, runs);
    }

    /// <summary>Drops a character (it left the world). Unsaved changes of a faulted character are lost with it.</summary>
    public void Detach(long characterId) => _characters.Remove(characterId);

    /// <summary>The character's run that is not claimed or abandoned; null when it has none.</summary>
    public Run? Active(long characterId) => _characters.TryGetValue(characterId, out var runs) ? runs.Active : null;

    /// <summary>What a claim of <paramref name="run"/> would pay now (R-5.5-03).</summary>
    public long ClaimExpOf(Run run) =>
        RunMath.ClaimExp(run.CompletedRewards, _rules.BonusPercent(run.CompletedSteps), _rules.PenaltyPercent(run.ForfeitedSteps));

    /// <summary>The first answer of a run operation already applied since the character was attached (double submit).</summary>
    public bool TryGetSeen(long characterId, string opKey, out QuestRunResult result)
    {
        result = null!;
        return _characters.TryGetValue(characterId, out var runs) && runs.SeenOps.TryGetValue(opKey, out result!);
    }

    /// <summary>True once the operation <paramref name="opKey"/> is in a committed save (rule 5: then it may be confirmed).</summary>
    public bool IsCommitted(long characterId, string opKey)
    {
        if (!_characters.TryGetValue(characterId, out var runs) || !runs.SeenOps.ContainsKey(opKey))
            return false;
        if (runs.PendingAudit.Any(a => a.OpKey == opKey))
            return false;
        return runs.InFlight is not { } inFlight || inFlight.Batch.Audit.All(a => a.OpKey != opKey);
    }

    /// <summary>
    /// Accepts a run built from <paramref name="offers"/> in run order (R-5.5-02: their rewards are locked into the steps). The
    /// caller checked the caps, the board epoch and the slots. Returns the new run.
    /// </summary>
    internal Run Accept(long characterId, Guid runId, string outpostMarkerId, long boardEpoch, IReadOnlyList<BoardOffer> offers,
        long nowUtcMs, string opKey, QuestRunResult answer)
    {
        var runs = Get(characterId);
        if (runs.Active != null)
            throw new InvalidOperationException($"character {characterId} already has run {runs.Active.RunId}");
        var steps = new List<RunStep>(offers.Count);
        for (int i = 0; i < offers.Count; i++)
        {
            var offer = offers[i];
            var record = new RunStepRecord(i, offer.Kind, offer.Def.Id, offer.Target.Monster.Id, offer.Item?.Id, offer.Count,
                0, offer.RewardExp, offer.RewardPercent, RunStepState.Active);
            steps.Add(new RunStep(record, offer.Target.Monster.NetId, offer.Item?.NetId ?? 0));
        }
        var run = new Run(runId, characterId, outpostMarkerId, RunState.Active, nowUtcMs, boardEpoch, steps);
        runs.Active = run;
        Record(characterId, runs, run, new RunAuditRecord(opKey, runId, QuestRunAction.Accept, -1, 0), opKey, answer);
        return run;
    }

    /// <summary>
    /// Adds <paramref name="units"/> to an active step (kills, or units delivered under <paramref name="opKey"/>), at most what it
    /// misses; the step is done at its count, and the run completed when no step is active. Returns whether the step became done.
    /// </summary>
    internal bool Advance(long characterId, int stepIndex, int units, string? opKey = null, QuestRunResult? answer = null)
    {
        var runs = Get(characterId);
        var run = runs.Active ?? throw new InvalidOperationException($"character {characterId} has no run");
        var step = (RunStep)run.Steps[stepIndex];
        if (step.State != RunStepState.Active || units <= 0)
            throw new InvalidOperationException($"step {stepIndex} of run {run.RunId} cannot advance");
        int taken = Math.Min(units, step.Missing);
        step.Progress += taken;
        bool done = step.Progress == step.Required;
        if (done)
            step.State = RunStepState.Done;
        UpdateState(run);
        var audit = opKey == null ? null : new RunAuditRecord(opKey, run.RunId, QuestRunAction.Deliver, stepIndex, taken);
        Record(characterId, runs, run, audit, opKey, answer);
        return done;
    }

    /// <summary>
    /// Forfeits an active step (R-5.5-03): it pays nothing and adds the forfeit penalty. A run with no active step left is
    /// completed, or abandoned (and closed) when no step is done.
    /// </summary>
    internal void Forfeit(long characterId, int stepIndex, string opKey, QuestRunResult answer)
    {
        var runs = Get(characterId);
        var run = runs.Active ?? throw new InvalidOperationException($"character {characterId} has no run");
        var step = (RunStep)run.Steps[stepIndex];
        if (step.State != RunStepState.Active)
            throw new InvalidOperationException($"step {stepIndex} of run {run.RunId} is not active");
        step.State = RunStepState.Forfeited;
        UpdateState(run);
        Record(characterId, runs, run, new RunAuditRecord(opKey, run.RunId, QuestRunAction.Forfeit, stepIndex, 0), opKey, answer);
        if (run.State == RunState.Abandoned)
            runs.Active = null;
    }

    /// <summary>
    /// Claims a completed run (R-5.5-03, one claim): its exp (completed steps x the bonus x the penalty) is recorded on the run
    /// and its audit row, the run is closed, and a save is asked for at once. Returns the exp; the caller pays it through
    /// <c>ExpService</c> in the same call, so both ride that save.
    /// </summary>
    internal long Claim(long characterId, string opKey, QuestRunResult answer)
    {
        var runs = Get(characterId);
        var run = runs.Active ?? throw new InvalidOperationException($"character {characterId} has no run");
        if (run.State != RunState.Completed)
            throw new InvalidOperationException($"run {run.RunId} is {run.State}, not completed");
        long exp = ClaimExpOf(run);
        run.ClaimedExp = exp;
        run.State = RunState.Claimed;
        answer.Exp = exp;
        Record(characterId, runs, run, new RunAuditRecord(opKey, run.RunId, QuestRunAction.Claim, -1, exp), opKey, answer);
        runs.Active = null;
        RequestSave?.Invoke(characterId, SaveReason.RunClaim);
        return exp;
    }

    public QuestBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var runs) || (runs.PendingRuns.Count == 0 && runs.PendingAudit.Count == 0))
            return null;
        if (runs.InFlight != null)
            throw new InvalidOperationException($"character {characterId} already has a run batch in flight");
        var batch = new QuestBatch(runs.PendingRuns.Values.Select(ToRecord).ToList(), runs.PendingAudit.ToList());
        runs.InFlight = new InFlightBatch(saveId, batch);
        runs.PendingRuns.Clear();
        runs.PendingAudit.Clear();
        return batch;
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var runs) || runs.InFlight is not { } inFlight || inFlight.SaveId != saveId)
            return; // a save without a batch, or a stale completion: idempotent
        runs.InFlight = null;
        foreach (var audit in inFlight.Batch.Audit)
        {
            if (audit.Action == QuestRunAction.Claim)
                ClaimCommitted?.Invoke(characterId, audit.OpKey);
        }
    }

    /// <summary>The run state the client is shown (spec 9.3 run tracker); state None without a run.</summary>
    public RunStatus Status(long characterId, uint revision)
    {
        var status = new RunStatus { Revision = revision };
        if (Active(characterId) is not { } run)
            return status;
        status.State = run.State;
        status.BonusPercent = (ushort)_rules.BonusPercent(run.CompletedSteps);
        status.PenaltyPercent = (ushort)_rules.PenaltyPercent(run.ForfeitedSteps);
        status.ClaimExp = ClaimExpOf(run);
        status.Steps = run.Steps.Select(s => new RunStepEntry
        {
            Kind = s.Kind,
            MonsterNetId = (ushort)s.MonsterNetId,
            ItemNetId = (ushort)s.ItemNetId,
            Required = (ushort)Math.Min(s.Required, ushort.MaxValue),
            Progress = (ushort)Math.Min(s.Progress, ushort.MaxValue),
            RewardExp = s.RewardExp,
            State = s.State,
        }).ToArray();
        return status;
    }

    /// <summary>Done or forfeited steps close the run: completed with at least one done step, abandoned without.</summary>
    private static void UpdateState(Run run)
    {
        if (run.HasActiveStep)
            run.State = RunState.Active;
        else
            run.State = run.CompletedSteps > 0 ? RunState.Completed : RunState.Abandoned;
    }

    private void Record(long characterId, CharacterRuns runs, Run run, RunAuditRecord? audit, string? opKey, QuestRunResult? answer)
    {
        runs.PendingRuns[run.RunId] = run;
        if (audit != null)
            runs.PendingAudit.Add(audit);
        if (opKey != null && answer != null)
            runs.SeenOps[opKey] = answer;
        MarkClass2?.Invoke(characterId);
        Changed?.Invoke(characterId);
    }

    private RunRecord ToRecord(Run run) => new(run.RunId, run.OwnerCharacterId, run.OutpostMarkerId, run.State, run.StartedAtUtcMs,
        run.BoardEpoch, _rules.BonusPercent(run.CompletedSteps), _rules.PenaltyPercent(run.ForfeitedSteps), run.ClaimedExp,
        run.Steps.Select(s => s.ToRecord()).ToList());

    private Run FromRecord(RunRecord record)
    {
        var steps = record.Steps.OrderBy(s => s.Index)
            .Select(s => new RunStep(s, _monsterNetId(s.MonsterDefId), s.ItemDefId == null ? 0 : _itemNetId(s.ItemDefId)))
            .ToList();
        return new Run(record.RunId, record.OwnerCharacterId, record.OutpostMarkerId, record.State, record.StartedAtUtcMs,
            record.BoardEpoch, steps) { ClaimedExp = record.ClaimedExp };
    }

    private CharacterRuns Get(long characterId) =>
        _characters.TryGetValue(characterId, out var runs)
            ? runs
            : throw new KeyNotFoundException($"character {characterId} has no runs attached");

    private sealed record InFlightBatch(Guid SaveId, QuestBatch Batch);

    private sealed class CharacterRuns
    {
        public Run? Active { get; set; }

        /// <summary>Runs changed since the last batch, by id (a claimed or abandoned run stays here until saved).</summary>
        public SortedDictionary<Guid, Run> PendingRuns { get; } = new();

        public List<RunAuditRecord> PendingAudit { get; } = new();
        public InFlightBatch? InFlight { get; set; }

        /// <summary>Run operation keys applied since the character was attached, with their answer (rule 5: a repeat gets the first).</summary>
        public Dictionary<string, QuestRunResult> SeenOps { get; } = new(StringComparer.Ordinal);
    }
}
