using GameServer.AI;
using GameServer.Combat;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Actions;

namespace GameServer.Tests.AI;

/// <summary>
/// How far a monster attack reaches, judged from the same rows hit resolution uses (backlog F): the committed monster
/// attacks. A warrior's body circle is 40 cm; facing 0 is +Y.
/// </summary>
public class MonsterAttackTests
{
    private static readonly int Body = FixedPoint.FromCm(40);

    private static (MonsterAiRules Rules, CombatRules Combat) Build()
    {
        var tables = TestCombat.Tables();
        var combat = TestCombat.Rules(tables);
        var rules = new MonsterAiRules(tables.Single<MonsterAiSettings>(), combat, ActionCatalog.From(tables),
            TestMaps.Open(), ProjectileRules.From(tables));
        return (rules, combat);
    }

    private static MonsterAttack Attack(string monsterId, string actionId)
    {
        var (rules, combat) = Build();
        Assert.True(combat.TryGetMonster(monsterId, out var monster));
        return Assert.Single(rules.AttacksOf(monster), a => a.Action.Id == actionId);
    }

    private static bool Hits(MonsterAttack attack, int yCm, int xCm = 0) =>
        attack.WouldHit(FixVec2.Zero, 0, FixVec2.FromCm(xCm, yCm), Body);

    [Fact]
    public void The_claw_reaches_its_cone_plus_the_target_body()
    {
        var claw = Attack("m1.demon", "monster.demon.claw");

        Assert.True(Hits(claw, 239));
        Assert.False(Hits(claw, 241));   // 200 cm cone + 40 cm body
        Assert.False(Hits(claw, -100));  // behind
        Assert.True(Hits(claw, 100, 100)); // 45 degrees, inside the 60-degree half angle
    }

    [Fact]
    public void The_pounce_reaches_past_its_dash()
    {
        var pounce = Attack("m1.demon", "monster.demon.pounce");

        // Dash 300 cm, then a capsule 150 cm long with a 60 cm radius: 300 + 150 + 60 + 40 = 550.
        Assert.True(Hits(pounce, 549));
        Assert.False(Hits(pounce, 551));
        Assert.True(Hits(pounce, 250)); // still inside the capsule at the end of the dash
        Assert.False(Hits(pounce, 150)); // dashed past: behind the strike's origin by more than the radii
        Assert.True(pounce.NominalReachCm > Attack("m1.demon", "monster.demon.claw").NominalReachCm);
    }

    [Fact]
    public void The_spit_reaches_its_projectile_range()
    {
        var spit = Attack("m1.alien", "monster.alien.spit");

        // 1200 cm range + 20 cm projectile radius + 40 cm body, in any direction the aim points.
        Assert.True(Hits(spit, 1_259));
        Assert.False(Hits(spit, 1_261));
        Assert.True(spit.WouldHit(FixVec2.Zero, 16384, FixVec2.FromCm(-500, -500), Body));
    }

    [Fact]
    public void Attacks_keep_the_table_order_and_unknown_monsters_have_none()
    {
        var (rules, combat) = Build();
        Assert.True(combat.TryGetMonster("m1.alien", out var alien));

        Assert.Equal(new[] { "monster.alien.slap", "monster.alien.spit" }, rules.AttacksOf(alien).Select(a => a.Action.Id));
        Assert.Empty(rules.AttacksOf(new MonsterDef { Id = "m1.nobody" }));
    }

    [Fact]
    public void Judging_an_attack_allocates_nothing()
    {
        var pounce = Attack("m1.demon", "monster.demon.pounce");
        Hits(pounce, 300);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            Hits(pounce, i);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
