using GameServer.Crafting;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;

namespace GameServer.Tests.Crafting;

/// <summary>
/// Gem socketing and removal on the world thread (spec 5.3 R-5.3-05, M3 decision 11; WP-M3-9) over the repository tables and a
/// real <see cref="InventoryService"/>: a socket consumes the gem and writes it into the item in one class 2 operation answered
/// only after the commit (rule 5); a failure consumes the gem and leaves the item untouched; a removal returns the gem for its
/// cost; a stolen gem taints the item and a gem out of a tainted item comes back stolen; every refusal; one operation at a time;
/// a repeated request gets its first answer; the socketed gems' stats join the equipped gear only once committed. The world's
/// gate is <see cref="GemWorldTests"/>; the database is <see cref="GemPersistenceTests"/>. Numbers are the placeholder rows.
/// </summary>
public class GemServiceTests
{
    internal const long Character = 1;
    internal const byte Normal = 1, Safe = 2, Equipped = 5;
    internal static readonly long Sword = ItemInstanceIds.Make(Character, 1);
    internal static readonly long Ring = ItemInstanceIds.Make(Character, 2);
    private const uint Station = 78;

    internal sealed class Fixture
    {
        private static readonly ConfigSet Tables = TestCombat.Tables();

        public Fixture(IReadOnlyList<ItemStackRow>? stacks = null, bool equipped = false, string? swordProperties = null,
            ulong worldSeed = 7, int fillNormal = 0)
        {
            Items = ItemRules.From(Tables)!;
            Rules = GemRules.From(Tables)!;
            Inventory = new InventoryService(Items, null);
            var instances = new List<ItemInstanceRow>
            {
                new(Sword, equipped ? Equipped : Safe, 0, "m3.forgedSword", 0, false, false,
                    swordProperties ?? ItemInstanceIds.NewProperties(Character)),
                new(Ring, Safe, 1, "m2.copperRing", 0, false, false, ItemInstanceIds.NewProperties(Character)),
            };
            var all = (stacks ?? DefaultStacks).ToList();
            for (int slot = 0, filled = 0; filled < fillNormal; slot++)
            {
                if (all.Any(s => s.Slot == slot))
                    continue;
                all.Add(new ItemStackRow(Normal, slot, "m1.demonHorn", 0, false, 1));
                filled++;
            }
            Stacks = all;
            Assert.True(Inventory.Attach(Character, new InventoryLoad(Stacks, instances, 100, true), "warrior", 12));
            Gems = new GemService(Rules, Inventory, Jobs, Bonuses, () => 1_793_404_800_000L, worldSeed)
            {
                OpCommitted = (_, op) => Committed.Add(op),
                MarkClass2 = _ => Class2++,
                RequestSave = (_, reason) => Saves.Add(reason),
            };
            Gems.Attach(Character);
            Jobs.Levels.Clear();
            Jobs.Levels["jeweler"] = 12;
        }

        public static readonly ItemStackRow[] DefaultStacks =
        [
            new(Normal, 0, "m3.rubyT1", 0, false, 3),
            new(Normal, 1, "m3.roughGemT1", 0, false, 4),
            new(Normal, 2, "m3.oreT1", 0, false, 5),
            new(Normal, 3, "m3.rubyT2", 0, false, 1),
        ];

        public ItemRules Items { get; }
        public GemRules Rules { get; }
        public InventoryService Inventory { get; }
        public GemService Gems { get; }
        public ForgeServiceTests.FakeJobs Jobs { get; } = new();
        public ForgeServiceTests.FakeBonuses Bonuses { get; } = new();
        public IReadOnlyList<ItemStackRow> Stacks { get; }
        public List<CommittedGemOp> Committed { get; } = [];
        public List<SaveReason> Saves { get; } = [];
        public int Class2 { get; private set; }

        public byte SwordContainer { get; init; } = Safe;

        /// <summary>A socket that always succeeds (+100 pp) or always fails (-100 pp).</summary>
        public GemResult? Socket(int gemSlot = 0, bool? succeed = true, uint requestId = 1, bool inReach = true, bool blocked = false,
            long instance = 0, byte? container = null, int slot = -1, int connection = 1)
        {
            if (succeed is { } yes)
                Bonuses.Values[JobBonusKind.SocketOddsPercent] = yes ? 100 : -100;
            var target = Target(instance, container, slot);
            return Gems.Handle(Character, new GemAsk(requestId, GemOp.Socket, Station, inReach, blocked, target.At, target.Id, gemSlot, 0,
                connection));
        }

        public GemResult? Remove(int gemIndex = 0, uint requestId = 2, long instance = 0, byte? container = null, int connection = 1)
        {
            var target = Target(instance, container, -1);
            return Gems.Handle(Character, new GemAsk(requestId, GemOp.Remove, Station, true, false, target.At, target.Id, 0, gemIndex,
                connection));
        }

        private (InventoryLocation At, long Id) Target(long instance, byte? container, int slot)
        {
            long id = instance == 0 ? Sword : instance;
            if (container is { } code)
                return (new InventoryLocation(code, slot < 0 ? (id == Ring ? 1 : 0) : slot), id);
            var at = Find(id);
            return (at, id);
        }

        /// <summary>The save the world would issue and its commit (rule 5).</summary>
        public (InventoryBatch? Items, CraftBatch? Craft) Commit()
        {
            var saveId = Guid.NewGuid();
            var items = Inventory.TakeBatch(Character, saveId);
            var craft = Gems.TakeBatch(Character, saveId);
            Inventory.Committed(Character, saveId);
            Gems.Committed(Character, saveId);
            return (items, craft);
        }

        public int Count(string itemId)
        {
            var bag = Inventory.Current(Character);
            int count = 0;
            for (int slot = 0; slot < bag.SlotCount; slot++)
            {
                if (!bag[slot].IsEmpty && Items.TryGetItem(bag[slot].ItemNetId, out var item) && item.Id == itemId)
                    count += bag[slot].Count;
            }
            return count;
        }

        public bool StolenAt(string itemId)
        {
            var bag = Inventory.Current(Character);
            for (int slot = 0; slot < bag.SlotCount; slot++)
            {
                if (!bag[slot].IsEmpty && Items.TryGetItem(bag[slot].ItemNetId, out var item) && item.Id == itemId && bag[slot].Stolen)
                    return true;
            }
            return false;
        }

        /// <summary>Where an instance lies now.</summary>
        public InventoryLocation Find(long instanceId)
        {
            var items = Inventory.CurrentInventory(Character);
            for (int index = 0; index < items.ContainerCount; index++)
            {
                var bag = items.BagAt(index);
                for (int slot = 0; slot < bag.SlotCount; slot++)
                {
                    if (bag[slot].IsInstance && bag[slot].InstanceId == instanceId)
                        return new InventoryLocation(items.Rule(index).Code, slot);
                }
            }
            throw new InvalidOperationException($"no instance {instanceId}");
        }

        /// <summary>The newest properties, a pending change included (never shown before its commit).</summary>
        public ItemProperties LatestSwordProperties()
        {
            Assert.True(Inventory.TryGetLatestProperties(Character, Sword, out var properties));
            return properties;
        }

        /// <summary>The committed properties: what the client and the stats see.</summary>
        public ItemProperties SwordProperties()
        {
            Assert.True(Inventory.TryGetProperties(Character, Sword, out var properties));
            return properties;
        }
    }

    [Fact]
    public void A_socket_consumes_the_gem_and_writes_it_into_the_item_in_one_class_2_operation_answered_after_the_commit()
    {
        var f = new Fixture();
        Assert.Null(f.Socket()); // applied: answered after the commit
        Assert.Equal(2, f.Count("m3.rubyT1"));
        Assert.Empty(f.SwordProperties().Gems); // shown only once committed (rule 5)
        Assert.Equal(new[] { "m3.rubyT1" }, f.LatestSwordProperties().Gems);
        Assert.Equal(new[] { SaveReason.Gem }, f.Saves);
        Assert.True(f.Class2 > 0);
        Assert.Empty(f.Committed);
        Assert.Equal(1, f.Gems.WaitingCount(Character));

        var (items, craft) = f.Commit();
        Assert.NotNull(items);
        Assert.Contains(items!.Ops, op => op.Reason == nameof(InventoryReason.GemSocket));
        Assert.Contains(items.Instances!, i => i.InstanceId == Sword && i.PropertiesJson.Contains("m3.rubyT1"));
        Assert.Contains(items.Audit, a => a.Kind == AuditKind.Modify && a.InstanceId == Sword);
        Assert.Equal(new[] { "m3.rubyT1" }, f.SwordProperties().Gems); // committed now
        var op = Assert.Single(craft!.Ops);
        Assert.Equal(CraftOpKind.Socket, op.Kind);
        Assert.Equal("m3.rubyT1", op.DefId);
        Assert.Equal("m3.forgedSword", op.OutputItemId);
        Assert.Equal(Sword, op.OutputInstanceId);
        Assert.True(op.Success);
        var answer = Assert.Single(f.Committed).Answer;
        Assert.Equal(GemReason.None, answer.Reason);
        Assert.True(answer.Success);
        Assert.Equal("jeweler", f.Committed[0].JobId);
        Assert.Equal(0, f.Gems.WaitingCount(Character));
    }

    [Fact]
    public void A_failed_socket_consumes_the_gem_and_leaves_the_item_untouched()
    {
        var f = new Fixture();
        string before = f.SwordProperties().ToJson();
        Assert.Null(f.Socket(succeed: false));
        Assert.Equal(2, f.Count("m3.rubyT1"));
        Assert.Equal(before, f.SwordProperties().ToJson());
        var (items, craft) = f.Commit();
        Assert.Contains(items!.Ops, op => op.Reason == nameof(InventoryReason.GemSocketFailed));
        Assert.DoesNotContain(items.Audit, a => a.Kind == AuditKind.Modify);
        Assert.False(Assert.Single(craft!.Ops).Success);
        Assert.False(Assert.Single(f.Committed).Answer.Success);
    }

    [Fact]
    public void The_roll_reads_the_tier_odds_plus_the_job_tree_bonus_from_the_seed()
    {
        // Over many seeds a T1 socket (80 %) succeeds about four times in five; the same seed gives the same outcome.
        int successes = 0;
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var f = new Fixture(worldSeed: seed);
            f.Socket(succeed: null);
            f.Commit();
            if (f.Committed[0].Answer.Success)
                successes++;
            var again = new Fixture(worldSeed: seed);
            again.Socket(succeed: null);
            again.Commit();
            Assert.Equal(f.Committed[0].Answer.Success, again.Committed[0].Answer.Success);
            Assert.Equal(f.Committed[0].Record.Seed, again.Committed[0].Record.Seed);
        }
        Assert.InRange(successes, 140, 180);
    }

    [Fact]
    public void A_stolen_gem_taints_the_item()
    {
        var f = new Fixture([new(Normal, 0, "m3.rubyT1", 0, true, 1)]);
        f.Socket();
        Assert.True(f.LatestSwordProperties().Tainted);
    }

    [Fact]
    public void A_removal_returns_the_gem_intact_for_the_cost()
    {
        var f = new Fixture();
        f.Socket();
        f.Commit();
        Assert.Null(f.Remove());
        Assert.Empty(f.LatestSwordProperties().Gems);
        Assert.Equal(3, f.Count("m3.rubyT1"));
        Assert.Equal(2, f.Count("m3.roughGemT1")); // GemSocketOdds t1: 2 rough gems
        var (items, craft) = f.Commit();
        Assert.All(items!.Ops, op => Assert.Equal(nameof(InventoryReason.GemRemove), op.Reason));
        var op = Assert.Single(craft!.Ops);
        Assert.Equal(CraftOpKind.Remove, op.Kind);
        Assert.Equal(new[] { "m3.roughGemT1:0:2" }, op.Inputs);
        var answer = f.Committed[^1].Answer;
        Assert.Equal(GemOp.Remove, answer.Op);
        Assert.Equal(2, answer.CostCount);
    }

    [Fact]
    public void Gentle_extraction_lowers_the_cost()
    {
        var f = new Fixture();
        f.Socket();
        f.Commit();
        f.Bonuses.Values[JobBonusKind.RemovalCostPercent] = 50;
        f.Remove();
        Assert.Equal(3, f.Count("m3.roughGemT1"));
    }

    [Fact]
    public void A_gem_out_of_a_tainted_item_comes_back_stolen_and_the_item_stays_tainted()
    {
        var tainted = ItemProperties.New(Character);
        tainted.Tainted = true;
        tainted.Gems.Add("m3.rubyT1");
        var f = new Fixture([new(Normal, 1, "m3.roughGemT1", 0, false, 4)], swordProperties: tainted.ToJson());
        Assert.Null(f.Remove());
        Assert.True(f.StolenAt("m3.rubyT1"));
        Assert.True(f.LatestSwordProperties().Tainted);
    }

    [Fact]
    public void Every_refusal_has_its_reason()
    {
        var f = new Fixture();
        Assert.Equal(GemReason.StationOutOfReach, f.Socket(inReach: false)!.Reason);
        Assert.Equal(GemReason.InCombat, f.Socket(blocked: true)!.Reason);
        Assert.Equal(GemReason.NotSocketable, f.Socket(instance: Ring)!.Reason); // gemSlots 0
        Assert.Equal(GemReason.NotSocketable, f.Socket(instance: Sword, container: Safe, slot: 5)!.Reason); // not where it is
        Assert.Equal(GemReason.NotAGem, f.Socket(gemSlot: 2)!.Reason);
        Assert.Equal(GemReason.NotAGem, f.Socket(gemSlot: 9)!.Reason); // an empty slot
        Assert.Equal(GemReason.NoSuchGem, f.Remove(gemIndex: 0)!.Reason);
        f.Jobs.Levels["jeweler"] = 4;
        Assert.Equal(GemReason.JobLevelTooLow, f.Socket(gemSlot: 3)!.Reason); // a T2 gem needs Jeweler level 5
        f.Jobs.Levels.Clear();
        Assert.Equal(GemReason.JobRequired, f.Socket()!.Reason);
        Assert.Empty(f.Saves);
        Assert.Equal(3, f.Count("m3.rubyT1"));
    }

    [Fact]
    public void A_full_item_and_a_missing_cost_and_a_full_bag_are_refused()
    {
        var socketed = ItemProperties.New(Character);
        socketed.Gems.Add("m3.rubyT1");
        var f = new Fixture([new(Normal, 0, "m3.rubyT1", 0, false, 3), new(Normal, 1, "m3.roughGemT1", 0, false, 1)],
            swordProperties: socketed.ToJson());
        Assert.Equal(GemReason.NoGemSlot, f.Socket()!.Reason);
        Assert.Equal(GemReason.MaterialsMissing, f.Remove()!.Reason); // 2 rough gems needed, 1 held
        var full = new Fixture([new(Normal, 1, "m3.roughGemT1", 0, false, 4)], swordProperties: socketed.ToJson(), fillNormal: 29);
        Assert.Equal(GemReason.BagFull, full.Remove()!.Reason);
    }

    [Fact]
    public void A_reserved_gem_or_target_is_refused()
    {
        var f = new Fixture();
        Assert.Equal(ReserveResult.Ok, f.Inventory.Reserve(Character, "other", [new ReserveItem(new InventoryLocation(Normal, 0), 3)]));
        Assert.Equal(GemReason.ItemReserved, f.Socket()!.Reason);
        f.Inventory.Release(Character, "other");
        var at = f.Find(Sword);
        Assert.Equal(ReserveResult.Ok, f.Inventory.Reserve(Character, "enchant", [new ReserveItem(at, 1)]));
        Assert.Equal(GemReason.ItemReserved, f.Socket()!.Reason);
    }

    [Fact]
    public void One_operation_waits_at_a_time_and_a_repeat_gets_its_first_answer()
    {
        var f = new Fixture();
        Assert.Null(f.Socket(requestId: 5));
        Assert.Null(f.Socket(requestId: 5)); // the same request while it saves: nothing more
        Assert.Equal(GemReason.GemBusy, f.Socket(requestId: 6)!.Reason);
        Assert.Equal(GemReason.GemBusy, f.Remove(requestId: 7)!.Reason); // two operations on one item
        Assert.Equal(2, f.Count("m3.rubyT1"));
        f.Commit();
        var repeat = f.Socket(requestId: 5);
        Assert.NotNull(repeat);
        Assert.Same(f.Committed[0].Answer, repeat);
        Assert.Equal(2, f.Count("m3.rubyT1"));
        Assert.Single(f.SwordProperties().Gems);
    }

    [Fact]
    public void A_crash_before_the_commit_leaves_the_gem_and_the_item_as_they_were()
    {
        var f = new Fixture();
        Assert.Null(f.Socket());
        // The process dies before the save: the next start loads the same rows.
        var after = new Fixture(f.Stacks);
        Assert.Equal(3, after.Count("m3.rubyT1"));
        Assert.Empty(after.SwordProperties().Gems);
        Assert.Empty(after.Committed);
    }

    [Fact]
    public void A_stale_or_retried_completion_answers_once()
    {
        var f = new Fixture();
        f.Socket();
        var saveId = Guid.NewGuid();
        f.Inventory.TakeBatch(Character, saveId);
        Assert.NotNull(f.Gems.TakeBatch(Character, saveId));
        f.Gems.Committed(Character, Guid.NewGuid()); // another save's completion
        Assert.Empty(f.Committed);
        f.Inventory.Committed(Character, saveId);
        f.Gems.Committed(Character, saveId);
        f.Gems.Committed(Character, saveId);
        Assert.Single(f.Committed);
    }

    [Fact]
    public void An_equipped_items_gems_join_its_stats_only_once_committed()
    {
        var f = new Fixture(equipped: true);
        Assert.Empty(f.Inventory.GetEquippedGemBonuses(Character));
        f.Socket();
        Assert.Empty(f.Inventory.GetEquippedGemBonuses(Character)); // not committed yet (rule 5)
        f.Commit();
        var bonus = Assert.Single(f.Inventory.GetEquippedGemBonuses(Character));
        Assert.Equal(("atk", 4, 0), (bonus.StatId, bonus.Flat, bonus.Percent));
        f.Remove();
        f.Commit();
        Assert.Empty(f.Inventory.GetEquippedGemBonuses(Character));
    }

    [Fact]
    public void A_gem_in_an_item_in_the_safe_bag_gives_no_stats()
    {
        var f = new Fixture();
        f.Socket();
        f.Commit();
        Assert.Empty(f.Inventory.GetEquippedGemBonuses(Character));
    }
}
