# EconomySimulator (spec 12.5, WP-M2-12, WP-M3-12)

A seeded Monte Carlo over the economy tables of M2 and M3. It looks for **structural flaws only**: a demand loop that never settles, a counter that grows without bound, a faucet that keeps accelerating. It tunes nothing: every value it reads is a placeholder (spec 12.5, balance after M2-M4).

```
dotnet run --project src/Tools/EconomySimulator -- [--data data/tables] [--world data/world/m1] [--scenario <id>] [--seed <n>] [--check] [--report <file.json>]
```

- **`--scenario`** plays one `EconomySimScenario` row; the default plays every row.
- **`--seed`** replaces the rows' seeds. Replicate `i` of a row uses `seed + i`.
- **`--check`** prints the verdicts only (CI runs it after the table validation).
- **`--report`** writes everything as JSON.
- **Exit codes:** 0 no flaw, 1 a check failed, 2 bad arguments, tables or world.

The whole check takes about two seconds.

## What it plays
Each `EconomySimScenario` row (`data/tables/EconomySimScenario.json`, tools only) sets:
- players, hours, Monte Carlo runs and the seed;
- the kill rate per player (QA wave 1's measured 1.33 per minute);
- the hunted monster types (empty: every type that is not a boss);
- kills per outpost visit (12, the `m2-exit` bot's `loopKillsPerRound`);
- the vendor goods bought, the units kept and the units used per hour (an assumption; from M3 it is capped per good by what its `ConsumableDef` allows: the toxicity or satiety decay over its cost, and its cooldown, through the shared `ConsumableRules.MaxUsesPerHour`; a good without a row is never used);
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
  2. The goods used since the last visit are taken out of stock (never more than the player holds) and counted.
  3. Each good is topped up to `buyKeep` at its `NpcVendorStock` price as far as the gold pays. The gold is destroyed (R-4.2-01).
- **Demand loop.** `DemandModel` runs every `DemandSettings.controlIntervalMs`. It is `DemandService.Step` written with the shared `DemandMath`: decay, ratio for the active players, the `spawnDelay` and `reward` curves, the clamps and the smoothing, and the price percent from the sales counters. `EconomySimulatorTests` checks it against `DemandService` step by step.

### The M3 activity (WP-M3-12, spec 12.5, 10.2, R-4.2-04)
A row with M3 fields (`harvestsPerPlayerHour`, `nodeIds`, `jobLevel`, `oneTimeBuyItemIds`, `conversionsPerPlayerHour`, `enchantTriesPerPlayerHour`, `socketsPerPlayerHour`, `gemRemovalsPerPlayerHour`; rows `m3-15` and `m3-300`) also plays the sinks below. The rates and the job level are the tool's assumptions; every odd, cost, cap and yield is the game's table. The M3 rolls come from a stream of their own, so a row without these fields gives the numbers it always gave for its seed, and a row's kills, loot and sales do not depend on them.

- **Nodes.** One pool per node type and region, from `GatherNodes`, the markers and `NodeDef`. A player tries a harvest with the row's chance per second, at its turn of the pools (players walk between the nodes). A depleted pool loses the try. The yield is the shared `HarvestMath` over `NodeYieldEntry`, with the job level's bonus and the repeats of the player's own window. One harvest depletes a node; it comes back after `RespawnMath.ScaledDelay` at the demand percent. The node counters are `DemandService`'s (`GatherSettings`), checked step by step.
- **Materials.** Loot of category material and harvest yields go to a material bag. Each outpost visit spends the time since the last one, as far as the bag allows.

| Sink (`M3Names`) | Unit | Tables and math |
|---|---|---|
| `vendorToolsAndMolds` | gold | `NpcVendorStock` for `oneTimeBuyItemIds`, once per character (tools never wear out, R-5.2-02; molds are reusable, R-5.3-01), so it falls to zero |
| `consumableUse` | units | as before: `usedPerPlayerHour`, capped by `ConsumableRules.MaxUsesPerHour` and what is held |
| `failedConversions` | units | `RecipeDef` convert rows, `RecipeInputDef`, `ConversionOdds` and its `dailyCap` per tier and UTC day, through `ForgeRolls.Draw` |
| `lostEnchantStones` | units | `EnchantOdds`, `EnchantMaterial` tier 1, `EnchantMath`; every try consumes them; an item at +9 is replaced by a new one at +0 |
| `failedSockets` | units | `GemSocketOdds`, `GemMath`; rough gems are cut first (`RecipeDef` refine rows whose output has a `GemDef`) |
| `gemRemovalCost` | units | `GemSocketOdds.removalCostItemId`/`Count`, `GemMath`; the gem returns intact |

Simplifications: no job tree bonus (the Monte Carlos below report the caps); a player holds the jobs and higher-tier tools of what its row plays (a Blacksmith with a Jeweler secondary); the forge's own use of materials is not played, since forged gear is not destroyed (R-4.2-04).

## What it prints
For each row:
- **One line per simulated hour:** gold created by sales (the faucet), gold destroyed by vendors (the sink), net, gold held and kills. Each is the mean over the runs, with the min and max where it matters.
- **Every multiplier per sample:** `price%` per sold item, `spawn%` and `reward%` per pool.
- **The verdicts:**
  1. **demand loop settles:** over the last `settleWindowPercent` of the time, every multiplier moves by at most `settleTolerancePercent` points.
  2. **counters bounded:** no kill or sales counter still climbs through the window (second half against first half). For example, `killRetentionPermille` 1000 fails here.
  3. **faucet settles:** gold created per hour in the window stays within the tolerance of its mean.
  4. **sink cover (WARN only):** the share of the faucet the vendors destroy (tools and molds included on an M3 row), the vendor goods used per player-hour and what they had cost (consumable use, WP-M3-3), and the net gold per player-hour. The vendors are the only gold sink until fees (M4), so this never fails the check.
  5. On an M3 row: **node demand loop settles** (every node multiplier within the tolerance over the window, no harvest counter climbing, the harvest units per hour within the tolerance of their mean); **M3 sinks reached** (a sink whose rate is above zero and that never moved fails: a sink that can never run balances nothing; the line holds each sink's total and per player-hour); **item sink cover** (WARN only: the share of the material units gathered and dropped that the item sinks destroy).
  - An M3 row's report also prints each sink and flow per simulated hour, and the material units held at the end.
- **The enchant ladder (WP-M3-8, spec 5.3 R-5.3-04):** once for the whole run (not per scenario), `EnchantMonteCarlo.Trials` seeded trials enchant one item from +0 until it first reaches +9 with the `EnchantOdds` weights (the shared `EnchantMath`, as the server rolls them). `EnchantOdds` is one table for every job; what differs per job is the bonus its own tree reaches. So there is a ladder without the bonus, one per bonus a job's tree reaches (`JobTree` capped by `JobBonusCap`: blacksmith and jeweler both 5 pp at the placeholders) and one at the cap. It prints the mean, p50, p95 and max tries and the mean stones (tier 1 `EnchantMaterial`) to each level, and the stones to +9 per item tier. Every stone is consumed whatever the outcome, so all of them are an item sink (R-4.2-04). Verdict **enchant ladder bounded:** every trial reaches +9 within `GuardAttempts` tries (a level that cannot rise fails it).
- **The conversion arbitrage (WP-M3-12, spec 10.2, R-5.2-04):** once for the whole run. Can a lower tier bought and converted beat the higher tier gathered? For every convert chain of `RecipeDef` (ore and the three gem kinds) and every range (T1 to T2, T2 to T3, T1 to T3), `ConversionArbitrage.Trials` seeded trials with the server's own `ForgeRolls.Draw` count the inputs one output consumes, without the tree and at the `conversionOddsPercent` and `keepInputsPercent` caps (clamped by `maxConversionPercent`). It prints the inputs (mean, the closed form `ExpectedInputs`, p50, p95), the days at `dailyCap` and the cost both ways.
  - **The cost unit is gathering labour**, because **no material has a price in M3**: no vendor sells one and contracts are M4 (`TODO(open)`). One harvest takes the longer of the harvest action (`ActionDef.TotalMs`) and `respawnMs` over the nodes of that type, over the units a harvest yields at the row's job level (`HarvestMath`, no repeats). A cut gem costs its rough gem's labour. A bought input is priced at the labour it embodies. The gold figures value labour at the first M3 row's gold per player-hour. If a vendor ever sells the input, its real `NpcVendorStock` price is used and the routes are compared in gold.
  - **Verdict conversion arbitrage:** FAIL when a route into the top tier costs less than gathering it, at either bonus level. That is the question of R-5.2-04 and the plan ("can T1 bought into T3 beat T3 gathered?"). The break-even is the share of its labour the input would have to sell at.
  - **Verdict conversion legs below the top tier:** WARN only, when a route into a middle tier wins.
  - Limits: walking is not counted, and monster drops feed both routes and are left out.
- **No M3 gold faucet (plan section 1, R-4.1-01):** no node yield, recipe output or mold output has a `PriceBase` row. FAIL otherwise.

## Files
| File | What it holds |
|---|---|
| `SimEconomy.cs` | the tables in the simulator's form: pools, loot, prices, demand settings, vendor goods |
| `DemandModel.cs` | the demand loop |
| `Simulation.cs` | one seeded run, plus `SpawnRules` |
| `ScenarioResult.cs` | runs summed up per hour and per sample, and the verdicts |
| `SettleCheck.cs` | the pure checks |
| `SimRunner.cs` | plays the runs and prints the text report |
| `SimCrafting.cs` | the M3 sinks' tables and activity, and their names (WP-M3-12) |
| `EnchantMonteCarlo.cs` | the enchant ladder (WP-M3-8): tries and stones to each level, and its verdict |
| `ConversionArbitrage.cs` | the conversion arbitrage (WP-M3-12) and the no gold faucet check |
| `Program.cs` | the command line |
