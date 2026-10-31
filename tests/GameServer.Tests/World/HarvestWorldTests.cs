using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Jobs;
using GameServer.Persistence;
using GameServer.Tests.Support;
using GameServer.World.Map;
using GameServer.World.Nodes;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using MoveKeys = Shared.Protocol.MoveKeys;
using Shared.Simulation.Actions;
using Shared.Simulation.Jobs;
using Shared.World;

namespace GameServer.Tests.World;

/// <summary>
/// Harvesting in the world tick by tick (spec 5.2 R-5.2-01, R-5.2-02, "M3 resource nodes"; WP-M3-5): the request starts the
/// node's harvest action (a channel) in phase 3; a movement input, a dodge, damage or the node depleted by someone else
/// breaks it; at its harvest effect the yield is rolled and added (class 2, reason Harvest) and the node depletes; the answer
/// and <see cref="Harvested"/> come only after the commit. The four transaction tests of rule 5 (double submit, retry, crash
/// in the middle, concurrent access) are here at the world level; the database level is <see cref="HarvestPersistenceTests"/>.
/// Numbers come from the placeholder rows of data/tables (NodeDef, NodeYieldEntry, HarvestSettings, the harvest actions).
/// </summary>
public class HarvestWorldTests
{
    private const string Near = "field1.node.ring1.a"; // T1 ore, 150 cm east of the spawn
    private const string Deep = "test.node.deep"; // T3 ore, 150 cm west: a T1 pickaxe is too low
    private const string Far = "test.node.far"; // T1 ore, 400 cm north: out of reach (reachCm 250)
    private const string Pickaxe = "m3.pickaxeT1";
    private const byte Normal = 1, Safe = 2;

    internal sealed class Fixture
    {
        public Fixture(bool job = true, bool pickaxe = true, IReadOnlyList<ItemStackRow>? bag = null, int players = 1)
        {
            Tables = TestCombat.Tables();
            Items = ItemRules.From(Tables)!;
            Actions = ActionCatalog.From(Tables);
            var full = NodeRules.From(Tables);
            Nodes = new NodeRules(full.Nodes,
            [
                new GatherNodes { MarkerId = Near, NodeId = "node.ore.t1" },
                new GatherNodes { MarkerId = Deep, NodeId = "node.ore.t3" },
                new GatherNodes { MarkerId = Far, NodeId = "node.ore.t1" },
            ], full.Settings, full.YieldTables, full.Harvest);
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<Harvested>(Log);
            var map = TestMaps.Map(TestMaps.Open().Grid,
            [
                TestMaps.SpawnMarker(0, 0), Node(Near, 150, 0), Node(Deep, -150, 0), Node(Far, 0, 400),
            ]);
            Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus, actions: Actions,
                combat: TestCombat.Rules(Tables), items: Items, nodes: Nodes, jobs: JobRules.From(Tables)!);
            for (int peer = 1; peer <= players; peer++)
                AddCharacter(peer, job, pickaxe, bag);
            for (int peer = 1; peer <= players; peer++)
                Joined.Add(Harness.Join(peer));
            Harness.Ticks(1);
        }

        public ConfigSet Tables { get; }
        public ItemRules Items { get; }
        public ActionCatalog Actions { get; }
        public NodeRules Nodes { get; }
        public EventBus Bus { get; }
        public EventLog Log { get; } = new();
        public WorldHarness Harness { get; }
        public List<Player> Joined { get; } = [];
        public Player Player => Joined[0];
        public long CharacterId => Player.CharacterId;
        public InventoryService Inventory => Harness.World.Inventory!;
        public HarvestService Harvests => Harness.World.Harvests!;
        public FakeTransport Transport => Harness.Transport;

        public void AddCharacter(int peer, bool job, bool pickaxe, IReadOnlyList<ItemStackRow>? bag)
        {
            var character = Harness.Characters.Add(peer, 0, new CharacterState(12, 0, "", 0, 0, 0, 0, 100, 50, 100));
            long id = character.CharacterId;
            Harness.Characters.Items[id] = bag ?? [];
            Harness.Characters.Instances[id] = pickaxe
                ? [new ItemInstanceRow(ItemInstanceIds.Make(id, 1), Safe, 0, Pickaxe, 0, false, false, ItemInstanceIds.NewProperties(id))]
                : [];
            if (job)
                Harness.Characters.JobRecords[id] = new JobRecord([new JobProgressRow("blacksmith", JobSlot.Main, 1, 0, 0, "{\"recent\":[]}")]);
        }

        public ResourceNode NodeAt(string markerId) =>
            Harness.World.Entities.OfType<ResourceNode>().Single(n => n.MarkerId == markerId);

        /// <summary>Ticks from the request until the harvest effect fired (startup and channel in ticks, plus the request's tick).</summary>
        public int HarvestTicks => Actions.Find("harvest.ore.t1")!.ChannelEnd + 1;

        /// <summary>Every unit in the Normal bag, the yield whatever entry it rolled.</summary>
        public int Units(long characterId)
        {
            var bag = Inventory.Current(characterId);
            int units = 0;
            for (int slot = 0; slot < bag.SlotCount; slot++)
                units += bag[slot].IsEmpty ? 0 : bag[slot].Count;
            return units;
        }

        public int StoredUnits(long characterId) =>
            Harness.Characters.Items.TryGetValue(characterId, out var rows) ? rows.Where(r => r.Container == Normal).Sum(r => r.Count) : 0;

        public ushort NetId(string itemId)
        {
            Assert.True(Items.TryGetItem(itemId, out var item));
            return (ushort)item.NetId;
        }

        public void Harvest(uint requestId, string markerId = Near, int peer = 1) =>
            Harness.SendHarvest(peer, requestId, NodeAt(markerId).Id);

        /// <summary>The next class 2 batch (every second) and its completion.</summary>
        public void Save()
        {
            Harness.Clock.Advance(1_000);
            Harness.Ticks(2);
        }

        public List<HarvestResult> Results(int peer = 1) => Transport.HarvestResultsFor(peer);

        public void Move(ushort sequence, MoveKeys keys = MoveKeys.W) =>
            Harness.SendInputs(1, TestWorlds.Frame(sequence, keys));
    }

    internal sealed class EventLog : IEventConsumer<Harvested>
    {
        public List<(Harvested Event, int RegionId)> Harvested { get; } = [];

        public void Handle(in EventEnvelope envelope, in Harvested payload) => Harvested.Add((payload, envelope.RegionId));
    }

    private static WorldMarker Node(string id, int xCm, int yCm) => new(id, MarkerKind.ResourceNode, FixVec2.FromCm(xCm, yCm), 0);

    private static ItemStackRow Row(int slot, string item, int count) => new(Normal, slot, item, 0, false, count);

    [Fact]
    public void A_blacksmith_mines_a_node_the_yield_and_the_depletion_come_at_the_effect_and_the_answer_after_the_commit()
    {
        var f = new Fixture();
        var node = f.NodeAt(Near);

        f.Harvest(1);
        f.Harness.Ticks(1);
        Assert.Equal(f.Actions.Find("harvest.ore.t1")!.Index, f.Player.Actions.ActionIndex); // the channel runs: a commitment
        f.Harness.Ticks(f.HarvestTicks - 2);
        Assert.Equal(0, f.Units(f.CharacterId)); // nothing before the effect point
        Assert.True(node.IsAvailable);

        f.Harness.Ticks(1);
        int units = f.Units(f.CharacterId);
        Assert.InRange(units, 1, 3); // the placeholder entries: ore 1-3, a gem or a stone 1, no bonus at job level 1
        Assert.False(node.IsAvailable); // depleted at once: no second channel can finish before the commit
        Assert.Empty(f.Results()); // not before the commit
        Assert.Empty(f.Log.Harvested);

        f.Save();
        var result = Assert.Single(f.Results());
        Assert.Equal((1u, node.Id, HarvestReason.None, (ushort)units), (result.RequestId, result.NodeEntityId, result.Reason, result.Count));
        var (harvested, region) = Assert.Single(f.Log.Harvested);
        Assert.Equal((f.CharacterId, f.Player.Id, "blacksmith", 1, node.Def.NetId, (int)result.ItemNetId, units),
            (harvested.CharacterId, harvested.EntityId, harvested.JobId, harvested.Tier, harvested.NodeDefNetId, harvested.ItemNetId,
                harvested.Count));
        Assert.Equal(node.RegionIndex, region); // demand counts per node type and region (R-4.2-03)
        Assert.NotEqual(0UL, harvested.Seed); // logged with the harvest (rule 6)
        Assert.StartsWith("harvest:test-run:", harvested.OpKey);
        Assert.Equal(units, f.StoredUnits(f.CharacterId));
        var job = Assert.Single(f.Harness.Saves.Jobs, j => j.Inventory != null && j.Inventory.Ops.Any(o => o.OpKey == harvested.OpKey));
        Assert.Equal(nameof(InventoryReason.Harvest), job.Inventory!.Ops.Single(o => o.OpKey == harvested.OpKey).Reason);
        Assert.Null(f.Player.Harvests.Current);
    }

    [Fact]
    public void The_answer_comes_right_before_the_contents_that_show_the_yield()
    {
        var f = new Fixture();
        f.Harvest(1);
        f.Harness.Ticks(f.HarvestTicks);
        int before = f.Transport.ReliableIdsFor(1).Count;
        f.Save();
        var ids = f.Transport.ReliableIdsFor(1).Skip(before).ToList();
        int answer = ids.IndexOf(ReliableMessageId.HarvestResult);
        Assert.True(answer >= 0);
        Assert.Equal(ReliableMessageId.InventoryContents, ids[answer + 1]);
    }

    [Fact]
    public void The_node_respawns_after_its_delay_on_the_world_clock()
    {
        var f = new Fixture();
        var node = f.NodeAt(Near);
        f.Harvest(1);
        f.Harness.Ticks(f.HarvestTicks);
        f.Save();
        Assert.False(node.IsAvailable);

        while (!node.IsAvailable && f.Harness.World.NowMs < node.RespawnAtMs + 1_000)
            f.Harness.Ticks(20);
        Assert.True(node.IsAvailable);

        f.Harvest(2); // and can be mined again
        f.Harness.Ticks(f.HarvestTicks);
        f.Save();
        Assert.Equal(HarvestReason.None, f.Results()[^1].Reason);
    }

    // ---- Refusals before the channel (spec 9.7 reason codes). ----

    [Fact]
    public void Without_the_job_the_harvest_is_refused_with_JobRequired()
    {
        var f = new Fixture(job: false);
        f.Harvest(1);
        f.Harness.Ticks(1);
        Assert.Equal(HarvestReason.JobRequired, Assert.Single(f.Results()).Reason);
        Assert.False(f.Player.Actions.IsBusy);
    }

    [Fact]
    public void Without_a_pickaxe_in_the_safe_bag_the_harvest_is_refused_with_ToolRequired()
    {
        var f = new Fixture(pickaxe: false);
        f.Harvest(1);
        f.Harness.Ticks(1);
        Assert.Equal(HarvestReason.ToolRequired, Assert.Single(f.Results()).Reason);
    }

    [Fact]
    public void A_t1_pickaxe_on_a_t3_node_is_refused_with_ToolTierTooLow()
    {
        var f = new Fixture();
        f.Harvest(1, Deep);
        f.Harness.Ticks(1);
        Assert.Equal(HarvestReason.ToolTierTooLow, Assert.Single(f.Results()).Reason);
    }

    [Fact]
    public void A_node_out_of_reach_or_not_a_node_or_depleted_is_refused()
    {
        var f = new Fixture();
        f.Harvest(1, Far);
        f.Harness.SendHarvest(1, 2, f.Player.Id);
        Assert.True(f.Harness.World.TryDepleteNode(f.NodeAt(Near).Id));
        f.Harvest(3);
        f.Harness.Ticks(1);
        Assert.Equal([HarvestReason.OutOfRange, HarvestReason.NodeNotFound, HarvestReason.NodeDepleted],
            f.Results().Select(r => r.Reason));
    }

    [Fact]
    public void A_full_bag_refuses_before_the_channel_starts()
    {
        var bag = Enumerable.Range(0, NormalSlots()).Select(slot => Row(slot, "m1.demonHorn", 99)).ToList();
        var f = new Fixture(bag: bag);
        f.Harvest(1);
        f.Harness.Ticks(1);
        Assert.Equal(HarvestReason.BagFull, Assert.Single(f.Results()).Reason);
        Assert.False(f.Player.Actions.IsBusy);
    }

    private static int NormalSlots() => ItemRules.From(TestCombat.Tables())!.NormalBag.Slots;

    [Fact]
    public void A_harvest_during_another_action_or_while_dead_is_refused()
    {
        var f = new Fixture();
        f.Harness.SendInputs(1, TestWorlds.Frame(1, actions: InputActions.Attack));
        f.Harness.Ticks(1);
        f.Harvest(1);
        f.Harness.Ticks(1);
        Assert.Equal(HarvestReason.ActionBusy, f.Results()[^1].Reason);

        f.Harness.Ticks(40);
        f.Player.Combatant!.Die(f.Harness.World.ServerTick);
        ActionMachine.Kill(f.Player.Actions);
        f.Harvest(2);
        f.Harness.Ticks(1);
        Assert.Equal(HarvestReason.HarvestWhileDead, f.Results()[^1].Reason);
    }

    // ---- The channel breaks on damage, a movement input or a dodge (user decision 5). ----

    [Fact]
    public void A_movement_input_breaks_the_channel_and_nothing_is_harvested()
    {
        var f = new Fixture();
        f.Harvest(1);
        f.Harness.Ticks(10);
        f.Move(1);
        f.Harness.Ticks(f.HarvestTicks);
        f.Save();

        Assert.Equal(HarvestReason.HarvestInterrupted, Assert.Single(f.Results()).Reason);
        Assert.Equal(0, f.Units(f.CharacterId));
        Assert.True(f.NodeAt(Near).IsAvailable);
        Assert.Empty(f.Log.Harvested);
    }

    [Fact]
    public void A_dodge_breaks_the_channel_and_nothing_is_harvested()
    {
        var f = new Fixture();
        f.Harvest(1);
        f.Harness.Ticks(10);
        f.Harness.SendInputs(1, TestWorlds.Frame(1, actions: InputActions.Dodge));
        f.Harness.Ticks(f.HarvestTicks);
        f.Save();

        Assert.Equal(HarvestReason.HarvestInterrupted, Assert.Single(f.Results()).Reason);
        Assert.Equal(0, f.Units(f.CharacterId));
        Assert.True(f.NodeAt(Near).IsAvailable);
    }

    [Fact]
    public void Damage_breaks_the_channel_even_without_a_flinch()
    {
        var f = new Fixture();
        f.Harvest(1);
        f.Harness.Ticks(10);
        // A hit too weak to flinch still breaks it: any HP lost (spec 5.2 "breaks on damage").
        f.Bus.Publish(new Damaged(9_999, f.Player.Id, 0, 5, 95, false));
        f.Harness.Ticks(2);
        Assert.False(f.Player.Actions.IsBusy); // broken in the next phase 3, before its effect
        f.Harness.Ticks(f.HarvestTicks);
        f.Save();

        Assert.Equal(HarvestReason.HarvestInterrupted, Assert.Single(f.Results()).Reason);
        Assert.Equal(0, f.Units(f.CharacterId));
        Assert.True(f.NodeAt(Near).IsAvailable);
    }

    [Fact]
    public void A_flinch_in_the_channel_interrupts_the_harvest()
    {
        var f = new Fixture();
        f.Harvest(1);
        f.Harness.Ticks(10);
        Assert.True(ActionMachine.Interrupt(f.Player.Actions, InterruptKind.Flinch, 300));
        f.Harness.Ticks(f.HarvestTicks);
        Assert.Equal(HarvestReason.HarvestInterrupted, Assert.Single(f.Results()).Reason);
        Assert.True(f.NodeAt(Near).IsAvailable);
    }

    [Fact]
    public void A_node_depleted_by_someone_else_during_the_channel_breaks_it_with_NodeDepleted()
    {
        var f = new Fixture();
        f.Harvest(1);
        f.Harness.Ticks(10);
        Assert.True(f.Harness.World.TryDepleteNode(f.NodeAt(Near).Id));
        f.Harness.Ticks(1);

        Assert.Equal(HarvestReason.NodeDepleted, Assert.Single(f.Results()).Reason);
        Assert.False(f.Player.Actions.IsBusy);
        Assert.Equal(0, f.Units(f.CharacterId));
    }

    [Fact]
    public void A_bag_filled_during_the_channel_refuses_at_the_end_and_leaves_the_node_available()
    {
        var f = new Fixture();
        f.Harvest(1);
        f.Harness.Ticks(10);
        // Loot lands meanwhile and fills every slot (the server adds; the player cannot move items during an action).
        Assert.True(f.Items.TryGetItem("m1.demonHorn", out var horn));
        for (int i = 0; i < f.Items.NormalBag.Slots; i++)
            f.Inventory.TryAdd(f.CharacterId, horn, 0, 99, $"test:fill:{i}", InventoryReason.LootPickup, "test");
        f.Harness.Ticks(f.HarvestTicks);

        Assert.Equal(HarvestReason.BagFull, Assert.Single(f.Results()).Reason);
        Assert.True(f.NodeAt(Near).IsAvailable);
        Assert.Empty(f.Log.Harvested);
    }

    // ---- The four transaction tests of rule 5 (CLAUDE.md), world level. ----

    [Fact]
    public void A_double_submit_harvests_once()
    {
        var f = new Fixture();
        f.Harvest(4);
        f.Harvest(4); // the same request again (a client retry before the answer)
        f.Harness.Ticks(f.HarvestTicks);
        f.Save();

        var result = Assert.Single(f.Results());
        Assert.Equal((4u, HarvestReason.None), (result.RequestId, result.Reason));
        Assert.Single(f.Log.Harvested);
        Assert.Equal(result.Count, f.Units(f.CharacterId));
    }

    [Fact]
    public void A_retry_after_the_answer_gets_the_first_answer_again_and_harvests_nothing_more()
    {
        var f = new Fixture();
        f.Harvest(4);
        f.Harness.Ticks(f.HarvestTicks);
        f.Save();
        var first = Assert.Single(f.Results());

        f.Harvest(4);
        f.Harness.Ticks(f.HarvestTicks);
        f.Save();

        Assert.Equal(2, f.Results().Count);
        Assert.All(f.Results(), r => Assert.Equal((first.RequestId, first.ItemNetId, first.Count, HarvestReason.None),
            (r.RequestId, r.ItemNetId, r.Count, r.Reason)));
        Assert.Single(f.Log.Harvested);
        Assert.Equal(first.Count, f.Units(f.CharacterId));
        Assert.False(f.Player.Actions.IsBusy); // the repeat started no channel
    }

    [Fact]
    public void A_failed_save_is_retried_and_the_harvest_is_confirmed_and_announced_once_after_the_retry_commits()
    {
        var f = new Fixture();
        f.Harness.Saves.Outcome = SaveOutcome.Failed;
        f.Harvest(1);
        f.Harness.Ticks(f.HarvestTicks);
        f.Save();
        Assert.Empty(f.Results()); // the save failed: not confirmed
        Assert.Empty(f.Log.Harvested);
        Assert.Equal(0, f.StoredUnits(f.CharacterId));

        f.Harness.Saves.Outcome = SaveOutcome.Saved;
        f.Save(); // the scheduler resends the identical batch

        var result = Assert.Single(f.Results());
        Assert.Equal(HarvestReason.None, result.Reason);
        Assert.Equal(result.Count, f.StoredUnits(f.CharacterId));
        Assert.Single(f.Log.Harvested);
        Assert.Equal(0, f.Harvests.PendingCommits);
    }

    [Fact]
    public void A_crash_before_the_commit_confirms_nothing_and_after_a_restart_the_node_is_available_and_the_bag_unchanged()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false; // the save is written but never completes: the server dies with it in flight

        f.Harvest(1);
        f.Harness.Ticks(f.HarvestTicks);
        f.Save();

        Assert.True(f.Units(f.CharacterId) > 0); // the world had added it
        Assert.False(f.NodeAt(Near).IsAvailable); // and depleted the node
        Assert.Empty(f.Results()); // but never told the player
        Assert.Empty(f.Log.Harvested); // nor the bus (no job XP, demand or achievement for it)
        Assert.Equal(0, f.StoredUnits(f.CharacterId)); // what a restarted server loads

        // A new server process loads what the database holds: an empty bag; nodes live in memory, so every one is available.
        var restarted = new Fixture();
        Assert.True(restarted.NodeAt(Near).IsAvailable);
        Assert.Equal(0, restarted.Units(restarted.CharacterId));
        restarted.Harvest(1);
        restarted.Harness.Ticks(restarted.HarvestTicks);
        restarted.Save();
        Assert.Equal(HarvestReason.None, Assert.Single(restarted.Results()).Reason);
        Assert.Single(restarted.Log.Harvested);
    }

    [Fact]
    public void Two_players_on_one_node_in_one_tick_the_lower_entity_id_wins_and_the_other_gets_NodeDepleted()
    {
        var f = new Fixture(players: 2);
        var (low, high) = f.Joined[0].Id < f.Joined[1].Id ? (f.Joined[0], f.Joined[1]) : (f.Joined[1], f.Joined[0]);
        int lowPeer = low == f.Joined[0] ? 1 : 2, highPeer = 3 - lowPeer;

        f.Harvest(1, peer: highPeer); // arrival order does not matter: phase 3 runs in entity id order
        f.Harvest(1, peer: lowPeer);
        f.Harness.Ticks(f.HarvestTicks);
        f.Save();

        Assert.Equal(HarvestReason.None, Assert.Single(f.Results(lowPeer)).Reason);
        Assert.Equal(HarvestReason.NodeDepleted, Assert.Single(f.Results(highPeer)).Reason);
        Assert.True(f.Units(low.CharacterId) > 0);
        Assert.Equal(0, f.Units(high.CharacterId));
        Assert.Equal(low.CharacterId, Assert.Single(f.Log.Harvested).Event.CharacterId);
    }

    [Fact]
    public void A_reconnect_with_the_same_request_id_is_a_new_harvest()
    {
        var f = new Fixture();
        f.Harvest(1);
        f.Harness.Ticks(f.HarvestTicks);
        f.Save();

        f.Harness.Leave(1);
        f.Harness.Ticks(3);
        f.Joined[0] = f.Harness.Join(1);
        f.Harness.Ticks(1);
        f.Harness.World.Nodes!.RespawnDue(long.MaxValue); // the node is back
        f.Harvest(1); // the client's counter started again
        f.Harness.Ticks(f.HarvestTicks);
        f.Save();

        Assert.Equal(2, f.Log.Harvested.Count);
        Assert.NotEqual(f.Log.Harvested[0].Event.OpKey, f.Log.Harvested[1].Event.OpKey);
    }

    [Fact]
    public void A_harvest_applied_before_a_disconnect_still_commits_and_is_announced()
    {
        var f = new Fixture();
        f.Harvest(1);
        f.Harness.Ticks(f.HarvestTicks);
        f.Harness.Leave(1); // gone before the commit: nobody reads the answer
        f.Save();

        var (harvested, _) = Assert.Single(f.Log.Harvested);
        Assert.Equal(harvested.Count, f.StoredUnits(f.CharacterId));
    }

    [Fact]
    public void Repeated_harvests_of_the_same_node_type_read_the_jobs_saved_window()
    {
        var f = new Fixture();
        var job = f.Harness.World.JobQuery!;
        Assert.Equal(0, job.Repeats(f.CharacterId, "blacksmith", JobActivity.Harvest, 1, 10));
        for (uint i = 1; i <= 3; i++)
        {
            f.Harness.World.Nodes!.RespawnDue(long.MaxValue);
            f.Harvest(i);
            f.Harness.Ticks(f.HarvestTicks);
            f.Save();
            f.Harness.Ticks(10); // the recovery runs out
        }
        Assert.Equal(3, job.Repeats(f.CharacterId, "blacksmith", JobActivity.Harvest, 1, 10)); // XP and yield read this
        Assert.Equal(3, f.Log.Harvested.Count);
    }

    private sealed class FixedBonuses : IJobBonusQuery
    {
        public int GemFind { get; set; }
        public List<JobBonusKind> Asked { get; } = [];

        public int Get(long characterId, string jobId, JobBonusKind kind, JobActivity activity)
        {
            Assert.Equal(JobActivity.Harvest, activity);
            Asked.Add(kind);
            return kind == JobBonusKind.GemFindPercent ? GemFind : 0;
        }

        public bool MoldTierUnlocked(long characterId, string jobId, int tier) => false;
    }

    [Fact]
    public void The_job_tree_bonuses_are_read_when_the_harvest_starts_and_kept_while_it_runs()
    {
        var f = new Fixture();
        var bonuses = new FixedBonuses { GemFind = 100 }; // every unit of the roll goes to the gem entries
        f.Harvests.Bonuses = bonuses;

        f.Harvest(1);
        f.Harness.Ticks(2);
        Assert.Equal([JobBonusKind.YieldPercent, JobBonusKind.GemFindPercent], bonuses.Asked);
        bonuses.GemFind = 0; // a respec during the channel changes nothing for it (job-tree decision 10)
        f.Harness.Ticks(f.HarvestTicks);
        f.Save();

        var result = Assert.Single(f.Results());
        Assert.Equal(f.NetId("m3.roughGemT1"), result.ItemNetId);
        Assert.Equal(2, bonuses.Asked.Count);
    }

    [Fact]
    public void A_harvest_asked_for_or_channelling_holds_the_job_activity_gate_so_a_respec_waits()
    {
        var f = new Fixture();
        var gate = Assert.Single(f.Harness.World.JobActivityGates);
        Assert.False(gate.InSession(f.CharacterId));

        f.Harvest(1);
        f.Harness.Ticks(3);
        Assert.True(gate.InSession(f.CharacterId)); // channelling: the bonuses it read stay
        f.Harness.Ticks(f.HarvestTicks);
        Assert.False(gate.InSession(f.CharacterId)); // applied at the effect point
    }

    [Fact]
    public void Done_a_blacksmith_with_a_pickaxe_mines_ore_in_ring_1_keeps_it_after_a_relogin_and_the_node_respawns()
    {
        // M3 plan WP-M3-5 "done", in one world: mine a ring 1 node, log out and in, the yield is in the loaded bag.
        var f = new Fixture();
        var node = f.NodeAt(Near);
        f.Harvest(1);
        f.Harness.Ticks(f.HarvestTicks);
        f.Save();
        var result = Assert.Single(f.Results());
        Assert.Equal(HarvestReason.None, result.Reason);

        f.Harness.Leave(1);
        f.Harness.Ticks(3);
        var again = f.Harness.Join(1);
        f.Harness.Ticks(1);
        Assert.Equal(result.Count, f.Units(again.CharacterId));
        Assert.Equal(result.Count, f.StoredUnits(again.CharacterId)); // what a restarted server loads
        Assert.Contains(f.Harness.Characters.Items[again.CharacterId], r => r.DefId == f.Items.TryGetItemId(result.ItemNetId));

        while (!node.IsAvailable && f.Harness.World.NowMs < node.RespawnAtMs + 1_000)
            f.Harness.Ticks(20);
        Assert.True(node.IsAvailable);
    }
}

internal static class ItemRulesTestExtensions
{
    public static string TryGetItemId(this ItemRules items, ushort netId) => items.TryGetItem(netId, out var item) ? item.Id : "";
}
