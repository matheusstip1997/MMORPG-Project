using GameServer.Crafting;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;

namespace GameServer.Tests.Crafting;

/// <summary>
/// Enchanting on the world thread (spec 5.3 R-5.3-04, M3 decision 10; WP-M3-8) over the repository tables and a real
/// <see cref="InventoryService"/>: each refusal, the stones and the item reserved, the stones always consumed and the level
/// written in one class 2 operation answered only after the commit (rule 5), the committed level the only one shown and
/// counted by the stats, a double submit and two attempts on one item, the job tree's bonus, the seed logged, and the enchant
/// lines of an equipped item with <see cref="EquipmentChanged"/>. The database is <see cref="EnchantPersistenceTests"/>.
/// Numbers are the placeholder rows (level 0 rises at 100 %).
/// </summary>
public class EnchantServiceTests
{
    private const long Character = 1;
    private const byte Normal = 1, Safe = 2, Equipped = 5;
    private const uint Station = 77;
    private static readonly long SwordId = ItemInstanceIds.Make(Character, 1);
    private static readonly long RingId = ItemInstanceIds.Make(Character, 2);

    private sealed class Log : IEventConsumer<EquipmentChanged>
    {
        public List<EquipmentChanged> Changes { get; } = new();
        public void Handle(in EventEnvelope envelope, in EquipmentChanged payload) => Changes.Add(payload);
    }

    private sealed class Fixture
    {
        private static readonly ConfigSet Tables = TestCombat.Tables();

        public Fixture(int swordLevel = 0, bool equipped = false, int stones = 3, ulong worldSeed = 42)
        {
            Items = ItemRules.From(Tables)!;
            Rules = EnchantRules.From(Tables)!;
            Bus.Subscribe<EquipmentChanged>(Log);
            Inventory = new InventoryService(Items, Bus);
            var sword = ItemProperties.New(Character);
            sword.Quality = 300;
            sword.CraftedStats.Add(new ItemStatBonus("atk", 20, 0));
            sword.Enchant = swordLevel;
            var instances = new List<ItemInstanceRow>
            {
                new(SwordId, equipped ? Equipped : Safe, 0, "m3.forgedSword", 0, false, false, sword.ToJson()),
                new(RingId, Safe, 1, "m2.copperRing", 0, false, false, ItemInstanceIds.NewProperties(Character)),
            };
            var stacks = stones > 0 ? new[] { new ItemStackRow(Normal, 3, "m3.enchantStone", 0, false, stones) } : Array.Empty<ItemStackRow>();
            Assert.True(Inventory.Attach(Character, new InventoryLoad(stacks, instances, 10, true), "warrior", 12));
            Enchant = new EnchantService(Rules, Inventory, Jobs, Bonuses, () => 1_793_404_800_000L, worldSeed)
            {
                OpCommitted = (_, op) => Committed.Add(op),
                RequestSave = (_, reason) => Saves.Add(reason),
            };
            Enchant.Attach(Character);
        }

        public EventBus Bus { get; } = new(1, new FakeClock(), TestPipelineSettings.BusLimits());
        public Log Log { get; } = new();
        public ItemRules Items { get; }
        public EnchantRules Rules { get; }
        public InventoryService Inventory { get; }
        public EnchantService Enchant { get; }
        public ForgeServiceTests.FakeJobs Jobs { get; } = new();
        public ForgeServiceTests.FakeBonuses Bonuses { get; } = new();
        public List<CommittedEnchantOp> Committed { get; } = [];
        public List<SaveReason> Saves { get; } = [];

        public EnchantResult? Request(uint requestId = 1, byte container = Safe, int slot = 0, long? instance = null,
            NpcInteraction station = NpcInteraction.Forge, bool inReach = true, bool blocked = false, int connection = 1) =>
            Enchant.Request(Character, new EnchantAsk(requestId, Station, station, new InventoryLocation(container, slot),
                instance ?? SwordId, inReach, blocked, connection));

        /// <summary>The save the world would issue and its commit (rule 5).</summary>
        public (InventoryBatch? Items, CraftBatch? Craft) Commit()
        {
            var saveId = Guid.NewGuid();
            var items = Inventory.TakeBatch(Character, saveId);
            var craft = Enchant.TakeBatch(Character, saveId);
            Inventory.Committed(Character, saveId);
            Enchant.Committed(Character, saveId);
            Bus.DispatchQueued();
            return (items, craft);
        }

        public int Stones => Inventory.Current(Character)[3].IsEmpty ? 0 : Inventory.Current(Character)[3].Count;

        public int CommittedLevel => Inventory.TryGetProperties(Character, SwordId, out var p) ? p.Enchant : -1;
    }

    [Theory]
    [InlineData(false, false, EnchantReason.StationOutOfReach)]
    [InlineData(true, true, EnchantReason.InCombat)]
    public void The_station_gate_and_combat_refuse(bool inReach, bool blocked, EnchantReason expected)
    {
        var f = new Fixture();
        Assert.Equal(expected, f.Request(inReach: inReach, blocked: blocked)!.Reason);
        Assert.Equal(3, f.Stones);
    }

    [Fact]
    public void The_item_must_be_the_named_equipment_instance_in_the_safe_bag_or_equipped()
    {
        var f = new Fixture();
        Assert.Equal(EnchantReason.NotEnchantable, f.Request(instance: RingId)!.Reason); // another instance in that slot
        Assert.Equal(EnchantReason.NotEnchantable, f.Request(container: Normal, slot: 3)!.Reason); // the Normal bag: stones
        Assert.Equal(EnchantReason.NotEnchantable, f.Request(slot: 5)!.Reason); // an empty slot
    }

    [Fact]
    public void Only_the_items_job_enchants_and_only_at_its_station()
    {
        var f = new Fixture();
        Assert.Equal(EnchantReason.WrongStation, f.Request(station: NpcInteraction.JewelerBench)!.Reason);
        // The ring is the Jeweler's (dropped gear by category, decision 10) and enchants at the bench.
        Assert.Equal(EnchantReason.JobRequired, f.Request(slot: 1, instance: RingId, station: NpcInteraction.JewelerBench)!.Reason);
        f.Jobs.Levels.Clear();
        Assert.Equal(EnchantReason.JobRequired, f.Request()!.Reason);
        Assert.Equal(3, f.Stones);
    }

    [Fact]
    public void An_item_at_plus_nine_is_refused_and_keeps_its_stones()
    {
        var f = new Fixture(swordLevel: 9);
        Assert.Equal(EnchantReason.EnchantMaxed, f.Request()!.Reason);
        Assert.Equal(3, f.Stones);
    }

    [Fact]
    public void Without_the_stones_it_is_refused()
    {
        var f = new Fixture(stones: 0);
        Assert.Equal(EnchantReason.MaterialsMissing, f.Request()!.Reason);
    }

    [Fact]
    public void An_attempt_consumes_its_stones_and_is_answered_only_after_the_commit()
    {
        var f = new Fixture();
        Assert.Null(f.Request()); // applied, waiting for its save
        Assert.Equal(new[] { SaveReason.Enchant }, f.Saves);
        Assert.Equal(2, f.Stones); // tier 1: one stone, consumed now (in the batch)
        Assert.Empty(f.Committed);
        // Rule 5: the committed level (what the contents and the stats read) is still +0; the next change builds on +1.
        Assert.Equal(0, f.CommittedLevel);
        Assert.True(f.Inventory.TryGetLatestProperties(Character, SwordId, out var latest));
        Assert.Equal(1, latest.Enchant);
        Assert.True(f.Inventory.IsReserved(Character, new InventoryLocation(Safe, 0))); // the item waits for its save

        var (items, craft) = f.Commit();

        var op = Assert.Single(f.Committed);
        Assert.Equal((EnchantReason.None, 0, 1, 1), (op.Answer.Reason, op.Answer.FromLevel, op.Answer.ToLevel, op.Answer.StonesUsed));
        Assert.Equal(1, f.CommittedLevel);
        Assert.False(f.Inventory.IsReserved(Character, new InventoryLocation(Safe, 0)));
        var record = Assert.Single(craft!.Ops);
        Assert.Equal((CraftOpKind.Enchant, "m3.forgedSword", SwordId, 0, 1, 1), (record.Kind, record.DefId, record.OutputInstanceId,
            record.EnchantFrom, record.EnchantTo, record.Tier));
        Assert.Equal(new[] { "m3.enchantStone:0:1" }, record.Inputs);
        Assert.Equal(EnchantService.OpKey(Character, record.Seed), record.OpKey); // the seed is logged (rule 6)
        Assert.Contains(items!.Ops, o => o.Reason == nameof(InventoryReason.Enchant));
        Assert.Contains(items.Audit, a => a.Kind == AuditKind.Modify && a.InstanceId == SwordId && a.CountDelta == 0);
        var instance = Assert.Single(items.Instances!, i => i.InstanceId == SwordId);
        Assert.True(ItemProperties.TryParse(instance.PropertiesJson, out var written, out _));
        Assert.Equal((1, 300), (written.Enchant, written.Quality)); // the rest of the properties kept
    }

    [Fact]
    public void A_double_submit_applies_it_once_and_the_repeat_after_the_commit_gets_the_first_answer()
    {
        var f = new Fixture();
        Assert.Null(f.Request(requestId: 5));
        Assert.Null(f.Request(requestId: 5)); // the same request while it saves: nothing more
        Assert.Equal(2, f.Stones);
        f.Commit();
        var repeat = f.Request(requestId: 5);
        Assert.Equal((EnchantReason.None, (byte)1), (repeat!.Reason, repeat.ToLevel));
        Assert.Equal(2, f.Stones);
        Assert.Single(f.Committed);
    }

    [Fact]
    public void A_second_attempt_on_the_item_before_the_commit_is_refused()
    {
        var f = new Fixture();
        Assert.Null(f.Request(requestId: 1));
        Assert.Equal(EnchantReason.ItemReserved, f.Request(requestId: 2)!.Reason);
        Assert.Equal(2, f.Stones);
        f.Commit();
        Assert.Null(f.Request(requestId: 3)); // after the commit the next try is allowed, from the committed +1
        f.Commit();
        Assert.Equal(1, f.Committed[1].Answer.FromLevel);
    }

    [Fact]
    public void A_reserved_stone_stack_is_not_taken()
    {
        var f = new Fixture(stones: 1);
        Assert.Equal(ReserveResult.Ok, f.Inventory.Reserve(Character, "forge:other", [new ReserveItem(new InventoryLocation(Normal, 3), 1)]));
        Assert.Equal(EnchantReason.MaterialsMissing, f.Request()!.Reason);
    }

    [Fact]
    public void A_reserved_item_is_refused()
    {
        var f = new Fixture();
        Assert.Equal(ReserveResult.Ok, f.Inventory.Reserve(Character, "forge:other", [new ReserveItem(new InventoryLocation(Safe, 0), 1)]));
        Assert.Equal(EnchantReason.ItemReserved, f.Request()!.Reason);
        Assert.Equal(3, f.Stones);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void The_job_tree_bonus_moves_the_odds_toward_plus_one(int bonus)
    {
        // +8 rises at 10 % (placeholder); with 90 points from -1 then no change it always rises.
        int ups = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var f = new Fixture(swordLevel: 8, worldSeed: seed);
            f.Bonuses.Values[JobBonusKind.EnchantOddsPercent] = bonus;
            Assert.Null(f.Request());
            f.Commit();
            if (f.Committed[0].Answer.ToLevel == 9)
                ups++;
        }
        if (bonus == 90)
            Assert.Equal(40, ups);
        else
            Assert.InRange(ups, 0, 20);
    }

    [Fact]
    public void The_logged_seed_replays_the_outcome()
    {
        for (ulong world = 1; world <= 20; world++)
        {
            var f = new Fixture(swordLevel: 5, worldSeed: world);
            Assert.Null(f.Request());
            var (_, craft) = f.Commit();
            var record = Assert.Single(craft!.Ops);
            var weights = EnchantMath.Weights(f.Rules.Odds(5)!, 0);
            int replay = EnchantMath.Apply(5, EnchantMath.Outcome(weights, EnchantMath.Draw(record.Seed)));
            Assert.Equal(replay, record.EnchantTo);
            Assert.InRange(record.EnchantTo, 4, 6);
        }
    }

    [Fact]
    public void An_equipped_item_rebuilds_its_enchant_lines_and_publishes_equipment_changed_after_the_commit()
    {
        var f = new Fixture(swordLevel: 0, equipped: true);
        Assert.Empty(f.Inventory.GetEnchantBonuses(Character));
        Assert.Null(f.Request(container: Equipped));
        Assert.Empty(f.Inventory.GetEnchantBonuses(Character)); // not before the commit (rule 5)
        f.Log.Changes.Clear();

        f.Commit();

        Assert.Single(f.Log.Changes, c => c.CharacterId == Character);
        var line = Assert.Single(f.Inventory.GetEnchantBonuses(Character));
        Assert.Equal(("atk", 2, 0), (line.StatId, line.Flat, line.Percent)); // +1 adds 10 % of atk 20 (placeholder)
        Assert.Contains(f.Inventory.GetEquippedBonuses(Character), b => b.StatId == "atk" && b.Flat == 20); // the snapshot unchanged
    }
}
