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
using Shared.Simulation.Stats;
using Shared.Util;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.Bots;

/// <summary>
/// QA M2 wave 0 F2: the M2 loop steps sell and buy (WP-M2-4, spec 4.2), equip (WP-M2-1, R-3.5-07) and rankUp (WP-M2-3,
/// R-3.6-04) against a step context over the repository's tables, an NPC in the client's entity registry (a snapshot as
/// the server writes it) and the server's answers applied to the client's kept state, as the world client does.
/// </summary>
public class BotLoopStepTests
{
    private const int OwnId = 1, NpcId = 2;
    private const int OutpostNetId = 1, VendorNetId = 2; // NpcDef m1.outpost, m1.vendor
    private const ushort Horn = 1, Ash = 2, Sword = 101, Bow = 102, Vest = 108, Ring = 111, Potion = 113; // ItemDef net ids
    private const byte SafeEquipment = 2, Equipped = 5, Chest = 3, WeaponMain = 0; // ContainerDef and EquipSlotDef codes

    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    // ---- sell -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Sell_opens_the_outpost_quotes_the_sell_only_stacks_and_sells_for_the_quoted_total()
    {
        var bot = new Bot(Data.Value.Behaviour("m2-loop"));
        bot.NpcAt(OutpostNetId);
        bot.Bag(Slot(0, Horn, 3), Slot(1, Ash, 2), Slot(2, Horn, 1, stolen: true));
        var step = new SellStep();
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var open = bot.Sent<OutpostInteractRequest>(0);
        Assert.Equal((NpcInteraction.Sell, (uint)NpcId), (open.Kind, open.NpcEntityId)); // the visit counts (outpostsVisited)
        bot.Reply(ReliableMessageId.OutpostInteractResult, new OutpostInteractResult { RequestId = open.RequestId, Kind = NpcInteraction.Sell });

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var quote = bot.Sent<SellQuoteRequest>(1);
        Assert.Equal(((byte)0, (ushort)3), (quote.Slot, quote.Count)); // only the horns: ash is a material, slot 2 is stolen
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // waits for the quote
        Assert.Equal(2, bot.SentCount);
        bot.Reply(ReliableMessageId.SellQuoteResult, new SellQuoteResult { RequestId = quote.RequestId, Slot = 0, Count = 3, Total = 27 });

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var sell = bot.Sent<SellRequest>(2);
        Assert.Equal(((byte)0, (ushort)3, 27L), (sell.Slot, sell.Count, sell.QuotedTotal));
        bot.Reply(ReliableMessageId.SellResult, new SellResult { RequestId = sell.RequestId, Count = 3, Gold = 27 });

        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(3, bot.Counters.Loop["sell.units"]);
        Assert.Equal(27, bot.Counters.Loop["sell.gold"]);
    }

    [Fact]
    public void Sell_quotes_again_after_an_expired_quote_and_skips_the_stack_after_loopSellQuoteRetries()
    {
        var behaviour = Data.Value.Behaviour("m2-loop");
        Assert.Equal(2, behaviour.LoopSellQuoteRetries);
        var bot = new Bot(behaviour);
        bot.NpcAt(OutpostNetId);
        bot.Bag(Slot(0, Horn, 3), Slot(4, Horn, 2));
        var step = new SellStep();
        step.Begin(bot.Context);
        bot.Tick(step);
        bot.Reply(ReliableMessageId.OutpostInteractResult, new OutpostInteractResult { RequestId = bot.Sent<OutpostInteractRequest>(0).RequestId });

        // Slot 0: the price fell three times between quote and sale (another seller): two new quotes, then skipped.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            bot.Tick(step);
            var quote = bot.Last<SellQuoteRequest>();
            Assert.Equal((byte)0, quote.Slot);
            bot.Reply(ReliableMessageId.SellQuoteResult, new SellQuoteResult { RequestId = quote.RequestId, Slot = 0, Count = 3, Total = 27 - attempt });
            bot.Tick(step);
            var sell = bot.Last<SellRequest>();
            Assert.Equal(27 - attempt, sell.QuotedTotal);
            bot.Reply(ReliableMessageId.SellResult, new SellResult { RequestId = sell.RequestId, Reason = OutpostReason.QuoteExpired });
        }
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // slot 4 next
        var next = bot.Last<SellQuoteRequest>();
        Assert.Equal((byte)4, next.Slot);
        bot.Reply(ReliableMessageId.SellQuoteResult, new SellQuoteResult { RequestId = next.RequestId, Slot = 4, Count = 2, Total = 18 });
        bot.Tick(step);
        bot.Reply(ReliableMessageId.SellResult, new SellResult { RequestId = bot.Last<SellRequest>().RequestId, Count = 2, Gold = 18 });

        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(3, bot.Counters.Loop["sell.quoteExpired"]);
        Assert.Equal(1, bot.Counters.Loop["sell.skipped"]);
        Assert.Equal(2, bot.Counters.Loop["sell.units"]);
    }

    [Fact]
    public void Sell_with_one_expired_quote_sells_on_the_new_quote()
    {
        var bot = new Bot(Data.Value.Behaviour("m2-loop"));
        bot.NpcAt(OutpostNetId);
        bot.Bag(Slot(0, Horn, 3));
        var step = new SellStep();
        step.Begin(bot.Context);
        bot.Tick(step);
        bot.Reply(ReliableMessageId.OutpostInteractResult, new OutpostInteractResult { RequestId = bot.Sent<OutpostInteractRequest>(0).RequestId });
        bot.Tick(step);
        bot.Reply(ReliableMessageId.SellQuoteResult, new SellQuoteResult { RequestId = bot.Last<SellQuoteRequest>().RequestId, Count = 3, Total = 27 });
        bot.Tick(step);
        bot.Reply(ReliableMessageId.SellResult, new SellResult { RequestId = bot.Last<SellRequest>().RequestId, Reason = OutpostReason.QuoteExpired });
        bot.Tick(step);
        bot.Reply(ReliableMessageId.SellQuoteResult, new SellQuoteResult { RequestId = bot.Last<SellQuoteRequest>().RequestId, Count = 3, Total = 24 });
        bot.Tick(step);
        Assert.Equal(24, bot.Last<SellRequest>().QuotedTotal);
        bot.Reply(ReliableMessageId.SellResult, new SellResult { RequestId = bot.Last<SellRequest>().RequestId, Count = 3, Gold = 24 });

        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["sell.quoteExpired"]);
        Assert.Equal(24, bot.Counters.Loop["sell.gold"]);
        Assert.False(bot.Counters.Loop.ContainsKey("sell.skipped"));
    }

    [Fact]
    public void Sell_with_nothing_sellable_is_done_at_once_without_walking_or_asking()
    {
        var bot = new Bot(Data.Value.Behaviour("m2-loop"));
        bot.Bag(Slot(0, Ash, 5));
        var step = new SellStep();
        step.Begin(bot.Context);

        var command = bot.Context.Brain.Hold();
        Assert.Equal(BotStepStatus.Done, step.Tick(bot.Context, ref command));
        Assert.Equal(MoveKeys.None, command.Keys);
        Assert.Equal(0, bot.SentCount);
        Assert.Equal(1, bot.Counters.Loop["sell.nothingToSell"]);
    }

    [Fact]
    public void Sell_walks_toward_the_outpost_marker_before_its_npc_is_in_interest()
    {
        var bot = new Bot(Data.Value.Behaviour("m2-loop"), at: Data.Value.Markers["m1.spawn.combat.boss"]); // ~59 m away
        bot.Bag(Slot(0, Horn, 1));
        var step = new SellStep();
        step.Begin(bot.Context);

        var command = bot.Context.Brain.Hold();
        Assert.Equal(BotStepStatus.Running, step.Tick(bot.Context, ref command));
        Assert.NotEqual(MoveKeys.None, command.Keys);
        Assert.Equal(0, bot.SentCount);
    }

    // ---- buy --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Buy_tops_the_potions_up_as_far_as_the_gold_pays_at_the_fixed_price()
    {
        var behaviour = Data.Value.Behaviour("m2-loop");
        Assert.Equal(("m2.potionHpSmall", 5), (behaviour.LoopBuyItemId, behaviour.LoopBuyKeep));
        var bot = new Bot(behaviour);
        bot.NpcAt(VendorNetId);
        bot.Bag(Slot(0, Potion, 1));
        bot.Reply(ReliableMessageId.GoldBalance, new GoldBalance { Gold = 60 });
        var step = new BuyStep();
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var buy = bot.Sent<VendorBuyRequest>(0);
        // 4 missing, 60 gold pays 2 at 25 (NpcVendorStock m1.vendor.potionHpSmall, R-4.2-01).
        Assert.Equal(((uint)NpcId, Potion, (ushort)2, 50L), (buy.NpcEntityId, buy.ItemNetId, buy.Count, buy.QuotedTotal));
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        bot.Reply(ReliableMessageId.VendorBuyResult, new VendorBuyResult { RequestId = buy.RequestId, ItemNetId = Potion, Count = 2, GoldSpent = 50 });

        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(2, bot.Counters.Loop["buy.units"]);
        Assert.Equal(50, bot.Counters.Loop["buy.gold"]);
    }

    [Theory]
    [InlineData(10, 1, "buy.noGold")] // 10 gold does not pay one potion: no walk
    [InlineData(500, 5, "buy.stocked")]
    public void Buy_is_done_at_once_without_gold_or_with_enough_potions(long gold, ushort held, string counter)
    {
        var bot = new Bot(Data.Value.Behaviour("m2-loop"));
        bot.NpcAt(VendorNetId);
        bot.Bag(Slot(0, Potion, held));
        bot.Reply(ReliableMessageId.GoldBalance, new GoldBalance { Gold = gold });
        var step = new BuyStep();
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(0, bot.SentCount);
        Assert.Equal(1, bot.Counters.Loop[counter]);
    }

    [Fact]
    public void Buy_of_an_item_no_vendor_stocks_finds_no_npc_and_a_refused_purchase_is_counted()
    {
        var behaviour = Clone(Data.Value.Behaviour("m2-loop"));
        behaviour.LoopBuyItemId = "m2.trainingSword";
        var bot = new Bot(behaviour);
        bot.NpcAt(VendorNetId);
        bot.Reply(ReliableMessageId.GoldBalance, new GoldBalance { Gold = 500 });
        var step = new BuyStep();
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["buy.noNpc"]);

        var potions = new Bot(Data.Value.Behaviour("m2-loop"));
        potions.NpcAt(VendorNetId);
        potions.Reply(ReliableMessageId.GoldBalance, new GoldBalance { Gold = 500 });
        step.Begin(potions.Context);
        potions.Tick(step);
        potions.Reply(ReliableMessageId.VendorBuyResult,
            new VendorBuyResult { RequestId = potions.Sent<VendorBuyRequest>(0).RequestId, Reason = OutpostReason.BagFull });
        Assert.Equal(BotStepStatus.Failed, potions.Tick(step));
        Assert.Equal(1, potions.Counters.Loop["buy.refused.BagFull"]);
    }

    [Fact]
    public void Buy_with_an_argument_buys_one_of_that_item_and_fails_without_gold()
    {
        // WP-M3-12: "buy:<item>" (the M3 exit run's pickaxe and potion) makes sure the bot holds one, whatever loopBuyItemId says.
        var bot = new Bot(Data.Value.Behaviour("m3-exit-blacksmith"));
        Assert.Equal("", bot.Context.Behaviour.LoopBuyItemId);
        bot.NpcAt(VendorNetId);
        bot.Bag();
        bot.Reply(ReliableMessageId.GoldBalance, new GoldBalance { Gold = 60 });
        var step = new BuyStep("m2.potionHpSmall");
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var buy = bot.Sent<VendorBuyRequest>(0);
        Assert.Equal((Potion, (ushort)1, 25L), (buy.ItemNetId, buy.Count, buy.QuotedTotal)); // one, not loopBuyKeep
        bot.Reply(ReliableMessageId.VendorBuyResult, new VendorBuyResult { RequestId = buy.RequestId, ItemNetId = Potion, Count = 1, GoldSpent = 25 });
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["buy.units"]);

        // The entry asks for the item, so no gold is a failure (the M2 top-up is done with buy.noGold instead).
        var poor = new Bot(Data.Value.Behaviour("m3-exit-blacksmith"));
        poor.NpcAt(VendorNetId);
        poor.Bag();
        poor.Reply(ReliableMessageId.GoldBalance, new GoldBalance { Gold = 10 });
        step.Begin(poor.Context);
        Assert.Equal(BotStepStatus.Failed, poor.Tick(step));
        Assert.Equal((1, 0), (poor.Counters.Loop["buy.noGold"], poor.SentCount));
    }

    [Fact]
    public void Buy_of_the_exit_run_pickaxe_with_the_real_tables_buys_one_at_its_vendor_price_and_fails_below_it()
    {
        // Regression (main b12931a): WP-M3-2's m3.pickaxeT1 row and its vendor stock make buy:m3.pickaxeT1 play for real.
        var data = Data.Value;
        var pickaxe = data.Row<ItemDef>("m3.pickaxeT1", i => i.Id);
        int price = data.Rows<NpcVendorStock>().Single(s => s.ItemId == pickaxe.Id && s.NpcId == "m1.vendor").PriceGold;
        var bot = new Bot(data.Behaviour("m3-exit-blacksmith"));
        bot.NpcAt(VendorNetId);
        bot.Bag();
        bot.Reply(ReliableMessageId.GoldBalance, new GoldBalance { Gold = price });
        var step = new BuyStep(pickaxe.Id);
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var buy = bot.Sent<VendorBuyRequest>(0);
        Assert.Equal(((uint)NpcId, (ushort)pickaxe.NetId, (ushort)1, (long)price), (buy.NpcEntityId, buy.ItemNetId, buy.Count, buy.QuotedTotal));
        bot.Reply(ReliableMessageId.VendorBuyResult,
            new VendorBuyResult { RequestId = buy.RequestId, ItemNetId = (ushort)pickaxe.NetId, Count = 1, GoldSpent = price });
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal((1, price), (bot.Counters.Loop["buy.units"], bot.Counters.Loop["buy.gold"]));

        var poor = new Bot(data.Behaviour("m3-exit-blacksmith"));
        poor.NpcAt(VendorNetId);
        poor.Bag();
        poor.Reply(ReliableMessageId.GoldBalance, new GoldBalance { Gold = price - 1 });
        step.Begin(poor.Context);
        Assert.Equal(BotStepStatus.Failed, poor.Tick(step));
        Assert.Equal((1, 0), (poor.Counters.Loop["buy.noGold"], poor.SentCount));
        Assert.Contains("buy.noGold", poor.Context.LastReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_priced_buy_of_a_script_comes_after_a_fight_and_a_sell_that_pay_for_it()
    {
        // A fresh exit character has no gold (WP-M3-12's first exit run): a buy:<item> a vendor stocks must follow a fight
        // and then a sell, or it fails with buy.noGold whatever the fight earns.
        var data = Data.Value;
        var stocked = data.Rows<NpcVendorStock>().Select(s => s.ItemId).ToHashSet(StringComparer.Ordinal);
        var scripts = data.Rows<BotBehaviour>().Where(b => b.LoopOnce).ToList();
        Assert.Contains(scripts, b => b.Id == "m3-exit-blacksmith");
        foreach (var script in scripts)
        {
            bool fought = false, sold = false;
            foreach (string entry in script.LoopSteps)
            {
                BotBehaviour.TryParseLoopStep(entry, out string id, out string argument);
                if (id == "fight")
                    fought = true;
                else if (id == "sell" && fought)
                    sold = true;
                else if (id == "buy" && stocked.Contains(argument))
                    Assert.True(sold, $"{script.Id}: '{entry}' comes before a fight and a sell");
            }
        }
    }

    [Fact]
    public void Buy_with_an_argument_counts_every_container_and_an_item_not_in_the_tables_is_not_yet()
    {
        var bot = new Bot(Data.Value.Behaviour("m3-exit-blacksmith"));
        bot.NpcAt(VendorNetId);
        bot.Bag([], Gear(SafeEquipment, Slot(0, Potion, 1))); // a tool lands in the Safe bag equipment area
        bot.Reply(ReliableMessageId.GoldBalance, new GoldBalance { Gold = 500 });
        var held = new BuyStep("m2.potionHpSmall");
        held.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Done, bot.Tick(held));
        Assert.Equal(1, bot.Counters.Loop["buy.stocked"]);

        // WP-M3-2's pickaxe row has not landed: not yet, with the reason the exit run prints.
        var pickaxe = new BuyStep("m3.pickaxeT1.missing");
        pickaxe.Begin(bot.Context);
        Assert.Equal(BotStepStatus.NotAvailable, bot.Tick(pickaxe));
        Assert.Contains("not in ItemDef yet", bot.Context.LastReason, StringComparison.Ordinal);
        Assert.Equal(0, bot.SentCount);

        // An item in the tables that no vendor stocks yet is not yet too (the vendor row is content still to come).
        var sword = new BuyStep("m2.trainingSword");
        sword.Begin(bot.Context);
        Assert.Equal(BotStepStatus.NotAvailable, bot.Tick(sword));
        Assert.Contains("NpcVendorStock", bot.Context.LastReason, StringComparison.Ordinal);
    }

    // ---- level (WP-M3-12) -------------------------------------------------------------------------------------------

    [Fact]
    public void Level_waits_for_the_own_level_and_is_done_at_the_argument_and_failed_below_it()
    {
        var bot = new Bot(Data.Value.Behaviour("m3-exit-blacksmith"));
        var step = new LevelStep("12");
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // no ProgressionState yet
        bot.Level(12);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["level.reached"]);

        var low = new Bot(Data.Value.Behaviour("m3-exit-blacksmith"));
        low.Level(1); // the seed did not apply
        Assert.Equal(BotStepStatus.Failed, low.Tick(step));
        Assert.Equal(1, low.Counters.Loop["level.below"]);
        Assert.Equal(BotStepStatus.Done, low.Tick(new LevelStep())); // without an argument: a known level
        Assert.Throws<ArgumentException>(() => new LevelStep("twelve"));
    }

    // ---- equip ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Equip_crafted_is_not_yet_until_the_client_can_tell_a_crafted_instance()
    {
        // WP-M3-12: the exit run's "equip:crafted" waits for WP-M3-2's typed properties and WP-M3-6's forge.
        var bot = new Bot(Data.Value.Behaviour("m3-exit-blacksmith"));
        bot.Level(12);
        bot.Bag(Slot(0, Sword, 1));
        var step = new EquipStep(EquipStep.Crafted);
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.NotAvailable, bot.Tick(step));
        Assert.Contains("WP-M3-6", bot.Context.LastReason, StringComparison.Ordinal);
        Assert.Equal(0, bot.SentCount);
        Assert.Throws<ArgumentException>(() => new EquipStep("best"));
    }

    [Fact]
    public void Equip_moves_a_usable_piece_into_its_empty_slot_out_of_combat()
    {
        var bot = new Bot(Data.Value.Behaviour("m2-loop"));
        bot.Level(1);
        bot.Bag([], Gear(SafeEquipment, Slot(3, Vest, 1)), Gear(Equipped, Slot(WeaponMain, Sword, 1)));
        var step = new EquipStep();
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var move = bot.Sent<InventoryMoveRequest>(0);
        Assert.Equal((SafeEquipment, (byte)3, Equipped, Chest), (move.FromContainer, move.FromSlot, move.ToContainer, move.ToSlot));
        bot.Reply(ReliableMessageId.InventoryMoveResult, new InventoryMoveResult { RequestId = move.RequestId, Outcome = InventoryMoveOutcome.Moved });

        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["equip.items"]);
    }

    [Fact]
    public void Equip_replaces_a_lower_rarity_and_keeps_an_equal_one()
    {
        var bot = new Bot(Data.Value.Behaviour("m2-loop"));
        bot.Level(1);
        bot.Bag([], Gear(SafeEquipment, Slot(0, Sword, 1, rarity: 1)), Gear(Equipped, Slot(WeaponMain, Sword, 1)));
        var step = new EquipStep();
        step.Begin(bot.Context);
        bot.Tick(step);
        var move = bot.Sent<InventoryMoveRequest>(0);
        Assert.Equal((SafeEquipment, (byte)0, Equipped, WeaponMain), (move.FromContainer, move.FromSlot, move.ToContainer, move.ToSlot));

        var same = new Bot(Data.Value.Behaviour("m2-loop"));
        same.Level(1);
        same.Bag([], Gear(SafeEquipment, Slot(0, Sword, 1, rarity: 1)), Gear(Equipped, Slot(WeaponMain, Sword, 1, rarity: 1)));
        step.Begin(same.Context);
        Assert.Equal(BotStepStatus.Done, same.Tick(step));
        Assert.Equal(1, same.Counters.Loop["equip.nothing"]);
        Assert.Equal(0, same.SentCount);
    }

    [Fact]
    public void Equip_skips_a_weapon_of_another_class_line_and_gear_above_the_level()
    {
        var bot = new Bot(Data.Value.Behaviour("m2-loop")); // a warrior: swords only (ClassGearDef, R-3.5-07)
        bot.Level(1);
        bot.Bag([], Gear(SafeEquipment, Slot(0, Bow, 1, rarity: 3), Slot(1, Ring, 1)), Gear(Equipped, Slot(WeaponMain, Sword, 1)));
        var step = new EquipStep();
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Done, bot.Tick(step)); // the ring needs level 5
        Assert.Equal(1, bot.Counters.Loop["equip.nothing"]);
        Assert.Equal(0, bot.SentCount);
    }

    [Fact]
    public void Equip_waits_while_in_combat_or_during_an_action_and_counts_an_InCombat_refusal()
    {
        var bot = new Bot(Data.Value.Behaviour("m2-loop"));
        bot.Level(1);
        bot.Bag([], Gear(SafeEquipment, Slot(0, Vest, 1)));
        int combatStateMs = Data.Value.Tables.Single<StartingValuesM1>().CombatStateMs;
        bot.Context.View.LastCombatAtMs = bot.Context.View.NowMs - 1_000;
        var step = new EquipStep();
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(0, bot.SentCount); // spec 12.7 combat state: hit 1 s ago
        bot.Context.View.NowMs = bot.Context.View.LastCombatAtMs + combatStateMs;
        bot.Context.View.ActionIdle = false;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(0, bot.SentCount); // an action runs
        bot.Context.View.ActionIdle = true;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var move = bot.Sent<InventoryMoveRequest>(0);

        bot.Reply(ReliableMessageId.InventoryMoveResult, new InventoryMoveResult { RequestId = move.RequestId, Reason = InventoryMoveReason.InCombat });
        Assert.Equal(BotStepStatus.Failed, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["equip.refused.InCombat"]);
    }

    // ---- rank-up ----------------------------------------------------------------------------------------------------

    [Fact]
    public void RankUp_asks_once_per_rank_and_again_only_after_a_level_up_since_the_refusal()
    {
        var bot = new Bot(Data.Value.Behaviour("m2-loop"));
        bot.NpcAt(OutpostNetId);
        var step = new RankUpStep();
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step)); // no ProgressionState yet
        Assert.Equal(1, bot.Counters.Loop["rankUp.waiting"]);

        bot.Level(3);
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var ask = bot.Sent<OutpostInteractRequest>(0);
        Assert.Equal((NpcInteraction.RankUp, (uint)NpcId), (ask.Kind, ask.NpcEntityId));
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        bot.Reply(ReliableMessageId.OutpostInteractResult,
            new OutpostInteractResult { RequestId = ask.RequestId, Kind = NpcInteraction.RankUp, Reason = OutpostReason.RankRequirementsNotMet });
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["rankUp.notMet"]);

        step.Begin(bot.Context); // the next round, same level: not again
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.SentCount);

        bot.Level(4);
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var again = bot.Sent<OutpostInteractRequest>(1);
        bot.Reply(ReliableMessageId.OutpostInteractResult,
            new OutpostInteractResult { RequestId = again.RequestId, Kind = NpcInteraction.RankUp });
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["rankUp.ranks"]);

        bot.Level(4, Rank.E); // the committed rank-up's ProgressionState: a new rank is asked about once
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(3, bot.SentCount);
    }

    [Fact]
    public void RankUp_at_the_top_rank_never_asks_again()
    {
        var bot = new Bot(Data.Value.Behaviour("m2-loop"));
        bot.NpcAt(OutpostNetId);
        bot.Level(80, Rank.SS);
        var step = new RankUpStep();
        step.Begin(bot.Context);
        bot.Tick(step);
        bot.Reply(ReliableMessageId.OutpostInteractResult,
            new OutpostInteractResult { RequestId = bot.Sent<OutpostInteractRequest>(0).RequestId, Kind = NpcInteraction.RankUp, Reason = OutpostReason.RankMaxed });
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));

        bot.Level(81, Rank.SS);
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.SentCount);
        Assert.Equal(1, bot.Counters.Loop["rankUp.maxed"]);
    }

    // ---- own gold -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_gold_balance_sets_the_own_gold_and_other_messages_do_not()
    {
        IBotOwnReader reader = new OwnGoldReader();
        var own = new BotOwnState();
        reader.OnReliable(ReliableMessageId.ProgressionState, ReliableMessages.Write(ReliableMessageId.ProgressionState, new ProgressionState { Level = 2 }), own);
        Assert.Null(own.Gold);
        reader.OnReliable(ReliableMessageId.GoldBalance, ReliableMessages.Write(ReliableMessageId.GoldBalance, new GoldBalance { Gold = 0 }), own);
        Assert.Equal(0, own.Gold); // known, even at 0
        reader.OnReliable(ReliableMessageId.GoldBalance, ReliableMessages.Write(ReliableMessageId.GoldBalance, new GoldBalance { Gold = 42 }), own);
        Assert.Equal(42, own.Gold);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static InventorySlotEntry Slot(byte slot, ushort item, ushort count, bool stolen = false, byte rarity = 0) =>
        new() { Slot = slot, ItemNetId = item, Count = count, Stolen = stolen, Rarity = rarity, InstanceId = count == 1 && item > 100 ? slot + 1000 : 0 };

    private static InventoryContainerEntry Gear(byte code, params InventorySlotEntry[] slots) =>
        new() { Code = code, SlotCount = (byte)Data.Value.Rows<ContainerDef>().Single(c => c.Code == code).Slots, Slots = slots };

    /// <summary>A copy of every settable property (the shared table rows stay untouched).</summary>
    private static BotBehaviour Clone(BotBehaviour source)
    {
        var copy = new BotBehaviour();
        foreach (var property in typeof(BotBehaviour).GetProperties().Where(p => p.CanWrite))
            property.SetValue(copy, property.GetValue(source));
        return copy;
    }

    /// <summary>A warrior's step context at the outpost marker with nothing connected; requests are kept, answers applied as the world client does.</summary>
    private sealed class Bot
    {
        private readonly List<byte[]> _sent = [];

        public Bot(BotBehaviour behaviour, FixVec2? at = null)
        {
            var data = Data.Value;
            var position = at ?? data.Markers["m1.npc.outpost"];
            var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), behaviour, data.Grid, position, 200, new Dictionary<int, int>(),
                new Pcg32Rng(1));
            Context = new BotStepContext(0, data, brain,
                new BotPerception { Self = position, Confirmed = position, NowMs = 1_000_000, ActionIdle = true },
                new EntityRegistry(PredictionSettings.From(data.Tables)), new ClientInventory(data.Tables), new BotOwnState(),
                new BotCounters(), BotStepRegistry.Default(), message => { _sent.Add(message); return true; }, _ => { })
            {
                ClassId = "warrior",
            };
        }

        public BotStepContext Context { get; }

        public BotCounters Counters => Context.Counters;

        public int SentCount => _sent.Count;

        public BotStepStatus Tick(IBotStep step)
        {
            var command = Context.Brain.Hold();
            return step.Tick(Context, ref command);
        }

        /// <summary>An NPC of <paramref name="defNetId"/> 50 cm from the bot, in the client's registry from a snapshot.</summary>
        public void NpcAt(ushort defNetId)
        {
            int x = (int)GridPath.Cm(Context.View.Self.X), y = (int)GridPath.Cm(Context.View.Self.Y);
            var buffer = new byte[512];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 100, OriginXCm = x, OriginZCm = y });
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = OwnId, Kind = EntityKind.Player, DefId = 1 }));
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = NpcId, Kind = EntityKind.Npc, DefId = defNetId }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = OwnId, XCm = x, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = NpcId, XCm = x + 50, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            int size = writer.Finish();
            Assert.True(Context.Entities.ApplySnapshot(buffer.AsSpan(0, size), out _, out _, out _));
        }

        public void Bag(params InventorySlotEntry[] normal) => Bag(normal, []);

        public void Bag(InventorySlotEntry[] normal, params InventoryContainerEntry[] containers) =>
            Reply(ReliableMessageId.InventoryContents, new InventoryContents
            {
                Revision = 1, SlotCount = (byte)Data.Value.Rows<ContainerDef>().Single(c => c.Id == ContainerDef.NormalBag).Slots,
                Slots = normal, Containers = containers,
            });

        public void Level(int level, Rank rank = Rank.F) =>
            Reply(ReliableMessageId.ProgressionState, new ProgressionState { Level = level, Rank = rank, BandPercent = 100 });

        /// <summary>A server message, applied to the kept client state the way <c>BotWorldClient</c> routes it.</summary>
        public void Reply<T>(ReliableMessageId id, T message)
        {
            var bytes = ReliableMessages.Write(id, message);
            if (!Context.Progression.Apply(bytes, out _) && !Context.Outposts.Apply(bytes, out _))
                Assert.True(Context.Inventory.Apply(bytes, out _), id.ToString());
        }

        public T Sent<T>(int index) => Read<T>(_sent[index]);

        public T Last<T>() => Read<T>(_sent.Last(m => Is<T>(m)));

        private static bool Is<T>(byte[] message)
        {
            try
            {
                Read<T>(message);
                return true;
            }
            catch (Xunit.Sdk.XunitException)
            {
                return false;
            }
        }

        private static T Read<T>(byte[] message)
        {
            object? value = typeof(T).Name switch
            {
                nameof(OutpostInteractRequest) when ReliableMessages.TryRead(message, out OutpostInteractRequest r) => r,
                nameof(SellQuoteRequest) when ReliableMessages.TryRead(message, out SellQuoteRequest r) => r,
                nameof(SellRequest) when ReliableMessages.TryRead(message, out SellRequest r) => r,
                nameof(VendorBuyRequest) when ReliableMessages.TryRead(message, out VendorBuyRequest r) => r,
                nameof(InventoryMoveRequest) when ReliableMessages.TryRead(message, out InventoryMoveRequest r) => r,
                _ => null,
            };
            Assert.True(value is T, $"not a {typeof(T).Name}: message id {ReliableMessages.PeekId(message)}");
            return (T)value!;
        }
    }
}
