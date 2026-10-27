namespace Shared.Simulation.Items
{
    /// <summary>
    /// One stat bonus of an equipped item (spec 4.1 M2 item model; interface of WP-M2-1 to the stats of WP-M2-2): a
    /// <c>StatDef</c> id, a flat value in the stat's units and whole percents of the stat. Read through
    /// <c>IEquipmentStatSource</c> it is already scaled by the instance's rarity (<see cref="Scale"/>).
    /// </summary>
    public readonly struct ItemStatBonus
    {
        public ItemStatBonus(string statId, int flat, int percent)
        {
            StatId = statId;
            Flat = flat;
            Percent = percent;
        }

        public string StatId { get; }
        public int Flat { get; }
        public int Percent { get; }

        /// <summary>
        /// The bonus of an instance whose rarity has <paramref name="statPercent"/> (<c>RarityDef.statPercent</c>): both
        /// values times the percent / 100, integer math truncated toward zero (architecture rule 6).
        /// </summary>
        public ItemStatBonus Scale(int statPercent) =>
            new ItemStatBonus(StatId, (int)((long)Flat * statPercent / 100), (int)((long)Percent * statPercent / 100));

        public override string ToString() => $"{StatId} {Flat:+0;-0;0} {Percent:+0;-0;0}%";
    }
}
