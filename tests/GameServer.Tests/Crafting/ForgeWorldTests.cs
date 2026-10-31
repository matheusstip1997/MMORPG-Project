using GameServer.Crafting;
using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Tests.Economy;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;

namespace GameServer.Tests.Crafting;

/// <summary>
/// The forge in the world (spec 5.3, 9.13; WP-M3-6) with the repository tables: a station opens the window, every request runs
/// the station gate, a result is answered and published only after its save committed (rule 5), leaving the station,
/// entering combat or disconnecting releases a running session, and the done check end to end: a Blacksmith with ore and a
/// hide refines two ingots, forges a sword at the forge, equips it, and its crafted stats raise the own stats.
/// </summary>
public class ForgeWorldTests
{
    private const string Forge = "test.npc.forge";
    private const string Bench = "test.npc.bench";
    private const string FarForge = "test.npc.forge.far";
    private const byte Normal = 1, Safe = 2, Equipped = 5;

    private sealed class Fixture
    {
        public Fixture()
        {
            var tables = TestCombat.Tables();
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<Crafted>(Log);
            Bus.Subscribe<Refined>(Log);
            Bus.Subscribe<ForgeCompleted>(Log);
            var map = TestMaps.Map(TestMaps.Open().Grid,
            [
                TestMaps.SpawnMarker(0, 0), TestCombat.MonsterMarker(4_000, 0), EconomyFixture.Npc(Forge, 200, 0, "m1.forge"),
                EconomyFixture.Npc(Bench, 0, 200, "m1.bench"), EconomyFixture.Npc(FarForge, 5_000, 5_000, "m1.forge"),
            ]);
            Rules = ForgeRules.From(tables)!;
            Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus,
                combat: TestCombat.Rules(tables, TestCombat.Point()), items: ItemRules.From(tables)!,
                economy: EconomyRules.From(tables)!, progression: ProgressionRules.From(tables)!, jobs: JobRules.From(tables)!,
                forge: Rules);
            var character = Harness.Characters.Add(1, 0, new CharacterState(12, 0, "", 0, 0, 0, 0, 100, 50, 100));
            Harness.Characters.Items[character.CharacterId] =
            [
                new ItemStackRow(Normal, 0, "m3.oreT1", 0, false, 4), // mined ore (WP-M3-5's harvest; seeded here)
                new ItemStackRow(Normal, 1, "m1.cyclopsHide", 0, false, 1), // a hide grip (decision 6)
            ];
            Harness.Characters.Instances[character.CharacterId] =
            [
                new ItemInstanceRow(ItemInstanceIds.Make(1, 1), Safe, 0, "m3.moldSword", 0, false, false, ItemInstanceIds.NewProperties(1)),
            ];
            Harness.Characters.JobRecords[character.CharacterId] = new JobRecord(
                [new JobProgressRow("blacksmith", JobSlot.Main, 1, 0, 0, "{\"v\":1,\"recent\":[]}")]);
            Player = Harness.Join(1);
            Harness.Ticks(1);
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public ForgeRules Rules { get; }
        public EventLog Log { get; } = new();
        public Player Player { get; }
        public ForgeService Service => Harness.World.Forge!;
        public FakeTransport Transport => Harness.Transport;
        public long CharacterId => Player.CharacterId;

        public uint NpcId(string markerId) => Harness.World.Entities.OfType<Npc>().Single(n => n.MarkerId == markerId).Id;

        public ForgeState Start(uint requestId, ForgeKind kind, int defNetId, string station = Forge, params (byte, byte)[] inputs)
        {
            Harness.SendForgeStart(1, requestId, NpcId(station), kind, defNetId, inputs);
            Harness.Ticks(1);
            return Transport.ForgeStatesFor(1)[^1];
        }

        public ForgeState Refine(uint requestId) => Start(requestId, ForgeKind.Recipe, 101, Forge, (Normal, 0));

        public void Finish(uint requestId)
        {
            Harness.SendForgeFinish(1, requestId);
            Harness.Ticks(1);
        }

        /// <summary>The forge's save (issued the next tick) and its completion.</summary>
        public void Commit() => Harness.Ticks(2);

        public int Unreserved(byte container, int slot) => Harness.World.Inventory!.Unreserved(CharacterId, new InventoryLocation(container, slot));

        public int Stat(string id) => Player.Combatant!.Stats.Get(Harness.World.Combat!.Config.Stats.IdFor(id));
    }

    internal sealed class EventLog : IEventConsumer<Crafted>, IEventConsumer<Refined>, IEventConsumer<ForgeCompleted>
    {
        public List<object> Events { get; } = [];
        public void Handle(in EventEnvelope envelope, in Crafted payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in Refined payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ForgeCompleted payload) => Events.Add(payload);
    }

    [Fact]
    public void A_station_opens_the_window_with_the_session_state()
    {
        var f = new Fixture();
        f.Harness.SendInteract(1, 1, f.NpcId(Forge), NpcInteraction.Forge);
        f.Harness.Ticks(1);

        var yes = f.Transport.InteractResultsFor(1)[^1];
        Assert.Equal((NpcInteraction.Forge, OutpostReason.None), (yes.Kind, yes.Reason));
        Assert.Equal(ForgePhase.None, f.Transport.ForgeStatesFor(1)[^1].Phase);
        f.Harness.SendInteract(1, 2, f.NpcId(Bench), NpcInteraction.JewelerBench);
        f.Harness.Ticks(1);
        Assert.Equal(OutpostReason.None, f.Transport.InteractResultsFor(1)[^1].Reason);
    }

    [Fact]
    public void A_start_runs_the_station_gate()
    {
        var f = new Fixture();
        Assert.Equal(ForgeReason.StationOutOfReach, f.Start(1, ForgeKind.Recipe, 101, FarForge, (Normal, 0)).Reason);
        Assert.Equal(ForgeReason.WrongStation, f.Start(2, ForgeKind.Recipe, 101, Bench, (Normal, 0)).Reason); // ore is a forge recipe
        Assert.Equal(4, f.Unreserved(Normal, 0));
        var state = f.Refine(3);
        Assert.Equal((ForgeReason.None, ForgePhase.Running, 3u), (state.Reason, state.Phase, state.RequestId));
        Assert.Equal(2, f.Unreserved(Normal, 0));
    }

    [Fact]
    public void A_result_is_answered_and_published_only_after_its_save_commits()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;
        f.Refine(1);
        f.Finish(2);
        f.Harness.Ticks(2); // the save is issued, not completed

        Assert.Empty(f.Transport.ForgeResultsFor(1));
        Assert.Empty(f.Log.Events);
        var save = f.Harness.Saves.Jobs.Single(j => j.Craft is { Ops.Count: > 0 });
        Assert.Equal(SaveReason.Craft, save.Reason);
        var op = Assert.Single(save.Craft!.Ops);
        Assert.Contains(save.Inventory!.Ops, o => o.OpKey == op.OpKey + ":in"); // one transaction (rule 5)

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        var answer = f.Transport.ForgeResultsFor(1)[^1];
        Assert.Equal((2u, ForgeReason.None, (ushort)1), (answer.RequestId, answer.Reason, answer.Count));
        Assert.Single(f.Log.Events.OfType<Refined>());
        var completed = Assert.Single(f.Log.Events.OfType<ForgeCompleted>());
        Assert.Equal(op.Seed, completed.Seed); // the seed reaches telemetry (rule 6)
        Assert.Equal(ForgePhase.Finished, f.Transport.ForgeStatesFor(1)[^1].Phase);
        Assert.Single(f.Harness.Characters.CraftOps);
    }

    [Fact]
    public void Leaving_the_station_ends_the_session_and_releases_the_inputs()
    {
        var f = new Fixture();
        f.Refine(1);
        f.Player.Movement = new MovementState(FixVec2.FromCm(3_000, 3_000), f.Player.Movement.Facing);
        f.Harness.Ticks(1);

        var state = f.Transport.ForgeStatesFor(1)[^1];
        Assert.Equal((ForgePhase.Cancelled, ForgeReason.LeftStation, 0u), (state.Phase, state.Reason, state.RequestId));
        Assert.Equal(4, f.Unreserved(Normal, 0));
        Assert.Null(f.Service.Session(f.CharacterId));
    }

    [Fact]
    public void Entering_combat_ends_the_session_and_releases_the_inputs()
    {
        var f = new Fixture();
        f.Refine(1);
        f.Player.LastCombatAtMs = f.Harness.Clock.UtcNowMs;
        f.Harness.Ticks(1);

        var state = f.Transport.ForgeStatesFor(1)[^1];
        Assert.Equal((ForgePhase.Cancelled, ForgeReason.InCombat), (state.Phase, state.Reason));
        Assert.Equal(4, f.Unreserved(Normal, 0));
        Assert.Equal(ForgeReason.InCombat, f.Refine(2).Reason); // and a start in combat is refused
    }

    [Fact]
    public void A_disconnect_releases_the_session_and_nothing_is_consumed()
    {
        var f = new Fixture();
        f.Refine(1);
        f.Harness.Leave(1);
        f.Harness.Ticks(1);

        Assert.Null(f.Service.Session(f.CharacterId));
        Assert.Contains(f.Harness.Characters.Items[f.CharacterId], s => s.DefId == "m3.oreT1" && s.Count == 4); // as saved
        Assert.Empty(f.Harness.Characters.CraftOps);
    }

    [Fact]
    public void A_cancel_request_releases_the_inputs()
    {
        var f = new Fixture();
        f.Refine(1);
        f.Harness.SendForgeCancel(1, 2);
        f.Harness.Ticks(1);

        var state = f.Transport.ForgeStatesFor(1)[^1];
        Assert.Equal((ForgePhase.Cancelled, ForgeReason.None, 2u), (state.Phase, state.Reason, state.RequestId));
        Assert.Equal(4, f.Unreserved(Normal, 0));
    }

    [Fact]
    public void A_blacksmith_forges_a_sword_from_ore_and_a_hide_equips_it_and_its_crafted_stats_show()
    {
        var f = new Fixture();
        f.Refine(1);
        f.Finish(2);
        f.Commit();
        f.Refine(3);
        f.Finish(4);
        f.Commit();
        var bag = f.Harness.World.Inventory!.Current(f.CharacterId);
        int ingotSlot = Enumerable.Range(0, bag.SlotCount).Single(s => !bag[s].IsEmpty && bag[s].ItemNetId == f.Rules.Item("m3.ingotT1")!.NetId);
        Assert.Equal(2, bag[ingotSlot].Count);

        var start = f.Start(5, ForgeKind.Mold, 1, Forge, (Normal, (byte)ingotSlot), (Normal, 1));
        Assert.Equal(ForgeReason.None, start.Reason);
        Assert.True(start.QualityMax > start.QualityMin);
        f.Harness.SendForgeAction(1, 6, 2); // hit
        f.Harness.Ticks(1);
        Assert.Equal(new byte[] { 2 }, f.Transport.ForgeStatesFor(1)[^1].Actions);
        f.Finish(7);
        f.Commit();

        var result = f.Transport.ForgeResultsFor(1)[^1];
        Assert.Equal((ForgeReason.None, (ushort)f.Rules.Item("m3.forgedSword")!.NetId), (result.Reason, result.OutputItemNetId));
        Assert.InRange(result.Quality, start.QualityMin, start.QualityMax);
        var crafted = f.Log.Events.OfType<Crafted>().Single();
        Assert.Equal(("blacksmith", f.CharacterId), (crafted.JobId, crafted.RecipientCharacterId));
        Assert.True(f.Harness.World.Inventory.TryGetProperties(f.CharacterId, result.InstanceId, out var properties));
        int atk = properties.CraftedStats.Single(s => s.StatId == "atk").Flat;
        Assert.True(atk >= 12); // two ingots and a hide, plus the quality bonus

        // Equip it (Safe bag equipment area to the main weapon slot): the stats follow the crafted snapshot.
        var inventory = f.Harness.World.Inventory.CurrentInventory(f.CharacterId);
        Assert.True(inventory.TryIndex(Safe, out int safeIndex));
        var safe = inventory.BagAt(safeIndex);
        int swordSlot = Enumerable.Range(0, safe.SlotCount).Single(s => safe[s].InstanceId == result.InstanceId);
        int atkBefore = f.Stat("atk");
        int ownStatsBefore = f.Transport.OwnStatsFor(1).Count;
        f.Harness.SendMove(1, 8, Safe, (byte)swordSlot, Equipped, 0);
        f.Harness.Ticks(2);
        f.Harness.Clock.Advance(1_000);
        f.Harness.Ticks(3); // the move's class 2 batch commits, EquipmentChanged is dispatched, phase 10 sends the stats
        Assert.Equal(InventoryMoveReason.None, f.Transport.MoveResultsFor(1)[^1].Reason);

        var worn = f.Harness.World.Inventory.GetEquippedBonuses(f.CharacterId);
        Assert.Contains(worn, b => b.StatId == "atk" && b.Flat == atk); // the crafted snapshot, not the item's fixed list
        Assert.NotEqual(atkBefore, f.Stat("atk"));
        Assert.True(f.Transport.OwnStatsFor(1).Count > ownStatsBefore);
    }
}
