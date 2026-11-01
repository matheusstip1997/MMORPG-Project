using Game.Gameplay.Entities;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Progression;
using GameServer.Stats;
using GameServer.Tests.Support;
using Shared.Simulation.Items;
using Shared.Simulation.Stats;

namespace GameServer.Tests.World;

/// <summary>
/// Stats v2 in the world (spec 3.4, 8.1, 9.4; WP-M2-2): the attributes from the class and the level (the progression store,
/// set again on <see cref="LevelUp"/>), gear modifiers from the inventory's committed equipment (WP-M2-1's
/// <see cref="IEquipmentStatSource"/>, rebuilt on <see cref="EquipmentChanged"/>), <see cref="StatsChanged"/> once per change
/// in phase 10, and the own-stats message read by the client core's <see cref="ClientStats"/> (the stats sync).
/// </summary>
public class StatsWorldTests
{
    private const byte Safe = 2, Equipped = 5, Main = 0;
    private const string Sword = "m2.trainingSword"; // ItemStatBonusDef: atk +10 (placeholder)

    private sealed class StubEquipment : IEquipmentStatSource
    {
        public List<ItemStatBonus> Bonuses { get; } = new();

        public IReadOnlyList<ItemStatBonus> GetEquippedBonuses(long characterId) => Bonuses;

        /// <summary>The enchant lines (source Enchant, WP-M3-8).</summary>
        public List<ItemStatBonus> EnchantBonuses { get; } = new();

        public IReadOnlyList<ItemStatBonus> GetEnchantBonuses(long characterId) => EnchantBonuses;

        public IReadOnlyList<Shared.Data.Tables.WeaponType> GetEquippedWeaponTypes(long characterId) =>
            Array.Empty<Shared.Data.Tables.WeaponType>();
    }

    private sealed class StatsLog : IEventConsumer<StatsChanged>
    {
        public List<StatsChanged> Events { get; } = new();

        public void Handle(in EventEnvelope envelope, in StatsChanged payload) => Events.Add(payload);
    }

    private sealed class Setup
    {
        public Setup()
        {
            var tables = TestCombat.Tables();
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe(Log);
            Harness = new WorldHarness(events: new EventDispatchHook(Bus), bus: Bus, combat: TestCombat.Rules(tables),
                items: ItemRules.From(tables), progression: ProgressionRules.From(tables));
            Client = new ClientStats(tables);
        }

        public EventBus Bus { get; }
        public StatsLog Log { get; } = new();
        public WorldHarness Harness { get; }
        public ClientStats Client { get; }

        /// <summary>The next class 2 batch (every second) and its completion: equipment changes commit (rule 5).</summary>
        public void Save()
        {
            Harness.Clock.Advance(1_000);
            Harness.Ticks(3); // the save commits, EquipmentChanged is dispatched (phase 11), the next phase 10 publishes
        }

        /// <summary>A placeholder gear instance created by the character with id 1 (its first ids).</summary>
        public static GameServer.Persistence.ItemInstanceRow Gear(long seq, byte container, int slot, string item) =>
            new(ItemInstanceIds.Make(1, seq), container, slot, item, 0, false, false, ItemInstanceIds.NewProperties(1));

        public int Stat(string id) => Harness.World.Combat!.Config.Stats.IdFor(id);

        public int Server(Player player, string id) => player.Combatant!.Stats.Get(Stat(id));

        /// <summary>Feeds every own-stats message the peer got to the client core, in order; returns how many.</summary>
        public int SyncClient(int peerId, ref int seen)
        {
            var messages = Harness.Transport.OwnStatsFor(peerId);
            for (int i = seen; i < messages.Count; i++)
                Assert.True(Client.Apply(messages[i], nowMs: 0));
            int fresh = messages.Count - seen;
            seen = messages.Count;
            return fresh;
        }
    }

    [Fact]
    public void Entering_the_world_publishes_StatsChanged_once_and_sends_the_level_1_stats()
    {
        var s = new Setup();
        var player = s.Harness.Join(1);
        s.Harness.Ticks(3);

        var changed = Assert.Single(s.Log.Events);
        Assert.Equal(player.Id, changed.EntityId);
        Assert.Equal(player.Combatant!.Stats.Version, changed.Version);
        int seen = 0;
        Assert.Equal(1, s.SyncClient(1, ref seen));
        Assert.Equal(0, s.Client.Dropped);
        Assert.Equal(s.Server(player, StatIds.Atk), s.Client.Get(StatIds.Atk));
        Assert.Equal(s.Server(player, StatIds.Str), s.Client.Get(StatIds.Str));
        Assert.True(s.Client.Get(StatIds.Str) > 0); // the class's level-1 STR
        Assert.Equal(0, s.Client.Toxicity(0));
        Assert.Equal(0, s.Client.Satiety(0));
    }

    [Fact]
    public void Equipping_gear_changes_Atk_on_the_server_and_the_client_once_the_save_committed()
    {
        var s = new Setup();
        var record = s.Harness.Characters.Add(1, 0, new GameServer.Persistence.CharacterState(1, 0, "", 0, 0, 0, 0, 100, 50, 100));
        s.Harness.Characters.Instances[record.CharacterId] = new[] { Setup.Gear(1, Safe, 0, Sword) };
        var player = s.Harness.Join(1);
        s.Harness.Ticks(1);
        int seen = 0;
        s.SyncClient(1, ref seen);
        int atkBefore = s.Server(player, StatIds.Atk);
        int events = s.Log.Events.Count;

        // Equipped through the bag move (WP-M2-1); the stats see only the committed set (rule 5).
        s.Harness.SendMove(1, requestId: 1, Safe, 0, Equipped, Main);
        s.Harness.Ticks(2);
        Assert.Equal(atkBefore, s.Server(player, StatIds.Atk));
        Assert.Empty(s.Log.Events.Skip(events));

        s.Save();

        Assert.Equal(atkBefore + 10, s.Server(player, StatIds.Atk));
        var changed = Assert.Single(s.Log.Events.Skip(events));
        Assert.Equal(player.Combatant!.Stats.Version, changed.Version);
        Assert.Equal(1, s.SyncClient(1, ref seen));
        Assert.Equal(atkBefore + 10, s.Client.Get(StatIds.Atk));

        // The same event again (a repeat) changes nothing and sends nothing: the gear is rebuilt from the source (rule 3).
        s.Bus.Publish(new EquipmentChanged(player.CharacterId, Main), player.CharacterId);
        s.Harness.Ticks(2);
        Assert.Equal(atkBefore + 10, s.Server(player, StatIds.Atk));
        Assert.Single(s.Log.Events.Skip(events));
        Assert.Equal(0, s.SyncClient(1, ref seen));

        // Unequipped: back to the class-and-level value.
        s.Harness.SendMove(1, requestId: 2, Equipped, Main, Safe, 0);
        s.Harness.Ticks(1);
        s.Save();
        Assert.Equal(atkBefore, s.Server(player, StatIds.Atk));
        Assert.Equal(1, s.SyncClient(1, ref seen));
        Assert.Equal(atkBefore, s.Client.Get(StatIds.Atk));
    }

    [Fact]
    public void Gear_bonuses_on_an_attribute_raise_the_derived_stats_through_the_formula()
    {
        // ItemStatBonusDef takes any StatDef id, the attributes included (spec 3.4); no shipped item has one yet.
        var s = new Setup();
        var player = s.Harness.Join(1);
        s.Harness.Ticks(1);
        var equipment = new StubEquipment();
        var stats = new StatsService(s.Harness.World.Combat!, null, equipment, null, _ => player, () => s.Harness.World.NowMs);
        int atkBefore = s.Server(player, StatIds.Atk);
        int strBefore = s.Server(player, StatIds.Str);

        equipment.Bonuses.Add(new ItemStatBonus(StatIds.Atk, 12, 0));
        equipment.Bonuses.Add(new ItemStatBonus(StatIds.Str, 4, 0));
        stats.RebuildGear(player);

        // STR +4 adds floor(4 x 100 / 100) = 4 more Atk through the formula (AttributeFormula atk str 100).
        Assert.Equal(strBefore + 4, s.Server(player, StatIds.Str));
        Assert.Equal(atkBefore + 12 + 4, s.Server(player, StatIds.Atk));
    }

    [Fact]
    public void A_level_up_sets_the_attributes_again_and_raises_the_derived_stats()
    {
        var s = new Setup();
        var player = s.Harness.Join(1);
        s.Harness.Ticks(1);
        int events = s.Log.Events.Count;
        Assert.True(s.Harness.World.Combat!.TryGetClass(player.Combatant!.TemplateId, out var classDef));
        int atkBefore = s.Server(player, StatIds.Atk);
        int maxHpBefore = s.Server(player, StatIds.MaxHp);

        // WP-M2-3: 172 + 276 exp is level 3 (placeholder curve); LevelUp is dispatched in phase 11.
        Assert.Equal(2, s.Harness.World.Exp!.Grant(player.CharacterId, player.Id, 172 + 276));
        s.Harness.Ticks(2);

        Assert.Equal(3, s.Harness.World.Progression!.GetLevel(player.CharacterId));
        for (int attribute = 0; attribute < Attributes.Count; attribute++)
            Assert.Equal(Attributes.Of(classDef, attribute, 3), s.Server(player, StatIds.Attributes[attribute]));
        Assert.True(s.Server(player, StatIds.Atk) > atkBefore);
        Assert.True(s.Server(player, StatIds.MaxHp) > maxHpBefore);
        Assert.Single(s.Log.Events.Skip(events));
        Assert.Equal(s.Server(player, StatIds.MaxHp), player.Combatant.Meters.Max(Shared.Simulation.Stats.ResourceKind.Hp, s.Harness.World.NowMs));

        // A repeated LevelUp reads the store's level again: nothing changes.
        s.Bus.Publish(new LevelUp(player.CharacterId, player.Id, 1, 3), player.CharacterId);
        s.Harness.Ticks(2);
        Assert.Single(s.Log.Events.Skip(events));
    }

    [Fact]
    public void A_character_logs_in_at_its_saved_level()
    {
        var s = new Setup();
        s.Harness.Characters.Add(1, 0, new GameServer.Persistence.CharacterState(15, 0, "", 0, 0, 0, 0, 100, 50, 100), classId: "mage");
        var player = s.Harness.Join(1);
        Assert.True(s.Harness.World.Combat!.TryGetClass("mage", out var mage));

        for (int attribute = 0; attribute < Attributes.Count; attribute++)
            Assert.Equal(Attributes.Of(mage, attribute, 15), s.Server(player, StatIds.Attributes[attribute]));
    }

    [Fact]
    public void A_status_changes_Def_and_is_published_once_when_it_starts_and_once_when_it_ends()
    {
        var s = new Setup();
        var player = s.Harness.Join(1);
        s.Harness.Ticks(1);
        int events = s.Log.Events.Count;
        int defBefore = s.Server(player, StatIds.Def);
        var world = s.Harness.World;
        Assert.True(world.Combat!.Statuses.TryGet("weakened", out var weakened)); // Def -20%, 8 s (spec 3.7)

        world.Statuses!.Apply(player, player, weakened, world.ServerTick);
        s.Harness.Ticks(1);
        Assert.Equal(defBefore * 80 / 100, s.Server(player, StatIds.Def));
        Assert.Single(s.Log.Events.Skip(events));

        s.Harness.Ticks(world.Combat.TicksFor(8_000) + 2);
        Assert.Equal(defBefore, s.Server(player, StatIds.Def));
        Assert.Equal(2, s.Log.Events.Skip(events).Count());
    }

    [Fact]
    public void Nothing_is_published_or_sent_while_nothing_changes()
    {
        var s = new Setup();
        s.Harness.Join(1);
        s.Harness.Ticks(1);
        int events = s.Log.Events.Count;
        long sent = s.Harness.World.StatsMessagesSent;

        s.Harness.Ticks(40);

        Assert.Equal(events, s.Log.Events.Count);
        Assert.Equal(sent, s.Harness.World.StatsMessagesSent);
    }

    [Fact]
    public void A_gear_bonus_on_a_stat_StatDef_lacks_is_skipped_and_counted()
    {
        // The item tables are validated against StatDef, so only a source out of step with the tables could do this.
        var s = new Setup();
        var player = s.Harness.Join(1);
        var equipment = new StubEquipment();
        var stats = new StatsService(s.Harness.World.Combat!, null, equipment, null, _ => player, () => s.Harness.World.NowMs);
        equipment.Bonuses.Add(new ItemStatBonus("luck", 50, 0)); // no item find/luck (R-3.4-02)
        stats.RebuildGear(player);
        Assert.Equal(1, stats.UnknownGearStats);
    }

    [Fact]
    public void Saved_toxicity_and_satiety_decay_across_the_logout_and_a_save_writes_them_at_the_utc_time()
    {
        // R-3.4-03 "computed from timestamps on login". ConsumptionMeterSettings placeholders: 10 and 5 points a minute.
        var s = new Setup();
        long now = s.Harness.Clock.UtcNowMs;
        var saved = new GameServer.Persistence.CharacterState(1, 0, GameServer.Sessions.SessionManager.MainWorldKey, 0, 0, 0, 0,
            Hp: 100, Mp: 10, Stamina: 50, ToxicityMilli: 40_000, SatietyMilli: 30_000, ConsumptionAtUtcMs: now - 3 * 60_000);
        var record = s.Harness.Characters.Add(5, 0, saved, classId: "mage");
        var player = s.Harness.Join(peerId: 1, accountId: 5);

        Assert.Equal(10, player.Consumption!.Toxicity(now)); // 40 - 3 minutes x 10
        Assert.Equal(15, player.Consumption.Satiety(now));   // 30 - 3 minutes x 5
        int seen = 0;
        s.SyncClient(1, ref seen);
        Assert.Equal(10, s.Client.Toxicity(0));
        Assert.Equal(15, s.Client.Satiety(0));
        Assert.Equal(s.Client.ToxicityMax, player.Consumption.Settings.ToxicityMax);

        s.Harness.Clock.Advance(30_000);
        var state = s.Harness.Sessions.Capture(record.CharacterId);
        Assert.Equal(5_000, state.ToxicityMilli);
        Assert.Equal(12_500, state.SatietyMilli);
        Assert.Equal(s.Harness.Clock.UtcNowMs, state.ConsumptionAtUtcMs);
    }
}
