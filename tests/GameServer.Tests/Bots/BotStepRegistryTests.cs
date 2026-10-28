using BotClient;
using BotClient.Brain;
using BotClient.Loop;
using BotClient.World;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
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
        Assert.Equal(BotBehaviour.LoopStepIds, loop.Steps.Select(s => s.Id));
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
            foreach (var step in new[] { "sell", "buy", "rankUp", "equip" })
                Assert.Equal(2, context.Counters.Loop[step + ".done"]);
            Assert.Equal(2, context.Counters.Loop["sell.nothingToSell"]);
            Assert.Equal(2, context.Counters.Loop["buy.noGold"]);
            Assert.Equal(2, context.Counters.Loop["rankUp.waiting"]);
            Assert.Equal(2, context.Counters.Loop["equip.nothing"]);
        }
        Assert.Same(log, secondLog);
        Assert.Equal(2, log.Count); // once per run, not per bot and round
        // WP-M2-6: the run step plays, but a bot that never got a RunStatus (nothing connected here) has no run to read.
        Assert.Contains(log, l => l.Contains("step 'run' not available yet (no RunStatus", StringComparison.Ordinal));
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
            new ClientInventory(data.Tables), new BotOwnState(), new BotCounters(), registry, _ => true, line => lines.Add(line));
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
