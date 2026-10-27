using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// A demand curve (R-4.2-03): a piecewise-linear map from a demand ratio (decayed count over the expected count for the
    /// active players, in thousandths: 1000 = as expected) to a whole percent; flat beyond its ends. Three rows
    /// (<see cref="EconomyCoverage"/>): <see cref="SpawnDelay"/> (respawn delay scale), <see cref="Reward"/> (reward
    /// multiplier for runs) and <see cref="Price"/> (the dynamic sell multiplier). The clamps and smoothing live in
    /// <see cref="DemandSettings"/> and <see cref="PriceFloorRecovery"/>. Placeholder shapes in data.
    /// </summary>
    [Table("DemandCurve", 1)]
    public sealed class DemandCurve : IValidatableRow
    {
        public const string SpawnDelay = "spawnDelay";
        public const string Reward = "reward";
        public const string Price = "price";

        public static readonly string[] Required = { SpawnDelay, Reward, Price };

        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>The ratio of each point in thousandths, ascending, the first one 0.</summary>
        public int[] RatioPermille { get; set; } = Array.Empty<int>();

        /// <summary>The whole percent at each point.</summary>
        public int[] Percent { get; set; } = Array.Empty<int>();

        public void Validate(RowErrors errors)
        {
            if (Array.IndexOf(Required, Id) < 0)
                errors.Add(nameof(Id), $"must be one of {string.Join(", ", Required)}");
            if (RatioPermille.Length < 2)
                errors.Add(nameof(RatioPermille), "must have at least two points");
            if (Percent.Length != RatioPermille.Length)
                errors.Add(nameof(Percent), "must have one value per ratio point");
            if (RatioPermille.Length > 0 && RatioPermille[0] != 0)
                errors.Add(nameof(RatioPermille), "must start at 0");
            for (int i = 1; i < RatioPermille.Length; i++)
            {
                if (RatioPermille[i] <= RatioPermille[i - 1])
                    errors.Add(nameof(RatioPermille), "must be strictly ascending");
                if (RatioPermille[i] > 1_000_000)
                    errors.Add(nameof(RatioPermille), "points above 1000000 (a ratio of 1000) are not supported");
            }
            foreach (var percent in Percent)
            {
                if (percent < 1 || percent > 100_000)
                    errors.Add(nameof(Percent), "values must be from 1 to 100000");
            }
        }

        public static DemandCurve? Find(ConfigSet set, string id) => NetIds.Find<DemandCurve>(set, id, row => row.Id);
    }
}
