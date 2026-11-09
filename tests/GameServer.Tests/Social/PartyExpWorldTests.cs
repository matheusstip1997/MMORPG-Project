using GameServer.Entities;
using GameServer.Events;
using GameServer.Law;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Sessions;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Progression;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Social;

/// <summary>
/// What a party changes in combat and progression (spec 6.5 "M4 party", R-3.6-03, R-2.4-03; M4 decision 1; WP-M4-2) with the
/// repository tables: a kill's exp shared by the members in range, the base split by damage with the floor and each paid by its
/// own rank and level; nothing to a member out of range or dead; party members never damage and always see each other; the
/// party frame's HP from the combatant.
/// </summary>
public class PartyExpWorldTests
{
    private sealed class Fixture
    {
        public Fixture()
        {
            var tables = TestCombat.Tables();
            Rules = ProgressionRules.From(tables)!;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<ExpGained>(Log);
            Harness = new WorldHarness(map: TestCombat.OpenMap(TestCombat.MonsterMarker(2_000, 0)),
                events: new EventDispatchHook(Bus), bus: Bus, actions: ActionCatalog.From(tables),
                combat: TestCombat.Rules(tables, TestCombat.Point()), progression: Rules, party: TestWorlds.Party());
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public ProgressionRules Rules { get; }
        public ExpLog Log { get; } = new();
        public GameWorld World => Harness.World;
        private uint _requests;

        public Player Join(int peerId, int xCm, int level = 1, Rank rank = Rank.F)
        {
            Harness.Characters.Add(peerId, 0, new CharacterState(level, 0, SessionManager.MainWorldKey, xCm, 0, 0, 0, 100, 10, 100, rank));
            return Harness.Join(peerId);
        }

        /// <summary>A party of the peers, the first the leader.</summary>
        public void Form(params int[] peers)
        {
            for (int i = 1; i < peers.Length; i++)
            {
                Harness.SendParty(peers[0], ++_requests, PartyAction.Invite, targetName: $"c{peers[i]}_0");
                Harness.Ticks(1);
                uint invite = Harness.Transport.PartyInvitesFor(peers[i])[^1].InviteId;
                Harness.SendParty(peers[i], ++_requests, PartyAction.Accept, invite);
                Harness.Ticks(1);
            }
        }

        public MonsterDef Demon => Assert.Single(World.Combat!.Monsters, m => m.Id == "m1.demon");

        /// <summary>Hits as HitResolver publishes them, then the kill by <paramref name="killer"/> at (0, 0), all in one tick.</summary>
        public void Kill(Player killer, params (Player Attacker, int Damage)[] hits)
        {
            foreach (var (attacker, damage) in hits)
                Bus.Publish(new Damaged(attacker.Id, 999, 0, damage, 0, false), attacker.CharacterId);
            Bus.Publish(new Killed(killer.Id, 999, 0, Demon.NetId, 0, 0), killer.CharacterId);
            Harness.Ticks(1);
        }

        /// <summary>The character's own gain from the demon: its rank and band (R-3.6-03), no book.</summary>
        public long Gain(Player player)
        {
            var progress = World.Progression!.Snapshot(player.CharacterId);
            var curves = Rules.Curves;
            return ExpMath.Gain(curves.BaseExpOf(Demon), curves.RankPercent(progress.Rank, Demon.Rank), curves.BandPpm(progress.Level, progress.Rank));
        }
    }

    internal sealed class ExpLog : IEventConsumer<ExpGained>
    {
        public List<ExpGained> Events { get; } = [];
        public void Handle(in EventEnvelope envelope, in ExpGained payload) => Events.Add(payload);
    }

    [Fact]
    public void A_kill_pays_both_members_by_their_own_rank_and_the_damage_split()
    {
        var f = new Fixture();
        var a = f.Join(1, 0);
        var b = f.Join(2, 500, level: 5, rank: Rank.E);
        f.Harness.Ticks(1);
        f.Form(1, 2);
        long gainA = f.Gain(a), gainB = f.Gain(b);
        Assert.NotEqual(gainA, gainB); // different ranks and levels: different modifiers

        f.Kill(a, (a, 300), (b, 100));

        var weights = new long[2];
        PartyExpSplit.Weights([300, 100], 10, weights); // 75 % and 25 %, above the floor
        Assert.Equal([(a.CharacterId, PartyExpSplit.Share(gainA, weights[0])), (b.CharacterId, PartyExpSplit.Share(gainB, weights[1]))],
            f.Log.Events.Select(e => (e.CharacterId, e.Amount)));
        Assert.Equal(b.Id, f.Log.Events[1].EntityId);
    }

    [Fact]
    public void A_member_out_of_range_or_dead_gets_nothing_and_a_solo_kill_pays_the_whole_gain()
    {
        var f = new Fixture();
        var a = f.Join(1, 0);
        var far = f.Join(2, 9_000);
        var dead = f.Join(3, 300);
        var solo = f.Join(4, 0);
        f.Harness.Ticks(1);
        f.Form(1, 2, 3);
        dead.Combatant!.Die(f.World.ServerTick);

        f.Kill(a, (a, 50), (far, 50), (dead, 50));
        f.Kill(solo, (solo, 10));

        Assert.Equal([(a.CharacterId, f.Gain(a)), (solo.CharacterId, f.Gain(solo))], f.Log.Events.Select(e => (e.CharacterId, e.Amount)));
    }

    [Fact]
    public void A_member_who_dealt_no_damage_in_range_still_gets_the_floor_share()
    {
        var f = new Fixture();
        var a = f.Join(1, 0);
        var b = f.Join(2, 200);
        f.Harness.Ticks(1);
        f.Form(1, 2);

        f.Kill(a, (a, 1_000));

        var weights = new long[2];
        PartyExpSplit.Weights([1_000, 0], 10, weights);
        Assert.Equal(PartyExpSplit.Share(f.Gain(b), weights[1]), Assert.Single(f.Log.Events, e => e.CharacterId == b.CharacterId).Amount);
        Assert.True(weights[1] > 0);
    }

    [Fact]
    public void Party_members_never_damage_and_always_see_each_other()
    {
        var f = new Fixture();
        var a = f.Join(1, 0);
        var b = f.Join(2, 200);
        var stranger = f.Join(3, 400);
        f.Harness.Ticks(1);
        f.Form(1, 2);

        Assert.False(PvpRules.CanDamage(a, b, 0, f.World.PartyQuery)); // party immunity (inert until M5)
        Assert.False(PvpRules.CanDamage(a, stranger, 0, f.World.PartyQuery));
        var hidden = new HideAll();
        var visibility = new PartyVisibility(hidden, f.World.PartyQuery);
        Assert.True(visibility.CanSee(a, b));
        Assert.True(visibility.CanSee(b, a));
        Assert.False(visibility.CanSee(a, stranger)); // not in the party: the inner query decides
        Assert.Same(hidden, PartyVisibility.Wrap(hidden, null));
    }

    [Fact]
    public void The_party_frame_carries_the_class_level_and_hp_percent()
    {
        var f = new Fixture();
        f.Join(1, 0);
        var b = f.Join(2, 200, level: 3);
        f.Harness.Ticks(1);
        f.Form(1, 2);

        var member = Assert.Single(f.Harness.Transport.PartyStatesFor(1)[^1].Members, m => m.EntityId == b.Id);
        Assert.True(f.World.Combat!.TryGetClass(b.Combatant!.TemplateId, out var classDef));
        Assert.Equal(((ushort)classDef.NetId, (ushort)3, true), (member.ClassNetId, member.Level, member.Online));
        Assert.InRange(member.HpPercent, (byte)1, (byte)100);
    }

    private sealed class HideAll : IVisibilityQuery
    {
        public bool CanSee(Entity observer, Entity target) => false;
    }
}
