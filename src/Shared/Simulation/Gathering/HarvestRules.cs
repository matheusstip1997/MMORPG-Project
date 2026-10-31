using Shared.Protocol;

namespace Shared.Simulation.Gathering
{
    /// <summary>What a harvest check reads (server, client prompt and bots fill it from their own views).</summary>
    public struct HarvestCheck
    {
        public bool Alive;
        /// <summary>The entity is a resource node.</summary>
        public bool IsNode;
        /// <summary>The node is available (not depleted).</summary>
        public bool Available;
        /// <summary>Squared distance from the harvester to the node in cm².</summary>
        public long DistanceSqCm;
        public int ReachCm;
        /// <summary>The harvester holds the node's job (R-5.2-01).</summary>
        public bool HasJob;
        /// <summary>The best tier of a tool of the node's type in the Safe bag's equipment area; 0 for none (R-5.2-02).</summary>
        public int BestToolTier;
        public int MinToolTier;
        /// <summary>The receiving bag has room for one unit of every item the node can yield at this job level.</summary>
        public bool HasRoom;
        /// <summary>An action, an interrupt, a use or another harvest is running.</summary>
        public bool Busy;
    }

    /// <summary>
    /// The harvest gate (spec 5.2 R-5.2-01, R-5.2-02, "M3 resource nodes"; WP-M3-5), shared so the server, the client's
    /// prompt and the bots check alike: alive, a node, available, in reach, the job, a tool of the node's type, its tier,
    /// room in the bag, not busy, in that order. The server checks again when the channel ends.
    /// </summary>
    public static class HarvestRules
    {
        public static HarvestReason Check(in HarvestCheck check)
        {
            if (!check.Alive)
                return HarvestReason.HarvestWhileDead;
            if (!check.IsNode)
                return HarvestReason.NodeNotFound;
            if (!check.Available)
                return HarvestReason.NodeDepleted;
            if (!InReach(check.DistanceSqCm, check.ReachCm))
                return HarvestReason.OutOfRange;
            if (!check.HasJob)
                return HarvestReason.JobRequired;
            if (check.BestToolTier <= 0)
                return HarvestReason.ToolRequired;
            if (check.BestToolTier < check.MinToolTier)
                return HarvestReason.ToolTierTooLow;
            if (!check.HasRoom)
                return HarvestReason.BagFull;
            if (check.Busy)
                return HarvestReason.ActionBusy;
            return HarvestReason.None;
        }

        /// <summary>A squared distance within <paramref name="reachCm"/>.</summary>
        public static bool InReach(long distanceSqCm, int reachCm) => distanceSqCm <= (long)reachCm * reachCm;

        /// <summary>The squared distance between two points in cm (no overflow for any world coordinate).</summary>
        public static long DistanceSq(int ax, int ay, int bx, int by)
        {
            long dx = (long)ax - bx, dy = (long)ay - by;
            return dx * dx + dy * dy;
        }
    }
}
