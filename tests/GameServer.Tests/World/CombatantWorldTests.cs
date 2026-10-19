using GameServer.Combat;
using GameServer.Entities;
using GameServer.Persistence;
using GameServer.Sessions;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Stats;

namespace GameServer.Tests.World;

/// <summary>
/// Combatants in the world tick by tick (backlog D5 wiring, F): players built from their class, monsters spawned at their
/// spawn points (phase 7) and removed and respawned after the spec 12.7 delay (phase 8), the position history of phase 2,
/// and the resources snapshots carry (spec 9.4).
/// </summary>
public class CombatantWorldTests
{
    private static readonly CharacterState Saved = new(5, 0, SessionManager.MainWorldKey, 0, 0, 0, 0, Hp: 40, Mp: 7, Stamina: 30);

    private static WorldHarness Harness(int monsterXCm = 1_000, int monsterYCm = 0, string monsterId = TestCombat.MonsterId) =>
        new(map: TestCombat.OpenMap(TestCombat.MonsterMarker(monsterXCm, monsterYCm, facing: 49_152)),
            combat: TestCombat.Rules(TestCombat.Point(monsterId: monsterId)));

    private static Monster OnlyMonster(WorldHarness harness) => Assert.Single(harness.World.Entities.OfType<Monster>());

    [Fact]
    public void A_monster_spawns_at_its_marker_on_the_first_tick_with_full_meters()
    {
        var harness = Harness(monsterXCm: 1_000, monsterYCm: -500);
        harness.Ticks(1);

        var monster = OnlyMonster(harness);
        var rules = harness.World.Combat!;
        Assert.True(rules.TryGetMonster(TestCombat.MonsterId, out var def));
        Assert.Equal((1_000, -500, (ushort)49_152), (monster.XCm, monster.YCm, monster.Movement.Facing));
        Assert.Equal(EntityKind.Monster, monster.Kind);
        Assert.Equal((ushort)def.NetId, monster.DefId);
        Assert.Equal(TestCombat.MonsterSpawnId, monster.SpawnPointId);
        var combatant = monster.Combatant!;
        Assert.Equal(Faction.Monster, combatant.Faction);
        Assert.Equal(def.MaxHp, combatant.Hp(harness.World.NowMs));
        Assert.True(combatant.History.TryGetAt(0, out var spawnedAt));
        Assert.Equal(monster.Movement.Position, spawnedAt);
    }

    [Fact]
    public void A_world_without_combat_rules_has_no_monsters_and_players_without_combatants()
    {
        var harness = new WorldHarness();
        var player = harness.Join(1);
        harness.Ticks(3);

        Assert.Null(player.Combatant);
        Assert.Equal((ushort)0, player.DefId);
        Assert.Empty(harness.World.Entities.OfType<Monster>());
        Assert.False(harness.LastSnapshot(1).Header.HasOwnResources);
        Assert.Equal(Snapshot.TopHpBucket, harness.LastSnapshot(1).Self.HpBucket);
    }

    [Fact]
    public void A_nearby_player_gets_the_monster_spawn_with_its_type_and_HP_bucket()
    {
        var harness = Harness(monsterXCm: 1_000);
        var player = harness.Join(1); // the join tick also spawns the monster (phase 7 comes before the snapshot)
        var monster = OnlyMonster(harness);

        var first = harness.LastSnapshot(1);
        var spawn = Assert.Single(first.Spawns, s => s.EntityId == monster.Id);
        Assert.Equal(EntityKind.Monster, spawn.Kind);
        Assert.Equal(monster.DefId, spawn.DefId);
        Assert.Equal(Snapshot.TopHpBucket, first.Entities.Single(e => e.EntityId == monster.Id).HpBucket);

        // Half the HP gone: the bucket follows Quantize.Bucket with the protocol's top bucket.
        var meters = monster.Combatant!.Meters;
        long now = harness.World.NowMs;
        int max = meters.Max(ResourceKind.Hp, now);
        meters.Drain(ResourceKind.Hp, max / 2, now);
        harness.Ticks(1);
        var after = harness.LastSnapshot(1).Entities.Single(e => e.EntityId == monster.Id);
        Assert.Equal(Quantize.Bucket(max - max / 2, max, Snapshot.TopHpBucket), after.HpBucket);
        Assert.InRange(after.HpBucket, 1, Snapshot.TopHpBucket - 1);
        Assert.NotNull(player.Combatant);
    }

    [Fact]
    public void A_player_far_away_does_not_get_the_monster()
    {
        var harness = Harness(monsterXCm: 5_000); // interest is a 30 m square around the observer
        harness.Join(1);
        harness.Ticks(1);

        var snapshot = harness.LastSnapshot(1);
        Assert.Single(snapshot.Entities);
        Assert.Single(harness.World.Entities.OfType<Monster>());
    }

    [Fact]
    public void The_observer_gets_its_exact_resources_and_others_only_a_bucket()
    {
        var harness = Harness();
        var player = harness.Join(1); // a new character (no saved world): full meters
        var rules = harness.World.Combat!;
        Assert.True(rules.TryGetClass("warrior", out var warrior));

        var header = harness.LastSnapshot(1).Header;
        Assert.True(header.HasOwnResources);
        Assert.Equal(warrior.MaxHp, header.Own.Hp);
        Assert.Equal(warrior.MaxHp, header.Own.MaxHp);
        Assert.Equal(warrior.MaxMp, header.Own.Mp);
        Assert.Equal(warrior.MaxMp, header.Own.MaxMp);
        Assert.Equal(header.Own.MaxStamina, header.Own.Stamina);
        Assert.Equal((ushort)warrior.NetId, player.DefId);

        // A spend shows up exactly in the next snapshot (stamina waits 1 s before it regenerates, spec 12.7).
        player.Combatant!.Meters.TrySpend(ResourceKind.Stamina, 25, harness.World.NowMs);
        harness.Ticks(1);
        var own = harness.LastSnapshot(1).Header.Own;
        Assert.Equal(own.MaxStamina - 25, own.Stamina);
    }

    [Fact]
    public void Another_player_sees_the_players_class_in_its_spawn()
    {
        var harness = Harness();
        harness.Characters.Add(2, 0, Saved with { PosXCm = 300 }, classId: "mage");
        harness.Join(1);
        var mage = harness.Join(2);
        harness.Ticks(1);

        Assert.True(harness.World.Combat!.TryGetClass("mage", out var mageDef));
        var spawn = harness.Transport.SnapshotsFor(1).SelectMany(s => s.Spawns).Single(s => s.EntityId == mage.Id);
        Assert.Equal((EntityKind.Player, (ushort)mageDef.NetId), (spawn.Kind, spawn.DefId));
    }

    [Fact]
    public void A_dead_monster_is_removed_in_phase_8_and_respawns_30_seconds_later()
    {
        var harness = Harness(monsterXCm: 1_000);
        harness.Join(1);
        var first = OnlyMonster(harness);

        first.Combatant!.Die(harness.World.ServerTick);
        uint removedTick = harness.World.ServerTick;
        harness.Ticks(1); // phase 8 of this tick removes it

        Assert.Empty(harness.World.Entities.OfType<Monster>());
        Assert.Contains(first.Id, harness.LastSnapshot(1).Despawns);

        // Spec 12.7: 30 s at 20 Hz is 600 ticks. The point stays empty until then.
        int respawnTicks = harness.World.Combat!.MonsterRespawnTicks;
        Assert.Equal(600, respawnTicks);
        while (harness.World.ServerTick < removedTick + (uint)respawnTicks)
        {
            harness.Ticks(1);
            Assert.Empty(harness.World.Entities.OfType<Monster>());
        }
        harness.Ticks(1); // tick removedTick + 600: phase 7 spawns it

        var second = OnlyMonster(harness);
        Assert.True(second.Id > first.Id);
        Assert.Equal(first.Movement, second.Movement);
        Assert.True(second.Combatant!.IsAlive);
        Assert.Equal(second.Combatant.MaxHp(harness.World.NowMs), second.Combatant.Hp(harness.World.NowMs));
        Assert.Contains(harness.LastSnapshot(1).Spawns, s => s.EntityId == second.Id && s.Kind == EntityKind.Monster);
    }

    [Fact]
    public void A_boss_waits_the_boss_respawn()
    {
        var harness = Harness(monsterId: TestCombat.BossId);
        harness.Ticks(1);
        OnlyMonster(harness).Combatant!.Die(harness.World.ServerTick);
        uint removedTick = harness.World.ServerTick;
        harness.Ticks(1);

        harness.Ticks(harness.World.Combat!.MonsterRespawnTicks + 1);
        Assert.Empty(harness.World.Entities.OfType<Monster>());
        harness.Ticks((int)(removedTick + (uint)harness.World.Combat.BossRespawnTicks - harness.World.ServerTick) + 1);
        Assert.Single(harness.World.Entities.OfType<Monster>());
    }

    [Fact]
    public void Phase_2_records_every_combatants_position_in_its_history()
    {
        var harness = Harness();
        var player = harness.Join(1);
        uint start = harness.World.ServerTick;
        harness.SendInputs(1, TestWorlds.Frame(1, Shared.Protocol.MoveKeys.D));
        harness.Ticks(1);

        Assert.True(player.Combatant!.History.TryGetAt(start, out var moved));
        Assert.Equal(player.Movement.Position, moved);
        Assert.True(player.Combatant.History.TryGetAt(start - 1, out var before));
        Assert.NotEqual(moved, before);
        Assert.True(OnlyMonster(harness).Combatant!.History.TryGetAt(start, out _));
        Assert.Equal(harness.World.Combat!.HistoryCapacity, player.Combatant.History.Capacity);
    }

    [Fact]
    public void A_returning_character_gets_its_saved_meters_and_a_save_writes_the_current_ones()
    {
        var harness = Harness();
        var record = harness.Characters.Add(5, 0, Saved, classId: "mage");
        var player = harness.Join(peerId: 1, accountId: 5);

        var own = harness.LastSnapshot(1).Header.Own;
        Assert.Equal((40, 7, 30), (own.Hp, own.Mp, own.Stamina));

        player.Combatant!.Meters.Drain(ResourceKind.Hp, 15, harness.World.NowMs);
        var state = harness.Sessions.Capture(record.CharacterId);
        Assert.Equal(25, state.Hp);
        Assert.Equal(7, state.Mp);
    }

    [Fact]
    public void A_character_of_an_unknown_class_is_refused()
    {
        var harness = Harness();
        harness.Characters.Add(5, 0, Saved, classId: "removedClass");
        harness.Connect(peerId: 1, accountId: 5);
        harness.Ticks(2);

        Assert.False(harness.World.TryGetPlayer(1, out _));
        Assert.Contains((1, ConnectRejectReason.CharacterUnavailable), harness.Transport.Kicks);
        Assert.Empty(harness.World.Entities.OfType<Player>());
    }

    private static WorldHarness ActionHarness() =>
        new(map: TestMaps.Open(), actions: TestActions.Catalog(), combat: TestCombat.Rules());

    [Fact]
    public void Phase_3_pays_an_actions_stamina_from_the_players_meter()
    {
        // R-3.1-02, R-3.1-04: attacks cost stamina (the test catalog: 10), spent from the combatant's meter when it starts.
        var harness = ActionHarness();
        var player = harness.Join(1);
        harness.SendInputs(1, TestWorlds.Frame(1, actions: InputActions.Attack));
        harness.Ticks(1);

        var own = harness.LastSnapshot(1).Header.Own;
        Assert.Equal(own.MaxStamina - 10, own.Stamina);
        Assert.Equal((byte)Shared.Simulation.Actions.ActionState.Startup, harness.LastSnapshot(1).Self.State);
        Assert.Empty(harness.Transport.ActionResultsFor(1));
        Assert.NotNull(player.Combatant);
    }

    [Fact]
    public void An_action_is_refused_with_NoStamina_when_the_meter_is_short_and_nothing_is_spent()
    {
        var harness = ActionHarness();
        var player = harness.Join(1);
        var meters = player.Combatant!.Meters;
        int max = meters.Max(ResourceKind.Stamina, harness.World.NowMs);
        Assert.Equal(SpendResult.Ok, meters.TrySpend(ResourceKind.Stamina, max - 5, harness.World.NowMs)); // 5 left, attack costs 10

        harness.SendInputs(1, TestWorlds.Frame(1, actions: InputActions.Attack));
        harness.Ticks(1);

        Assert.Equal([((ushort)1, (ushort)TestActions.AttackNetId, (byte)0, ActionReason.NoStamina)],
            harness.Transport.ActionResultsFor(1));
        Assert.Equal((byte)Shared.Simulation.Actions.ActionState.Idle, harness.LastSnapshot(1).Self.State);
        Assert.Equal(5, harness.LastSnapshot(1).Header.Own.Stamina);
        // A refusal changes nothing else: the dodge pool is untouched.
        Assert.Equal(3, player.Actions.Charges(Shared.Data.Tables.ChargePool.Dodge));
    }

    [Fact]
    public void Stamina_regenerates_after_the_delay_so_the_action_starts_again()
    {
        // Spec 12.7: 20 per second after a 1 s delay. 5 left needs 5 more: 1 s delay + 250 ms = 25 ticks.
        var harness = ActionHarness();
        var player = harness.Join(1);
        var meters = player.Combatant!.Meters;
        meters.TrySpend(ResourceKind.Stamina, meters.Max(ResourceKind.Stamina, harness.World.NowMs) - 5, harness.World.NowMs);
        harness.Ticks(25);

        harness.SendInputs(1, TestWorlds.Frame(1, actions: InputActions.Attack));
        harness.Ticks(1);

        Assert.Empty(harness.Transport.ActionResultsFor(1));
        Assert.Equal((byte)Shared.Simulation.Actions.ActionState.Startup, harness.LastSnapshot(1).Self.State);
    }

    [Fact]
    public void A_spawn_point_on_a_missing_or_wrong_marker_refuses_the_world()
    {
        var rules = TestCombat.Rules(TestCombat.Point(markerId: TestMaps.SpawnId)); // a playerSpawn marker
        Assert.Throws<ArgumentException>(() => new WorldHarness(map: TestMaps.Open(), combat: rules));
        var missing = TestCombat.Rules(TestCombat.Point(markerId: "nowhere"));
        Assert.Throws<ArgumentException>(() => new WorldHarness(map: TestMaps.Open(), combat: missing));
    }
}
