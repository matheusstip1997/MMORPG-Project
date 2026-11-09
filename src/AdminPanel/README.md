# Admin panel v0 (backlog J, spec 7.4)

A web page and a small JSON API to search characters, kick a connected character, read server logs and telemetry, and see
the economy's faucet and sink totals (WP-M4-5).
**Dev only in M1**: anyone who can reach it signs in with any name. Production admin auth and roles are Open (spec 7.4).

## Run it

With the composed dev stack (spec 11.3) it is already running: `docker compose up -d --wait`, then open
<http://127.0.0.1:5090> and sign in with any name (recorded as `dev:<name>`).

By hand, next to servers started by hand (`unity/Assets/Game/UI/README.md`), in PowerShell at the repository root:

```powershell
$env:MMORPG_GLOBAL_DB = "Host=127.0.0.1;Port=5432;Username=mmorpg;Password=mmorpg_dev;Database=mmorpg_global"
$env:MMORPG_REALM_DB = "Host=127.0.0.1;Port=5432;Username=mmorpg;Password=mmorpg_dev;Database=mmorpg_realm_1"
$env:MMORPG_REALM_ID = "1"; $env:MMORPG_REDIS = "127.0.0.1:6379"
$env:MMORPG_ADMIN_IDENTITY = "dev"   # Debug builds only; the default "none" lets nobody sign in
dotnet run --project src/AdminPanel
```

Optional: `MMORPG_REDIS_PREFIX` (the game server's, default `mmorpg:`), `MMORPG_TELEMETRY_DIR` (default `telemetry`),
`MMORPG_ADMIN_LOG_DIR` (a folder of `*.log` files, default `logs`), `MMORPG_ADMIN_URLS` (default
`http://127.0.0.1:5090`; plain HTTP on loopback only), `MMORPG_DATA_DIR`. Tuning: the `AdminSettings` table.

## What it does

| Page part | API | Reads or writes |
|---|---|---|
| Sign in / out | `POST /api/session` `{name}`, `DELETE /api/session`, `GET /api/session` | cookie `mmorpg_admin` |
| Search | `GET /api/characters?name=<prefix>&account=<dev:7, SteamID or id:12>` | realm `characters`, global `accounts`, Redis ownership locks (who holds it) |
| Character | `GET /api/characters/{id}` | the saved row (not live state) and its admin actions |
| Kick | `POST /api/characters/{id}/kick` `{requestId, note}` | writes one `admin_actions` row; the game server runs it |
| Follow an action | `GET /api/actions/{requestId}`, `GET /api/actions?characterId=` | `admin_actions` |
| Server logs | `GET /api/logs?file=&contains=&lines=` | `*.log` in the log folder (names from the listing only) |
| Telemetry | `GET /api/telemetry?characterId=&accountId=&kind=&limit=` | the newest telemetry JSON lines files |
| Economy | `GET /api/economy?hours=<n>` | realm `gold_ledger` (faucet and sink totals per UTC hour and by reason, moves left out), `faucet_sink_snapshots`, the newest `EconomyTotals` telemetry record |
| Health | `GET /healthz` | nothing |

- **Requests:** every request but `/healthz` and the sign-in needs the cookie. Every `POST` and `DELETE` also needs the header `X-Admin-Request: 1`.
- **Answers:** every answer has `ok`. A refusal also has `reasonCode` (`AdminReasonCode`).

**Kick, step by step:**
1. The page makes a request id per kick and sends the same id again on a retry.
2. The panel records the kick for the game server whose ownership lock holds the character. A character nobody holds is recorded finished as `NotOnline`.
3. That server's `AdminCommandWorker` claims the row off the tick.
4. The world disconnects the client in tick phase 1 (reason `Kicked`). The character lingers like after a disconnect (R-2.1-08).
5. The worker writes the outcome back: `Kicked`, `NotConnected` (already lingering), `NotOnline` or `Expired` (no server answered within `AdminSettings.commandExpiryMs`).

**Audit:** every action (sign-in, search, character view, log view, telemetry view, economy view, kick) is one `admin_actions` row with the admin, time, target and detail.

**Economy (WP-M4-5, spec 7.4; the page's final shape is M4 decision 13, still Open):**
- The window is the last `n` UTC hours, the current one included (default `AdminSettings.economyDefaultHours`, at most `economyMaxHours`).
- The hourly totals are the authoritative figures from `gold_ledger` and equal `BotClient report --kind economy` over the same run's telemetry (moves between players count as neither; `OutpostEndToEndTests` compares the two).
- The answer lists the `InventoryReason` names in value order, so the page needs no change for a new reason.
- The Economy tab draws the hours as a bar chart by hand (no chart library), then the table per hour and reason, the snapshots (gold online, active players) and the live line.
- To compare with a bot run: point `MMORPG_REALM_DB` at the run's realm database and `MMORPG_TELEMETRY_DIR` at its `telemetry/` folder, set `MMORPG_ADMIN_URLS=http://127.0.0.1:5091` beside the composed panel, and compare `/api/economy` with `BotClient report --telemetry <dir> --kind economy --json <file>`.

## Tests

- `tests/AdminPanel.Tests`: the API against a real PostgreSQL and Redis, plus the log and telemetry readers.
- `tests/GameServer.Tests/Admin`:
  - the kick on the world tick;
  - the action log repository;
  - a real server kicking a real client.
- `tools/compose/Test-ComposeStack.ps1`: the whole stack, also in CI.
