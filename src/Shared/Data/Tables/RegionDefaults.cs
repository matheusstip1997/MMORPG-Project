using System;
using Shared.World;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Default region flags per area type (spec 2.3 "Default flags by area type", R-2.3-05; catalog 8.5). Values come
    /// from the spec table, not tuning. Enum fields are camelCase strings in data/tables/RegionDefaults.json.
    /// TODO(open): the other R-2.3-05 flags have no per-area defaults in the spec yet (see <see cref="RegionFlags"/>).
    /// </summary>
    [Table("RegionDefaults", 1)]
    public sealed class RegionDefaults : IValidatableRow
    {
        /// <summary>Area type, for example <c>normalCity</c> or <c>openWorld</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        public PvpMode PvpMode { get; set; }

        /// <summary>Whether kills change honor (spec 2.3 "Honor effects").</summary>
        public bool HonorEffects { get; set; }

        public DeathDrop DeathDrop { get; set; }

        public RegionFlags ToFlags() => new RegionFlags(PvpMode, HonorEffects, DeathDrop);

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (!Enum.IsDefined(typeof(PvpMode), PvpMode))
                errors.Add(nameof(PvpMode), $"{(int)PvpMode} is not a pvp mode");
            if (!Enum.IsDefined(typeof(DeathDrop), DeathDrop))
                errors.Add(nameof(DeathDrop), $"{(int)DeathDrop} is not a death drop mode");
        }
    }
}
