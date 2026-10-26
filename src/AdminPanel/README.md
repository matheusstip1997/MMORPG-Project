# Admin panel v0 (backlog J, spec 7.4)

A web page and a small JSON API to search characters, kick a connected character, and read server logs and telemetry.
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
| Health | `GET /healthz` | nothing |

- **Requests:** every request but `/healthz` and the sign-in needs the cookie. Every `POST` and `DELETE` also needs the header `X-Admin-Request: 1`.
- **Answers:** every answer has `ok`. A refusal also has `reasonCode` (`AdminReasonCode`).

**Kick, step by step:**
1. The page makes a request id per kick and sends the same id again on a retry.
2. The panel records the kick for the game server whose ownership lock holds the character. A character nobody holds is recorded finished as `NotOnline`.
3. That server's `AdminCommandWorker` claims the row off the tick.
4. The world disconnects the client in tick phase 1 (reason `Kicked`). The character lingers like after a disconnect (R-2.1-08).
5. The worker writes the outcome back: `Kicked`, `NotConnected` (already lingering), `NotOnline` or `Expired` (no server answered within `AdminSettings.commandExpiryMs`).

**Audit:** every action (sign-in, search, character view, log view, telemetry view, kick) is one `admin_actions` row with the admin, time, target and detail.

## Tests

- `tests/AdminPanel.Tests`: the API against a real PostgreSQL and Redis, plus the log and telemetry readers.
- `tests/GameServer.Tests/Admin`:
  - the kick on the world tick;
  - the action log repository;
  - a real server kicking a real client.
- `tools/compose/Test-ComposeStack.ps1`: the whole stack, also in CI.
