# BotClient (backlog J)

Headless bots, a link simulator and the bot load test (spec 11.5; M1 exit criteria in spec 12.4).

- **Bots play like the Unity client, without a screen.** A bot runs the client's own engine-free sources, compiled for .NET in `src/Tools/ClientCore`:
  - `ClientConnection`, `LocalPlayerPredictor`, `InputPacketBuilder`, `TickClock`, `EntityRegistry`, `InterpolationClock`, `ClientInventory`;
  - the Auth API client of the login screens.
- **It never touches server internals** (no `GameServer` reference). It logs in with the dev login (`dev:<n>`), creates a character of its class if the account has none, enters the world, and sends inputs through the predictor. The server sees nothing a real client would not send.
- **How a bot plays** (`BotBehaviour` table, row `field-combat`):
  - walks to its home spawn point;
  - fights the nearest living monster with its class kit the way the QA kill step does (`ActionBindings`: normal attack, skills when the predicted machine says they are ready);
  - presses through the 4-hit normal attack chain (R-3.1-10, `pressAttackChain`): while a hit with a next hit runs, it presses the attack once its chain window opens (`chainFromMs`; the machine starts the next hit at `chainLinkMs`) and once more for the link tick: the server's machine steps once per server tick, runs a few ticks behind the prediction after input catch-up (or ahead after late frames), and takes the press only from `chainFromMs` to the hit's end, so the first press covers a server ahead and the second one behind (taken, the first one makes the second land in the next hit before its window, where it is dropped). Per hit (again after a snapshot corrects the hit's elapsed ticks; `chainPresses` counts hits pressed for), when the snapshot stamina pays the next hit and the target is in reach (QA's `QaFightPlan.ChainPressDue`). Like QA, it starts the normal attack only when the stamina pays the whole chain (`KitAction.ChainStaminaCost`, 40 for every root kit today): its skills spend the stamina first, so hit 1 would otherwise come at the last 10 stamina and hit 2 could never be paid. `field-combat-hit1` is the M1 bot that played hit 1 only;
  - backs off when hurt the way the QA kill step does (`QaFightPlan.Recovered`): below `backOffBelowPercent` with no heal ready it walks away for at least `backOffMs`, then stays out (new legs away from a monster closer than `backOffDistanceCm`, else it stands and regenerates) until HP is back to `reengageAbovePercent` or a self-heal is usable, for `backOffRecoverMs` at most. It starts no new fight below `reengageAbovePercent` either (same wait), but finishes the fight it is in down to the back-off line. Before this, a hurt bot walked back after `backOffMs` at the HP that sent it away and looped back off, walk back, hit until it died;
  - with `planBackOffLegs` (row `m2-exit`, WP-M2-12) each leg is planned as the QA kill step plans it (`QaNavigation.TryBackOffTarget`, `Brain/BackOffLeg.cs`): as directly away from the threat as the world allows, turning in 15 degree steps, the leg ends within the threat's `leashRangeCm` less `backOffLeashMarginCm` of its spawn point (the nearest `SpawnPoints` marker of its type: the monster does not leash, walk home and heal), on a clear straight walk, and outside every other spawn point's `aggroRangeCm` plus `avoidAggroMarginCm` (it does not pull a second monster); with no such direction, inside the leash only; else straight away. QA's no-entry (city) check is not ported. The summary counts back-offs and the legs of each kind (`backOffLegs.planned`, `.leashOnly`, `.straight`);
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
| `loadtest [--scenario ci-smoke] [--link <id>] [--bots <n>] [--duration-ms <ms>] [--behaviour <id>] [--db-suffix <s>] [--run-dir <dir>]` | The whole load test. It needs PostgreSQL and Redis (docker compose) and migrated databases (`DbMigrate -- --dev [--db-suffix <s>]`). It writes `summary.json`, `gameserver.log`/`.err`, `authapi.log`/`.err` and `telemetry/` into the run folder (default `artifacts/load/<time>-<scenario>`). Exit code 0 means every criterion passed. |
| `run [--scenario <id>] [--auth-url http://127.0.0.1:5080] [--game 127.0.0.1:7777] [--bots <n>] [--class <id>] [--link <id>] [--duration-ms <ms>]` | Bots against servers that are already running (for example the QA stack). The servers must share the ticket secret, and the Auth API must use `MMORPG_AUTH_IDENTITY=dev`. |
| `relay [--listen 7778] [--server 127.0.0.1:7777] [--link rtt100]` | The link simulator on its own, for the Unity client (below). |
| `report --telemetry <dir> [--from-ms <utc>] [--to-ms <utc>]` | Tick p50, p95, p99 and max from a telemetry folder. |
| `report --telemetry <dir> --kind economy [--from-ms <utc>] [--to-ms <utc>] [--data <tables>] [--world <dir>] [--json <file>]` | The economy dashboard (R-4.2-07, WP-M2-4, WP-M2-12; spec 12.5): gold created (faucets) and destroyed (sinks) per UTC hour and by reason, with the exp of each hour; units and gold per sold item and the sale gold per monster type (through the loot tables); gold destroyed by reason and the vendor goods bought; each item's sell multiplier (first, min, max, newest); the spawn delay and reward multipliers per monster type and region (`DemandUpdated`); exp per source (kills by monster type, run claims); the gold held online at the last `EconomyTotals`. Names come from `--data` and `--world` (default the repository's `data/tables` and `data/world/m1`; numbers without them). `--json` writes it all as JSON. A test reads a recorded sample (`tests/GameServer.Tests/Bots/Fixtures/economy-telemetry-sample`). |

Run them as `dotnet artifacts/bin/BotClient/debug/BotClient.dll <command> …`, or as `dotnet run --project src/Tools/BotClient -- <command> …`.

**Local 50-bot run (M1 exit criterion):**

```
powershell -ExecutionPolicy Bypass -File tools/load/Run-BotLoad.ps1 [-Link rtt100|rtt200] [-DbSuffix <s>]
```

It brings up docker compose, builds, migrates, and runs `loadtest --scenario m1-exit`. It prints the tick p50 and p99 at the end.

**M2 exit run (spec 12.2, WP-M2-12):**

```
powershell -ExecutionPolicy Bypass -File tools/load/Run-M2Exit.ps1 [-DbSuffix <s>] [-Link <id>] [-Bots <n>] [-DurationMs <ms>] [-Behaviour <id>]
```

`Run-BotLoad.ps1 -Scenario m2-exit`, then the level report: 5 new characters play the M2 loop until every one reaches level 30 (the run ends early then) or the time limit (4 h, a placeholder). It prints each bot's level per `levelSampleMs`, who reached 30 and when, the tick p99 and kills per bot-minute, then each bot's class, level, kills, deaths and loop rounds and the back-off legs. The scenario plays behaviour `m2-exit` (rounds of 12 kills, homes on the field camps only, planned back-off legs); `-Behaviour m2-loop` plays the one-kill rounds of before WP-M2-12 for a comparison (both scripts pass `-Behaviour` to `loadtest --behaviour`). Until the protocol carries the own level (WP-M2-3) it prints `level not in protocol yet` and the criterion `bots reached level 30` fails; use `-DurationMs 180000` for a short check.

**Attack chain comparison:** `loadtest --scenario m1-exit --behaviour field-combat-hit1` against the same run with `field-combat`; the summary has `killsPerBotMinute`, `chainPresses` and `chainHitsStarted` (hits 2 to 4 the server ran).

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
| `m2-loop` | 5 | 3 min | rtt100 | ≤ 100% | locally, a check of the M2 loop (`loadtest --scenario m2-loop`) |
| `m2-exit` | 5 | until level 30, at most 4 h | lan | ≤ 100% | locally, `tools/load/Run-M2Exit.ps1`; also every bot at `targetLevel` 30; behaviour `m2-exit` |

The loop runs in CI through the `BotBehaviourTests` scenario test in `dotnet test` (one warrior plays a round of `m2-loop` against the in-process server), so `bot-smoke` gains no time.

Every scenario also requires:
- all bots logged in and entered the world;
- no disconnects;
- at least `minInCombatPercent` of the bots reached combat (the server ran one of their attacks with a living monster in reach): 100% in CI, 90% for `m1-exit`, where 50 bots share the 5 monsters;
- no world crash, security event, dropped telemetry or malformed input, and no unreliable message the server refused as too large to send (`SendCounters`);
- no error lines from either server.

## The M2 loop (WP-M2-12)

A behaviour with `loopSteps` (row `m2-loop`) plays them in order, round after round (`Loop/BotLoop.cs`); without them the bot is the M1 brain alone (`field-combat`, CI).

| Step | File | Today | Made real by |
|---|---|---|---|
| `fight` | `Loop/Steps/FightStep.cs` | the brain's fight until `loopKillsPerRound` own kills, looting each kill as it goes (its drop is locked to it, R-4.3-02; WP-M2-12) | |
| `pickup` | `Loop/Steps/PickupStep.cs` | its own drops (R-4.3-02) until none seen for `loopPickupIdleMs`, at most `lootAfterKillMs` | |
| `sell` | `Loop/Steps/SellStep.cs` | walks to the nearest outpost NPC (its marker until the NPC is in interest), opens the sale (`OutpostInteractRequest` Sell: the visit counts), then per sell-only stack in the Normal bag (not stolen) `SellQuoteRequest` and `SellRequest` for the quoted total; `QuoteExpired` quotes again up to `loopSellQuoteRetries` times, then the stack is skipped. `sell.units`, `sell.gold`, `sell.quoteExpired`, `sell.skipped`; `sell.nothingToSell` without walking | WP-M2-4 |
| `buy` | `Loop/Steps/BuyStep.cs` | tops `loopBuyItemId` up to `loopBuyKeep` units at the nearest vendor stocking it, as far as the committed gold pays (one `VendorBuyRequest`, fixed price times count, R-4.2-01). `buy.units`, `buy.gold`; `buy.stocked`, `buy.noGold` without walking | WP-M2-4 |
| `run` | `Loop/Steps/RunStep.cs` | reads the run from the kept `RunStatus` (`context.Quests`, ClientCore's `ClientQuests`, sent at enter world): without a run it walks to the nearest outpost offering quests, opens the board (`OutpostInteractRequest` Quests) and accepts `loopRunSteps` kill offers (fewest kills first, at most `QuestCaps.maxStepsPerRun`; `QuestRunRequest` Accept with the board's epoch); the fights of the next rounds complete them (one kill counts for one step); a completed run is claimed (answered once committed). `run.accepted`, `run.steps`, `run.claimed`, `run.exp`; `run.active` at once; "not available" only without a `RunStatus` | WP-M2-6 (outpost quests and runs) |
| `learn` | `Loop/Steps/LearnStep.cs` | spends the unspent points (`ProgressionState`) one `SkillTreeRequest` at a time from the kept `SkillTreeState` (ClientCore `ClientSkillTree`, the shared `SkillTreeRules`): a plain node it can learn now, kit skills first (their default slots are the keys the brain presses), else one more level of the lowest learned hotbar skill; no evolutions. The server places a learned skill on its default slot; the predicted loadout is rebuilt from the state. `learn.skills`, `learn.upgrades`, `learn.refused.<reason>`; `learn.nothing` at once | WP-M2-5 |
| `allocate` | `Loop/Steps/AllocateStep.cs` | places the unspent free attribute points (`ProgressionState.attributePoints`, R-3.4-08) one `AttributeSpendRequest` at a time, out of combat: each point to the attribute of the class's `loopAttributeOrder` entry (`"warrior:str,sta"`; a tier-1 class uses its root's) with the fewest placed points, ties in list order; a class without an entry puts every point on its fastest-growing attribute (`ClassDef` growth). `allocate.points`, `allocate.refused.<reason>`; `allocate.nothing`, `allocate.inCombat` (the next round tries again) or `allocate.waiting` (no state yet) at once | WP-M2-13 |
| `rankUp` | `Loop/Steps/RankUpStep.cs` | asks the outpost (`OutpostInteractRequest` RankUp, R-3.6-04) once per rank and again only after a level-up since the last `RankRequirementsNotMet`: the requirements are achievement counters the server holds, not in `ProgressionState`. `rankUp.ranks`, `rankUp.notMet`; `rankUp.waiting` at once | WP-M2-3 |
| `equip` | `Loop/Steps/EquipStep.cs` | one piece per round from a bag that takes equipment to its lowest `EquipSlotDef` slot of the Equipped container (`InventoryMoveRequest`), when usable (`minLevel`, the class line's weapon types, R-3.5-07) and the slot is empty or holds a lower rarity tier (equal or higher stays); waits out the combat state (`combatStateMs` since the last own hit given or taken) and any running action first. `equip.items`, `equip.refused.<reason>`; `equip.nothing` at once | WP-M2-1 |
| own level | `World/OwnProgressReader.cs` | `ProgressionState` (protocol 18) | WP-M2-3 (own exp and level) |
| own gold | `World/OwnGoldReader.cs` | `GoldBalance` (protocol 17) | WP-M2-4 |

A step with nothing to do (nothing to sell, no gold, no NPC, no better gear) is done with a counter; "not available" is only for protocol that has not landed. A server refusal is counted as `<step>.refused.<reason>` and the step fails (the loop goes on).

**The step contract** (`Loop/IBotStep.cs`, `Loop/BotStepContext.cs`). A WP makes its step real by replacing that one file's body:
- `Begin(context)` when the step becomes current, then `Tick(context, ref command)` once per client tick. It returns `Running` (the command it filled is sent), `Done`, `NotAvailable` (`context.NotAvailable(id, why)` logs once per run) or `Failed`. Anything but `Running` hands over to the next step in the same tick and leaves the command alone. A step running past `loopStepTimeoutMs` is ended and counted as `timedOut`.
- **Reading the world** goes only through the context, which holds the Unity client's engine-free state (ClientCore), never server code: `context.Entities` (`EntityRegistry`: every entity of the snapshots with kind, def id and position samples; NPC entities included), `context.Inventory` (`ClientInventory`), `context.Outposts` (`ClientOutposts`: NPC definitions, outpost and vendor requests with their ids, the last answers, the committed gold), `context.Progression` (`ClientProgression`), `context.View` (predicted and server-confirmed position, meters, monsters, own drops, last own hit, action idle), `context.Own` (level, exp, gold), `context.Data` (tables, markers with npc `defId`s, grid). The world client keeps inventory, outposts and progression from the first message on, so a step sees what enter world sent before the loop existed (QA M2 wave 0 F2): read the kept state, not only `OnReliable`. `context.ApproachNpc(wanted, ref command, …)` walks to an NPC for its interaction range; `context.InCombat` is the client's view of the combat state.
- **Acting:** fill the tick's `BotCommand` (keys, facing, buttons; `context.Walk(goal, command)` walks the grid path), or send a reliable request with `context.SendReliable(ReliableMessages.Write(id, message))` (the server needs a `ReliableMessageCaps` row and an `InputRouter` route for the id, R-2.5-07). Results come back through `OnReliable(context, id, message)`, which every step gets for every reliable message (after the world client's own handling).
- **Progress:** `context.Count("sell.items", n)`; the loop itself counts `<step>.done`, `.notAvailable`, `.failed`, `.timedOut` and the rounds. All of it lands in `summary.json` under `loop` and per bot.
- **Own state:** an `IBotOwnReader` (`OnReliable` or `OnSnapshot` after the registry applied a snapshot) sets `BotOwnState.Level`/`Exp`/`Gold`; `OwnProgressReader` reads level and exp from `ProgressionState` (WP-M2-3), `OwnGoldReader` the gold from `GoldBalance` (WP-M2-4).
- Everything runs on the bots' simulation thread.

## Limits and TODOs
- **One LiteNetLib manager per bot** (a few threads each): fine for tens of bots. The ramp of spec 11.5 (100-500) needs a shared manager. TODO(J): BotClient v2. `NetSecurity.maxPeers` is 100 today.
- **The fight logic, the chain press and grid paths are ports** of the QA run's `QaFightPlan` and `QaNavigation`. TODO(J): one engine-free copy for both.
- **TODO(7.3) bot scoring:** behavioural scoring is an offline worker over telemetry (spec 7.3 layer 3, M5). BotClient accounts are dev logins in the range the run prints; real bots would never use the dev login.
- Character names are `Bot` plus random letters. Runs create accounts that nothing deletes, so use `-DbSuffix` for databases of your own.
