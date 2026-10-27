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

    [Fact]
    public void A_melee_bot_fights_from_the_bosss_body_instead_of_walking_into_it()
    {
        // R-2.2-07: the warrior's approach (55 % of its reach) lies inside the Cyclops's body (contact 158 cm): the bot stops
        // walking at contact plus approachBodyMarginCm and attacks from there.
        var data = Data.Value;
        var behaviour = data.Behaviour("field-combat");
        var boss = MonsterDef.Find(data.Tables, "m1.cyclopsBoss")!;
        var warrior = ClassDef.Find(data.Tables, "warrior")!;
        int contactCm = warrior.BodyRadiusCm + boss.BodyRadiusCm - data.Tables.Single<BodyCollisionSettings>().OverlapToleranceCm;
        var kit = BotKit.For(data.Tables, "warrior");
        Assert.True(kit.NormalAttack.ReachCm * behaviour.ApproachReachPercent / 100 < contactCm, "the case this rule is for");
        var home = data.Markers["m1.spawn.combat.boss"];
        var view = new BotPerception
        {
            Self = home, NowMs = 1_000, HasResources = true, Ready = _ => true,
            Own = new OwnResources { Hp = 150, MaxHp = 150, Mp = 50, MaxMp = 50, Stamina = 100, MaxStamina = 100 },
        };
        view.Monsters.Add(new SeenEntity(9, EntityKind.Monster, (ushort)boss.NetId, home + FixVec2.FromCm(contactCm + 5, 0), true,
            ActionState.Idle, 0, 0));

        var withBodies = new BotBrain(kit, behaviour, data.Grid, home, 200, new Dictionary<int, int>(), new Pcg32Rng(1),
            new Dictionary<int, int> { [boss.NetId] = contactCm });
        withBodies.Think(view);
        Assert.Equal(BotActivity.Fighting, withBodies.Activity);

        var withoutBodies = new BotBrain(kit, behaviour, data.Grid, home, 200, new Dictionary<int, int>(), new Pcg32Rng(1));
        withoutBodies.Think(view);
        Assert.Equal(BotActivity.Approaching, withoutBodies.Activity);
    }

    [Fact]
    public void The_bot_presses_through_the_attack_chain_once_per_hit_when_the_window_opens()
    {
        // R-3.1-10: while a hit with a next hit runs, the predicted machine is busy (nothing is "ready"), so only the chain
        // press can continue the attack: once per hit, from the chain window on.
        var (brain, view) = ChainFight(Data.Value.Behaviour("field-combat"));
        view.Chain = new OwnChain(true, StartTick: 100, WindowOpen: false, NextStaminaCost: 10);
        Assert.Equal(InputActions.None, brain.Think(view).Actions);

        view.NowMs += 50;
        view.Chain = view.Chain with { WindowOpen = true };
        Assert.Equal(InputActions.Attack, brain.Think(view).Actions);
        view.NowMs += 50;
        Assert.Equal(InputActions.None, brain.Think(view).Actions); // this hit was pressed for

        view.NowMs += 50;
        view.Chain = new OwnChain(true, StartTick: 110, WindowOpen: true, NextStaminaCost: 10); // the next hit runs
        Assert.Equal(InputActions.Attack, brain.Think(view).Actions);
        Assert.Equal(2, brain.ChainPresses);
        Assert.Equal(BotActivity.Fighting, brain.Activity);
    }

    [Fact]
    public void No_chain_press_when_switched_off_without_stamina_for_the_next_hit_or_out_of_reach()
    {
        var data = Data.Value;
        var source = data.Behaviour("field-combat");
        var off = data.Behaviour("field-combat-hit1");
        Assert.False(off.PressAttackChain);
        Assert.True(source.PressAttackChain);
        var open = new OwnChain(true, 100, true, 10);

        var (brain, view) = ChainFight(off);
        view.Chain = open;
        Assert.Equal(InputActions.None, brain.Think(view).Actions);

        (brain, view) = ChainFight(source);
        view.Chain = open;
        view.Own = view.Own with { Stamina = 9 };
        Assert.Equal(InputActions.None, brain.Think(view).Actions);

        var reach = BotKit.For(data.Tables, "warrior").NormalAttack.ReachCm;
        (brain, view) = ChainFight(source, distanceCm: reach + 50);
        view.Chain = open;
        Assert.Equal(InputActions.None, brain.Think(view).Actions);
        Assert.Equal(0, brain.ChainPresses);
    }

    [Fact]
    public void A_bot_that_presses_through_the_chain_starts_it_only_when_the_stamina_pays_every_hit()
    {
        // R-3.1-10, QA's ChainStaminaCost: skills spend the stamina first, so hit 1 at the last stamina could never go on.
        var data = Data.Value;
        var kit = BotKit.For(data.Tables, "warrior");
        int chainCost = ActionDef.ChainFrom(data.Tables, "attack.warrior", out _).Sum(id => ActionDef.Find(data.Tables, id)!.StaminaCost);
        Assert.Equal(chainCost, kit.NormalAttack.ChainStaminaCost);
        Assert.True(chainCost > kit.NormalAttack.Def.StaminaCost);
        Assert.All(kit.Actions.Skip(1), a => Assert.Equal(a.Def.StaminaCost, a.ChainStaminaCost));

        var onlyAttack = new FightState(150, 150, 1_000, chainCost - 1, 0, a => a.Slot == 0);
        Assert.Equal(FightMove.Wait, kit.Choose(onlyAttack, data.Behaviour("field-combat")).Move);
        Assert.Equal("attack.warrior", kit.Choose(onlyAttack, data.Behaviour("field-combat-hit1")).Action!.Id);
        var paid = new FightState(150, 150, 1_000, chainCost, 0, a => a.Slot == 0);
        Assert.Equal("attack.warrior", kit.Choose(paid, data.Behaviour("field-combat")).Action!.Id);
    }

    [Fact]
    public void The_loot_mode_only_loots_and_the_fight_mode_never_does()
    {
        var data = Data.Value;
        var (brain, view) = ChainFight(data.Behaviour("m2-loop"));
        view.Ready = _ => true;
        view.LastKillAtMs = view.NowMs;
        view.Drops.Add(new SeenEntity(20, EntityKind.LootDrop, 1, view.Self + FixVec2.FromCm(30, 0), true, ActionState.Idle, 0, 0));

        var fight = brain.Think(view, BotBrainMode.Fight);
        Assert.Equal((BotActivity.Fighting, 0u), (brain.Activity, fight.PickupDropId));
        Assert.False(brain.SawDrop);

        var loot = brain.Think(view, BotBrainMode.Loot);
        Assert.Equal((BotActivity.Looting, 20u), (brain.Activity, loot.PickupDropId));
        Assert.True(brain.SawDrop);

        // The drop was asked for: nothing left to loot, and the loot mode does not fight the demon next to it.
        var idle = brain.Think(view, BotBrainMode.Loot);
        Assert.Equal((BotActivity.Waiting, InputActions.None, 0u), (brain.Activity, idle.Actions, brain.TargetId));
        Assert.False(brain.SawDrop);
    }

    /// <summary>A warrior next to a demon in the middle of its normal attack (the machine busy: nothing ready).</summary>
    private static (BotBrain Brain, BotPerception View) ChainFight(BotBehaviour behaviour, int distanceCm = 60)
    {
        var data = Data.Value;
        var home = data.Markers["m1.spawn.combat.a"];
        var demon = MonsterDef.Find(data.Tables, "m1.demon")!;
        var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), behaviour, data.Grid, home, 200, new Dictionary<int, int>(),
            new Pcg32Rng(3));
        var view = new BotPerception
        {
            Self = home, NowMs = 1_000, HasResources = true, Ready = _ => false,
            Own = new OwnResources { Hp = 150, MaxHp = 150, Mp = 50, MaxMp = 50, Stamina = 100, MaxStamina = 100 },
        };
        view.Monsters.Add(new SeenEntity(7, EntityKind.Monster, (ushort)demon.NetId, home + FixVec2.FromCm(distanceCm, 0), true,
            ActionState.Idle, 0, 0));
        return (brain, view);
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
