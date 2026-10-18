# 8. Cross-System Reference

## 8.1 Event catalog
| Event | Emitted by | Consumed by |
|---|---|---|
| `SessionStarted` / `SessionEnded` | Net | Party, Guilds, Law (linger), Telemetry |
| `Moved` (sampled) | Net | Telemetry, bot scoring |
| `ActionStarted`, `HitLanded`, `Damaged` | Combat | Gauges, Poise, Telemetry |
| `Killed` | Combat | Exp, Achievements, Quests, Demand, Loot, Honor, Reputation |
| `Died` | Combat | Loot (bag), Law (respawn lock, arrest window), Telemetry |
| `Staggered`, `Flinched`, `ParryAttempted` | Combat | Telemetry |
| `StatsChanged` | Stats | Visibility, UI sync |
| `LevelUp`, `RankChanged`, `AchievementDone` | Progression | Skills (points), Achievements, Telemetry |
| `ClassAdvanced`, `SkillLearned`, `SkillEvolved`, `Respecced` | Skills | Stats, Hotbar |
| `Harvested`, `Crafted`, `Refined`, `Converted`, `Enchanted`, `GemSocketed` | Crafting/Gathering | Jobs, Achievements, Demand, Portfolio, Quests, Honor (crafts with stolen inputs) |
| `ItemCreated`, `ItemTransferred`, `ItemDestroyed`, `GoldChanged` | Inventory | Telemetry, Audit, bot scoring |
| `ItemSold`, `PriceChanged`, `DemandUpdated` | Economy | Demand, Quest board, Telemetry |
| `FeeCharged`, `GoldDestroyed` | FeeService / SinkLedger | Guild treasury, Telemetry |
| `LootDropped`, `LootRolled`, `LootBagCreated`, `LootBagPickedUp`, `LootBagExpired` | Loot | Honor, Stolen mark, Telemetry |
| `ItemStolen`, `ItemLaundered`, `ItemReturned`, `BlackMarketSale` | Items | Honor, Achievements, Telemetry |
| `QuestStepDone`, `QuestForfeited`, `RunClaimed` | Quests | Exp, Achievements, Telemetry |
| `ContractCompleted`, `ContractExpired` | Contracts | Portfolio, Telemetry |
| `Flagged`, `HonorChanged` | Law | UI, Guards, Black market gate |
| `Detected`, `Revealed`, `Disguised`, `DisguiseBroken` | Visibility | Guards, Monster aggro, Telemetry |
| `ArrestStarted`, `ArrestCompleted`, `Sentenced`, `Released` | Law | Honor, Inventory, Telemetry |
| `EnteredRegion`, `LeftRegion`, `InstanceCreated`, `InstanceClosed` | World | Law, Guilds, Economy |
| `SiegeScheduled`, `SiegeResolved`, `TerritoryChanged`, `ReputationChanged` | Guilds | FeeService, Guards, Telemetry |
| `PartyChanged`, `GroupFormed` | Social | Quests, Loot, PvpRules |
| `EffectApplied`, `EffectRemoved`, `EffectCleansed` | Status system | HUD sync, Telemetry |
| `BuildupTriggered` | Build-ups | Status system, HUD sync, Telemetry |
| `RaidQueued`, `RaidStarted`, `RaidCompleted` | Raids | Achievements, Telemetry |
| `ArenaQueued`, `MatchFound`, `RoundEnded`, `MatchEnded`, `SeasonClosed` | Arena | Rating, Unlocks, Telemetry, abuse scoring |

## 8.2 Transaction catalog (all atomic, idempotent, audited)
| Operation | Owner service | Atomic steps |
|---|---|---|
| Sell at outpost | PriceService + Inventory | remove items, add gold, register sales (class 1) |
| NPC vendor purchase | Inventory | remove gold (destroyed), add item (class 1) |
| Trade | Inventory | validate both sides, swap items and gold |
| Auction/shop sale | Inventory + FeeService | escrow item, pay seller minus fee, deliver item |
| Craft / refine / convert / enchant | ForgeEngine + Inventory | consume reserved materials, create or modify item, log seed |
| Gem socket | GemService + Inventory | consume gem, update item, recompute stats |
| Contract accept / complete | Contracts + Escrow | lock customer assets, deliver output, pay crafter minus fee |
| Run claim | RunService | step bonus, forfeit penalty, exp per member by contribution (class 2) |
| Death drop | Loot + Inventory | move Normal bag into persisted loot bag |
| Loot pickup / return | Loot + Honor | move items, set/clear stolen mark, honor change |
| Laundering | Fence + Inventory + Honor | charge fee, clear mark, lower honor |
| Sentence | SentencingService | honor reset, confiscate, jail, audit |
| Release | JailService | return items (stolen to original owner by mail) |
| Skill learn / evolve / respec | SkillTree | check, adjust nodes and points, recompute stats |
| Class advance | Skills | change class, unlock tree, remap skills |
| Instance entry/exit | WorldManager | remove, validate cap, add, persist location |
| Guild feature purchase | Treasury | debit restricted ledger, destroy gold, grant feature |
| Siege resolve | SiegeService | ownership change or treasury theft, cooldown |
| Create/delete character | Accounts | slot check, name uniqueness, soft delete |
| Arena match end | Arena + Rating | update ratings, apply penalties, return players |
| Season close | SeasonService + Unlocks | rank rewards and skins granted once (idempotent) |

## 8.3 How the systems talk
Pattern: **commands** go through service interfaces and return results; **facts** are published as events.
| System | Calls (commands) | Emits / consumes |
|---|---|---|
| Net / Session | Persistence, WorldManager, Sentencing | emits Session events |
| World / Interest | Visibility, Regions | emits region and instance events |
| Combat | Stats, Skills, PvpRules, Visibility | emits Damaged, Killed, Died; consumes nothing from gameplay |
| Stats | none (data) | emits StatsChanged; consumes skill/gear/buff changes |
| Progression | Skills, Inventory (rewards none) | consumes Killed, quest, craft events |
| Items / Inventory | Persistence | emits item and gold events; called by all value systems |
| Economy | Inventory, SpawnController | consumes Killed, Harvested; emits price/demand events |
| Crafting / Gathering | Inventory, Stats, StolenMark | emits Crafted, Harvested... |
| Contracts | Escrow, Inventory, FeeService, ForgeEngine | consumes Crafted for portfolio |
| Quests / Runs | Inventory, Exp, Party | consumes Killed, deliveries |
| Law (PvP/Honor/Arrest) | Inventory, WorldManager, Honor | consumes Killed, Died, Detected, item events |
| Guilds | FeeService, WorldManager, Treasury | consumes FeeCharged, crimes, region events |
| Social | WorldManager, Mail | emits PartyChanged |
| Arena | WorldManager, Party, StatBlock, Unlocks | emits match and season events |
| Telemetry | none | consumes everything |

```
Gameplay input -> Combat -> Killed/Died ------------------------> EventBus
   EventBus -> Exp/Achievements/Quests/Demand/Loot/Honor/Reputation/Telemetry
   Loot/Quests/Crafting/Contracts/Trade --calls--> InventoryService / EscrowService / FeeService
   Law --calls--> SentencingService -> Inventory + World + Honor
   Guilds --calls--> Treasury, WorldManager(sieges)
```

## 8.4 Region flags
See the table in section 2.3; flags are consumed by PvpRules, Visibility, death handling, mounts, FeeService, GuardNpcAi, Territory.

## 8.5 Config table catalog
| Domain | Tables |
|---|---|
| Combat | `ActionDef`, `ActionEffectDef`, `ActionBindings` (M1 placeholder until class kits), `HitShapeDef`, `ProjectileDef`, `PoiseDef`, `ParryDef`, `ParryColors`, `DamageConstants`, `RankFactor`, `ElementCaps` |
| Stats / classes | `StatDef`, `ClassTree`, `ClassDef`, `GaugeDef`, `SkillDef`, `SkillTree`, `SkillEvolution`, `PassiveTriggers` |
| Progression | `ExpCurve`, `RankModifier`, `LevelPenalty`, `RankRequirements`, `AchievementDef` |
| Items / loot | `ItemDef`, `ContainerDef`, `LootTables`, `RarityDef`, `PartyLootParams`, `OwnerWindows`, `BagExpiry` |
| Economy | `PriceBase`, `RarityMult`, `PriceFloorRecovery`, `DemandCurve`, `SpawnPoints`, `MonsterDef`, `FeeRates` (default and bounds), `BlackMarket` |
| Crafting / jobs | `MoldDef`, `RecipeDef`, `QualityThresholds`, `QualityCaps`, `EnchantOdds`, `ConversionOdds`, `GatherNodes`, `ToolDef`, `JobDef`, `JobTree`, `JobXpWeights` |
| Quests | `OutpostQuestDef`, `RunBonus`, `ForfeitPenalty`, `QuestCaps` |
| Law | `HonorWeights`, `HonorTiers`, `FlagWindow`, `JailFormula`, `ArrestWindow`, `GuardNpcTiers`, `PerPairLimits` |
| Guilds | `GuildCreateCost`, `SiegeWindows`, `SiegeCaps`, `SiegeCooldowns`, `TreasuryTheftCap`, `ReputationWeights`, `GuardHireCosts`, `GuildFeatureCosts` |
| Status / raids | `StatusEffectDef`, `ImmunityTags`, `BuildupDef`, `RaidDef`, `RaidScaling` (by rank tier), `BossDef` |
| Arena | `ArenaTemplates`, `ArenaClassTemplates`, `ArenaRuleset`, `LivesPerMode`, `PrepTime`, `RoundsToWin`, `RatingParams`, `QueuePenalties`, `SeasonDef`, `SeasonRewards` |
| Ranks, network, persistence | `RankLevelRanges`, `ExpBandCurves`, `LagCompensation`, `NetSecurity` (limits, key IDs), `PersistenceIntervals`, `RegionDefaults`, `NpcVendorStock`, `StartingValuesM1` |
| UI | `HudLayout`, `HudThemeTokens`, `HudModes`, `BuffIconRegistry`, `ReasonCodeStrings`, `Hotkeys`, `ClientCamera` |
| Client prediction | `ClientPrediction` (correction tolerance, snap and smoothing, pending-input cap, catch-up ticks, interpolation clock) |
| World / ops | `RegionDef`, `LingerRules`, `QueueCaps`, `TickRates`, `InterestRadius`, `VisibilityBands`, `EventBusLimits`, `TelemetrySettings` |
