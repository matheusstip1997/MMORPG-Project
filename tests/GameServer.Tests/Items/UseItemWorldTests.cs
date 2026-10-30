using GameServer.Combat;
using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Economy;
using GameServer.Tests.Support;
using GameServer.World.Map;
using Shared.Data;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Items;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Items;

/// <summary>
/// Consumable use in the world tick by tick (spec 3.4 "Consumable use", R-3.4-03, R-3.7-05; WP-M3-3): the request starts
/// the item's use action in phase 3, the unit is removed at the action's consume effect (class 2, reason ItemUsed), the
/// heal, MP restore, status and meter raise follow, and the answer comes only after the commit. The four transaction tests
/// of rule 5 (double submit, retry, crash in the middle, concurrent access) are here at the world level; the database
/// level is <see cref="ItemUsePersistenceTests"/>. Numbers come from the placeholder rows of data/tables/ConsumableDef.json.
/// </summary>
public class UseItemWorldTests
{
    private const string HpPotion = "m2.potionHpSmall"; // heal 60, toxicity 25, cooldown 5 s (placeholders)
    private const string MpPotion = "m2.potionMpSmall"; // MP 30, toxicity 25
    private const string Bread = "m2.breadLoaf"; // satiety 30, wellFed
    private const string Horn = "m1.demonHorn"; // sell-only: not usable

    private sealed class Fixture
    {
        public Fixture(ItemStackRow[] bag, int toxicityMilli = 0, int satietyMilli = 0, int hp = 100)
        {
            Tables = TestCombat.Tables();
            Items = ItemRules.From(Tables)!;
            Actions = ActionCatalog.From(Tables);
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<ItemUsed>(Used);
            var map = TestMaps.Map(TestMaps.Open().Grid,
            [
                TestMaps.SpawnMarker(0, 0), TestCombat.MonsterMarker(5_000, 0),
                EconomyFixture.Npc(EconomyFixture.Vendor, 0, 200, "m1.vendor"),
            ]);
            Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus, actions: Actions,
                combat: TestCombat.Rules(Tables, TestCombat.Point()), items: Items, economy: EconomyRules.From(Tables));
            long now = Harness.Clock.UtcNowMs;
            Character = Harness.Characters.Add(1, 0, new CharacterState(1, 0, "", 0, 0, 0, 0, hp, 10, 100,
                ToxicityMilli: toxicityMilli, SatietyMilli: satietyMilli, ConsumptionAtUtcMs: now));
            Harness.Characters.Items[Character.CharacterId] = bag;
            Harness.Characters.StoreGold(Character.CharacterId, 100);
            Player = Harness.Join(1);
            Harness.Ticks(1);
        }

        public ConfigSet Tables { get; }
        public ItemRules Items { get; }
        public ActionCatalog Actions { get; }
        public EventBus Bus { get; }
        public UsedRecorder Used { get; } = new();
        public WorldHarness Harness { get; }
        public CharacterRecord Character { get; }
        public Player Player { get; set; }
        public long CharacterId => Character.CharacterId;
        public InventoryService Inventory => Harness.World.Inventory!;
        public FakeTransport Transport => Harness.Transport;
        public ConsumableUse Uses => Harness.World.Consumables!;
        public long Now => Harness.Clock.UtcNowMs;

        /// <summary>Ticks from the request until the drink's consume effect fired (startup rounded to ticks, plus the request's tick).</summary>
        public int DrinkTicks => Actions.Find("use.drink")!.StartupEnd + 1;

        public int EatTicks => Actions.Find("use.eat")!.StartupEnd + 1;

        public ItemStack Slot(int slot) => Inventory.Current(CharacterId)[slot];

        public ushort NetId(string itemId)
        {
            Assert.True(Items.TryGetItem(itemId, out var item));
            return (ushort)item.NetId;
        }

        public int Hp => Player.Combatant!.Hp(Harness.World.NowMs);

        public int Mp => Player.Combatant!.Meters.Current(ResourceKind.Mp, Harness.World.NowMs);

        public void Hurt(int amount) => Player.Combatant!.Meters.Drain(ResourceKind.Hp, amount, Harness.World.NowMs);

        /// <summary>The next class 2 batch (every second) and its completion.</summary>
        public void Save()
        {
            Harness.Clock.Advance(1_000);
            Harness.Ticks(2);
        }

        public List<UseItemResult> Results => Transport.UseResultsFor(1);
    }

    internal sealed class UsedRecorder : IEventConsumer<ItemUsed>
    {
        public List<ItemUsed> Events { get; } = [];

        public void Handle(in EventEnvelope envelope, in ItemUsed payload) => Events.Add(payload);
    }

    private static ItemStackRow Row(int slot, string item, int count) => new(1, slot, item, 0, false, count);

    [Fact]
    public void Drinking_an_hp_potion_removes_one_at_the_effect_point_heals_raises_toxicity_and_answers_after_the_save()
    {
        var f = new Fixture([Row(0, HpPotion, 3)]);
        f.Hurt(80);
        int hpBefore = f.Hp;

        f.Harness.SendUseItem(1, requestId: 1, slot: 0);
        f.Harness.Ticks(1);
        Assert.Equal(f.Actions.Find("use.drink")!.Index, f.Player.Actions.ActionIndex); // the drink runs: a commitment
        Assert.Equal(3, f.Slot(0).Count); // nothing used before the effect point

        f.Harness.Ticks(f.DrinkTicks - 1);
        Assert.Equal(2, f.Slot(0).Count);
        Assert.Equal(hpBefore + 60, f.Hp);
        Assert.Equal(25, f.Player.Consumption!.Toxicity(f.Now));
        Assert.Empty(f.Results); // not before the commit

        f.Save();
        var result = Assert.Single(f.Results);
        Assert.Equal((1u, f.NetId(HpPotion), UseItemReason.None), (result.RequestId, result.ItemNetId, result.Reason));
        var job = Assert.Single(f.Harness.Saves.Jobs, j => j.Inventory != null && j.Inventory.Ops.Any(o => o.OpKey.StartsWith("use:")));
        var op = Assert.Single(job.Inventory!.Ops);
        Assert.Equal((f.Uses.OpKey(f.Player, 1), nameof(InventoryReason.ItemUsed)), (op.OpKey, op.Reason));
        // The meter rides the same save as the removal (it decayed for the second the save waited: 10 points a minute).
        Assert.Equal(25_000 - 1_000 * 10 * 1_000 / 60_000, job.State.ToxicityMilli);
        var used = Assert.Single(f.Used.Events);
        Assert.Equal((f.CharacterId, f.Player.Id, "", 0, (int)f.NetId(HpPotion), 1, op.OpKey),
            (used.CharacterId, used.EntityId, used.JobId, used.Tier, used.ItemNetId, used.Count, used.OpKey)); // WP-M3-1's record
        var ids = f.Transport.InventoryMessageIdsFor(1);
        Assert.Equal([ReliableMessageId.UseItemResult, ReliableMessageId.InventoryContents], ids.Skip(ids.Count - 2));
    }

    [Fact]
    public void An_mp_potion_restores_mp_and_bread_adds_satiety_and_the_food_status()
    {
        var f = new Fixture([Row(0, MpPotion, 1), Row(1, Bread, 1)]);
        f.Player.Combatant!.Meters.Set(ResourceKind.Mp, 0, f.Harness.World.NowMs);
        int mpBefore = f.Mp;

        f.Harness.SendUseItem(1, 1, 0);
        f.Harness.Ticks(f.Actions.Find("use.drink")!.TotalTicks + 1); // the drink ends
        f.Harness.SendUseItem(1, 2, 1);
        f.Harness.Ticks(f.EatTicks);

        Assert.True(f.Mp > mpBefore);
        Assert.Equal(25, f.Player.Consumption!.Toxicity(f.Now));
        Assert.Equal(30, f.Player.Consumption.Satiety(f.Now));
        var status = Assert.Single(Enumerable.Range(0, f.Player.Combatant!.Statuses.Count), i => f.Player.Combatant.Statuses[i].Definition.Id == "wellFed");
        Assert.Equal(StatSourceType.Food, f.Player.Combatant.Statuses[status].Definition.SourceType); // R-3.7-05
        Assert.True(f.Slot(0).IsEmpty && f.Slot(1).IsEmpty);
    }

    [Theory]
    [InlineData(80_000, 0, 0, UseItemReason.TooToxic)] // 80 + 25 > 100
    [InlineData(0, 80_000, 1, UseItemReason.TooFull)] // 80 + 30 > 100: you cannot overeat
    [InlineData(0, 0, 2, UseItemReason.ItemNotUsable)]
    [InlineData(0, 0, 5, UseItemReason.ItemSlotEmpty)]
    [InlineData(0, 0, 30, UseItemReason.ItemSlotInvalid)]
    public void A_refused_use_is_answered_at_once_and_changes_nothing(int toxicityMilli, int satietyMilli, byte slot, UseItemReason reason)
    {
        var f = new Fixture([Row(0, HpPotion, 1), Row(1, Bread, 1), Row(2, Horn, 1)], toxicityMilli, satietyMilli);

        f.Harness.SendUseItem(1, 7, slot);
        f.Harness.Ticks(1);

        var result = Assert.Single(f.Results);
        Assert.Equal((7u, reason), (result.RequestId, result.Reason));
        Assert.False(f.Player.Actions.IsBusy); // no action started
        Assert.False(f.Inventory.HasUnsaved(f.CharacterId));
    }

    [Fact]
    public void Exactly_reaching_the_toxicity_cap_is_allowed()
    {
        var f = new Fixture([Row(0, HpPotion, 1)], toxicityMilli: 75_000);

        f.Harness.SendUseItem(1, 1, 0);
        f.Harness.Ticks(f.DrinkTicks);

        Assert.True(f.Slot(0).IsEmpty);
        Assert.Equal(100, f.Player.Consumption!.Toxicity(f.Now));
    }

    [Fact]
    public void A_stack_a_craft_reserved_is_refused_with_ItemReserved_and_one_left_unreserved_can_still_be_drunk()
    {
        // WP-M3-2's reservations (spec 4.1, R-5.3-03): reserved units cannot be used, at the request or the effect point.
        var f = new Fixture([Row(0, HpPotion, 2)]);
        var at = new InventoryLocation((byte)f.Items.NormalBag.Code, 0);
        Assert.Equal(ReserveResult.Ok, f.Inventory.Reserve(f.CharacterId, "craft:test", [new ReserveItem(at, 2)]));

        f.Harness.SendUseItem(1, 1, 0);
        f.Harness.Ticks(1);
        Assert.Equal(UseItemReason.ItemReserved, Assert.Single(f.Results).Reason);
        Assert.False(f.Player.Actions.IsBusy);

        f.Inventory.Release(f.CharacterId, "craft:test");
        Assert.Equal(ReserveResult.Ok, f.Inventory.Reserve(f.CharacterId, "craft:test", [new ReserveItem(at, 1)]));
        f.Harness.SendUseItem(1, 2, 0);
        f.Harness.Ticks(f.DrinkTicks);
        Assert.Equal(1, f.Slot(0).Count); // the unreserved unit was drunk, the reserved one stays
    }

    [Fact]
    public void A_second_use_is_refused_while_the_first_runs_and_the_cooldown_refuses_the_next_drink()
    {
        var f = new Fixture([Row(0, HpPotion, 3)]);

        f.Harness.SendUseItem(1, 1, 0);
        f.Harness.SendUseItem(1, 2, 0);
        f.Harness.Ticks(1);
        Assert.Equal((2u, UseItemReason.UseInProgress), (Assert.Single(f.Results).RequestId, f.Results[0].Reason));

        f.Harness.Ticks(f.Actions.Find("use.drink")!.TotalTicks + 1); // the drink ended, 5 s of cooldown have not
        f.Harness.SendUseItem(1, 3, 0);
        f.Harness.Ticks(1);
        Assert.Equal((3u, UseItemReason.OnCooldown), (f.Results[^1].RequestId, f.Results[^1].Reason));
        Assert.Equal(2, f.Slot(0).Count);
    }

    [Fact]
    public void A_use_during_another_action_or_a_flinch_is_refused_with_ActionBusy()
    {
        var f = new Fixture([Row(0, HpPotion, 1)]);
        ActionMachine.Interrupt(f.Player.Actions, InterruptKind.Flinch, 500);

        f.Harness.SendUseItem(1, 1, 0);
        f.Harness.Ticks(1);

        Assert.Equal(UseItemReason.ActionBusy, Assert.Single(f.Results).Reason);
        Assert.Equal(1, f.Slot(0).Count);
        Assert.Empty(f.Transport.ActionResultsFor(1)); // answered by its UseItemResult only
    }

    [Fact]
    public void A_flinch_in_startup_interrupts_the_drink_like_a_skill_and_uses_nothing()
    {
        var f = new Fixture([Row(0, HpPotion, 1)]);
        f.Hurt(50);
        int hp = f.Hp;

        f.Harness.SendUseItem(1, 1, 0);
        f.Harness.Ticks(3);
        Assert.True(ActionMachine.Interrupt(f.Player.Actions, InterruptKind.Flinch, 300)); // a hit in startup
        f.Harness.Ticks(f.DrinkTicks);

        Assert.Equal(UseItemReason.UseInterrupted, Assert.Single(f.Results).Reason);
        Assert.Equal(1, f.Slot(0).Count);
        Assert.Equal(hp, f.Hp);
        Assert.Equal(0, f.Player.Consumption!.Toxicity(f.Now));
        Assert.False(f.Inventory.HasUnsaved(f.CharacterId));
    }

    [Fact]
    public void A_stack_moved_away_before_the_effect_point_is_not_used()
    {
        var f = new Fixture([Row(0, HpPotion, 1)]);

        f.Harness.SendUseItem(1, 1, 0);
        f.Harness.Ticks(2);
        f.Harness.SendMove(1, 9, fromSlot: 0, toSlot: 4);
        f.Harness.Ticks(f.DrinkTicks);

        Assert.Equal(UseItemReason.ItemSlotEmpty, Assert.Single(f.Results).Reason);
        Assert.Equal(1, f.Slot(4).Count); // never duplicated, never lost
    }

    // ---- The four transaction tests of rule 5 (CLAUDE.md), world level. ----

    [Fact]
    public void A_double_submit_uses_one_potion()
    {
        var f = new Fixture([Row(0, HpPotion, 3)]);

        f.Harness.SendUseItem(1, 4, 0);
        f.Harness.SendUseItem(1, 4, 0); // the same request again (a client retry before the answer)
        f.Harness.Ticks(f.DrinkTicks);
        f.Save();

        Assert.Equal(2, f.Slot(0).Count);
        var result = Assert.Single(f.Results);
        Assert.Equal((4u, UseItemReason.None), (result.RequestId, result.Reason));
        Assert.Single(f.Used.Events);
        Assert.Equal(25, f.Player.Consumption!.Toxicity(f.Now));
    }

    [Fact]
    public void A_retry_after_the_answer_gets_the_first_answer_again_and_uses_nothing_more()
    {
        var f = new Fixture([Row(0, HpPotion, 3)]);
        f.Harness.SendUseItem(1, 4, 0);
        f.Harness.Ticks(f.DrinkTicks);
        f.Save();
        Assert.Single(f.Results);

        f.Harness.Clock.Advance(10_000); // past the cooldown: only the key stops it
        f.Harness.Ticks(200);
        f.Harness.SendUseItem(1, 4, 0);
        f.Harness.Ticks(f.DrinkTicks);
        f.Save();

        Assert.Equal(2, f.Results.Count);
        Assert.All(f.Results, r => Assert.Equal((4u, f.NetId(HpPotion), UseItemReason.None), (r.RequestId, r.ItemNetId, r.Reason)));
        Assert.Equal(2, f.Slot(0).Count);
        Assert.Single(f.Used.Events);
        Assert.False(f.Player.Actions.IsBusy); // the repeat started no drink
    }

    [Fact]
    public void A_failed_save_is_retried_and_the_use_is_confirmed_once_after_the_retry_commits()
    {
        var f = new Fixture([Row(0, HpPotion, 3)]);
        f.Harness.Saves.Outcome = SaveOutcome.Failed;

        f.Harness.SendUseItem(1, 1, 0);
        f.Harness.Ticks(f.DrinkTicks);
        f.Save();
        Assert.Empty(f.Results); // the save failed: not confirmed
        Assert.Equal(3, f.Harness.Characters.Items[f.CharacterId].Single().Count);

        f.Harness.Saves.Outcome = SaveOutcome.Saved;
        f.Save(); // the scheduler resends the identical batch

        Assert.Equal(UseItemReason.None, Assert.Single(f.Results).Reason);
        Assert.Equal(2, f.Harness.Characters.Items[f.CharacterId].Single().Count);
        Assert.Single(f.Used.Events);
    }

    [Fact]
    public void A_crash_before_the_commit_confirms_nothing_and_the_reloaded_character_still_has_the_potion()
    {
        var f = new Fixture([Row(0, HpPotion, 3)]);
        f.Harness.Saves.AutoComplete = false; // the save is written but never completes: the server dies with it in flight

        f.Harness.SendUseItem(1, 1, 0);
        f.Harness.Ticks(f.DrinkTicks);
        f.Save();

        Assert.Equal(2, f.Slot(0).Count); // the world had used it
        Assert.Empty(f.Results); // but never told the player
        Assert.Empty(f.Used.Events);
        var stored = f.Harness.Characters.Get(f.CharacterId);
        Assert.Equal(0, stored.State.ToxicityMilli);
        Assert.Equal(3, f.Harness.Characters.Items[f.CharacterId].Single().Count); // what a restarted server loads

        // A new server process loads what the database holds: three potions, no toxicity; one use takes exactly one.
        var restarted = new Fixture([Row(0, HpPotion, 3)]);
        restarted.Harness.SendUseItem(1, 1, 0);
        restarted.Harness.Ticks(restarted.DrinkTicks);
        restarted.Save();
        Assert.Equal(2, restarted.Harness.Characters.Items[restarted.CharacterId].Single().Count);
        Assert.Equal(UseItemReason.None, Assert.Single(restarted.Results).Reason);
    }

    [Fact]
    public void A_use_and_a_move_of_the_same_last_unit_in_one_tick_never_duplicate_or_lose_it()
    {
        var f = new Fixture([Row(0, HpPotion, 1)]);

        f.Harness.SendUseItem(1, 1, 0);
        f.Harness.SendMove(1, 2, fromSlot: 0, toSlot: 9); // concurrent access to the same unit
        f.Harness.Ticks(f.DrinkTicks);
        f.Save();

        Assert.Equal(1, f.Slot(9).Count); // the move ran first in arrival order; the drink found the slot empty
        Assert.Equal(UseItemReason.ItemSlotEmpty, Assert.Single(f.Results).Reason);
        Assert.Empty(f.Used.Events);
        Assert.Equal(1, f.Harness.Characters.Items[f.CharacterId].Sum(r => r.Count));
    }

    [Fact]
    public void Two_players_drinking_in_the_same_tick_each_use_their_own_potion()
    {
        var f = new Fixture([Row(0, HpPotion, 2)]);
        var second = f.Harness.Characters.Add(2, 0, new CharacterState(1, 0, "", 0, 0, 0, 0, 100, 10, 100));
        f.Harness.Characters.Items[second.CharacterId] = [Row(0, HpPotion, 2)];
        f.Harness.Join(2);
        f.Harness.Ticks(1);

        f.Harness.SendUseItem(1, 1, 0);
        f.Harness.SendUseItem(2, 1, 0); // the same request id on another connection: another key
        f.Harness.Ticks(f.DrinkTicks);
        f.Save();

        Assert.Equal(1, f.Harness.Characters.Items[f.CharacterId].Single().Count);
        Assert.Equal(1, f.Harness.Characters.Items[second.CharacterId].Single().Count);
        Assert.Equal(2, f.Used.Events.Count);
    }

    [Fact]
    public void A_reconnect_with_the_same_request_id_is_a_new_use()
    {
        var f = new Fixture([Row(0, HpPotion, 3)]);
        f.Harness.SendUseItem(1, 1, 0);
        f.Harness.Ticks(f.DrinkTicks);
        f.Save();

        f.Harness.Leave(1);
        f.Harness.Ticks(3);
        f.Player = f.Harness.Join(1);
        f.Harness.Ticks(1);
        f.Harness.SendUseItem(1, 1, 0); // the client's counter started again
        f.Harness.Ticks(f.DrinkTicks);
        f.Save();

        Assert.Equal(1, f.Slot(0).Count);
        Assert.Equal(2, f.Used.Events.Count);
    }

    [Fact]
    public void A_character_buys_and_drinks_an_hp_potion_in_combat_and_after_a_relogin_the_potion_is_gone_and_toxicity_stays()
    {
        // M3 plan WP-M3-3 "done": buy at the vendor, drink in combat, HP and TOX rise, relogin.
        var f = new Fixture([]);
        uint vendor = f.Harness.World.Entities.OfType<Npc>().Single(n => n.MarkerId == EconomyFixture.Vendor).Id;
        f.Harness.SendBuy(1, 1, vendor, f.NetId(HpPotion), 2, 50);
        f.Harness.Ticks(3); // class 1: saved at once
        Assert.Equal(OutpostReason.None, Assert.Single(f.Transport.BuyResultsFor(1)).Reason);

        f.Player.LastCombatAtMs = f.Now; // a monster just hit it: in combat (spec 12.7)
        f.Hurt(70);
        int hp = f.Hp;
        f.Harness.SendUseItem(1, 1, 0);
        f.Harness.Ticks(f.DrinkTicks);
        f.Save();

        Assert.Equal(UseItemReason.None, Assert.Single(f.Results).Reason);
        Assert.True(f.Hp > hp);
        Assert.Equal(25, f.Player.Consumption!.Toxicity(f.Now));

        f.Harness.Leave(1);
        f.Harness.Ticks(3);
        var player = f.Harness.Join(1);
        f.Harness.Ticks(1);
        Assert.Equal(1, f.Inventory.Current(player.CharacterId)[0].Count); // one bought, one drunk
        Assert.Equal(25, player.Consumption!.Toxicity(f.Now)); // saved and loaded (decay starts from the save time)
    }
}
