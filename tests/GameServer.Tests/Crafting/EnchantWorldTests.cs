using GameServer.Crafting;
using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Tests.Economy;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;

namespace GameServer.Tests.Crafting;

/// <summary>
/// Enchanting in the world (spec 5.3 R-5.3-04, 9.13; WP-M3-8) with the repository tables: every request runs the station
/// gate (reach, the item job's station, combat), an attempt is answered and published (<see cref="Enchanted"/>) only after
/// its save committed (rule 5), the contents sent before the commit still show the old level, and the done check: a
/// Blacksmith enchants its equipped forged sword at the forge, the stone is gone and the own atk follows the level.
/// </summary>
public class EnchantWorldTests
{
    private const string Forge = "test.npc.forge";
    private const string Bench = "test.npc.bench";
    private const string FarForge = "test.npc.forge.far";
    private const byte Normal = 1, Equipped = 5;
    private static readonly long SwordId = ItemInstanceIds.Make(1, 1);

    private sealed class Fixture
    {
        public Fixture()
        {
            var tables = TestCombat.Tables();
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<Enchanted>(Log);
            var map = TestMaps.Map(TestMaps.Open().Grid,
            [
                TestMaps.SpawnMarker(0, 0), TestCombat.MonsterMarker(4_000, 0), EconomyFixture.Npc(Forge, 200, 0, "m1.forge"),
                EconomyFixture.Npc(Bench, 0, 200, "m1.bench"), EconomyFixture.Npc(FarForge, 5_000, 5_000, "m1.forge"),
            ]);
            Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus,
                combat: TestCombat.Rules(tables, TestCombat.Point()), items: ItemRules.From(tables)!,
                economy: EconomyRules.From(tables)!, progression: ProgressionRules.From(tables)!, jobs: JobRules.From(tables)!,
                forge: ForgeRules.From(tables)!, enchant: EnchantRules.From(tables)!);
            var character = Harness.Characters.Add(1, 0, new CharacterState(12, 0, "", 0, 0, 0, 0, 100, 50, 100));
            Harness.Characters.Items[character.CharacterId] = [new ItemStackRow(Normal, 0, "m3.enchantStone", 0, false, 2)];
            var sword = ItemProperties.New(1);
            sword.CrafterId = 1;
            sword.Quality = 300;
            sword.CraftedStats.Add(new ItemStatBonus("atk", 20, 0));
            Harness.Characters.Instances[character.CharacterId] =
                [new ItemInstanceRow(SwordId, Equipped, 0, "m3.forgedSword", 0, false, false, sword.ToJson())];
            Harness.Characters.JobRecords[character.CharacterId] = new JobRecord(
                [new JobProgressRow("blacksmith", JobSlot.Main, 1, 0, 0, "{\"v\":1,\"recent\":[]}")]);
            Player = Harness.Join(1);
            Harness.Ticks(1);
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public EventLog Log { get; } = new();
        public Player Player { get; }
        public FakeTransport Transport => Harness.Transport;
        public long CharacterId => Player.CharacterId;

        public uint NpcId(string markerId) => Harness.World.Entities.OfType<Npc>().Single(n => n.MarkerId == markerId).Id;

        public void Enchant(uint requestId, string station = Forge)
        {
            Harness.SendEnchant(1, requestId, NpcId(station), Equipped, 0, SwordId);
            Harness.Ticks(1);
        }

        public int Stones => Harness.World.Inventory!.Current(CharacterId)[0].IsEmpty ? 0 : Harness.World.Inventory.Current(CharacterId)[0].Count;

        public int Stat(string id) => Player.Combatant!.Stats.Get(Harness.World.Combat!.Config.Stats.IdFor(id));
    }

    internal sealed class EventLog : IEventConsumer<Enchanted>
    {
        public List<Enchanted> Events { get; } = [];
        public void Handle(in EventEnvelope envelope, in Enchanted payload) => Events.Add(payload);
    }

    [Fact]
    public void An_attempt_runs_the_station_gate()
    {
        var f = new Fixture();
        f.Enchant(1, FarForge);
        Assert.Equal(EnchantReason.StationOutOfReach, f.Transport.EnchantResultsFor(1)[^1].Reason);
        f.Enchant(2, Bench); // the sword is the Blacksmith's: it enchants at the forge
        Assert.Equal(EnchantReason.WrongStation, f.Transport.EnchantResultsFor(1)[^1].Reason);
        Assert.Equal(2, f.Stones);
    }

    [Fact]
    public void An_attempt_in_combat_is_refused()
    {
        var f = new Fixture();
        f.Player.LastCombatAtMs = f.Harness.Clock.UtcNowMs;
        f.Enchant(1);
        Assert.Equal(EnchantReason.InCombat, f.Transport.EnchantResultsFor(1)[^1].Reason);
        Assert.Equal(2, f.Stones);
    }

    [Fact]
    public void A_blacksmith_enchants_its_equipped_sword_answered_after_the_commit_and_the_stats_follow()
    {
        var f = new Fixture();
        int atkBefore = f.Stat("atk");
        f.Harness.Saves.AutoComplete = false;

        f.Enchant(1);
        f.Harness.Ticks(1); // the save is issued; not completed

        Assert.Empty(f.Transport.EnchantResultsFor(1)); // rule 5: nothing answered before the commit
        Assert.Empty(f.Log.Events);
        Assert.Equal(atkBefore, f.Stat("atk"));
        var contents = f.Transport.InventoryFor(1);
        Assert.All(contents, c => Assert.All(c.Containers.Where(k => k.Code == Equipped).SelectMany(k => k.Slots),
            s => Assert.Equal(0, s.Enchant))); // the level is never sent before its save
        var save = f.Harness.Saves.Jobs.Single(j => j.Craft is { Ops.Count: > 0 });
        Assert.Equal(CraftOpKind.Enchant, Assert.Single(save.Craft!.Ops).Kind);

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(3); // the commit, EquipmentChanged dispatched, phase 10 sends

        var answer = f.Transport.EnchantResultsFor(1)[^1];
        Assert.Equal((EnchantReason.None, (byte)0, (byte)1, (byte)1), (answer.Reason, answer.FromLevel, answer.ToLevel, answer.StonesUsed));
        var enchanted = Assert.Single(f.Log.Events);
        Assert.Equal(("blacksmith", 0, 1, SwordId), (enchanted.JobId, enchanted.FromLevel, enchanted.ToLevel, enchanted.InstanceId));
        Assert.Equal(1, f.Stones);
        var sent = f.Transport.InventoryFor(1)[^1].Containers.Single(k => k.Code == Equipped).Slots.Single();
        Assert.Equal(1, sent.Enchant);
        Assert.True(f.Stat("atk") > atkBefore); // +1: 10 % of the sword's atk 20 (placeholder) on top
        var stats = f.Player.Combatant!.Stats;
        var enchantLine = Enumerable.Range(0, stats.ModifierCount).Select(stats.ModifierAt)
            .Single(m => m.SourceType == Shared.Simulation.Stats.StatSourceType.Enchant);
        Assert.Equal(2, enchantLine.Flat);
    }
}
