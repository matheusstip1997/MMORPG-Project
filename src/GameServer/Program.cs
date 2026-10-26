// Game server entry point (spec 1.6). Settings come from environment variables (see Host/ServerSettings.cs);
// game values come from the config tables in data/tables.
//
// Dev only: generate a local ticket secret once and keep it out of the repository, for example in PowerShell:
//   $env:MMORPG_TICKET_SECRET = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
//   $env:MMORPG_TICKET_KEY_ID = "1"; $env:MMORPG_REALM_ID = "1"; $env:MMORPG_GAME_PORT = "7777"
//   $env:MMORPG_REALM_DB = "Host=127.0.0.1;Port=5432;Username=mmorpg;Password=mmorpg_dev;Database=mmorpg_realm_1"
//   $env:MMORPG_REDIS = "127.0.0.1:6379"
//   docker compose up -d --wait postgres redis; dotnet run --project src/Tools/DbMigrate -- --dev
//   (`docker compose up -d --wait` alone also starts a composed GameServer on 7777: see docker-compose.yml)
//   dotnet run --project src/GameServer
// A client logs in to the character in its ticket's slot (create one through the Auth API first).
// Clients and bots need tickets signed with the same secret (the Auth API, or DevTicketIssuer in tests and bots).
// Telemetry goes to ./telemetry (git-ignored at the repository root) unless MMORPG_TELEMETRY_DIR names another folder.
// The world comes from data/world/m1 (exported by UnityExporter) unless MMORPG_WORLD_DIR names another export.

using GameServer.Host;
using GameServer.Host.Config;
using GameServer.World.Map;
using Shared.Data.Tables;

var settings = ServerSettings.Read(Environment.GetEnvironmentVariable, out var settingErrors);
if (settings == null)
{
    foreach (var error in settingErrors)
        Console.Error.WriteLine($"ERROR {error}");
    Console.Error.WriteLine("See src/GameServer/Program.cs for the settings and a dev setup.");
    return 2;
}

#if !DEV_TOOLS
if (settings.DevTools)
{
    Console.Error.WriteLine($"ERROR {ServerSettings.DevToolsVariable} is on, but this build has no dev tools (build Debug, or -p:DevTools=true).");
    return 2;
}
#endif

// Every table check DataValidator runs (ConfigChecks), including the cross-table rules of the stat, class and monster tables.
var config = ConfigChecks.LoadAndCheck(settings.DataDirectory);
if (!config.Ok)
{
    foreach (var error in config.Errors)
        Console.Error.WriteLine($"ERROR {error}");
    Console.Error.WriteLine("Config tables are invalid; run src/Tools/DataValidator for a report.");
    return 1;
}

// Spec 11.6: the server verifies the exported world (manifest version, file hashes, regions, markers) before it starts.
var world = WorldLoader.Load(settings.WorldDirectory, config.Set);
// The markers the tables name: the player spawn and every SpawnPoints row.
var markerErrors = world.Map == null ? [] : WorldLoader.CheckConfigMarkers(world.Map, config.Set);
if (!world.Ok || markerErrors.Count > 0)
{
    foreach (var error in world.Errors.Concat(markerErrors))
        Console.Error.WriteLine($"ERROR {error}");
    Console.Error.WriteLine("The world files are invalid; run src/Tools/DataValidator with --world for a report.");
    return 1;
}

var clock = new SystemClock();
// Dev builds on Windows: 1 ms sleeps for the tick clock (QA wave 6 O1); other builds and Linux change nothing.
using var timerResolution = WindowsTimerResolution.Raise();
using var server = new Bootstrap(ServerConfig.From(config.Set), settings, clock, new StopwatchTickClock(), world.Map!);
using var stopRequested = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // shut down cleanly instead of killing the process
    stopRequested.Set();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stopRequested.Set();

server.Start();
Console.WriteLine($"GameServer running: {settings}. Press Ctrl+C to stop.");

#if DEV_TOOLS
// Spec 11.7: dev hot reload. A watcher thread reloads and checks the tables after every change; the world swaps accepted
// tables in at a tick boundary.
using var tableWatcher = settings.DevTools ? StartHotReload() : null;

GameServer.Dev.TableWatcher StartHotReload()
{
    var reloader = new GameServer.Dev.TableReloader(settings.DataDirectory, world.Map!, config.Set, server.World.Retunes,
        server.TelemetryQueue, clock, Bootstrap.MainWorldId, Console.Out);
    var debounceMs = config.Set.Single<DevTools>().HotReloadDebounceMs;
    Console.WriteLine($"Dev tools on: tables in {Path.GetFullPath(settings.DataDirectory)} reload {debounceMs} ms after a change.");
    return new GameServer.Dev.TableWatcher(settings.DataDirectory, debounceMs, () => reloader.Reload());
}
#endif

// Wake up once a second to notice a crashed simulation thread.
while (!stopRequested.Wait(TimeSpan.FromSeconds(1)) && server.WorldThread.Failure == null)
{
}

server.Stop();
var loop = server.WorldThread.Loop;
Console.WriteLine($"GameServer stopped after {loop.TicksRun} ticks (max tick {loop.MaxTickMicros} us, {loop.Overruns} overruns).");
return server.WorldThread.Failure == null ? 0 : 1;
