using GameServer.Bystial;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Bystial;

/// <summary>
/// The Bystial book in the world (spec 14.2, WP-M2-8) with the repository tables (placeholders of the user decision of
/// 2026-10-27): the demon's card (normal at 100 kills: Max HP +10, fire resistance +5), the alien's (Def +2, build-up
/// resistance 5) and the cyclops boss's (normal at 1 kill: Atk +3, damage against beasts +3 %); rank F opens one slot.
/// Cards come from the kill counters of the progression store (R-14.2-01/02) on <c>Killed</c> in phase 11, idempotently;
/// placing and swapping are idempotent class 2 operations confirmed after their save (rule 5); the stats, the damage
/// and the exp read the committed book only.
/// </summary>
public class BystialWorldTests
{
    private const int Peer = 1;

    private sealed class Fixture
    {
        public Fixture(Action<ConfigSet>? edit = null, string monsterId = TestCombat.MonsterId)
        {
            var tables = TestCombat.Tables();
            edit?.Invoke(tables);
            var combat = TestCombat.Rules(tables, TestCombat.Point(monsterId: monsterId));
            Rules = BystialRules.From(tables, combat.Config)!;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<CardEarned>(Log);
            Bus.Subscribe<CardEvolved>(Log);
            Bus.Subscribe<BookChanged>(Log);
            Bus.Subscribe<ExpGained>(Log);
            Harness = new WorldHarness(map: TestCombat.OpenMap(TestCombat.MonsterMarker(2_000, 0)),
                events: new EventDispatchHook(Bus), bus: Bus, actions: ActionCatalog.From(tables), combat: combat,
                items: ItemRules.From(tables), progression: GameServer.Progression.ProgressionRules.From(tables), bystial: Rules);
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public BystialRules Rules { get; }
        public EventLog Log { get; } = new();
        public GameWorld World => Harness.World;
        public BystialService Book => World.Bystial!;

        /// <summary>The character of <see cref="Peer"/>, created before it joins with these saved kill counts.</summary>
        public Player Join(params (string Monster, long Kills)[] kills)
        {
            var record = Harness.Characters.Add(Peer, 0, new CharacterState(1, 0, "", 0, 0, 0, 0, 100, 50, 100));
            Harness.Characters.Progression[record.CharacterId] = new ProgressionRecord("{\"v\":1}",
                kills.Select(k => new MonsterKillCount(k.Monster, k.Kills)).ToList());
            var player = Harness.Join(Peer);
            Harness.Ticks(1);
            return player;
        }

        /// <summary>A kill as HitResolver publishes it, delivered in this tick's phase 11; returns its envelope.</summary>
        public EventEnvelope Kill(Player player, string monsterId)
        {
            var envelope = Bus.Publish(new Killed(player.Id, 99, 0, NetIdOf(monsterId), 0, 0), player.CharacterId);
            Harness.Ticks(1);
            return envelope;
        }

        /// <summary>
        /// Requests sent so far applied (phase 1), then the next class 2 batch (every second), its completion, and the phase 10
        /// that follows.
        /// </summary>
        public void Save()
        {
            Harness.Ticks(1);
            Harness.Clock.Advance(1_000);
            Harness.Ticks(3);
        }

        public ushort NetIdOf(string monsterId) => (ushort)Assert.Single(World.Combat!.Monsters, m => m.Id == monsterId).NetId;

        public int Stat(Player player, string statId)
        {
            var stats = player.Combatant!.Stats;
            stats.Refresh();
            return stats.Get(stats.Rules.IdFor(statId));
        }

        public BookState LastBook() => Harness.Transport.BookFor(Peer)[^1];

        public BookCard? CardIn(BookState state, string monsterId) =>
            state.Cards.SingleOrDefault(c => c.MonsterNetId == NetIdOf(monsterId));
    }

    internal sealed class EventLog : IEventConsumer<CardEarned>, IEventConsumer<CardEvolved>, IEventConsumer<BookChanged>,
        IEventConsumer<ExpGained>
    {
        public List<object> Events { get; } = [];
        public void Handle(in EventEnvelope envelope, in CardEarned payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in CardEvolved payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in BookChanged payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ExpGained payload) => Events.Add(payload);
    }

    [Fact]
    public void A_new_client_hears_its_book_with_the_counters_toward_the_first_card()
    {
        var f = new Fixture();
        f.Join(("m1.demon", 37));

        var state = f.LastBook();
        Assert.Equal((BookCause.Initial, (byte)1), (state.Cause, state.SlotCount)); // rank F: one slot (R-14.2-04)
        var demon = Assert.Single(state.Cards);
        Assert.Equal((BystialTier.None, BookLimits.NoSlot, 37L, 100L), (demon.Tier, demon.Slot, demon.Kills, demon.NextTierKills));
    }

    [Fact]
    public void The_100th_demon_kill_earns_the_normal_card_once_its_save_committed()
    {
        var f = new Fixture();
        var player = f.Join(("m1.demon", 99));

        f.Kill(player, "m1.demon");
        f.Harness.Ticks(1);
        Assert.Equal((BystialTier.Normal, -1), f.Book.CardOf(player.CharacterId, "m1.demon")); // held, not committed yet
        var beforeSave = f.CardIn(f.LastBook(), "m1.demon")!;
        Assert.Equal((BystialTier.None, 100L), (beforeSave.Tier, beforeSave.Kills)); // the client sees only what committed
        Assert.Empty(f.Log.Events.OfType<CardEarned>());

        f.Save();

        var earned = Assert.Single(f.Log.Events.OfType<CardEarned>());
        Assert.Equal((player.CharacterId, (int)f.NetIdOf("m1.demon"), BystialTier.Normal, 100L),
            (earned.CharacterId, earned.MonsterDefId, earned.Tier, earned.Kills));
        var state = f.LastBook();
        Assert.Equal(BookCause.CardEarned, state.Cause);
        var card = f.CardIn(state, "m1.demon")!;
        Assert.Equal((BystialTier.Normal, BookLimits.NoSlot, 100L, 500L), (card.Tier, card.Slot, card.Kills, card.NextTierKills));
        var op = Assert.Single(f.Harness.Characters.BookOps);
        Assert.Equal((BystialService.TierOpKey(player.CharacterId, "m1.demon", BystialTier.Normal), BystialOpKind.Earn, 100L),
            (op.OpKey, op.Kind, op.KillCount));
        Assert.Equal(BystialTier.Normal, Assert.Single(f.Harness.Characters.Books[player.CharacterId].Cards).Tier);
    }

    [Fact]
    public void The_same_kill_delivered_twice_evolves_nothing_twice()
    {
        var f = new Fixture();
        var player = f.Join(("m1.demon", 99));
        var envelope = f.Kill(player, "m1.demon");

        // The same event again (a replay after a consumer fault): ignored by event id, by the counter and by the book.
        f.Book.Handle(envelope, new Killed(player.Id, 99, 0, f.NetIdOf("m1.demon"), 0, 0));
        f.World.Achievements!.Handle(envelope, new Killed(player.Id, 99, 0, f.NetIdOf("m1.demon"), 0, 0));
        f.Save();
        f.Save();

        Assert.Equal(100, f.World.Progression!.KillCount(player.CharacterId, "m1.demon"));
        Assert.Single(f.Harness.Characters.BookOps);
        Assert.Single(f.Log.Events.OfType<CardEarned>());
    }

    [Fact]
    public void The_book_reads_the_kill_after_the_achievements_counted_it()
    {
        // Subscription order: AchievementService first, so the count the book reads already holds this kill.
        var f = new Fixture();
        var player = f.Join(("m1.demon", 499), ("m1.alien", 99));
        f.Save();
        f.Kill(player, "m1.alien"); // 100: normal
        f.Kill(player, "m1.demon"); // 500: rare
        f.Save();

        Assert.Equal((BystialTier.Normal, BystialTier.Rare),
            (f.Book.CardOf(player.CharacterId, "m1.alien").Tier, f.Book.CardOf(player.CharacterId, "m1.demon").Tier));
    }

    [Fact]
    public void Kills_saved_before_the_book_earn_their_card_at_login_and_a_jump_is_one_step()
    {
        var f = new Fixture();
        var player = f.Join(("m1.demon", 600), ("m1.cyclopsBoss", 25));
        f.Save();

        Assert.Equal(BystialTier.Rare, f.Book.CardOf(player.CharacterId, "m1.demon").Tier);
        Assert.Equal(BystialTier.Epic, f.Book.CardOf(player.CharacterId, "m1.cyclopsBoss").Tier); // boss ladder 1, 5, 20, 50
        Assert.Equal(2, f.Harness.Characters.BookOps.Count);
        Assert.All(f.Harness.Characters.BookOps, op => Assert.Equal(BystialOpKind.Earn, op.Kind));
        Assert.Equal(2, f.Log.Events.OfType<CardEarned>().Count());
    }

    [Fact]
    public void A_card_evolves_at_the_next_milestone_and_never_goes_down()
    {
        var f = new Fixture();
        var player = f.Join(("m1.demon", 499));
        f.Save();
        f.Kill(player, "m1.demon");
        f.Save();

        var evolved = Assert.Single(f.Log.Events.OfType<CardEvolved>());
        Assert.Equal((BystialTier.Normal, BystialTier.Rare), (evolved.From, evolved.To));
        Assert.Equal(BookCause.CardEvolved, f.LastBook().Cause);

        // A login with fewer kills than the card's tier (a lowered count) keeps the card.
        f.Harness.Leave(Peer);
        f.Harness.Ticks(3);
        f.Harness.Characters.Progression[player.CharacterId] = new ProgressionRecord("{\"v\":1}", [new MonsterKillCount("m1.demon", 3)]);
        var again = f.Harness.Join(Peer);
        f.Harness.Ticks(1);
        Assert.Equal(BystialTier.Rare, f.Book.CardOf(again.CharacterId, "m1.demon").Tier);
    }

    [Fact]
    public void Placing_the_card_raises_its_stats_only_after_the_save_and_confirms_then()
    {
        var f = new Fixture();
        var player = f.Join(("m1.demon", 100));
        f.Save();
        int maxHp = f.Stat(player, StatIds.MaxHp);
        int fireRes = f.Stat(player, "fireRes");

        f.Harness.SendBookChange(Peer, 1, f.NetIdOf("m1.demon"), 0);
        f.Harness.Ticks(1);
        Assert.Empty(f.Harness.Transport.BookResultsFor(Peer)); // an accepted change waits for its commit
        Assert.Equal(maxHp, f.Stat(player, StatIds.MaxHp));
        Assert.Equal((BystialTier.Normal, 0), f.Book.CardOf(player.CharacterId, "m1.demon"));

        f.Save();

        Assert.Equal(maxHp + 10, f.Stat(player, StatIds.MaxHp));
        Assert.Equal(fireRes + 5, f.Stat(player, "fireRes"));
        Assert.Equal(BookReason.None, Assert.Single(f.Harness.Transport.BookResultsFor(Peer)).Reason);
        var state = f.LastBook();
        Assert.Equal((BookCause.Changed, (byte)0), (state.Cause, f.CardIn(state, "m1.demon")!.Slot));
        Assert.Single(f.Log.Events.OfType<BookChanged>());
        var ids = f.Harness.Transport.ReliableIdsFor(Peer);
        Assert.True(ids.LastIndexOf(ReliableMessageId.BookChangeResult) < ids.LastIndexOf(ReliableMessageId.BookState));
        Assert.Equal(new int?[] { 0 }, f.Harness.Characters.Books[player.CharacterId].Cards.Select(c => c.Slot));
    }

    [Fact]
    public void Taking_the_card_out_removes_its_stats()
    {
        var f = new Fixture();
        var player = f.Join(("m1.demon", 100));
        f.Save();
        int maxHp = f.Stat(player, StatIds.MaxHp);
        f.Harness.SendBookChange(Peer, 1, f.NetIdOf("m1.demon"), BookLimits.AnyFreeSlot);
        f.Save();
        Assert.Equal(maxHp + 10, f.Stat(player, StatIds.MaxHp));

        f.Harness.SendBookChange(Peer, 2, f.NetIdOf("m1.demon"), BookLimits.NoSlot);
        f.Save();

        Assert.Equal(maxHp, f.Stat(player, StatIds.MaxHp));
        Assert.Equal(BystialOpKind.TakeOut, f.Harness.Characters.BookOps[^1].Kind);
    }

    [Fact]
    public void Placing_into_the_occupied_slot_swaps_the_cards()
    {
        var f = new Fixture();
        var player = f.Join(("m1.demon", 100), ("m1.alien", 100));
        f.Save();
        int maxHp = f.Stat(player, StatIds.MaxHp);
        int def = f.Stat(player, StatIds.Def);
        f.Harness.SendBookChange(Peer, 1, f.NetIdOf("m1.demon"), 0);
        f.Save();

        f.Harness.SendBookChange(Peer, 2, f.NetIdOf("m1.alien"), 0);
        f.Save();

        Assert.Equal((BystialTier.Normal, -1), f.Book.CardOf(player.CharacterId, "m1.demon"));
        Assert.Equal((BystialTier.Normal, 0), f.Book.CardOf(player.CharacterId, "m1.alien"));
        Assert.Equal((maxHp, def + 2), (f.Stat(player, StatIds.MaxHp), f.Stat(player, StatIds.Def)));
        Assert.Equal(5, f.Stat(player, StatIds.BuildUpResistPercent)); // the alien's build-up resistance, through the stats
        var swap = f.Harness.Characters.BookOps[^1];
        Assert.Equal((BystialOpKind.Swap, "m1.alien", "m1.demon", (int?)0), (swap.Kind, swap.MonsterDefId, swap.OtherMonsterDefId, swap.Slot));
    }

    [Fact]
    public void A_repeated_request_id_gets_its_first_answer_and_changes_nothing_more()
    {
        var f = new Fixture();
        var player = f.Join(("m1.demon", 100));
        f.Save();
        int ops = f.Harness.Characters.BookOps.Count;

        f.Harness.SendBookChange(Peer, 7, f.NetIdOf("m1.demon"), 0);
        f.Harness.SendBookChange(Peer, 7, f.NetIdOf("m1.demon"), 0); // double submit
        f.Save();
        player.LastCombatAtMs = f.Harness.Clock.UtcNowMs; // even in combat now, the repeat gets the first answer
        f.Harness.SendBookChange(Peer, 7, f.NetIdOf("m1.demon"), 0);
        f.Save();

        Assert.Equal(ops + 1, f.Harness.Characters.BookOps.Count);
        Assert.Equal(new[] { BookReason.None, BookReason.None, BookReason.None }, f.Harness.Transport.BookResultsFor(Peer).Select(r => r.Reason));
    }

    [Fact]
    public void Each_refusal_has_its_reason_and_changes_nothing()
    {
        var f = new Fixture();
        var player = f.Join(("m1.demon", 100), ("m1.alien", 100), ("m1.cyclopsBoss", 0));
        f.Save();
        int ops = f.Harness.Characters.BookOps.Count;

        f.Harness.SendBookChange(Peer, 1, f.NetIdOf("m1.cyclopsBoss"), 0); // no kill yet
        f.Harness.SendBookChange(Peer, 2, 999, 0); // no such monster type
        f.Harness.Ticks(1); // the request queue holds a few per tick (LootSettings, InventorySettings)
        f.Harness.SendBookChange(Peer, 3, f.NetIdOf("m1.demon"), 1); // rank F has one slot
        f.Harness.SendBookChange(Peer, 4, f.NetIdOf("m1.demon"), BookLimits.NoSlot); // not in a slot
        f.Harness.Ticks(1);
        f.Harness.SendBookChange(Peer, 5, f.NetIdOf("m1.demon"), 0);
        f.Harness.Ticks(1);
        f.Harness.SendBookChange(Peer, 6, f.NetIdOf("m1.demon"), 0); // already placed
        f.Harness.SendBookChange(Peer, 7, f.NetIdOf("m1.alien"), BookLimits.AnyFreeSlot); // the one slot is taken
        f.Harness.Ticks(1);
        player.LastCombatAtMs = f.Harness.Clock.UtcNowMs;
        f.Harness.SendBookChange(Peer, 8, f.NetIdOf("m1.alien"), 0); // R-14.2-06: not in combat
        f.Save();

        var reasons = f.Harness.Transport.BookResultsFor(Peer).ToDictionary(r => r.RequestId, r => r.Reason);
        Assert.Equal(BookReason.CardNotEarned, reasons[1]);
        Assert.Equal(BookReason.CardNotEarned, reasons[2]);
        Assert.Equal(BookReason.NoFreeBookSlot, reasons[3]);
        Assert.Equal(BookReason.CardNotPlaced, reasons[4]);
        Assert.Equal(BookReason.None, reasons[5]);
        Assert.Equal(BookReason.AlreadyPlaced, reasons[6]);
        Assert.Equal(BookReason.NoFreeBookSlot, reasons[7]);
        Assert.Equal(BookReason.InCombat, reasons[8]);
        Assert.Equal(ops + 1, f.Harness.Characters.BookOps.Count);
        Assert.Equal((BystialTier.Normal, -1), f.Book.CardOf(player.CharacterId, "m1.alien"));
    }

    [Fact]
    public void A_rank_up_opens_more_slots()
    {
        var f = new Fixture();
        var player = f.Join(("m1.demon", 100), ("m1.alien", 100));
        f.Save();
        f.Harness.SendBookChange(Peer, 1, f.NetIdOf("m1.demon"), 0);
        f.Save();
        f.Harness.SendBookChange(Peer, 2, f.NetIdOf("m1.alien"), 1);
        f.Save();
        Assert.Equal(BookReason.NoFreeBookSlot, f.Harness.Transport.BookResultsFor(Peer)[^1].Reason);

        // Rank E (the rank-up itself is WP-M2-3's; its event tells the book).
        f.World.Progression!.ApplyRankUp(player.CharacterId, "rankUp:test");
        f.Save();
        f.Bus.Publish(new RankChanged(player.CharacterId, player.Id, Rank.F, Rank.E), player.CharacterId);
        f.Harness.Ticks(2);
        Assert.Equal((byte)2, f.LastBook().SlotCount);

        f.Harness.SendBookChange(Peer, 3, f.NetIdOf("m1.alien"), 1);
        f.Save();
        Assert.Equal(BookReason.None, f.Harness.Transport.BookResultsFor(Peer)[^1].Reason);
        Assert.Equal((BystialTier.Normal, 1), f.Book.CardOf(player.CharacterId, "m1.alien"));
    }

    [Fact]
    public void The_boss_card_adds_damage_against_beasts_through_the_hit_resolver()
    {
        var f = new Fixture(monsterId: TestCombat.BossId);
        var player = f.Join(("m1.cyclopsBoss", 1));
        f.Save();
        f.Harness.Ticks(2);
        var boss = Assert.Single(f.World.Entities.OfType<Monster>());
        Assert.Equal(0, f.Book.DamagePercent(player, boss)); // in the collection, not placed

        f.Harness.SendBookChange(Peer, 1, f.NetIdOf("m1.cyclopsBoss"), 0);
        f.Save();

        Assert.Same(f.Book, f.World.Hits!.CardBonus);
        Assert.Equal(3, f.Book.DamagePercent(player, boss)); // the cyclops is a beast (placeholder family)
        Assert.Equal(0, f.Book.DamagePercent(boss, player)); // only a player's book, only against a monster
    }

    [Fact]
    public void An_exp_bonus_against_the_family_pays_more_for_its_kills()
    {
        // The demon's card made an exp card for this test: +10 % against demons (the family exp cap is 20).
        var f = new Fixture(edit: tables =>
        {
            foreach (var card in BystialCardDef.Rows(tables).Where(c => c.MonsterId == "m1.demon"))
                (card.EffectKind, card.EffectElement, card.EffectValue) = (BystialEffectKind.FamilyExp, Element.None, 10);
        });
        var player = f.Join(("m1.demon", 100));
        f.Save();
        f.Kill(player, "m1.demon");
        long plain = f.Log.Events.OfType<ExpGained>().Last().Amount;
        f.Harness.SendBookChange(Peer, 1, f.NetIdOf("m1.demon"), 0);
        f.Save();

        f.Kill(player, "m1.demon");

        Assert.Equal(plain * 110 / 100, f.Log.Events.OfType<ExpGained>().Last().Amount);
    }

    [Fact]
    public void Without_the_tables_there_is_no_book_and_a_request_is_refused()
    {
        var tables = TestCombat.Tables();
        var harness = new WorldHarness(actions: ActionCatalog.From(tables), combat: TestCombat.Rules(tables),
            items: ItemRules.From(tables), progression: GameServer.Progression.ProgressionRules.From(tables));
        harness.Join(Peer);
        harness.SendBookChange(Peer, 1, 1, 0);
        harness.Ticks(1);

        Assert.Null(harness.World.Bystial);
        Assert.Equal(BookReason.BookUnavailable, Assert.Single(harness.Transport.BookResultsFor(Peer)).Reason);
        Assert.Empty(harness.Transport.BookFor(Peer));
    }
}
