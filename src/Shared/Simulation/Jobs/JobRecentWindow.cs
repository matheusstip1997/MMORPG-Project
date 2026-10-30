using System;

namespace Shared.Simulation.Jobs
{
    /// <summary>
    /// The last activities of one job, oldest first (R-5.1-04 diminishing returns; WP-M3-1): a fixed ring of (activity,
    /// subject) where the subject is the node or recipe net id of the activity event. Saved with the job record
    /// (<c>job_progress.recent</c>), so logging out does not reset the diminishing returns. Not thread-safe.
    /// </summary>
    public sealed class JobRecentWindow
    {
        private readonly JobActivity[] _activities;
        private readonly int[] _subjects;
        private int _next;

        public JobRecentWindow(int capacity)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _activities = new JobActivity[capacity];
            _subjects = new int[capacity];
        }

        public int Capacity => _activities.Length;

        public int Count { get; private set; }

        /// <summary>
        /// How many of the newest <paramref name="lastN"/> entries are the same activity on the same subject (the repeats the
        /// next activity's XP is diminished by).
        /// </summary>
        public int Repeats(JobActivity activity, int subject, int lastN)
        {
            int n = Math.Min(Math.Max(0, lastN), Count);
            int repeats = 0;
            for (int age = 0; age < n; age++)
            {
                int index = (_next - 1 - age + Capacity * 2) % Capacity;
                if (_activities[index] == activity && _subjects[index] == subject)
                    repeats++;
            }
            return repeats;
        }

        /// <summary>Appends an entry; when full the oldest is dropped.</summary>
        public void Add(JobActivity activity, int subject)
        {
            _activities[_next] = activity;
            _subjects[_next] = subject;
            _next = (_next + 1) % Capacity;
            if (Count < Capacity)
                Count++;
        }

        /// <summary>The entry at <paramref name="index"/>, 0 the oldest.</summary>
        public (JobActivity Activity, int Subject) At(int index)
        {
            if (index < 0 || index >= Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            int at = (_next - Count + index + Capacity) % Capacity;
            return (_activities[at], _subjects[at]);
        }

        public JobRecentWindow Clone()
        {
            var copy = new JobRecentWindow(Capacity);
            for (int i = 0; i < Count; i++)
            {
                var (activity, subject) = At(i);
                copy.Add(activity, subject);
            }
            return copy;
        }
    }
}
