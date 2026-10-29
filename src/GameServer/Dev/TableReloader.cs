using System.Collections;
using GameServer.AI;
using GameServer.Combat;
using GameServer.Host.Config;
using GameServer.Telemetry;
using GameServer.World;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Actions;
using Shared.Util;

namespace GameServer.Dev;

/// <summary>What one <see cref="TableReloader.Reload"/> did.</summary>
public enum ReloadOutcome
{
    /// <summary>No table differs from the running ones (an editor saved without a change, or a change was reverted).</summary>
    Unchanged,
    /// <summary>The files changed while they were read; the change event that follows reloads again.</summary>
    Unsettled,
    /// <summary>A check failed or a changed table needs a restart: nothing changed in the world.</summary>
    Rejected,
    /// <summary>Only tables the server does not read changed: nothing to swap.</summary>
    NotServer,
    /// <summary>The prepared rules were posted to the world's <see cref="RetuneMailbox"/>.</summary>
    Posted,
}

/// <summary>The result of one reload: the outcome, the reasons of a rejection, the changed tables, and the posted plan.</summary>
public sealed record ReloadReport(ReloadOutcome Outcome, IReadOnlyList<string> Errors, IReadOnlyList<string> ChangedTables,
    ulong Hash, RetunePlan? Plan);

/// <summary>
/// Dev hot reload of the config tables (spec 11.7, backlog A): reads every table file, runs every check the server start
/// and <c>DataValidator</c> run (<see cref="ConfigChecks"/>, plus the world's markers), compares the rows with the running
/// tables, and applies the reload classes of <see cref="TableReloadPolicy"/>. All or nothing: any error, or a change to a
/// Restart table, or a FixedRows table whose rows were added, removed, reordered or renumbered, rejects the whole reload;
/// the world keeps its tables. Otherwise it builds the new action catalog, combat, projectile and monster AI rules and
/// posts them to the world, which swaps them in at its next tick boundary (<see cref="GameWorld.ApplyRetune"/>).
/// <para>
/// Runs on the watcher's thread, never on the tick thread: it reads files and allocates. One reload at a time. Every
/// rejection and every swap is written to the console and to telemetry (<see cref="TelemetryKind.ConfigReload"/>).
/// </para>
/// </summary>
public sealed class TableReloader
{
    private readonly string _directory;
    private readonly WorldMap _map;
    private readonly RetuneMailbox _mailbox;
    private readonly TelemetryQueue? _telemetry;
    private readonly IClock _clock;
    private readonly int _worldId;
    private readonly TextWriter _log;
    private readonly object _gate = new();
    private ConfigSet _running;

    /// <param name="running">The tables the world runs now (what the server started with).</param>
    public TableReloader(string directory, WorldMap map, ConfigSet running, RetuneMailbox mailbox, TelemetryQueue? telemetry,
        IClock clock, int worldId, TextWriter log)
    {
        _directory = directory;
        _map = map;
        _running = running;
        _mailbox = mailbox;
        _telemetry = telemetry;
        _clock = clock;
        _worldId = worldId;
        _log = log;
    }

    /// <summary>The Hot tables' file hash, as the dev client computes it (<see cref="TableHash"/>).</summary>
    public static ulong HashOf(string directory) =>
        TableHash.Compute(TableReloadPolicy.HotTables(), name =>
        {
            var path = Path.Combine(directory, name + ".json");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        });

    public ReloadReport Reload()
    {
        lock (_gate)
        {
            var report = ReloadLocked();
            Report(report);
            return report;
        }
    }

    private ReloadReport ReloadLocked()
    {
        ulong hash = HashOf(_directory);
        var loaded = ConfigChecks.LoadAndCheck(_directory);
        if (HashOf(_directory) != hash)
            return new ReloadReport(ReloadOutcome.Unsettled, [], [], hash, null);

        var errors = loaded.Errors.Select(e => e.ToString()).ToList();
        if (errors.Count == 0)
            errors.AddRange(WorldLoader.CheckConfigMarkers(_map, loaded.Set).Select(e => e.ToString()));
        var changed = ChangedTables(_running, loaded.Set);
        var names = changed.Select(t => TableInfo.For(t).Name).ToList();
        if (errors.Count > 0)
            return new ReloadReport(ReloadOutcome.Rejected, errors, names, hash, null);
        if (changed.Count == 0)
            return new ReloadReport(ReloadOutcome.Unchanged, [], [], hash, null);

        foreach (var table in changed)
        {
            var name = TableInfo.For(table).Name;
            switch (TableReloadPolicy.ClassOf(table))
            {
                case ReloadClass.Restart:
                    errors.Add($"{name} changed: the server reads it only at start (restart the server)");
                    break;
                case ReloadClass.Hot when TableReloadPolicy.FixedRows(table):
                    if (SameRows(table, _running, loaded.Set) is { } problem)
                        errors.Add($"{name}: {problem} (rows of this table may only change their values; restart the server)");
                    break;
            }
        }
        if (errors.Count > 0)
            return new ReloadReport(ReloadOutcome.Rejected, errors, names, hash, null);

        if (!changed.Any(t => TableReloadPolicy.ClassOf(t) == ReloadClass.Hot))
        {
            _running = loaded.Set;
            return new ReloadReport(ReloadOutcome.NotServer, [], names, hash, null);
        }

        var plan = BuildPlan(loaded.Set, hash, names, errors);
        if (plan == null)
            return new ReloadReport(ReloadOutcome.Rejected, errors, names, hash, null);
        _running = loaded.Set;
        _mailbox.Post(plan);
        return new ReloadReport(ReloadOutcome.Posted, [], names, hash, plan);
    }

    private RetunePlan? BuildPlan(ConfigSet set, ulong hash, IReadOnlyList<string> names, List<string> errors)
    {
        var crossTable = new List<ValidationError>();
        var combat = CombatRules.TryBuild(set, crossTable);
        if (combat == null)
        {
            errors.AddRange(crossTable.Select(e => e.ToString()));
            return null;
        }
        var actions = ActionCatalog.From(set);
        var projectiles = ProjectileRules.From(set);
        var ai = combat.MonsterAi == null ? null : new MonsterAiRules(combat.MonsterAi, combat, actions, _map, projectiles);
        return new RetunePlan(actions, combat, projectiles, ai, hash, names);
    }

    /// <summary>Every table whose rows differ (count, order or any field value), in <see cref="ConfigTables.All"/> order.</summary>
    public static List<Type> ChangedTables(ConfigSet before, ConfigSet after)
    {
        var changed = new List<Type>();
        foreach (var type in ConfigTables.All)
        {
            before.TryGet(type, out var a);
            after.TryGet(type, out var b);
            if (a == null || b == null)
            {
                if (a != b)
                    changed.Add(type);
                continue;
            }
            if (a.Rows.Count != b.Rows.Count)
            {
                changed.Add(type);
                continue;
            }
            for (int i = 0; i < a.Rows.Count; i++)
            {
                if (!SameValues(type, a.Rows[i], b.Rows[i]))
                {
                    changed.Add(type);
                    break;
                }
            }
        }
        return changed;
    }

    /// <summary>Null when the table keeps its row ids in order and its netIds; else what changed.</summary>
    private static string? SameRows(Type type, ConfigSet before, ConfigSet after)
    {
        before.TryGet(type, out var a);
        after.TryGet(type, out var b);
        if (a == null || b == null)
            return "the table is missing";
        if (a.Rows.Count != b.Rows.Count)
            return $"{b.Rows.Count} rows instead of {a.Rows.Count}";
        var info = TableInfo.For(type);
        var netId = type.GetProperty("NetId");
        for (int i = 0; i < a.Rows.Count; i++)
        {
            if (info.IdField != null && !Equals(info.IdField.GetValue(a.Rows[i]), info.IdField.GetValue(b.Rows[i])))
                return $"row {i} is '{info.IdField.GetValue(b.Rows[i])}' instead of '{info.IdField.GetValue(a.Rows[i])}'";
            if (netId != null && !Equals(netId.GetValue(a.Rows[i]), netId.GetValue(b.Rows[i])))
                return $"row {i} changed its netId";
        }
        return null;
    }

    private static bool SameValues(Type type, object a, object b)
    {
        foreach (var field in TableInfo.For(type).Fields)
        {
            var x = field.GetValue(a);
            var y = field.GetValue(b);
            if (x is Array || y is Array)
            {
                if (!StructuralComparisons.StructuralEqualityComparer.Equals(x, y))
                    return false;
            }
            else if (!Equals(x, y))
            {
                return false;
            }
        }
        return true;
    }

    private void Report(ReloadReport report)
    {
        string tables = report.ChangedTables.Count == 0 ? "" : $" ({string.Join(", ", report.ChangedTables)})";
        switch (report.Outcome)
        {
            case ReloadOutcome.Rejected:
                foreach (var error in report.Errors)
                    _log.WriteLine($"ERROR reload {error}");
                _log.WriteLine($"Table reload REJECTED{tables}: {report.Errors.Count} error(s); the server keeps its tables.");
                Record(ConfigReloadResult.Rejected, report, tick: 0);
                break;
            case ReloadOutcome.NotServer:
                _log.WriteLine($"Table reload: nothing to swap, the server does not read the changed tables{tables}.");
                Record(ConfigReloadResult.NotServer, report, tick: 0);
                break;
            case ReloadOutcome.Posted:
                // The log and the record wait for the world's tick boundary; the continuation runs on the thread pool.
                report.Plan!.Applied.ContinueWith(applied =>
                {
                    if (!applied.IsCompletedSuccessfully)
                        return; // superseded by a newer reload before the world took it
                    lock (_gate)
                    {
                        _log.WriteLine($"Table reload accepted at tick {applied.Result}{tables}, hash {report.Hash:x16}.");
                        Record(ConfigReloadResult.Accepted, report, applied.Result);
                    }
                }, TaskScheduler.Default);
                break;
        }
    }

    private void Record(ConfigReloadResult result, ReloadReport report, uint tick) =>
        _telemetry?.TryEnqueue(new TelemetryRecord(TelemetryKind.ConfigReload, _clock.UtcNowMs, WorldId: _worldId,
            Subject: report.ChangedTables.Count, V0: tick, V1: (long)result, V2: unchecked((long)report.Hash),
            V3: report.Errors.Count));
}
