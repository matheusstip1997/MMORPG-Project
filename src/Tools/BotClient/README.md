# BotClient (backlog J)

Headless bots, a link simulator and the bot load test (spec 11.5; M1 exit criteria in spec 12.4).

- **Bots play like the Unity client, without a screen.** A bot runs the client's own engine-free sources, compiled for .NET in `src/Tools/ClientCore`:
  - `ClientConnection`, `LocalPlayerPredictor`, `InputPacketBuilder`, `TickClock`, `EntityRegistry`, `InterpolationClock`, `ClientInventory`;
  - the Auth API client of the login screens.
- **It never touches server internals** (no `GameServer` reference). It logs in with the dev login (`dev:<n>`), creates a character of its class if the account has none, enters the world, and sends inputs through the predictor. The server sees nothing a real client would not send.
- **How a bot plays** (`BotBehaviour` table, row `field-combat`):
  - walks to its home spawn point;
  - fights the nearest living monster with its class kit the way the QA kill step does (`ActionBindings`: normal attack, skills when the predicted machine says they are ready);
  - dodges some monster attacks;
  - loots its own kills;
  - after a death it respawns and starts over.
- **Determinism:** all random choices come from the run seed, which is printed.
- **Link simulation.** `UdpLinkRelay` delays and drops whole UDP datagrams (`BotLinkProfile`: one-way delay per direction, jitter, loss, reordering on or off) with a seeded `LinkConditioner` per direction and client.
  - **Why below LiteNetLib:** the relay works on raw datagrams, so LiteNetLib's ping and resends see the simulated link. The server caps the view lag a client may claim with that ping (R-3.1-03). A delay above LiteNetLib (an `ITransport` wrapper) would be invisible to it, and dodges and parries would be judged against a false round trip.
  - LiteNetLib's own simulation is not used: it only works in DEBUG builds of the library and is not seeded.
  - **Latency means round trip:** `rtt100` is 50 ms each way (user decision 2026-10-26).
  - **Own source address per client.** The relay gives every client its own loopback source address toward the server (127.0.0.2, .3, …). The per-IP connect limit (`NetSecurity`, R-2.5-03) then treats each bot as its own player, without changing the limits.
- **Load test.** `loadtest` is the same on CI and locally. It:
  1. starts the GameServer and the Auth API from their Debug builds, on free ports, with secrets generated for the run (process environment only) and a Redis key prefix of their own;
  2. plays a `BotScenario`;
  3. reads the server's per-tick `TickTiming` telemetry (`TelemetrySettings.tickTimingLog`);
  4. checks the pass criteria.
  The tick budget is one tick period (20 Hz: 50 ms). CI requires p99 ≤ 50% (spec 11.5); `m1-exit` requires ≤ 100% (spec 12.4).

## Commands

Build first (Debug: the dev login exists only there):

```
dotnet build src/GameServer; dotnet build src/AuthApi; dotnet build src/Tools/BotClient
```

| Command | What it does |
|---|---|
| `loadtest [--scenario ci-smoke] [--link <id>] [--bots <n>] [--duration-ms <ms>] [--db-suffix <s>] [--run-dir <dir>]` | The whole load test. It needs PostgreSQL and Redis (docker compose) and migrated databases (`DbMigrate -- --dev [--db-suffix <s>]`). It writes `summary.json`, `gameserver.log`/`.err`, `authapi.log`/`.err` and `telemetry/` into the run folder (default `artifacts/load/<time>-<scenario>`). Exit code 0 means every criterion passed. |
| `run [--scenario <id>] [--auth-url http://127.0.0.1:5080] [--game 127.0.0.1:7777] [--bots <n>] [--class <id>] [--link <id>] [--duration-ms <ms>]` | Bots against servers that are already running (for example the QA stack). The servers must share the ticket secret, and the Auth API must use `MMORPG_AUTH_IDENTITY=dev`. |
| `relay [--listen 7778] [--server 127.0.0.1:7777] [--link rtt100]` | The link simulator on its own, for the Unity client (below). |
| `report --telemetry <dir> [--from-ms <utc>] [--to-ms <utc>]` | Tick p50, p95, p99 and max from a telemetry folder. |

Run them as `dotnet artifacts/bin/BotClient/debug/BotClient.dll <command> …`, or as `dotnet run --project src/Tools/BotClient -- <command> …`.

**Local 50-bot run (M1 exit criterion):**

```
powershell -ExecutionPolicy Bypass -File tools/load/Run-BotLoad.ps1 [-Link rtt100|rtt200] [-DbSuffix <s>]
```

It brings up docker compose, builds, migrates, and runs `loadtest --scenario m1-exit`. It prints the tick p50 and p99 at the end.

## Unity editor at 100 or 200 ms

The relay needs no Unity change.

1. Start the GameServer (CLAUDE.md).
2. Run `dotnet run --project src/Tools/BotClient -- relay --listen 7778 --server 127.0.0.1:7777 --link rtt200`.
3. Point the client at port 7778:
   - **walk-around scene:** `dotnet run --project src/Tools/DevConnect -- issue --port 7778`;
   - **login flow:** the `ServerDirectory` row's port, as a local edit you do not commit.

The relay prints what it dropped and how late it sent (Windows timer wake-ups add up to about a millisecond; the tool raises the timer resolution to 1 ms and opts out of power throttling, which on Windows 11 would otherwise ignore that request for a process without a visible window, and the relay thread runs above normal priority).

## Scenarios (`data/tables/BotScenario.json`)

| Id | Bots | Time | Link | Tick p99 | Where |
|---|---|---|---|---|---|
| `ci-smoke` | 20 | 2 min | rtt100 | ≤ 50% of the tick | CI, every push and pull request |
| `ci-merge` | 20 | 5 min | rtt100 | ≤ 50% | CI, pushes to main |
| `m1-exit` | 50 | 5 min | lan (`-Link` for others) | ≤ 100% | locally, `tools/load/Run-BotLoad.ps1` |

Every scenario also requires:
- all bots logged in and entered the world;
- no disconnects;
- at least `minInCombatPercent` of the bots reached combat (the server ran one of their attacks with a living monster in reach): 100% in CI, 90% for `m1-exit`, where 50 bots share the 5 monsters;
- no world crash, security event, dropped telemetry or malformed input, and no unreliable message the server refused as too large to send (`SendCounters`);
- no error lines from either server.

## Limits and TODOs
- **One LiteNetLib manager per bot** (a few threads each): fine for tens of bots. The ramp of spec 11.5 (100-500) needs a shared manager. TODO(J): BotClient v2. `NetSecurity.maxPeers` is 100 today.
- **The fight logic and grid paths are ports** of the QA run's `QaFightPlan` and `QaNavigation`. TODO(J): one engine-free copy for both.
- **TODO(7.3) bot scoring:** behavioural scoring is an offline worker over telemetry (spec 7.3 layer 3, M5). BotClient accounts are dev logins in the range the run prints; real bots would never use the dev login.
- Character names are `Bot` plus random letters. Runs create accounts that nothing deletes, so use `-DbSuffix` for databases of your own.
