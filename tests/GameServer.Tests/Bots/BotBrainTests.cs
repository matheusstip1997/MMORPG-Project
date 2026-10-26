using BotClient;
using BotClient.Brain;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Util;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.Bots;

/// <summary>Backlog J: the bot's decisions, pure over the repository's tables and the M1 world.</summary>
public class BotBrainTests
{
    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    [Theory]
    [InlineData(0, 1, MoveKeys.W, 0)]
    [InlineData(1, 0, MoveKeys.D, 16_384)]
    [InlineData(0, -1, MoveKeys.S, 32_768)]
    [InlineData(-1, 0, MoveKeys.A, 49_152)]
    [InlineData(1, 1, MoveKeys.W | MoveKeys.D, 8_192)]
    public void Keys_and_facing_point_where_the_bot_walks(double dx, double dy, MoveKeys keys, int facing)
    {
        Assert.Equal(keys, GridPath.KeysToward(dx * 100, dy * 100));
        Assert.Equal((ushort)facing, GridPath.FacingToward(dx * 100, dy * 100));
    }

    [Fact]
    public void A_path_leads_from_the_city_spawn_through_the_gate_to_every_home()
    {
        var data = Data.Value;
        var city = data.Markers["m1.spawn.city"];
        foreach (var home in data.Behaviour("field-combat").HomeMarkerIds)
        {
            var path = GridPath.Find(data.Grid, city, data.Markers[home]);
            Assert.NotNull(path);
            Assert.Equal(data.Markers[home], path![^1]);
            Assert.True(GridPath.Clear(data.Grid, city, GridPath.NextWaypoint(data.Grid, city, path)));
        }
    }

    [Fact]
    public void The_kit_uses_the_strongest_ready_action_in_reach_and_backs_off_when_hurt_without_a_heal()
    {
        var data = Data.Value;
        var behaviour = data.Behaviour("field-combat");
        var kit = BotKit.For(data.Tables, "warrior");
        Assert.Equal("attack.warrior", kit.NormalAttack.Id);
        Assert.Equal(ActionBindings.For(data.Tables, "warrior")!.SkillSlots.Count(s => s.Length > 0) + 1, kit.Actions.Count);

        var strongest = kit.Actions.Where(a => a.Damages).OrderByDescending(a => a.DamagePercent).First();
        var all = kit.Choose(new FightState(150, 150, 1_000, 1_000, 0, _ => true), behaviour);
        Assert.Equal((FightMove.Use, strongest.Id), (all.Move, all.Action!.Id));

        var onlyAttack = kit.Choose(new FightState(150, 150, 1_000, 1_000, 0, a => a.Slot == 0), behaviour);
        Assert.Equal("attack.warrior", onlyAttack.Action!.Id);

        var outOfReach = kit.Choose(new FightState(150, 150, 1_000, 1_000, 5_000, _ => true), behaviour);
        Assert.Equal(FightMove.Wait, outOfReach.Move);

        // The warrior's kit heals nobody: at 10 % HP it backs off (QA: below 20 %).
        var hurt = kit.Choose(new FightState(15, 150, 1_000, 1_000, 0, _ => true), behaviour);
        Assert.Equal(FightMove.BackOff, hurt.Move);
    }

    [Fact]
    public void The_same_seed_and_perceptions_give_the_same_inputs()
    {
        var first = Play(seed: 11);
        Assert.Equal(first, Play(seed: 11));
        Assert.NotEqual(first, Play(seed: 12)); // other dodge rolls and reaction delays
        Assert.Contains(first, c => c.Actions == InputActions.Dodge);
        Assert.Contains(first, c => c.Actions == InputActions.Attack || c.SkillSlot != 0);
    }

    /// <summary>A demon next to the bot that starts an attack every 10 ticks; 600 ticks at 50 ms.</summary>
    private static List<BotCommand> Play(ulong seed)
    {
        var data = Data.Value;
        var source = data.Behaviour("field-combat");
        var behaviour = new BotBehaviour
        {
            Id = "test", HomeMarkerIds = source.HomeMarkerIds, TargetSearchCm = source.TargetSearchCm,
            ApproachReachPercent = source.ApproachReachPercent, HealBelowPercent = source.HealBelowPercent,
            BackOffBelowPercent = source.BackOffBelowPercent, BackOffDistanceCm = source.BackOffDistanceCm, BackOffMs = source.BackOffMs,
            DodgeChancePercent = 50, ReactionMinMs = 100, ReactionMaxMs = 600, PickupSearchCm = source.PickupSearchCm,
            PickupReachPercent = source.PickupReachPercent, LootAfterKillMs = source.LootAfterKillMs,
            StuckTimeoutMs = source.StuckTimeoutMs, StuckMinMoveCm = source.StuckMinMoveCm,
        };
        var home = data.Markers["m1.spawn.combat.a"];
        var demon = MonsterDef.Find(data.Tables, "m1.demon")!;
        var attack = ActionDef.Find(data.Tables, demon.Attacks[0])!;
        var reach = new Dictionary<int, int> { [attack.NetId] = BotKit.ReachCm(data.Tables, attack.Id) };
        var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), behaviour, data.Grid, home, 200, reach, new Pcg32Rng(seed));

        var view = new BotPerception
        {
            Self = home, HasResources = true, Own = new OwnResources { Hp = 150, MaxHp = 150, Mp = 50, MaxMp = 50, Stamina = 100, MaxStamina = 100 },
        };
        var commands = new List<BotCommand>();
        bool busy = false;
        for (int tick = 0; tick < 600; tick++)
        {
            view.NowMs = tick * 50L;
            view.Ready = _ => !busy;
            view.Monsters.Clear();
            uint start = (uint)(tick / 10 * 10);
            view.Monsters.Add(new SeenEntity(7, EntityKind.Monster, (ushort)demon.NetId, home + FixVec2.FromCm(60, 0), true,
                tick % 10 < 3 ? ActionState.Startup : ActionState.Idle, (ushort)attack.NetId, start));
            var command = brain.Think(view);
            busy = command.Actions != InputActions.None || command.SkillSlot != 0 ? true : tick % 7 == 0 ? false : busy;
            commands.Add(command);
        }
        return commands;
    }
}
