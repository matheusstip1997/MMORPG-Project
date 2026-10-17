// Dev-only connection helper until the Auth API exists (spec 2.1, 2.5). Never deploy it.
//
//   serve  Starts a loopback test host (the real LiteNetLibTransport with an echo listener) and keeps a fresh ticket
//          for it in the dev ticket file, re-issued after each use and before it expires, and deleted when the host
//          stops (clients skip a file whose ticket has expired, left by a host that did not stop cleanly). The ticket
//          secret is random and lives only in this process.
//   issue  Writes one ticket for another server that trusts the secret in the MMORPG_DEV_TICKET_SECRET environment
//          variable (hex, at least 32 bytes), for example a local GameServer host once it loads a dev key ring.
//
// Usage: dotnet run --project src/Tools/DevConnect -- serve [--port 7777] [--realm 1] [--account 1] [--out <file>]
//                                                          [--data data/tables] [--exit-after <seconds>]
//        dotnet run --project src/Tools/DevConnect -- issue --port <port> [--host 127.0.0.1] [--key-id 1] [--realm 1]
//                                                          [--account 1] [--out <file>] [--data data/tables]
// Exit codes: 0 ok, 1 config tables invalid, 2 bad arguments.

using System.Globalization;
using System.Security.Cryptography;
using DevConnect;
using GameServer.Host;
using GameServer.Host.Config;
using GameServer.Net;
using GameServer.Net.Tickets;
using Shared.Data;
using Shared.Data.Tables;

const string SecretVariable = "MMORPG_DEV_TICKET_SECRET";

if (args.Length == 0 || (args[0] != "serve" && args[0] != "issue"))
    return Usage();
var mode = args[0];
var options = new Dictionary<string, string>();
for (var i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
        return Usage();
    options[args[i][2..]] = args[++i];
}

var host = Option("host", "127.0.0.1");
var port = int.Parse(Option("port", mode == "serve" ? "7777" : "0"), CultureInfo.InvariantCulture);
var realm = int.Parse(Option("realm", "1"), CultureInfo.InvariantCulture);
var accountId = long.Parse(Option("account", "1"), CultureInfo.InvariantCulture);
var keyId = byte.Parse(Option("key-id", "1"), CultureInfo.InvariantCulture);
var outPath = Option("out", DevTicketFile.DefaultPath);
var exitAfterSeconds = int.Parse(Option("exit-after", "0"), CultureInfo.InvariantCulture);
if (port <= 0 && mode == "issue")
    return Usage();

var config = ConfigLoader.LoadAndValidate(Option("data", Path.Combine("data", "tables")), ConfigTables.All);
if (!config.Ok)
{
    foreach (var error in config.Errors)
        Console.Error.WriteLine($"ERROR {error}");
    return 1;
}
var limits = config.Set.Single<NetSecurity>();
var clock = new SystemClock();

if (mode == "issue")
{
    var secretHex = Environment.GetEnvironmentVariable(SecretVariable);
    if (string.IsNullOrEmpty(secretHex))
    {
        Console.Error.WriteLine($"Set {SecretVariable} to the server's dev ticket secret (hex).");
        return 2;
    }
    var issuer = new DevTicketIssuer(keyId, Convert.FromHexString(secretHex), clock, limits.TicketLifetimeMs);
    WriteTicket(issuer);
    return 0;
}

var secret = RandomNumberGenerator.GetBytes(TicketKeyRing.MinKeySize);
var validator = new TicketValidator(new TicketKeyRing().Add(keyId, secret), new InMemoryNonceCache(clock), clock, realm, limits.TicketLifetimeMs);
var echo = new EchoHost();
using var transport = new LiteNetLibTransport(new ConnectionApprover(validator), echo, limits, clock);
echo.Transport = transport;
var serveIssuer = new DevTicketIssuer(keyId, secret, clock, limits.TicketLifetimeMs);
var ticketUsed = false;
echo.PeerConnected += () => ticketUsed = true;

transport.Start(port);
port = transport.LocalPort;
Console.WriteLine($"Dev test host on UDP {host}:{port}, realm {realm}. Ctrl+C stops it.");

var stop = false;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop = true; };
var startedAtMs = clock.UtcNowMs;
var issuedAtMs = WriteTicket(serveIssuer);
while (!stop && (exitAfterSeconds <= 0 || clock.UtcNowMs - startedAtMs < exitAfterSeconds * 1000L))
{
    transport.PollEvents();
    // Tickets are single use and short lived (R-2.5-01): keep one usable ticket in the file at all times.
    if (ticketUsed || clock.UtcNowMs - issuedAtMs > limits.TicketLifetimeMs / 2)
    {
        ticketUsed = false;
        issuedAtMs = WriteTicket(serveIssuer);
    }
    Thread.Sleep(5);
}
transport.Stop();
// No host, no ticket: a leftover file would point clients at a port nobody listens on.
File.Delete(outPath);
return 0;

long WriteTicket(DevTicketIssuer issuer)
{
    var now = clock.UtcNowMs;
    var issued = issuer.Issue(accountId, realm, slot: 0);
    DevTicketFile.Write(outPath, host, port, accountId, now + limits.TicketLifetimeMs, issued, limits);
    CryptographicOperations.ZeroMemory(issued.SessionKey);
    Console.WriteLine($"Wrote a ticket for account {accountId} to {Path.GetFullPath(outPath)}");
    return now;
}

string Option(string name, string fallback) => options.TryGetValue(name, out var value) ? value : fallback;

static int Usage()
{
    Console.Error.WriteLine("Usage: DevConnect serve [--port 7777] [--realm 1] [--account 1] [--out <file>] [--data <dir>] [--exit-after <seconds>]");
    Console.Error.WriteLine("       DevConnect issue --port <port> [--host 127.0.0.1] [--key-id 1] [--realm 1] [--account 1] [--out <file>] [--data <dir>]");
    return 2;
}
