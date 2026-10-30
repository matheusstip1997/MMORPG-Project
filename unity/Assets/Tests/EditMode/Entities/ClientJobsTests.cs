#nullable enable
using Game.Gameplay.Entities;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Jobs;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>
    /// WP-M3-1 on the client: the reliable <see cref="JobState"/> and <see cref="JobResult"/> read in Unity's build of
    /// Game.Shared (MemoryPack), <see cref="ClientJobs"/> keeps the newest state and the last answer, numbers its picks per
    /// connection, and leaves other messages to the other readers.
    /// </summary>
    public class ClientJobsTests
    {
        private static byte[] State(uint version, params JobEntry[] jobs) =>
            ReliableMessages.Write(ReliableMessageId.JobState, new JobState { Version = version, Jobs = jobs, Cause = JobCause.Picked });

        private static JobEntry Entry(byte job, JobSlot slot, ushort level = 1) =>
            new JobEntry { JobNetId = job, Slot = slot, Level = level, Xp = 10, XpToNext = 100, Points = 0 };

        [Test]
        public void The_newest_state_is_kept_and_an_older_one_is_ignored()
        {
            var jobs = new ClientJobs();

            Assert.That(jobs.Apply(State(2, Entry(1, JobSlot.Main)), out var log), Is.True);
            Assert.That(jobs.Apply(State(1), out _), Is.True); // taken, but older

            Assert.That(jobs.State!.Version, Is.EqualTo(2u));
            Assert.That(jobs.InSlot(JobSlot.Main)!.JobNetId, Is.EqualTo((byte)1));
            Assert.That(jobs.InSlot(JobSlot.Secondary), Is.Null);
            Assert.That(jobs.Held(1), Is.Not.Null);
            Assert.That(log, Does.Contain("1 held"));
        }

        [Test]
        public void Picks_are_numbered_per_connection_and_answers_are_kept()
        {
            var jobs = new ClientJobs();
            var request = jobs.Request(42, 1, JobSlot.Main, out uint id);
            Assert.That(id, Is.EqualTo(1u));
            Assert.That(ReliableMessages.TryRead(request, out JobRequest read), Is.True);
            Assert.That((read.RequestId, read.NpcEntityId, read.JobNetId, read.Slot), Is.EqualTo((1u, 42u, (byte)1, JobSlot.Main)));

            var answer = ReliableMessages.Write(ReliableMessageId.JobResult,
                new JobResult { RequestId = 1, Slot = JobSlot.Main, JobNetId = 1, Reason = JobReason.JobAlreadyChosen });
            Assert.That(jobs.Apply(answer, out _), Is.True);
            Assert.That(jobs.LastResult!.Reason, Is.EqualTo(JobReason.JobAlreadyChosen));

            var other = ReliableMessages.Write(ReliableMessageId.InventoryMoveResult,
                new InventoryMoveResult { RequestId = 1, Outcome = InventoryMoveOutcome.Moved });
            Assert.That(jobs.Apply(other, out _), Is.False);

            jobs.Reset();
            Assert.That((jobs.State, jobs.LastResult, jobs.RequestsSent), Is.EqualTo(((JobState?)null, (JobResult?)null, 0u)));
            jobs.Request(42, 1, JobSlot.Main, out id);
            Assert.That(id, Is.EqualTo(1u));
        }

        [Test]
        public void As_the_own_jobs_it_names_the_held_jobs_by_JobDef_id()
        {
            // IOwnJobs (WP-M3-4's job nodes): the JobDef id maps to the net id through the rules; the version follows each change.
            var set = new ConfigSet();
            set.Add(new ConfigTable(typeof(JobSettings), 1, new object[] { new JobSettings { MaxLevel = 2, PointsPerLevel = 1, RecentOpKeys = 8 } }));
            set.Add(new ConfigTable(typeof(JobLevelCurve), 1, new object[] { new JobLevelCurve { Level = 1, XpToNext = 100 } }));
            set.Add(new ConfigTable(typeof(JobDef), 1, new object[]
            {
                new JobDef { Id = "blacksmith", NetId = 1, NameId = "job.blacksmith", AllowedAsSecondary = true, MinLevel = 12 },
                new JobDef { Id = "jeweler", NetId = 2, NameId = "job.jeweler", AllowedAsSecondary = true, MinLevel = 12 },
            }));
            IOwnJobs own = new ClientJobs();
            var jobs = (ClientJobs)own;
            Assert.That(own.Has("blacksmith"), Is.False, "no rules, no state");

            jobs.Rules = JobRules.From(set);
            int version = own.Version;
            jobs.Apply(State(1, Entry(1, JobSlot.Main)), out _);
            Assert.That((own.Has("blacksmith"), own.Has("jeweler"), own.Has("unknown")), Is.EqualTo((true, false, false)));
            Assert.That(own.Version, Is.GreaterThan(version));

            version = own.Version;
            jobs.Reset();
            Assert.That((own.Has("blacksmith"), own.Version > version), Is.EqualTo((false, true)));
        }
    }
}
