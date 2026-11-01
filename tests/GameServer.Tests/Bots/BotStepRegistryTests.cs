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
using Shared.Simulation.Actions;
using Shared.Util;

namespace GameServer.Tests.Bots;

/// <summary>WP-M2-12: the M2 loop's step registry and the loop that plays the steps (spec 11.5).</summary>
public class BotStepRegistryTests
{
    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    [Fact]
    public void The_registry_has_one_step_per_loop_step_id_of_the_table()
    {
        var registry = BotStepRegistry.Default();
        Assert.Equal(BotBehaviour.LoopStepIds.OrderBy(i => i, StringComparer.Ordinal), registry.Ids.OrderBy(i => i, StringComparer.Ordinal));
        var loop = registry.NewLoop(Data.Value.Behaviour("m2-loop"));
        Assert.Equal(M2StepIds, loop.Steps.Select(s => s.Id));
        // WP-M3-12: the M3 loop has every known step but the exit run's level check; "buy:m3.pickaxeT1" is a second buy step.
        var m3 = registry.NewLoop(Data.Value.Behaviour("m3-loop"));
        Assert.Equal(BotBehaviour.LoopStepIds.Where(i => i != "level").OrderBy(i => i, StringComparer.Ordinal), m3.Steps.Select(s => s.Id).Distinct().OrderBy(i => i, StringComparer.Ordinal));
        Assert.Equal(2, m3.Steps.Count(s => s.Id == "buy"));
        Assert.False(m3.IsScript);
    }

    /// <summary>The M2 loop's steps (WP-M2-12), the first nine of <see cref="BotBehaviour.LoopStepIds"/>.</summary>
    internal static readonly string[] M2StepIds = ["fight", "pickup", "sell", "buy", "run", "learn", "allocate", "rankUp", "equip"];

    [Fact]
    public void The_m3_loop_plays_the_m2_steps_and_passes_every_m3_placeholder_logging_each_once_per_run()
    {
        // WP-M3-12: a round of m3-loop with nothing connected: the M2 steps have nothing to do, takeJob (real since WP-M3-1) waits for a job state, every other M3 step is "not yet",
        // the buy entry with an argument is what the contract says for the tables (failed with no gold once WP-M3-2's
        // pickaxe row and its vendor stock are there, not yet without them), and the loop goes round.
        var registry = BotStepRegistry.Default();
        var behaviour = Data.Value.Behaviour("m3-loop");
        var contexts = new[] { Context(registry, out var log), Context(registry, out _, log) };
        var loops = contexts.Select(_ => registry.NewLoop(behaviour)).ToArray();
        for (int round = 0; round < 2; round++)
        {
            for (int bot = 0; bot < 2; bot++)
            {
                var context = contexts[bot];
                context.View.NowMs += 50;
                loops[bot].Tick(context);
                Assert.Equal("fight", loops[bot].Current!.Id);
                context.Counters.Kills++;
                context.View.NowMs += 50;
                loops[bot].Tick(context); // fight done; useItem has nothing to do (WP-M3-3); pickup waits
                Assert.Equal("pickup", loops[bot].Current!.Id);
                context.View.NowMs += behaviour.LoopPickupIdleMs;
                loops[bot].Tick(context);
                Assert.Equal("fight", loops[bot].Current!.Id);
            }
        }

        string[] m3 = ["refine", "convert", "craft", "enchant", "socket"];
        bool pickaxeNotYet = BuyWithNothingConnected("m3.pickaxeT1") == StepOutcome.NotYet;
        foreach (var context in contexts)
        {
            Assert.Equal(2, context.Counters.LoopRounds);
            Assert.Equal(2, context.Counters.Loop["takeJob.waiting"]); // no JobState with nothing connected (WP-M3-1)
            Assert.Equal(2, context.Counters.Loop["gather.noJob"]); // WP-M3-5: real; no job held with nothing connected
            foreach (var step in m3)
                Assert.Equal(2, context.Counters.Loop[step + ".notAvailable"]);
            Assert.Equal(2, context.Counters.Loop[pickaxeNotYet ? "buy.notAvailable" : "buy.failed"]); // buy:m3.pickaxeT1
            Assert.Equal(2, context.Counters.Loop["buy.done"]); // the potion top-up: no gold
            Assert.Equal(pickaxeNotYet ? 2 : 4, context.Counters.Loop["buy.noGold"]); // the top-up, and the pickaxe once it plays
            Assert.Equal(2, context.Counters.Loop["useItem.off"]); // WP-M3-3: real; this context's brain plays m2-loop, which never drinks
            foreach (var step in new[] { "useItem", "sell", "allocate", "rankUp", "equip" })
                Assert.Equal(2, context.Counters.Loop[step + ".done"]);
        }
        foreach (var step in m3)
            Assert.Single(log, l => l.StartsWith($"step '{step}' not available yet", StringComparison.Ordinal));
        if (pickaxeNotYet)
            Assert.Single(log, l => l.StartsWith("step 'buy' not available yet", StringComparison.Ordinal));
        else
            Assert.DoesNotContain(log, l => l.StartsWith("step 'buy' ", StringComparison.Ordinal));
    }

    /// <summary>
    /// What <c>buy:&lt;item&gt;</c> ends with in a harness with nothing connected (0 gold), per the BuyStep contract
    /// (Loop/Steps/README.md): not yet while the item or a vendor stocking it is not in the tables, else failed (no gold).
    /// The loop tests read the tables instead of assuming which content has landed (WP-M3-2's pickaxe row).
    /// </summary>
    private static StepOutcome BuyWithNothingConnected(string itemId)
    {
        var data = Data.Value;
        bool sold = data.Rows<ItemDef>().Any(i => i.Id == itemId) && data.Rows<NpcVendorStock>().Any(s => s.ItemId == itemId);
        return sold ? StepOutcome.Failed : StepOutcome.NotYet;
    }

    [Theory]
    [InlineData("m3-exit-blacksmith")]
    [InlineData("m3-exit-jeweler")]
    public void An_exit_script_plays_each_entry_once_keeps_its_outcome_then_finishes_and_holds(string behaviourId)
    {
        // WP-M3-12: a level-12 bot (the seed) with nothing connected. level is done, takeJob (WP-M3-1) and learnJob (WP-M3-7) are done waiting for a job state, the M3 placeholders and the entries
        // whose content has not landed are not yet with their reasons, the M2 steps play, and the script ends after one pass.
        var registry = BotStepRegistry.Default();
        var behaviour = Data.Value.Behaviour(behaviourId);
        var context = Context(registry, out _);
        context.Own.Level = 12;
        var loop = registry.NewLoop(behaviour);
        Assert.True(loop.IsScript);
        for (int tick = 0; tick < 200 && !loop.Finished; tick++)
        {
            context.View.NowMs += 50;
            if (loop.Current?.Id == "fight")
            {
                context.Counters.Kills++;
                foreach (var monster in Data.Value.Rows<MonsterDef>()) // a camp entry counts its own monster's kills
                    context.Counters.KillsByDef[monster.NetId] = context.Counters.KillsOf(monster.NetId) + 1;
            }
            if (loop.Current?.Id == "pickup")
                context.View.NowMs += behaviour.LoopPickupIdleMs;
            loop.Tick(context);
        }

        Assert.True(loop.Finished);
        Assert.Null(loop.Current);
        Assert.Equal(1, context.Counters.LoopRounds);
        Assert.Equal(behaviour.LoopSteps, loop.Outcomes.Select(o => o.Entry));
        var outcomes = loop.Outcomes.GroupBy(o => o.Entry).ToDictionary(g => g.Key, g => g.First()); // a script may repeat "fight"
        Assert.Equal(StepOutcome.Done, outcomes["level:12"].Outcome);
        foreach (var outcome in loop.Outcomes.Where(o => o.Entry != "level:12"))
        {
            string entry = outcome.Entry;
            BotBehaviour.TryParseLoopStep(entry, out string id, out _);
            // takeJob (WP-M3-1), gather (WP-M3-5) and learnJob (WP-M3-7) are real: done waiting for their state.
            bool m2 = (M2StepIds.Contains(id) && entry != "equip:crafted") || id is "takeJob" or "gather" or "learnJob";
            if (entry == "useItem:m2.potionHpSmall")
                Assert.Equal(StepOutcome.Done, outcome.Outcome); // WP-M3-3: real; no potion held without the purchase
            else if (id == "buy" && BotBehaviour.TryParseLoopStep(entry, out _, out string item) && item.Length > 0)
            {
                // No gold without a sale (nothing connected), or not yet while the item's rows have not landed.
                var expected = BuyWithNothingConnected(item);
                Assert.True(outcome.Outcome == expected && outcome.Reason.Length > 0, $"{entry}: {outcome}, expected {expected}");
                if (expected == StepOutcome.Failed)
                    Assert.Contains("buy.noGold", outcome.Reason, StringComparison.Ordinal);
            }
            else if (entry == "equip:crafted")
                Assert.Equal(StepOutcome.Failed, outcome.Outcome); // WP-M3-6: real; nothing was crafted with nothing connected
            else if (m2)
                Assert.Equal(StepOutcome.Done, outcome.Outcome);
            else
                Assert.True(outcome.Outcome == StepOutcome.NotYet && outcome.Reason.Length > 0, $"{entry}: {outcome}");
        }
        Assert.Equal(1, context.Counters.Loop["takeJob.waiting"]); // the real step (WP-M3-1): no JobState with nothing connected

        // A finished script holds: no step runs again and nothing more is counted.
        long rounds = context.Counters.LoopRounds;
        var command = loop.Tick(context);
        Assert.Equal(context.Brain.Hold().Keys, command.Keys);
        Assert.Equal(rounds, context.Counters.LoopRounds);
    }

    [Fact]
    public void A_script_plays_a_repeated_entry_twice_and_a_step_gets_its_argument()
    {
        var registry = BotStepRegistry.Default();
        var script = new BotBehaviour { Id = "x", LoopOnce = true, LoopSteps = ["level:3", "enchant:m3.forgedSword", "level:3"], LoopStepTimeoutMs = 1_000 };
        var context = Context(registry, out _);
        context.Own.Level = 2;
        var loop = registry.NewLoop(script);
        context.View.NowMs += 50;
        loop.Tick(context);
        Assert.True(loop.Finished);
        Assert.Equal([StepOutcome.Failed, StepOutcome.NotYet, StepOutcome.Failed], loop.Outcomes.Select(o => o.Outcome));
        Assert.Equal(2, context.Counters.Loop["level.below"]);
        Assert.Contains("argument 'm3.forgedSword'", loop.Outcomes[1].Reason, StringComparison.Ordinal);
        Assert.Contains("level 2 is below 3", loop.Outcomes[0].Reason, StringComparison.Ordinal); // a failure keeps its reason
        Assert.Throws<ArgumentException>(() => registry.NewLoop(new BotBehaviour { Id = "y", LoopOnce = true, LoopSteps = ["fight:none"] }));
    }

    [Fact]
    public void A_loop_with_an_unknown_or_no_step_is_refused_at_start()
    {
        var registry = BotStepRegistry.Default();
        Assert.Throws<ArgumentException>(() => registry.NewLoop(new BotBehaviour { Id = "x", LoopSteps = ["fight", "dance"] }));
        Assert.Throws<ArgumentException>(() => registry.NewLoop(new BotBehaviour { Id = "x" }));
    }

    [Fact]
    public void Steps_play_in_order_and_hand_over_in_the_same_tick_then_the_loop_starts_a_new_round()
    {
        var calls = new List<string>();
        var a = new ScriptedStep("a", calls, t => t < 2 ? BotStepStatus.Running : BotStepStatus.Done);
        var b = new ScriptedStep("b", calls, _ => BotStepStatus.NotAvailable);
        var c = new ScriptedStep("c", calls, _ => BotStepStatus.Failed);
        var loop = new BotLoop([a, b, c], stepTimeoutMs: 10_000);
        var context = Context(BotStepRegistry.Default(), out _);

        for (int tick = 0; tick < 4; tick++)
        {
            context.View.NowMs = tick * 50L;
            loop.Tick(context);
        }

        // Ticks 1-2: a runs. Tick 3: a ends, b and c end at once, a begins again; tick 4: a runs.
        Assert.Equal(["a+", "a", "a", "a", "b+", "b", "c+", "c", "a+", "a"], calls);
        Assert.Equal(1, context.Counters.LoopRounds);
        Assert.Equal(1, context.Counters.Loop["a.done"]);
        Assert.Equal(1, context.Counters.Loop["b.notAvailable"]);
        Assert.Equal(1, context.Counters.Loop["c.failed"]);
        Assert.Same(a, loop.Current);
    }

    [Fact]
    public void A_step_running_past_the_timeout_is_ended_and_counted()
    {
        var calls = new List<string>();
        var stuck = new ScriptedStep("stuck", calls, _ => BotStepStatus.Running);
        var next = new ScriptedStep("next", calls, _ => BotStepStatus.Running);
        var loop = new BotLoop([stuck, next], stepTimeoutMs: 1_000);
        var context = Context(BotStepRegistry.Default(), out _);

        context.View.NowMs = 0;
        loop.Tick(context);
        context.View.NowMs = 1_000;
        loop.Tick(context);
        Assert.Same(stuck, loop.Current);
        context.View.NowMs = 1_050;
        loop.Tick(context);

        Assert.Same(next, loop.Current);
        Assert.Equal(1, context.Counters.Loop["stuck.timedOut"]);
    }

    [Fact]
    public void The_m2_loop_fights_until_a_kill_picks_up_then_plays_the_steps_with_nothing_to_do_and_passes_the_placeholders_logging_each_once_per_run()
    {
        var registry = BotStepRegistry.Default();
        var behaviour = Data.Value.Behaviour("m2-loop");
        var first = Context(registry, out var log);
        var second = Context(registry, out var secondLog, log);
        var loops = new[] { registry.NewLoop(behaviour), registry.NewLoop(behaviour) };
        var contexts = new[] { first, second };

        for (int round = 0; round < 2; round++)
        {
            for (int bot = 0; bot < 2; bot++)
            {
                var context = contexts[bot];
                context.View.NowMs += 50;
                loops[bot].Tick(context);
                Assert.Equal("fight", loops[bot].Current!.Id); // no kill yet
                context.Counters.Kills++;
                context.View.NowMs += 50;
                loops[bot].Tick(context);
                Assert.Equal("pickup", loops[bot].Current!.Id); // no drop seen yet: it waits loopPickupIdleMs
                context.View.NowMs += behaviour.LoopPickupIdleMs;
                loops[bot].Tick(context); // pickup ends; nothing to sell or buy, the placeholders pass, no rank known, no gear
                Assert.Equal("fight", loops[bot].Current!.Id);
            }
        }

        foreach (var context in contexts)
        {
            Assert.Equal(2, context.Counters.LoopRounds);
            Assert.Equal(2, context.Counters.Loop["fight.done"]);
            Assert.Equal(2, context.Counters.Loop["pickup.done"]);
            foreach (var placeholder in new[] { "run", "learn" })
                Assert.Equal(2, context.Counters.Loop[placeholder + ".notAvailable"]);
            // QA M2 wave 0 F2: the steps that play count "nothing to do" as done, never "not available".
            foreach (var step in new[] { "sell", "buy", "allocate", "rankUp", "equip" })
                Assert.Equal(2, context.Counters.Loop[step + ".done"]);
            Assert.Equal(2, context.Counters.Loop["sell.nothingToSell"]);
            Assert.Equal(2, context.Counters.Loop["buy.noGold"]);
            Assert.Equal(2, context.Counters.Loop["rankUp.waiting"]);
            Assert.Equal(2, context.Counters.Loop["allocate.waiting"]); // WP-M2-13: no ProgressionState here
            Assert.Equal(2, context.Counters.Loop["equip.nothing"]);
        }
        Assert.Same(log, secondLog);
        Assert.Equal(2, log.Count); // once per run, not per bot and round
        // WP-M2-6: the run step plays, but a bot that never got a RunStatus (nothing connected here) has no run to read.
        Assert.Contains(log, l => l.Contains("step 'run' not available yet (no RunStatus", StringComparison.Ordinal));
    }

    [Fact]
    public void The_fight_step_loots_its_own_kill_before_it_fights_on_and_ends_after_its_kills()
    {
        // WP-M2-12: a round of loopKillsPerRound kills loots each kill as it goes, so no drop is left behind.
        var data = Data.Value;
        var behaviour = data.Behaviour("m2-exit");
        var home = data.Markers[behaviour.HomeMarkerIds[0]];
        var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), behaviour, data.Grid, home, 200, new Dictionary<int, int>(),
            new Pcg32Rng(1));
        var view = new BotPerception
        {
            Self = home, NowMs = 1_000, LastKillAtMs = 1_000, HasResources = true, Ready = _ => true,
            Own = new OwnResources { Hp = 150, MaxHp = 150, Mp = 50, MaxMp = 50, Stamina = 100, MaxStamina = 100 },
        };
        var demon = MonsterDef.Find(data.Tables, "m1.demon")!;
        view.Monsters.Add(new SeenEntity(7, EntityKind.Monster, (ushort)demon.NetId, home + FixVec2.FromCm(60, 0), true, ActionState.Idle, 0, 0));
        view.Drops.Add(new SeenEntity(20, EntityKind.LootDrop, 1, home + FixVec2.FromCm(30, 0), true, ActionState.Idle, 0, 0));
        var counters = new BotCounters();
        var context = new BotStepContext(0, data, brain, view, new EntityRegistry(PredictionSettings.From(data.Tables)),
            new ClientInventory(data.Tables), new BotOwnState(), counters, BotStepRegistry.Default(), _ => true, _ => { });
        var step = new FightStep();
        step.Begin(context);

        var command = new BotCommand();
        Assert.Equal(BotStepStatus.Running, step.Tick(context, ref command));
        Assert.Equal(20u, command.PickupDropId); // its kill's drop first

        for (int i = 0; i < behaviour.LoopKillsPerRound; i++)
            counters.Kills++;
        Assert.Equal(BotStepStatus.Done, step.Tick(context, ref command));
    }

    [Fact]
    public void A_camp_fight_counts_only_kills_of_the_camps_own_monster()
    {
        // Wave 1 QA fix run: fight:1@m1.spawn.combat.boss ended on an alien's kill near the camp, so no hide came for the grip
        // (the script now fights wolves for a sinew; the rule holds for any camp).
        var context = Context(BotStepRegistry.Default(), out _);
        var boss = MonsterDef.Find(Data.Value.Tables, "m1.cyclopsBoss")!;
        var alien = MonsterDef.Find(Data.Value.Tables, "m1.alien")!;
        var step = new FightStep("1@m1.spawn.combat.boss");
        step.Begin(context);
        var command = new BotCommand();

        context.Counters.Kills++;
        context.Counters.KillsByDef[alien.NetId] = 1;
        Assert.Equal(BotStepStatus.Running, step.Tick(context, ref command));
        Assert.Equal(Data.Value.Markers["m1.spawn.combat.boss"], context.Brain.Home);

        context.Counters.Kills++;
        context.Counters.KillsByDef[boss.NetId] = 1;
        Assert.Equal(BotStepStatus.Done, step.Tick(context, ref command));
        Assert.NotEqual(Data.Value.Markers["m1.spawn.combat.boss"], context.Brain.Home); // its own home back
    }

    /// <summary>A bot's step context over the repository's tables, with nothing connected.</summary>
    private static BotStepContext Context(BotStepRegistry registry, out List<string> log, List<string>? shared = null)
    {
        var data = Data.Value;
        var behaviour = data.Behaviour("m2-loop");
        var home = data.Markers[behaviour.HomeMarkerIds[0]];
        var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), behaviour, data.Grid, home, 200, new Dictionary<int, int>(),
            new Pcg32Rng(1));
        var lines = log = shared ?? [];
        return new BotStepContext(0, data, brain, new BotPerception { Self = home }, new EntityRegistry(PredictionSettings.From(data.Tables)),
            new ClientInventory(data.Tables), new BotOwnState(), new BotCounters(), registry, _ => true, line => lines.Add(line))
        {
            Consumables = new ClientConsumables(data.Tables), // WP-M3-3
            Harvest = new ClientHarvest(data.Tables), Nodes = new ClientNodes(data.Tables), // WP-M3-5
        };
    }

    /// <summary>Records "id+" on begin and "id" on each tick; the script gets the ticks since its begin.</summary>
    private sealed class ScriptedStep(string id, List<string> calls, Func<int, BotStepStatus> script) : IBotStep
    {
        private int _ticks;

        public string Id => id;

        public void Begin(BotStepContext context)
        {
            calls.Add(id + "+");
            _ticks = 0;
        }

        public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
        {
            calls.Add(id);
            return script(_ticks++);
        }

        public void OnReliable(BotStepContext context, ReliableMessageId messageId, ReadOnlySpan<byte> message) => calls.Add(id + "!");
    }
}
