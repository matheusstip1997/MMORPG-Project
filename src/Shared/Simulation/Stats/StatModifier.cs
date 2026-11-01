namespace Shared.Simulation.Stats
{
    /// <summary>
    /// Where a <see cref="StatModifier"/> comes from (spec 3.4 implementation: gear, skill, buff, food, potion, gem,
    /// enchant, mount, title; debuff from 3.7; job from 6.6). The arena masks sources by type (R-6.6-05).
    /// </summary>
    public enum StatSourceType : byte
    {
        Gear = 0,
        /// <summary>The skill tree's passives (spec 3.5, source type "skillTree"; WP-M2-5: <c>StatsService.RebuildSkills</c>).</summary>
        Skill = 1,
        Buff = 2,
        Debuff = 3,
        Food = 4,
        Potion = 5,
        Gem = 6,
        /// <summary>The enchant lines of equipped enchanted items (spec 3.4, 5.3 M3 decision 10; WP-M3-8: <c>EnchantBonus</c> by level, source id 0, rebuilt with the gear).</summary>
        Enchant = 7,
        Mount = 8,
        Title = 9,
        Job = 10,
        /// <summary>The placed cards and family set bonuses of the Bystial book (spec 14.2, WP-M2-8; source id 0, rebuilt whole).</summary>
        BystialCard = 11,
        /// <summary>
        /// The free attribute points placed by the player (R-3.4-08, WP-M2-13): flat points on STR, DEX, STA and INT, which
        /// the derived stats read like the class growth (source id 0, rebuilt whole by <c>StatsService.RebuildAttributes</c>).
        /// </summary>
        Attribute = 12,
    }

    /// <summary>
    /// One change to one stat (spec 3.4 implementation: <c>{statId, flat, percent, sourceType, sourceId, expiresAt}</c>).
    /// <see cref="StatId"/> is the stat's index in <see cref="StatRules"/>. A stat's value is
    /// <c>(base + sum of flat) x (100 + sum of percent) / 100</c>, so percents add up rather than multiply
    /// (placeholder decision). <see cref="ExpiresAtMs"/> is UTC ms from <c>IClock</c>; 0 means it never expires.
    /// </summary>
    public readonly struct StatModifier
    {
        public StatModifier(int statId, int flat, int percent, StatSourceType sourceType, long sourceId, long expiresAtMs = 0)
        {
            StatId = statId;
            Flat = flat;
            Percent = percent;
            SourceType = sourceType;
            SourceId = sourceId;
            ExpiresAtMs = expiresAtMs;
        }

        public int StatId { get; }

        public int Flat { get; }

        /// <summary>Whole percents; negative values lower the stat.</summary>
        public int Percent { get; }

        public StatSourceType SourceType { get; }

        /// <summary>Item instance, effect, skill or other id of the source; together with the type it names the source.</summary>
        public long SourceId { get; }

        public long ExpiresAtMs { get; }

        public bool Expires => ExpiresAtMs != 0;
    }
}
