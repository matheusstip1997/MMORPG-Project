using System;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace Shared.Simulation.Progression
{
    /// <summary>
    /// The free attribute points of R-3.4-08 (WP-M2-13, user decisions 2026-10-29): every level from 2 gives
    /// <c>ProgressionSettings.attributePointsPerLevel</c> unspent points, placed one at a time on STR, DEX, STA or INT, out of
    /// combat and not during an action, with no cap per attribute beyond the attribute's <c>StatDef</c> max. Pure integer
    /// math shared by the server (the checks of <c>ProgressionStore</c>), client previews and the bots.
    /// </summary>
    public static class AttributePoints
    {
        /// <summary>Points a character has earned at <paramref name="level"/> (none at level 1).</summary>
        public static int Earned(int level, int perLevel)
        {
            long earned = (long)Math.Max(0, level - 1) * Math.Max(0, perLevel);
            return (int)Math.Min(int.MaxValue, earned);
        }

        /// <summary>
        /// Why placing one point on <paramref name="attribute"/> is refused, or <see cref="AttributeReason.None"/>. In this
        /// order: the progression is not held (<paramref name="loaded"/>), the attribute is unknown, the character is in
        /// combat or acting (<paramref name="blocked"/>), no unspent point, or <paramref name="baseWithPlaced"/> (class base and
        /// growth plus the points already placed on it) is at <paramref name="statMax"/>.
        /// </summary>
        public static AttributeReason Check(bool loaded, int attribute, bool blocked, int unspent, long baseWithPlaced, int statMax)
        {
            if (!loaded)
                return AttributeReason.AttributeUnavailable;
            if (attribute < 0 || attribute >= Attributes.Count)
                return AttributeReason.UnknownAttribute;
            if (blocked)
                return AttributeReason.AttributeBlockedCombat;
            if (unspent <= 0)
                return AttributeReason.NoAttributePoints;
            if (baseWithPlaced >= statMax)
                return AttributeReason.AttributeAtMax;
            return AttributeReason.None;
        }
    }
}
