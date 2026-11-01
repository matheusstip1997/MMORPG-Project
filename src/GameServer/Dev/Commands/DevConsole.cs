using System.Globalization;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Jobs;
using GameServer.Persistence;
using GameServer.World;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;

namespace GameServer.Dev.Commands;

/// <summary>
/// The dev console's executor (spec 11.7 dev console, WP-M3-13): runs a player's <c>/</c> lines on the world thread in
/// phase 1, in player id order, against the character's own state, through the writers that own it (rule 4):
/// <see cref="InventoryService"/> for items and gold (reason <see cref="InventoryReason.Admin"/>, source
/// <c>dev-console:account:&lt;id&gt;</c>), <see cref="Progression.ProgressionStore"/> for level, exp, points and kill
/// counts, <see cref="JobService"/> for jobs; the world itself for a position, meters, a death, spawned monsters and nodes.
/// <list type="bullet">
/// <item>Only for accounts the dev login made (<see cref="IDevAccounts"/>); the world has a console only in a DevTools build
/// with <c>MMORPG_DEV_TOOLS</c> on.</item>
/// <item>Every id and number is checked against the tables (the level cap, a stack, the gem slots, the enchant cap, the
/// spawn cap) and only states play can reach are made (user answer 2026-11-01: values only go up). Every path returns a
/// reason; nothing throws into the tick.</item>
/// <item>Each line has its own operation key <c>dev:{run}:{entity}:{connection}:{n}</c>, so the writers apply it once. A
/// command that changes saved state asks for a save at once and is answered only when that save committed (rule 5); one
/// command per character at a time (<see cref="DevReason.DevBusy"/>). A refusal, and a command on volatile state, is
/// answered at once.</item>
/// <item>Every command that ran or was refused is published as <see cref="DevCommandRan"/> (telemetry <c>DevCommand</c>).</item>
/// </list>
/// </summary>
public sealed class DevConsole : IEventConsumer<SessionStarted>
{
    private readonly GameWorld _world;
    private readonly IDevAccounts _accounts;
    private readonly DevTools _settings;
    private readonly string _runId;
    private readonly Dictionary<long, Waiting> _waiting = new();

    internal DevConsole(GameWorld world, IDevAccounts accounts, DevTools settings, string runId)
    {
        _world = world;
        _accounts = accounts;
        _settings = settings;
        _runId = runId;
    }

    /// <summary>
    /// Asks the character's save scheduler for a save at once; false when the character has none (set by the
    /// SessionManager; a world without sessions answers at once).
    /// </summary>
    public Func<long, SaveReason, bool>? RequestSave { get; set; }

    /// <summary>Commands that ran, refused ones excluded.</summary>
    public long Ran { get; private set; }

    public long Refused { get; private set; }

    /// <summary>Characters whose command waits for its save.</summary>
    public int WaitingCount => _waiting.Count;

    public void Handle(in EventEnvelope envelope, in SessionStarted payload) => _accounts.Prefetch(payload.AccountId);

    /// <summary>Phase 1: one checked chat line starting with <c>/</c> from <paramref name="player"/>.</summary>
    internal void Submit(Player player, string line)
    {
        uint n = ++player.DevLines;
        string opKey = string.Create(CultureInfo.InvariantCulture, $"dev:{_runId}:{player.Id}:{player.Connection}:{n}");
        var status = _accounts.Status(player.AccountId);
        if (status != DevAccountStatus.Dev)
        {
            Answer(player, DevCommandKind.None, new Outcome(DevReason.NotDevAccount, status == DevAccountStatus.Checking
                ? "checking your account: send the command again"
                : "this account was not made by the dev login"), opKey);
            return;
        }
        var parsed = DevCommandParser.TryParse(line, out var command, out var detail);
        if (parsed != DevReason.None)
        {
            Answer(player, DevCommandKind.None, new Outcome(parsed, detail), opKey);
            return;
        }
        var kind = command!.Kind;
        if (_waiting.ContainsKey(player.CharacterId))
        {
            Answer(player, kind, new Outcome(DevReason.DevBusy, "the last command is still saving"), opKey);
            return;
        }
        if (player.Combatant is { IsAlive: false } && kind is not (DevCommandKind.Help or DevCommandKind.Where))
        {
            Answer(player, kind, new Outcome(DevReason.Dead), opKey);
            return;
        }

        Outcome outcome;
        try
        {
            outcome = Run(player, command, opKey);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            // A state the checks above did not foresee (a character half attached): refused, never thrown into the tick.
            outcome = new Outcome(DevReason.DevUnavailable, DevCommandParser.Clip(e.Message));
        }
        if (outcome.Reason == DevReason.None && outcome.Saves
            && RequestSave != null && RequestSave(player.CharacterId, SaveReason.DevCommand))
        {
            _waiting.Add(player.CharacterId, new Waiting(Result(outcome), player.Connection, kind, outcome, opKey));
            return;
        }
        Answer(player, kind, outcome, opKey);
    }

    /// <summary>A save was issued: it carries the waiting command's change (applied in phase 1, before this save was taken).</summary>
    internal void OnSaveIssued(long characterId, Guid saveId)
    {
        if (_waiting.TryGetValue(characterId, out var waiting) && waiting.SaveId == null)
            waiting.SaveId = saveId;
    }

    /// <summary>A save committed: the command it carries is answered now (rule 5).</summary>
    internal void OnSaveCommitted(long characterId, Guid saveId)
    {
        if (!_waiting.TryGetValue(characterId, out var waiting) || waiting.SaveId != saveId)
            return;
        _waiting.Remove(characterId);
        var player = _world.PlayerOf(characterId);
        Publish(player, characterId, waiting.Kind, waiting.Outcome, waiting.OpKey);
        Ran++;
        if (player != null && player.Connection == waiting.Connection)
            player.DevAnswers.Add(waiting.Answer);
    }

    /// <summary>The character left the world: a waiting answer is dropped (its save committed or the character faulted).</summary>
    internal void Detach(long characterId) => _waiting.Remove(characterId);

    private Outcome Run(Player player, DevCommand command, string opKey) => command.Kind switch
    {
        DevCommandKind.Help => new Outcome(DevReason.None, DevCommandParser.HelpLines(command.Arg(0))),
        DevCommandKind.Give => Give(player, command, opKey),
        DevCommandKind.Take => Take(player, command, opKey),
        DevCommandKind.ClearBag => ClearBag(player, opKey),
        DevCommandKind.Gold => Gold(player, command, opKey),
        DevCommandKind.Level => Level(player, command),
        DevCommandKind.Exp => Exp(player, command),
        DevCommandKind.Points => Points(player, command),
        DevCommandKind.Job => Job(player, command, opKey),
        DevCommandKind.JobLevel => JobLevel(player, command),
        DevCommandKind.Kills => Kills(player, command),
        DevCommandKind.Teleport => Teleport(player, command),
        DevCommandKind.Heal => Heal(player),
        DevCommandKind.Die => Die(player),
        DevCommandKind.Spawn => Spawn(player, command),
        DevCommandKind.Node => Node(player, command),
        DevCommandKind.Where => Where(player),
        _ => new Outcome(DevReason.UnknownCommand),
    };

    private string Source(Player player) => string.Create(CultureInfo.InvariantCulture, $"dev-console:account:{player.AccountId}");

    private Outcome Give(Player player, DevCommand command, string opKey)
    {
        if (_world.Inventory is not { } inventory || !inventory.IsAttached(player.CharacterId))
            return new Outcome(DevReason.DevUnavailable, "no inventory");
        var rules = inventory.Rules;
        if (!rules.TryGetItem(command.Arg(0), out var item))
            return new Outcome(DevReason.UnknownItem, $"no ItemDef '{DevCommandParser.Clip(command.Arg(0))}'");
        long count = 1;
        if (command.Args.Count > 1 && !DevCommandParser.TryInt(command.Arg(1), 1, int.MaxValue, out count))
            return new Outcome(DevReason.BadArguments, "the count is a whole number from 1");
        byte rarity = 0;
        if (command.Option("rarity") is { } rarityId && !rules.TryGetRarityTier(rarityId, out rarity))
            return new Outcome(DevReason.UnknownItem, $"no RarityDef '{DevCommandParser.Clip(rarityId)}': {string.Join(", ", rules.RarityIds)}");

        ItemProperties? properties = null;
        var enchant = command.Option("enchant");
        var gems = command.Option("gems");
        if (enchant != null || gems != null)
        {
            if (!ItemCategories.IsInstance(item.Category))
                return new Outcome(DevReason.BadArguments, $"{item.Id} is not gear: enchant and gems are for gear");
            properties = ItemProperties.New(player.CharacterId);
            if (enchant != null)
            {
                if (!DevCommandParser.TryInt(enchant, 0, EnchantLimits.MaxLevel, out long level))
                    return new Outcome(DevReason.OverLimit, $"enchant is 0 to {EnchantLimits.MaxLevel}");
                if (level > 0 && item.JobId.Length == 0)
                    return new Outcome(DevReason.OverLimit, $"{item.Id} cannot be enchanted (no ItemDef.jobId)");
                properties.Enchant = (int)level;
            }
            if (gems != null)
            {
                foreach (var gem in gems.Split(','))
                {
                    if (!rules.TryGetGemBonus(gem, out _))
                        return new Outcome(DevReason.UnknownItem, $"no GemDef '{DevCommandParser.Clip(gem)}'");
                    properties.Gems.Add(gem);
                }
                if (properties.Gems.Count > item.GemSlots)
                    return new Outcome(DevReason.OverLimit, $"{item.Id} has {item.GemSlots} gem slot(s)");
            }
            if (!rules.Represents(properties))
                return new Outcome(DevReason.OverLimit, "those properties are not ones the tables know");
        }

        var added = inventory.TryAdd(player.CharacterId, item, rarity, (int)count, opKey, InventoryReason.Admin, Source(player), properties);
        if (added.Added == 0)
            return new Outcome(DevReason.BagFull, $"no room for {item.Id}");
        var line = added.Added < count ? $"+{added.Added} {item.Id} (only {added.Added} of {count} fit)" : $"+{added.Added} {item.Id}";
        return new Outcome(DevReason.None, line) { Saves = true, Subject = item.NetId, Value = added.Added };
    }

    private Outcome Take(Player player, DevCommand command, string opKey)
    {
        if (_world.Inventory is not { } inventory || !inventory.IsAttached(player.CharacterId))
            return new Outcome(DevReason.DevUnavailable, "no inventory");
        if (!inventory.Rules.TryGetItem(command.Arg(0), out var item))
            return new Outcome(DevReason.UnknownItem, $"no ItemDef '{DevCommandParser.Clip(command.Arg(0))}'");
        long count = 1;
        if (command.Args.Count > 1 && !DevCommandParser.TryInt(command.Arg(1), 1, int.MaxValue, out count))
            return new Outcome(DevReason.BadArguments, "the count is a whole number from 1");
        if (inventory.Rules.ReceiverOf(item.Category) is not { } container
            || !inventory.CurrentInventory(player.CharacterId).TryIndex((byte)container.Code, out int index))
            return new Outcome(DevReason.NotInBag, $"no bag holds {item.Id}");
        var bag = inventory.CurrentInventory(player.CharacterId).BagAt(index);
        long held = 0;
        for (int slot = 0; slot < bag.SlotCount; slot++)
        {
            if (bag[slot].ItemNetId == item.NetId)
                held += bag[slot].Count;
        }
        if (held < count)
            return new Outcome(DevReason.NotInBag, $"the bag holds {held} {item.Id}");
        long left = count;
        int part = 0;
        for (int slot = 0; slot < bag.SlotCount && left > 0; slot++)
        {
            var stack = bag[slot];
            if (stack.IsEmpty || stack.ItemNetId != item.NetId)
                continue;
            int take = (int)Math.Min(left, stack.Count);
            if (inventory.TryRemove(player.CharacterId, container.Code, slot, take, $"{opKey}:{part++}", InventoryReason.Admin, Source(player)))
                left -= take;
        }
        long taken = count - left;
        if (taken == 0)
            return new Outcome(DevReason.NotInBag, $"{item.Id} is reserved (a forge or gem operation holds it)");
        return new Outcome(DevReason.None, $"-{taken} {item.Id}" + (left > 0 ? $" ({left} reserved)" : "")) { Saves = true, Subject = item.NetId, Value = taken };
    }

    private Outcome ClearBag(Player player, string opKey)
    {
        if (_world.Inventory is not { } inventory || !inventory.IsAttached(player.CharacterId))
            return new Outcome(DevReason.DevUnavailable, "no inventory");
        var bag = inventory.Current(player.CharacterId);
        int code = inventory.Rules.NormalBag.Code;
        int stacks = 0, kept = 0;
        for (int slot = 0; slot < bag.SlotCount; slot++)
        {
            var stack = bag[slot];
            if (stack.IsEmpty)
                continue;
            if (inventory.TryRemove(player.CharacterId, code, slot, stack.Count, $"{opKey}:{slot}", InventoryReason.Admin, Source(player)))
                stacks++;
            else
                kept++;
        }
        var line = kept > 0 ? $"{stacks} stack(s) removed, {kept} reserved stack(s) kept" : $"{stacks} stack(s) removed";
        return new Outcome(DevReason.None, line) { Saves = stacks > 0, Value = stacks };
    }

    private Outcome Gold(Player player, DevCommand command, string opKey)
    {
        if (_world.Inventory is not { } inventory || !inventory.HasGold(player.CharacterId))
            return new Outcome(DevReason.DevUnavailable, "no gold here");
        if (!DevCommandParser.TryInt(command.Arg(0), 0, long.MaxValue, out long amount))
            return new Outcome(DevReason.BadArguments, "the amount is a whole number from 0");
        if (amount > inventory.MaxGold)
            return new Outcome(DevReason.OverLimit, $"EconomySettings.maxGold is {inventory.MaxGold}");
        long current = inventory.Gold(player.CharacterId);
        if (amount == current)
            return new Outcome(DevReason.None, $"gold is {current} already") { Value = amount };
        var result = amount > current
            ? inventory.TryAddGold(player.CharacterId, amount - current, opKey, InventoryReason.Admin, Source(player))
            : inventory.TryRemoveGold(player.CharacterId, current - amount, opKey, InventoryReason.Admin, Source(player));
        if (!result.Applied)
            return new Outcome(DevReason.OverLimit, $"EconomySettings.maxGold is {inventory.MaxGold}");
        return new Outcome(DevReason.None, $"gold {current} -> {amount}") { Saves = true, Value = amount };
    }

    private Outcome Level(Player player, DevCommand command)
    {
        if (_world.Progression is not { } progression || !progression.IsAttached(player.CharacterId))
            return new Outcome(DevReason.DevUnavailable, "no progression");
        int max = progression.Rules.Curves.MaxLevel;
        if (!DevCommandParser.TryInt(command.Arg(0), 1, int.MaxValue, out long level))
            return new Outcome(DevReason.BadArguments, "the level is a whole number from 1");
        if (level > max)
            return new Outcome(DevReason.OverLimit, $"ProgressionSettings.maxLevel is {max}");
        var before = progression.Snapshot(player.CharacterId);
        if (level < before.Level)
            return new Outcome(DevReason.LevelDown, $"you are level {before.Level}");
        if (level == before.Level)
            return new Outcome(DevReason.None, $"you are level {level} already") { Value = level };
        progression.TryGetAttributes(player.CharacterId, out var attributesBefore);
        progression.AddExp(player.CharacterId, progression.ExpToReach(player.CharacterId, (int)level), out _);
        var after = progression.Snapshot(player.CharacterId);
        progression.TryGetAttributes(player.CharacterId, out var attributesAfter);
        return new Outcome(DevReason.None, $"level {before.Level} -> {after.Level}, +{after.SkillPoints - before.SkillPoints} skill points, "
            + $"+{attributesAfter.Unspent - attributesBefore.Unspent} attribute points") { Saves = true, Value = after.Level };
    }

    private Outcome Exp(Player player, DevCommand command)
    {
        if (_world.Progression is not { } progression || !progression.IsAttached(player.CharacterId))
            return new Outcome(DevReason.DevUnavailable, "no progression");
        if (!DevCommandParser.TryInt(command.Arg(0), 1, int.MaxValue, out long amount))
            return new Outcome(DevReason.BadArguments, "the amount is a whole number from 1");
        var before = progression.Snapshot(player.CharacterId);
        if (before.Level >= progression.Rules.Curves.MaxLevel)
            return new Outcome(DevReason.OverLimit, $"you are at ProgressionSettings.maxLevel {before.Level}");
        progression.AddExp(player.CharacterId, amount, out _);
        var after = progression.Snapshot(player.CharacterId);
        return new Outcome(DevReason.None, $"+{amount} exp: level {after.Level}, exp {after.Exp}") { Saves = true, Value = amount };
    }

    private Outcome Points(Player player, DevCommand command)
    {
        if (_world.Progression is not { } progression || !progression.IsAttached(player.CharacterId))
            return new Outcome(DevReason.DevUnavailable, "no progression");
        bool skill = string.Equals(command.Arg(0), "skill", StringComparison.OrdinalIgnoreCase);
        if (!skill && !string.Equals(command.Arg(0), "attr", StringComparison.OrdinalIgnoreCase))
            return new Outcome(DevReason.BadArguments, "usage: " + command.Spec.Usage);
        long have = skill ? progression.Snapshot(player.CharacterId).SkillPoints : progression.WorkingAttributes(player.CharacterId).Unspent;
        if (!DevCommandParser.TryInt(command.Arg(1), 1, int.MaxValue - Math.Max(0, have), out long points))
            return new Outcome(DevReason.BadArguments, "the points are a whole number from 1");
        progression.DevGrantPoints(player.CharacterId, skill ? (int)points : 0, skill ? 0 : (int)points);
        return new Outcome(DevReason.None, $"+{points} {(skill ? "skill" : "attribute")} points") { Saves = true, Value = points };
    }

    private Outcome Job(Player player, DevCommand command, string opKey)
    {
        if (_world.Jobs is not { } jobs || !jobs.IsAttached(player.CharacterId))
            return new Outcome(DevReason.DevUnavailable, "no jobs");
        if (jobs.Rules.Find(command.Arg(0)) is not { } job)
            return new Outcome(DevReason.UnknownJob, $"no JobDef '{DevCommandParser.Clip(command.Arg(0))}'");
        JobSlot slot;
        if (command.Args.Count > 1)
        {
            if (string.Equals(command.Arg(1), "main", StringComparison.OrdinalIgnoreCase))
                slot = JobSlot.Main;
            else if (string.Equals(command.Arg(1), "secondary", StringComparison.OrdinalIgnoreCase))
                slot = JobSlot.Secondary;
            else
                return new Outcome(DevReason.BadArguments, "usage: " + command.Spec.Usage);
        }
        else
        {
            slot = jobs.JobIn(player.CharacterId, JobSlot.Main) == null ? JobSlot.Main : JobSlot.Secondary;
        }
        // The pick's own rules (main before secondary, the level, the starter items' room); only the job master's reach and
        // the combat check are skipped.
        var ask = new JobAsk(player.DevLines, (byte)job.NetId, slot, opKey, _world.Progression?.GetLevel(player.CharacterId) ?? 1,
            Blocked: false, InReach: true);
        if (jobs.Handle(player.CharacterId, ask, player.Connection) is { } refused)
            return new Outcome(DevReason.JobRefused, $"the pick was refused: {refused.Reason}");
        return new Outcome(DevReason.None, $"{job.Id} is your {(slot == JobSlot.Main ? "main" : "secondary")} job")
            { Saves = true, Subject = job.NetId, Value = (long)slot };
    }

    private Outcome JobLevel(Player player, DevCommand command)
    {
        if (_world.Jobs is not { } jobs || !jobs.IsAttached(player.CharacterId))
            return new Outcome(DevReason.DevUnavailable, "no jobs");
        if (jobs.Rules.Find(command.Arg(0)) is not { } job)
            return new Outcome(DevReason.UnknownJob, $"no JobDef '{DevCommandParser.Clip(command.Arg(0))}'");
        if (!DevCommandParser.TryInt(command.Arg(1), 1, int.MaxValue, out long level))
            return new Outcome(DevReason.BadArguments, "the level is a whole number from 1");
        if (level > jobs.Rules.MaxLevel)
            return new Outcome(DevReason.OverLimit, $"JobSettings.maxLevel is {jobs.Rules.MaxLevel}");
        int current = jobs.JobLevel(player.CharacterId, job.Id);
        if (current == 0)
            return new Outcome(DevReason.UnknownJob, $"you do not hold {job.Id} (or its pick is still saving)");
        if (level < current)
            return new Outcome(DevReason.LevelDown, $"{job.Id} is level {current}");
        if (level == current)
            return new Outcome(DevReason.None, $"{job.Id} is level {current} already") { Subject = job.NetId, Value = level };
        int reached = jobs.DevRaiseLevel(player.CharacterId, job.Id, (int)level, player.RegionIndex);
        var line = reached < level
            ? $"{job.Id} level {current} -> {reached} (a secondary job stays at or below the main's level)"
            : $"{job.Id} level {current} -> {reached}";
        return new Outcome(DevReason.None, line) { Saves = true, Subject = job.NetId, Value = reached };
    }

    private Outcome Kills(Player player, DevCommand command)
    {
        if (_world.Progression is not { } progression || !progression.IsAttached(player.CharacterId) || _world.Combat is not { } combat)
            return new Outcome(DevReason.DevUnavailable, "no kill counts");
        if (!combat.TryGetMonster(command.Arg(0), out var monster))
            return new Outcome(DevReason.UnknownMonster, $"no MonsterDef '{DevCommandParser.Clip(command.Arg(0))}'");
        if (!DevCommandParser.TryInt(command.Arg(1), 0, int.MaxValue, out long count))
            return new Outcome(DevReason.BadArguments, "the count is a whole number from 0");
        long have = progression.KillCount(player.CharacterId, monster.Id);
        if (count < have)
            return new Outcome(DevReason.ValueDown, $"you have {have} {monster.Id} kills");
        if (count == have)
            return new Outcome(DevReason.None, $"{monster.Id} kills are {have} already") { Subject = monster.NetId, Value = count };
        progression.DevRaiseKillCount(player.CharacterId, monster.Id, count, monster.Boss);
        _world.DevBystial?.CatchUp(player.CharacterId); // the book's cards to the tiers the count reaches (R-14.2-01)
        return new Outcome(DevReason.None, $"{monster.Id} kills {have} -> {count}") { Saves = true, Subject = monster.NetId, Value = count };
    }

    private Outcome Teleport(Player player, DevCommand command)
    {
        FixVec2 to;
        string where;
        if (command.Args.Count == 1)
        {
            if (!_world.Map.TryGetMarker(command.Arg(0), out var marker))
                return new Outcome(DevReason.UnknownMarker, $"no marker '{DevCommandParser.Clip(command.Arg(0))}'");
            to = marker.Position;
            where = marker.Id;
        }
        else
        {
            const long limit = int.MaxValue / FixedPoint.RawPerCm;
            if (!DevCommandParser.TryInt(command.Arg(0), -limit, limit, out long x) || !DevCommandParser.TryInt(command.Arg(1), -limit, limit, out long y))
                return new Outcome(DevReason.BadArguments, "x and y are whole centimetres");
            to = new FixVec2(FixedPoint.FromCm((int)x), FixedPoint.FromCm((int)y));
            where = $"{x} {y}";
        }
        var reason = _world.DevTeleport(player, to);
        if (reason != DevReason.None)
            return new Outcome(reason, $"{where}: not walkable, or a region above your level");
        return new Outcome(DevReason.None, $"at {where} ({player.XCm} {player.YCm} cm)");
    }

    private Outcome Heal(Player player)
    {
        if (player.Combatant == null)
            return new Outcome(DevReason.DevUnavailable, "no combatant");
        _world.DevHeal(player);
        return new Outcome(DevReason.None, "HP, MP, stamina and poise full");
    }

    private Outcome Die(Player player)
    {
        if (player.Combatant == null)
            return new Outcome(DevReason.DevUnavailable, "no combatant");
        _world.DevDie(player);
        return new Outcome(DevReason.None, "you died");
    }

    private Outcome Spawn(Player player, DevCommand command)
    {
        if (_world.Combat is not { } combat)
            return new Outcome(DevReason.DevUnavailable, "no monsters");
        if (!combat.TryGetMonster(command.Arg(0), out var monster))
            return new Outcome(DevReason.UnknownMonster, $"no MonsterDef '{DevCommandParser.Clip(command.Arg(0))}'");
        long count = 1;
        if (command.Args.Count > 1 && !DevCommandParser.TryInt(command.Arg(1), 1, int.MaxValue, out count))
            return new Outcome(DevReason.BadArguments, "the count is a whole number from 1");
        if (count > _settings.MaxSpawnPerCommand)
            return new Outcome(DevReason.OverLimit, $"DevTools.maxSpawnPerCommand is {_settings.MaxSpawnPerCommand}");
        int spawned = _world.DevSpawn(player, monster, (int)count);
        return new Outcome(DevReason.None, $"{spawned} {monster.Id} spawned") { Subject = monster.NetId, Value = spawned };
    }

    private Outcome Node(Player player, DevCommand command)
    {
        bool deplete = string.Equals(command.Arg(0), "deplete", StringComparison.OrdinalIgnoreCase);
        if (!deplete && !string.Equals(command.Arg(0), "respawn", StringComparison.OrdinalIgnoreCase))
            return new Outcome(DevReason.BadArguments, "usage: " + command.Spec.Usage);
        if (_world.DevNearestNode(player) is not { } node)
            return new Outcome(DevReason.NoNode, "no resource node within your interest radius");
        bool changed = deplete ? _world.DevDepleteNode(node) : _world.DevRespawnNode(node);
        string state = deplete ? "depleted" : "available";
        return new Outcome(DevReason.None, changed ? $"{node.MarkerId} ({node.Def.Id}) {state}" : $"{node.MarkerId} ({node.Def.Id}) is {state} already")
            { Subject = node.Def.NetId };
    }

    private Outcome Where(Player player)
    {
        var region = _world.Map.RegionByIndex(player.RegionIndex)?.Id ?? player.RegionIndex.ToString(CultureInfo.InvariantCulture);
        var marker = _world.DevNearestMarker(player);
        return new Outcome(DevReason.None, new List<string>
        {
            $"at {player.XCm} {player.YCm} cm, region {region}",
            marker == null ? "no marker" : $"nearest marker {marker.Id}",
        });
    }

    private void Answer(Player player, DevCommandKind kind, in Outcome outcome, string opKey)
    {
        Publish(player, player.CharacterId, kind, outcome, opKey);
        if (outcome.Reason == DevReason.None)
            Ran++;
        else
            Refused++;
        player.DevAnswers.Add(Result(outcome));
    }

    private void Publish(Player? player, long characterId, DevCommandKind kind, in Outcome outcome, string opKey) =>
        _world.DevEvents?.Publish(new DevCommandRan(characterId, player?.AccountId ?? 0, player?.Id ?? 0, (byte)kind, outcome.Subject,
            outcome.Value, outcome.Reason, opKey), characterId, player?.RegionIndex ?? 0);

    /// <summary>The answer: the reason and the lines, each cut to the protocol caps.</summary>
    private static DevCommandResult Result(in Outcome outcome)
    {
        int count = Math.Min(outcome.Lines.Count, DevConsoleLimits.MaxLines);
        var lines = new string[count];
        for (int i = 0; i < count; i++)
        {
            var line = outcome.Lines[i];
            lines[i] = line.Length <= DevConsoleLimits.MaxLineChars ? line : line.Substring(0, DevConsoleLimits.MaxLineChars);
        }
        return new DevCommandResult { Reason = outcome.Reason, Lines = lines };
    }

    /// <summary>What a command did: its reason and lines, whether it waits for a save, and its telemetry values.</summary>
    private readonly record struct Outcome(DevReason Reason, IReadOnlyList<string> Lines)
    {
        public Outcome(DevReason reason) : this(reason, Array.Empty<string>())
        {
        }

        public Outcome(DevReason reason, string line) : this(reason, new[] { line })
        {
        }

        /// <summary>The command changed saved state: answered once its save committed.</summary>
        public bool Saves { get; init; }

        public int Subject { get; init; }

        public long Value { get; init; }
    }

    private sealed class Waiting(DevCommandResult answer, int connection, DevCommandKind kind, DevConsole.Outcome outcome, string opKey)
    {
        public DevCommandResult Answer { get; } = answer;
        public int Connection { get; } = connection;
        public DevCommandKind Kind { get; } = kind;
        public Outcome Outcome { get; } = outcome;
        public string OpKey { get; } = opKey;

        /// <summary>The save that carries the change, once issued.</summary>
        public Guid? SaveId { get; set; }
    }
}
