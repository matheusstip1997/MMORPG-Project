#nullable enable

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The jobs of the own character as the client knows them (spec 5.1, 9.3): what the minimap's job nodes and the node
    /// prompt filter on (WP-M3-4). <see cref="ClientJobs"/> implements it from <c>JobState</c> (WP-M3-1).
    /// </summary>
    public interface IOwnJobs
    {
        /// <summary>True when the own character has the job with this <c>JobDef</c> id (main or secondary).</summary>
        bool Has(string jobId);

        /// <summary>Changes whenever the job list changes (readers rebuild what they derived from it).</summary>
        int Version { get; }
    }
}
