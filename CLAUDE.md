# GAME TITLE: MMORPG (Unity client + .NET server)

Top-down 45-degree action MMORPG with souls-like pacing (stamina, dodge, parry), a closed player-driven economy, a PK/honor law system, jobs and crafting, and guild territories. Steam only. Starts at 10-15 players, must scale to hundreds. Team of 5, no deadline, phased delivery.

## Source of truth
- The design lives in `docs/spec/` (start with `docs/spec/00-index.md`; for Milestone 1 read `docs/spec/m1-quick-spec.md` first). Read the file for the system you are changing **before** you change it. Rules have IDs like `R-3.3-02`: cite them in commits, tests, and comments where relevant.
- Rules marked **Open** in the spec are undecided: do not invent them. Leave a `TODO(open): ...` and ask.
- If code and spec disagree, stop and ask which is right. If a rule changes, update the spec file in the same change.

## Current milestone
**M1: combat slice** (small safe city, 4 root classes, a few monsters, one boss, login screens, minimal HUD). Backlog A-K is in `docs/spec/12-delivery-plan-milestones.md`. Do not build M2+ systems yet (crafting, economy loops, guilds, arena, raids) unless asked.

## Stack
- Client: Unity 6.3 LTS, C#. Server: .NET (current LTS), C#. Transport: LiteNetLib behind `ITransport`.
- Serialization: hand-written bit-packed writers for hot unreliable messages (inputs, snapshots, projectile spawns); MemoryPack for reliable messages (append fields only); versioned JSON (PostgreSQL JSONB) for item properties.
- Data: PostgreSQL, Redis. Dev: `docker compose`. CI: GitHub Actions. Login: Steam (a dev login exists behind a build flag).

## Repository layout (target)
```
src/Shared/        netstandard2.1, C# 9, referenced by Unity (local package) and the server
src/GameServer/    Host, Net, World, Entities, Combat, Stats, Skills, Progression, Items, Economy,
                   Crafting, Contracts, Quests, Social, Arena, Law, Guilds, Events, Persistence, Telemetry
src/AuthApi/  src/AdminPanel/  src/Workers/  src/Tools/ (UnityExporter, DataValidator, EconomySimulator, BotClient)
unity/             Unity project (Assets/Game/{Net,Input,Prediction,Entities,UI,VFX,Audio,Streaming})
data/              config tables (json/csv) + schemas      tests/   infra/   docs/
```

## Commands
```
dotnet build Mmorpg.sln
dotnet test Mmorpg.sln
dotnet run --project src/GameServer   # needs MMORPG_TICKET_SECRET, _KEY_ID, _REALM_ID, _GAME_PORT; dev setup in src/GameServer/Program.cs
docker compose up -d --wait   # PostgreSQL + Redis on 127.0.0.1 (+ services as they are added)
dotnet run --project src/Tools/DataValidator   # validates data/tables; --report <file.json> for a JSON report
```
Config tables: row type in `src/Shared/Data/Tables` (attributes `[Table]`, `[Singleton]`, `[TableId]`, `[Range]`, `[Ref]`, `IValidatableRow` for cross-field rules), registered in `ConfigTables.All`, data in `data/tables/<Table>.json` (camelCase, integer ms/cm/percent). Loader: `src/GameServer/Host/Config/ConfigLoader.cs`.
Networking: protocol types in `src/Shared/Protocol`; server transport in `src/GameServer/Net` (`ITransport`, `LiteNetLibTransport`, `ConnectionApprover`, `Tickets/`). Integration tests connect a real LiteNetLib client over localhost.
SDK is pinned in `global.json`. CI (`.github/workflows/ci.yml`) runs build and tests on every push.
Unity editor is pinned to **6000.3.26f1** (`unity/ProjectSettings/ProjectVersion.txt`), URP + Input System. `src/Shared` is the local package `com.mmorpg.shared` (asmdef `Game.Shared`); .NET build output goes to `/artifacts` so Unity never imports `obj/`.
Unity tests can run headless: `Unity.exe -batchmode -projectPath unity -runTests -testPlatform EditMode -testResults <file.xml> -logFile -` (editor: `C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Unity.exe`).

## Architecture rules (non-negotiable)
1. **Server authoritative.** Clients send inputs and requests, never results.
2. **Data driven.** No magic numbers: every rate, window, cap, cost is a config table with a schema and a validator rule.
3. **Event driven.** Systems publish facts on the event bus; they do not call into each other's internals. Consumers are idempotent.
4. **One writer per concern.** Only `InventoryService` changes items and gold. Only `FeeService` computes fees. Only `HonorService` changes honor. Only `SentencingService` jails.
5. **Atomic and idempotent.** Every value-moving operation is one DB transaction with an idempotency key and an audit row; commit before confirming to the player.
6. **Deterministic simulation.** Integer or fixed-point math in simulation code, fixed processing order (entity ID), seeded `IRng` with logged seeds, time from `IClock` (never `DateTime.Now`), UTC timestamps instead of timers.
7. **Shared code stays portable.** `Shared` depends only on MemoryPack, uses no Unity or server APIs, and is compiled as C# 9 (pin `LangVersion` 9 in its project). Movement, damage, stat, and action-timing code lives there so client prediction and server match.
8. **Identity display** goes through `DisplayIdentity(entity, observer)` only (disguise system). Never read raw names for display.
9. **Hidden entities are never sent** to clients. Visibility goes through `IVisibilityQuery`.
10. **Telemetry is async** and must never block the tick.
11. **Save classes** (`docs/spec/07-operations-platform.md`): transfers between players or with shared state commit synchronously (including outpost sales); single-player gains are batched every 1 s; volatile state every 10 s; save a character's pending batch before any synchronous operation on it.
12. **20 Hz tick** with sub-tick input timestamps; lag compensation capped at 200 ms.

## Code conventions
- No allocations in hot paths (tick loop, packet encode/decode). No I/O on the tick thread.
- Nullable reference types enabled on the server. Small classes, explicit names, no clever abstractions.
- Every pure function (damage, price, quality, exp, honor, fee split, loot roll) has unit tests. Every transaction has integration tests: double submit, retry, crash in the middle, concurrent access.
- Add or change config tables together with their schema and the `DataValidator` rule.
- Reason codes for every refused action (they drive UI text); see `docs/spec/09-client-ui-hud-login.md`.

## Working agreement
- Small tasks, one branch per task. For anything touching more than 3 files, write a short plan first and wait for approval.
- Build and run tests before saying a task is done; report what you ran.
- Ask before adding a dependency. Do not touch `infra/`, secrets, AWS, or Steam credentials.
- Never commit `Library/`, `Temp/`, `Logs/`, `Builds/`, or secrets. Large binaries go through Git LFS.
- Keep this file short. If a rule matters to a specific system, put it in `docs/spec/`.

## Never
- Never trust a client value. Never change items or gold outside `InventoryService`. Never hardcode rates. Never use `DateTime.Now` or `System.Random` in simulation. Never put Steam-specific code outside the auth layer. Never invent an Open rule.
