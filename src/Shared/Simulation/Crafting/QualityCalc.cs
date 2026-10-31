using Shared.Data.Tables;

namespace Shared.Simulation.Crafting
{
    /// <summary>
    /// The raw quality numbers of one craft (R-5.3-02: "material numbers + action numbers + skill-tree bonuses + minigame
    /// result + server randomness + a small bounded random term"), before <see cref="QualityCalc"/> caps them.
    /// </summary>
    public struct QualityInputs
    {
        public int Materials;
        public int Actions;
        public int JobTree;
        public int Minigame;
        public int ServerRoll;
        /// <summary>Signed: from minus its cap to its cap.</summary>
        public int SmallTerm;
    }

    /// <summary>The capped components of one craft and their sum (the item's quality).</summary>
    public readonly struct QualityBreakdown
    {
        public QualityBreakdown(int materials, int actions, int jobTree, int minigame, int serverRoll, int smallTerm, int total)
        {
            Materials = materials;
            Actions = actions;
            JobTree = jobTree;
            Minigame = minigame;
            ServerRoll = serverRoll;
            SmallTerm = smallTerm;
            Total = total;
        }

        public int Materials { get; }
        public int Actions { get; }
        public int JobTree { get; }
        public int Minigame { get; }
        public int ServerRoll { get; }
        public int SmallTerm { get; }

        /// <summary>The quality: the sum, never below 0 nor above <see cref="QualityCalc.MaxQuality"/>.</summary>
        public int Total { get; }
    }

    /// <summary>The cap of each <see cref="QualityComponent"/> (<see cref="QualityCaps"/> rows).</summary>
    public readonly struct QualityCapSet
    {
        public QualityCapSet(int materials, int actions, int jobTree, int minigame, int serverRoll, int smallTerm)
        {
            Materials = materials;
            Actions = actions;
            JobTree = jobTree;
            Minigame = minigame;
            ServerRoll = serverRoll;
            SmallTerm = smallTerm;
        }

        public int Materials { get; }
        public int Actions { get; }
        public int JobTree { get; }
        public int Minigame { get; }
        public int ServerRoll { get; }
        public int SmallTerm { get; }

        public int Of(QualityComponent component) => component switch
        {
            QualityComponent.Materials => Materials,
            QualityComponent.Actions => Actions,
            QualityComponent.JobTree => JobTree,
            QualityComponent.Minigame => Minigame,
            QualityComponent.ServerRoll => ServerRoll,
            QualityComponent.SmallTerm => SmallTerm,
            _ => 0,
        };

        /// <summary>The caps from <see cref="QualityCaps"/> rows; false when a component has no row.</summary>
        public static bool TryBuild(System.Collections.Generic.IEnumerable<QualityCaps> rows, out QualityCapSet caps)
        {
            int[] values = new int[(int)QualityComponent.SmallTerm + 1];
            foreach (var row in rows)
            {
                if (QualityCaps.TryComponent(row.Id, out var component))
                    values[(int)component] = row.Cap;
            }
            caps = new QualityCapSet(values[1], values[2], values[3], values[4], values[5], values[6]);
            for (int i = 1; i < values.Length; i++)
            {
                if (values[i] <= 0)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Quality of a forge craft (spec 5.3 "QualityCalc is a pure function with each component bounded by config";
    /// R-5.3-02; WP-M3-6). Every component is clamped to 0..its cap, the small term to -cap..cap, and the sum to
    /// 0..<see cref="MaxQuality"/>. Integer only, no allocation (architecture rule 6, 7).
    /// </summary>
    public static class QualityCalc
    {
        /// <summary>The highest quality an item instance stores (<c>ItemProperties.MaxQuality</c>).</summary>
        public const int MaxQuality = ushort.MaxValue;

        public static QualityBreakdown Compute(in QualityInputs raw, in QualityCapSet caps)
        {
            int materials = Clamp(raw.Materials, 0, caps.Materials);
            int actions = Clamp(raw.Actions, 0, caps.Actions);
            int jobTree = Clamp(raw.JobTree, 0, caps.JobTree);
            int minigame = Clamp(raw.Minigame, 0, caps.Minigame);
            int serverRoll = Clamp(raw.ServerRoll, 0, caps.ServerRoll);
            int smallTerm = Clamp(raw.SmallTerm, -caps.SmallTerm, caps.SmallTerm);
            long sum = (long)materials + actions + jobTree + minigame + serverRoll + smallTerm;
            int total = sum < 0 ? 0 : sum > MaxQuality ? MaxQuality : (int)sum;
            return new QualityBreakdown(materials, actions, jobTree, minigame, serverRoll, smallTerm, total);
        }

        /// <summary>
        /// The quality range a session can still end in, for the window's preview: the known numbers (materials, job tree)
        /// with every random number at its lowest and at its highest. Shown before Finish instead of a rolled number, so
        /// cancelling and restarting rerolls nothing the player saw (M3 plan risk 1).
        /// </summary>
        public static void Range(int materials, int actionsMin, int actionsMax, int jobTree, int minigameMax, in QualityCapSet caps,
            out int min, out int max)
        {
            var low = new QualityInputs
            {
                Materials = materials, Actions = actionsMin, JobTree = jobTree, Minigame = 0, ServerRoll = 0,
                SmallTerm = -caps.SmallTerm,
            };
            var high = new QualityInputs
            {
                Materials = materials, Actions = actionsMax, JobTree = jobTree, Minigame = minigameMax,
                ServerRoll = caps.ServerRoll, SmallTerm = caps.SmallTerm,
            };
            min = Compute(in low, in caps).Total;
            max = Compute(in high, in caps).Total;
        }

        private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    }
}
