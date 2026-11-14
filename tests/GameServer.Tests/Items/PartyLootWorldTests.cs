using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Persistence;
using GameServer.Sessions;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Tests.Items;

/// <summary>
/// Party loot in the world tick by tick (spec 4.3 R-4.3-02 "M4 party loot", M4 decision 2; WP-M4-6), over the repository
/// tables: a party's drop is locked to its members and a member's pickup rolls among the eligible members (seeded, logged);
/// the winner's bag takes it, a full bag sends it by mail on the winner's save; each part is announced after the commit (user
/// answer 2026-11-13); a member's pickup still rolls after the lock while an outsider then takes freely; a member that left is
/// neither locked in nor a candidate; two pickups in one tick take a drop once (the <c>NoDuplication</c> tests).
/// </summary>
public class PartyLootWorldTests
{
    private const int Horns = 3;

    private sealed class Fixture : IEventConsumer<LootRolled>, IEventConsumer<LootPickedUp>
    {
        private uint _requests;

        public Fixture()
        {
            var tables = TestCombat.Tables();
            SetDemonLoot(tables, Horns);
            Items = ItemRules.From(tables)!;
            Params = PartyLootParams.Find(tables)!;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<LootRolled>(this);
            Bus.Subscribe<LootPickedUp>(this);
            Harness = new WorldHarness(map: TestCombat.OpenMap(TestCombat.MonsterMarker(2_000, 0)),
                events: new EventDispatchHook(Bus), bus: Bus, combat: TestCombat.Rules(tables, TestCombat.Point()), items: Items,
                rngSeed: 42, party: TestWorlds.Party(), mail: MailRules.From(tables, Items), partyLoot: Params);
            World.Loot!.UnitValue = _ => 10; // a value, so the debts grow (the world reads PriceBase through its economy)
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public ItemRules Items { get; }
        public PartyLootParams Params { get; }
        public GameWorld World => Harness.World;
        public InventoryService Inventory => World.Inventory!;
        public List<LootRolled> Rolls { get; } = [];
        public List<LootPickedUp> Picks { get; } = [];

        public void Handle(in EventEnvelope envelope, in LootRolled payload) => Rolls.Add(payload);
        public void Handle(in EventEnvelope envelope, in LootPickedUp payload) => Picks.Add(payload);

        public Player Join(int peerId, int xCm)
        {
            Harness.Characters.Add(peerId, 0, new CharacterState(5, 0, SessionManager.MainWorldKey, xCm, 0, 0, 0, 100, 10, 100));
            return Harness.Join(peerId);
        }

        /// <summary>A party of the peers, the first the leader.</summary>
        public void Form(params int[] peers)
        {
            Harness.Ticks(1);
            for (int i = 1; i < peers.Length; i++)
            {
                Harness.SendParty(peers[0], ++_requests, PartyAction.Invite, targetName: $"c{peers[i]}_0");
                Harness.Ticks(1);
                uint invite = Harness.Transport.PartyInvitesFor(peers[i])[^1].InviteId;
                Harness.SendParty(peers[i], ++_requests, PartyAction.Accept, invite);
                Harness.Ticks(1);
            }
        }

        public void Leave(int peerId)
        {
            Harness.SendParty(peerId, ++_requests, PartyAction.Leave);
            Harness.Ticks(1);
        }

        public Monster Monster() => Assert.Single(World.Entities.OfType<Monster>());

        /// <summary>The hits, then the kill by <paramref name="killer"/> at (100, 0), as HitResolver publishes them, in one tick.</summary>
        public LootDrop Kill(Player killer, params Player[] hitters)
        {
            var monster = Monster();
            foreach (var hitter in hitters)
                Bus.Publish(new Damaged(hitter.Id, monster.Id, 0, 10, 0, false), hitter.CharacterId);
            Bus.Publish(new Killed(killer.Id, monster.Id, 0, monster.DefId, 100, 0), killer.CharacterId);
            Harness.Ticks(1);
            return Assert.Single(World.Entities.OfType<LootDrop>());
        }

        public int Units(Player player) =>
            Enumerable.Range(0, Inventory.Rules.NormalBag.Slots).Sum(i => Inventory.Current(player.CharacterId)[i].Count);

        /// <summary>Fills the player's Normal bag with full stacks of potions (nothing else fits).</summary>
        public void FillBag(Player player, int slots)
        {
            Assert.True(Items.TryGetItem("m2.potionHpSmall", out var potion));
            for (int i = 0; i < slots; i++)
                Assert.Equal(potion.MaxStack, Inventory.TryAdd(player.CharacterId, potion, 0, potion.MaxStack, $"fill:{player.CharacterId}:{i}",
                    InventoryReason.Admin, "test").Added);
        }

        /// <summary>The next class 2 batch and its completion.</summary>
        public void Save()
        {
            Harness.Clock.Advance(1_000);
            Harness.Ticks(2);
        }
    }

    private static void SetDemonLoot(ConfigSet tables, int count)
    {
        Assert.True(tables.TryGet(typeof(LootTableEntry), out var table));
        var entries = table.Rows.Cast<LootTableEntry>().ToList();
        foreach (var entry in entries.Where(e => e.TableId == "m1.demon"))
            (entry.CountMin, entry.CountMax) = (count, count);
        entries.RemoveAll(e => e.TableId == "m1.demon" && e.ItemId != "m1.demonHorn");
        tables.Add(new ConfigTable(typeof(LootTableEntry), 1, entries.Cast<object>().ToList()));
    }

    [Fact]
    public void A_party_drop_is_locked_to_the_party_and_a_members_pickup_rolls_every_unit_among_the_eligible()
    {
        var f = new Fixture();
        var a = f.Join(1, 0);
        var b = f.Join(2, 200);
        var outsider = f.Join(3, 100);
        f.Form(1, 2);

        var drop = f.Kill(a, b);
        Assert.Equal(f.World.PartyQuery.PartyIdOf(a.CharacterId), drop.OwnerPartyId);
        Assert.Equal(new[] { a.CharacterId, b.CharacterId }, drop.EligibleAtKill);

        f.Harness.SendPickup(3, drop.Id);
        f.Harness.Ticks(1);
        Assert.Equal(PickupReason.LootLocked, Assert.Single(f.Harness.Transport.PickupResultsFor(3)).Reason);

        f.Harness.SendPickup(1, drop.Id);
        f.Harness.Ticks(1);

        var result = Assert.Single(f.Harness.Transport.PickupResultsFor(1));
        Assert.DoesNotContain(drop, f.World.Entities);
        var roll = Assert.Single(f.Rolls);
        // #238 review: the picker hears of its own bag part only; the partner's part reaches it as a LootRollNotice.
        Assert.Equal((PickupReason.None, (ushort)roll.Won[0]), (result.Reason, result.Taken));
        Assert.Equal(new[] { a.CharacterId, b.CharacterId }, roll.Candidates);
        Assert.Equal((Horns, a.CharacterId, drop.DropKey), (roll.Units, roll.PickerCharacterId, roll.DropKey));
        Assert.Equal(Horns, roll.Won.Sum());
        Assert.Equal(new[] { roll.Won[0], roll.Won[1] }, new[] { f.Units(a), f.Units(b) });
        Assert.Equal(0, f.Units(outsider));

        // The logged seed reproduces the roll (rule 6): no debts before, value 10 per unit.
        var won = new int[2];
        var weights = new long[2];
        var after = new long[2];
        PartyLoot.RollUnits(new long[] { 0, 0 }, 10, f.Params.DebtHalfWeightGold, Horns, roll.Seed, weights, won, after);
        Assert.Equal(won, roll.Won);
        Assert.Equal(weights, roll.WeightsPpm);
        long party = drop.OwnerPartyId;
        Assert.Equal(10L * won[1], f.World.Parties!.DebtOf(party, b.CharacterId, f.World.NowMs));
    }

    [Fact]
    public void Each_winners_part_is_announced_to_the_party_only_after_the_winners_save_committed()
    {
        var f = new Fixture();
        var a = f.Join(1, 0);
        var b = f.Join(2, 200);
        f.Form(1, 2);
        var drop = f.Kill(a, b);
        f.Harness.Saves.AutoComplete = false;

        f.Harness.SendPickup(2, drop.Id);
        f.Harness.Ticks(1);
        f.Save();
        Assert.Empty(f.Harness.Transport.LootRollNoticesFor(1));
        Assert.Empty(f.Harness.Transport.LootRollNoticesFor(2));

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        var roll = Assert.Single(f.Rolls);
        int parts = roll.Won.Count(w => w > 0);
        foreach (int peer in new[] { 1, 2 })
        {
            var notices = f.Harness.Transport.LootRollNoticesFor(peer);
            Assert.Equal(parts, notices.Count);
            Assert.All(notices, n => Assert.Equal((drop.Id, drop.DefId), (n.DropEntityId, n.ItemNetId)));
            Assert.Equal(Horns, notices.Sum(n => n.Units));
            Assert.All(notices, n => Assert.Equal(n.WinnerEntityId == a.Id ? "c1_0" : "c2_0", n.WinnerName)); // DisplayIdentity
        }
        Assert.Equal(0, f.World.Loot!.PendingNotices);
    }

    [Fact]
    public void After_the_lock_a_member_still_rolls_and_an_outsider_takes_freely()
    {
        var f = new Fixture();
        var a = f.Join(1, 0);
        var b = f.Join(2, 200);
        var outsider = f.Join(3, 100);
        f.Form(1, 2);
        var first = f.Kill(a, b);
        f.Harness.Ticks(f.Params.LockMs / (1000 / f.World.Settings.TickRateHz) + 1);

        f.Harness.SendPickup(1, first.Id);
        f.Harness.Ticks(1);
        var roll = Assert.Single(f.Rolls); // user answer 2026-11-13: a member's pickup rolls after the lock too
        Assert.Equal(new[] { a.CharacterId, b.CharacterId }, roll.Candidates);
        Assert.Equal(0, f.Units(outsider));
    }

    [Fact]
    public void An_outsider_takes_a_party_drop_whole_after_the_lock_without_a_roll()
    {
        var f = new Fixture();
        var a = f.Join(1, 0);
        var b = f.Join(2, 200);
        var outsider = f.Join(3, 100);
        f.Form(1, 2);
        var drop = f.Kill(a, b);
        f.Harness.Ticks(f.Params.LockMs / (1000 / f.World.Settings.TickRateHz) + 1);

        f.Harness.SendPickup(3, drop.Id);
        f.Harness.Ticks(1);

        Assert.Equal(PickupReason.None, Assert.Single(f.Harness.Transport.PickupResultsFor(3)).Reason);
        Assert.Equal(Horns, f.Units(outsider));
        Assert.Empty(f.Rolls);
    }

    [Fact]
    public void A_member_that_left_between_the_kill_and_the_pickup_is_locked_out_and_no_candidate()
    {
        var f = new Fixture();
        var a = f.Join(1, 0);
        var b = f.Join(2, 200);
        var c = f.Join(3, 150);
        f.Form(1, 2, 3);
        var drop = f.Kill(a, b, c);
        Assert.Equal(3, drop.EligibleAtKill.Count);

        f.Leave(3);
        f.Harness.SendPickup(3, drop.Id);
        f.Harness.Ticks(1);
        Assert.Equal(PickupReason.LootLocked, Assert.Single(f.Harness.Transport.PickupResultsFor(3)).Reason);

        f.Harness.SendPickup(2, drop.Id);
        f.Harness.Ticks(1);
        var roll = Assert.Single(f.Rolls);
        Assert.Equal(new[] { a.CharacterId, b.CharacterId }, roll.Candidates);
        Assert.Equal(0, f.Units(c));
    }

    [Fact]
    public void A_member_out_of_reach_of_the_drop_is_no_candidate_and_without_a_hit_or_range_is_not_eligible()
    {
        var f = new Fixture();
        var a = f.Join(1, 0);
        var b = f.Join(2, 200);
        var far = f.Join(3, 9_000); // beyond killRangeCm of the kill, no hit
        f.Form(1, 2, 3);
        var drop = f.Kill(a); // b hit nothing but stood within range of the kill

        Assert.Equal(new[] { a.CharacterId, b.CharacterId }, drop.EligibleAtKill);
        Assert.DoesNotContain(far.CharacterId, drop.EligibleAtKill);
    }

    [Fact]
    public void A_winner_whose_bag_is_full_gets_the_units_by_mail_on_its_own_save()
    {
        var f = new Fixture();
        var a = f.Join(1, 9_000); // the killer, then out of reach of the drop: not a candidate at the pickup
        var b = f.Join(2, 200);
        f.Form(1, 2);
        f.FillBag(b, f.Inventory.Rules.NormalBag.Slots);
        f.Save();
        var drop = f.Kill(a, b);
        f.Harness.Saves.AutoComplete = false;

        f.Harness.SendPickup(2, drop.Id);
        f.Harness.Ticks(1);

        var roll = Assert.Single(f.Rolls);
        Assert.Equal(new[] { b.CharacterId }, roll.Candidates);
        Assert.Equal(new[] { Horns }, roll.Mailed);
        Assert.DoesNotContain(drop, f.World.Entities);
        f.Harness.Ticks(2);
        var save = f.Harness.Saves.Jobs.Last(j => j.CharacterId == b.CharacterId);
        var send = Assert.Single(save.Mail!.Sends);
        Assert.Equal((b.CharacterId, MailKind.LootOverflow, Horns), (send.Draft.ToCharacterId, send.Draft.Kind, send.Draft.Items.Single().Count));
        Assert.EndsWith(":mail", send.Draft.OpKey);
        Assert.Empty(f.Harness.Transport.LootRollNoticesFor(2)); // not before the commit

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);
        var notice = Assert.Single(f.Harness.Transport.LootRollNoticesFor(2));
        Assert.Equal(((ushort)0, (ushort)Horns, b.Id), (notice.Units, notice.Mailed, notice.WinnerEntityId));
    }

    [Fact]
    public void A_partial_fit_splits_the_winning_between_the_bag_and_the_mail()
    {
        var f = new Fixture();
        var a = f.Join(1, 9_000);
        var b = f.Join(2, 200);
        f.Form(1, 2);
        f.FillBag(b, f.Inventory.Rules.NormalBag.Slots - 1);
        Assert.True(f.Items.TryGetItem("m1.demonHorn", out var horn));
        f.Inventory.TryAdd(b.CharacterId, horn, 0, horn.MaxStack - 1, "fill:horns", InventoryReason.Admin, "test");
        var drop = f.Kill(a, b);

        f.Harness.SendPickup(2, drop.Id);
        f.Harness.Ticks(1);

        var roll = Assert.Single(f.Rolls);
        Assert.Equal((Horns, Horns - 1), (roll.Won[0], roll.Mailed[0]));
        var bag = f.Inventory.Current(b.CharacterId);
        Assert.Equal(horn.MaxStack, Enumerable.Range(0, f.Inventory.Rules.NormalBag.Slots).Select(i => bag[i]).Where(s => s.ItemNetId == horn.NetId).Sum(s => s.Count));
    }

    [Fact]
    [Trait("Category", "NoDuplication")]
    public void Two_members_picking_one_drop_in_one_tick_roll_it_once()
    {
        var f = new Fixture();
        var a = f.Join(1, 0);
        var b = f.Join(2, 200);
        f.Form(1, 2);
        var drop = f.Kill(a, b);

        f.Harness.SendPickup(2, drop.Id);
        f.Harness.SendPickup(1, drop.Id);
        f.Harness.SendPickup(1, drop.Id); // a double submit
        f.Harness.Ticks(1);

        Assert.Single(f.Rolls);
        Assert.Equal(PickupReason.None, f.Harness.Transport.PickupResultsFor(1)[0].Reason); // entity id order: a first
        Assert.Equal(PickupReason.LootNotFound, f.Harness.Transport.PickupResultsFor(1)[1].Reason);
        Assert.Equal(PickupReason.LootNotFound, Assert.Single(f.Harness.Transport.PickupResultsFor(2)).Reason);
        Assert.Equal(Horns, f.Units(a) + f.Units(b));
    }

    [Fact]
    [Trait("Category", "NoDuplication")]
    public void A_part_is_applied_and_announced_once_whatever_is_retried()
    {
        var f = new Fixture();
        var a = f.Join(1, 0);
        var b = f.Join(2, 200);
        f.Form(1, 2);
        var drop = f.Kill(a, b);
        f.Harness.SendPickup(1, drop.Id);
        f.Harness.Ticks(1);
        var roll = Assert.Single(f.Rolls);
        int winner = Array.FindIndex(roll.Won, w => w > 0);
        long winnerId = roll.Candidates[winner];
        string key = $"{drop.DropKey}:p0:w{winner}";
        Assert.True(f.Inventory.WasApplied(winnerId, key));

        // The same operation key applied again (a retried request reaching the writer) adds nothing.
        Assert.True(f.Items.TryGetItem("m1.demonHorn", out var horn));
        int before = f.Units(winnerId == a.CharacterId ? a : b);
        f.Inventory.TryAdd(winnerId, horn, 0, roll.Won[winner], key, InventoryReason.LootPickup, drop.DropKey);
        Assert.Equal(before, f.Units(winnerId == a.CharacterId ? a : b));

        f.Save();
        int notices = f.Harness.Transport.LootRollNoticesFor(1).Count;
        f.World.Loot!.OnOpCommitted(winnerId, key, InventoryReason.LootPickup); // a commit heard twice
        f.Harness.Ticks(1);
        Assert.Equal(notices, f.Harness.Transport.LootRollNoticesFor(1).Count);
    }

    [Fact]
    [Trait("Category", "NoDuplication")]
    public void A_crash_before_the_winners_save_loses_the_unsaved_part_and_duplicates_nothing()
    {
        var f = new Fixture();
        var a = f.Join(1, 0);
        var b = f.Join(2, 200);
        f.Form(1, 2);
        var drop = f.Kill(a, b);
        f.Harness.Saves.AutoComplete = false;

        f.Harness.SendPickup(1, drop.Id);
        f.Harness.Ticks(1);
        f.Save(); // issued, never completed: the server dies here

        Assert.DoesNotContain(drop, f.World.Entities); // the drop (volatile, spec 4.3) cannot be taken again
        Assert.True(f.Inventory.Committed(a.CharacterId).All(s => s.IsEmpty));
        Assert.True(f.Inventory.Committed(b.CharacterId).All(s => s.IsEmpty));
        Assert.Empty(f.Harness.Transport.LootRollNoticesFor(1)); // nothing was announced
        Assert.Empty(f.Harness.Transport.LootRollNoticesFor(2));
    }

    [Fact]
    public void A_members_debt_goes_when_it_leaves_and_the_partys_when_it_disbands()
    {
        var f = new Fixture();
        var a = f.Join(1, 0);
        var b = f.Join(2, 200);
        var c = f.Join(3, 300);
        f.Form(1, 2, 3);
        var parties = f.World.Parties!;
        long party = parties.PartyIdOf(a.CharacterId);
        long now = f.World.NowMs;
        parties.SetDebt(party, b.CharacterId, 500, now);
        parties.SetDebt(party, c.CharacterId, 300, now);
        Assert.Equal(250, parties.DebtOf(party, b.CharacterId, now + f.Params.DebtHalfLifeMs));

        f.Leave(2);
        Assert.Equal(0, parties.DebtOf(party, b.CharacterId, now));
        Assert.Equal(300, parties.DebtOf(party, c.CharacterId, now));

        f.Leave(3); // two members left: the party dissolves
        Assert.Equal(0, parties.DebtOf(party, c.CharacterId, now));
        parties.SetDebt(party, a.CharacterId, 100, now); // nothing to write to
        Assert.Equal(0, parties.DebtOf(party, a.CharacterId, now));
    }

    [Fact]
    public void A_killer_alone_keeps_the_m1_lock_and_takes_without_a_roll()
    {
        var f = new Fixture();
        var a = f.Join(1, 0);
        var other = f.Join(2, 200);
        f.Harness.Ticks(1);
        var drop = f.Kill(a);
        Assert.Equal(0, drop.OwnerPartyId);
        Assert.Empty(drop.EligibleAtKill);

        f.Harness.SendPickup(2, drop.Id);
        f.Harness.Ticks(1);
        Assert.Equal(PickupReason.LootLocked, Assert.Single(f.Harness.Transport.PickupResultsFor(2)).Reason);
        f.Harness.SendPickup(1, drop.Id);
        f.Harness.Ticks(1);
        Assert.Equal(Horns, f.Units(a));
        Assert.Empty(f.Rolls);
        Assert.Equal(0, f.Units(other));
    }
}
