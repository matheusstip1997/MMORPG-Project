using System.Globalization;
using GameServer.Events;
using GameServer.Items;
using GameServer.Jobs;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;
using Shared.Util;

namespace GameServer.Crafting;

/// <summary>What one forge start asks for, as the world reads it in phase 1 (<see cref="InReach"/>: the station gate passed).</summary>
public readonly record struct ForgeStartAsk(uint RequestId, uint StationEntityId, NpcInteraction Station, ForgeKind Kind, int DefNetId,
    IReadOnlyList<InventoryLocation> Inputs, bool InReach, bool Blocked, int Connection);

/// <summary>An operation whose save committed: its audit row, the answer, the session's definition and its connection.</summary>
public sealed record CommittedForgeOp(CraftOpRecord Record, ForgeResult Answer, ForgeDefinition Definition, int Connection);

/// <summary>
/// The forge engine of every character held by this world (spec 5.3 R-5.3-01..03, 5.2 R-5.2-04; WP-M3-6), on the world
/// thread: one <see cref="ForgeSession"/> per character, from the start at a station to the commit of its result.
/// <para>
/// <b>Start</b> checks the definition (craftable, station, job and job level through <see cref="IJobQuery"/>), the
/// conversion daily cap, the mold in the Safe bag and every input against its slot (<see cref="ForgeRules"/>), then
/// reserves the inputs and the mold (<see cref="InventoryService.Reserve"/>, memory only: a crash loses nothing).
/// <b>Actions</b> append to the session, up to <c>ForgeSettings.maxActions</c>. <b>Finish</b> draws every random number from
/// the session's seed (<see cref="ForgeRolls"/>), computes the quality, rarity and stats snapshot (<see cref="QualityCalc"/>,
/// <see cref="ForgeRules.CraftedStats"/>), and in one class 2 operation consumes the reservation
/// (<see cref="InventoryService.TryRemoveReserved"/>) and creates the output (<see cref="InventoryService.TryAdd"/> with its
/// properties: crafted stats, quality, crafter, taint) or, for a failed conversion, nothing (a sink). It asks for a save at
/// once; the answer and the events come only after the commit (rule 5), so a crash before it leaves the inputs untouched
/// and shows nothing, and nothing can be rerolled. <b>Cancel</b> (a request, a disconnect, leaving the station or combat)
/// releases both reservations; nothing is consumed.
/// </para>
/// <para>
/// <see cref="InventoryService"/> stays the only item writer (rule 4): the forge reserves, consumes and creates through it.
/// </para>
/// </summary>
public sealed class ForgeService : ICraftBatchSource, IJobActivityGate
{
    /// <summary>The <c>Pcg32Rng</c> stream the session seeds come from (the world seed is logged by the host).</summary>
    public const ulong SeedStream = 0x46534545UL; // "FSEE"

    /// <summary>Answers to finishes remembered per character, so a repeated request gets its first answer.</summary>
    private const int SeenAnswers = 16;

    private readonly ForgeRules _rules;
    private readonly InventoryService _inventory;
    private readonly IJobQuery? _jobs;
    private readonly IJobBonusQuery _bonuses;
    private readonly IForgeChallenge _challenge;
    private readonly Func<long> _nowMs;
    private readonly Pcg32Rng _seeds;
    private readonly Dictionary<long, CharacterForge> _characters = new();
    private readonly List<ReserveItem> _reserve = new();

    public ForgeService(ForgeRules rules, InventoryService inventory, IJobQuery? jobs, IJobBonusQuery? bonuses, IForgeChallenge? challenge,
        Func<long> nowMs, ulong worldSeed)
    {
        _rules = rules;
        _inventory = inventory;
        _jobs = jobs;
        _bonuses = bonuses ?? NoJobBonuses.Instance;
        _challenge = challenge ?? NoForgeChallenge.Instance;
        _nowMs = nowMs;
        _seeds = new Pcg32Rng(worldSeed, SeedStream);
    }

    public ForgeRules Rules => _rules;

    /// <summary>Marks a character class 2 dirty with its save scheduler (set by the SessionManager).</summary>
    public Action<long>? MarkClass2 { get; set; }

    /// <summary>Asks for a save at the next tick (every forge result, R-7.1-05; set by the SessionManager).</summary>
    public Action<long, SaveReason>? RequestSave { get; set; }

    /// <summary>A save committed an operation of this character (set by the world: the events and the answer).</summary>
    public Action<long, CommittedForgeOp>? OpCommitted { get; set; }

    public int Count => _characters.Count;

    public bool IsAttached(long characterId) => _characters.ContainsKey(characterId);

    /// <summary>Starts holding a character, with its committed conversions of the last days (the daily cap).</summary>
    public void Attach(long characterId, CraftRecord record)
    {
        var c = new CharacterForge();
        foreach (var conversion in record.Conversions)
            c.CountConversion(ForgeRolls.UtcDay(conversion.AtUtcMs), conversion.ToTier);
        _characters[characterId] = c;
    }

    /// <summary>The character left the world: its session ends (the reservations end with the inventory's detach).</summary>
    public void Detach(long characterId)
    {
        Abort(characterId);
        _characters.Remove(characterId);
    }

    /// <summary>The character's session, or null.</summary>
    public ForgeSession? Session(long characterId) =>
        _characters.TryGetValue(characterId, out var c) ? c.Session : null;

    /// <summary>A session runs or waits for its save (the job tree refuses a respec then, job-tree design; WP-M3-7).</summary>
    public bool HasSession(long characterId) => Session(characterId) != null;

    /// <summary>The job tree's respec gate (<see cref="IJobActivityGate"/>, WP-M3-7): a forge session keeps the bonuses it read.</summary>
    public bool InSession(long characterId) => HasSession(characterId);

    /// <summary>A session runs (not finishing): the world checks its station and combat each tick.</summary>
    public bool IsRunning(long characterId) => Session(characterId)?.State == ForgeSessionState.Running;

    /// <summary>Conversions into <paramref name="toTier"/> this UTC day (both outcomes count).</summary>
    public int ConversionsToday(long characterId, int toTier) =>
        _characters.TryGetValue(characterId, out var c) ? c.Conversions(ForgeRolls.UtcDay(_nowMs()), toTier) : 0;

    /// <summary>Operations applied and waiting for their save.</summary>
    public int WaitingCount(long characterId) =>
        _characters.TryGetValue(characterId, out var c) ? c.Pending.Count + (c.InFlight?.Ops.Count ?? 0) : 0;

    /// <summary>One start (phase 1): <see cref="ForgeReason.None"/> when the session runs with its inputs reserved.</summary>
    public ForgeReason Start(long characterId, in ForgeStartAsk ask)
    {
        if (!_characters.TryGetValue(characterId, out var c) || !_inventory.IsAttached(characterId))
            return ForgeReason.ForgeUnavailable;
        if (c.Session != null)
            return ForgeReason.SessionActive;
        if (!ask.InReach)
            return ForgeReason.StationOutOfReach;
        if (ask.Blocked)
            return ForgeReason.InCombat;
        if (!_rules.TryGet(ask.Kind, ask.DefNetId, out var definition))
            return ForgeReason.UnknownRecipe;
        int jobLevel = _jobs?.JobLevel(characterId, definition.JobId) ?? 0;
        var reason = ForgeRules.CheckDefinition(definition, ask.Station, jobLevel);
        if (reason != ForgeReason.None)
            return reason;
        if (ForgeRules.ConversionTier(definition) is int toTier and > 0
            && (_rules.Odds(toTier) is not { } odds || c.Conversions(ForgeRolls.UtcDay(_nowMs()), toTier) >= odds.DailyCap))
            return ForgeReason.ConversionCapReached;
        // Spec 5.3 job-tree decision: making a tier 2 or 3 mold needs the Master Molds node (using one needs only the level).
        if (definition.RecipeType == RecipeType.Mold && definition.Tier >= 2
            && !_bonuses.MoldTierUnlocked(characterId, definition.JobId, definition.Tier))
            return ForgeReason.MoldTierLocked;
        if (ask.Inputs.Count != definition.Inputs.Count)
            return ForgeReason.SlotMaterialInvalid;

        InventoryLocation? mold = null;
        if (definition.MoldItemId.Length > 0)
        {
            mold = FindMold(characterId, definition.MoldItemId, out bool moldReserved);
            if (mold == null)
                return moldReserved ? ForgeReason.ItemReserved : ForgeReason.MoldRequired;
        }

        var inventory = _inventory.CurrentInventory(characterId);
        var items = new ItemDef[ask.Inputs.Count];
        var rarities = new byte[ask.Inputs.Count];
        var stolen = new bool[ask.Inputs.Count];
        byte normal = (byte)_inventory.Rules.NormalBag.Code;
        _reserve.Clear();
        for (int i = 0; i < ask.Inputs.Count; i++)
        {
            var at = ask.Inputs[i];
            if (at.Container != normal || !inventory.TryIndex(at.Container, out int index) || at.Slot < 0
                || at.Slot >= inventory.BagAt(index).SlotCount)
                return ForgeReason.SlotMaterialInvalid;
            var stack = inventory.BagAt(index)[at.Slot];
            var item = stack.IsEmpty ? null : _rules.Item(stack.ItemNetId);
            reason = ForgeRules.CheckInput(definition, i, item, FreeUnits(characterId, at, ask.Inputs, i, definition));
            if (reason != ForgeReason.None)
                return reason;
            items[i] = item!;
            rarities[i] = stack.Rarity;
            stolen[i] = stack.Stolen;
            _reserve.Add(new ReserveItem(at, definition.Inputs[i].Count));
        }

        string key = ReservationKey(characterId, NextSeed(out ulong seed));
        var reserved = _inventory.Reserve(characterId, key, _reserve);
        if (reserved != ReserveResult.Ok)
            return reserved == ReserveResult.ItemReserved ? ForgeReason.ItemReserved : ForgeReason.MaterialsMissing;
        var session = new ForgeSession(characterId, definition, ask.StationEntityId, ask.Station, seed, key,
            ask.Inputs.ToArray(), items, rarities, stolen, mold, ask.Connection)
        {
            State = ForgeSessionState.MaterialsReserved,
            // The job tree bonuses, read once at the start: a running session keeps them (job-tree decision 10).
            QualityBonus = definition.RollsQuality ? Math.Clamp(_bonuses.Get(characterId, definition.JobId, JobBonusKind.QualityBonus, definition.Activity), 0, _rules.Caps.JobTree) : 0,
            ExtraActions = definition.Actions.Count > 0 ? Math.Max(0, _bonuses.Get(characterId, definition.JobId, JobBonusKind.ActionCount, JobActivity.Craft)) : 0,
            ConversionBonus = definition.RecipeType == RecipeType.Convert ? Math.Max(0, _bonuses.Get(characterId, definition.JobId, JobBonusKind.ConversionOddsPercent, JobActivity.Convert)) : 0,
            KeepInputsPercent = definition.RecipeType == RecipeType.Convert ? Math.Clamp(_bonuses.Get(characterId, definition.JobId, JobBonusKind.KeepInputsPercent, JobActivity.Convert), 0, 100) : 0,
        };
        if (mold is { } moldAt && _inventory.Reserve(characterId, session.MoldKey, new[] { new ReserveItem(moldAt, 1) }) != ReserveResult.Ok)
        {
            _inventory.Release(characterId, key);
            return ForgeReason.ItemReserved;
        }
        session.State = ForgeSessionState.Running;
        c.Session = session;
        return ForgeReason.None;
    }

    /// <summary>One forge action (phase 1).</summary>
    public ForgeReason Action(long characterId, int actionNetId)
    {
        if (Session(characterId) is not { State: ForgeSessionState.Running } session)
            return ForgeReason.NoSession;
        var reason = _rules.CheckAction(session.Definition, actionNetId, session.Actions.Count, session.ExtraActions);
        if (reason == ForgeReason.None)
            session.Actions.Add(actionNetId);
        return reason;
    }

    /// <summary>A cancel request (phase 1): the reservations are released, nothing is consumed.</summary>
    public ForgeReason Cancel(long characterId) => Abort(characterId) ? ForgeReason.None : ForgeReason.NoSession;

    /// <summary>
    /// Ends a running session without a result (cancel, disconnect, the station left, combat): both reservations are
    /// released. False when no session runs; a finishing session is never aborted (its result is applied already).
    /// </summary>
    public bool Abort(long characterId)
    {
        if (!_characters.TryGetValue(characterId, out var c) || c.Session is not { } session
            || session.State == ForgeSessionState.Finishing)
            return false;
        _inventory.Release(characterId, session.ReservationKey);
        _inventory.Release(characterId, session.MoldKey);
        session.State = ForgeSessionState.Cancelled;
        c.Session = null;
        return true;
    }

    /// <summary>
    /// One finish (phase 1). Returns the answer to send now (a refusal, or the first answer of a repeated request), or null
    /// when the result was applied and waits for its save (answered through <see cref="OpCommitted"/>), or this same request
    /// repeats while it saves. Another finish of a finishing session is refused <see cref="ForgeReason.NoSession"/> at once
    /// (a double finish changes nothing, and every request is answered).
    /// </summary>
    public ForgeResult? Finish(long characterId, uint requestId, int connection)
    {
        if (!_characters.TryGetValue(characterId, out var c))
            return Refusal(requestId, ForgeKind.None, 0, ForgeReason.ForgeUnavailable);
        if (c.Seen(connection, requestId) is { } seen)
            return seen;
        if (c.Session is not { } session)
            return Refusal(requestId, ForgeKind.None, 0, ForgeReason.NoSession);
        if (session.State == ForgeSessionState.Finishing) // the finishing request itself waits for its commit; any other has nothing to finish
            return session.FinishRequestId == requestId && session.FinishConnection == connection
                ? null
                : Refusal(requestId, session.Definition.Kind, session.Definition.NetId, ForgeReason.NoSession);
        var definition = session.Definition;
        if (session.State != ForgeSessionState.Running)
            return Refusal(requestId, definition.Kind, definition.NetId, ForgeReason.NoSession);

        int toTier = ForgeRules.ConversionTier(definition);
        var odds = toTier > 0 ? _rules.Odds(toTier) : null;
        if (toTier > 0 && (odds == null || c.Conversions(ForgeRolls.UtcDay(_nowMs()), toTier) >= odds.DailyCap))
            return Refusal(requestId, definition.Kind, definition.NetId, ForgeReason.ConversionCapReached);

        // Every random number, from the session's seed in a fixed order (rule 6).
        var actionMin = new List<int>(session.Actions.Count);
        var actionMax = new List<int>(session.Actions.Count);
        foreach (int netId in session.Actions)
        {
            var action = _rules.Action(netId)!;
            actionMin.Add(action.QualityMin);
            actionMax.Add(action.QualityMax);
        }
        var rolls = ForgeRolls.Draw(session.Seed, actionMin, actionMax, _rules.Caps.ServerRoll, _rules.Caps.SmallTerm,
            odds == null ? 0 : _rules.ConversionPercent(odds.SuccessPercent, session.ConversionBonus), session.KeepInputsPercent);
        int quality = 0;
        if (definition.RollsQuality)
        {
            var raw = new QualityInputs
            {
                Materials = _rules.MaterialNumber(definition, session.InputItems),
                Actions = rolls.Actions,
                JobTree = session.QualityBonus,
                Minigame = _challenge.Score(session),
                ServerRoll = rolls.ServerRoll,
                SmallTerm = rolls.SmallTerm,
            };
            quality = QualityCalc.Compute(in raw, _rules.Caps).Total;
        }
        byte rarity = definition.RollsQuality ? _rules.Bands.Rarity(quality) : (byte)0;
        var output = rolls.Success ? definition.Output! : null;
        int outputCount = output == null ? 0 : definition.OutputCount;

        // The output must fit before anything is consumed; the session keeps running (the same seed gives the same result).
        // TODO(WP-M3-6 review, low): the check counts the reserved inputs' slots as taken, so a refine that would empty its
        // ore slot is refused in an otherwise full bag; count the slots the consumption frees (an InventoryService query).
        if (output != null && _inventory.SpaceFor(session.OutputRecipient, output, rarity) < outputCount)
            return Refusal(requestId, definition.Kind, definition.NetId, ForgeReason.BagFull);

        bool taint = _inventory.TaintsOutput(characterId, session.ReservationKey);
        var (kind, reason) = OpKindOf(definition, rolls.Success);
        string source = $"forge:{definition.Id}";
        // A failed conversion consumes its inputs (R-5.2-04) unless the Salvager node's roll keeps them (job-tree design).
        bool kept = !rolls.Success && rolls.InputsKept;
        if (kept)
            _inventory.Release(characterId, session.ReservationKey);
        else if (!_inventory.TryRemoveReserved(characterId, session.ReservationKey, session.ReservationKey + ":in", reason, source))
            throw new InvalidOperationException($"forge session {session.ReservationKey} lost its reservation");
        long instanceId = 0;
        if (output != null)
        {
            ItemProperties? properties = null;
            if (ItemCategories.IsInstance(output.Category))
            {
                properties = ItemProperties.New(session.OutputRecipient);
                properties.Quality = quality;
                properties.CrafterId = session.Owner;
                properties.Tainted = taint;
                properties.CraftedStats.AddRange(_rules.CraftedStats(definition, session.InputItems, quality));
            }
            var added = _inventory.TryAdd(session.OutputRecipient, output, rarity, outputCount, session.ReservationKey + ":out", reason,
                source, properties, stolen: properties == null && taint);
            if (added.Added != outputCount)
                throw new InvalidOperationException($"forge output {output.Id} did not fit after the room check");
            instanceId = added.FirstInstanceId;
        }
        _inventory.Release(characterId, session.MoldKey);
        if (toTier > 0)
            c.CountConversion(ForgeRolls.UtcDay(_nowMs()), toTier);

        var inputs = new List<string>(session.Inputs.Count);
        for (int i = 0; i < session.Inputs.Count; i++)
            inputs.Add(string.Create(CultureInfo.InvariantCulture,
                $"{session.InputItems[i].Id}:{session.InputRarities[i]}:{definition.Inputs[i].Count}"));
        var record = new CraftOpRecord(session.ReservationKey, kind, definition.Id, definition.Tier, inputs, definition.Output!.Id,
            outputCount, instanceId, quality, rarity, rolls.Success, session.Seed, session.OutputRecipient, _nowMs(), kept);
        var answer = new ForgeResult
        {
            RequestId = requestId, Reason = ForgeReason.None, Kind = definition.Kind, DefNetId = (ushort)definition.NetId,
            OutputItemNetId = (ushort)definition.Output.NetId, Count = (ushort)outputCount, Rarity = rarity,
            Quality = (ushort)Math.Clamp(quality, 0, ushort.MaxValue), InstanceId = instanceId, Success = rolls.Success,
            InputsKept = kept,
        };
        session.State = ForgeSessionState.Finishing;
        session.FinishRequestId = requestId;
        session.FinishConnection = connection;
        c.Pending.Add(new PendingOp(record, answer, definition, connection));
        MarkClass2?.Invoke(characterId);
        RequestSave?.Invoke(characterId, SaveReason.Craft);
        return null;
    }

    /// <summary>The window's view of the session: phase, actions and the quality range still possible (never a rolled number).</summary>
    public ForgeState State(long characterId, uint requestId, ForgeReason reason)
    {
        var state = new ForgeState { RequestId = requestId, Reason = reason, MaxActions = (byte)_rules.Settings.MaxActions };
        if (Session(characterId) is not { } session)
            return state;
        state.MaxActions = (byte)Math.Min(_rules.Settings.MaxActions + session.ExtraActions, ForgeStations.MaxSessionActions);
        var definition = session.Definition;
        state.Phase = session.State == ForgeSessionState.Finishing ? ForgePhase.Finishing : ForgePhase.Running;
        state.Kind = definition.Kind;
        state.DefNetId = (ushort)definition.NetId;
        state.StationEntityId = session.StationEntityId;
        state.Actions = new byte[session.Actions.Count];
        for (int i = 0; i < session.Actions.Count; i++)
            state.Actions[i] = (byte)session.Actions[i];
        if (definition.RollsQuality)
        {
            _rules.Preview(definition, session.InputItems, session.Actions,
                session.QualityBonus,
                _challenge.MaxScore(definition), out int min, out int max);
            state.QualityMin = (ushort)Math.Clamp(min, 0, ushort.MaxValue);
            state.QualityMax = (ushort)Math.Clamp(max, 0, ushort.MaxValue);
            state.RarityMin = _rules.Bands.Rarity(min);
            state.RarityMax = _rules.Bands.Rarity(max);
        }
        return state;
    }

    public CraftBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var c) || c.Pending.Count == 0)
            return null;
        if (c.InFlight != null)
            throw new InvalidOperationException($"character {characterId} already has a forge batch in flight");
        c.InFlight = new InFlightBatch(saveId, c.Pending.ToList());
        c.Pending.Clear();
        return new CraftBatch(c.InFlight.Ops.Select(op => op.Record).ToList());
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var c) || c.InFlight is not { } inFlight || inFlight.SaveId != saveId)
            return; // a save without a batch, or a stale completion: idempotent
        c.InFlight = null;
        foreach (var op in inFlight.Ops)
        {
            c.Remember(op.Connection, op.Answer);
            if (c.Session is { State: ForgeSessionState.Finishing } session && session.ReservationKey == op.Record.OpKey)
            {
                session.State = ForgeSessionState.Finished;
                c.Session = null;
            }
            OpCommitted?.Invoke(characterId, new CommittedForgeOp(op.Record, op.Answer, op.Definition, op.Connection));
        }
    }

    /// <summary>The reservation and operation key of a session: one per character and seed (spec 9.13 pattern).</summary>
    public static string ReservationKey(long characterId, ulong seed) =>
        string.Create(CultureInfo.InvariantCulture, $"forge:{characterId}:{seed:x16}");

    private ulong NextSeed(out ulong seed)
    {
        seed = ((ulong)_seeds.NextUInt() << 32) | _seeds.NextUInt();
        return seed;
    }

    /// <summary>A free mold instance in the Safe bag's equipment area; <paramref name="reserved"/> when only reserved ones are there.</summary>
    private InventoryLocation? FindMold(long characterId, string moldItemId, out bool reserved)
    {
        reserved = false;
        var mold = _rules.Item(moldItemId);
        var safe = _inventory.Rules.ReceiverOf(ItemCategory.MoldOrTool);
        if (mold == null || safe == null)
            return null;
        var inventory = _inventory.CurrentInventory(characterId);
        if (!inventory.TryIndex((byte)safe.Code, out int index))
            return null;
        var bag = inventory.BagAt(index);
        for (int slot = 0; slot < bag.SlotCount; slot++)
        {
            if (bag[slot].IsEmpty || bag[slot].ItemNetId != mold.NetId)
                continue;
            var at = new InventoryLocation((byte)safe.Code, slot);
            if (_inventory.Unreserved(characterId, at) > 0)
                return at;
            reserved = true;
        }
        return null;
    }

    /// <summary>Unreserved units of an input's stack, less what earlier inputs of the same start take from it.</summary>
    private int FreeUnits(long characterId, InventoryLocation at, IReadOnlyList<InventoryLocation> inputs, int index,
        ForgeDefinition definition)
    {
        int free = _inventory.Unreserved(characterId, at);
        for (int i = 0; i < index; i++)
        {
            if (inputs[i].Container == at.Container && inputs[i].Slot == at.Slot)
                free -= definition.Inputs[i].Count;
        }
        return free;
    }

    private static (CraftOpKind Kind, InventoryReason Reason) OpKindOf(ForgeDefinition definition, bool success) =>
        definition.RecipeType switch
        {
            RecipeType.Refine => (CraftOpKind.Refine, InventoryReason.Refine),
            RecipeType.Convert => (CraftOpKind.Convert, success ? InventoryReason.Convert : InventoryReason.ConvertFailed),
            RecipeType.Mold => (CraftOpKind.Mold, InventoryReason.Craft),
            _ => (CraftOpKind.Craft, InventoryReason.Craft),
        };

    private static ForgeResult Refusal(uint requestId, ForgeKind kind, int defNetId, ForgeReason reason) =>
        new() { RequestId = requestId, Kind = kind, DefNetId = (ushort)defNetId, Reason = reason };

    private sealed record PendingOp(CraftOpRecord Record, ForgeResult Answer, ForgeDefinition Definition, int Connection);

    private sealed record InFlightBatch(Guid SaveId, List<PendingOp> Ops);

    private sealed class CharacterForge
    {
        private readonly Dictionary<(long Day, int Tier), int> _conversions = new();
        private readonly Queue<(int Connection, ForgeResult Answer)> _seen = new();

        public ForgeSession? Session { get; set; }
        public List<PendingOp> Pending { get; } = new();
        public InFlightBatch? InFlight { get; set; }

        public int Conversions(long day, int toTier) => _conversions.TryGetValue((day, toTier), out int count) ? count : 0;

        public void CountConversion(long day, int toTier) => _conversions[(day, toTier)] = Conversions(day, toTier) + 1;

        public ForgeResult? Seen(int connection, uint requestId)
        {
            foreach (var (seenConnection, answer) in _seen)
            {
                if (seenConnection == connection && answer.RequestId == requestId)
                    return answer;
            }
            return null;
        }

        public void Remember(int connection, ForgeResult answer)
        {
            if (_seen.Count == SeenAnswers)
                _seen.Dequeue();
            _seen.Enqueue((connection, answer));
        }
    }
}
