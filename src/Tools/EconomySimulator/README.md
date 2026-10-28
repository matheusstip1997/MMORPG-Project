# EconomySimulator (spec 12.5, WP-M2-12)

A seeded Monte Carlo over the M2 economy tables. It looks for **structural flaws only**: a demand loop that never settles, a counter that grows without bound, a faucet that keeps accelerating. It tunes nothing: every value it reads is a placeholder (spec 12.5, balance after M2-M4).

```
dotnet run --project src/Tools/EconomySimulator -- [--data data/tables] [--world data/world/m1] [--scenario <id>] [--seed <n>] [--check] [--report <file.json>]
```

- **`--scenario`** plays one `EconomySimScenario` row; the default plays every row.
- **`--seed`** replaces the rows' seeds. Replicate `i` of a row uses `seed + i`.
- **`--check`** prints the verdicts only (CI runs it after the table validation).
- **`--report`** writes everything as JSON.
- **Exit codes:** 0 no flaw, 1 a check failed, 2 bad arguments, tables or world.

The whole check takes about a second.

## What it plays
Each `EconomySimScenario` row (`data/tables/EconomySimScenario.json`, tools only) sets:
- players, hours, Monte Carlo runs and the seed;
- the kill rate per player (QA wave 1's measured 1.33 per minute);
- the hunted monster types (empty: every type that is not a boss);
- kills per outpost visit (12, the `m2-exit` bot's `loopKillsPerRound`);
- the vendor goods bought, the units kept and the units used per hour (an assumption: consumables do nothing until M3);
- the sample length, the settle window and the tolerance.

The model steps one simulated second at a time (`Simulation.cs`):
- **Spawn pools.** There is one pool per monster type and region, from `SpawnPoints` and the world's markers. Demons split between `m1.combat` and `field1.ring1`, as the server's demand counters do.
  - A kill needs a living monster in the player's pool.
  - The monster respawns after `monsterRespawnMs` (or `bossRespawnMs`) times its spawn delay percent at that moment. A shortened delay never goes below `minRespawnMs` (the `SpawnController` rule, `SpawnRules`).
  - So heavy hunting slows respawns: this is the feedback the loop must settle with.
- **Players** spread round robin over the hunted pools. Each one kills with the row's chance per second.
- **Loot.** Each kill rolls its `LootTableDef`/`LootTableEntry` with the shared `LootRoll`. Sell-only drops go to the player's bag.
- **Outpost visits** come every `killsPerVisit` kills. At each one:
  1. Every sell-only stack is sold with the shared `PriceQuote`: per unit, sequentially, against the item's sales counter, then counted as sold (R-4.2-02).
  2. The goods used since the last visit are taken out of stock.
  3. Each good is topped up to `buyKeep` at its `NpcVendorStock` price as far as the gold pays. The gold is destroyed (R-4.2-01).
- **Demand loop.** `DemandModel` runs every `DemandSettings.controlIntervalMs`. It is `DemandService.Step` written with the shared `DemandMath`: decay, ratio for the active players, the `spawnDelay` and `reward` curves, the clamps and the smoothing, and the price percent from the sales counters. `EconomySimulatorTests` checks it against `DemandService` step by step.

## What it prints
For each row:
- **One line per simulated hour:** gold created by sales (the faucet), gold destroyed by vendors (the sink), net, gold held and kills. Each is the mean over the runs, with the min and max where it matters.
- **Every multiplier per sample:** `price%` per sold item, `spawn%` and `reward%` per pool.
- **The verdicts:**
  1. **demand loop settles:** over the last `settleWindowPercent` of the time, every multiplier moves by at most `settleTolerancePercent` points.
  2. **counters bounded:** no kill or sales counter still climbs through the window (second half against first half). For example, `killRetentionPermille` 1000 fails here.
  3. **faucet settles:** gold created per hour in the window stays within the tolerance of its mean.
  4. **sink cover (WARN only in M2):** the share of the faucet the vendors destroy and the net gold per player-hour. The vendors are the only sink until fees (M4) and consumable use (M3), so this never fails the check.

## Files
| File | What it holds |
|---|---|
| `SimEconomy.cs` | the tables in the simulator's form: pools, loot, prices, demand settings, vendor goods |
| `DemandModel.cs` | the demand loop |
| `Simulation.cs` | one seeded run, plus `SpawnRules` |
| `ScenarioResult.cs` | runs summed up per hour and per sample, and the verdicts |
| `SettleCheck.cs` | the pure checks |
| `SimRunner.cs` | plays the runs and prints the text report |
| `Program.cs` | the command line |
