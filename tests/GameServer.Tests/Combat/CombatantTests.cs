using GameServer.Combat;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Combat;

/// <summary>Combatant creation from ClassDef and MonsterDef (backlog D5 wiring), saved meters, and the position history.</summary>
public class CombatantTests
{
    private readonly CombatRules _rules = TestCombat.Rules();

    private ClassDef Class(string id)
    {
        Assert.True(_rules.TryGetClass(id, out var classDef));
        return classDef;
    }

    [Theory]
    [InlineData("warrior")]
    [InlineData("archer")]
    [InlineData("mage")]
    [InlineData("assist")]
    public void A_player_starts_full_with_its_class_numbers_and_the_shared_stamina(string classId)
    {
        var classDef = Class(classId);
        var starting = TestCombat.Tables().Single<StartingValuesM1>();
        var player = _rules.ForPlayer(classDef, nowMs: 5_000);
        var ids = _rules.Config.Ids;

        Assert.Equal(Faction.Player, player.Faction);
        Assert.Equal(classId, player.TemplateId);
        Assert.Equal(Rank.F, player.Rank);
        Assert.Equal(classDef.ArmorClass, player.ArmorClass);
        Assert.True(player.IsAlive);
        Assert.Equal(classDef.Atk, player.Stats.Get(ids.Atk));
        Assert.Equal(classDef.Matk, player.Stats.Get(ids.Matk));
        Assert.Equal(classDef.Def, player.Stats.Get(ids.Def));
        Assert.Equal(classDef.Mdef, player.Stats.Get(ids.Mdef));
        Assert.Equal(classDef.MaxHp, player.Meters.Current(ResourceKind.Hp, 5_000));
        Assert.Equal(classDef.MaxHp, player.Meters.Max(ResourceKind.Hp, 5_000));
        Assert.Equal(classDef.MaxMp, player.Meters.Current(ResourceKind.Mp, 5_000));
        // R-3.1-02, spec 12.7: one stamina pool and rate for every class.
        Assert.Equal(starting.StaminaPool, player.Meters.Max(ResourceKind.Stamina, 5_000));
        Assert.Equal(starting.StaminaRegenPerSec, player.Stats.Get(ids.StaminaRegen));
        // R-3.3-01: the bar starts full; R-3.3-07: the class's flinch numbers.
        Assert.Equal(PoiseState.Full(classDef.PoiseMax), player.Poise);
        Assert.Equal(classDef.FlinchResist, player.FlinchResist);
        Assert.Equal(classDef.FlinchDurationPercent, player.FlinchDurationPercent);
        Assert.Equal(classDef.FlinchImmunityMs, player.FlinchImmunityMs);
        Assert.Equal(0u, player.FlinchImmuneUntilTick);
    }

    [Fact]
    public void Each_root_class_has_its_armor_class()
    {
        // R-3.3-05, R-3.5-08: armor class comes from ClassDef (user decision 2026-10-22).
        Assert.Equal(ArmorClass.Heavy, _rules.ForPlayer(Class("warrior"), 0).ArmorClass);
        Assert.Equal(ArmorClass.Light, _rules.ForPlayer(Class("mage"), 0).ArmorClass);
        Assert.Equal(ArmorClass.Light, _rules.ForPlayer(Class("archer"), 0).ArmorClass);
        Assert.Equal(ArmorClass.Medium, _rules.ForPlayer(Class("assist"), 0).ArmorClass);
    }

    [Fact]
    public void A_monster_gets_its_type_numbers_rank_and_poise()
    {
        Assert.True(_rules.TryGetMonster(TestCombat.MonsterId, out var def));
        var monster = _rules.ForMonster(def, nowMs: 0);

        Assert.Equal(Faction.Monster, monster.Faction);
        Assert.Equal(def.Rank, monster.Rank);
        Assert.Equal(ArmorClass.Light, monster.ArmorClass);
        Assert.Equal(def.MaxHp, monster.Hp(0));
        Assert.Equal(def.MaxHp, monster.MaxHp(0));
        Assert.Equal(def.Atk, monster.Stats.Get(_rules.Config.Ids.Atk));
        Assert.Equal(def.PoiseMax, monster.Poise.Current);
        Assert.Equal(def.FlinchResist, monster.FlinchResist);
    }

    [Fact]
    public void Poise_changes_in_place_through_the_ref()
    {
        var player = _rules.ForPlayer(Class("warrior"), 0);
        player.Poise.Current -= 30;
        player.Poise.LastHitTick = 7;
        Assert.Equal(PoiseState.Full(100).Max - 30, player.Poise.Current);
        Assert.Equal(7u, player.Poise.LastHitTick);
    }

    [Fact]
    public void Die_is_idempotent_and_keeps_the_first_tick()
    {
        var player = _rules.ForPlayer(Class("warrior"), 0);
        player.Die(10);
        player.Die(20);
        Assert.False(player.IsAlive);
        Assert.Equal(10u, player.DiedAtTick);
    }

    [Fact]
    public void Saved_meters_round_trip_and_clamp_to_the_class_max()
    {
        var player = _rules.ForPlayer(Class("mage"), 0);
        CombatRules.Restore(player, new SavedMeters(Hp: 40, Mp: 7, Stamina: 30), nowMs: 0);
        Assert.Equal(new SavedMeters(40, 7, 30), CombatRules.Capture(player, nowMs: 0));

        CombatRules.Restore(player, new SavedMeters(Hp: 1_000_000, Mp: 1_000_000, Stamina: 1_000_000), nowMs: 0);
        var max = Class("mage");
        Assert.Equal(new SavedMeters(max.MaxHp, max.MaxMp, TestCombat.Tables().Single<StartingValuesM1>().StaminaPool),
            CombatRules.Capture(player, nowMs: 0));
    }

    [Fact]
    public void Times_come_from_ticks_and_the_spec_values()
    {
        // 20 Hz: 50 ms per tick; spec 12.7: monsters 30 s, bosses 10 min, lag compensation cap 200 ms.
        Assert.Equal(20, _rules.TickRateHz);
        Assert.Equal(0, _rules.MsAt(0));
        Assert.Equal(1_000, _rules.MsAt(20));
        Assert.Equal(600, _rules.MonsterRespawnTicks);
        Assert.Equal(12_000, _rules.BossRespawnTicks);
        Assert.Equal(5, _rules.HistoryCapacity); // 4 ticks back plus now
        Assert.Equal(1, _rules.TicksFor(1));
        Assert.Equal(1, _rules.TicksFor(50));
        Assert.Equal(2, _rules.TicksFor(51));
        Assert.True(_rules.TryGetMonster(TestCombat.BossId, out var boss));
        Assert.Equal(_rules.BossRespawnTicks, _rules.RespawnTicks(boss));
    }

    [Fact]
    public void The_history_returns_the_newest_entry_not_after_the_tick_and_the_oldest_past_the_cap()
    {
        var history = new PositionHistory(3);
        Assert.False(history.TryGetAt(5, out _));

        for (uint tick = 10; tick <= 14; tick++)
            history.Record(tick, FixVec2.FromCm((int)tick, 0));

        Assert.Equal(3, history.Count);
        Assert.True(history.TryGetAt(14, out var now));
        Assert.Equal(FixVec2.FromCm(14, 0), now);
        Assert.True(history.TryGetAt(13, out var back));
        Assert.Equal(FixVec2.FromCm(13, 0), back);
        Assert.True(history.TryGetAt(99, out var future));
        Assert.Equal(FixVec2.FromCm(14, 0), future);
        Assert.True(history.TryGetAt(2, out var capped)); // older than the ring: the cap
        Assert.Equal(FixVec2.FromCm(12, 0), capped);
    }

    [Fact]
    public void The_history_overwrites_a_tick_recorded_twice_and_clears()
    {
        var history = new PositionHistory(2);
        history.Record(1, FixVec2.FromCm(1, 0));
        history.Record(1, FixVec2.FromCm(2, 0));
        Assert.Equal(1, history.Count);
        Assert.True(history.TryGetAt(1, out var position));
        Assert.Equal(FixVec2.FromCm(2, 0), position);

        history.Clear();
        Assert.Equal(0, history.Count);
        Assert.False(history.TryGetAt(1, out _));
    }

    [Fact]
    public void The_history_handles_tick_wraparound()
    {
        var history = new PositionHistory(4);
        history.Record(uint.MaxValue - 1, FixVec2.FromCm(1, 0));
        history.Record(uint.MaxValue, FixVec2.FromCm(2, 0));
        history.Record(0, FixVec2.FromCm(3, 0));
        Assert.True(history.TryGetAt(uint.MaxValue, out var position));
        Assert.Equal(FixVec2.FromCm(2, 0), position);
    }
}
