using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Jobs;
using GameServer.Net;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Jobs;

namespace GameServer.World;

/// <summary>
/// Jobs in the world (spec 5.1, 9.13; WP-M3-1, WP-M3-7): <see cref="JobService"/> holds every character's job records and
/// earns their XP from the activity events; the world reads the pick and job tree requests in phase 1 (the job master gate,
/// combat, the activity sessions), announces committed operations on the bus (<c>JobChosen</c>, <c>JobNodeLearned</c>,
/// <c>JobTreeRespec</c>) and to the player, and sends the job state in phase 10. Without the job tables nobody holds a job
/// (worlds built for tests).
/// </summary>
public sealed partial class GameWorld
{
    private JobService? _jobs;
    private JobMasterInteraction? _jobMaster;
    private JobSender? _jobSender;
    private string _jobRunId = string.Empty;
    private int _jobCombatStateMs;

    /// <summary>The job records of the world's characters (WP-M3-1); null without the job tables.</summary>
    public JobService? Jobs => _jobs;

    /// <summary>What harvesting, the forge, enchanting and gems read of a character's jobs (WP-M3-1); null without the job tables.</summary>
    public IJobQuery? JobQuery => _jobs;

    /// <summary>
    /// The job tree bonuses an activity reads when it starts (WP-M3-7; harvesting, WP-M3-5): capped, from the committed
    /// nodes (<see cref="JobService"/>). Without the job tables, none.
    /// </summary>
    public IJobBonusQuery JobBonusQuery => (IJobBonusQuery?)_jobs ?? NoJobBonuses.Instance;

    /// <summary>
    /// Activities in progress that read the job tree's bonuses (a harvest channel, a forge session): a respec waits while one
    /// runs (WP-M3-7, decision 6). The harvest and forge services add their <see cref="IJobActivityGate"/> here.
    /// </summary>
    public List<IJobActivityGate> JobActivityGates { get; } = new();

    private void BuildJobs(JobRules? rules, EventBus? events, string runId, int combatStateMs)
    {
        _jobSender = new JobSender(_transport);
        if (rules == null)
            return;
        _jobRunId = runId;
        _jobCombatStateMs = combatStateMs;
        _jobs = new JobService(rules, _inventory, events)
        {
            OpCommitted = OnJobOpCommitted,
            Changed = OnJobsChanged,
            EntityOf = id => _playersByCharacter.TryGetValue(id, out var player) ? player.Id : 0,
        };
        if (events != null)
            _jobs.Subscribe(events);
        if (_achievements != null)
            _achievements.ActivityOps = new RecentOpKeys(rules.Settings.RecentOpKeys); // gathering and crafting counters
    }

    /// <summary>Phase 1: one job pick of <paramref name="player"/>, answered now or once its save committed.</summary>
    private void HandleJobRequest(Player player, in ClientRequest request)
    {
        var slot = (JobSlot)request.ToSlot;
        if (_jobs == null)
        {
            player.Jobs.Results.Add(new JobResult
            {
                RequestId = request.RequestId, Slot = slot, JobNetId = (byte)request.ItemNetId, Reason = JobReason.JobsUnavailable,
            });
            return;
        }
        // Spec 5.1 M3 decisions: at the job master (the outpost gate and range), out of combat and not during an action.
        bool inReach = Outposts != null
                       && Outposts.CheckReach(player, request.DropEntityId, NpcInteraction.Jobs, out _) == OutpostReason.None;
        bool blocked = CombatState.IsInCombat(player.LastCombatAtMs, _clock.UtcNowMs, _jobCombatStateMs) || player.Actions.IsBusy;
        var ask = new JobAsk(request.RequestId, (byte)request.ItemNetId, slot, JobOpKey(player, request.RequestId),
            _progression?.GetLevel(player.CharacterId) ?? 1, blocked, inReach);
        if (_jobs.Handle(player.CharacterId, ask, player.Connection) is { } answer)
            player.Jobs.Results.Add(answer);
    }

    /// <summary>
    /// Phase 1: one job tree learn or respec of <paramref name="player"/> (WP-M3-7), from the Jobs window anywhere. Learning
    /// works any time (decision 10); a respec is refused in combat or an action and during a harvest or forge session.
    /// </summary>
    private void HandleJobTreeRequest(Player player, in ClientRequest request)
    {
        var op = (JobTreeOp)request.FromSlot;
        if (_jobs == null)
        {
            player.Jobs.TreeResults.Add(new JobTreeResult
            {
                RequestId = request.RequestId, Op = op, JobNetId = (byte)request.ItemNetId, NodeId = request.Text ?? string.Empty,
                Reason = JobReason.JobsUnavailable,
            });
            return;
        }
        bool blocked = CombatState.IsInCombat(player.LastCombatAtMs, _clock.UtcNowMs, _jobCombatStateMs) || player.Actions.IsBusy;
        var ask = new JobTreeAsk(request.RequestId, op, (byte)request.ItemNetId, request.Text ?? string.Empty,
            JobTreeOpKey(player, request.RequestId), _progression?.GetLevel(player.CharacterId) ?? 1, blocked,
            InJobActivity(player.CharacterId));
        if (_jobs.Handle(player.CharacterId, ask, player.Connection) is { } answer)
            player.Jobs.TreeResults.Add(answer);
    }

    /// <summary>Any registered activity of the character is in progress (a respec waits, decision 6).</summary>
    private bool InJobActivity(long characterId)
    {
        foreach (var gate in JobActivityGates)
        {
            if (gate.InSession(characterId))
                return true;
        }
        return false;
    }

    /// <summary>The idempotency key of a pick: one per world run, entity, connection and request id (spec 9.13 pattern).</summary>
    internal string JobOpKey(Player player, uint requestId) => $"job:{_jobRunId}:{player.Id}:{player.Connection}:{requestId}";

    /// <summary>The idempotency key of a job tree learn or respec (WP-M3-7), like <see cref="JobOpKey"/>.</summary>
    internal string JobTreeOpKey(Player player, uint requestId) => $"jobTree:{_jobRunId}:{player.Id}:{player.Connection}:{requestId}";

    /// <summary>A save committed a pick, a learn or a respec (rule 5): the bus and the answer to the connection that asked.</summary>
    private void OnJobOpCommitted(long characterId, CommittedJobOp op)
    {
        _playersByCharacter.TryGetValue(characterId, out var player);
        uint entityId = player?.Id ?? 0;
        int region = player?.RegionIndex ?? 0;
        var record = op.Record;
        switch (record.Op)
        {
            case Persistence.JobOp.Pick:
                _events?.Publish(new JobChosen(characterId, entityId, record.JobId, record.Slot), characterId, region);
                break;
            case Persistence.JobOp.Learn:
                _events?.Publish(new JobNodeLearned(characterId, entityId, record.JobId, record.NodeId, record.FromLevel, record.ToLevel,
                    record.PointsBefore - record.PointsAfter), characterId, region);
                break;
            case Persistence.JobOp.Respec:
                _events?.Publish(new JobTreeRespec(characterId, entityId, record.JobId, record.FromLevel, record.PointsAfter - record.PointsBefore),
                    characterId, region);
                break;
        }
        if (player == null || player.Connection != op.Connection)
            return;
        if (op.Answer != null && op.Answer.RequestId != Dev.Commands.DevConsole.ConsoleRequestId) // a dev console pick answers in the console (WP-M3-13)
            player.Jobs.Results.Add(op.Answer);
        if (op.TreeAnswer != null)
            player.Jobs.TreeResults.Add(op.TreeAnswer);
    }

    /// <summary>The character's committed jobs or their progress changed: the client hears of it in phase 10.</summary>
    private void OnJobsChanged(long characterId, JobCause cause)
    {
        if (_playersByCharacter.TryGetValue(characterId, out var player))
            player.Jobs.Mark(cause);
    }
}
