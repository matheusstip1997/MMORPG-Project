using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Stats;
using GameServer.Tests.Combat;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Items;
using Shared.Simulation.Skills;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Skills;

/// <summary>
/// The content of the six shipping trees in the world with the committed tables (spec 3.5; WP-M2-5 PR 2, PLACEHOLDER
/// numbers): every active or buff node, learned and placed on a hotbar slot with its weapon equipped (R-3.5-07), starts its
/// own action when pressed (an evolution its own or its base's, R-3.5-05); every damaging one hits the demon in front, the
/// heal and the buffs land on the caster; the passives change their stat by their trigger. The player stands at the
/// origin facing north (facing 0 is +Y); the demon stands that far in front, its aggro range cut to 1 cm.
/// </summary>
public class SkillContentWorldTests
{
    private const ushort North = 0;
    private const byte Slot = 2;
    private const int Level = 50; // every node of the trees is learnable at 50 (the last tier-1 row)

    private sealed class StubEquipment(params WeaponType[] weapons) : IEquipmentStatSource
    {
        public WeaponType[] Weapons { get; set; } = weapons;

        public IReadOnlyList<ItemStatBonus> GetEquippedBonuses(long characterId) => Array.Empty<ItemStatBonus>();

        public IReadOnlyList<ItemStatBonus> GetEnchantBonuses(long characterId) => Array.Empty<ItemStatBonus>();
        public IReadOnlyList<ItemStatBonus> GetEquippedGemBonuses(long characterId) => Array.Empty<ItemStatBonus>();

        public IReadOnlyList<WeaponType> GetEquippedWeaponTypes(long characterId) => Weapons;
    }

    private sealed class Setup
    {
        /// <param name="nodeId">The one learned node, or null for none.</param>
        /// <param name="monsterYCm">The demon this far north of the player, or 0 for none.</param>
        public Setup(string classId, string? nodeId, int monsterYCm = 0)
        {
            Tables = TestCombat.Tables();
            Assert.True(Tables.TryGet(typeof(MonsterDef), out var monsters));
            monsters.Rows.Cast<MonsterDef>().Single(m => m.Id == TestCombat.MonsterId).AggroRangeCm = 1;
            Rules = SkillRules.From(Tables)!;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Log = CombatEventLog.Attach(Bus);
            Statuses = StatusEventLog.Attach(Bus);
            var markers = monsterYCm > 0 ? new[] { TestCombat.MonsterMarker(0, monsterYCm) } : Array.Empty<GameServer.World.Map.WorldMarker>();
            Harness = new WorldHarness(map: TestCombat.OpenMap(markers), events: new EventDispatchHook(Bus), bus: Bus,
                actions: ActionCatalog.From(Tables), combat: TestCombat.Rules(Tables, markers.Select(m => TestCombat.Point(markerId: m.Id)).ToArray()),
                items: ItemRules.From(Tables), progression: ProgressionRules.From(Tables), skills: Rules,
                projectiles: ProjectileRules.From(Tables));
            var character = Harness.Characters.Add(1, 0, new CharacterState(Level, 0, "", 0, 0, 0, 0, 100, 50, 100, SkillPoints: 0),
                classId: classId);
            if (nodeId != null)
                Harness.Characters.Skills[character.CharacterId] = new SkillRecord([new LearnedSkill(nodeId, 1)], HotbarJson: null);
            Player = Harness.Join(1);
            Harness.Ticks(1);
            Monster = Harness.World.Entities.OfType<Monster>().SingleOrDefault();
        }

        public ConfigSet Tables { get; }
        public SkillRules Rules { get; }
        public EventBus Bus { get; }
        public CombatEventLog Log { get; }
        public StatusEventLog Statuses { get; }
        public WorldHarness Harness { get; }
        public Player Player { get; }
        public Monster? Monster { get; }
        public ActionCatalog Catalog => Harness.World.Actions;
        public long NowMs => Harness.World.NowMs;

        /// <summary>Puts the node on <see cref="Slot"/>; the answer comes once the hotbar save committed (rule 5).</summary>
        public void Place(string nodeId)
        {
            Harness.SendSkillTree(1, 1, SkillTreeOp.AssignSlot, nodeId, Slot);
            Harness.Ticks(4);
            Assert.Equal(SkillTreeReason.None, Harness.Transport.SkillResultsFor(1).Last().Reason);
        }

        /// <summary>R-3.5-07: the node's weapons as the committed equipped set (what the inventory would report).</summary>
        public void Equip(string nodeId)
        {
            var weapons = SkillTree.Find(Tables, nodeId)!.Weapons;
            if (weapons.Length != 0)
                Player.Actions.EquippedWeapons = WeaponMasks.Of(weapons);
        }

        /// <summary>Releases the keys, then presses the slot in the frame the next tick applies, then ticks once.</summary>
        public void Press()
        {
            Harness.SendInputs(1, TestWorlds.Frame(2, facing: North));
            Harness.Ticks(1);
            Harness.SendInputs(1, TestWorlds.Frame(3, facing: North, skillSlot: Slot));
            Harness.Ticks(1);
        }

        public string Running() => Player.Actions.ActionIndex >= 0 ? Catalog[Player.Actions.ActionIndex].Id : "";

        public List<Damaged> DamageOnMonster() =>
            Log.Events.OfType<Damaged>().Where(d => d.SourceId == Player.Id && d.TargetId == Monster!.Id).ToList();

        public int Stat(string statId) => Player.Combatant!.Stats.Get(Harness.World.Combat!.Config.Stats.IdFor(statId));

        /// <summary>Learns, places, equips and presses the node; returns its resolved action.</summary>
        public string Use(string nodeId)
        {
            string actionId = Rules.Skill(nodeId)!.ActionId;
            Place(nodeId);
            Equip(nodeId);
            Press();
            Assert.Empty(Harness.Transport.ActionResultsFor(1)); // nothing refused (learned, weapon, MP, stamina)
            Assert.Equal(actionId, Running());
            return actionId;
        }
    }

    /// <summary>Every damaging node and how far in front its first hit reaches the demon (body radius 50 cm).</summary>
    public static TheoryData<string, string, int> DamagingNodes => new()
    {
        { "warrior", "warrior.upperCut", 200 },
        { "knight", "knight.shieldStrike", 150 }, // PR 1
        { "archer", "archer.stab", 150 }, // dual daggers
        { "archer", "archer.doubleCut", 150 }, // dual daggers
        { "archer", "archer.bombThrow", 1_000 },
        { "mage", "mage.icePike", 300 }, // the 2 m circle 1.5 m in front
        { "mage", "mage.rockThrow", 1_000 },
        { "assist", "assist.punchStrike", 150 },
        { "assist", "assist.jumpKick", 450 }, // 3 m jump, then the 1.5 m circle
        { "knight", "knight.piercingSequence", 300 },
        { "knight", "knight.flamePiercing", 300 },
        { "knight", "knight.shieldSequence", 150 },
        { "swordman", "swordman.dashStrike", 650 }, // 5 m dash, then the thrust
        { "swordman", "swordman.bloodStrike", 250 },
        { "swordman", "swordman.doubleSlash", 150 },
        { "swordman", "swordman.pommelHit", 150 },
        { "swordman", "swordman.heavyStrike", 150 },
    };

    /// <summary>The nodes that reach the caster only (a heal or a buff) and what lands.</summary>
    public static TheoryData<string, string, string> SelfNodes => new()
    {
        { "mage", "mage.concentration", "concentration" },
        { "swordman", "swordman.focus", "focus" },
        { "knight", "knight.newSkill", "staminaBuff" }, // inherits Stamina Buff (TODO(design))
        { "assist", "assist.heal", "" },
    };

    [Fact]
    public void Every_active_node_without_a_kit_test_is_covered_here()
    {
        // The M1 kit actions are covered by World/KitWorldTests; every other active or buff node is in one of the lists.
        var tables = TestCombat.Tables();
        var rules = SkillRules.From(tables)!;
        Assert.True(tables.TryGet(typeof(ActionBindings), out var bindings));
        var kitActions = bindings.Rows.Cast<ActionBindings>().SelectMany(b => b.SkillSlots).Where(s => s.Length != 0).ToHashSet();
        var covered = DamagingNodes.Select(row => (string)row[1]).Concat(SelfNodes.Select(row => (string)row[1]))
            .Append("warrior.provoke") // no damage (WP-M2-9): Provoke_provokes_the_demon_in_its_cone
            .ToHashSet();
        var expected = SkillTree.Rows(tables)
            .Where(n => n.Kind is SkillNodeKind.Active or SkillNodeKind.Buff)
            .Where(n => n.Id is "knight.newSkill" || !kitActions.Contains(rules.Skill(n.Id)!.ActionId))
            .Select(n => n.Id)
            .ToHashSet();
        Assert.Equal(expected.OrderBy(id => id, StringComparer.Ordinal), covered.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(DamagingNodes))]
    public void A_damaging_node_starts_its_own_action_from_the_hotbar_and_hits_the_demon(string classId, string nodeId, int monsterYCm)
    {
        var s = new Setup(classId, nodeId, monsterYCm);
        int hp = s.Monster!.Combatant!.Hp(s.NowMs);

        string actionId = s.Use(nodeId);
        Assert.Equal("skill." + nodeId, actionId); // its own action, not the base's (an evolution overrides it, R-3.5-05)
        for (int i = 0; i < 80 && s.DamageOnMonster().Count == 0; i++)
            s.Harness.Ticks(1);

        var hits = s.DamageOnMonster();
        Assert.NotEmpty(hits);
        Assert.All(hits, d => Assert.Equal(s.Catalog.Find(actionId)!.NetId, d.ActionId));
        Assert.True(s.Monster.Combatant.Hp(s.NowMs) < hp);
    }

    [Theory]
    [MemberData(nameof(SelfNodes))]
    public void A_heal_or_buff_node_lands_on_the_caster(string classId, string nodeId, string statusId)
    {
        var s = new Setup(classId, nodeId);
        int mpRegen = s.Stat(StatIds.MpRegen);

        s.Player.Combatant!.Meters.Set(ResourceKind.Hp, 100, s.NowMs); // hurt, so the heal has something to restore
        string actionId = s.Use(nodeId);
        var action = s.Catalog.Find(actionId)!;
        if (statusId == "concentration")
        {
            // WP-M2-9: MP regeneration up while the skill channels, gone when the channel ends (endsWithPhase).
            s.Harness.Ticks(action.StartupEnd + 2);
            Assert.True(s.Player.Combatant!.Statuses.Has(statusId), "concentration not on the caster while it channels");
            Assert.True(s.Stat(StatIds.MpRegen) > mpRegen); // StatusModifierDef concentration.mpRegen
            s.Harness.Ticks(action.TotalTicks);
            Assert.False(s.Player.Combatant.Statuses.Has(statusId), "concentration outlived the channel");
            Assert.Equal(mpRegen, s.Stat(StatIds.MpRegen));
            return;
        }
        s.Harness.Ticks(action.TotalTicks + 1);

        if (statusId.Length == 0)
            Assert.Contains(s.Statuses.Events.OfType<Healed>(), h => h.TargetId == s.Player.Id && h.Amount > 0);
        else
            Assert.True(s.Player.Combatant!.Statuses.Has(statusId), $"{nodeId}: {statusId} not on the caster");
    }

    [Fact]
    public void Provoke_provokes_the_demon_in_its_cone_and_it_turns_on_the_caster()
    {
        // WP-M2-9 (spec 3.7 M2 status list, provoke and threat): an ApplyStatus provoked on the enemies in the cone.
        var s = new Setup("warrior", "warrior.provoke", 250);
        string actionId = s.Use("warrior.provoke");
        Assert.Equal("skill.warrior.provoke", actionId);
        s.Harness.Ticks(s.Catalog.Find(actionId)!.TotalTicks + 1);

        Assert.True(s.Monster!.Combatant!.Statuses.Has("provoked"));
        Assert.Same(s.Player, s.Monster.Brain.Target);
    }

    /// <summary>The node's passives again with <paramref name="weapons"/> equipped; returns the stat.</summary>
    private static int WithWeapons(Setup s, string statId, params WeaponType[] weapons)
    {
        var stats = new StatsService(s.Harness.World.Combat!, null, new StubEquipment(weapons), null, _ => s.Player, () => s.NowMs)
        {
            Skills = s.Harness.World.Skills,
        };
        stats.RebuildSkills(s.Player);
        return s.Stat(statId);
    }

    [Fact]
    public void Sword_combat_adds_def_always_and_damage_dealt_only_without_a_shield()
    {
        var none = new Setup("swordman", null);
        int def = WithWeapons(none, StatIds.Def), dealt = WithWeapons(none, StatIds.DamageDealtPercent);
        var s = new Setup("swordman", "swordman.swordCombat");

        Assert.Equal((def + 2, dealt + 2), (WithWeapons(s, StatIds.Def), WithWeapons(s, StatIds.DamageDealtPercent)));
        Assert.Equal((def + 2, dealt), (WithWeapons(s, StatIds.Def, WeaponType.Shield), WithWeapons(s, StatIds.DamageDealtPercent, WeaponType.Shield)));
    }

    [Fact]
    public void Knight_s_oath_adds_def_and_more_def_with_a_shield()
    {
        var none = new Setup("knight", null);
        int def = WithWeapons(none, StatIds.Def);
        var s = new Setup("knight", "knight.knightsOuth");

        Assert.Equal(def + 2, WithWeapons(s, StatIds.Def, WeaponType.Spear));
        Assert.True(WithWeapons(s, StatIds.Def, WeaponType.Sword, WeaponType.Shield) > def + 2); // +2 % per level
    }

    [Fact]
    public void Spear_training_adds_dex_only_with_a_spear()
    {
        var none = new Setup("knight", null);
        int dex = WithWeapons(none, StatIds.Dex);
        var s = new Setup("knight", "knight.spareTraining");

        Assert.Equal(dex, WithWeapons(s, StatIds.Dex, WeaponType.Sword));
        Assert.Equal(dex + 2, WithWeapons(s, StatIds.Dex, WeaponType.Spear));
    }
}
