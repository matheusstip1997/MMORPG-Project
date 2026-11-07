using BotClient;
using BotClient.Brain;
using BotClient.Loop;
using BotClient.Loop.Steps;
using BotClient.World;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Util;

namespace GameServer.Tests.Bots;

/// <summary>
/// The loop's useItem step (WP-M3-3, spec 3.4 consumable use; BotClient README contract): below the behaviour's
/// <c>loopDrinkBelowPercent</c> it drinks the HP potion it holds, plays the drink in the predictor and waits for the answer.
/// </summary>
public class UseItemStepTests
{
    private static readonly Lazy<BotData> Data = new(() => BotData.Load(Support.RepositoryPaths.Root()));

    private sealed class Setup
    {
        public Setup(int hp, params InventorySlotEntry[] bag)
        {
            var data = Data.Value;
            var behaviour = data.Behaviour("m3-loop"); // loopDrinkBelowPercent 50, loopBuyItemId the small HP potion
            var home = data.Markers[behaviour.HomeMarkerIds[0]];
            var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), behaviour, data.Grid, home, 200, new Dictionary<int, int>(),
                new Pcg32Rng(1));
            View = new BotPerception
            {
                Self = home, NowMs = 1_000, HasResources = true, ActionIdle = true,
                Own = new OwnResources { Hp = hp, MaxHp = 150, Mp = 50, MaxMp = 50, Stamina = 100, MaxStamina = 100 },
            };
            Inventory = new ClientInventory(data.Tables);
            Inventory.Apply(ReliableMessages.Write(ReliableMessageId.InventoryContents,
                new InventoryContents { Revision = 1, SlotCount = 30, Slots = bag }), out _);
            Consumables = new ClientConsumables(data.Tables);
            Context = new BotStepContext(0, data, brain, View, new EntityRegistry(PredictionSettings.From(data.Tables)), Inventory,
                new BotOwnState(), new BotCounters(), BotStepRegistry.Default(), message => { Sent.Add(message); return true; }, _ => { })
            {
                Consumables = Consumables,
                PlayAction = id => Played.Add(id),
            };
        }

        public BotPerception View { get; }
        public ClientInventory Inventory { get; }
        public ClientConsumables Consumables { get; }
        public BotStepContext Context { get; }
        public List<byte[]> Sent { get; } = [];
        public List<string> Played { get; } = [];

        public BotStepStatus Tick(UseItemStep step)
        {
            var command = new BotCommand();
            return step.Tick(Context, ref command);
        }
    }

    private static ushort NetId(string itemId) => (ushort)ItemDef.Find(Data.Value.Tables, itemId)!.NetId;

    private static InventorySlotEntry Slot(byte slot, string itemId, ushort count) =>
        new() { Slot = slot, ItemNetId = NetId(itemId), Count = count };

    [Fact]
    public void Below_the_threshold_it_drinks_the_hp_potion_plays_the_drink_and_waits_for_the_answer()
    {
        var s = new Setup(hp: 40, Slot(0, "m2.breadLoaf", 3), Slot(4, "m2.potionHpSmall", 2));
        var step = new UseItemStep();
        step.Begin(s.Context);

        Assert.Equal(BotStepStatus.Running, s.Tick(step));
        var sent = Assert.Single(s.Sent);
        Assert.True(ReliableMessages.TryRead(sent, out UseItemRequest request));
        Assert.Equal((0, 4), (request.Container, request.Slot)); // the potion, not the bread
        Assert.Equal(["use.drink"], s.Played);
        Assert.Equal(BotStepStatus.Running, s.Tick(step)); // no answer yet

        s.Consumables.Apply(ReliableMessages.Write(ReliableMessageId.UseItemResult,
            new UseItemResult { RequestId = request.RequestId, ItemNetId = NetId("m2.potionHpSmall"), Reason = UseItemReason.None }), out _);
        Assert.Equal(BotStepStatus.Done, s.Tick(step));
        Assert.Equal(1, s.Context.Counters.Loop["useItem.used"]);
    }

    [Fact]
    public void A_refusal_is_counted_by_reason_and_fails_the_step()
    {
        var s = new Setup(hp: 40, Slot(0, "m2.potionHpSmall", 1));
        var step = new UseItemStep();
        step.Begin(s.Context);
        s.Tick(step);
        Assert.True(ReliableMessages.TryRead(s.Sent[0], out UseItemRequest request));

        s.Consumables.Apply(ReliableMessages.Write(ReliableMessageId.UseItemResult,
            new UseItemResult { RequestId = request.RequestId, Reason = UseItemReason.TooToxic }), out _);

        Assert.Equal(BotStepStatus.Failed, s.Tick(step));
        Assert.Equal(1, s.Context.Counters.Loop["useItem.refused.TooToxic"]);
    }

    [Theory]
    [InlineData(100, "useItem.notNeeded")] // 100 of 150 is above half
    [InlineData(40, "useItem.none")] // no HP potion held
    public void Nothing_to_drink_is_done_at_once_with_a_counter(int hp, string counter)
    {
        var s = new Setup(hp, Slot(0, "m2.breadLoaf", 3));
        var step = new UseItemStep();
        step.Begin(s.Context);

        Assert.Equal(BotStepStatus.Done, s.Tick(step));
        Assert.Equal(1, s.Context.Counters.Loop[counter]);
        Assert.Empty(s.Sent);
    }

    [Fact]
    public void It_waits_for_its_own_action_to_end_before_it_drinks()
    {
        var s = new Setup(hp: 40, Slot(0, "m2.potionHpSmall", 1));
        s.View.ActionIdle = false;
        var step = new UseItemStep();
        step.Begin(s.Context);

        Assert.Equal(BotStepStatus.Running, s.Tick(step));
        Assert.Empty(s.Sent);
        s.View.ActionIdle = true;
        s.Tick(step);
        Assert.Single(s.Sent);
    }

    [Fact]
    public void A_script_entry_with_an_item_uses_that_item_now_whatever_the_hp()
    {
        // The M3 exit script's "useItem:m2.potionHpSmall" (WP-M3-12): drink the bought potion, in combat after the fight.
        var s = new Setup(hp: 150, Slot(2, "m2.breadLoaf", 1), Slot(6, "m2.potionHpSmall", 1));
        var step = new UseItemStep("m2.potionHpSmall");
        step.Begin(s.Context);

        Assert.Equal(BotStepStatus.Running, s.Tick(step));
        Assert.True(ReliableMessages.TryRead(Assert.Single(s.Sent), out UseItemRequest request));
        Assert.Equal(6, request.Slot);

        var notConsumable = new UseItemStep("m1.demonHorn");
        notConsumable.Begin(s.Context);
        Assert.Equal(BotStepStatus.Failed, s.Tick(notConsumable));
        Assert.Equal(1, s.Context.Counters.Loop["useItem.notConsumable"]);
    }

    /// <summary>The combat form's context: <see cref="ClientStats"/> beside the consumables (a separate context: init properties).</summary>
    private sealed class CombatSetup
    {
        public const string Camp = "m1.spawn.combat.a";

        public CombatSetup(int hp, params InventorySlotEntry[] bag)
        {
            Inner = new Setup(hp, bag);
            Stats = new ClientStats(Data.Value.Tables);
            Context = new BotStepContext(0, Data.Value, Inner.Context.Brain, Inner.View, new EntityRegistry(PredictionSettings.From(Data.Value.Tables)),
                Inner.Inventory, new BotOwnState(), new BotCounters(), BotStepRegistry.Default(), message => { Inner.Sent.Add(message); return true; }, _ => { })
            {
                Consumables = Inner.Consumables,
                Stats = Stats,
                PlayAction = id => Inner.Played.Add(id),
            };
            SetToxicity(0);
        }

        public Setup Inner { get; }
        public ClientStats Stats { get; }
        public BotStepContext Context { get; }

        public void SetToxicity(int milli)
        {
            var stats = new OwnStats { Version = 1, Values = new int[Data.Value.Rows<StatDef>().Count()], ToxicityMilli = milli };
            Assert.True(Stats.Apply(ReliableMessages.Write(ReliableMessageId.OwnStats, stats), Inner.View.NowMs));
        }

        public BotStepStatus Tick(UseItemStep step)
        {
            var command = new BotCommand();
            return step.Tick(Context, ref command);
        }

        public void Answer(UseItemReason reason = UseItemReason.None)
        {
            Assert.True(ReliableMessages.TryRead(Inner.Sent[^1], out UseItemRequest request));
            Inner.Consumables.Apply(ReliableMessages.Write(ReliableMessageId.UseItemResult,
                new UseItemResult { RequestId = request.RequestId, ItemNetId = NetId("m2.potionHpSmall"), Reason = reason }), out _);
        }

        public void SetHp(int hp) => Inner.View.Own = new OwnResources { Hp = hp, MaxHp = 150, Mp = 50, MaxMp = 50, Stamina = 100, MaxStamina = 100 };
    }

    [Fact]
    public void The_combat_form_fights_at_the_camp_until_hurt_in_combat_then_drinks_and_is_done_when_hp_and_toxicity_rose()
    {
        // WP-M3-12, plan section 1: "a vendor potion is drunk in combat: HP rises and TOX rises".
        var s = new CombatSetup(hp: 150, Slot(4, "m2.potionHpSmall", 1));
        var homeBefore = s.Context.Brain.Home;
        var step = new UseItemStep("m2.potionHpSmall@" + CombatSetup.Camp);
        step.Begin(s.Context);
        Assert.Equal(Data.Value.Markers[CombatSetup.Camp], s.Context.Brain.Home); // it fights at the camp

        Assert.Equal(BotStepStatus.Running, s.Tick(step)); // full HP, not in combat: no drink
        Assert.Empty(s.Inner.Sent);
        s.SetHp(90);
        Assert.Equal(BotStepStatus.Running, s.Tick(step)); // hurt, not in combat: still no drink
        Assert.Empty(s.Inner.Sent);
        s.Inner.View.LastCombatAtMs = s.Inner.View.NowMs;
        s.SetHp(150);
        Assert.Equal(BotStepStatus.Running, s.Tick(step)); // in combat, unhurt: still no drink
        Assert.Empty(s.Inner.Sent);

        s.SetHp(90);
        Assert.Equal(BotStepStatus.Running, s.Tick(step));
        Assert.True(ReliableMessages.TryRead(Assert.Single(s.Inner.Sent), out UseItemRequest request));
        Assert.Equal(4, request.Slot);
        Assert.Equal(["use.drink"], s.Inner.Played);
        Assert.Equal(BotStepStatus.Running, s.Tick(step)); // no answer yet

        s.Answer();
        s.SetHp(70); // a hit lands during the drink: the rise counts from the lowest HP
        Assert.Equal(BotStepStatus.Running, s.Tick(step)); // used, nothing has shown yet
        s.SetHp(130); // +60, the potion's heal
        s.SetToxicity(25_000);
        Assert.Equal(BotStepStatus.Done, s.Tick(step));

        var counters = s.Context.Counters.Loop;
        Assert.Equal((1, 1, 90, 130, 25_000), (counters["useItem.inCombat"], counters["useItem.used"], counters["useItem.hpBefore"], counters["useItem.hpAfter"], counters["useItem.toxAfterMilli"]));
        Assert.Equal(homeBefore, s.Context.Brain.Home); // back to the camp of its level
    }

    [Theory]
    [InlineData(true, false, "useItem.noToxRise")]
    [InlineData(false, true, "useItem.noHpRise")]
    public void The_combat_form_fails_when_hp_or_toxicity_never_show_the_potion(bool hp, bool tox, string counter)
    {
        var s = new CombatSetup(hp: 90, Slot(0, "m2.potionHpSmall", 1));
        s.Inner.View.LastCombatAtMs = s.Inner.View.NowMs;
        var step = new UseItemStep("m2.potionHpSmall@" + CombatSetup.Camp);
        step.Begin(s.Context);
        s.Tick(step);
        s.Answer();
        if (hp)
            s.SetHp(150);
        if (tox)
            s.SetToxicity(25_000);

        Assert.Equal(BotStepStatus.Running, s.Tick(step)); // within the wait
        s.Inner.View.NowMs += UseItemStep.ConfirmMs;

        Assert.Equal(BotStepStatus.Failed, s.Tick(step));
        Assert.Equal(1, s.Context.Counters.Loop[counter]);
        Assert.NotEmpty(s.Context.LastReason);
    }

    [Fact]
    public void The_combat_form_fails_without_the_potion_and_on_a_refusal()
    {
        var none = new CombatSetup(hp: 90, Slot(0, "m2.breadLoaf", 1));
        var step = new UseItemStep("m2.potionHpSmall@" + CombatSetup.Camp);
        step.Begin(none.Context);
        Assert.Equal(BotStepStatus.Failed, none.Tick(step));
        Assert.Equal(1, none.Context.Counters.Loop["useItem.none"]);

        var refused = new CombatSetup(hp: 90, Slot(0, "m2.potionHpSmall", 1));
        refused.Inner.View.LastCombatAtMs = refused.Inner.View.NowMs;
        step = new UseItemStep("m2.potionHpSmall@" + CombatSetup.Camp);
        step.Begin(refused.Context);
        refused.Tick(step);
        refused.Answer(UseItemReason.TooToxic);
        Assert.Equal(BotStepStatus.Failed, refused.Tick(step));
        Assert.Equal(1, refused.Context.Counters.Loop["useItem.refused.TooToxic"]);
    }

    [Theory]
    [InlineData("@m1.spawn.combat.a")]
    [InlineData("m2.potionHpSmall@")]
    public void The_combat_form_needs_an_item_and_a_camp(string argument) =>
        Assert.Throws<ArgumentException>(() => new UseItemStep(argument));
}
