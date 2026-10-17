// Game server entry point (spec 1.6). Settings come from environment variables (see Host/ServerSettings.cs);
// game values come from the config tables in data/tables.
//
// Dev only: generate a local ticket secret once and keep it out of the repository, for example in PowerShell:
//   $env:MMORPG_TICKET_SECRET = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
//   $env:MMORPG_TICKET_KEY_ID = "1"; $env:MMORPG_REALM_ID = "1"; $env:MMORPG_GAME_PORT = "7777"
//   dotnet run --project src/GameServer
// Clients and bots need tickets signed with the same secret (the Auth API, or DevTicketIssuer in tests and bots).

using GameServer.Host;
using GameServer.Host.Config;
using Shared.Data;

var settings = ServerSettings.Read(Environment.GetEnvironmentVariable, out var settingErrors);
if (settings == null)
{
    foreach (var error in settingErrors)
        Console.Error.WriteLine($"ERROR {error}");
    Console.Error.WriteLine("See src/GameServer/Program.cs for the settings and a dev setup.");
    return 2;
}

var config = ConfigLoader.LoadAndValidate(settings.DataDirectory, ConfigTables.All);
if (!config.Ok)
{
    foreach (var error in config.Errors)
        Console.Error.WriteLine($"ERROR {error}");
    Console.Error.WriteLine("Config tables are invalid; run src/Tools/DataValidator for a report.");
    return 1;
}

using var server = new Bootstrap(ServerConfig.From(config.Set), settings, new SystemClock(), new StopwatchTickClock());
using var stopRequested = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // shut down cleanly instead of killing the process
    stopRequested.Set();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stopRequested.Set();

server.Start();
Console.WriteLine($"GameServer running: {settings}. Press Ctrl+C to stop.");

// Wake up once a second to notice a crashed simulation thread.
while (!stopRequested.Wait(TimeSpan.FromSeconds(1)) && server.WorldThread.Failure == null)
{
}

server.Stop();
var loop = server.WorldThread.Loop;
Console.WriteLine($"GameServer stopped after {loop.TicksRun} ticks (max tick {loop.MaxTickMicros} us, {loop.Overruns} overruns).");
return server.WorldThread.Failure == null ? 0 : 1;
