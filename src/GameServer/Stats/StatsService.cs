using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Progression;
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
internal sealed class StatsService : IEventConsumer<EquipmentChanged>, IEventConsumer<LevelUp>
{
    private readonly ProgressionStore? _progression;
    private readonly IEquipmentStatSource? _equipment;
    private readonly EventBus? _events;
    private readonly Func<long, Player?> _byCharacter;
    private readonly Func<long> _nowMs;
    private CombatRules _combat;

    /// <param name="progression">Character levels; without it every character is level 1.</param>
    /// <param name="equipment">Equipped gear bonuses; without it there is no gear.</param>
    /// <param name="nowMs">The world's simulation time (the resource meters' clock).</param>
    public StatsService(CombatRules combat, ProgressionStore? progression, IEquipmentStatSource? equipment, EventBus? events,
        Func<long, Player?> byCharacter, Func<long> nowMs)
    {
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

    public void Subscribe(EventBus events)
    {
        events.Subscribe<EquipmentChanged>(this);
        events.Subscribe<LevelUp>(this);
    }

    /// <summary>Dev hot reload (spec 11.7): later builds and level changes use the new class rows.</summary>
    public void Retune(CombatRules combat) => _combat = combat;

    /// <summary>A player entered the world with its combatant (built at its level): its gear bonuses go in.</summary>
    public void Attach(Player player)
    {
        if (player.Combatant != null)
            RebuildGear(player);
    }

    public void Handle(in EventEnvelope envelope, in EquipmentChanged payload)
    {
        if (_byCharacter(payload.CharacterId) is { Combatant: not null } player)
            RebuildGear(player);
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
        if (_equipment != null)
        {
            var bonuses = _equipment.GetEquippedBonuses(player.CharacterId);
            for (int i = 0; i < bonuses.Count; i++)
            {
                var bonus = bonuses[i];
                if (!stats.Rules.TryGetId(bonus.StatId, out int statId))
                {
                    UnknownGearStats++;
                    continue;
                }
                stats.Add(new StatModifier(statId, bonus.Flat, bonus.Percent, StatSourceType.Gear, 0));
            }
        }
        combatant.Meters.Settle(now);
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
