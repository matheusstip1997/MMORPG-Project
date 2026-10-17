using System;

namespace Shared.World
{
    /// <summary>
    /// R-2.3-05 <c>pvpMode</c>. In config files the values are camelCase strings: <c>off</c>, <c>armedOnly</c>,
    /// <c>alwaysOn</c>.
    /// </summary>
    public enum PvpMode
    {
        Off = 0,
        ArmedOnly = 1,
        AlwaysOn = 2,
    }

    /// <summary>R-2.3-05 <c>deathDrop</c>. Config values: <c>on</c>, <c>off</c>, <c>confiscate</c>.</summary>
    public enum DeathDrop
    {
        Off = 0,
        On = 1,
        Confiscate = 2,
    }

    /// <summary>
    /// The resolved flags of one region (spec 2.3, R-2.3-05), from its <c>RegionDef</c> area type and the
    /// <c>RegionDefaults</c> row for it. M1 carries only the flags whose defaults spec 2.3 defines per area type.
    /// TODO(open): honorPolicy, visibilityChecks, mountsAllowed, feeProfile, ownerGuildId, arenaQueue, raidStart and
    /// arenaRuleset have no defaults per area type in the spec yet; add them with the systems that read them.
    /// </summary>
    public readonly struct RegionFlags : IEquatable<RegionFlags>
    {
        public RegionFlags(PvpMode pvpMode, bool honorEffects, DeathDrop deathDrop)
        {
            PvpMode = pvpMode;
            HonorEffects = honorEffects;
            DeathDrop = deathDrop;
        }

        public PvpMode PvpMode { get; }

        /// <summary>Whether kills change honor here (spec 2.3 "Honor effects"; off in lawless cities).</summary>
        public bool HonorEffects { get; }

        public DeathDrop DeathDrop { get; }

        public bool Equals(RegionFlags other) =>
            PvpMode == other.PvpMode && HonorEffects == other.HonorEffects && DeathDrop == other.DeathDrop;

        public override bool Equals(object? obj) => obj is RegionFlags other && Equals(other);
        public override int GetHashCode() => ((int)PvpMode * 3 + (int)DeathDrop) * 2 + (HonorEffects ? 1 : 0);
        public override string ToString() => $"pvp {PvpMode}, honor effects {HonorEffects}, death drop {DeathDrop}";
    }
}
