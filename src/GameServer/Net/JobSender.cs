using GameServer.Entities;
using GameServer.Jobs;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (spec 5.1, 9.13; WP-M3-1, WP-M3-7): a player's pick and job tree answers, then its <see cref="JobState"/> (the
/// committed jobs with level, XP, points and learned nodes) when it changed, the job master opened, or the client is new.
/// Reliable channel, MemoryPack, never the hot snapshot; an accepted pick, learn or respec is answered only once its save
/// committed (rule 5), right before the state that shows it.
/// </summary>
internal sealed class JobSender
{
    private readonly ITransport _transport;

    public JobSender(ITransport transport)
    {
        _transport = transport;
    }

    public long StatesSent { get; private set; }

    public void Send(Player player, JobService? jobs)
    {
        var answers = player.Jobs;
        foreach (var result in answers.Results)
            _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.JobResult, result));
        answers.Results.Clear();
        foreach (var result in answers.TreeResults)
            _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.JobTreeResult, result));
        answers.TreeResults.Clear();
        if (!answers.Dirty || jobs?.View(player.CharacterId) is not { } view)
            return;
        answers.Dirty = false;
        var entries = new JobEntry[Math.Min(view.Count, ReliableMessages.MaxJobEntries)];
        for (int i = 0; i < entries.Length; i++)
        {
            var job = view[i];
            var nodes = new JobNodeEntry[Math.Min(job.Nodes.Count, ReliableMessages.MaxJobTreeNodes)];
            for (int n = 0; n < nodes.Length; n++)
                nodes[n] = new JobNodeEntry { NodeId = job.Nodes[n].NodeId, Level = (byte)Math.Clamp(job.Nodes[n].Level, 1, byte.MaxValue) };
            entries[i] = new JobEntry
            {
                JobNetId = (byte)job.Def.NetId,
                Slot = job.Slot,
                Level = (ushort)Math.Clamp(job.Level, 1, ushort.MaxValue),
                Xp = (uint)Math.Clamp(job.Xp, 0, uint.MaxValue),
                XpToNext = (uint)Math.Max(0, job.XpToNext),
                Points = (ushort)Math.Clamp(job.Points, 0, ushort.MaxValue),
                Nodes = nodes,
            };
        }
        var message = new JobState { Version = ++answers.Version, Jobs = entries, Cause = answers.Cause };
        answers.Cause = JobCause.Snapshot;
        _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.JobState, message));
        StatesSent++;
    }
}
