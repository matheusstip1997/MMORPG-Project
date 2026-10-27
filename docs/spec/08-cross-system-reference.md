# 8. Cross-System Reference

## 8.1 Event catalog
| Event | Emitted by | Consumed by |
|---|---|---|
| `SessionStarted` / `SessionEnded` | Net | Party, Guilds, Law (linger), Telemetry |
| `Moved` (sampled) | Net | Telemetry, bot scoring |
| `ActionStarted`, `HitLanded`, `Damaged` | Combat | Gauges, Poise, Telemetry |
| `Killed` | Combat | Exp, Achievements, Quests, Demand, Loot (M1: `LootService`; the event carries the death position), Honor, Reputation |
| `Died` | Combat | Loot (bag), Law (respawn lock, arrest window), Telemetry |
| `Respawned` | Combat (phase 8, player respawn) | Telemetry (TODO: Law respawn lock, R-6.3-03) |
| `Staggered`, `Flinched`, `ParryAttempted` | Combat | Telemetry |
| `ProjectileSpawned`, `ProjectileHit` | Combat (ProjectileSystem) | none yet (TODO: telemetry) |
| `StatsChanged` | Stats | Visibility, UI sync |
| `LevelUp`, `RankChanged`, `AchievementDone` | Progression | Skills (points), Achievements, Constellar (star points, M6), Telemetry. M2 (WP-M2-3): `LevelUp {characterId, entityId, fromLevel, toLevel}` (several levels of one gain in one event; Stats recompute, WP-M2-2), `RankChanged {characterId, entityId, from, to}` (published only after the rank-up's save commits); `AchievementDone` not yet (counters only) |
| `ExpGained` | Progression (`ExpService`, WP-M2-3) | Telemetry: `{characterId, entityId, amount, victimDefId, rankPercent, bandPpm}`, every kill that pays exp (amount 0 possible: no exp floor) |
| `OutpostVisited`, `RunClaimed` | Outposts (WP-M2-4), Quests (WP-M2-6) | Achievements (`AchievementService.Record`: outposts visited by outpost id, runs claimed); names agreed in wave 0, consumers wired by the second WP to merge |
| `StarPointsGained`, `ConstellarChanged` | Constellar (section 14.1, M6) | Stats, Telemetry |
| `CardEarned`, `CardEvolved`, `BookChanged` | Bystial book (section 14.2, M2) | Stats, Achievements, Telemetry |
| `ClassAdvanced`, `SkillLearned`, `SkillEvolved`, `Respecced` | Skills | Stats, Hotbar |
| `Harvested`, `Crafted`, `Refined`, `Converted`, `Enchanted`, `GemSocketed` | Crafting/Gathering | Jobs, Achievements, Demand, Portfolio, Quests, Honor (crafts with stolen inputs) |
| `ItemCreated`, `ItemTransferred`, `ItemDestroyed`, `GoldChanged` | Inventory (M1: `ItemCreated`, `ItemDestroyed`, published after the save commits; M2: `GoldChanged` too, WP-M2-4) | Telemetry, Audit, EconomyLedger, bot scoring |
| `EquipmentChanged` (`characterId`, `slot` = `EquipSlotDef.code`) | Inventory (WP-M2-1: after the save that changed the committed equipped set, one per changed slot) | Stats (gear modifiers through `IEquipmentStatSource`, WP-M2-2), Telemetry |
| `ItemSold`, `PriceChanged`, `DemandUpdated` | Economy (M2, WP-M2-4: `ItemSold` by `InventoryService` after the sale committed; `DemandUpdated` per kill counter and `PriceChanged` per item by `DemandService` each control step) | Demand, Quest board, Telemetry (Achievements do not consume `ItemSold` in v0, WP-M2-3) |
| `OutpostVisited` | Outposts (M2, WP-M2-4: the first successful interaction with an outpost NPC in a visit, `EconomySettings.visitGapMs`) | Achievements ("outposts visited", each outpost once by its npc marker id, WP-M2-3), Telemetry |
| `FeeCharged`, `GoldDestroyed` | FeeService / SinkLedger (M2: `GoldDestroyed` by `InventoryService` after a vendor purchase committed) | Guild treasury, EconomyLedger, Telemetry |
| `LootDropped`, `LootRolled`, `LootBagCreated`, `LootBagPickedUp`, `LootBagExpired` | Loot | Honor, Stolen mark, Telemetry |
| `LootPickedUp`, `LootDropExpired` | Loot (ground drops, M1) | Telemetry |
| `ItemStolen`, `ItemLaundered`, `ItemReturned`, `BlackMarketSale` | Items | Honor, Achievements, Telemetry |
| `QuestStepDone`, `QuestForfeited`, `RunClaimed` | Quests | Exp, Achievements, Telemetry |
| `ContractCompleted`, `ContractExpired` | Contracts | Portfolio, Telemetry |
| `Flagged`, `HonorChanged` | Law | UI, Guards, Black market gate |
| `Detected`, `Revealed`, `Disguised`, `DisguiseBroken` | Visibility | Guards, Monster aggro, Telemetry |
| `ArrestStarted`, `ArrestCompleted`, `Sentenced`, `Released` | Law | Honor, Inventory, Telemetry |
| `EnteredRegion`, `LeftRegion`, `InstanceCreated`, `InstanceClosed` | World | Law, Guilds, Economy |
| `SiegeScheduled`, `SiegeResolved`, `TerritoryChanged`, `ReputationChanged` | Guilds | FeeService, Guards, Telemetry |
| `PartyChanged`, `GroupFormed` | Social | Quests, Loot, PvpRules |
| `EffectApplied`, `EffectRemoved`, `EffectCleansed`, `Healed` | Status system (phase 6, spec 3.7) | HUD sync, Telemetry |
| `BuildupTriggered` | Build-ups (phases 4-5, on the hit that fills a meter; M1: `TargetId`, `SourceId`, `BuildUpId`, `StatusId`) | Status system, HUD sync, Telemetry |
| `GroundAreaSpawned` | Ground areas (phase 3, spec 3.1) | none in M1 |
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
| Combat | `ActionDef`, `ActionEffectDef`, `ActionBindings` (M1 placeholder until class kits), `HitShapeDef`, `ProjectileDef`, `ProjectileSettings`, `MonsterAiSettings` (monster AI, F), `PoiseDef`, `ParryDef`, `ParryColors` (M1: colors are `ActionDef.parryClassPve/Pvp`, the uncolored reduction is `StartingValuesM1.uncoloredDamageReductionPercent`, armor classes are `ClassDef.armorClass`), `DamageConstants`, `RankFactor`, `ElementCaps` (one row per element of R-3.4-07), `GroundAreaSettings` (pool, cap per owner, event repeats; spec 3.1) |
| Stats / classes | `StatDef`, `ClassTree`, `ClassDef`, `GaugeDef`, `SkillDef`, `SkillTree`, `SkillEvolution`, `PassiveTriggers` |
| Progression | `ProgressionSettings` (level cap, skill points per level), `ExpCurve`, `RankModifier`, `RankRequirements`, `AchievementDef` (WP-M2-3; `LevelPenalty` is covered by `ExpBandCurves`); `MonsterDef` `level`, `recommendedLevelMin/Max`, `baseExp` |
| Constellar (M6) | `ConstellarBoard`, `StarPointSources`, `StarGemDef`, `ConstellarCaps` (section 14.1) |
| Bystial book (M2) | `BystialCardDef`, `BystialSlots`, `BystialFamilyBonus` (section 14.2) |
| Items / loot | `ItemDef` (M2: equipment slots, weapon type, minimum level), `ContainerDef` (M2: kind, receiving container), `EquipSlotDef`, `ItemStatBonusDef` (gear stat bonuses), `ClassGearDef` (class weapon types, starter weapon; WP-M2-1), `LootTables` (M1: `LootTableDef` and `LootTableEntry`), `LootSettings` (M1: pickup range, drop scatter, queue and world caps), `RarityDef` (M2: tiers and `statPercent`), `PartyLootParams`, `OwnerWindows`, `BagExpiry` (M1: `StartingValuesM1.partyLootLockMs`, `bagExpiryMs`) |
| Economy | `PriceBase` (M2: base sell price per sell-only item), `RarityMult` (M2: sell price percent per rarity), `PriceFloorRecovery` (M2: floor, maximum, recovery and expected sales of the dynamic multiplier), `DemandCurve` (M2: `spawnDelay`, `reward`, `price`), `DemandSettings` (M2: control interval, persistence, active-player minimum, kill retention, expected kills, clamps, smoothing, respawn floor), `EconomySettings` (M2: gold cap, request queue, visit gap), `NpcDef` (M2: outpost and vendor NPCs placed by npc markers, spec 11.6), `NpcVendorStock` (M2: vendor items and fixed prices), `SpawnPoints`, `MonsterDef` (stats, poise, flinch, `attacks`, `moveSpeedCmPerSec`, `aggroRangeCm`, `leashRangeCm`), `FeeRates` (default and bounds), `BlackMarket` |
| Crafting / jobs | `MoldDef`, `RecipeDef`, `QualityThresholds`, `QualityCaps`, `EnchantOdds`, `ConversionOdds`, `GatherNodes`, `ToolDef`, `JobDef`, `JobTree`, `JobXpWeights` |
| Quests | `OutpostQuestDef`, `RunBonus`, `ForfeitPenalty`, `QuestCaps` |
| Law | `HonorWeights`, `HonorTiers`, `FlagWindow`, `JailFormula`, `ArrestWindow`, `GuardNpcTiers`, `PerPairLimits` |
| Guilds | `GuildCreateCost`, `SiegeWindows`, `SiegeCaps`, `SiegeCooldowns`, `TreasuryTheftCap`, `ReputationWeights`, `GuardHireCosts`, `GuildFeatureCosts` |
| Status / raids | `StatusEffectDef`, `StatusModifierDef` (stat changes per status, M1), `ImmunityTags` (M1: `StatusEffectDef.tags` against `MonsterDef.statusImmuneTags`), `BuildUpDef` (spec 3.8; M1: Burn only), `EffectBuildUpDef` (build-up changes per hit, M1), `RaidDef`, `RaidScaling` (by rank tier), `BossDef` |
| Arena | `ArenaTemplates`, `ArenaClassTemplates`, `ArenaRuleset`, `LivesPerMode`, `PrepTime`, `RoundsToWin`, `RatingParams`, `QueuePenalties`, `SeasonDef`, `SeasonRewards` |
| Ranks, network, persistence | `RankLevelRanges`, `ExpBandCurves`, `LagCompensation`, `NetSecurity` (limits, key IDs), `PersistenceIntervals`, `RegionDefaults`, `StartingValuesM1` |
| UI | `HudLayout`, `HudThemeTokens`, `HudModes`, `BuffIconRegistry`, `ReasonCodeStrings`, `Hotkeys`, `ClientCamera`, `TargetLockSettings` (R-2.2-06) |
| Client prediction | `ClientPrediction` (correction tolerance, snap and smoothing, pending-input cap, catch-up ticks, interpolation clock) |
| World / ops | `RegionDef`, `LingerRules`, `QueueCaps`, `TickRates`, `InterestRadius`, `VisibilityBands`, `EventBusLimits`, `TelemetrySettings` |
| Tools (bots, spec 11.5) | `BotScenario` (M2: `targetLevel`, `levelSampleMs` for the `m2-exit` run), `BotBehaviour` (M2: `pressAttackChain`, `loopSteps`, `loopKillsPerRound`, `loopPickupIdleMs`, `loopStepTimeoutMs`), `BotLinkProfile`; tools only, not game rules |
| Dev tools | `DevTools` (spec 11.7: hot reload debounce, strike trace, telemetry poll, tick window, timeline length, gizmo hold time and colors; keys in `Hotkeys`) |
