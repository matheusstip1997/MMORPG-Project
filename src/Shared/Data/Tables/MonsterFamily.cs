namespace Shared.Data.Tables
{
    /// <summary>
    /// The family a monster type belongs to (R-14.2-05; user decision 2026-10-27, placeholders): the Bystial book's family
    /// set bonuses and its effects against a family read it from <see cref="MonsterDef.Family"/>. The M2 world uses demon,
    /// beast and aberration. Config enum (camelCase names); append only, never renumber.
    /// </summary>
    public enum MonsterFamily : byte
    {
        /// <summary>No family (the default of rows that predate the field; a monster with Bystial cards needs one).</summary>
        None = 0,
        Demon = 1,
        Beast = 2,
        Aberration = 3,
        Undead = 4,
        Humanoid = 5,
        Elemental = 6,
        Dragon = 7,
        Plant = 8,
    }

    public static class MonsterFamilies
    {
        /// <summary>Highest family value; the families are 1..Last.</summary>
        public const MonsterFamily Last = MonsterFamily.Plant;

        /// <summary>Length of an array indexed by family value, <see cref="MonsterFamily.None"/> included.</summary>
        public const int ArrayLength = (int)Last + 1;
    }

    /// <summary>
    /// A Bystial card's tier (R-14.2-02; user decision 2026-10-27): normal, rare, epic, legendary, earned at the kill
    /// thresholds of <see cref="BystialCardDef"/>. Stored in <c>bystial_book.tier</c> and sent in <c>BookState</c>. Config enum;
    /// append only, never renumber.
    /// </summary>
    public enum BystialTier : byte
    {
        /// <summary>No card yet.</summary>
        None = 0,
        Normal = 1,
        Rare = 2,
        Epic = 3,
        Legendary = 4,
    }

    public static class BystialTiers
    {
        public const BystialTier Last = BystialTier.Legendary;

        /// <summary>Number of tiers, without <see cref="BystialTier.None"/>.</summary>
        public const int Count = (int)Last;
    }

    /// <summary>
    /// The themed effect of a card tier or a family set bonus (R-14.2-05; user decision 2026-10-27: a fixed list). Every
    /// kind's sum is capped by its <see cref="BystialEffectCap"/> row (architecture rule 2). Config enum; append only.
    /// </summary>
    public enum BystialEffectKind : byte
    {
        /// <summary>No effect: the row gives only its stat bonus.</summary>
        None = 0,
        /// <summary>Whole percents on the resistance stat of <c>effectElement</c> (its <see cref="ElementCaps"/> row).</summary>
        ElementResistance = 1,
        /// <summary>Whole percents less gain on every build-up meter (the <c>buildUpResistPercent</c> stat, spec 3.8).</summary>
        BuildUpResistance = 2,
        /// <summary>Whole percents more damage dealt to monsters of the card's (or the set bonus's) family.</summary>
        FamilyDamage = 3,
        /// <summary>Whole percents more exp from kills of monsters of the card's (or the set bonus's) family.</summary>
        FamilyExp = 4,
    }

    public static class BystialEffectKinds
    {
        public const BystialEffectKind Last = BystialEffectKind.FamilyExp;

        /// <summary>Length of an array indexed by kind value, <see cref="BystialEffectKind.None"/> included.</summary>
        public const int ArrayLength = (int)Last + 1;
    }
}
