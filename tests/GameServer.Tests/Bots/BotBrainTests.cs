using BotClient;
using BotClient.Brain;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Util;
using Shared.World;
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
    public void Paths_to_the_field_zone_camps_step_over_walkable_neighbours_without_cutting_corners()
    {
        var data = Data.Value;
        var grid = data.Grid;
        var city = data.Markers["m1.spawn.city"];
        foreach (var home in data.Behaviour("field-zone").HomeMarkerIds)
        {
            var path = GridPath.Find(grid, city, data.Markers[home]);
            Assert.NotNull(path);
            Assert.Equal(data.Markers[home], path![^1]);
            int column = grid.ColumnOf(city.X), row = grid.RowOf(city.Y);
            for (int i = 0; i < path.Count - 1; i++)
            {
                int c = grid.ColumnOf(path[i].X), r = grid.RowOf(path[i].Y);
                int dx = c - column, dy = r - row;
                Assert.True(Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1 && (dx != 0 || dy != 0), $"{home}: step {i} is not a neighbour");
                Assert.True(grid.IsWalkableCell(c, r), $"{home}: step {i} is not walkable");
                if (dx != 0 && dy != 0)
                    Assert.True(grid.IsWalkableCell(column + dx, row) && grid.IsWalkableCell(column, row + dy), $"{home}: step {i} cuts a corner");
                (column, row) = (c, r);
            }
        }
    }

    [Fact]
    public void A_far_path_searches_near_its_line_and_allocates_no_grid_sized_buffer()
    {
        // m2-zone (QA M2 wave 1, O6): the breadth-first search flooded the field zone's grid and allocated a grid-sized array
        // per call; 50 bots replanning stalled the bot thread for seconds and every stall came back as prediction corrections.
        var data = Data.Value;
        var city = data.Markers["m1.spawn.city"];
        var homes = data.Behaviour("field-zone").HomeMarkerIds.Select(h => data.Markers[h]).ToList();
        var farthest = homes.OrderByDescending(h => GridPath.DistanceCm(city, h)).First();
        Assert.True(GridPath.DistanceCm(city, farthest) > 30_000, "the farthest camp is over 300 m away");

        Assert.NotNull(GridPath.Find(data.Grid, city, farthest)); // warms the reused search state up
        Assert.True(GridPath.LastExpandedCells < data.Grid.CellCount / 10,
            $"expanded {GridPath.LastExpandedCells} of {data.Grid.CellCount} cells");

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.NotNull(GridPath.Find(data.Grid, city, farthest));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < data.Grid.CellCount, $"allocated {allocated} bytes for a grid of {data.Grid.CellCount} cells");
    }

    [Fact]
    public void A_goal_inside_a_wall_has_no_path_at_once_and_a_goal_on_the_wall_s_edge_is_reached()
    {
        // m2-zone: back-off legs ending inside a ridge searched the whole grid (624 000 cells, about 0.5 s) to find no path.
        var data = Data.Value;
        var grid = data.Grid;
        int half = FixedPoint.FromCm(grid.CellSizeCm) / 2;
        FixVec2 Center(int c, int r) => new(grid.ColumnStartRaw(c) + half, grid.RowStartRaw(r) + half);
        var city = data.Markers["m1.spawn.city"];

        int inside = Enumerable.Range(0, grid.CellCount).First(cell =>
        {
            int c = cell % grid.Width, r = cell / grid.Width;
            return grid.InBounds(c - 1, r - 1) && grid.InBounds(c + 1, r + 1) && !grid.IsWalkableCell(c, r)
                   && !grid.IsWalkableCell(c - 1, r) && !grid.IsWalkableCell(c + 1, r) && !grid.IsWalkableCell(c, r - 1) && !grid.IsWalkableCell(c, r + 1);
        });
        Assert.Null(GridPath.Find(grid, city, Center(inside % grid.Width, inside / grid.Width)));
        Assert.Equal(0, GridPath.LastExpandedCells);

        // East of the city spawn along its row, the first blocked cell: entered from its walkable west side, as before.
        int column = grid.ColumnOf(city.X), row = grid.RowOf(city.Y);
        while (grid.IsWalkableCell(column + 1, row))
            column++;
        var edge = Center(column + 1, row);
        var path = GridPath.Find(grid, city, edge);
        Assert.NotNull(path);
        Assert.Equal(edge, path![^1]);
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
    public void The_bot_presses_through_the_attack_chain_when_the_window_opens_and_again_at_the_link()
    {
        // R-3.1-10: while a hit with a next hit runs, the predicted machine is busy (nothing is "ready"), so only the chain
        // press can continue the attack: when the chain window opens, and once more for the link tick.
        var (brain, view) = ChainFight(Data.Value.Behaviour("field-combat"));
        view.Chain = new OwnChain(true, StartTick: 100, WindowOpen: false, AtLink: false, NextStaminaCost: 10);
        Assert.Equal(InputActions.None, brain.Think(view).Actions);

        view.NowMs += 50;
        view.Chain = view.Chain with { WindowOpen = true };
        Assert.Equal(InputActions.Attack, brain.Think(view).Actions);
        view.NowMs += 50;
        Assert.Equal(InputActions.None, brain.Think(view).Actions); // this hit was pressed for

        view.NowMs += 50;
        view.Chain = view.Chain with { AtLink = true };
        Assert.Equal(InputActions.Attack, brain.Think(view).Actions); // the second press, for the link tick
        view.NowMs += 50;
        Assert.Equal(InputActions.None, brain.Think(view).Actions);
        Assert.Equal(1, brain.ChainPresses); // hits pressed for

        view.NowMs += 50;
        view.Chain = new OwnChain(true, StartTick: 110, WindowOpen: true, AtLink: true, NextStaminaCost: 10); // the next hit runs
        Assert.Equal(InputActions.Attack, brain.Think(view).Actions); // window and link at once: one press
        view.NowMs += 50;
        Assert.Equal(InputActions.None, brain.Think(view).Actions);
        Assert.Equal(2, brain.ChainPresses);
        Assert.Equal(BotActivity.Fighting, brain.Activity);
    }

    [Fact]
    public void A_chain_press_reaches_the_server_with_its_machine_behind_or_ahead_of_the_prediction()
    {
        // R-3.1-10, CI run 40564316522 ("chain 2/0"): the server's machine steps once per server tick, not once per input
        // frame. Input catch-up (ServerLoop.inputBacklogTicks: two queued frames in one step) puts it behind the bot's
        // prediction, ticks without a frame put it ahead, and it takes the press only from chainFromMs to the hit's end.
        // Pressed only when the window opened, it was dropped from 2 ticks behind (traced under CPU starvation: 4 behind);
        // the second press for the link tick reaches it up to link - chainFrom ticks behind, the first one up to
        // total - 2 - chainFrom ahead, and the second never moves the server on to hit 3 early.
        var catalog = ActionCatalog.From(Data.Value.Tables);
        var loadout = catalog.Loadout("warrior");
        var hit1 = catalog[loadout.AttackIndex];
        int behind = hit1.ChainLinkTick - hit1.ChainFromTick, ahead = hit1.TotalTicks - 2 - hit1.ChainFromTick;
        Assert.True(behind >= 2 && ahead >= 2, $"the warrior's hit 1 leaves {behind} ticks behind and {ahead} ahead");
        for (int offset = -behind; offset <= ahead; offset++)
            Assert.True(ServerStartsTheNextHit(catalog, loadout, offset), $"server {offset} ticks off the prediction");
    }

    [Fact]
    public void No_chain_press_when_switched_off_without_stamina_for_the_next_hit_or_out_of_reach()
    {
        var data = Data.Value;
        var source = data.Behaviour("field-combat");
        var off = data.Behaviour("field-combat-hit1");
        Assert.False(off.PressAttackChain);
        Assert.True(source.PressAttackChain);
        var open = new OwnChain(true, 100, true, true, 10);

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
    public void A_warrior_that_does_not_dodge_starts_its_chain_before_any_opening_skill_is_ready_again()
    {
        // The m2-loop scenario test (BotBehaviourTests, no dodges) relies on this: from full stamina the kit opens with its
        // skills, biggest multiplier first, and the normal attack, which waits for the stamina of the whole chain
        // (R-3.1-10), comes once they are spent and before any of them is ready again, so the first fight has a chain.
        // Stamina from StartingValuesM1 (regen after the delay since the last spend); each press waits the action's length
        // plus the longest reaction, the slowest the bot presses.
        var data = Data.Value;
        var behaviour = data.Behaviour("m2-loop");
        var kit = BotKit.For(data.Tables, "warrior");
        var start = data.Tables.Single<StartingValuesM1>();
        var warrior = ClassDef.Find(data.Tables, "warrior")!;
        int mp = warrior.MaxMp, staminaAtSpend = start.StaminaPool;
        long t = 0, spentAt = -start.StaminaRegenDelayMs;
        var readyAt = new Dictionary<string, long>();
        var used = new List<string>();
        while (true)
        {
            Assert.True(t < 60_000, $"no normal attack after {string.Join(", ", used)}");
            int stamina = (int)Math.Min(start.StaminaPool,
                staminaAtSpend + Math.Max(0, t - spentAt - start.StaminaRegenDelayMs) * start.StaminaRegenPerSec / 1000);
            long now = t;
            var choice = kit.Choose(new FightState(warrior.MaxHp, warrior.MaxHp, mp, stamina, 0,
                a => !readyAt.TryGetValue(a.Id, out long at) || now >= at), behaviour);
            if (choice.Move != FightMove.Use)
            {
                t += 50;
                continue;
            }
            var action = choice.Action!;
            if (action == kit.NormalAttack)
                break;
            Assert.DoesNotContain(action.Id, used); // a skill ready again before the chain
            used.Add(action.Id);
            var def = action.Def;
            staminaAtSpend = stamina - def.StaminaCost;
            spentAt = t;
            mp -= def.MpCost;
            readyAt[action.Id] = t + def.CooldownMs;
            t +=def.StartupMs + def.ChannelMs + def.ActiveMs + def.RecoveryMs + behaviour.ReactionMaxMs;
        }
        Assert.Equal(kit.Actions.Where(a => a.Damages && a != kit.NormalAttack).Select(a => a.Id).OrderBy(id => id),
            used.OrderBy(id => id));
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

    [Fact]
    public void The_all_mode_the_loop_fight_plays_loots_its_kill_before_it_fights_on()
    {
        // WP-M2-12: a round of several kills loots each one (FightStep plays BotBrainMode.All).
        var (brain, view) = ChainFight(Data.Value.Behaviour("m2-exit"));
        view.Ready = _ => true;
        view.LastKillAtMs = view.NowMs;
        view.Drops.Add(new SeenEntity(20, EntityKind.LootDrop, 1, view.Self + FixVec2.FromCm(30, 0), true, ActionState.Idle, 0, 0));

        var loot = brain.Think(view, BotBrainMode.All);
        Assert.Equal((BotActivity.Looting, 20u), (brain.Activity, loot.PickupDropId));

        var fight = brain.Think(view, BotBrainMode.All); // the drop was asked for: back to the demon
        Assert.Equal((BotActivity.Fighting, 7u, 0u), (brain.Activity, brain.TargetId, fight.PickupDropId));
    }

    [Fact]
    public void A_planned_back_off_leg_ends_inside_the_threats_leash_and_outside_the_other_spawns_aggro()
    {
        var data = Data.Value;
        var behaviour = data.Behaviour("m2-exit");
        var map = MapOf(data);
        var demon = MonsterDef.Find(data.Tables, "m1.demon")!;
        foreach (var marker in new[] { "m1.spawn.combat.a", "m1.spawn.combat.b", "field1.spawn.demon.a1" })
        {
            var home = data.Markers[marker];
            var self = home + FixVec2.FromCm(-150, 0);
            var threat = home + FixVec2.FromCm(-90, 0);

            var (to, kind) = BackOffLeg.Plan(data.Grid, map, behaviour, self, threat, demon.NetId);

            Assert.NotEqual(BackOffLegKind.Straight, kind);
            Assert.True(BackOffLeg.DistanceCm(to, home) <= demon.LeashRangeCm - behaviour.BackOffLeashMarginCm, marker);
            Assert.Equal(behaviour.BackOffDistanceCm, BackOffLeg.DistanceCm(self, to), 0);
            Assert.True(GridPath.Clear(data.Grid, self, to), marker);
            if (kind == BackOffLegKind.Planned)
            {
                Assert.All(map.Spawns.Where(s => s.Home != home),
                    s => Assert.True(BackOffLeg.DistanceCm(to, s.Home) >= s.AggroRangeCm + behaviour.AvoidAggroMarginCm, marker));
            }
        }
    }

    [Fact]
    public void A_planned_leg_turns_away_from_another_spawns_aggro_and_goes_straight_when_nothing_fits()
    {
        var data = Data.Value;
        var behaviour = data.Behaviour("m2-exit");
        var home = data.Markers["m1.spawn.combat.a"];
        var self = home;
        var threat = home + FixVec2.FromCm(100, 0); // straight away is west
        var west = BackOffLeg.Straight(self, threat, behaviour.BackOffDistanceCm);
        var alone = new BackOffMap([new SpawnCircle(1, home, 5_000, 800)]);
        var guarded = new BackOffMap([new SpawnCircle(1, home, 5_000, 800), new SpawnCircle(2, west, 5_000, 200)]);

        var (free, freeKind) = BackOffLeg.Plan(data.Grid, alone, behaviour, self, threat, 1);
        var (turned, turnedKind) = BackOffLeg.Plan(data.Grid, guarded, behaviour, self, threat, 1);

        Assert.Equal((west, BackOffLegKind.Planned), (free, freeKind)); // straight away fits
        Assert.Equal(BackOffLegKind.Planned, turnedKind);
        Assert.True(BackOffLeg.DistanceCm(turned, west) >= 200 + behaviour.AvoidAggroMarginCm);

        // A leash shorter than the margin leaves no direction; an unknown monster type has no home: straight away.
        var tight = new BackOffMap([new SpawnCircle(1, home, behaviour.BackOffLeashMarginCm, 800)]);
        Assert.Equal((west, BackOffLegKind.Straight), BackOffLeg.Plan(data.Grid, tight, behaviour, self, threat, 1));
        Assert.Equal((west, BackOffLegKind.Straight), BackOffLeg.Plan(data.Grid, alone, behaviour, self, threat, 99));
    }

    [Fact]
    public void A_planned_leg_never_enters_a_region_the_bots_level_gate_closes()
    {
        // R-2.3-06 (WP-M2-10): a bot below a region's level cannot step in, so a leg there would only stall the walk.
        var data = Data.Value;
        var behaviour = data.Behaviour("m2-exit");
        var home = data.Markers["field1.spawn.demon.a1"];
        byte own = data.Grid.RegionIndexAt(home);
        var minLevels = new int[256];
        for (int i = 1; i < minLevels.Length; i++)
            minLevels[i] = i == own ? 0 : 10;
        var gate = new MoverGate(RegionGates.FromMinLevels(minLevels), level: 1);
        var self = home + FixVec2.FromCm(-150, 0);

        for (int degrees = 0; degrees < 360; degrees += 45)
        {
            double a = degrees * Math.PI / 180;
            var threat = self + FixVec2.FromCm((int)(Math.Cos(a) * 100), (int)(Math.Sin(a) * 100));
            var (to, kind) = BackOffLeg.Plan(data.Grid, MapOf(data), behaviour, self, threat, 1, gate);
            if (kind != BackOffLegKind.Straight)
                Assert.True(GridPath.Clear(data.Grid, self, to, gate), $"{degrees} degrees: the leg crosses a closed gate");
        }
    }

    [Fact]
    public void The_brain_plans_its_legs_only_when_the_behaviour_says_so()
    {
        var data = Data.Value;
        var map = MapOf(data);

        var (planned, _) = HurtFight(data.Behaviour("m2-exit"), map);
        Assert.Equal(1, planned.BackOffLegsPlanned + planned.BackOffLegsLeashOnly);
        Assert.Equal(0, planned.BackOffLegsStraight);

        var (straight, _) = HurtFight(data.Behaviour("m2-loop"), map); // planBackOffLegs false: the map is ignored
        Assert.Equal(1, straight.BackOffs);
        Assert.Equal(0, straight.BackOffLegsPlanned + straight.BackOffLegsLeashOnly + straight.BackOffLegsStraight);
    }

    private static BackOffMap MapOf(BotData data) => BackOffMap.From(data.Rows<SpawnPoints>(), data.Markers, data.Rows<MonsterDef>());

    [Fact]
    public void Recovered_means_hp_at_the_re_engage_line_or_a_self_heal_usable()
    {
        // QaFightPlan.Recovered (QA wave 5 H2), with the line from BotBehaviour.reengageAbovePercent.
        var data = Data.Value;
        var behaviour = data.Behaviour("field-combat");
        int line = behaviour.ReengageAbovePercent * 150 / 100;
        var warrior = BotKit.For(data.Tables, "warrior");
        Assert.False(warrior.Recovered(new FightState(line - 1, 150, 1_000, 1_000, 0, _ => true), behaviour));
        Assert.True(warrior.Recovered(new FightState(line, 150, 1_000, 1_000, 0, _ => true), behaviour));

        var assist = BotKit.For(data.Tables, "assist");
        Assert.Contains(assist.Actions, a => a.HealsSelf);
        Assert.True(assist.Recovered(new FightState(10, 150, 1_000, 1_000, 0, _ => true), behaviour));
        Assert.False(assist.Recovered(new FightState(10, 150, 1_000, 1_000, 0, a => !a.HealsSelf), behaviour));
        Assert.False(assist.Recovered(new FightState(10, 150, 0, 0, 0, _ => true), behaviour)); // the heal cannot be paid
    }

    [Fact]
    public void After_a_back_off_a_still_hurt_bot_keeps_backing_off_and_fights_again_once_recovered()
    {
        var behaviour = Data.Value.Behaviour("field-combat");
        var (brain, view) = HurtFight(behaviour);
        long backOffAt = view.NowMs;

        // Still below the re-engage line after the shortest back-off: it stays out (it used to walk back here).
        view.NowMs = backOffAt + behaviour.BackOffMs;
        view.Own = view.Own with { Hp = Hp(behaviour.ReengageAbovePercent) - 1 };
        var command = brain.Think(view);
        Assert.Equal((BotActivity.BackingOff, 0u), (brain.Activity, brain.TargetId));
        Assert.Equal((InputActions.None, (byte)0), (command.Actions, command.SkillSlot));
        Assert.NotEqual(MoveKeys.None, command.Keys); // a new leg away from the demon next to it

        view.NowMs = backOffAt + behaviour.BackOffRecoverMs - 50;
        brain.Think(view);
        Assert.Equal(BotActivity.BackingOff, brain.Activity);

        // HP back at the line: it fights the demon again.
        view.NowMs += 50 - 1;
        view.Own = view.Own with { Hp = Hp(behaviour.ReengageAbovePercent) };
        brain.Think(view);
        Assert.Equal((BotActivity.Fighting, 7u), (brain.Activity, brain.TargetId));
        Assert.Equal(1, brain.BackOffs);
    }

    [Fact]
    public void The_shortest_back_off_holds_even_when_recovered_and_the_recovery_time_ends_it_when_not()
    {
        var behaviour = Data.Value.Behaviour("field-combat");
        var (brain, view) = HurtFight(behaviour);
        long backOffAt = view.NowMs;

        view.NowMs = backOffAt + behaviour.BackOffMs - 50;
        view.Own = view.Own with { Hp = 150 };
        brain.Think(view);
        Assert.Equal(BotActivity.BackingOff, brain.Activity); // backOffMs is the minimum

        (brain, view) = HurtFight(behaviour);
        backOffAt = view.NowMs;
        view.Own = view.Own with { Hp = Hp(behaviour.ReengageAbovePercent) - 1 }; // above the back-off line, never recovers
        view.NowMs = backOffAt + behaviour.BackOffRecoverMs;
        brain.Think(view);
        Assert.Equal((BotActivity.Fighting, 7u), (brain.Activity, brain.TargetId)); // fights at the HP it has
    }

    [Fact]
    public void A_hurt_bot_takes_no_new_target_below_the_re_engage_line_but_finishes_its_fight()
    {
        var behaviour = Data.Value.Behaviour("field-combat");
        int hurt = Hp(behaviour.ReengageAbovePercent) - 1;
        Assert.True(hurt > Hp(behaviour.BackOffBelowPercent), "between the back-off and re-engage lines");

        // A new demon (e.g. the next one after a kill) at that HP: no fight, it waits, for backOffRecoverMs at most.
        var (brain, view) = ChainFight(behaviour);
        view.Ready = _ => true;
        view.Own = view.Own with { Hp = hurt };
        long start = view.NowMs;
        var command = brain.Think(view);
        Assert.Equal((BotActivity.BackingOff, 0u, InputActions.None), (brain.Activity, brain.TargetId, command.Actions));
        Assert.Equal(0, brain.ActionsPressed);
        view.NowMs = start + behaviour.BackOffRecoverMs - 50;
        brain.Think(view);
        Assert.Equal((BotActivity.BackingOff, 0u), (brain.Activity, brain.TargetId));
        view.NowMs = start + behaviour.BackOffRecoverMs;
        brain.Think(view);
        Assert.Equal((BotActivity.Fighting, 7u), (brain.Activity, brain.TargetId));

        // The fight it is in goes on at that HP (QA's does too): only the back-off line sends it away.
        (brain, view) = ChainFight(behaviour);
        view.Ready = _ => true;
        brain.Think(view);
        view.NowMs += 50;
        view.Own = view.Own with { Hp = hurt };
        brain.Think(view);
        Assert.Equal((BotActivity.Fighting, 7u), (brain.Activity, brain.TargetId));
        Assert.Equal(0, brain.BackOffs);
    }

    private static int Hp(int percent) => percent * 150 / 100;

    /// <summary>A warrior (no self-heal) fighting a demon at full HP, then hurt below the back-off line: it backs off.</summary>
    private static (BotBrain Brain, BotPerception View) HurtFight(BotBehaviour behaviour, BackOffMap? map = null)
    {
        var (brain, view) = ChainFight(behaviour, map: map);
        view.Ready = _ => true;
        brain.Think(view);
        Assert.Equal((BotActivity.Fighting, 7u), (brain.Activity, brain.TargetId));

        view.NowMs += 50;
        view.Own = view.Own with { Hp = Hp(behaviour.BackOffBelowPercent) - 1 };
        brain.Think(view);
        Assert.Equal(1, brain.BackOffs);
        view.NowMs += 50;
        var command = brain.Think(view);
        Assert.Equal((BotActivity.BackingOff, InputActions.None), (brain.Activity, command.Actions));
        view.NowMs -= 50;
        return (brain, view);
    }

    /// <summary>A warrior next to a demon in the middle of its normal attack (the machine busy: nothing ready).</summary>
    private static (BotBrain Brain, BotPerception View) ChainFight(BotBehaviour behaviour, int distanceCm = 60, BackOffMap? map = null)
    {
        var data = Data.Value;
        var home = data.Markers["m1.spawn.combat.a"];
        var demon = MonsterDef.Find(data.Tables, "m1.demon")!;
        var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), behaviour, data.Grid, home, 200, new Dictionary<int, int>(),
            new Pcg32Rng(3), backOffMap: map);
        var view = new BotPerception
        {
            Self = home, NowMs = 1_000, HasResources = true, Ready = _ => false,
            Own = new OwnResources { Hp = 150, MaxHp = 150, Mp = 50, MaxMp = 50, Stamina = 100, MaxStamina = 100 },
        };
        view.Monsters.Add(new SeenEntity(7, EntityKind.Monster, (ushort)demon.NetId, home + FixVec2.FromCm(distanceCm, 0), true,
            ActionState.Idle, 0, 0));
        return (brain, view);
    }

    /// <summary>
    /// A warrior bot next to a demon presses the normal attack, then plays its chain from its predicted machine
    /// (<see cref="BotClient.World.BotWorldClient.Chain"/>, <see cref="BotBrain.Think"/>) with no snapshot in between. The
    /// server's machine gets the same frames <paramref name="offset"/> ticks off: behind, the first frames after the start
    /// are stepped in pairs (input catch-up); ahead, it steps without a frame first. True when the server starts hit 2
    /// and hit 3 no earlier than hit 2's link tick.
    /// </summary>
    private static bool ServerStartsTheNextHit(ActionCatalog catalog, ActionLoadout loadout, int offset)
    {
        var (brain, view) = ChainFight(Data.Value.Behaviour("field-combat"));
        var hit1 = catalog[loadout.AttackIndex];
        var predicted = new ActionMachineState(catalog, loadout);
        var frames = new List<InputActions> { InputActions.Attack };
        Step(predicted, InputActions.Attack, 1);
        while (frames.Count < 2 * hit1.TotalTicks)
        {
            view.NowMs += 50;
            view.Chain = BotClient.World.BotWorldClient.Chain(predicted);
            var actions = brain.Think(view).Actions;
            frames.Add(actions);
            Step(predicted, actions, frames.Count);
        }
        Assert.True(brain.ChainPresses >= 2, $"{brain.ChainPresses} hits pressed for");

        // Hit 2 must follow this hit 1 (after its end a later press only starts a new hit 1), and hit 3 may start no
        // earlier than hit 2's link tick (the second press must not skip ahead).
        var server = new ActionMachineState(catalog, loadout);
        Step(server, frames[0], 1);
        var steps = new List<(InputActions Held, int Sequence, bool HasInput)>();
        for (int i = 0; i < offset; i++)
            steps.Add((InputActions.None, 0, false));
        int next = 1;
        for (int i = 0; i < -offset; i++, next += 2)
            steps.Add((frames[next] | frames[next + 1], next + 1, true));
        for (; next < frames.Count; next++)
            steps.Add((frames[next], next, true));
        var hit2 = catalog[hit1.ChainNextIndex];
        bool inHit2 = false;
        foreach (var (held, sequence, hasInput) in steps)
        {
            int was = server.ActionIndex, elapsed = server.Elapsed;
            int running = Step(server, held, sequence, hasInput);
            if (running == was && server.Elapsed > elapsed)
                continue; // the same hit runs on
            if (!inHit2 && running == hit1.ChainNextIndex)
            {
                inHit2 = true;
                continue;
            }
            // Hit 1 ended or restarted without hit 2, or hit 2 ended: hit 3 counts only from hit 2's link tick on.
            return inHit2 && (running != hit2.ChainNextIndex || elapsed + 1 >= hit2.ChainLinkTick);
        }
        return inHit2;
    }

    /// <summary>One machine step on one input frame; returns the running action's catalog index after it.</summary>
    private static int Step(ActionMachineState machine, InputActions held, int sequence, bool hasInput = true)
    {
        ActionMachine.Step(machine, new ActionInput(hasInput, held, 0, 0, (ushort)sequence), UnlimitedActionResources.Instance,
            new FiredEffect[ActionMachine.MaxFiredPerStep]);
        return machine.ActionIndex;
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
