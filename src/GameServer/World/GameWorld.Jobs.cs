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
/// Jobs in the world (spec 5.1, 9.13; WP-M3-1): <see cref="JobService"/> holds every character's job records and earns their
/// XP from the activity events; the world reads the pick requests in phase 1 (the job master gate, combat), announces a
/// committed pick on the bus (<c>JobChosen</c>) and to the player, and sends the job state in phase 10. Without the job
/// tables nobody holds a job (worlds built for tests).
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

    /// <summary>The idempotency key of a pick: one per world run, entity, connection and request id (spec 9.13 pattern).</summary>
    internal string JobOpKey(Player player, uint requestId) => $"job:{_jobRunId}:{player.Id}:{player.Connection}:{requestId}";

    /// <summary>A save committed a pick (rule 5): the bus and the answer to the connection that asked.</summary>
    private void OnJobOpCommitted(long characterId, CommittedJobOp op)
    {
        _playersByCharacter.TryGetValue(characterId, out var player);
        _events?.Publish(new JobChosen(characterId, player?.Id ?? 0, op.Record.JobId, op.Record.Slot), characterId,
            player?.RegionIndex ?? 0);
        if (player != null && player.Connection == op.Connection)
            player.Jobs.Results.Add(op.Answer);
    }

    /// <summary>The character's committed jobs or their progress changed: the client hears of it in phase 10.</summary>
    private void OnJobsChanged(long characterId, JobCause cause)
    {
        if (_playersByCharacter.TryGetValue(characterId, out var player))
            player.Jobs.Mark(cause);
    }
}
