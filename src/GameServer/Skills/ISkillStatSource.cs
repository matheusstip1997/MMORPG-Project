using Shared.Data.Tables;

namespace GameServer.Skills;

/// <summary>A learned passive's modifier and the level it is learned at (the modifier is per level, user decision 2026-10-27).</summary>
public readonly record struct SkillPassive(SkillModifierDef Modifier, int Level);

/// <summary>
/// What the skill tree gives a character's stats (spec 3.5 Implementation, R-3.5-03/08; WP-M2-5), read by
/// <c>Stats/StatsService</c> like the gear source: the visible (committed) class, the learned passives' modifiers and the
/// armor tier. Implemented by <see cref="SkillTreeService"/>; world thread only.
/// </summary>
public interface ISkillStatSource
{
    /// <summary>The character's class as committed; null when its skills are not held.</summary>
    string? ClassOf(long characterId);

    /// <summary>Adds the learned passives' modifiers to <paramref name="into"/>; false when the skills are not held.</summary>
    bool GetPassives(long characterId, List<SkillPassive> into);

    /// <summary>R-3.5-08: the class's tier, or the latest learned armor passive's; null when the skills are not held.</summary>
    ArmorClass? EffectiveArmor(long characterId);
}
