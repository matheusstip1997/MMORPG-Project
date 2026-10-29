using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace Shared.Tests.Data;

/// <summary>
/// The Bystial tables of spec 14.2 (WP-M2-8): BystialCardDef, BystialSlots, BystialFamilyBonus, BystialStatCap,
/// BystialEffectCap, BystialCardArt, and the MonsterDef family. Each DataValidator rule fails on a broken row; the
/// committed tables pass.
/// </summary>
public class BystialTablesTests
{
    private static MonsterDef Monster(string id, int netId, MonsterFamily family, bool boss = false) => new()
    {
        Id = id, NetId = netId, VisualId = "", MaxHp = 1, PoiseMax = 1, BodyRadiusCm = 1, MoveSpeedCmPerSec = 1, AggroRangeCm = 1,
        LeashRangeCm = 2, Boss = boss, Family = family,
    };

    private static BystialCardDef Card(string monster, BystialTier tier, int kills, string stat = "maxHp",
        BystialEffectKind kind = BystialEffectKind.ElementResistance, Element element = Element.Fire, int value = 5) => new()
    {
        Id = $"{monster}.{tier}", MonsterId = monster, Tier = tier, KillsRequired = kills, StatId = stat, Flat = 10,
        EffectKind = kind, EffectElement = kind == BystialEffectKind.ElementResistance ? element : Element.None, EffectValue = value,
    };

    private static IEnumerable<BystialCardDef> Ladder(string monster, params int[] kills) =>
        kills.Select((k, i) => Card(monster, (BystialTier)(i + 1), k));

    private static IEnumerable<BystialSlots> Slots() =>
        Enumerable.Range(0, Ranks.Count).Select(r => new BystialSlots { Rank = (Rank)r, Slots = r + 1 });

    private static ConfigSet Set(IEnumerable<BystialCardDef>? cards = null, IEnumerable<BystialSlots>? slots = null,
        IEnumerable<BystialFamilyBonus>? families = null, IEnumerable<BystialStatCap>? statCaps = null,
        IEnumerable<BystialEffectCap>? effectCaps = null, IEnumerable<BystialCardArt>? art = null, MonsterFamily demonFamily = MonsterFamily.Demon)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(StatDef), 1, new object[]
        {
            new StatDef { Id = "maxHp", Min = 1, Max = 1000 }, new StatDef { Id = "atk", Min = 0, Max = 1000 },
            new StatDef { Id = "fireRes", Min = -900, Max = 100 },
        }));
        set.Add(new ConfigTable(typeof(MonsterDef), 2, new object[]
        {
            Monster("m1.demon", 1, demonFamily), Monster("m1.cyclopsBoss", 3, MonsterFamily.Beast, boss: true),
        }));
        set.Add(new ConfigTable(typeof(BystialCardDef), 1,
            (cards ?? Ladder("m1.demon", 100, 500, 2_000, 5_000).Concat(Ladder("m1.cyclopsBoss", 1, 5, 20, 50))).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(BystialSlots), 1, (slots ?? Slots()).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(BystialFamilyBonus), 1, (families ?? new[]
        {
            new BystialFamilyBonus { Id = "demon.2", Family = MonsterFamily.Demon, CardsPlaced = 2, StatId = "maxHp", Flat = 20 },
            new BystialFamilyBonus { Id = "demon.4", Family = MonsterFamily.Demon, CardsPlaced = 4, EffectKind = BystialEffectKind.FamilyDamage, EffectValue = 5 },
        }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(BystialStatCap), 1, (statCaps ?? new[]
        {
            new BystialStatCap { StatId = "maxHp", MaxFlat = 200 }, new BystialStatCap { StatId = "atk", MaxFlat = 40 },
        }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(BystialEffectCap), 1, (effectCaps ?? new[]
        {
            new BystialEffectCap { Kind = BystialEffectKind.ElementResistance, MaxValue = 30 },
            new BystialEffectCap { Kind = BystialEffectKind.FamilyDamage, MaxValue = 20 },
        }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(BystialCardArt), 1, (art ?? new[]
        {
            new BystialCardArt { Id = "m1.demon", PlaceholderColor = "#c8453c" }, new BystialCardArt { Id = "m1.cyclopsBoss", PlaceholderColor = "#3b7fd6" },
        }).Cast<object>().ToList()));
        return set;
    }

    /// <summary>Errors of the Bystial tables and the MonsterDef family (the test set leaves the monsters' visuals out).</summary>
    private static List<ValidationError> Validate(ConfigSet set) =>
        TableValidator.Validate(set).Where(e => e.Table.StartsWith("Bystial", StringComparison.Ordinal) || e.Field == "Family").ToList();

    [Fact]
    public void Valid_tables_pass() => Assert.Empty(Validate(Set()));

    [Fact]
    public void The_committed_tables_pass() => Assert.Empty(TableValidator.Validate(Shared.Tests.Simulation.Progression.CommittedTables.Load()));

    [Fact]
    public void A_carded_monster_needs_a_family() =>
        Assert.Contains(Validate(Set(demonFamily: MonsterFamily.None)), e => e.Table == "BystialCardDef" && e.Field == "MonsterId");

    [Fact]
    public void A_ladder_starts_at_normal_with_no_gap_and_each_tier_once()
    {
        var gap = Ladder("m1.demon", 100, 500).Where(c => c.Tier != BystialTier.Normal);
        Assert.Contains(Validate(Set(cards: gap)), e => e.Table == "BystialCardDef" && e.Field == "Tier");
        var twice = Ladder("m1.demon", 100, 500).Append(Card("m1.demon", BystialTier.Rare, 600)).Select((c, i) => { c.Id += i; return c; });
        Assert.Contains(Validate(Set(cards: twice)),
            e => e.Table == "BystialCardDef" && e.Field == "Tier");
        var none = new[] { Card("m1.demon", BystialTier.None, 100) };
        Assert.Contains(Validate(Set(cards: none)), e => e.Table == "BystialCardDef" && e.Field == "Tier");
    }

    [Fact]
    public void Kills_rise_strictly_with_the_tier() =>
        Assert.Contains(Validate(Set(cards: Ladder("m1.demon", 100, 100))), e => e.Table == "BystialCardDef" && e.Field == "KillsRequired");

    [Fact]
    public void Effects_name_their_element_and_value()
    {
        var noElement = Card("m1.demon", BystialTier.Normal, 100);
        noElement.EffectElement = Element.None;
        Assert.Contains(Validate(Set(cards: new[] { noElement })), e => e.Table == "BystialCardDef" && e.Field == "EffectElement");
        var noValue = Card("m1.demon", BystialTier.Normal, 100, value: 0);
        Assert.Contains(Validate(Set(cards: new[] { noValue })), e => e.Table == "BystialCardDef" && e.Field == "EffectValue");
        var stray = Card("m1.demon", BystialTier.Normal, 100, kind: BystialEffectKind.FamilyDamage);
        stray.EffectElement = Element.Ice;
        Assert.Contains(Validate(Set(cards: new[] { stray })), e => e.Table == "BystialCardDef" && e.Field == "EffectElement");
        var noBonus = Card("m1.demon", BystialTier.Normal, 100);
        noBonus.Flat = 0;
        Assert.Contains(Validate(Set(cards: new[] { noBonus })), e => e.Table == "BystialCardDef" && e.Field == "Flat");
    }

    [Fact]
    public void Every_bonus_is_capped()
    {
        var noStatCap = Set(statCaps: new[] { new BystialStatCap { StatId = "atk", MaxFlat = 40 } });
        Assert.Contains(Validate(noStatCap), e => e.Table == "BystialCardDef" && e.Field == "StatId");
        Assert.Contains(Validate(noStatCap), e => e.Table == "BystialFamilyBonus" && e.Field == "StatId");
        var noEffectCap = Set(effectCaps: new[] { new BystialEffectCap { Kind = BystialEffectKind.ElementResistance, MaxValue = 30 } });
        Assert.Contains(Validate(noEffectCap), e => e.Table == "BystialFamilyBonus" && e.Field == "EffectKind");
        var twice = Set(effectCaps: new[]
        {
            new BystialEffectCap { Kind = BystialEffectKind.ElementResistance, MaxValue = 30 },
            new BystialEffectCap { Kind = BystialEffectKind.ElementResistance, MaxValue = 10 },
            new BystialEffectCap { Kind = BystialEffectKind.FamilyDamage, MaxValue = 20 },
        });
        Assert.Contains(Validate(twice), e => e.Table == "BystialEffectCap" && e.Field == "Kind");
    }

    [Fact]
    public void Every_rank_has_one_slot_row_and_slots_never_shrink()
    {
        Assert.Contains(Validate(Set(slots: Slots().Where(s => s.Rank != Rank.D))), e => e.Table == "BystialSlots" && e.Field == "Rank");
        var shrinking = Slots().Select(s => s.Rank == Rank.S ? new BystialSlots { Rank = Rank.S, Slots = 1 } : s);
        Assert.Contains(Validate(Set(slots: shrinking)), e => e.Table == "BystialSlots" && e.Field == "Slots");
        var tooMany = Slots().Select(s => s.Rank == Rank.SS ? new BystialSlots { Rank = Rank.SS, Slots = BookLimits.MaxSlots + 1 } : s);
        Assert.Contains(Validate(Set(slots: tooMany)), e => e.Table == "BystialSlots" && e.Field == "Slots");
    }

    [Fact]
    public void A_family_bonus_has_one_row_per_count_within_the_slots_and_gives_something()
    {
        var twice = new[]
        {
            new BystialFamilyBonus { Id = "a", Family = MonsterFamily.Demon, CardsPlaced = 2, StatId = "maxHp", Flat = 1 },
            new BystialFamilyBonus { Id = "b", Family = MonsterFamily.Demon, CardsPlaced = 2, StatId = "maxHp", Flat = 2 },
        };
        Assert.Contains(Validate(Set(families: twice)), e => e.Table == "BystialFamilyBonus" && e.Field == "CardsPlaced");
        var beyond = new[] { new BystialFamilyBonus { Id = "a", Family = MonsterFamily.Demon, CardsPlaced = 9, StatId = "maxHp", Flat = 1 } };
        Assert.Contains(Validate(Set(families: beyond)), e => e.Table == "BystialFamilyBonus" && e.Field == "CardsPlaced");
        var nothing = new[] { new BystialFamilyBonus { Id = "a", Family = MonsterFamily.Demon, CardsPlaced = 2 } };
        Assert.Contains(Validate(Set(families: nothing)), e => e.Table == "BystialFamilyBonus" && e.Field == "EffectKind");
        var noFamily = new[] { new BystialFamilyBonus { Id = "a", CardsPlaced = 2, StatId = "maxHp", Flat = 1 } };
        Assert.Contains(Validate(Set(families: noFamily)), e => e.Table == "BystialFamilyBonus" && e.Field == "Family");
    }

    [Fact]
    public void Every_carded_monster_has_card_art() =>
        Assert.Contains(Validate(Set(art: new[] { new BystialCardArt { Id = "m1.demon", PlaceholderColor = "#c8453c" } })),
            e => e.Table == "BystialCardDef" && e.Message.Contains("m1.cyclopsBoss"));

    [Fact]
    public void The_shipped_ladders_follow_the_decision_of_2026_10_27()
    {
        var set = Shared.Tests.Simulation.Progression.CommittedTables.Load();
        var cards = BystialCardDef.Rows(set);
        foreach (var monster in cards.Select(c => c.MonsterId).Distinct())
        {
            var def = MonsterDef.Find(set, monster)!;
            var kills = cards.Where(c => c.MonsterId == monster).OrderBy(c => c.Tier).Select(c => c.KillsRequired);
            Assert.Equal(def.Boss ? new[] { 1, 5, 20, 50 } : new[] { 100, 500, 2_000, 5_000 }, kills);
        }
        var slots = (set.TryGet(typeof(BystialSlots), out var table) ? table.Rows.Cast<BystialSlots>() : Enumerable.Empty<BystialSlots>()).OrderBy(s => s.Rank).Select(s => s.Slots);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, slots);
        Assert.Equal((MonsterFamily.Demon, MonsterFamily.Aberration, MonsterFamily.Beast),
            (MonsterDef.Find(set, "m1.demon")!.Family, MonsterDef.Find(set, "m1.alien")!.Family, MonsterDef.Find(set, "m1.cyclopsBoss")!.Family));
    }
}
