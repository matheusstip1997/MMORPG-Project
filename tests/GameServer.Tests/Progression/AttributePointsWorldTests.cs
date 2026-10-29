using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Skills;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Progression;

/// <summary>
/// Free attribute points in the world tick by tick (spec 3.4 R-3.4-08, WP-M2-13; user decisions 2026-10-29) with the
/// repository tables: a level from 2 grants a point, a placement is applied by the progression store, saved at once and
/// answered, announced and shown (stats, <see cref="ProgressionState"/>) only once its save committed (rule 5); refused in
/// combat and during an action; a repeated request gets the first answer; a skill respec gives the placed points back.
/// </summary>
public class AttributePointsWorldTests
{
    private sealed class Fixture : IEventConsumer<AttributesChanged>
    {
        public Fixture(int level = 3, int? attributePoints = null, int strPlaced = 0, string classId = "warrior")
        {
            var tables = TestCombat.Tables();
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<AttributesChanged>(this);
            Harness = new WorldHarness(map: TestCombat.OpenMap(TestCombat.MonsterMarker(0, 150)), events: new EventDispatchHook(Bus),
                bus: Bus, actions: ActionCatalog.From(tables), combat: TestCombat.Rules(tables, TestCombat.Point()),
                items: ItemRules.From(tables), progression: ProgressionRules.From(tables), skills: SkillRules.From(tables));
            Character = Harness.Characters.Add(1, 0, new CharacterState(level, 0, "", 0, 0, 0, 0, 100, 50, 100, SkillPoints: level,
                AttributePoints: attributePoints ?? level - 1, StrPlaced: strPlaced), classId: classId);
            Player = Harness.Join(1);
            Harness.Ticks(1);
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public CharacterRecord Character { get; }
        public Player Player { get; }
        public long Id => Character.CharacterId;
        public GameWorld World => Harness.World;
        public FakeTransport Transport => Harness.Transport;
        public List<AttributesChanged> Events { get; } = [];

        public void Handle(in EventEnvelope envelope, in AttributesChanged payload) => Events.Add(payload);

        private uint _requests;

        /// <summary>Sends a request and runs the ticks for its save and the answer (saves complete at once by default).</summary>
        public AttributeSpendResult? Place(int attribute, uint? requestId = null, int ticks = 4)
        {
            uint id = requestId ?? ++_requests;
            int before = Transport.AttributeResultsFor(1).Count;
            Harness.SendAttributeSpend(1, id, attribute);
            Harness.Ticks(ticks);
            var results = Transport.AttributeResultsFor(1);
            return results.Count > before ? results[^1] : null;
        }

        public int Stat(string id) => Player.Combatant!.Stats.Get(World.Combat!.Config.Stats.IdFor(id));

        public AttributeSnapshot Committed() => World.Progression!.TryGetAttributes(Id, out var a) ? a : default;
    }

    [Fact]
    public void A_placed_point_is_answered_and_raises_the_stats_only_once_its_save_committed()
    {
        var f = new Fixture();
        int str = f.Stat(StatIds.Str), atk = f.Stat(StatIds.Atk), def = f.Stat(StatIds.Def);
        f.Harness.Saves.AutoComplete = false;

        Assert.Null(f.Place(Attributes.Str, ticks: 3));
        Assert.Empty(f.Transport.AttributeResultsFor(1)); // rule 5: no answer before the commit
        Assert.Equal((str, atk), (f.Stat(StatIds.Str), f.Stat(StatIds.Atk))); // nor any stat
        Assert.Equal(new AttributeSnapshot(2, 0, 0, 0, 0), f.Committed());
        Assert.Equal(new AttributeSnapshot(1, 1, 0, 0, 0), f.World.Progression!.WorkingAttributes(f.Id));
        var job = Assert.Single(f.Harness.Saves.Jobs, j => j.Progression is { AttributeOps.Count: 1 });
        Assert.Equal((SaveReason.AttributePoints, 1, 1), (job.Reason, job.State.AttributePoints, job.State.StrPlaced));
        var op = Assert.Single(job.Progression!.AttributeOpList);
        Assert.Equal((AttributeOpKind.Place, Attributes.Str, 1, 2, 1), (op.Kind, op.Attribute, op.Points, op.PointsBefore, op.PointsAfter));
        Assert.Empty(f.Events);

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        var answer = Assert.Single(f.Transport.AttributeResultsFor(1));
        Assert.Equal((AttributeReason.None, (byte)Attributes.Str), (answer.Reason, answer.Attribute));
        Assert.Equal(new AttributeSnapshot(1, 1, 0, 0, 0), f.Committed());
        // R-3.4-08: the placed point is the Attribute source, which the derived stats read like the growth.
        Assert.Equal(str + 1, f.Stat(StatIds.Str));
        Assert.Equal(atk + 1, f.Stat(StatIds.Atk)); // atk: str 100 hundredths
        Assert.True(f.Stat(StatIds.Def) >= def);
        var changed = Assert.Single(f.Events);
        Assert.Equal((f.Id, Attributes.Str, 1, 1), (changed.CharacterId, changed.Attribute, changed.Points, changed.Unspent));
        var last = f.Transport.ProgressionFor(1)[^1];
        Assert.Equal((ProgressionCause.AttributePoints, 1, 1), (last.Cause, last.AttributePoints, last.StrPlaced));
        // The answer comes before the state that shows it (spec 9.13 pattern).
        var ids = f.Transport.ReliableIdsFor(1);
        Assert.True(ids.LastIndexOf(ReliableMessageId.AttributeSpendResult) < ids.LastIndexOf(ReliableMessageId.ProgressionState));
        var stats = f.Player.Combatant!.Stats;
        var modifier = Assert.Single(Enumerable.Range(0, stats.ModifierCount).Select(stats.ModifierAt),
            m => m.SourceType == StatSourceType.Attribute);
        Assert.Equal((f.World.Combat!.Config.Stats.IdFor(StatIds.Str), 1, 0L), (modifier.StatId, modifier.Flat, modifier.SourceId));
    }

    [Fact]
    public void A_repeated_request_gets_the_first_answer_and_changes_nothing()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;
        Assert.Null(f.Place(Attributes.Dex, requestId: 5, ticks: 2));
        Assert.Null(f.Place(Attributes.Dex, requestId: 5, ticks: 2)); // a double submit while the first waits: nothing
        Assert.Equal(new AttributeSnapshot(1, 0, 1, 0, 0), f.World.Progression!.WorkingAttributes(f.Id));
        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);
        Assert.Single(f.Transport.AttributeResultsFor(1));

        var again = f.Place(Attributes.Dex, requestId: 5);
        Assert.Equal(AttributeReason.None, again!.Reason); // the first answer again
        Assert.Equal(new AttributeSnapshot(1, 0, 1, 0, 0), f.Committed());
        Assert.Single(f.Events);
    }

    [Fact]
    public void Refusals_are_answered_at_once()
    {
        var f = new Fixture(level: 1, attributePoints: 0);
        f.Harness.Saves.AutoComplete = false;
        Assert.Equal(AttributeReason.NoAttributePoints, f.Place(Attributes.Sta, ticks: 1)!.Reason);

        var fighter = new Fixture();
        fighter.Player.LastCombatAtMs = fighter.Harness.Clock.UtcNowMs;
        Assert.Equal(AttributeReason.AttributeBlockedCombat, fighter.Place(Attributes.Int, ticks: 1)!.Reason);
        Assert.Equal(new AttributeSnapshot(2, 0, 0, 0, 0), fighter.World.Progression!.WorkingAttributes(fighter.Id));
        Assert.Empty(fighter.Events);
    }

    [Fact]
    public void A_placement_during_an_action_is_refused()
    {
        var f = new Fixture();
        f.Harness.SendInputs(1, TestWorlds.Frame(1, actions: InputActions.Attack));
        f.Harness.Ticks(1);
        Assert.True(f.Player.Actions.IsBusy);
        Assert.Equal(AttributeReason.AttributeBlockedCombat, f.Place(Attributes.Str, ticks: 1)!.Reason);
    }

    [Fact]
    public void Each_level_from_2_gives_one_point_and_a_new_character_has_none()
    {
        var f = new Fixture(level: 1, attributePoints: 0);
        Assert.Equal(0, f.Transport.ProgressionFor(1)[^1].AttributePoints);

        int gained = f.World.Exp!.Grant(f.Id, f.Player.Id, 172 + 276 + 10); // level 1 to 3 (ProgressionWorldTests)
        f.Harness.Ticks(1);

        Assert.Equal(2, gained);
        Assert.Equal(new AttributeSnapshot(2, 0, 0, 0, 0), f.Committed());
        var last = f.Transport.ProgressionFor(1)[^1];
        Assert.Equal((3, 2, ProgressionCause.LevelUp), (last.Level, last.AttributePoints, last.Cause));
        Assert.Contains(f.Harness.Saves.Jobs, j => j.Reason == SaveReason.LevelUp && j.State.AttributePoints == 2);
    }

    [Fact]
    public void A_respec_gives_the_placed_points_back_in_its_save_and_the_stats_drop_after_the_commit()
    {
        var f = new Fixture(level: 5, attributePoints: 2, strPlaced: 2);
        int str = f.Stat(StatIds.Str);
        Assert.Equal(new AttributeSnapshot(2, 2, 0, 0, 0), f.Committed());
        f.Harness.Saves.AutoComplete = false;

        f.Harness.SendSkillTree(1, 1, SkillTreeOp.Respec);
        f.Harness.Ticks(3);
        // Every character has learned something to respec? A fresh one has not: learn first when needed.
        if (f.Transport.SkillResultsFor(1).LastOrDefault() is { Reason: SkillTreeReason.NothingToRespec })
        {
            f.Harness.Saves.AutoComplete = true;
            f.Harness.SendSkillTree(1, 2, SkillTreeOp.Learn, "warrior.piercingAttack");
            f.Harness.Ticks(4);
            f.Harness.Saves.AutoComplete = false;
            f.Harness.SendSkillTree(1, 3, SkillTreeOp.Respec);
            f.Harness.Ticks(3);
        }
        Assert.Equal(str, f.Stat(StatIds.Str)); // not before the commit
        var job = Assert.Single(f.Harness.Saves.Jobs, j => j.Skills is { Ops.Count: > 0 } s && s.Ops.Any(o => o.Op == SkillTreeOp.Respec));
        var reset = Assert.Single(job.Progression!.AttributeOpList); // the same save, one transaction
        Assert.Equal((AttributeOpKind.Reset, -1, 2, 2, 4), (reset.Kind, reset.Attribute, reset.Points, reset.PointsBefore, reset.PointsAfter));
        Assert.EndsWith(":attributes", reset.OpKey);
        Assert.Equal((4, 0), (job.State.AttributePoints, job.State.StrPlaced));

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        Assert.Equal(new AttributeSnapshot(4, 0, 0, 0, 0), f.Committed());
        Assert.Equal(str - 2, f.Stat(StatIds.Str));
        var changed = Assert.Single(f.Events);
        Assert.Equal((-1, 2, 4), (changed.Attribute, changed.Points, changed.Unspent));
        Assert.Equal(4, f.Transport.ProgressionFor(1)[^1].AttributePoints);
    }

    [Fact]
    public void Placed_points_are_loaded_with_the_character_and_count_from_the_spawn()
    {
        var plain = new Fixture(level: 5, attributePoints: 4);
        var placed = new Fixture(level: 5, attributePoints: 2, strPlaced: 2);
        Assert.Equal(plain.Stat(StatIds.Str) + 2, placed.Stat(StatIds.Str));
        Assert.Equal(plain.Stat(StatIds.Atk) + 2, placed.Stat(StatIds.Atk));
        var first = placed.Transport.ProgressionFor(1)[0];
        Assert.Equal((ProgressionCause.Initial, 2, 2), (first.Cause, first.AttributePoints, first.StrPlaced));
    }
}
