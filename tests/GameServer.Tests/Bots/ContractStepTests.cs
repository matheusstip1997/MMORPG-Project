using BotClient;
using BotClient.Brain;
using BotClient.Loop;
using BotClient.Loop.Steps;
using BotClient.World;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Contracts;
using Shared.Simulation.Crafting;
using Shared.Simulation.Economy;
using Shared.Simulation.Jobs;
using Shared.Util;

namespace GameServer.Tests.Bots;

/// <summary>
/// The contract step's enchant contracts (WP-M4-10 part C, spec 5.4, 5.3 R-5.3-04, 11.5) against a step context over the
/// repository's tables, the contract board and the city forge in the client's entity registry and the server's answers applied
/// to the kept clients, as the world client does: the customer's proposal names a sword of its Safe bag (never an equipped one)
/// and the stones of its tier as <see cref="ContractRules.CheckEnchantTerms"/> wants them; the crafter accepts at the board,
/// makes one attempt at the forge naming the contract and checks its pay less the contract fee.
/// </summary>
public class ContractStepTests
{
    private const int OwnId = 1, BoardId = 2, ForgeId = 3;
    private const ushort BoardNetId = 8, ForgeNetId = 4; // NpcDef m1.contractBoard, m1.forge
    private const byte Blacksmith = 1, Normal = 1, Safe = 2, Equipped = 5;
    private const long SafeSwordId = 77, WornSwordId = 78;

    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    [Fact]
    public void An_enchant_proposal_reads_its_item_gold_and_stone_supplier()
    {
        var step = new ContractStep("propose:enchant:m3.forgedSword:20:stones=crafter");
        Assert.Equal((true, "m3.forgedSword", true), (step.ProposesEnchant, step.EnchantItemId, step.CrafterSuppliesStones));
        var any = new ContractStep("propose:enchant");
        Assert.Equal((true, "", false), (any.ProposesEnchant, any.EnchantItemId, any.CrafterSuppliesStones));
        Assert.False(new ContractStep("propose:m3.moldSword:20").ProposesEnchant);

        // Two item ids, or an unknown key: a bad argument fails the entry.
        foreach (var bad in new[] { "propose:enchant:m3.forgedSword:m3.forgedChest", "propose:enchant:stones=nobody", "propose:m3.moldSword:m3.forgedSword" })
        {
            var bot = new Bot("m4-contract-customer");
            var failing = new ContractStep(bad);
            failing.Begin(bot.Context);
            Assert.Equal(BotStepStatus.Failed, bot.Tick(failing));
        }
    }

    [Fact]
    public void The_enchant_slots_name_the_safe_bag_sword_and_the_stones_the_terms_want()
    {
        var data = Data.Value;
        var rules = EnchantRules.From(data.Tables)!;
        var sword = rules.Item("m3.forgedSword")!;
        Assert.True(rules.TryMaterial(sword, out var stone, out int count));
        var slots = new List<ContractSlot>();

        // The worn sword is never offered (the escrow takes no equipped gear): the Safe bag's one is.
        string? missing = ContractStep.TryEnchantSlots(rules, Contents(stones: count, safeSword: true, wornSword: true, level: 3), "", false,
            slots, out var item);
        Assert.Null(missing);
        Assert.Same(sword, item);
        Assert.Equal(ContractReason.None, ContractRules.CheckEnchantTerms(rules, item, 3, slots));
        var target = slots[ContractRules.EnchantItemSlot];
        Assert.Equal((ContractSupplier.Customer, Safe, (byte)0, SafeSwordId, (ushort)1, (byte)3),
            (target.Supplier, target.Container, target.Slot, target.InstanceId, target.Count, target.EnchantLevel));
        var stones = slots[ContractRules.EnchantStonesSlot];
        Assert.Equal((ContractSupplier.Customer, Normal, (byte)4, (ushort)stone.NetId, (ushort)count),
            (stones.Supplier, stones.Container, stones.Slot, stones.ItemNetId, stones.Count));

        // The crafter's stones: named, from no bag of the customer's (it needs none).
        Assert.Null(ContractStep.TryEnchantSlots(rules, Contents(stones: 0, safeSword: true), "m3.forgedSword", true, slots, out item));
        Assert.Equal(ContractReason.None, ContractRules.CheckEnchantTerms(rules, item, 0, slots));
        Assert.Equal((ContractSupplier.Crafter, (byte)0, (ushort)count),
            (slots[1].Supplier, slots[1].Container, slots[1].Count));

        Assert.Equal("contract.noItem", ContractStep.TryEnchantSlots(rules, Contents(stones: count, wornSword: true), "", false, slots, out _));
        Assert.Equal("contract.noItem", ContractStep.TryEnchantSlots(rules, Contents(stones: count, safeSword: true, level: 9), "", false, slots, out _));
        Assert.Equal("contract.noItem", ContractStep.TryEnchantSlots(rules, Contents(stones: count, safeSword: true), "m3.forgedChest", false, slots, out _));
        Assert.Equal("contract.noStones", ContractStep.TryEnchantSlots(rules, Contents(stones: count - 1, safeSword: true), "", false, slots, out _));
        Assert.Empty(slots);
    }

    [Fact]
    public void The_customer_proposes_the_enchant_contract_and_posts_its_id()
    {
        var sides = MemoryPairBoard.NewPair();
        var bot = new Bot("m4-contract-customer", new PairLink(sides[1], false));
        bot.Gold(100);
        bot.Inventory(Contents(stones: 2, safeSword: true, wornSword: true));
        var step = new ContractStep("propose:enchant:m3.forgedSword:20");
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var propose = bot.Sent<ContractRequest>(0);
        Assert.Equal((ContractAction.Propose, ForgeKind.Enchant, (ushort)Item("m3.forgedSword"), 20L, 2),
            (propose.Action, propose.Kind, propose.DefNetId, propose.Gold, propose.Slots.Length));
        Assert.Equal(SafeSwordId, propose.Slots[0].InstanceId);

        bot.Reply(ReliableMessageId.ContractResult, new ContractResult
        {
            RequestId = propose.RequestId, Action = ContractAction.Propose, ContractId = 41, Gold = 20,
        });
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(new PairLink(sides[0], true).TryRead(ContractStep.IdKey, out string posted));
        Assert.Equal("41", posted);
        Assert.Equal(1, bot.Counters.Loop["contract.proposed"]);
    }

    [Fact]
    public void Without_a_safe_bag_item_or_gold_the_proposal_is_soft_failed()
    {
        var noItem = new Bot("m4-contract-customer");
        noItem.Gold(100);
        noItem.Inventory(Contents(stones: 2, wornSword: true));
        var step = new ContractStep("propose:enchant");
        step.Begin(noItem.Context);
        Assert.Equal(BotStepStatus.Failed, noItem.Tick(step)); // a script
        Assert.Equal(1, noItem.Counters.Loop["contract.noItem"]);
        Assert.Equal(0, noItem.SentCount);

        var noGold = new Bot("m4-contract-customer");
        noGold.Gold(5);
        noGold.Inventory(Contents(stones: 2, safeSword: true));
        step = new ContractStep("propose:enchant:m3.forgedSword:20");
        step.Begin(noGold.Context);
        Assert.Equal(BotStepStatus.Failed, noGold.Tick(step));
        Assert.Equal(1, noGold.Counters.Loop["contract.noGold"]);
    }

    [Fact]
    public void The_crafter_accepts_makes_one_attempt_naming_the_contract_and_checks_its_pay()
    {
        var sides = MemoryPairBoard.NewPair();
        new PairLink(sides[1], false).Post(ContractStep.IdKey, "1042");
        var bot = new Bot("m4-contract-crafter", new PairLink(sides[0], true));
        bot.Gold(100);
        bot.Inventory(Contents(stones: 0));
        var step = new ContractStep("craft");
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // reads the id
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // at the board: accepts
        var accept = bot.Sent<ContractRequest>(0);
        Assert.Equal((ContractAction.Accept, (uint)BoardId, 1042L), (accept.Action, accept.NpcEntityId, accept.ContractId));
        bot.Reply(ReliableMessageId.ContractResult, new ContractResult
        {
            RequestId = accept.RequestId, Action = ContractAction.Accept, ContractId = 1042, Gold = 20,
        });
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        bot.Reply(ReliableMessageId.ContractList, new ContractList { Rows = [Entry(1042, crafterStones: false)], Badge = 1 });
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // the enchant branch, not the forge
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // at the forge: one attempt
        var attempt = bot.Sent<EnchantRequest>(1);
        Assert.Equal(((uint)ForgeId, 1042L, 0L), (attempt.StationEntityId, attempt.ContractId, attempt.InstanceId));
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // the answer comes after the commit
        Assert.Equal(2, bot.SentCount);

        bot.Enchanted(attempt.RequestId, EnchantReason.None, from: 0, to: 1);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // waits for the pay
        var rate = Data.Value.Rows<FeeRates>().Single(r => r.Id == "contract");
        long pay = 20 - FeeMath.Total(FeeType.Contract, 20, rate.DefaultPercent, 0);
        bot.Gold(100 + pay);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal((1, 1, 1, pay), (bot.Counters.Loop["contract.accepted"], bot.Counters.Loop["contract.enchanted"],
            bot.Counters.Loop["contract.enchant.up"], bot.Counters.Loop["contract.paid"]));
        Assert.Equal((byte)1, step.Enchanted!.ToLevel);
    }

    [Fact]
    public void A_crafter_without_the_stones_the_terms_ask_of_it_soft_fails_and_a_refusal_fails_with_its_reason()
    {
        var bot = Accepted(1043, crafterStones: true, stones: 0);
        var step = bot.Step;
        Assert.Equal(BotStepStatus.Failed, bot.Bot.Tick(step)); // a script: noStones fails
        Assert.Equal(1, bot.Bot.Counters.Loop["contract.noStones"]);
        Assert.Equal(1, bot.Bot.SentCount); // only the acceptance

        var refused = Accepted(1044, crafterStones: true, stones: 2);
        Assert.Equal(BotStepStatus.Running, refused.Bot.Tick(refused.Step));
        var attempt = refused.Bot.Sent<EnchantRequest>(1);
        Assert.Equal(1044L, attempt.ContractId);
        refused.Bot.Enchanted(attempt.RequestId, EnchantReason.WrongStation);
        Assert.Equal(BotStepStatus.Failed, refused.Bot.Tick(refused.Step));
        Assert.Equal(1, refused.Bot.Counters.Loop["contract.enchantRefused.WrongStation"]);
    }

    [Fact]
    public void The_crafter_starts_the_work_cancels_the_session_and_its_expiry_charges_the_cancel_cost()
    {
        // QA M4 wave 2 D2: the expiry charge of a started contract had no live run (the report's expiry charges owed 0, taken 0).
        var sides = MemoryPairBoard.NewPair();
        new PairLink(sides[1], false).Post(ContractStep.IdKey, "1050");
        var bot = new Bot("m4-contract-crafter", new PairLink(sides[0], true));
        bot.Gold(100);
        bot.Inventory(Contents(stones: 0));
        var step = new ContractStep("craft:expire");
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // reads the id
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // at the board: accepts
        var accept = bot.Sent<ContractRequest>(0);
        bot.Reply(ReliableMessageId.ContractResult, new ContractResult
        {
            RequestId = accept.RequestId, Action = ContractAction.Accept, ContractId = 1050, Gold = 40,
        });
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var sword = ForgeRules.From(Data.Value.Tables)!.Definitions.Single(d => d.Id == "m3.moldSword");
        var listed = new ContractEntry
        {
            ContractId = 1050, Status = ContractStatus.Accepted, Role = ContractRole.Crafter, Kind = sword.Kind, DefNetId = (ushort)sword.NetId,
            Gold = 40, DeadlineHours = 1,
        };
        bot.Reply(ReliableMessageId.ContractList, new ContractList { Rows = [listed], Badge = 1 });
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // the forge run
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // at the forge: the start naming the contract
        Assert.True(ReliableMessages.TryRead(bot.Bytes(1), out ForgeStart start));
        Assert.Equal((1050L, (uint)ForgeId), (start.ContractId, start.StationEntityId));
        bot.Forge(new ForgeState { Version = 1, RequestId = start.RequestId, Phase = ForgePhase.Running, Kind = sword.Kind, DefNetId = (ushort)sword.NetId });
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // the work started: the session is cancelled
        Assert.True(ReliableMessages.TryRead(bot.Bytes(2), out ForgeCancel cancel));
        bot.Forge(new ForgeState { Version = 2, RequestId = cancel.RequestId, Phase = ForgePhase.Cancelled });
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // the own list does not show the work started yet
        bot.Context.View.NowMs += 10_000;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(3, bot.SentCount);
        listed.WorkStarted = true; // the world's flag, then contractWorkSavedWaitMs for its save
        bot.Reply(ReliableMessageId.ContractList, new ContractList { Rows = [listed], Badge = 1 });
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        int savedWaitMs = bot.Context.Behaviour.ContractWorkSavedWaitMs;
        Assert.True(savedWaitMs > 0);
        bot.Context.View.NowMs += savedWaitMs - 1;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(3, bot.SentCount);
        bot.Context.View.NowMs += 1;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // /contracts expire
        Assert.True(ChatCodec.TryReadSendMessage(bot.Bytes(3), out var line) && line.Text == "/contracts expire");

        long cost = ContractMath.CancelCost(40, ContractSettings.Find(Data.Value.Tables)!.CancelCostPercent);
        Assert.True(cost > 0);
        bot.Reply(ReliableMessageId.ContractList, new ContractList { Rows = [], Badge = 0 }); // closed
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // the charge has not come yet
        bot.Gold(100 - cost);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal((1, 1, cost), (bot.Counters.Loop["contract.accepted"], bot.Counters.Loop["contract.expired"], bot.Counters.Loop["contract.expiryCharged"]));
        Assert.False(bot.Counters.Loop.ContainsKey("contract.crafted"), "nothing was made");
    }

    [Fact]
    public void Die_dev_ignores_a_bag_an_earlier_run_left_and_reports_the_one_its_own_death_dropped()
    {
        // Review of #279 (this rig: a bot at the contract board): die:dev has no camp, so it adopted any own bag not used in this
        // process and posted it instead of sending /die; a rerun inside the bag expiry posted the old bag.
        var sides = MemoryPairBoard.NewPair();
        var partner = new PairLink(sides[1], false);
        var bot = new Bot("m4-contract-crafter", new PairLink(sides[0], true));
        bot.Bags(new OwnLootBagEntry { BagEntityId = 50, XCm = 100, YCm = 100, Units = 2 });
        var step = new DieStep("dev");
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(ChatCodec.TryReadSendMessage(bot.Bytes(0), out var line) && line.Text == "/die", "it dies instead of adopting bag 50");
        Assert.False(partner.TryRead("bag.at", out _));
        bot.Counters.Deaths++;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // dead, its bag not listed yet
        bot.Bags(new OwnLootBagEntry { BagEntityId = 51, XCm = 300, YCm = 200, Units = 3 }, new OwnLootBagEntry { BagEntityId = 50, XCm = 100, YCm = 100, Units = 2 });
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.True(partner.TryRead("bag.at", out string posted));
        Assert.Equal("51,300,200", posted);
        Assert.False(bot.Counters.Loop.ContainsKey("die.adopted"));
    }

    [Fact]
    public void The_m4_contract_scenario_has_an_enchant_leg_and_the_qa_partner_an_enchant_script()
    {
        var data = Data.Value;
        Assert.Contains("m3.enchantStone:2", data.Scenario("m4-contract").SeedItemIds);
        var customer = data.Behaviour("m4-contract-customer").LoopSteps.ToList();
        int enchant = customer.IndexOf("contract:propose:enchant:m3.forgedSword:20");
        Assert.True(enchant > customer.IndexOf("contract:propose:m3.moldSword:40") && customer[enchant + 1] == "mail:claim");
        Assert.Equal(2, data.Behaviour("m4-contract-crafter").LoopSteps.Count(s => s == "contract:craft"));
        // QA M4 wave 2 D2: a started contract expires and charges the crafter; the customer proposes it and claims the return.
        Assert.Contains("contract:craft:expire", data.Behaviour("m4-contract-crafter").LoopSteps);
        int started = customer.IndexOf("contract:propose:m3.moldSword:40:crafterExpires");
        Assert.True(started > enchant && customer[started + 1] == "mail:claim");
        Assert.Contains("m3.oreT1:6", data.Scenario("m4-contract").SeedItemIds); // three sword proposals, two ore each
        Assert.Equal(["takeJob:blacksmith", "contract:craft"], data.Behaviour("qa-partner-contract-enchant").LoopSteps);
    }

    /// <summary>A crafter that accepted <paramref name="contractId"/> and listed it, ready for its attempt.</summary>
    private static (Bot Bot, ContractStep Step) Accepted(long contractId, bool crafterStones, int stones)
    {
        var sides = MemoryPairBoard.NewPair();
        new PairLink(sides[1], false).Post(ContractStep.IdKey, contractId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var bot = new Bot("m4-contract-crafter", new PairLink(sides[0], true));
        bot.Gold(100);
        bot.Inventory(Contents(stones));
        var step = new ContractStep("craft");
        step.Begin(bot.Context);
        bot.Tick(step);
        bot.Tick(step);
        var accept = bot.Sent<ContractRequest>(0);
        bot.Reply(ReliableMessageId.ContractResult, new ContractResult
        {
            RequestId = accept.RequestId, Action = ContractAction.Accept, ContractId = contractId, Gold = 20,
        });
        bot.Tick(step);
        bot.Reply(ReliableMessageId.ContractList, new ContractList { Rows = [Entry(contractId, crafterStones)], Badge = 1 });
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        return (bot, step);
    }

    private static ContractEntry Entry(long contractId, bool crafterStones)
    {
        var rules = EnchantRules.From(Data.Value.Tables)!;
        var sword = rules.Item("m3.forgedSword")!;
        rules.TryMaterial(sword, out var stone, out int count);
        return new ContractEntry
        {
            ContractId = contractId, Status = ContractStatus.Accepted, Role = ContractRole.Crafter, Kind = ForgeKind.Enchant,
            DefNetId = (ushort)sword.NetId, Gold = 20, DeadlineHours = 1,
            Slots =
            [
                new ContractSlot { Supplier = ContractSupplier.Customer, Container = Safe, ItemNetId = (ushort)sword.NetId, Count = 1, InstanceId = SafeSwordId },
                new ContractSlot
                {
                    Supplier = crafterStones ? ContractSupplier.Crafter : ContractSupplier.Customer, ItemNetId = (ushort)stone.NetId, Count = (ushort)count,
                },
            ],
        };
    }

    private static ushort Item(string id) => (ushort)Data.Value.Rows<ItemDef>().Single(i => i.Id == id).NetId;

    /// <summary>The stones in the Normal bag (slot 4), a sword in the Safe bag (slot 0) and one worn, as asked.</summary>
    private static InventoryContents Contents(int stones, bool safeSword = false, bool wornSword = false, int level = 0) => new()
    {
        Revision = 1, SlotCount = 30,
        Slots = stones > 0 ? [new InventorySlotEntry { Slot = 4, ItemNetId = Item("m3.enchantStone"), Count = (ushort)stones }] : [],
        Containers =
        [
            new InventoryContainerEntry
            {
                Code = Safe, SlotCount = 20,
                Slots = safeSword
                    ? [new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.forgedSword"), Count = 1, InstanceId = SafeSwordId, CrafterId = 1, Enchant = (byte)level }]
                    : [],
            },
            new InventoryContainerEntry
            {
                Code = Equipped, SlotCount = 8,
                Slots = wornSword
                    ? [new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.forgedSword"), Count = 1, InstanceId = WornSwordId, CrafterId = 1 }]
                    : [],
            },
        ],
    };

    /// <summary>A level-12 Blacksmith's step context at the contract board, the city forge beside it, with nothing connected.</summary>
    private sealed class Bot
    {
        private readonly List<byte[]> _sent = [];
        private uint _revision;

        public Bot(string behaviour, PairLink? pair = null)
        {
            var data = Data.Value;
            var position = data.Markers["m1.npc.contractBoard"];
            var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), data.Behaviour(behaviour), data.Grid, position, 200,
                new Dictionary<int, int>(), new Pcg32Rng(1));
            Context = new BotStepContext(0, data, brain,
                new BotPerception { Self = position, Confirmed = position, NowMs = 1_000_000, ActionIdle = true },
                new EntityRegistry(PredictionSettings.From(data.Tables)), new ClientInventory(data.Tables), new BotOwnState(),
                new BotCounters(), BotStepRegistry.Default(), message => { _sent.Add(message); return true; }, _ => { })
            {
                ClassId = "warrior",
                Enchant = new ClientEnchant { Rules = EnchantRules.From(data.Tables) },
                Forge = new ClientForge { Rules = ForgeRules.From(data.Tables) }, // contract:craft:expire
                LootBags = new ClientLootBags(), // die:dev
                Contracts = new ClientContracts(),
                Pair = pair,
            };
            Npcs();
            Assert.True(Context.Jobs.Apply(ReliableMessages.Write(ReliableMessageId.JobState, new JobState
            {
                Version = 1, Jobs = [new JobEntry { JobNetId = Blacksmith, Slot = JobSlot.Main, Level = 12, XpToNext = 100 }],
            }), out _));
        }

        public BotStepContext Context { get; }

        public BotCounters Counters => Context.Counters;

        public int SentCount => _sent.Count;

        public BotStepStatus Tick(IBotStep step)
        {
            var command = Context.Brain.Hold();
            return step.Tick(Context, ref command);
        }

        public void Gold(long gold) => Assert.True(Context.Outposts.Apply(
            ReliableMessages.Write(ReliableMessageId.GoldBalance, new GoldBalance { Gold = gold }), out _));

        public void Inventory(InventoryContents contents)
        {
            contents.Revision = ++_revision;
            Assert.True(Context.Inventory.Apply(ReliableMessages.Write(ReliableMessageId.InventoryContents, contents), out _));
        }

        public void Reply<T>(ReliableMessageId id, T message) =>
            Assert.True(Context.Contracts!.Apply(ReliableMessages.Write(id, message), out _), id.ToString());

        public void Enchanted(uint requestId, EnchantReason reason, int from = 0, int to = 0) =>
            Assert.True(Context.Enchant.Apply(ReliableMessages.Write(ReliableMessageId.EnchantResult, new EnchantResult
            {
                RequestId = requestId, Reason = reason, InstanceId = SafeSwordId, ItemNetId = Item("m3.forgedSword"),
                FromLevel = (byte)from, ToLevel = (byte)to, StonesUsed = (byte)(reason == EnchantReason.None ? 1 : 0),
            }), out _));

        public byte[] Bytes(int index) => _sent[index];

        private uint _bagsRevision;

        public void Bags(params OwnLootBagEntry[] bags) => Assert.True(Context.LootBags!.Apply(
            ReliableMessages.Write(ReliableMessageId.OwnLootBags, new OwnLootBags { Revision = ++_bagsRevision, Bags = bags }), out _));

        public void Forge(ForgeState state) =>
            Assert.True(Context.Forge.Apply(ReliableMessages.Write(ReliableMessageId.ForgeState, state), out _));

        public T Sent<T>(int index) where T : class
        {
            var bytes = _sent[index];
            object? read = typeof(T) == typeof(ContractRequest)
                ? ReliableMessages.TryRead(bytes, out ContractRequest contract) ? contract : null
                : typeof(T) == typeof(EnchantRequest)
                    ? ReliableMessages.TryRead(bytes, out EnchantRequest enchant) ? enchant : null
                    : null;
            return read as T ?? throw new InvalidOperationException($"message {index} is not a {typeof(T).Name}");
        }

        /// <summary>The own player, the contract board and the forge 50 cm away, in the client's registry from a snapshot.</summary>
        private void Npcs()
        {
            int x = (int)GridPath.Cm(Context.View.Self.X), y = (int)GridPath.Cm(Context.View.Self.Y);
            var buffer = new byte[512];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 100, OriginXCm = x, OriginZCm = y });
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = OwnId, Kind = EntityKind.Player, DefId = 1 }));
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = BoardId, Kind = EntityKind.Npc, DefId = BoardNetId }));
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = ForgeId, Kind = EntityKind.Npc, DefId = ForgeNetId }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = OwnId, XCm = x, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = BoardId, XCm = x + 50, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = ForgeId, XCm = x - 50, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            int size = writer.Finish();
            Assert.True(Context.Entities.ApplySnapshot(buffer.AsSpan(0, size), out _, out _, out _));
        }
    }
}
