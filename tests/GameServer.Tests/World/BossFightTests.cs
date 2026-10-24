using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Persistence;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Xunit.Abstractions;

namespace GameServer.Tests.World;

/// <summary>
/// The M1 boss through the tick (WP10, backlog F; M1 exit criterion "the boss is beatable by 3-4 players"), with the
/// repository's tables at 20 Hz: <c>m1.cyclopsBoss</c> at its own spawn point, the four root classes around it pressing
/// their normal attack. Players get a huge HP pool here so the fight always runs to the end; the boss's damage is not
/// what these tests check.
/// </summary>
public class BossFightTests(ITestOutputHelper output)
{
    private const int BossYCm = 1_000;

    private sealed class Setup
    {
        private ushort _sequence;

        public Setup(string[] classes, Action<ConfigSet>? tune = null)
        {
            var tables = TestCombat.Tables();
            Assert.True(tables.TryGet(typeof(ClassDef), out var classRows));
            foreach (ClassDef row in classRows.Rows)
                row.MaxHp = 1_000_000;
            tune?.Invoke(tables);
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Log = CombatEventLog.Attach(Bus);
            Harness = new WorldHarness(
                map: TestCombat.OpenMap(TestCombat.MonsterMarker(0, BossYCm)),
                events: new EventDispatchHook(Bus), bus: Bus,
                actions: ActionCatalog.From(tables),
                combat: TestCombat.Rules(tables, TestCombat.Point(monsterId: TestCombat.BossId)),
                projectiles: ProjectileRules.From(tables));
            for (int i = 0; i < classes.Length; i++)
            {
                int peer = i + 1;
                Harness.Characters.Add(peer, 0, new CharacterState(1, 0, CharacterState.NotPlacedWorldId, 0, 0, 0, 0, 0, 0, 0),
                    classId: classes[i]);
                Players.Add(Harness.Join(peer)); // the first join tick also spawns the boss
            }
            Boss = Assert.Single(World.Entities.OfType<Monster>());
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public CombatEventLog Log { get; }
        public List<Player> Players { get; } = [];
        public Monster Boss { get; }
        public GameWorld World => Harness.World;
        public long NowMs => World.NowMs;

        /// <summary>Puts a player <paramref name="distanceCm"/> from the boss, on the side <paramref name="side"/> (binary angle).</summary>
        public void PlaceAround(Player player, ushort side, int distanceCm)
        {
            var position = Boss.Movement.Position + FixedTrig.Along(side, distanceCm * FixedPoint.RawPerCm);
            player.Movement = new MovementState(position, FacingBoss(position));
            player.SpeedCheckedPosition = position;
            player.RegionIndex = World.Map.Grid.RegionIndexAt(position);
            World.Grid.Update(player);
        }

        public ushort FacingBoss(FixVec2 from)
        {
            var at = Boss.Movement.Position;
            return FixedTrig.AngleOf((long)at.X - from.X, (long)at.Y - from.Y);
        }

        /// <summary>One tick; each player sends a frame facing the boss, with <paramref name="actions"/> for the players listed.</summary>
        public void Step(InputActions actions = InputActions.None, IReadOnlyCollection<Player>? pressing = null)
        {
            _sequence++;
            for (int i = 0; i < Players.Count; i++)
            {
                var player = Players[i];
                bool presses = pressing == null || pressing.Contains(player);
                Harness.SendInputs(i + 1, TestWorlds.Frame(_sequence, facing: FacingBoss(player.Movement.Position),
                    actions: presses ? actions : InputActions.None));
            }
            World.Tick();
        }
    }

    [Fact]
    public void Four_players_break_the_bosses_poise_and_kill_it_and_it_respawns_after_10_minutes()
    {
        var s = new Setup(["warrior", "archer", "mage", "assist"]);
        // Inside the 220 cm cone of attack.warrior, all around the boss (its body is 120 cm).
        for (int i = 0; i < s.Players.Count; i++)
            s.PlaceAround(s.Players[i], (ushort)(i * 16_384), 200);
        var marker = s.Boss.Movement.Position;
        var combatant = s.Boss.Combatant!;
        int maxHp = combatant.MaxHp(s.NowMs);
        Assert.Equal(6_000, maxHp);

        // Everyone presses the normal attack every 20 ticks (refused presses for stamina just wait for regeneration).
        int ticks = 0;
        while (combatant.IsAlive)
        {
            Assert.True(ticks < 12_000, $"the boss still has {combatant.Hp(s.NowMs)} HP after 10 minutes");
            s.Step(ticks % 20 == 0 ? InputActions.Attack : InputActions.None);
            ticks++;
        }
        s.Bus.DispatchQueued();
        output.WriteLine($"4 players with normal attacks only: the boss died after {ticks} ticks ({ticks / 20} s), "
            + $"{s.Log.Events.OfType<Staggered>().Count(e => e.EntityId == s.Boss.Id)} poise breaks");

        // Every player hurt it, its poise broke (R-3.3-01) and it died to a player.
        var damage = s.Log.Events.OfType<Damaged>().Where(d => d.TargetId == s.Boss.Id).ToList();
        Assert.All(s.Players, p => Assert.Contains(damage, d => d.SourceId == p.Id));
        Assert.Equal(maxHp, damage.Sum(d => d.Amount));
        Assert.Contains(s.Log.Events.OfType<Staggered>(), e => e.EntityId == s.Boss.Id);
        var killed = Assert.Single(s.Log.Events.OfType<Killed>(), k => k.VictimId == s.Boss.Id);
        Assert.Contains(s.Players, p => p.Id == killed.KillerId);
        Assert.Equal(3, s.World.Combat!.TryGetMonster(TestCombat.BossId, out var def) ? def.NetId : 0);
        Assert.Equal(def.NetId, killed.VictimDefId);

        // Phase 8 removes it; bossRespawnMs (10 min = 12000 ticks, spec 12.7) later it is back at its marker, full.
        uint diedAt = combatant.DiedAtTick;
        for (int i = 0; i < s.Players.Count; i++)
            s.Harness.Leave(i + 1);
        s.Harness.Ticks(1);
        Assert.Empty(s.World.Entities.OfType<Monster>());
        s.Harness.Ticks((int)(diedAt + (uint)s.World.Combat.BossRespawnTicks - s.World.ServerTick) - 1);
        Assert.Empty(s.World.Entities.OfType<Monster>());
        s.Harness.Ticks(2);
        var respawned = Assert.Single(s.World.Entities.OfType<Monster>());
        Assert.NotEqual(s.Boss.Id, respawned.Id);
        Assert.Equal(marker, respawned.Movement.Position);
        Assert.Equal(maxHp, respawned.Combatant!.Hp(s.NowMs));
        Assert.Equal(respawned.Combatant.Poise.Max, respawned.Combatant.Poise.Current);
    }

    [Fact]
    public void A_poise_break_staggers_the_boss_out_of_a_super_armor_startup()
    {
        // R-3.3-08: super armor ignores flinches, a poise break still staggers.
        var s = new Setup(["warrior"], t => Boss(t).Attacks = ["monster.cyclops.stomp"]);
        s.PlaceAround(s.Players[0], 32_768, 200);
        for (int i = 0; i < 40 && s.Boss.Actions.State != ActionState.Startup; i++)
            s.Step();
        Assert.Equal(ActionState.Startup, s.Boss.Actions.State);

        s.Boss.Combatant!.Poise.Current = 1;
        s.Step(InputActions.Attack);
        for (int i = 0; i < 10 && s.Boss.Actions.State == ActionState.Startup; i++)
            s.Step();

        Assert.Equal(ActionState.Staggered, s.Boss.Actions.State);
    }

    [Theory]
    [InlineData("monster.cyclops.stomp", "warrior", ParryResult.FailedRed)]
    [InlineData("monster.cyclops.stomp", "assist", ParryResult.FailedRed)]
    [InlineData("monster.cyclops.stomp", "archer", ParryResult.FailedRed)]
    [InlineData("monster.cyclops.slam", "warrior", ParryResult.Success)]
    [InlineData("monster.cyclops.slam", "assist", ParryResult.FailedArmor)]
    [InlineData("monster.cyclops.slam", "archer", ParryResult.FailedArmor)]
    [InlineData("monster.cyclops.sweep", "warrior", ParryResult.Success)]
    [InlineData("monster.cyclops.sweep", "assist", ParryResult.Success)]
    [InlineData("monster.cyclops.sweep", "archer", ParryResult.FailedArmor)]
    [InlineData("monster.cyclops.swipe", "warrior", ParryResult.Success)]
    [InlineData("monster.cyclops.swipe", "assist", ParryResult.Success)]
    [InlineData("monster.cyclops.swipe", "mage", ParryResult.Success)]
    [InlineData("monster.cyclops.rockThrow", "archer", ParryResult.Reduced)]
    public void A_timed_parry_against_each_boss_attack_follows_its_color_and_the_armor_class(
        string actionId, string classId, ParryResult expected)
    {
        // R-3.3-04 through the boss's own data: red, yellow (heavy), blue (medium or heavy), green, uncolored (reduced).
        var s = new Setup([classId], t => Boss(t).Attacks = [actionId]);
        s.PlaceAround(s.Players[0], 32_768, 200);
        var attack = s.World.Actions!.Find(actionId)!;
        int fireTick = attack.Effects.Single(e => e.Kind is EffectKind.Strike or EffectKind.Projectile).FireTick;

        // The parry window (its 200 ms Active phase) is 4 ticks: press it 2 ticks before the hit fires.
        for (int i = 0; i < 40 && !(s.Boss.Actions.ActionIndex == attack.Index && s.Boss.Actions.Elapsed >= fireTick - 2); i++)
            s.Step();
        Assert.Equal(attack.Index, s.Boss.Actions.ActionIndex);
        s.Step(InputActions.Parry);
        for (int i = 0; i < 20 && !s.Log.Events.OfType<ParryAttempted>().Any(); i++)
        {
            s.Step();
            s.Bus.DispatchQueued();
        }

        var parry = Assert.Single(s.Log.Events.OfType<ParryAttempted>());
        Assert.Equal((s.Players[0].Id, s.Boss.Id, attack.NetId, attack.Def.ParryClassPve, expected),
            (parry.DefenderId, parry.AttackerId, parry.ActionId, parry.Color, parry.Result));
    }

    private static MonsterDef Boss(ConfigSet tables)
    {
        Assert.True(tables.TryGet(typeof(MonsterDef), out var table));
        return table.Rows.Cast<MonsterDef>().Single(m => m.Id == TestCombat.BossId);
    }
}
