using GameServer.Bystial;
using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Progression;
using GameServer.Skills;
using Shared.Data.Tables;
using Shared.Simulation.Items;
using Shared.Simulation.Stats;

namespace GameServer.Stats;

/// <summary>
/// The stat sources of the world's characters (spec 3.4, WP-M2-2), besides statuses (the <c>StatusSystem</c>, spec 3.7):
/// the class and the level set the attributes (the <see cref="ProgressionStore"/>, set again on <see cref="LevelUp"/>), the
/// equipped gear adds modifiers of source type gear (<see cref="IEquipmentStatSource"/>, the inventory's committed
/// equipment, rebuilt on <see cref="EquipmentChanged"/>). In phase 10 it publishes <see cref="StatsChanged"/> for every
/// character whose final stats changed since the last one, whatever changed them, and marks its own-stats message due. A
/// stat block recomputes only when a source changed, so the check costs a comparison per player otherwise. World thread only.
/// </summary>
internal sealed class StatsService : IEventConsumer<EquipmentChanged>, IEventConsumer<LevelUp>, IEventConsumer<SkillLearned>,
    IEventConsumer<SkillEvolved>, IEventConsumer<Respecced>, IEventConsumer<ClassAdvanced>, IEventConsumer<BookChanged>,
    IEventConsumer<AttributesChanged>
{
    private readonly ProgressionStore? _progression;
    private readonly IEquipmentStatSource? _equipment;
    private readonly BystialService? _book;
    private readonly EventBus? _events;
    private readonly Func<long, Player?> _byCharacter;
    private readonly Func<long> _nowMs;
    private CombatRules _combat;
    private readonly List<SkillPassive> _passives = new();

    /// <param name="progression">Character levels; without it every character is level 1.</param>
    /// <param name="equipment">Equipped gear bonuses; without it there is no gear.</param>
    /// <param name="nowMs">The world's simulation time (the resource meters' clock).</param>
    /// <param name="book">The Bystial book (WP-M2-8): placed cards and family set bonuses; without it there is none.</param>
    public StatsService(CombatRules combat, ProgressionStore? progression, IEquipmentStatSource? equipment, EventBus? events,
        Func<long, Player?> byCharacter, Func<long> nowMs, BystialService? book = null)
    {
        _book = book;
        _combat = combat;
        _progression = progression;
        _equipment = equipment;
        _events = events;
        _byCharacter = byCharacter;
        _nowMs = nowMs;
    }

    /// <summary>Gear bonuses naming a stat <c>StatDef</c> does not have (skipped; the item tables are validated against it).</summary>
    public long UnknownGearStats { get; private set; }

    /// <summary>StatsChanged events published.</summary>
    public long Published { get; private set; }

    /// <summary>
    /// The skill tree (WP-M2-5): the committed class (set again on <see cref="ClassAdvanced"/>), the learned passives' modifiers
    /// (source type skill, rebuilt on every skill event and on <see cref="EquipmentChanged"/> for the weapon triggers) and the
    /// armor tier (R-3.5-08). Null: no skill tree, the class and tier the combatant was built with.
    /// </summary>
    public ISkillStatSource? Skills { get; set; }

    public void Subscribe(EventBus events)
    {
        events.Subscribe<EquipmentChanged>(this);
        events.Subscribe<LevelUp>(this);
        events.Subscribe<SkillLearned>(this);
        events.Subscribe<SkillEvolved>(this);
        events.Subscribe<Respecced>(this);
        events.Subscribe<ClassAdvanced>(this);
        events.Subscribe<AttributesChanged>(this);
        if (_book != null)
            events.Subscribe<BookChanged>(this);
    }

    /// <summary>Dev hot reload (spec 11.7): later builds and level changes use the new class rows.</summary>
    public void Retune(CombatRules combat) => _combat = combat;

    /// <summary>A player entered the world with its combatant (built at its level): its gear, book and placed attribute points go in.</summary>
    public void Attach(Player player)
    {
        if (player.Combatant == null)
            return;
        RebuildGear(player);
        RebuildSkills(player);
        RebuildBook(player);
        RebuildAttributes(player);
    }

    /// <summary>A placement or a respec's reset committed (R-3.4-08): the placed points again, from the store (idempotent).</summary>
    public void Handle(in EventEnvelope envelope, in AttributesChanged payload)
    {
        if (_byCharacter(payload.CharacterId) is { Combatant: not null } player)
            RebuildAttributes(player);
    }

    /// <summary>
    /// The free attribute points again (R-3.4-08, WP-M2-13; source type <c>Attribute</c>, source id 0): one flat modifier per
    /// attribute with committed placed points, which the derived stats of <c>AttributeFormula</c> read like the class growth
    /// (the block computes the attributes with their flat modifiers first). Modifiers, not the base, because a level-up or a
    /// class advance sets the base again. Rebuilt whole from the store, so a repeated or late event ends in the same state.
    /// </summary>
    internal void RebuildAttributes(Player player)
    {
        var combatant = player.Combatant!;
        var stats = combatant.Stats;
        if (_progression == null || stats.Formula is not { } formula || !_progression.TryGetAttributes(player.CharacterId, out var placed))
            return;
        long now = _nowMs();
        combatant.Meters.Settle(now);
        stats.RemoveSourceType(StatSourceType.Attribute);
        for (int attribute = 0; attribute < Attributes.Count; attribute++)
        {
            int points = placed.Placed(attribute);
            if (points > 0)
                stats.Add(new StatModifier(formula.AttributeStat(attribute), points, 0, StatSourceType.Attribute, 0));
        }
        combatant.Meters.Settle(now);
    }

    public void Handle(in EventEnvelope envelope, in EquipmentChanged payload)
    {
        if (_byCharacter(payload.CharacterId) is not { Combatant: not null } player)
            return;
        RebuildGear(player);
        RebuildSkills(player); // the weapon triggers of the passives (Knight's Oath with a shield)
    }

    public void Handle(in EventEnvelope envelope, in SkillLearned payload) => SkillsChanged(payload.CharacterId);

    public void Handle(in EventEnvelope envelope, in SkillEvolved payload) => SkillsChanged(payload.CharacterId);

    public void Handle(in EventEnvelope envelope, in Respecced payload) => SkillsChanged(payload.CharacterId);

    /// <summary>
    /// R-3.5-01 class advance (spec 3.5 flow "recompute stats"): the class bases and attributes of the committed class at the
    /// current level, then the passives and the armor tier. Read from the sources, not the event, so it is idempotent.
    /// </summary>
    public void Handle(in EventEnvelope envelope, in ClassAdvanced payload) => SkillsChanged(payload.CharacterId);

    private void SkillsChanged(long characterId)
    {
        if (_byCharacter(characterId) is not { Combatant: { } combatant } player)
            return;
        if (Skills?.ClassOf(characterId) is { } classId && classId != combatant.TemplateId && _combat.TryGetClass(classId, out var classDef))
            _combat.SetClass(combatant, classDef, LevelOf(characterId), _nowMs());
        RebuildSkills(player);
    }

    /// <summary>
    /// Every passive modifier again (source type skill, the skill tree; spec 3.5): each learned passive's rows times its
    /// level while their trigger holds for the committed equipped weapons, and the armor tier (R-3.5-08). Rebuilt whole, so
    /// a repeated event ends in the same state (rule 3).
    /// </summary>
    internal void RebuildSkills(Player player)
    {
        if (Skills == null)
            return;
        var combatant = player.Combatant!;
        var stats = combatant.Stats;
        long now = _nowMs();
        combatant.Meters.Settle(now);
        stats.RemoveSourceType(StatSourceType.Skill);
        _passives.Clear();
        if (Skills.GetPassives(player.CharacterId, _passives))
        {
            uint weapons = _equipment != null ? WeaponMasks.Of(_equipment.GetEquippedWeaponTypes(player.CharacterId)) : 0;
            foreach (var passive in _passives)
            {
                var modifier = passive.Modifier;
                if (!modifier.Applies(weapons) || !stats.Rules.TryGetId(modifier.Stat, out int statId))
                    continue;
                stats.Add(new StatModifier(statId, modifier.FlatPerLevel * passive.Level, modifier.PercentPerLevel * passive.Level,
                    StatSourceType.Skill, 0));
            }
        }
        if (Skills.EffectiveArmor(player.CharacterId) is { } armor)
            combatant.ArmorClass = armor;
        combatant.Meters.Settle(now);
    }

    /// <summary>WP-M2-8: the committed book changed (a card placed, swapped, taken out or evolved): its whole source again.</summary>
    public void Handle(in EventEnvelope envelope, in BookChanged payload)
    {
        if (_byCharacter(payload.CharacterId) is { Combatant: not null } player)
            RebuildBook(player);
    }

    /// <summary>
    /// Every Bystial modifier again from the committed book (spec 14.2: source type <c>bystialCard</c>, source id 0): the
    /// placed cards' and family set bonuses' stat bonuses and resistances, already capped by <c>BystialMath.Aggregate</c>.
    /// The whole set is rebuilt, so a repeated or late <see cref="BookChanged"/> ends in the same state (rule 3).
    /// TODO(M5): arena stat blocks come from class templates (spec 6.6) and never call this.
    /// </summary>
    internal void RebuildBook(Player player)
    {
        if (_book == null)
            return;
        var combatant = player.Combatant!;
        var stats = combatant.Stats;
        long now = _nowMs();
        combatant.Meters.Settle(now);
        stats.RemoveSourceType(StatSourceType.BystialCard);
        foreach (var bonus in _book.BonusesOf(player.CharacterId).Stats)
        {
            if ((uint)bonus.StatId < (uint)stats.Rules.Count)
                stats.Add(new StatModifier(bonus.StatId, bonus.Flat, bonus.Percent, StatSourceType.BystialCard, 0));
        }
        combatant.Meters.Settle(now);
    }

    /// <summary>
    /// WP-M2-3's level-up: the attributes are set again from the store's current level (not the event's, so a repeated or
    /// late event ends in the same state, rule 3). Unknown characters are ignored.
    /// </summary>
    public void Handle(in EventEnvelope envelope, in LevelUp payload) => LevelChanged(payload.CharacterId);

    /// <summary>The level of <paramref name="characterId"/> changed (a level-up, later a class change): its attributes again.</summary>
    internal void LevelChanged(long characterId)
    {
        if (_byCharacter(characterId) is not { Combatant: { } combatant })
            return;
        if (!_combat.TryGetClass(combatant.TemplateId, out var classDef))
            return;
        _combat.SetLevel(combatant, classDef, LevelOf(characterId), _nowMs());
    }

    private int LevelOf(long characterId) => Math.Max(1, _progression?.GetLevel(characterId) ?? 1);

    /// <summary>
    /// Every gear modifier again from the equipped items (spec 3.4: source type gear). The whole set is rebuilt, so a
    /// repeated or out-of-order <see cref="EquipmentChanged"/> ends in the same state (consumers are idempotent, rule 3).
    /// The bonuses are one entry per item and stat, not per slot, so every gear modifier has source id 0; nothing removes a
    /// single slot's modifiers.
    /// </summary>
    internal void RebuildGear(Player player)
    {
        var combatant = player.Combatant!;
        var stats = combatant.Stats;
        long now = _nowMs();
        combatant.Meters.Settle(now);
        stats.RemoveSourceType(StatSourceType.Gear);
        stats.RemoveSourceType(StatSourceType.Enchant);
        stats.RemoveSourceType(StatSourceType.Gem);
        if (_equipment != null)
        {
            AddGear(stats, _equipment.GetEquippedBonuses(player.CharacterId), StatSourceType.Gear);
            // Spec 3.4 / 5.3 M3 decision 10 (WP-M3-8): the enchant lines are their own source, so the arena can mask them.
            AddGear(stats, _equipment.GetEnchantBonuses(player.CharacterId), StatSourceType.Enchant);
            // The socketed gems (R-5.3-05, WP-M3-9): their GemDef rows, read at stat time, as their own source type.
            AddGear(stats, _equipment.GetEquippedGemBonuses(player.CharacterId), StatSourceType.Gem);
        }
        combatant.Meters.Settle(now);
    }

    private void AddGear(StatBlock stats, IReadOnlyList<ItemStatBonus> bonuses, StatSourceType source)
    {
        for (int i = 0; i < bonuses.Count; i++)
        {
            var bonus = bonuses[i];
            if (!stats.Rules.TryGetId(bonus.StatId, out int statId))
            {
                UnknownGearStats++;
                continue;
            }
            stats.Add(new StatModifier(statId, bonus.Flat, bonus.Percent, source, 0));
        }
    }

    /// <summary>
    /// Phase 10, before the sends: a <see cref="StatsChanged"/> for each player whose final stats changed (or that just
    /// entered the world), in entity-id order, and its stats message marked due.
    /// </summary>
    public void PublishChanges(List<Entity> entities)
    {
        foreach (var entity in entities) // List's struct enumerator: no allocation per tick
        {
            if (entity is not Player { Combatant: { } combatant } player)
                continue;
            var stats = combatant.Stats;
            stats.Refresh();
            if (stats.Version == player.PublishedStatsVersion)
                continue;
            player.PublishedStatsVersion = stats.Version;
            player.StatsMessageDue = true;
            _events?.Publish(new StatsChanged(player.CharacterId, player.Id, stats.Version), player.CharacterId);
            Published++;
        }
    }
}
