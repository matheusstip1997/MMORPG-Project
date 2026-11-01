using GameServer.Bystial;
using GameServer.Dev.Commands;
using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Tests.Economy;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Jobs;

namespace GameServer.Tests.Dev;

/// <summary>
/// The dev console in the world (spec 11.7 dev console, WP-M3-13), with the repository tables: a chat line starting with
/// <c>/</c> never reaches local chat, every command goes through the writer that owns the state with an operation key of its
/// own, a command that changes saved state is answered only once its save committed (rule 5), every id and limit comes from
/// the tables, values only go up, and only dev-login accounts are served.
/// </summary>
public class DevConsoleWorldTests
{
    private const int Peer = 1;

    internal sealed class FakeDevAccounts : IDevAccounts
    {
        public HashSet<long> NotDev { get; } = [];
        public HashSet<long> Checking { get; } = [];
        public List<long> Prefetched { get; } = [];

        public DevAccountStatus Status(long accountId) =>
            Checking.Contains(accountId) ? DevAccountStatus.Checking : NotDev.Contains(accountId) ? DevAccountStatus.NotDev : DevAccountStatus.Dev;

        public void Prefetch(long accountId) => Prefetched.Add(accountId);
    }

    internal sealed class Fixture
    {
        public Fixture(int level = 12, bool console = true, int maxSpawn = 3, long gold = 0)
        {
            var tables = TestCombat.Tables();
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<DevCommandRan>(Log);
            var map = TestMaps.Map(TestMaps.Open().Grid,
            [
                TestMaps.SpawnMarker(0, 0), TestCombat.MonsterMarker(2_000, 0), EconomyFixture.Npc("test.npc.jobMaster", 200, 0, "m1.jobMaster"),
            ]);
            var combat = TestCombat.Rules(tables, TestCombat.Point());
            Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus, combat: combat,
                items: ItemRules.From(tables)!, economy: EconomyRules.From(tables)!, progression: ProgressionRules.From(tables)!,
                bystial: BystialRules.From(tables, combat.Config), jobs: JobRules.From(tables)!,
                chat: TestWorlds.Chat(burst: 50, messagesPerPeriod: 50, maxQueuedLines: 8));
            if (console)
                Console = Harness.World.EnableDevConsole(Accounts, new DevTools { MaxSpawnPerCommand = maxSpawn });
            var character = Harness.Characters.Add(Peer, 0, new CharacterState(level, 0, "", 0, 0, 0, 0, 100, 50, 100));
            Harness.Characters.Items[character.CharacterId] = [];
            if (gold > 0)
                Harness.Characters.StoreGold(character.CharacterId, gold);
            Player = Harness.Join(Peer);
            Harness.Ticks(1);
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public EventLog Log { get; } = new();
        public FakeDevAccounts Accounts { get; } = new();
        public DevConsole? Console { get; }
        public Player Player { get; }
        public long CharacterId => Player.CharacterId;
        public InventoryService Inventory => Harness.World.Inventory!;
        public ProgressionStore Progression => Harness.World.Progression!;

        /// <summary>Sends one line; the save it asks for is issued the next tick and its completion applied the one after.</summary>
        public DevCommandResult Run(string line, int ticks = 3)
        {
            int before = Answers.Count;
            Harness.SendChat(Peer, line);
            Harness.Ticks(ticks);
            Assert.True(Answers.Count > before, $"no answer to '{line}'");
            return Answers[^1];
        }

        public List<DevCommandResult> Answers => Harness.Transport.DevResultsFor(Peer);

        public IEnumerable<Shared.Simulation.Items.Bag> Bags()
        {
            var inventory = Inventory.CurrentInventory(CharacterId);
            return Enumerable.Range(0, inventory.ContainerCount).Select(inventory.BagAt);
        }

        public int Held(string itemId)
        {
            Assert.True(Inventory.Rules.TryGetItem(itemId, out var item));
            int count = 0;
            foreach (var container in Bags())
            {
                for (int slot = 0; slot < container.SlotCount; slot++)
                    count += container[slot].ItemNetId == item.NetId ? container[slot].Count : 0;
            }
            return count;
        }
    }

    internal sealed class EventLog : IEventConsumer<DevCommandRan>
    {
        public List<DevCommandRan> Events { get; } = [];
        public void Handle(in EventEnvelope envelope, in DevCommandRan payload) => Events.Add(payload);
    }

    [Fact]
    public void A_slash_line_never_reaches_local_chat_and_a_plain_line_still_does()
    {
        var f = new Fixture();
        f.Run("/help");
        f.Harness.SendChat(Peer, "hello");
        f.Harness.Ticks(1);

        var lines = f.Harness.Transport.ChatLinesFor(Peer);
        Assert.Equal(["hello"], lines.Select(l => l.Text));
        Assert.Equal(DevCommandParser.Commands.Count, f.Answers[0].Lines.Length);
    }

    [Fact]
    public void Without_a_console_every_slash_line_is_refused_DevToolsOff_and_nothing_changes()
    {
        var f = new Fixture(console: false);
        var answer = f.Run("/give m3.oreT1 4");

        Assert.Equal(DevReason.DevToolsOff, answer.Reason);
        Assert.Equal(0, f.Held("m3.oreT1"));
        Assert.Empty(f.Harness.Transport.ChatLinesFor(Peer));
    }

    [Fact]
    public void A_Steam_account_is_never_served_and_a_checking_one_is_asked_to_send_again()
    {
        var f = new Fixture();
        f.Accounts.NotDev.Add(f.Player.AccountId);
        Assert.Equal(DevReason.NotDevAccount, f.Run("/give m3.oreT1 4").Reason);
        f.Accounts.NotDev.Clear();
        f.Accounts.Checking.Add(f.Player.AccountId);
        Assert.Contains("send the command again", f.Run("/give m3.oreT1 4").Lines[0]);

        Assert.Equal(0, f.Held("m3.oreT1"));
        Assert.All(f.Log.Events, e => Assert.Equal(DevReason.NotDevAccount, e.Reason));
    }

    [Fact]
    public void A_session_start_prefetches_the_account_check()
    {
        var f = new Fixture();
        Assert.Contains(f.Player.AccountId, f.Accounts.Prefetched);
    }

    [Fact]
    public void Give_adds_through_the_inventory_and_is_answered_only_after_its_save_commits()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;

        f.Harness.SendChat(Peer, "/give m3.oreT1 4");
        f.Harness.Ticks(2); // applied, its save issued, not completed

        Assert.Empty(f.Answers);
        Assert.Equal(4, f.Held("m3.oreT1"));
        var save = f.Harness.Saves.Jobs.Single(j => j.Inventory is { Ops.Count: > 0 });
        Assert.Equal(SaveReason.DevCommand, save.Reason);
        var op = Assert.Single(save.Inventory!.Ops);
        Assert.StartsWith("dev:test-run:", op.OpKey);
        Assert.Equal((nameof(InventoryReason.Admin), $"dev-console:account:{f.Player.AccountId}"), (op.Reason, op.Source));
        Assert.Empty(f.Log.Events); // published only after the commit

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        var answer = Assert.Single(f.Answers);
        Assert.Equal((DevReason.None, "+4 m3.oreT1"), (answer.Reason, answer.Lines.Single()));
        var ran = Assert.Single(f.Log.Events);
        Assert.Equal(((byte)DevCommandKind.Give, 401, 4L, DevReason.None), (ran.Command, ran.Subject, ran.Value, ran.Reason));
        Assert.Equal(0, f.Console!.WaitingCount);
    }

    [Fact]
    public void A_second_command_while_the_first_saves_is_refused_DevBusy_and_applies_nothing()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;
        f.Harness.SendChat(Peer, "/give m3.oreT1 4");
        f.Harness.Ticks(1);

        Assert.Equal(DevReason.DevBusy, f.Run("/give m3.oreT1 4", ticks: 1).Reason);
        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        Assert.Equal(4, f.Held("m3.oreT1"));
        Assert.Equal([DevReason.DevBusy, DevReason.None], f.Answers.Select(a => a.Reason));
    }

    [Fact]
    public void Two_lines_are_two_commands_each_with_its_own_operation_key()
    {
        var f = new Fixture();
        f.Run("/give m3.oreT1 4");
        f.Run("/give m3.oreT1 4");

        Assert.Equal(8, f.Held("m3.oreT1"));
        var keys = f.Harness.Saves.Jobs.SelectMany(j => j.Inventory?.Ops ?? []).Select(o => o.OpKey).ToList();
        Assert.Equal(2, keys.Distinct().Count());
    }

    [Fact]
    public void Give_checks_the_tables_unknown_ids_gear_options_and_the_gem_slots()
    {
        var f = new Fixture();
        Assert.Equal(DevReason.UnknownItem, f.Run("/give m9.nothing").Reason);
        Assert.Equal(DevReason.UnknownItem, f.Run("/give m3.forgedSword rarity=mythic").Reason);
        Assert.Equal(DevReason.BadArguments, f.Run("/give m3.oreT1 0").Reason);
        Assert.Equal(DevReason.BadArguments, f.Run("/give m3.oreT1 1 enchant=2").Reason); // not gear
        Assert.Equal(DevReason.OverLimit, f.Run($"/give m3.forgedSword enchant={EnchantLimits.MaxLevel + 1}").Reason);
        Assert.Equal(DevReason.UnknownItem, f.Run("/give m3.forgedSword gems=m3.oreT1").Reason); // not a GemDef
        Assert.Equal(DevReason.OverLimit, f.Run("/give m3.forgedSword gems=m3.rubyT1,m3.rubyT2").Reason); // one gem slot
        Assert.Equal(0, f.Held("m3.forgedSword"));
    }

    [Fact]
    public void Give_makes_gear_with_its_enchant_level_and_gems_as_the_writer_stores_them()
    {
        var f = new Fixture();
        var answer = f.Run("/give m3.forgedSword enchant=3 gems=m3.rubyT1");

        Assert.Equal(DevReason.None, answer.Reason);
        Assert.Equal(1, f.Held("m3.forgedSword"));
        var instance = f.Bags().SelectMany(b => Enumerable.Range(0, b.SlotCount).Select(s => b[s]))
            .Single(s => s.IsInstance);
        Assert.True(f.Inventory.TryGetProperties(f.CharacterId, instance.InstanceId, out var properties));
        Assert.Equal((3, "m3.rubyT1", f.CharacterId), (properties.Enchant, properties.Gems.Single(), properties.OriginalOwner));
    }

    [Fact]
    public void A_full_bag_gives_what_fits_and_says_so()
    {
        var f = new Fixture();
        int slots = f.Inventory.Current(f.CharacterId).SlotCount;
        var answer = f.Run($"/give m3.oreT1 {slots * 99 + 5}");

        Assert.Equal(DevReason.None, answer.Reason);
        Assert.Contains($"only {slots * 99} of", answer.Lines[0]);
        Assert.Equal(DevReason.BagFull, f.Run("/give m3.oreT1 1").Reason);
    }

    [Fact]
    public void Take_and_clearbag_remove_through_the_inventory()
    {
        var f = new Fixture();
        f.Run("/give m3.oreT1 120"); // two stacks
        f.Run("/give m3.oreT2 3");

        Assert.Equal(DevReason.NotInBag, f.Run("/take m3.oreT1 121").Reason);
        Assert.Equal(DevReason.None, f.Run("/take m3.oreT1 100").Reason);
        Assert.Equal(20, f.Held("m3.oreT1"));
        Assert.Equal("2 stack(s) removed", f.Run("/clearbag").Lines[0]);
        Assert.Equal((0, 0), (f.Held("m3.oreT1"), f.Held("m3.oreT2")));
    }

    [Fact]
    public void Gold_sets_the_balance_up_or_down_within_maxGold()
    {
        var f = new Fixture(gold: 100);
        Assert.Equal("gold 100 -> 500", f.Run("/gold 500").Lines[0]);
        Assert.Equal(500, f.Inventory.CommittedGold(f.CharacterId));
        Assert.Equal("gold 500 -> 20", f.Run("/gold 20").Lines[0]);
        Assert.Equal(20, f.Inventory.CommittedGold(f.CharacterId));
        Assert.Equal(DevReason.OverLimit, f.Run($"/gold {f.Inventory.MaxGold + 1}").Reason);
        Assert.Equal(500L, f.Harness.Transport.GoldFor(Peer).Max());
    }

    [Fact]
    public void Level_takes_the_curves_exp_and_pays_every_levels_points_but_never_goes_down()
    {
        var f = new Fixture(level: 12);
        var before = f.Progression.Snapshot(f.CharacterId);
        var curves = f.Progression.Rules.Curves;

        var answer = f.Run("/level 16");

        var after = f.Progression.Snapshot(f.CharacterId);
        Assert.Equal((16, 0L), (after.Level, after.Exp));
        Assert.Equal(before.SkillPoints + 4 * curves.SkillPointsPerLevel, after.SkillPoints);
        Assert.Contains($"+{4 * curves.AttributePointsPerLevel} attribute points", answer.Lines[0]);
        Assert.Equal(DevReason.LevelDown, f.Run("/level 15").Reason);
        Assert.Equal(DevReason.OverLimit, f.Run($"/level {curves.MaxLevel + 1}").Reason);
        Assert.Contains("already", f.Run("/level 16").Lines[0]);
        Assert.Equal(16, f.Harness.Transport.ProgressionFor(Peer)[^1].Level);
    }

    [Fact]
    public void Exp_and_points_go_through_the_progression_store()
    {
        var f = new Fixture(level: 12);
        var before = f.Progression.Snapshot(f.CharacterId);
        f.Run("/exp 10");
        Assert.Equal(before.Exp + 10, f.Progression.Snapshot(f.CharacterId).Exp);

        f.Run("/points skill 5");
        f.Run("/points attr 2");

        Assert.Equal(before.SkillPoints + 5, f.Progression.Snapshot(f.CharacterId).SkillPoints);
        Assert.True(f.Progression.TryGetAttributes(f.CharacterId, out var attributes));
        Assert.Equal(2, attributes.Unspent);
        Assert.Equal(DevReason.BadArguments, f.Run("/points luck 2").Reason);
    }

    [Fact]
    public void Job_runs_the_picks_own_rules_and_joblevel_raises_a_held_job()
    {
        var f = new Fixture(level: 12);
        Assert.Equal(DevReason.UnknownJob, f.Run("/joblevel blacksmith 5").Reason); // not held yet
        Assert.Equal(DevReason.JobRefused, f.Run("/job jeweler secondary").Reason); // the main job comes first
        Assert.Equal(DevReason.UnknownJob, f.Run("/job fisher").Reason);

        Assert.Equal("blacksmith is your main job", f.Run("/job blacksmith").Lines[0]);
        var jobs = f.Harness.World.Jobs!;
        Assert.Equal("blacksmith", jobs.JobIn(f.CharacterId, JobSlot.Main));
        Assert.Equal(1, f.Held("m3.moldSword")); // the starter items too

        Assert.Equal("blacksmith level 1 -> 10", f.Run("/joblevel blacksmith 10").Lines[0]);
        Assert.Equal(10, jobs.JobLevel(f.CharacterId, "blacksmith"));
        Assert.Equal(DevReason.LevelDown, f.Run("/joblevel blacksmith 9").Reason);
        Assert.Equal(DevReason.OverLimit, f.Run($"/joblevel blacksmith {jobs.Rules.MaxLevel + 1}").Reason);
        Assert.Equal("jeweler is your secondary job", f.Run("/job jeweler").Lines[0]); // the free slot by default
    }

    [Fact]
    public void A_secondary_job_stays_at_or_below_the_main()
    {
        var f = new Fixture(level: 12);
        f.Run("/job blacksmith");
        f.Run("/joblevel blacksmith 4");
        f.Run("/job jeweler");

        var answer = f.Run("/joblevel jeweler 10");

        Assert.Contains("stays at or below the main", answer.Lines[0]);
        Assert.True(f.Harness.World.Jobs!.JobLevel(f.CharacterId, "jeweler") <= 4);
    }

    [Fact]
    public void Kills_raise_the_count_the_book_reads_and_never_lower_it()
    {
        var f = new Fixture();
        var answer = f.Run($"/kills {TestCombat.MonsterId} 14");

        Assert.Equal(DevReason.None, answer.Reason);
        Assert.Equal(14, f.Progression.KillCount(f.CharacterId, TestCombat.MonsterId));
        var save = f.Harness.Saves.Jobs.Last(j => j.Progression != null);
        Assert.Contains(save.Progression!.KillCounts, k => k.MonsterDefId == TestCombat.MonsterId && k.Count == 14);
        Assert.Equal(DevReason.ValueDown, f.Run($"/kills {TestCombat.MonsterId} 3").Reason);
        Assert.Equal(DevReason.UnknownMonster, f.Run("/kills m9.ghost 3").Reason);
    }

    [Fact]
    public void Teleport_moves_to_a_marker_or_a_point_on_walkable_ground()
    {
        var f = new Fixture();
        Assert.Equal(DevReason.None, f.Run($"/tp {TestCombat.MonsterSpawnId}").Reason);
        Assert.Equal((2_000, 0), (f.Player.XCm, f.Player.YCm));
        Assert.Equal(FixedPoint.FromCm(2_000), f.Player.SpeedCheckedPosition.X); // the speed check starts there

        Assert.Equal(DevReason.None, f.Run("/tp 300 -400").Reason);
        Assert.Equal((300, -400), (f.Player.XCm, f.Player.YCm));
        Assert.Equal(DevReason.UnknownMarker, f.Run("/tp nowhere").Reason);
        Assert.Equal(DevReason.TargetBlocked, f.Run("/tp 8000000 0").Reason); // off the grid
        Assert.Equal(DevReason.BadArguments, f.Run("/tp 99999999 0").Reason); // beyond the fixed-point range
        Assert.Equal(DevReason.BadArguments, f.Run("/tp a b").Reason);
    }

    [Fact]
    public void Heal_die_and_where_act_on_the_own_character()
    {
        var f = new Fixture();
        var combatant = f.Player.Combatant!;
        long now = f.Harness.World.NowMs;
        combatant.Meters.Set(Shared.Simulation.Stats.ResourceKind.Hp, 1, now);

        f.Run("/heal");
        Assert.Equal(combatant.MaxHp(f.Harness.World.NowMs), combatant.Hp(f.Harness.World.NowMs));

        Assert.Equal("you died", f.Run("/die", ticks: 1).Lines[0]);
        Assert.False(combatant.IsAlive);
        Assert.Equal(DevReason.Dead, f.Run("/heal", ticks: 1).Reason);
        Assert.StartsWith("at ", f.Run("/where", ticks: 1).Lines[0]); // read-only commands still work
    }

    [Fact]
    public void Spawn_places_monsters_up_to_the_DevTools_cap_and_they_leave_once_dead()
    {
        var f = new Fixture(maxSpawn: 2);
        int before = f.Harness.World.Entities.OfType<Monster>().Count();

        Assert.Equal(DevReason.OverLimit, f.Run($"/spawn {TestCombat.MonsterId} 3").Reason);
        Assert.Equal("2 m1.demon spawned", f.Run($"/spawn {TestCombat.MonsterId} 2").Lines[0]);
        var spawned = f.Harness.World.Entities.OfType<Monster>().Where(m => m.SpawnPointId.StartsWith("dev:")).ToList();
        Assert.Equal(2, spawned.Count);
        Assert.Equal(before + 2, f.Harness.World.Entities.OfType<Monster>().Count());

        spawned[0].Combatant!.Die(f.Harness.World.ServerTick);
        f.Harness.Ticks(1);
        Assert.Equal(before + 1, f.Harness.World.Entities.OfType<Monster>().Count());
        Assert.Equal(DevReason.UnknownMonster, f.Run("/spawn m9.ghost").Reason);
    }

    [Fact]
    public void Node_needs_a_node_in_reach()
    {
        var f = new Fixture();
        Assert.Equal(DevReason.NoNode, f.Run("/node deplete").Reason);
        Assert.Equal(DevReason.BadArguments, f.Run("/node grow").Reason);
    }

    [Fact]
    public void A_refusal_is_answered_at_once_and_published()
    {
        var f = new Fixture();
        var answer = f.Run("/fly", ticks: 1);

        Assert.Equal(DevReason.UnknownCommand, answer.Reason);
        var refused = Assert.Single(f.Log.Events);
        Assert.Equal(((byte)DevCommandKind.None, DevReason.UnknownCommand), (refused.Command, refused.Reason));
        Assert.Equal(1, f.Console!.Refused);
    }

    [Fact]
    public void An_answer_for_an_old_connection_is_dropped()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;
        f.Harness.SendChat(Peer, "/give m3.oreT1 4");
        f.Harness.Ticks(2);
        f.Harness.Leave(Peer);
        f.Harness.Ticks(1);

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(3);

        Assert.Empty(f.Answers);
    }
}
