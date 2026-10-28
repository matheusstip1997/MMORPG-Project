using MemoryPack;
using Shared.Simulation.Stats;

namespace Shared.Protocol
{
    /// <summary>Why a <see cref="ProgressionState"/> was sent (the client toasts level-ups and rank-ups). Append only.</summary>
    public enum ProgressionCause : byte
    {
        /// <summary>Entering the world (or a reconnect): the state as it is, no toast.</summary>
        Initial = 0,
        /// <summary>Exp changed without a level-up.</summary>
        Exp = 1,
        /// <summary>One or more levels gained (several in one kill are allowed).</summary>
        LevelUp = 2,
        /// <summary>A rank-up committed (R-3.6-04; sent only after its save, rule 5).</summary>
        RankUp = 3,
        /// <summary>The skill tree spent or gave back points (R-3.5-04, WP-M2-5; sent only after the save committed).</summary>
        SkillPoints = 4,
    }

    /// <summary>
    /// Server to client, reliable (spec 3.6, 9.4; WP-M2-3): the observer's own level, exp, rank and skill points for the exp
    /// bar, the resource panel line and the toasts, plus the band multiplier of R-3.6-04 (the <c>exp_note</c>). Sent at
    /// enter world and whenever it changed, never in the hot snapshot. MemoryPack after the <see cref="ReliableMessageId"/>
    /// byte; append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class ProgressionState
    {
        public int Level { get; set; }

        /// <summary>Exp into the current level.</summary>
        public long Exp { get; set; }

        /// <summary>Exp the current level needs; 0 at the level cap.</summary>
        public long ExpToNext { get; set; }

        public Rank Rank { get; set; }

        /// <summary>Unspent skill points (R-3.5-04; the skill tree spends them, WP-M2-5).</summary>
        public int SkillPoints { get; set; }

        /// <summary>The band modifier of R-3.6-03 for the character's level and rank, in percent (100 = x1.0).</summary>
        public int BandPercent { get; set; }

        public ProgressionCause Cause { get; set; }
    }
}
