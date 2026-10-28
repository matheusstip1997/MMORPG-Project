using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Stats;
using Shared.Tests.Simulation.Progression;
using Shared.Util;

namespace Shared.Tests.Data;

/// <summary>
/// The field zone's monster roster (WP-M2-7, spec 12.8, user decision 2026-10-27) keeps the shape the plan promised, on
/// the committed tables: eight types in four level bands plus a field boss, recommended ranges that tile levels 1 to 30,
/// exp on the 3.6 curve, a sold drop per type, and stats that scale with the characters: an even-level character of each
/// root class needs about as many normal hits to kill each type, and falls in about as many of its hits, as against the
/// type's template at band 1 (the demon for melee, the alien for ranged). PLACEHOLDER numbers: a retune that keeps the
/// shape changes the rows, not this test.
/// </summary>
public class FieldRosterShapeTests
{
    private static readonly string[] Classes = ["warrior", "archer", "mage", "assist"];

    /// <summary>The roster by band; the field boss apart.</summary>
    private static readonly (int Band, string Id, string Template)[] Roster =
    [
        (1, "m1.demon", "m1.demon"), (1, "m1.alien", "m1.alien"),
        (2, "m2.imp", "m1.demon"), (2, "m2.spitter", "m1.alien"),
        (3, "m2.wolf", "m1.demon"), (3, "m2.lurker", "m1.alien"),
        (4, "m2.brute", "m1.demon"), (4, "m2.hellhound", "m1.demon"),
    ];

    private const string FieldBoss = "m2.warlord";

    /// <summary>Role factors on HP the rows document (MonsterDef.json): the brute is 1.5x, the wolf 0.9x.</summary>
    private static readonly Dictionary<string, double> HpRole = new() { ["m2.brute"] = 1.5, ["m2.wolf"] = 0.9 };

    private static readonly (int Min, int Max)[] Bands = [(1, 7), (8, 15), (16, 23), (24, 30)];

    private static readonly ConfigSet Set = CommittedTables.Load();

    private static List<T> Rows<T>()
    {
        Assert.True(Set.TryGet(typeof(T), out var table));
        return table.Rows.Cast<T>().ToList();
    }

    private static MonsterDef Monster(string id) => Rows<MonsterDef>().Single(m => m.Id == id);

    [Fact]
    public void Eight_types_sit_in_their_level_bands_and_the_boss_is_level_30()
    {
        foreach (var (band, id, _) in Roster)
        {
            var monster = Monster(id);
            Assert.InRange(monster.Level, Bands[band - 1].Min, Bands[band - 1].Max);
            Assert.False(monster.Boss, id);
        }
        Assert.Equal(8, Roster.Select(r => r.Id).Distinct().Count());
        for (int band = 1; band <= 4; band++)
            Assert.Equal(2, Roster.Count(r => r.Band == band));
        var boss = Monster(FieldBoss);
        Assert.True(boss.Boss);
        Assert.Equal(30, boss.Level);
    }

    [Fact]
    public void Recommended_ranges_tile_every_level_from_1_to_30()
    {
        for (int level = 1; level <= 30; level++)
        {
            int covering = Roster.Count(r => Monster(r.Id).RecommendedMin() <= level && level <= Monster(r.Id).RecommendedMax());
            Assert.True(covering >= 1, $"no type recommended at level {level}");
        }
        foreach (var (_, id, _) in Roster)
        {
            var monster = Monster(id);
            Assert.InRange(monster.Level, monster.RecommendedMin(), monster.RecommendedMax());
        }
    }

    [Fact]
    public void Normal_types_give_the_curve_base_exp_and_the_boss_five_times_it()
    {
        var curve = Rows<ExpCurve>();
        foreach (var (_, id, _) in Roster.Where(r => r.Id.StartsWith("m2.", StringComparison.Ordinal)))
            Assert.Equal(0, Monster(id).BaseExp);
        var boss = Monster(FieldBoss);
        Assert.Equal(5 * curve.Single(c => c.Level == boss.Level).MonsterBaseExp, boss.BaseExp);
    }

    [Fact]
    public void Every_type_drops_a_sell_only_item_the_outposts_buy()
    {
        var items = Rows<ItemDef>().ToDictionary(i => i.Id);
        var prices = Rows<PriceBase>().Select(p => p.ItemId).ToHashSet();
        var tables = Rows<LootTableDef>();
        var entries = Rows<LootTableEntry>();
        foreach (var id in Roster.Select(r => r.Id).Append(FieldBoss))
        {
            var table = tables.Single(t => t.MonsterId == id);
            Assert.Contains(entries.Where(e => e.TableId == table.Id),
                e => items[e.ItemId].Category == ItemCategory.SellOnly && prices.Contains(e.ItemId));
        }
    }

    [Fact]
    public void Every_new_type_has_a_family_a_bystial_card_ladder_and_the_normal_ones_a_quest_item()
    {
        // Spec 14.2 (families, cards; bosses on the boss ladder) and 5.5 (return steps need a QuestItemDrop row): the zone's
        // types join the book and the outpost board like the M1 monsters (WP-M2-7 after WP-M2-8 and WP-M2-6).
        var cards = Rows<BystialCardDef>();
        var drops = Rows<QuestItemDrop>();
        foreach (var id in Roster.Select(r => r.Id).Where(id => id.StartsWith("m2.", StringComparison.Ordinal)).Append(FieldBoss))
        {
            var monster = Monster(id);
            Assert.NotEqual(MonsterFamily.None, monster.Family);
            var ladder = cards.Where(c => c.MonsterId == id).OrderBy(c => c.Tier).Select(c => c.KillsRequired).ToList();
            Assert.Equal(monster.Boss ? new[] { 1, 5, 20, 50 } : new[] { 100, 500, 2_000, 5_000 }, ladder);
            if (!monster.Boss)
                Assert.Contains(drops, d => d.MonsterId == id);
        }
    }

    /// <summary>
    /// Hits to kill the type with the class's normal hit (100 %), and the type's main hit (<see cref="MainHit"/>) to kill
    /// the character, each against the same at band 1: an even-level character against the type, a template-level
    /// character against the template. Within 25 %, after the documented role factor on HP.
    /// </summary>
    [Fact]
    public void An_even_level_character_trades_hits_with_each_type_like_a_new_one_with_its_template()
    {
        var errors = new List<string>();
        var config = CombatConfig.TryBuild(Set, new List<ValidationError>())!;
        Assert.NotNull(config);
        foreach (var (_, id, templateId) in Roster.Where(r => r.Band > 1))
        {
            var monster = Monster(id);
            var template = Monster(templateId);
            foreach (var classId in Classes)
            {
                var (kill, die) = Trade(config, classId, monster);
                var (killRef, dieRef) = Trade(config, classId, template);
                double killRatio = kill / killRef / HpRole.GetValueOrDefault(id, 1.0);
                double dieRatio = die / dieRef;
                if (killRatio < 0.75 || killRatio > 1.25 || dieRatio < 0.75 || dieRatio > 1.25)
                    errors.Add($"{id} vs {classId} at {monster.Level}: kill {kill:F1} hits (ref {killRef:F1}), die {die:F1} hits (ref {dieRef:F1})");
            }
        }
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    private static (double Kill, double Die) Trade(CombatConfig config, string classId, MonsterDef monster)
    {
        int level = monster.Level;
        var classDef = Rows<ClassDef>().Single(c => c.Id == classId);
        // A character ranks up to E around level 5 (F to E: 60 kills, two types, one outpost; R-3.6-04).
        var playerRank = level < 5 ? Rank.F : Rank.E;
        var player = new StatBlock(config.Stats, config.Attributes);
        player.SetBase(config.Ids.MaxHp, classDef.MaxHp);
        player.SetBase(config.Ids.Atk, classDef.Atk);
        player.SetBase(config.Ids.Matk, classDef.Matk);
        player.SetBase(config.Ids.Def, classDef.Def);
        player.SetBase(config.Ids.Mdef, classDef.Mdef);
        Attributes.SetBases(player, config.Attributes, classDef, level);

        var body = new StatBlock(config.Stats, config.Attributes);
        body.SetBase(config.Ids.MaxHp, monster.MaxHp);
        body.SetBase(config.Ids.Atk, monster.Atk);
        body.SetBase(config.Ids.Matk, monster.Matk);
        body.SetBase(config.Ids.Def, monster.Def);
        body.SetBase(config.Ids.Mdef, monster.Mdef);

        var hit = Rows<ActionEffectDef>().Single(e => e.ActionId == "attack." + classId && e.DamagePercent > 0);
        double dealt = Damage(config, player, playerRank, body, monster.Rank, hit);

        var monsterHit = MainHit(monster);
        double taken = Damage(config, body, monster.Rank, player, playerRank, monsterHit);

        return (monster.MaxHp / dealt, player.Get(config.Ids.MaxHp) / taken);
    }

    /// <summary>
    /// The hit a type lands most: a ranged type's projectile (it keeps its distance), a melee type's attack with the
    /// shortest cooldown (the claw, between pounces).
    /// </summary>
    private static ActionEffectDef MainHit(MonsterDef monster)
    {
        var effects = monster.Attacks.SelectMany(a => Rows<ActionEffectDef>().Where(e => e.ActionId == a && e.DamagePercent > 0)).ToList();
        var projectile = effects.FirstOrDefault(e => e.Kind == EffectKind.Projectile);
        if (projectile != null)
            return projectile;
        var filler = monster.Attacks.Select(a => Rows<ActionDef>().Single(d => d.Id == a)).OrderBy(a => a.CooldownMs).First();
        return effects.First(e => e.ActionId == filler.Id);
    }

    private static int Damage(CombatConfig config, StatBlock attacker, Rank attackerRank, StatBlock defender, Rank defenderRank,
        ActionEffectDef effect)
    {
        var kind = effect.DamageKind == EffectDamageKind.Magic ? DamageKind.Magic : DamageKind.Physical;
        var input = DamageCalc.Gather(config, attacker, attackerRank, defender, defenderRank, kind, effect.DamagePercent, 0,
            DamageCalc.NoElement);
        input.NoCrit = true;
        return DamageCalc.Compute(input, config, new Pcg32Rng(1, 1)).Damage;
    }
}
