#nullable enable

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The jobs of the own character as the client knows them (spec 5.1, 9.3): what the minimap's job nodes and the node
    /// prompt filter on (WP-M3-4). TODO(WP-M3-1): <c>ClientJobs</c> implements this from <c>JobState</c>; until it is on
    /// main, <see cref="AllJobs"/> stands in and shows every node.
    /// </summary>
    public interface IOwnJobs
    {
        /// <summary>True when the own character has the job with this <c>JobDef</c> id (main or secondary).</summary>
        bool Has(string jobId);

        /// <summary>Changes whenever the job list changes (readers rebuild what they derived from it).</summary>
        int Version { get; }
    }

    /// <summary>The stand-in until <c>ClientJobs</c> lands (WP-M3-1): every job counts as the own job.</summary>
    public sealed class AllJobs : IOwnJobs
    {
        public static readonly AllJobs Instance = new AllJobs();

        public bool Has(string jobId) => true;

        public int Version => 0;
    }
}
