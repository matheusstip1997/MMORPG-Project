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
| `loadtest [--scenario ci-smoke] [--link <id>] [--bots <n>] [--duration-ms <ms>] [--behaviour <id>] [--db-suffix <s>] [--run-dir <dir>] [--seed-level <n>]` | The whole load test. `--seed-level` sets each new character's level before the bots enter (the scenario's `seedLevel`; "The M3 loop and the M3 exit run" below). It needs PostgreSQL and Redis (docker compose) and migrated databases (`DbMigrate -- --dev [--db-suffix <s>]`). It writes `summary.json`, `gameserver.log`/`.err`, `authapi.log`/`.err` and `telemetry/` into the run folder (default `artifacts/load/<time>-<scenario>`). Exit code 0 means every criterion passed. |
| `run [--scenario <id>] [--auth-url http://127.0.0.1:5080] [--game 127.0.0.1:7777] [--bots <n>] [--class <id>] [--link <id>] [--duration-ms <ms>]` | Bots against servers that are already running (for example the QA stack). The servers must share the ticket secret, and the Auth API must use `MMORPG_AUTH_IDENTITY=dev`. |
| `relay [--listen 7778] [--server 127.0.0.1:7777] [--link rtt100]` | The link simulator on its own, for the Unity client (below). |
| `report --telemetry <dir> [--from-ms <utc>] [--to-ms <utc>]` | Tick p50, p95, p99 and max from a telemetry folder. |
| `report --telemetry <dir> --kind economy [--from-ms <utc>] [--to-ms <utc>] [--data <tables>] [--world <dir>] [--json <file>]` | The economy dashboard (R-4.2-07, WP-M2-4, WP-M2-12; spec 12.5): gold created (faucets) and destroyed (sinks) per UTC hour and by reason, with the exp of each hour; units and gold per sold item and the sale gold per monster type (through the loot tables); gold destroyed by reason and the vendor goods bought; each item's sell multiplier (first, min, max, newest); the spawn delay and reward multipliers per monster type and region (`DemandUpdated`); exp per source (kills by monster type, run claims); the gold held online at the last `EconomyTotals`. Names come from `--data` and `--world` (default the repository's `data/tables` and `data/world/m1`; numbers without them). `--json` writes it all as JSON. Items created and destroyed by reason, and the M3 section (WP-M3-12): items by M3 reason, job XP per activity and job, harvests per node type, always printed (zeros until the M3 WPs write them; "M3 telemetry" below). A test reads a recorded sample (`tests/GameServer.Tests/Bots/Fixtures/economy-telemetry-sample`). |

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
| `m3-loop` | 5 | 3 min | rtt100 | ≤ 100% | locally, a check of the M3 loop (`loadtest --scenario m3-loop`) |
| `m3-exit` | 2 | until both scripts finished, at most 20 min | lan | ≤ 100% | locally, `tools/load/Run-M3Exit.ps1`; level seed 12, scripts `m3-exit-blacksmith` and `m3-exit-jeweler`, every entry done, 50% in combat |

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
| `useItem` | `Loop/Steps/UseItemStep.cs` | below `loopDrinkBelowPercent` of its max HP (0: never) drinks one HP potion it holds (`loopBuyItemId` when it heals, else the lowest consumable that heals HP): once its own action is idle, one `UseItemRequest` through ClientCore's `ClientConsumables` (`context.Consumables`), the drink played in the predictor (`context.PlayAction`), done when the use is answered (after its commit). `useItem.used`, `useItem.refused.<reason>`; `useItem.off`, `useItem.notNeeded`, `useItem.none`, `useItem.skipped.<reason>` at once | WP-M3-3 (consumable use) |
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

**The step contract** (arguments, scripts, statuses, reading the world through ClientCore, reporting, the test every step needs, the M3 steps and who fills each) is in [`Loop/Steps/README.md`](Loop/Steps/README.md).

## The M3 loop and the M3 exit run (WP-M3-12)

- **`m3-loop`** (scenario and behaviour): the M2 loop with the M3 steps mixed in (`useItem` after the fight, `buy:m3.pickaxeT1` before the potions' `buy` so the round's gold pays the tool first, `takeJob`, `gather`, `refine`, `convert`, `craft`, `enchant`, `socket`), for ongoing load. The M3 steps are placeholders that report "not yet" until their WPs land; a new character plays the M2 steps up to the job level. `loadtest --scenario m3-loop [--seed-level 12]` (`--seed-level` plays the M3 steps at once).
- **`m3-exit`**: the exit criterion of `docs/plans/m3-jobs-crafting-plan.md` section 1 as two scripts (`BotBehaviour.loopOnce`), one per bot (`BotScenario.botBehaviourIds`):
  - bot 0, `m3-exit-blacksmith` (warrior): `level:12`, `takeJob:blacksmith`, `fight:12`, `pickup`, `sell` (the gold; three rounds of `fight:12`, `pickup`, `sell`), `buy:m3.pickaxeT1`, `gather:6` (six harvests: the 4 ore of the two refines with a margin for the gem and stone rolls; `BotTablesTests` checks the count against the yield and recipe tables), `fight:13@field1.spawn.wolf.a1` and `pickup` (a wolf sinew for the grip: a camp entry counts only kills of its `SpawnPoints` monster; 13 kills miss the 30 % sinew drop under 1 %. The cyclops hide of decision 6 stays the T1 grip in the tables, but two level-12 bots do not kill the level-30 boss: the QA and WP-M3-12 runs' "done" on a boss entry were stray kills near its camp), `refine`, `refine`, `craft`, `learnJob:blacksmith`, `equip:crafted`, `fight`, `enchant`, `buy:m2.potionHpSmall`, `useItem:m2.potionHpSmall` (drinks it at once, WP-M3-3);
  - bot 1, `m3-exit-jeweler` (mage): `level:12`, `takeJob:jeweler`, `refine` and `socket` ("not yet" until WP-M3-9 adds the Jeweler's recipes and the socket protocol), `learnJob:jeweler`.
- **The level seed** (`BotScenario.seedLevel` 12, `loadtest --seed-level`): before the bots enter, the load test creates each character through the Auth API (no world entry) and runs the built `DbMigrate --dev [--db-suffix <s>] --set-level <name> <level>` (output in the run folder's `seed.log`). Dev databases only: refused with `--pg`.
- **The result** per bot and entry: done, not yet (with the reason: what has not landed and which WP lands it), failed, timed out. `summary.json` has `botsDetail[].script` and the totals `scripts`; the criterion "every script step done" (`requireStepsDone`) passes only when every entry is done. The run ends once both scripts finished (at most 20 minutes, a placeholder).

```
powershell -ExecutionPolicy Bypass -File tools/load/Run-M3Exit.ps1 [-DbSuffix <s>] [-Link <id>] [-DurationMs <ms>] [-NoDocker]
```

It runs `Run-BotLoad.ps1 -Scenario m3-exit`, prints every entry with its outcome, then the M3 section of `report --kind economy` on the run's telemetry (also written to `economy.json` in the run folder). With the play stack up pass `-NoDocker` (the load test uses free ports, so the composed servers keep running).

## M3 telemetry the economy report reads (fixed names; WP-M3-12)

The M3 section of `report --kind economy` always prints these counters, at zero until the WPs write the records. A WP writes exactly these kinds and field names (`GameServer/Telemetry/TelemetrySchema.cs`):

| Kind | Written by | Fields | The report |
|---|---|---|---|
| `ItemCreated`, `ItemDestroyed` (exist) | `InventoryService` | `item`, `rarity`, `count`, `reason` (`InventoryReason`) | units created and destroyed per reason name |
| `JobXpGained` (new) | WP-M3-1 | subject `entity`, `amount`, `job` (`JobDef.netId`), `activity` (1 gather, 2 refine, 3 craft, 4 convert, 5 enchant, 6 socket; 0 other), V3 free | job XP and grants per activity, XP per job |
| `Harvested` (new) | WP-M3-5 | subject `entity`, `nodeDef` (`NodeDef.netId`), `item`, `count`, `seed`; region in `r` | harvests and units per node type |

- **M3 item reasons.** Each WP appends its `InventoryReason` member with this camelCase name and appends the name to `EconomyReport.InventoryReasonNames` in the same change (`EconomyReportM3Tests` compares the two lists): `jobStart` (WP-M3-1, the starter molds), `itemUsed` (WP-M3-3), `harvest` (WP-M3-5), `refine`, `craft`, `convert` (inputs consumed and outputs created), `convertFailed` (the inputs a failed conversion consumes; WP-M3-6), `enchant` (the stones, WP-M3-8), `gemSocket` (the gem, socketed or lost), `gemRemove` (the gem returned; WP-M3-9). The values are taken at merge time.
- **Job activity values** are a proposal for WP-M3-1's activity enum; if it numbers them differently it updates `EconomyReport.JobActivityNames` in the same PR. Job and node names print as numbers until `EconomyNames` learns `JobDef` and `NodeDef` (the WPs that add the tables).

## Limits and TODOs
- **One LiteNetLib manager per bot** (a few threads each): fine for tens of bots. The ramp of spec 11.5 (100-500) needs a shared manager. TODO(J): BotClient v2. `NetSecurity.maxPeers` is 100 today.
- **The fight logic, the chain press and grid paths are ports** of the QA run's `QaFightPlan` and `QaNavigation`. TODO(J): one engine-free copy for both.
- **TODO(7.3) bot scoring:** behavioural scoring is an offline worker over telemetry (spec 7.3 layer 3, M5). BotClient accounts are dev logins in the range the run prints; real bots would never use the dev login.
- Character names are `Bot` plus random letters. Runs create accounts that nothing deletes, so use `-DbSuffix` for databases of your own.
