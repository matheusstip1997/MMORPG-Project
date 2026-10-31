using GameServer.Crafting;
using GameServer.Events;
using GameServer.Items;
using GameServer.Jobs;
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
/// The forge engine on the world thread (spec 5.3 R-5.3-01..03, 5.2 R-5.2-04; WP-M3-6) over the repository tables and a real
/// <see cref="InventoryService"/>: a start checks and reserves, Finish consumes the reservation and creates the output in one
/// class 2 operation answered only after the commit (rule 5), a double finish makes one item, two holders cannot reserve one
/// stack, cancel and abort release everything, a stolen input taints the output, a failed conversion consumes its inputs, the
/// daily cap counts per UTC day. The world's gate, ticks and saves are <see cref="ForgeWorldTests"/>; the database is
/// <see cref="ForgePersistenceTests"/>. Numbers are the placeholder rows.
/// </summary>
public class ForgeServiceTests
{
    internal const long Character = 1;
    internal const byte Normal = 1, Safe = 2;
    internal const int SwordMold = 1, RefineIngot = 101, ConvertOre = 102, PickaxeMoldRecipe = 104;
    private const uint Station = 77;

    internal sealed class FakeJobs : IJobQuery
    {
        public Dictionary<string, int> Levels { get; } = new() { ["blacksmith"] = 12 };
        public bool HasJob(long characterId, string jobId) => Levels.ContainsKey(jobId);
        public int JobLevel(long characterId, string jobId) => Levels.TryGetValue(jobId, out int level) ? level : 0;
        public string? JobIn(long characterId, JobSlot slot) => null;
        public int Repeats(long characterId, string jobId, JobActivity activity, int subjectNetId, int lastN) => 0;
    }

    internal sealed class FakeBonuses : IJobBonusQuery
    {
        public Dictionary<JobBonusKind, int> Values { get; } = new();
        public int Get(long characterId, string jobId, JobBonusKind kind, JobActivity activity) => Values.TryGetValue(kind, out int value) ? value : 0;
        public bool MoldTierUnlocked(long characterId, string jobId, int tier) => Values.ContainsKey(JobBonusKind.UnlockMoldTiers);
    }

    internal sealed class Fixture
    {
        private static readonly ConfigSet Tables = TestCombat.Tables();

        public Fixture(IReadOnlyList<ItemStackRow>? stacks = null, bool mold = true, int fillSafe = 0, CraftRecord? craft = null,
            ulong worldSeed = 42)
        {
            Items = ItemRules.From(Tables)!;
            Rules = ForgeRules.From(Tables)!;
            Inventory = new InventoryService(Items, null);
            var instances = new List<ItemInstanceRow>();
            if (mold)
                instances.Add(new ItemInstanceRow(ItemInstanceIds.Make(Character, 1), Safe, 0, "m3.moldSword", 0, false, false,
                    ItemInstanceIds.NewProperties(Character)));
            for (int i = 0; i < fillSafe; i++)
                instances.Add(new ItemInstanceRow(ItemInstanceIds.Make(Character, 10 + i), Safe, 1 + i, "m2.copperRing", 0, false, false,
                    ItemInstanceIds.NewProperties(Character)));
            Stacks = stacks ?? DefaultStacks;
            Assert.True(Inventory.Attach(Character, new InventoryLoad(Stacks, instances, 100, true), "warrior", 12));
            Forge = new ForgeService(Rules, Inventory, Jobs, Bonuses, null, () => NowMs, worldSeed)
            {
                OpCommitted = (_, op) => Committed.Add(op),
                MarkClass2 = _ => Class2++,
                RequestSave = (_, reason) => Saves.Add(reason),
            };
            Forge.Attach(Character, craft ?? CraftRecord.Empty);
        }

        public static readonly ItemStackRow[] DefaultStacks =
        [
            new(Normal, 0, "m3.oreT1", 0, false, 12),
            new(Normal, 1, "m1.cyclopsHide", 0, false, 2),
            new(Normal, 2, "m3.ingotT1", 0, false, 4),
        ];

        public ItemRules Items { get; }
        public ForgeRules Rules { get; }
        public InventoryService Inventory { get; }
        public ForgeService Forge { get; }
        public FakeJobs Jobs { get; } = new();
        public FakeBonuses Bonuses { get; } = new();
        public IReadOnlyList<ItemStackRow> Stacks { get; }
        public long NowMs { get; set; } = 1_793_404_800_000L + 3_600_000; // 2026-10-31 01:00 UTC
        public List<CommittedForgeOp> Committed { get; } = [];
        public List<SaveReason> Saves { get; } = [];
        public int Class2 { get; private set; }

        public ForgeReason Start(ForgeKind kind, int defNetId, params (byte Container, int Slot)[] inputs) =>
            Forge.Start(Character, new ForgeStartAsk(1, Station, NpcInteraction.Forge, kind, defNetId,
                inputs.Select(i => new InventoryLocation(i.Container, i.Slot)).ToArray(), InReach: true, Blocked: false, Connection: 1));

        public ForgeReason StartSword() => Start(ForgeKind.Mold, SwordMold, (Normal, 2), (Normal, 1));

        public ForgeReason StartRefine() => Start(ForgeKind.Recipe, RefineIngot, (Normal, 0));

        /// <summary>The save the world would issue and its commit (rule 5).</summary>
        public (InventoryBatch? Items, CraftBatch? Craft) Commit()
        {
            var saveId = Guid.NewGuid();
            var items = Inventory.TakeBatch(Character, saveId);
            var craft = Forge.TakeBatch(Character, saveId);
            Inventory.Committed(Character, saveId);
            Forge.Committed(Character, saveId);
            return (items, craft);
        }

        public int Count(string itemId, int slot)
        {
            var stack = Inventory.Current(Character)[slot];
            return !stack.IsEmpty && Items.TryGetItem(stack.ItemNetId, out var item) && item.Id == itemId ? stack.Count : 0;
        }

        public int Unreserved(int slot) => Inventory.Unreserved(Character, new InventoryLocation(Normal, slot));

        /// <summary>The instances in the Safe bag's equipment area, by item id.</summary>
        public List<(long Id, string Item)> SafeInstances()
        {
            var inventory = Inventory.CurrentInventory(Character);
            Assert.True(inventory.TryIndex(Safe, out int index));
            var bag = inventory.BagAt(index);
            var list = new List<(long, string)>();
            for (int slot = 0; slot < bag.SlotCount; slot++)
            {
                if (!bag[slot].IsEmpty && Items.TryGetItem(bag[slot].ItemNetId, out var item))
                    list.Add((bag[slot].InstanceId, item.Id));
            }
            return list;
        }
    }

    [Fact]
    public void A_refine_reserves_then_consumes_and_makes_its_output_in_one_operation_answered_after_the_commit()
    {
        var f = new Fixture();
        Assert.Equal(ForgeReason.None, f.StartRefine());
        Assert.Equal(10, f.Unreserved(0)); // two ore held by the session (R-5.3-03: reserved at start)
        Assert.Equal(12, f.Count("m3.oreT1", 0)); // nothing consumed yet

        Assert.Null(f.Forge.Finish(Character, 5, connection: 1)); // applied, waiting for its save
        Assert.Equal([SaveReason.Craft], f.Saves);
        Assert.Empty(f.Committed); // no answer before the commit
        Assert.Equal(ForgeSessionState.Finishing, f.Forge.Session(Character)!.State);

        var (items, craft) = f.Commit();

        var op = Assert.Single(craft!.Ops);
        Assert.Equal((CraftOpKind.Refine, "m3.refine.ingotT1", "m3.ingotT1", 1, true), (op.Kind, op.DefId, op.OutputItemId, op.OutputCount, op.Success));
        Assert.Equal(["m3.oreT1:0:2"], op.Inputs);
        Assert.Contains(items!.Ops, o => o.OpKey == op.OpKey + ":in");
        Assert.Contains(items.Ops, o => o.OpKey == op.OpKey + ":out"); // the same save (rule 5)
        var answer = Assert.Single(f.Committed).Answer;
        Assert.Equal((5u, ForgeReason.None, (ushort)1, true), (answer.RequestId, answer.Reason, answer.Count, answer.Success));
        Assert.Equal(10, f.Count("m3.oreT1", 0));
        Assert.Equal(5, f.Count("m3.ingotT1", 2)); // onto the ingot stack
        Assert.Null(f.Forge.Session(Character));
    }

    [Fact]
    public void A_sword_craft_stores_its_stats_quality_and_crafter_and_its_seed_is_logged()
    {
        var f = new Fixture();
        Assert.Equal(ForgeReason.None, f.StartSword());
        Assert.Equal(ForgeReason.None, f.Forge.Action(Character, 1)); // smelt
        Assert.Equal(ForgeReason.None, f.Forge.Action(Character, 2)); // hit
        var preview = f.Forge.State(Character, 3, ForgeReason.None);
        Assert.Equal(ForgePhase.Running, preview.Phase);
        Assert.Equal(new byte[] { 1, 2 }, preview.Actions);

        Assert.Null(f.Forge.Finish(Character, 4, 1));
        var (_, craft) = f.Commit();

        var op = Assert.Single(craft!.Ops);
        Assert.Equal(CraftOpKind.Craft, op.Kind);
        Assert.InRange(op.Quality, preview.QualityMin, preview.QualityMax); // the preview held the result
        Assert.NotEqual(0UL, op.Seed);
        var answer = f.Committed.Single().Answer;
        Assert.Equal(op.OutputInstanceId, answer.InstanceId);
        Assert.True(f.Inventory.TryGetProperties(Character, answer.InstanceId, out var properties));
        Assert.Equal(op.Quality, properties.Quality);
        Assert.Equal(Character, properties.CrafterId);
        Assert.False(properties.Tainted);
        int bonus = f.Rules.Bands.StatBonusPercent(op.Quality);
        Assert.Equal(12 * (100 + bonus) / 100, properties.CraftedStats.Single(s => s.StatId == "atk").Flat); // ingot 5x2 + hide 2
        Assert.Equal(f.Rules.Bands.Rarity(op.Quality), answer.Rarity);
        Assert.Contains(f.SafeInstances(), i => i.Item == "m3.moldSword"); // the mold is reusable (R-5.3-01)
        Assert.Equal(2, f.Count("m3.ingotT1", 2));
        Assert.Equal(1, f.Count("m1.cyclopsHide", 1));
        Assert.True(f.Inventory.Unreserved(Character, new InventoryLocation(Safe, 0)) == 1); // the mold's own key is released

        // The logged seed replays the craft (rule 6).
        var rolls = ForgeRolls.Draw(op.Seed, new[] { 10, 0 }, new[] { 40, 60 }, f.Rules.Caps.ServerRoll, f.Rules.Caps.SmallTerm, 0);
        var raw = new QualityInputs { Materials = 60, Actions = rolls.Actions, ServerRoll = rolls.ServerRoll, SmallTerm = rolls.SmallTerm };
        Assert.Equal(op.Quality, QualityCalc.Compute(in raw, f.Rules.Caps).Total);
    }

    [Fact]
    public void A_stolen_input_taints_the_output()
    {
        var f = new Fixture(stacks:
        [
            new(Normal, 1, "m1.cyclopsHide", 0, true, 1), // a stolen hide (R-4.4-03)
            new(Normal, 2, "m3.ingotT1", 0, false, 2),
        ]);
        Assert.Equal(ForgeReason.None, f.StartSword());
        f.Forge.Finish(Character, 1, 1);
        f.Commit();

        Assert.True(f.Inventory.TryGetProperties(Character, f.Committed.Single().Answer.InstanceId, out var properties));
        Assert.True(properties.Tainted);
    }

    [Fact]
    public void A_stolen_input_marks_a_refined_stack_stolen()
    {
        var f = new Fixture(stacks: [new(Normal, 0, "m3.oreT1", 0, true, 2)]);
        Assert.Equal(ForgeReason.None, f.StartRefine());
        f.Forge.Finish(Character, 1, 1);
        f.Commit();

        var ingot = f.Inventory.Current(Character)[0];
        Assert.True(ingot.Stolen); // the stolen-tag inheritance of R-5.2-04 / R-4.4-03
    }

    [Fact]
    public void A_double_finish_makes_one_item_and_a_repeat_after_the_commit_gets_the_first_answer()
    {
        var f = new Fixture();
        f.StartSword();
        Assert.Null(f.Forge.Finish(Character, 7, 1));
        Assert.Null(f.Forge.Finish(Character, 7, 1)); // while it saves: nothing more, the commit answers it
        Assert.Equal(ForgeReason.NoSession, f.Forge.Finish(Character, 8, 1)!.Reason);
        Assert.Equal(1, f.Forge.WaitingCount(Character));

        var (_, craft) = f.Commit();
        var repeat = f.Forge.Finish(Character, 7, 1);

        Assert.Single(craft!.Ops);
        Assert.Single(f.SafeInstances(), i => i.Item == "m3.forgedSword");
        Assert.Equal(f.Committed.Single().Answer, repeat);
        Assert.Equal(ForgeReason.NoSession, f.Forge.Finish(Character, 9, 1)!.Reason);
    }

    [Fact]
    public void A_second_finish_with_a_new_id_while_the_first_saves_is_answered_at_once()
    {
        // Wave 1 QA F7, rule 5: every request is answered; only the finishing request itself waits for its commit (as a
        // waiting job op key does). A new id, or the same id from another connection, is refused NoSession at once.
        var f = new Fixture();
        f.StartSword();
        Assert.Null(f.Forge.Finish(Character, 7, 1));

        var second = f.Forge.Finish(Character, 8, 1);
        var otherConnection = f.Forge.Finish(Character, 7, 2);

        Assert.Equal((8u, ForgeReason.NoSession), (second!.RequestId, second.Reason));
        Assert.Equal((7u, ForgeReason.NoSession), (otherConnection!.RequestId, otherConnection.Reason));
        Assert.Equal(ForgeSessionState.Finishing, f.Forge.Session(Character)!.State); // the first result is untouched
        var (_, craft) = f.Commit();
        Assert.Single(craft!.Ops);
        Assert.Equal(7u, f.Committed.Single().Answer.RequestId);
    }

    [Fact]
    public void Two_holders_cannot_reserve_one_stack()
    {
        var f = new Fixture(stacks: [new(Normal, 0, "m3.oreT1", 0, false, 2)]);
        Assert.Equal(ReserveResult.Ok, f.Inventory.Reserve(Character, "other", [new ReserveItem(new InventoryLocation(Normal, 0), 1)]));

        Assert.Equal(ForgeReason.MaterialsMissing, f.StartRefine()); // one free unit, two needed

        Assert.True(f.Inventory.Release(Character, "other"));
        Assert.Equal(ForgeReason.None, f.StartRefine());
        Assert.Equal(ReserveResult.ItemReserved,
            f.Inventory.Reserve(Character, "late", [new ReserveItem(new InventoryLocation(Normal, 0), 1)])); // the session holds both
        Assert.Equal(ForgeReason.SessionActive, f.StartRefine()); // one session per character
        Assert.False(f.Inventory.TryRemove(Character, 0, 1, "sell:1", InventoryReason.OutpostSale, "test")); // reserved units stay
    }

    [Fact]
    public void Two_inputs_of_one_start_count_against_one_stack()
    {
        var f = new Fixture(stacks: [new(Normal, 1, "m1.cyclopsHide", 0, false, 1), new(Normal, 2, "m3.ingotT1", 0, false, 2)]);
        Assert.Equal(ForgeReason.SlotMaterialInvalid, f.Start(ForgeKind.Mold, SwordMold, (Normal, 2), (Normal, 2))); // ingot in the grip
        var g = new Fixture(stacks: [new(Normal, 1, "m1.cyclopsHide", 0, false, 1), new(Normal, 2, "m3.ingotT1", 0, false, 1)]);
        Assert.Equal(ForgeReason.MaterialsMissing, g.StartSword()); // the blade needs two ingots
    }

    [Fact]
    public void Cancel_and_abort_release_everything_and_consume_nothing()
    {
        var f = new Fixture();
        f.StartSword();
        Assert.Equal(0, f.Inventory.Unreserved(Character, new InventoryLocation(Safe, 0))); // the mold is held too
        Assert.Equal(ForgeReason.None, f.Forge.Cancel(Character));
        Assert.Equal(4, f.Unreserved(2));
        Assert.Equal(1, f.Inventory.Unreserved(Character, new InventoryLocation(Safe, 0)));
        Assert.Null(f.Inventory.TakeBatch(Character, Guid.NewGuid())); // nothing written
        Assert.Equal(ForgeReason.NoSession, f.Forge.Cancel(Character));

        f.StartRefine();
        Assert.True(f.Forge.Abort(Character)); // a disconnect, leaving the station, combat
        Assert.Equal(12, f.Unreserved(0));

        f.StartRefine();
        f.Forge.Finish(Character, 1, 1);
        Assert.False(f.Forge.Abort(Character)); // a finishing session is applied already: it is never aborted
    }

    [Fact]
    public void A_crash_before_the_commit_leaves_the_inputs_as_the_database_has_them()
    {
        var f = new Fixture();
        f.StartSword();
        f.Forge.Finish(Character, 1, 1); // applied in memory, the save never commits (the world dies)
        Assert.Empty(f.Committed);

        // The next login loads what the database holds: nothing of the craft was written.
        var again = new Fixture(stacks: f.Stacks);
        Assert.Equal(4, again.Count("m3.ingotT1", 2));
        Assert.Equal(2, again.Count("m1.cyclopsHide", 1));
        Assert.DoesNotContain(again.SafeInstances(), i => i.Item == "m3.forgedSword");
        Assert.Null(again.Forge.Session(Character));
    }

    [Fact]
    public void A_retried_save_answers_once_and_a_stale_completion_changes_nothing()
    {
        var f = new Fixture();
        f.StartRefine();
        f.Forge.Finish(Character, 1, 1);
        var saveId = Guid.NewGuid();
        Assert.NotNull(f.Forge.TakeBatch(Character, saveId));
        f.Forge.Committed(Character, Guid.NewGuid()); // another save's completion
        Assert.Empty(f.Committed);

        f.Forge.Committed(Character, saveId); // the retry of the same save committed (SaveScheduler resends the same job)
        f.Forge.Committed(Character, saveId);
        Assert.Single(f.Committed);
    }

    [Fact]
    public void Starts_are_gated_on_the_mold_job_level_station_and_materials()
    {
        Assert.Equal(ForgeReason.MoldRequired, new Fixture(mold: false).StartSword());
        var f = new Fixture();
        f.Jobs.Levels.Clear();
        Assert.Equal(ForgeReason.JobRequired, f.StartSword());
        f.Jobs.Levels["blacksmith"] = 4;
        Assert.Equal(ForgeReason.JobLevelTooLow, f.Start(ForgeKind.Recipe, PickaxeMoldRecipe, (Normal, 2))); // minJobLevel 5
        Assert.Equal(ForgeReason.UnknownRecipe, f.Start(ForgeKind.Recipe, 999, (Normal, 0)));
        Assert.Equal(ForgeReason.RecipeUnavailable, f.Start(ForgeKind.Mold, 3)); // the ring mold: WP-M3-9
        Assert.Equal(ForgeReason.SlotMaterialInvalid, f.Start(ForgeKind.Mold, SwordMold, (Normal, 0), (Normal, 1))); // ore blade
        Assert.Equal(ForgeReason.SlotMaterialInvalid, f.Start(ForgeKind.Mold, SwordMold, (Normal, 2))); // one input missing
        Assert.Equal(ForgeReason.SlotMaterialInvalid, f.Start(ForgeKind.Mold, SwordMold, (Safe, 0), (Normal, 1))); // not the Normal bag
        Assert.Equal(ForgeReason.WrongStation, f.Forge.Start(Character, new ForgeStartAsk(1, Station, NpcInteraction.JewelerBench,
            ForgeKind.Mold, SwordMold, [new(Normal, 2), new(Normal, 1)], true, false, 1)));
        Assert.Equal(ForgeReason.StationOutOfReach, f.Forge.Start(Character, new ForgeStartAsk(1, Station, NpcInteraction.Forge,
            ForgeKind.Mold, SwordMold, [new(Normal, 2), new(Normal, 1)], InReach: false, Blocked: false, 1)));
        Assert.Equal(ForgeReason.InCombat, f.Forge.Start(Character, new ForgeStartAsk(1, Station, NpcInteraction.Forge,
            ForgeKind.Mold, SwordMold, [new(Normal, 2), new(Normal, 1)], InReach: true, Blocked: true, 1)));
        Assert.Equal(12, f.Unreserved(0)); // no refusal reserves anything
    }

    [Fact]
    public void Actions_follow_the_mold_and_the_session_count_and_recipes_take_none()
    {
        var f = new Fixture();
        Assert.Equal(ForgeReason.NoSession, f.Forge.Action(Character, 1));
        f.StartRefine();
        Assert.Equal(ForgeReason.ActionNotAllowed, f.Forge.Action(Character, 1));
        f.Forge.Cancel(Character);
        f.StartSword();
        for (int i = 0; i < f.Rules.Settings.MaxActions; i++)
            Assert.Equal(ForgeReason.None, f.Forge.Action(Character, 3));
        Assert.Equal(ForgeReason.ActionLimitReached, f.Forge.Action(Character, 3));
        Assert.Equal(ForgeReason.ActionNotAllowed, f.Forge.Action(Character, 99));
    }

    [Fact]
    public void A_full_bag_refuses_the_finish_and_the_session_keeps_running()
    {
        var f = new Fixture(fillSafe: 19); // the mold and 19 rings fill the 20 slots that receive equipment
        f.StartSword();
        var refusal = f.Forge.Finish(Character, 1, 1);
        Assert.Equal(ForgeReason.BagFull, refusal!.Reason);
        Assert.Equal(ForgeSessionState.Running, f.Forge.Session(Character)!.State);
        Assert.Equal(4, f.Count("m3.ingotT1", 2)); // nothing consumed
    }

    [Fact]
    public void A_failed_conversion_consumes_its_inputs_and_makes_nothing()
    {
        // Seeds come from the world seed: try worlds until one conversion fails (70 % success), then check that one.
        for (ulong worldSeed = 1; worldSeed < 200; worldSeed++)
        {
            var f = new Fixture(worldSeed: worldSeed);
            f.Jobs.Levels["blacksmith"] = 12;
            Assert.Equal(ForgeReason.None, f.Start(ForgeKind.Recipe, ConvertOre, (Normal, 0)));
            f.Forge.Finish(Character, 1, 1);
            var (items, craft) = f.Commit();
            var op = Assert.Single(craft!.Ops);
            if (op.Success)
                continue;
            Assert.Equal((CraftOpKind.Convert, 0, 0L), (op.Kind, op.OutputCount, op.OutputInstanceId));
            Assert.Equal(7, f.Count("m3.oreT1", 0)); // five ore gone: a sink (R-5.2-04)
            Assert.Equal(InventoryReason.ConvertFailed.ToString(), items!.Ops.Single().Reason);
            Assert.False(f.Committed.Single().Answer.Success);
            Assert.Equal(1, f.Forge.ConversionsToday(Character, 2)); // a failure counts toward the cap
            return;
        }
        Assert.Fail("no failed conversion in 200 world seeds at 70 % success");
    }

    [Fact]
    public void The_conversion_cap_counts_per_utc_day_from_the_saved_conversions()
    {
        var odds = new Fixture().Rules.Odds(2)!;
        long now = new Fixture().NowMs;
        var today = Enumerable.Range(0, odds.DailyCap).Select(i => new CraftConversion(2, now - 1_000 * (i + 1))).ToList();
        var f = new Fixture(craft: new CraftRecord(today));
        Assert.Equal(ForgeReason.ConversionCapReached, f.Start(ForgeKind.Recipe, ConvertOre, (Normal, 0)));

        var yesterday = Enumerable.Range(0, odds.DailyCap).Select(_ => new CraftConversion(2, now - 2 * 3_600_000)).ToList(); // 23:00
        var g = new Fixture(craft: new CraftRecord(yesterday));
        Assert.Equal(ForgeReason.None, g.Start(ForgeKind.Recipe, ConvertOre, (Normal, 0)));
    }

    [Fact]
    public void A_mold_recipe_makes_a_tool_mold_that_forges_the_higher_tier_pickaxe()
    {
        var f = new Fixture(stacks:
        [
            new(Normal, 0, "m3.oreT2", 0, false, 3), new(Normal, 1, "m1.cyclopsHide", 0, false, 1),
            new(Normal, 2, "m3.ingotT1", 0, false, 4),
        ]);
        Assert.Equal(ForgeReason.MoldTierLocked, f.Start(ForgeKind.Recipe, PickaxeMoldRecipe, (Normal, 2))); // Master Molds
        f.Bonuses.Values[JobBonusKind.UnlockMoldTiers] = 1;
        Assert.Equal(ForgeReason.None, f.Start(ForgeKind.Recipe, PickaxeMoldRecipe, (Normal, 2)));
        f.Forge.Finish(Character, 1, 1);
        var (_, craft) = f.Commit();
        Assert.Equal(CraftOpKind.Mold, craft!.Ops.Single().Kind);
        Assert.Contains(f.SafeInstances(), i => i.Item == "m3.moldPickaxeT2");

        Assert.Equal(ForgeReason.None, f.Start(ForgeKind.Mold, 5, (Normal, 0), (Normal, 1))); // the T2 pickaxe mold
        f.Forge.Finish(Character, 2, 1);
        f.Commit();
        Assert.Contains(f.SafeInstances(), i => i.Item == "m3.pickaxeT2"); // the user's answer: tools come from the forge
    }
    [Fact]
    public void The_job_tree_bonuses_are_read_once_at_the_start_and_kept()
    {
        var f = new Fixture();
        f.Bonuses.Values[JobBonusKind.ActionCount] = 2;
        f.Bonuses.Values[JobBonusKind.QualityBonus] = 10;
        f.StartSword();
        Assert.True(f.Forge.HasSession(Character)); // the job tree refuses a respec now (WP-M3-7)
        Assert.True(f.Forge.InSession(Character));
        f.Bonuses.Values.Clear(); // a respec after the start changes nothing (job-tree decision 10)
        for (int i = 0; i < f.Rules.Settings.MaxActions + 2; i++)
            Assert.Equal(ForgeReason.None, f.Forge.Action(Character, 3));
        Assert.Equal(ForgeReason.ActionLimitReached, f.Forge.Action(Character, 3));
        Assert.Equal(f.Rules.Settings.MaxActions + 2, f.Forge.State(Character, 0, ForgeReason.None).MaxActions);
        Assert.Equal(10, f.Forge.Session(Character)!.QualityBonus);
        f.Forge.Finish(Character, 1, 1);
        f.Commit();
        Assert.False(f.Forge.HasSession(Character));
    }

    [Fact]
    public void A_failed_conversion_may_keep_its_inputs_with_the_salvager_bonus()
    {
        for (ulong worldSeed = 1; worldSeed < 300; worldSeed++)
        {
            var f = new Fixture(worldSeed: worldSeed);
            f.Bonuses.Values[JobBonusKind.KeepInputsPercent] = 100;
            Assert.Equal(ForgeReason.None, f.Start(ForgeKind.Recipe, ConvertOre, (Normal, 0)));
            f.Forge.Finish(Character, 1, 1);
            var (items, craft) = f.Commit();
            var op = Assert.Single(craft!.Ops);
            if (op.Success)
                continue;
            Assert.True(op.InputsKept);
            Assert.True(f.Committed.Single().Answer.InputsKept);
            Assert.Equal(12, f.Count("m3.oreT1", 0)); // nothing consumed
            Assert.Equal(12, f.Unreserved(0));
            Assert.Null(items); // no item change to save
            return;
        }
        Assert.Fail("no failed conversion in 300 world seeds");
    }
}
