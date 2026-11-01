using GameServer.Crafting;
using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Tests.Economy;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;

namespace GameServer.Tests.Crafting;

/// <summary>
/// Gems in the world (spec 5.3 R-5.3-05, 9.13; WP-M3-9) with the repository tables: a request runs the jeweler's bench gate,
/// an operation is answered and published (<c>GemSocketed</c>, <c>GemRemoved</c>, <c>GemCompleted</c> with its seed) only
/// after its save committed, and the done check: a Jeweler sockets a ruby into the sword it wears, its attack rises by the
/// gem's row in the own stats, and a removal takes it back out.
/// </summary>
public class GemWorldTests
{
    private const string Bench = "test.npc.bench";
    private const string Forge = "test.npc.forge";
    private const string FarBench = "test.npc.bench.far";
    private const byte Normal = 1, Equipped = 5;
    private static readonly long Sword = ItemInstanceIds.Make(1, 1);

    private sealed class Fixture
    {
        public Fixture(ulong rngSeed = 0, string job = "jeweler")
        {
            var tables = TestCombat.Tables();
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<GemSocketed>(Log);
            Bus.Subscribe<GemRemoved>(Log);
            Bus.Subscribe<GemCompleted>(Log);
            var map = TestMaps.Map(TestMaps.Open().Grid,
            [
                TestMaps.SpawnMarker(0, 0), TestCombat.MonsterMarker(4_000, 0), EconomyFixture.Npc(Bench, 0, 200, "m1.bench"),
                EconomyFixture.Npc(Forge, 200, 0, "m1.forge"), EconomyFixture.Npc(FarBench, 5_000, 5_000, "m1.bench"),
            ]);
            Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus, rngSeed: rngSeed,
                combat: TestCombat.Rules(tables, TestCombat.Point()), items: ItemRules.From(tables)!,
                economy: EconomyRules.From(tables)!, progression: ProgressionRules.From(tables)!, jobs: JobRules.From(tables)!,
                forge: ForgeRules.From(tables)!, gems: GemRules.From(tables)!);
            var character = Harness.Characters.Add(1, 0, new CharacterState(12, 0, "", 0, 0, 0, 0, 100, 50, 100));
            Harness.Characters.Items[character.CharacterId] =
            [
                new ItemStackRow(Normal, 0, "m3.rubyT1", 0, false, 3),
                new ItemStackRow(Normal, 1, "m3.roughGemT1", 0, false, 4),
            ];
            Harness.Characters.Instances[character.CharacterId] =
            [
                new ItemInstanceRow(Sword, Equipped, 0, "m3.forgedSword", 0, false, false, ItemInstanceIds.NewProperties(1)),
            ];
            Harness.Characters.JobRecords[character.CharacterId] = new JobRecord(
                [new JobProgressRow(job, JobSlot.Main, 1, 0, 0, "{\"v\":1,\"recent\":[]}")]);
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

        public void Socket(uint requestId, string station = Bench)
        {
            Harness.SendGem(1, requestId, NpcId(station), GemOp.Socket, Equipped, 0, Sword, gemSlot: 0);
            Harness.Ticks(1);
        }

        public void Remove(uint requestId)
        {
            Harness.SendGem(1, requestId, NpcId(Bench), GemOp.Remove, Equipped, 0, Sword, gemIndex: 0);
            Harness.Ticks(1);
        }

        /// <summary>The gem operation's save (issued the next tick), its completion, the stats dispatch and phase 10.</summary>
        public void Commit()
        {
            Harness.Ticks(2);
            Harness.Clock.Advance(1_000);
            Harness.Ticks(3);
        }

        public int Stat(string id) => Player.Combatant!.Stats.Get(Harness.World.Combat!.Config.Stats.IdFor(id));
    }

    internal sealed class EventLog : IEventConsumer<GemSocketed>, IEventConsumer<GemRemoved>, IEventConsumer<GemCompleted>
    {
        public List<object> Events { get; } = [];
        public void Handle(in EventEnvelope envelope, in GemSocketed payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in GemRemoved payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in GemCompleted payload) => Events.Add(payload);
    }

    [Fact]
    public void A_request_runs_the_bench_gate()
    {
        var f = new Fixture();
        f.Socket(1, Forge); // a forge is no jeweler's bench
        f.Socket(2, FarBench); // out of reach
        var answers = f.Transport.GemResultsFor(1);
        Assert.Equal(new[] { GemReason.StationOutOfReach, GemReason.StationOutOfReach }, answers.Select(a => a.Reason));

        var smith = new Fixture(job: "blacksmith");
        smith.Socket(1);
        Assert.Equal(GemReason.JobRequired, smith.Transport.GemResultsFor(1)[^1].Reason);
    }

    [Fact]
    public void An_operation_is_answered_and_published_only_after_its_save_commits()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;
        f.Socket(1);
        f.Harness.Ticks(2); // the save is issued, not completed

        Assert.Empty(f.Transport.GemResultsFor(1));
        Assert.Empty(f.Log.Events);
        var save = f.Harness.Saves.Jobs.Single(j => j.Craft is { Ops.Count: > 0 });
        Assert.Equal(SaveReason.Gem, save.Reason);
        var op = Assert.Single(save.Craft!.Ops);
        Assert.Equal(CraftOpKind.Socket, op.Kind);
        Assert.Contains(save.Inventory!.Ops, o => o.OpKey == op.OpKey + ":gem"); // one transaction (rule 5)

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        var answer = f.Transport.GemResultsFor(1)[^1];
        Assert.Equal((1u, GemReason.None), (answer.RequestId, answer.Reason));
        var socketed = Assert.Single(f.Log.Events.OfType<GemSocketed>());
        Assert.Equal(("jeweler", answer.Success, Sword), (socketed.JobId, socketed.Success, socketed.InstanceId));
        Assert.Equal(op.Seed, Assert.Single(f.Log.Events.OfType<GemCompleted>()).Seed); // the seed reaches telemetry (rule 6)
        Assert.Single(f.Harness.Characters.CraftOps);
    }

    [Fact]
    public void A_jeweler_sockets_a_ruby_into_the_worn_sword_its_attack_shows_it_and_a_removal_takes_it_back()
    {
        // The first world seed whose first socket succeeds (T1: 80 %); the seed alone decides (rule 6).
        Fixture? f = null;
        for (ulong seed = 0; seed < 20 && f == null; seed++)
        {
            var candidate = new Fixture(seed);
            int before = candidate.Stat("atk");
            candidate.Socket(1);
            candidate.Commit();
            var answer = candidate.Transport.GemResultsFor(1)[^1];
            Assert.Equal(GemReason.None, answer.Reason);
            if (!answer.Success)
            {
                Assert.Equal(before, candidate.Stat("atk")); // a lost gem leaves the sword as it was
                continue;
            }
            Assert.Equal(before + 4, candidate.Stat("atk")); // GemDef m3.rubyT1: atk +4, source Gem
            f = candidate;
        }
        Assert.NotNull(f);
        Assert.True(f!.Harness.World.Inventory!.TryGetProperties(f.CharacterId, Sword, out var properties));
        Assert.Equal(new[] { "m3.rubyT1" }, properties.Gems);
        Assert.NotEmpty(f.Transport.OwnStatsFor(1));
        int socketedAtk = f.Stat("atk");

        f.Remove(2);
        f.Commit();
        var removed = f.Transport.GemResultsFor(1)[^1];
        Assert.Equal((GemReason.None, GemOp.Remove, (ushort)2), (removed.Reason, removed.Op, removed.CostCount));
        Assert.Equal(socketedAtk - 4, f.Stat("atk"));
        Assert.Single(f.Log.Events.OfType<GemRemoved>());
    }
}
