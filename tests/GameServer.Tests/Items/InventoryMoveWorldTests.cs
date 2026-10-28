using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Tests.Items;

/// <summary>
/// Bag moves of the inventory window in the world tick by tick (backlog G client part, WP16; spec 4.1, 9.13): the server
/// applies them through <see cref="InventoryService.TryMove"/>, refuses with a reason at once, and confirms an accepted
/// move only once its save committed (rule 5), right before the contents that show it; a repeated request id changes
/// nothing (double submit); moves of one player in one tick run in arrival order on one bag; a reconnect starts a new key
/// scope.
/// </summary>
public class InventoryMoveWorldTests
{
    private const string Horn = "m1.demonHorn"; // maxStack 99
    private const string Ash = "m1.demonAsh";

    private sealed class Fixture
    {
        public Fixture(params ItemStackRow[] bag)
        {
            var tables = TestCombat.Tables();
            Items = ItemRules.From(tables)!;
            Harness = new WorldHarness(map: TestCombat.OpenMap(TestCombat.MonsterMarker(2_000, 0)),
                combat: TestCombat.Rules(tables, TestCombat.Point()), items: Items);
            Character = Harness.Characters.Add(1, 0, new CharacterState(1, 0, "", 0, 0, 0, 0, 100, 50, 100));
            Harness.Characters.Items[Character.CharacterId] = bag;
            Player = Harness.Join(1);
            Harness.Ticks(1);
        }

        public WorldHarness Harness { get; }
        public ItemRules Items { get; }
        public CharacterRecord Character { get; }
        public Player Player { get; }
        public InventoryService Inventory => Harness.World.Inventory!;
        public FakeTransport Transport => Harness.Transport;

        public ItemStack Slot(int slot) => Inventory.Current(Character.CharacterId)[slot];

        public ushort NetId(string itemId)
        {
            Assert.True(Items.TryGetItem(itemId, out var item));
            return (ushort)item.NetId;
        }

        /// <summary>The next class 2 batch (every second) and its completion.</summary>
        public void Save()
        {
            Harness.Clock.Advance(1_000);
            Harness.Ticks(2);
        }

        public InventorySlotEntry? Shown(int slot) => Transport.InventoryFor(1)[^1].Slots.SingleOrDefault(s => s.Slot == slot);
    }

    private static ItemStackRow Row(int slot, string item, int count) => new(1, slot, item, 0, false, count);

    [Fact]
    public void A_move_into_an_empty_slot_is_applied_at_once_and_confirmed_only_after_its_save()
    {
        var f = new Fixture(Row(0, Horn, 5));
        int contentsBefore = f.Transport.InventoryFor(1).Count;

        f.Harness.SendMove(1, requestId: 1, fromSlot: 0, toSlot: 7);
        f.Harness.Ticks(1);

        Assert.True(f.Slot(0).IsEmpty);
        Assert.Equal(5, f.Slot(7).Count); // the world sees it now
        Assert.Empty(f.Transport.MoveResultsFor(1)); // the player is told only after the commit
        Assert.Equal(contentsBefore, f.Transport.InventoryFor(1).Count);

        f.Save();

        var result = Assert.Single(f.Transport.MoveResultsFor(1));
        Assert.Equal((1u, InventoryMoveReason.None, InventoryMoveOutcome.Moved), (result.RequestId, result.Reason, result.Outcome));
        Assert.Equal(5, f.Shown(7)!.Count);
        Assert.Null(f.Shown(0));
        var ids = f.Transport.InventoryMessageIdsFor(1);
        Assert.Equal([ReliableMessageId.InventoryMoveResult, ReliableMessageId.InventoryContents], ids.Skip(ids.Count - 2));
        var job = Assert.Single(f.Harness.Saves.Jobs, j => j.Inventory != null);
        var op = Assert.Single(job.Inventory!.Ops);
        Assert.Equal($"move:test-run:{f.Player.Id}:{f.Player.Connection}:1", op.OpKey);
        Assert.Equal(nameof(InventoryReason.PlayerMove), op.Reason);
        Assert.All(job.Inventory.Audit, a => Assert.Equal(AuditKind.Move, a.Kind));
        Assert.Equal([0, 7], job.Inventory.Slots.Select(s => s.Slot));
    }

    [Fact]
    public void Merge_and_swap_report_their_outcome()
    {
        var f = new Fixture(Row(0, Horn, 60), Row(1, Horn, 50), Row(2, Ash, 3));

        f.Harness.SendMove(1, 1, fromSlot: 0, toSlot: 1); // 49 fit onto the 50: 11 stay
        f.Harness.SendMove(1, 2, fromSlot: 2, toSlot: 0); // ash onto the horn rest: swap
        f.Harness.Ticks(1);
        f.Save();

        Assert.Equal([InventoryMoveOutcome.Merged, InventoryMoveOutcome.Swapped], f.Transport.MoveResultsFor(1).Select(r => r.Outcome));
        Assert.Equal((f.NetId(Ash), 3), (f.Slot(0).ItemNetId, f.Slot(0).Count));
        Assert.Equal(99, f.Slot(1).Count);
        Assert.Equal((f.NetId(Horn), 11), (f.Slot(2).ItemNetId, f.Slot(2).Count));
        Assert.Equal(11, f.Shown(2)!.Count);
    }

    [Theory]
    [InlineData(0, 0, InventoryMoveReason.ItemSlotInvalid)] // same slot
    [InlineData(0, 30, InventoryMoveReason.ItemSlotInvalid)] // outside the 30-slot bag
    [InlineData(255, 1, InventoryMoveReason.ItemSlotInvalid)]
    [InlineData(5, 1, InventoryMoveReason.ItemSlotEmpty)]
    [InlineData(1, 0, InventoryMoveReason.ItemStackFull)] // horn onto a full horn stack
    public void A_refused_move_is_answered_at_once_with_its_reason_and_changes_nothing(byte from, byte to, InventoryMoveReason reason)
    {
        var f = new Fixture(Row(0, Horn, 99), Row(1, Horn, 4));
        int contents = f.Transport.InventoryFor(1).Count;

        f.Harness.SendMove(1, 9, from, to);
        f.Harness.Ticks(1);

        var result = Assert.Single(f.Transport.MoveResultsFor(1));
        Assert.Equal((9u, reason, InventoryMoveOutcome.None), (result.RequestId, result.Reason, result.Outcome));
        Assert.Equal((99, 4), (f.Slot(0).Count, f.Slot(1).Count));
        Assert.False(f.Inventory.HasUnsaved(f.Character.CharacterId));
        f.Save();
        Assert.Equal(contents, f.Transport.InventoryFor(1).Count); // nothing to save, nothing new to show
    }

    [Fact]
    public void A_double_submit_moves_once_and_gets_the_same_answer_twice()
    {
        var f = new Fixture(Row(0, Horn, 5), Row(3, Ash, 2));

        f.Harness.SendMove(1, 4, 0, 3); // swap
        f.Harness.SendMove(1, 4, 0, 3); // the same request again (a client retry): would swap back if applied
        f.Harness.Ticks(1);
        f.Harness.SendMove(1, 4, 0, 3); // and once more in a later tick
        f.Harness.Ticks(1);
        f.Save();

        Assert.Equal((f.NetId(Ash), f.NetId(Horn)), (f.Slot(0).ItemNetId, f.Slot(3).ItemNetId));
        var results = f.Transport.MoveResultsFor(1);
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal((4u, InventoryMoveReason.None, InventoryMoveOutcome.Swapped), (r.RequestId, r.Reason, r.Outcome)));
        Assert.Single(f.Harness.Saves.Jobs.Where(j => j.Inventory != null).SelectMany(j => j.Inventory!.Ops));
    }

    // ---- stack splitting (WP-M2-11, spec 9.13; protocol 24): a move with a count below the stack's

    [Fact]
    public void A_split_moves_its_count_into_an_empty_slot_and_is_confirmed_after_its_save()
    {
        var f = new Fixture(Row(0, Horn, 5));

        f.Harness.SendMove(1, 1, fromContainer: 0, fromSlot: 0, toContainer: 0, toSlot: 6, count: 2);
        f.Harness.Ticks(1);

        Assert.Equal((3, 2), (f.Slot(0).Count, f.Slot(6).Count));
        Assert.Empty(f.Transport.MoveResultsFor(1)); // commit before confirming (rule 5)
        f.Save();
        var result = Assert.Single(f.Transport.MoveResultsFor(1));
        Assert.Equal((InventoryMoveReason.None, InventoryMoveOutcome.Moved), (result.Reason, result.Outcome));
        Assert.Equal((3, 2), (f.Shown(0)!.Count, f.Shown(6)!.Count));
        var job = Assert.Single(f.Harness.Saves.Jobs, j => j.Inventory != null);
        Assert.Equal(nameof(InventoryReason.PlayerMove), Assert.Single(job.Inventory!.Ops).Reason);
        Assert.All(job.Inventory.Audit, a => Assert.Equal(AuditKind.Move, a.Kind));
        Assert.Equal(0, job.Inventory.Audit.Sum(a => a.CountDelta)); // a split creates and destroys nothing
    }

    [Fact]
    public void A_double_submitted_split_moves_once_and_gets_the_same_answer()
    {
        var f = new Fixture(Row(0, Horn, 5));

        f.Harness.SendMove(1, 3, 0, 0, 0, 6, count: 2);
        f.Harness.SendMove(1, 3, 0, 0, 0, 6, count: 2); // a client retry: applied again it would leave 1 and 4
        f.Harness.Ticks(1);
        f.Save();

        Assert.Equal((3, 2), (f.Slot(0).Count, f.Slot(6).Count));
        Assert.All(f.Transport.MoveResultsFor(1), r => Assert.Equal((3u, InventoryMoveOutcome.Moved), (r.RequestId, r.Outcome)));
        Assert.Equal(2, f.Transport.MoveResultsFor(1).Count);
        Assert.Single(f.Harness.Saves.Jobs.Where(j => j.Inventory != null).SelectMany(j => j.Inventory!.Ops));
    }

    [Fact]
    public void Two_splits_racing_for_one_stack_apply_in_order_and_the_second_finds_too_few()
    {
        var f = new Fixture(Row(0, Horn, 5), Row(1, Horn, 97));

        f.Harness.SendMove(1, 1, 0, 0, 0, 6, count: 3);
        f.Harness.SendMove(1, 2, 0, 0, 0, 7, count: 3); // only 2 are left after the first
        f.Harness.SendMove(1, 3, 0, 0, 0, 1, count: 2); // the whole rest: a plain merge onto 97 (99 max)
        f.Harness.Ticks(1);
        f.Save();

        Assert.Equal([(2u, InventoryMoveReason.ItemSlotInvalid), (1u, InventoryMoveReason.None), (3u, InventoryMoveReason.None)],
            f.Transport.MoveResultsFor(1).Select(r => (r.RequestId, r.Reason)));
        Assert.True(f.Slot(0).IsEmpty);
        Assert.Equal((99, 3), (f.Slot(1).Count, f.Slot(6).Count));
        Assert.True(f.Slot(7).IsEmpty);
    }

    [Theory]
    [InlineData(1, 2, InventoryMoveReason.ItemStackFull)] // 2 more onto 98 of 99
    [InlineData(0, 3, InventoryMoveReason.ItemSlotInvalid)] // ash is another item
    public void A_split_onto_an_occupied_slot_needs_the_same_item_with_room(byte to, byte count, InventoryMoveReason reason)
    {
        var f = new Fixture(Row(0, Horn, 5), Row(1, Horn, 98), Row(3, Ash, 2));
        byte target = to == 0 ? (byte)3 : to;

        f.Harness.SendMove(1, 9, 0, 0, 0, target, count);
        f.Harness.Ticks(1);

        var result = Assert.Single(f.Transport.MoveResultsFor(1));
        Assert.Equal((9u, reason), (result.RequestId, result.Reason));
        Assert.Equal(5, f.Slot(0).Count);
        Assert.False(f.Inventory.HasUnsaved(f.Character.CharacterId));
    }

    [Fact]
    public void Two_moves_from_one_slot_in_one_tick_run_in_arrival_order_on_one_bag()
    {
        var f = new Fixture(Row(0, Horn, 5));

        f.Harness.SendMove(1, 1, 0, 4);
        f.Harness.SendMove(1, 2, 0, 5); // its source was just emptied by the first
        f.Harness.Ticks(1);

        var refused = Assert.Single(f.Transport.MoveResultsFor(1)); // the refusal goes out at once
        Assert.Equal((2u, InventoryMoveReason.ItemSlotEmpty), (refused.RequestId, refused.Reason));
        f.Save();
        Assert.Equal([(2u, InventoryMoveReason.ItemSlotEmpty), (1u, InventoryMoveReason.None)],
            f.Transport.MoveResultsFor(1).Select(r => (r.RequestId, r.Reason)));
        Assert.Equal(5, f.Shown(4)!.Count);
        Assert.Null(f.Shown(5));
    }

    [Fact]
    public void A_move_made_while_a_save_is_in_flight_is_confirmed_with_the_next_save()
    {
        var f = new Fixture(Row(0, Horn, 5), Row(1, Ash, 2));
        f.Harness.Saves.AutoComplete = false;
        f.Harness.SendMove(1, 1, 0, 10);
        f.Harness.Ticks(1);
        f.Harness.Clock.Advance(1_000);
        f.Harness.Ticks(1); // the batch with move 1 is taken and in flight
        Assert.Single(f.Harness.Saves.Jobs, j => j.Inventory != null);

        f.Harness.SendMove(1, 2, 1, 11);
        f.Harness.Ticks(1);
        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(1);

        var first = Assert.Single(f.Transport.MoveResultsFor(1));
        Assert.Equal(1u, first.RequestId); // move 2 rides on the next save
        Assert.Null(f.Shown(10 + 1));
        Assert.Equal(5, f.Shown(10)!.Count);

        f.Harness.Saves.AutoComplete = true;
        f.Save();
        Assert.Equal([1u, 2u], f.Transport.MoveResultsFor(1).Select(r => r.RequestId));
        Assert.Equal(2, f.Shown(11)!.Count);
    }

    [Fact]
    public void After_a_reconnect_the_same_request_id_is_a_new_move()
    {
        var f = new Fixture(Row(0, Horn, 5));
        f.Harness.SendMove(1, 1, 0, 1);
        f.Harness.Ticks(1);
        f.Save();
        string firstKey = $"move:test-run:{f.Player.Id}:{f.Player.Connection}:1";
        Assert.True(f.Inventory.IsCommitted(f.Character.CharacterId, firstKey));

        // The client reconnects within the linger window and its request counter starts again at 1.
        var bound = Assert.Single(f.Harness.World.Entities.OfType<Player>());
        int connection = bound.Connection;
        f.Harness.Leave(1);
        f.Harness.Ticks(1);
        f.Harness.Connect(1);
        f.Harness.Ticks(2);
        Assert.True(f.Harness.World.TryGetPlayer(1, out var again));
        if (ReferenceEquals(again, bound))
            Assert.Equal(connection + 1, again.Connection);

        f.Harness.SendMove(1, 1, 1, 2);
        f.Harness.Ticks(1);
        f.Save();

        Assert.Equal(5, f.Slot(2).Count); // applied, not taken for a repeat of the first move
        Assert.Equal(InventoryMoveOutcome.Moved, f.Transport.MoveResultsFor(1)[^1].Outcome);
    }

    [Fact]
    public void IsCommitted_follows_an_operation_from_pending_to_in_flight_to_saved()
    {
        var f = new Fixture(Row(0, Horn, 5));
        long id = f.Character.CharacterId;
        string key = $"move:test-run:{f.Player.Id}:{f.Player.Connection}:1";
        Assert.False(f.Inventory.IsCommitted(id, key)); // unknown

        f.Harness.SendMove(1, 1, 0, 1);
        f.Harness.Ticks(1);
        Assert.False(f.Inventory.IsCommitted(id, key)); // pending
        f.Harness.Saves.AutoComplete = false;
        f.Harness.Clock.Advance(1_000);
        f.Harness.Ticks(1);
        Assert.False(f.Inventory.IsCommitted(id, key)); // in flight
        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(1);
        Assert.True(f.Inventory.IsCommitted(id, key));
        Assert.False(f.Inventory.IsCommitted(id + 1, key)); // another character
    }

    [Theory]
    [InlineData(0, 0, 3, InventoryMoveReason.ItemSlotInvalid)]
    [InlineData(1, 2, 3, InventoryMoveReason.ItemSlotEmpty)]
    [InlineData(0, 2, 3, InventoryMoveReason.ItemSlotInvalid)] // the bag would have moved it: not a refusal reason
    public void WhyRefused_follows_the_order_of_the_bag_checks(int from, int to, int slots, InventoryMoveReason expected)
    {
        var bag = new Bag(slots);
        bag.Load(0, new ItemStack(1, 0, false, 2));

        Assert.Equal(expected, InventoryMoveHandler.WhyRefused(bag, from, to));
    }
}
