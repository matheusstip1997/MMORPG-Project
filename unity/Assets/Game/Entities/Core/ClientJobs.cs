#nullable enable
using System;
using Shared.Protocol;
using Shared.Simulation.Jobs;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's side of jobs (spec 5.1, 9.13; WP-M3-1), engine-free so the bots use it too: the character's jobs as the
    /// server last sent them (<see cref="JobState"/>), the last answer to a pick, and the pick requests with their ids (one
    /// counter per connection: the server scopes them by connection). The server checks every pick again; the client never
    /// decides a job, a level or XP. It is the client's <see cref="IOwnJobs"/> (the minimap's job nodes and the node prompt,
    /// WP-M3-4), which needs <see cref="Rules"/> to map a <c>JobDef</c> id to its net id.
    /// </summary>
    public sealed class ClientJobs : IOwnJobs
    {
        /// <summary>The job tables (from the config the client loaded); without them <see cref="Has"/> is false.</summary>
        public JobRules? Rules { get; set; }

        /// <summary>The last state (null before the first); a newer version replaces an older one.</summary>
        public JobState? State { get; private set; }

        public JobResult? LastResult { get; private set; }

        /// <summary>Pick requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>Counts the states taken and the resets (the UI redraws on a change).</summary>
        public int Revision { get; private set; }

        /// <inheritdoc/>
        public int Version => Revision;

        /// <summary>True when the last state holds the job with this <c>JobDef</c> id (main or secondary).</summary>
        public bool Has(string jobId) => Rules?.Find(jobId) is { } job && Held(job.NetId) != null;

        /// <summary>
        /// Takes the job messages of the reliable channel; false for any other message. <paramref name="log"/> says what
        /// happened in words for the dev log.
        /// </summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.JobState:
                    if (ReliableMessages.TryRead(message, out JobState state) && (State == null || state.Version > State.Version))
                    {
                        State = state;
                        Revision++;
                        log = $"jobs ({state.Cause}): {state.Jobs.Length} held";
                    }
                    return true;
                case ReliableMessageId.JobResult:
                    if (ReliableMessages.TryRead(message, out JobResult result))
                    {
                        LastResult = result;
                        log = $"job pick {result.RequestId} ({result.JobNetId} as {result.Slot}): {result.Reason}";
                    }
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The job in <paramref name="slot"/> as last sent; null when the slot is empty or nothing was sent yet.</summary>
        public JobEntry? InSlot(JobSlot slot)
        {
            if (State == null)
                return null;
            foreach (var job in State.Jobs)
            {
                if (job.Slot == slot)
                    return job;
            }
            return null;
        }

        /// <summary>The held job with <paramref name="jobNetId"/>; null when the character does not hold it.</summary>
        public JobEntry? Held(int jobNetId)
        {
            if (State == null)
                return null;
            foreach (var job in State.Jobs)
            {
                if (job.JobNetId == jobNetId)
                    return job;
            }
            return null;
        }

        /// <summary>A permanent pick of <paramref name="jobNetId"/> for <paramref name="slot"/> at the job master <paramref name="npcEntityId"/>.</summary>
        public byte[] Request(uint npcEntityId, byte jobNetId, JobSlot slot, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.JobRequest, new JobRequest
            {
                RequestId = requestId, NpcEntityId = npcEntityId, JobNetId = jobNetId, Slot = slot,
            });
        }

        /// <summary>The connection ended: the jobs shown are stale and the next connection numbers its requests from 1.</summary>
        public void Reset()
        {
            State = null;
            LastResult = null;
            RequestsSent = 0;
            Revision++;
        }
    }
}
