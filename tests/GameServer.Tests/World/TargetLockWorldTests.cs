using GameServer.Entities;
using GameServer.Tests.Support;
using GameServer.World;
using GameServer.World.Map;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using static GameServer.Tests.Support.TestWorlds;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// R-2.2-06, M3 decision 16: the lock reaches the server as the frames' locked flag plus a reliable
/// <see cref="TargetLockChanged"/>. The walk never depends on it; the server aims a locked press that is neither a move nor a
/// ground skill at the locked enemy itself (its position history on the press frame's view clock), and a target it does not
/// know, dead or out of the player's view leaves the press at the mouse.
/// </summary>
public class TargetLockWorldTests
{
    private const ushort East = 16_384;
    private const ushort Mouse = 40_000; // about 220 degrees: never toward the demon in the east

    /// <summary>Monsters never see players: they stay where they spawned. <see cref="HideFromPlayers"/> hides the demon too.</summary>
    private sealed class Visibility : IVisibilityQuery
    {
        public bool HideFromPlayers { get; set; }

        public bool CanSee(Entity observer, Entity target) =>
            !(observer is Monster && target is Player) && !(HideFromPlayers && observer is Player && target is Monster);
    }

    /// <summary>
    /// An attack and a parry (lock aimed), a dodge along the keys (a move skill, backward with none) and a ground skill on slot
    /// 1 (a GroundArea effect: the mouse, lock or not). No stamina costs, so prediction (which has no meters) matches.
    /// </summary>
    private static ActionCatalog Catalog() => new(20,
        [
            Act("attack", 1, ActionKind.Attack, 100, 100, 100), Act("dodge", 2, ActionKind.Dodge, 50, 250, 200, ActionAim.Body),
            Act("parry", 3, ActionKind.Parry, 50, 200, 250), Act("skill.ground", 4, ActionKind.Skill, 100, 100, 100, ActionAim.Ground),
        ],
        [
            new ActionEffectDef
            {
                Id = "dodge.dash", ActionId = "dodge", Kind = EffectKind.Dash, Phase = ActionPhase.Active, DistanceCm = 400,
                DurationMs = 250, DashDirection = DashDirection.MoveInput, DirectionOffsetDeg = 180,
            },
            new ActionEffectDef
            {
                Id = "skill.ground.area", ActionId = "skill.ground", Kind = EffectKind.GroundArea, Phase = ActionPhase.Recovery,
                DurationMs = 1000, TickIntervalMs = 250,
            },
        ],
        new ActionBindings { NormalAttack = "attack", Dodge = "dodge", Parry = "parry", SkillSlots = ["skill.ground"] },
        new ChargePoolConfig(3, 4000), new ChargePoolConfig(3, 5000));

    private static ActionDef Act(string id, int netId, ActionKind kind, int startup, int active, int recovery,
        ActionAim aim = ActionAim.Target) => new()
    {
        Id = id, NetId = netId, Kind = kind, StartupMs = startup, ActiveMs = active, RecoveryMs = recovery,
        ChargePool = ActionDef.PoolOf(kind), Aim = aim,
    };

    private static (WorldHarness Harness, Player Player, Monster Demon, Visibility Visibility) Setup(int demonXCm = 1_000)
    {
        // The player at the origin, a demon east of it (10 m: inside the 30 m interest of the test settings).
        var tables = TestCombat.Tables();
        var markers = new List<WorldMarker> { TestMaps.SpawnMarker(0, 0), TestCombat.MonsterMarker(demonXCm, 0, id: "demon.a") };
        var visibility = new Visibility();
        var harness = new WorldHarness(map: TestMaps.Map(TestMaps.Open().Grid, markers), visibility: visibility, actions: Catalog(),
            values: TestSessions.Values(lingerOutOfCombatMs: 0),
            combat: TestCombat.Rules(tables, [TestCombat.Point("demon.a", TestCombat.MonsterId)]));
        var player = harness.Join(1);
        harness.Ticks(1);
        return (harness, player, harness.World.Entities.OfType<Monster>().Single(), visibility);
    }

    /// <summary>The lock message: kept on the session's input queue, read before that tick's frames.</summary>
    private static void Lock(WorldHarness harness, uint target) =>
        harness.Router.OnDataReceived(1, Channel.ReliableOrdered,
            ReliableMessages.Write(ReliableMessageId.TargetLockChanged, new TargetLockChanged { TargetEntityId = target }));

    private static ushort Wire(ushort angle) => Quantize.RoundTripAngle(angle, InputPacket.AimBits);

    private static void Press(WorldHarness harness, ushort sequence, InputActions actions, bool locked, MoveKeys keys = MoveKeys.None,
        byte slot = 0)
    {
        harness.SendInputs(1, Frame(sequence, keys, Mouse, actions, slot, locked));
        harness.Ticks(1);
    }

    [Fact]
    public void A_locked_attack_aims_at_the_target_and_turns_the_body_to_it()
    {
        var (harness, player, demon, _) = Setup();
        Lock(harness, demon.Id);

        Press(harness, 1, InputActions.Attack, locked: true);

        Assert.Equal(East, player.Actions.Facing);
        Assert.Equal(East, player.Movement.Facing);
        Assert.Equal(1, harness.World.LockAimsResolved);
        Assert.Equal(0, harness.World.LockAimFallbacks);
    }

    [Fact]
    public void A_locked_parry_faces_the_target_and_an_unlocked_one_the_mouse()
    {
        var (harness, player, demon, _) = Setup();
        Lock(harness, demon.Id);
        Press(harness, 1, InputActions.Parry, locked: true);
        Assert.Equal(East, player.Actions.Facing);

        var (free, freePlayer, freeDemon, _) = Setup();
        Lock(free, freeDemon.Id); // the message alone changes nothing: the frame's flag decides
        Press(free, 1, InputActions.Parry, locked: false);
        Assert.Equal(Wire(Mouse), freePlayer.Actions.Facing);
        Assert.Equal(0, free.World.LockAimsResolved + free.World.LockAimFallbacks);
    }

    [Fact]
    public void A_ground_skill_aims_at_the_mouse_and_a_dodge_follows_the_keys_lock_or_not()
    {
        var (harness, player, demon, _) = Setup();
        Lock(harness, demon.Id);

        Press(harness, 1, InputActions.None, locked: true, slot: 1); // the ground skill: the body turns to the mouse area
        Assert.Equal(Wire(Mouse), player.Actions.Facing);

        var (dodging, dodger, target, _) = Setup();
        Lock(dodging, target.Id);
        var start = dodger.Movement.Position;
        Press(dodging, 1, InputActions.Dodge, locked: true, keys: MoveKeys.W); // north, whatever the lock
        Assert.Equal(0, dodger.Actions.Facing);
        for (ushort i = 2; i < 12; i++)
            Press(dodging, i, InputActions.None, locked: true);
        Assert.True(dodger.Movement.Position.Y - start.Y > FixedPoint.FromCm(400));
        Assert.Equal(start.X, dodger.Movement.Position.X);
    }

    [Fact]
    public void A_locked_walk_is_the_unlocked_walk_and_the_body_faces_it()
    {
        // M3 decision 16 rule 1: the mouse 60 degrees off the demon, W held: north, the body facing north, lock or not.
        var (locked, lockedPlayer, demon, _) = Setup();
        var (free, freePlayer, _, _) = Setup();
        Lock(locked, demon.Id);
        for (ushort i = 1; i <= 10; i++)
        {
            locked.SendInputs(1, Frame(i, MoveKeys.W, 27_307, locked: true));
            free.SendInputs(1, Frame(i, MoveKeys.W, 27_307));
            locked.Ticks(1);
            free.Ticks(1);
        }

        Assert.Equal(freePlayer.Movement, lockedPlayer.Movement);
        Assert.Equal(0, lockedPlayer.Movement.Facing);
        Assert.Equal(10, locked.World.LockAimsResolved); // every locked frame is aimed, in case a press starts on it
        Assert.Equal(0, locked.World.LockAimFallbacks);
    }

    [Fact]
    public void The_lock_aim_reads_the_targets_history_on_the_view_clock_between_two_ticks()
    {
        // R-3.1-03, R-3.1-04: the player drew the demon 2 ticks back plus a fraction; its history says (-10 m, +10 m) then
        // (+10 m, +10 m) for those two ticks, while the demon really stands due east now. Half way it was due north.
        Assert.Equal(0, AimAtViewClock(subTick: 32));
        Assert.Equal(57_344, AimAtViewClock(subTick: 0)); // no fraction: the older sample, north-west
    }

    private static int AimAtViewClock(byte subTick)
    {
        var (harness, player, demon, _) = Setup();
        harness.Ticks(5); // a view tick of 0 means "no view yet"
        Lock(harness, demon.Id);
        uint tick = harness.World.ServerTick;
        demon.Combatant!.History.Clear();
        demon.Combatant.History.Record(tick - 2, FixVec2.FromCm(-1_000, 1_000));
        demon.Combatant.History.Record(tick - 1, FixVec2.FromCm(1_000, 1_000));

        harness.SendInputs(1, Frame(1, MoveKeys.None, Mouse, InputActions.Attack, locked: true) with { ViewTick = tick - 2, SubTick = subTick });
        harness.Ticks(1);

        Assert.Equal(1, harness.World.LockAimsResolved);
        return player.Actions.Facing;
    }

    [Fact]
    public void Without_a_known_target_a_locked_press_aims_at_the_mouse_and_is_counted()
    {
        var (harness, player, demon, _) = Setup();

        Press(harness, 1, InputActions.Attack, locked: true); // no TargetLockChanged yet
        Assert.Equal(Wire(Mouse), player.Actions.Facing);
        Assert.Equal(1, harness.World.LockAimNoTarget);

        Lock(harness, demon.Id);
        Lock(harness, 0); // cleared again
        for (ushort i = 2; i < 20; i++)
            Press(harness, i, InputActions.None, locked: true);
        Press(harness, 20, InputActions.Attack, locked: true);
        Assert.Equal(Wire(Mouse), player.Actions.Facing);
        Assert.Equal(0, harness.World.LockAimsResolved);
    }

    [Fact]
    public void A_dead_target_leaves_the_press_at_the_mouse()
    {
        var (harness, player, demon, _) = Setup();
        Lock(harness, demon.Id);
        demon.Combatant!.Die(harness.World.ServerTick);

        Press(harness, 1, InputActions.Attack, locked: true);

        Assert.Equal(Wire(Mouse), player.Actions.Facing);
        Assert.Equal(1, harness.World.LockAimFallbacks); // dead, or already removed as dead
        Assert.Equal(0, harness.World.LockAimsResolved);
    }

    [Fact]
    public void A_target_out_of_view_or_hidden_leaves_the_press_at_the_mouse()
    {
        // Beyond the interest area (30 m half-size in the test settings): never in this player's view.
        var (far, farPlayer, farDemon, _) = Setup(demonXCm: 4_000);
        Lock(far, farDemon.Id);
        Press(far, 1, InputActions.Attack, locked: true);
        Assert.Equal(Wire(Mouse), farPlayer.Actions.Facing);
        Assert.Equal(1, far.World.LockAimNotInView);

        // Hidden from the player (rule 9, IVisibilityQuery): the same.
        var (hidden, hiddenPlayer, hiddenDemon, visibility) = Setup();
        visibility.HideFromPlayers = true;
        hidden.Ticks(1); // the next interest update drops it
        Lock(hidden, hiddenDemon.Id);
        Press(hidden, 1, InputActions.Attack, locked: true);
        Assert.Equal(Wire(Mouse), hiddenPlayer.Actions.Facing);
        Assert.Equal(1, hidden.World.LockAimNotInView);
    }

    [Fact]
    public void A_target_in_view_beyond_the_break_range_leaves_the_press_at_the_mouse()
    {
        // #154 review: a modified client keeping its lock past breakRangeCm (20 m) gets no aim. The demon at 25 m is inside the
        // 30 m interest, so only the range refuses; at 15 m the same press aims at it.
        var (far, farPlayer, farDemon, _) = Setup(demonXCm: 2_500);
        Lock(far, farDemon.Id);
        Press(far, 1, InputActions.Attack, locked: true);
        Assert.Equal(Wire(Mouse), farPlayer.Actions.Facing);
        Assert.Equal(1, far.World.LockAimOutOfRange);
        Assert.Equal(1, far.World.LockAimFallbacks);
        Assert.Equal(0, far.World.LockAimsResolved);

        var (near, nearPlayer, nearDemon, _) = Setup(demonXCm: 1_500);
        Lock(near, nearDemon.Id);
        Press(near, 1, InputActions.Attack, locked: true);
        Assert.Equal(East, nearPlayer.Actions.Facing);
        Assert.Equal(0, near.World.LockAimOutOfRange);
    }

    [Fact]
    public void The_committed_tables_aim_ice_pike_at_the_mouse_flamethrower_at_the_target_and_move_skills_with_the_body()
    {
        // M3 decision 16 (the user's answer): only an area placed at the pointer keeps the mouse under a lock.
        var catalog = ActionCatalog.From(TestCombat.Tables());
        Assert.Equal(ActionAim.Ground, catalog.Find("skill.mage.icePike")!.Aim);
        Assert.Equal(ActionAim.Target, catalog.Find("skill.mage.flamethrower")!.Aim);
        Assert.Equal(ActionAim.Body, catalog.Find("dodge")!.Aim);
        Assert.Equal(ActionAim.Body, catalog.Find("skill.archer.dash")!.Aim);
        Assert.Equal(ActionAim.Body, catalog.Find("skill.archer.backStep")!.Aim);
        Assert.Equal(ActionAim.Target, catalog.Find("skill.warrior.jumpStrike")!.Aim);
        Assert.Equal(ActionAim.Target, catalog.Find("parry")!.Aim);
    }

    [Fact]
    public void An_attack_turns_the_body_to_the_mouse_and_it_stays_until_the_walk_turns_it()
    {
        // M3 decision 15 rule 2, unchanged by decision 16 without a lock.
        var (harness, player, _, _) = Setup();
        harness.SendInputs(1, Frame(1, MoveKeys.W)); // walk north
        harness.Ticks(1);
        Assert.Equal(0, player.Movement.Facing);

        harness.SendInputs(1, Frame(2, MoveKeys.None, 23_040, InputActions.Attack)); // the mouse at about 126 degrees
        harness.Ticks(1);
        Assert.Equal(23_040, player.Actions.Facing);
        Assert.Equal(23_040, player.Movement.Facing);

        for (ushort i = 3; i < 40; i++) // the attack ends; standing still keeps the facing, the mouse elsewhere
        {
            harness.SendInputs(1, Frame(i, MoveKeys.None, 50_000));
            harness.Ticks(1);
        }
        Assert.False(player.Actions.IsBusy);
        Assert.Equal(23_040, player.Movement.Facing);

        harness.SendInputs(1, Frame(40, MoveKeys.D, 50_000)); // walking turns it
        harness.Ticks(1);
        Assert.Equal(East, player.Movement.Facing);
    }

    [Fact]
    public void Prediction_matches_the_server_across_lock_on_and_off_with_attacks_parries_a_ground_skill_and_dodges()
    {
        // M3 decision 16: free walks, a free attack at the mouse; locked walks (as free), a locked attack and parry at the demon,
        // a locked ground skill at the mouse, a locked dodge along the keys and one with none (backward); then free again.
        var tables = TestCombat.Tables();
        var markers = new List<WorldMarker> { TestMaps.SpawnMarker(0, 0), TestCombat.MonsterMarker(1_000, 0, id: "demon.a") };
        var map = TestMaps.Map(TestMaps.Open().Grid, markers);
        var catalog = Catalog();
        var harness = new WorldHarness(map: map, visibility: new Visibility(), actions: catalog,
            values: TestSessions.Values(lingerOutOfCombatMs: 0),
            combat: TestCombat.Rules(tables, [TestCombat.Point("demon.a", TestCombat.MonsterId)]));
        var client = new WorldActionsTests.Client(harness, map.Grid, catalog);
        harness.Ticks(1);
        var demon = harness.World.Entities.OfType<Monster>().Single();
        Lock(harness, demon.Id);
        var drawn = demon.Movement.Position; // it stands: drawn where the server has it

        var script = new List<(MoveKeys Keys, InputActions Actions, byte Slot, bool Locked)>();
        void Hold(int ticks, MoveKeys keys, bool locked)
        {
            for (int i = 0; i < ticks; i++)
                script.Add((keys, InputActions.None, 0, locked));
        }
        Hold(4, MoveKeys.W | MoveKeys.D, false);
        script.Add((MoveKeys.None, InputActions.Attack, 0, false)); // the body turns to the mouse
        Hold(16, MoveKeys.None, false);
        Hold(6, MoveKeys.D, true);                                   // a locked walk is a free walk
        script.Add((MoveKeys.None, InputActions.Attack, 0, true));   // at the demon
        Hold(16, MoveKeys.None, true);
        script.Add((MoveKeys.None, InputActions.Parry, 0, true));    // at the demon
        Hold(16, MoveKeys.None, true);
        script.Add((MoveKeys.None, InputActions.None, 1, true));     // the ground skill: at the mouse
        Hold(16, MoveKeys.None, true);
        script.Add((MoveKeys.S, InputActions.Dodge, 0, true));       // south along the keys
        Hold(12, MoveKeys.None, true);
        script.Add((MoveKeys.None, InputActions.Dodge, 0, true));    // none held: backward from the facing
        Hold(12, MoveKeys.W, true);
        Hold(6, MoveKeys.A, false);                                  // free again: west

        foreach (var step in script)
            client.Tick(step.Keys, Mouse, step.Actions, step.Slot, locked: step.Locked, lockTarget: step.Locked ? drawn : null);

        Assert.Equal(0, client.Predictor.Corrections);
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
        Assert.Equal(client.Server.Movement.Facing, client.Predictor.State.Facing);
        Assert.Equal(49_152, client.Server.Movement.Facing); // the free walk west
        Assert.True(harness.World.LockAimsResolved > 0);   // every locked frame
        Assert.Equal(0, harness.World.LockAimFallbacks);
        Assert.Equal(0, harness.World.SpeedViolations);
    }
}
