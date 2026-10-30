# Plan: Milestone 3, jobs and crafting (handover to the M3 orchestrator)

Prepared 2026-10-30 at the user's request, when M2 was functionally complete (main at the merge of PR #113, f68fad5).
It is written for a fresh orchestrator session. Read it together with:
- `CLAUDE.md`;
- `docs/spec/00-index.md`;
- `docs/spec/05-jobs-crafting-contracts-quests.md`;
- `docs/spec/12-delivery-plan-milestones.md`;
- the memory files.

Then start with the handover checklist at the end. Everything here follows the spec. Where the spec says **Open**,
this plan lists the question for the user with a recommendation and never answers it. The one exception is a rule the
user decided in the outline review: decision 2, jobs cannot be changed.

## 1. Goal and exit criteria (spec 12.2)
**M3: jobs and crafting.** Gathering, the forge engine, two jobs, enchanting and gems.

**Exit:** a crafted item can be made from gathered materials and used.

Proposed acceptance checks, to confirm with the user in the first session:
- **The exit run.** A fresh character goes through the loop on main, as a scripted bot (`m3-exit`) and a human (the QA
  smoke):
  1. It reaches the job level of the unlock schedule and picks the Blacksmith job.
  2. It buys a pickaxe from a vendor and mines tier-1 ore in the field zone.
  3. It forges a sword at the city forge from the ore and a grip material (decision 6).
  4. It equips the sword: the crafted stats show in `OwnStats` and the tooltip.
  5. It kills a monster with the sword.

  A second character with the Jeweler job refines a rough gem and sockets it into a piece of gear. The sword gets one
  enchant attempt, whatever its result. A vendor potion is drunk in combat: HP rises and TOX rises.
- **Transaction tests.** Every value-moving operation added in M3 has the four integration tests of rule 5: double
  submit, retry, crash in the middle and concurrent access. The operations are:
  - harvest, refine, convert, craft and enchant;
  - gem socket and gem removal;
  - consumable use;
  - the job pick (permanent, decision 2);
  - job tree learn and respec.

  Each is answered only after its save committed.
- **Unit tests.** Every pure function has them: the yield roll, `QualityCalc`, the rarity mapping, the crafted stats,
  the enchant, conversion and socket rolls, and job XP with diminishing returns. The seed of every harvest, forge,
  enchant, conversion and socket roll is logged (rule 6).
- **Economy simulator.**
  - It gains the M3 sinks: vendor tools and molds, consumable use, failed conversions, lost enchant materials and
    failed sockets.
  - It runs a Monte Carlo of the enchant odds and of conversion arbitrage (spec 10.2).
  - `--check` still passes in CI, and the sink cover line counts consumable use.
  - M3 adds **no gold faucet**: gathered and crafted items are not sell-only (R-4.1-01), so the outpost does not buy
    them.
- **Smoke and load.**
  - The QA smoke harness grows a step per feature (pick a job, gather, forge, equip the crafted item, enchant, socket,
    drink).
  - The bots play the M3 loop.
  - The 50-bot zone run with resource nodes loaded still meets the tick budget.

## 2. Roles and process (carried over from M2; see `docs/plans/m2-core-loop-plan.md` section 2)
- **Orchestrator.** It splits the work into packages (WPs), briefs sessions, reviews PRs and relays interface changes
  between parallel packages. It dispatches QA findings to fixers without waiting and keeps this plan's section 3a
  current. **The user merges every PR.**
- **WP sessions.**
  - One session per WP, each with its own worktree and branch.
  - **Plan mode first:** the session writes a file-level plan and waits for the user's approval before coding.
  - Model Opus 5.5 at high effort for every session the orchestrator starts.
  - A WP that would touch more than one session's worth of work ships in two PRs, as WP-M2-5 did.
- **Fixer and QA.** Their role files are `.claude/agents/fixer.md` and `.claude/agents/qa.md`. They run as visible
  sessions, never as background subagents. QA findings go to a fixer automatically; design calls go to the user.
- **Temporary design sessions**, like the M2 skill talks, may edit only `docs/design/**`. They report decisions to the
  orchestrator, who writes them into the spec and the decision log. Decision 9 (job skill trees) may use one.
- **Balance is deferred to the end of the project** (user decision 2026-10-30). Every number in M3 is a placeholder
  row in a config table, and **no WP tunes**. A WP that needs a number to make a test pass sets a plausible
  placeholder and says so. It never asks for or runs a balance pass. The M2 balance phase (the Warrior K pass, the M2
  exit run's pacing) waits with it.
- **Standing rules** (memory files and M2):
  - Everything is written in English.
  - Branches with a new migration use their own databases (`DbMigrate --dev --db-suffix <x>`,
    `Run-QaSmoke.ps1 -DbSuffix <x>`).
  - Sessions may stop the user's composed play stack for a smoke run with exactly
    `docker compose stop authapi gameserver adminpanel`, and must restore it with exactly `docker compose up -d --wait`.
    After a merge that changes server code or a table schema, the orchestrator rebuilds the stack without asking.
  - Run the Unity EditMode suite before a PR that changes table rows Unity reads, from a short worktree path.
  - Never touch `infra/`, secrets, AWS or Steam.
- **Working agreement.**
  - Whoever merges later takes protocol main+1 and the next migration number, and rebases (section 7).
  - A PR that changes a table schema also updates the validator and the spec.
  - Placeholders (art, numbers) are data, so a swap never touches code.
  - Every refusal has a reason code and a `ReasonCodeStrings` row.
  - New events go into spec 8.1, transactions into 8.2, tables into 8.5.
  - The user's play sessions drive a findings loop (finding, then a fixer or a small WP, then a PR).

## 3a. Where we are (kept by the M3 orchestrator)
_One dated entry per milestone event: decisions answered, waves briefed, PRs merged in order with their protocol and
migration numbers, QA results, carry-overs._

- **2026-10-30:** M3 started. Main at the merge of PR #114 (this plan), 19d7349: protocol 25, highest reliable id 33,
  realm schema V0011, no open PRs; CI green on every merge up to #113 (the #114 run, docs only, was still running).
  Verified in the orchestrator's worktree: `dotnet build` 0 warnings, `dotnet test Mmorpg.sln` 3,283 tests green
  (Shared 1,816, GameServer 1,353, AuthApi 84, AdminPanel 30). Docker Desktop was found stopped; the orchestrator
  started it and brought the play stack back with `docker compose up -d --wait` (images on main, protocol 25, V0011; no
  rebuild needed). The one smoke run of checklist step 2 waits for the wave 0 QA, since main did not change since the
  wave 2 QA of 2026-10-29. Balance stays deferred to the end of the project (user, 2026-10-30), so the M2 balance phase
  and the M2 exit run wait with it. Next: decisions 1 to 5 of section 5 to the user, their spec and decision-log PR,
  then the wave 0 briefs (WP-M3-1, -2, -3, -4 and the WP-M3-12 skeleton) in plan mode.

## 3. Where M2 left things (main at PR #113, 2026-10-29)
**Done in M2:**
- item instances and containers, equipment and the starter weapon;
- stats v2 with attributes and free attribute points;
- exp, level, rank and achievement counters;
- outposts with selling, the demand loop, NPC vendors, gold and the ledger;
- skill tree v1 with the eight tier-1 class rows;
- outpost quests and runs, and the first field zone (greybox, four rings, seven types and the Ashen Warlord);
- the Bystial book, statuses and the tier-1 mechanics;
- the tutorial gate and the unlock schedule;
- HUD v2 with the character sheet, and minimap v0;
- the economy simulator and report, and the leveling bot with the `m2-loop` and `m2-exit` scenarios.

Protocol **25**, highest reliable message id **33** (`AttributeSpendResult`), realm schema **V0011**.

**What M3 builds on (checked on f68fad5):**
- **Protocol.**
  - `src/Shared/Protocol/ProtocolVersion.cs` holds `Current = 25`.
  - Reliable ids are the `ReliableMessageId` enum in `src/Shared/Protocol/ActionMessages.cs`; the last are 32
    `AttributeSpendRequest` and 33 `AttributeSpendResult`.
  - A new message needs a layout in `ReliableMessageReader.Build` (R-2.5-07, `MemoryPackGuard`), and a client message
    also needs a `ReliableMessageCaps` row (11 rows today) and a route in `src/GameServer/Net/InputRouter.cs`.
  - **M3 starts at protocol 26 and reliable id 34.**
- **Persistence.**
  - Realm migrations V0001 to V0011 are in `src/GameServer/Persistence/Migrations/Realm`; **M3 starts at V0012**.
  - Class 1 operations ask for an immediate save that carries the pending class 2 batch (the ordering barrier by
    inclusion, spec 7.1).
  - Value-moving single-player operations (skill tree, attribute points) also save at once and answer after the commit.
  - Pickups and moves ride the next class 2 batch and are answered after its commit.
- **Items** (`src/GameServer/Items`):
  - `InventoryService` is the only writer of items and gold. It offers `TryAdd`, `TryRemove`, `TryMove` (with a
    split count), `TryEquip`/`TryUnequip`, `GrantStarterGear`, `TryAddGold`/`TryRemoveGold`, `SpaceFor`, `Accepts`,
    `IsNonTradable`, `WasApplied`, `IsCommitted`, and `TakeBatch`/`Committed` for saving.
  - **There is no `Reserve`/`Release`/`Escrow` yet** (`InventoryService.cs:38`, TODO).
  - Instance properties are carried as an **unparsed** `PropertiesJson` string (`{"v":1}`; the database checks that
    `v` is present, V0004). Defaults come from `ItemInstanceIds.cs`, and enchant and gems are inert. **There is no
    crafted-stat snapshot.**
  - `ItemCategory` (`src/Shared/Data/Tables/ItemCategory.cs`) has SellOnly 0, Material 1, Equipment 2, Consumable 3,
    **MoldOrTool 4** and Quest 5. Equipment and MoldOrTool are instances; the Safe bag equipment area
    (`safeEquipment`, code 2) takes molds and tools.
  - `InventoryReason` (`src/GameServer/Events/ItemEvents.cs`) runs from None 0 to QuestDelivery 8.
  - Gear stats reach `StatsService` through `IEquipmentStatSource` (`GetEquippedBonuses`, scaled by
    `RarityDef.statPercent`) and the `EquipmentChanged` event.
  - **No code uses an item.** `src/GameServer/Stats/ConsumptionMeters.cs` (`AddToxicity`, `AddSatiety`) has no caller.
  - Rarity tiers common to legendary exist; the forge is the only source of epic and legendary (spec 4.1).
- **Stats.** `StatSourceType` (`src/Shared/Simulation/Stats/StatModifier.cs`) already has Food 4, Potion 5, Gem 6,
  Enchant 7 and Job 10, besides Gear, Skill, Buff, Debuff, Mount, Title, BystialCard and Attribute.
- **Outposts and NPCs** (`src/GameServer/Outposts`):
  - Every interaction goes through the `OutpostInteractions` gate (NPC visible, offers the kind, in range) and an
    `IOutpostInteractionHandler` per `NpcInteraction`: `OutpostReason? Interact(Player, Npc, uint requestId)`, where
    null means the handler answers later.
  - `NpcInteraction` has Sell 1, Buy 2, RankUp 3 and Quests 4. `NpcDef.json` has `m1.outpost` and `m1.vendor`; both
    outposts of the zone name them.
  - NPCs are placed by `npc` markers (spec 11.6). Vendors create stackables only (`VendorHandler`); vendor
    instances (tools, molds) are new.
- **World.**
  - `MarkerKind.ResourceNode = 4` exists, and `data/world/m1/markers.json` has **12 resource node markers**
    (`field1.node.ring1.a`/`b`, `ring2.a`-`d`, `ring3.a`-`c`, `ring4.a`-`c`), data only; the server loads and
    ignores them.
  - The snapshot entity kind `ResourceNode = 6` is reserved (`src/Shared/Protocol/Snapshot.cs`).
  - The greybox is built by `unity/Assets/Game/WorldAuthoring/Editor/FieldZoneGreybox`.
- **Actions.**
  - `ActionMachine` has a `Channel` phase (`ActionDef.channelMs` up to 60 s), interrupted by flinch or poise break.
  - `ActionKind` is Attack, Skill, Dodge and Parry only: there is **no interact or use-item kind**.
  - The `interact` hotkey (T) sends `OutpostInteractRequest` and is not an action.
- **Events.**
  - `src/GameServer/Events` has none of the M3 activity events (`Harvested`, `Crafted`, `Refined`, `Converted`,
    `Enchanted`, `GemSocketed`); spec 8.1 lists them.
  - `AchievementCategory` has MonsterKills, MonsterTypesKilled, BossKills, RunsClaimed and OutpostsVisited, but no
    crafting or gathering category (R-3.6-05).
- **Demand.** `src/GameServer/Economy/DemandService` keeps decayed counters per (monster type, region) and per sold
  item. R-4.2-03 and R-5.2-03 add resource-node types to the same loop.
- **Progression.** `src/GameServer/Progression/ProgressionStore` is the only writer of level, exp, rank, skill and
  attribute points and counters. Job progress is a new concern with its own writer (WP-M3-1).
- **Tools.**
  - Bot loop steps, one file each, are in `src/Tools/BotClient/Loop/Steps`: Allocate, Buy, Equip, Fight, Learn,
    Pickup, RankUp, Run and Sell. Their contract is in that README.
  - The QA smoke steps are `StepNames` in `unity/Assets/Game/Dev/Qa/QaScenario.cs`, from title to tutorial-gate.
    Test-only seed scripts live in `tools/qa` (`Set-BagItem.ps1`, `Set-KillCount.ps1`).
  - `src/Tools/EconomySimulator` (SimEconomy, SimRunner, DemandModel, SettleCheck, ScenarioResult) models sales as
    the only faucet and vendors as the only sink. `EconomySimScenario.usedPerPlayerHour` is a placeholder until M3.
- **UI.**
  - `unity/Assets/Game/UI/Menus` has Book, Character, Inventory, Outpost, Quests and Skills; every window follows the
    9.13 pattern.
  - The shortcut row (`HudLayout` `shortcut_row`) has character, inventory, skills, book and journal. **There is no
    JB (Jobs) button and no `toggleJobs` hotkey.**
  - The `cast_bar` element is in the 9.3 registry for harvest and forge channels.

**Tables on main (109):** AchievementDef ActionBindings ActionDef ActionEffectDef AdminSettings AttributeFormula
AuthApiLimits BodyCollisionSettings BotBehaviour BotLinkProfile BotScenario BuffIconRegistry BuildUpDef BystialCardArt
BystialCardDef BystialEffectCap BystialFamilyBonus BystialSlots BystialStatCap CharacterCreationConfig
CharacterSheetStats CharacterSlots ChatSettings ClassDef ClassGearDef ClientCamera ClientPrediction
ConsumptionMeterSettings ContainerDef DamageConstants DemandCurve DemandSettings DevTools EconomySettings
EconomySimScenario EffectBuildUpDef ElementCaps EntityVisualDef EquipSlotDef EventBusLimits ExpBandCurves ExpCurve
ForfeitPenalty GroundAreaSettings HitShapeDef Hotkeys HudLayout HudSettings HudThemeTokens InventorySettings
InventoryWindowSettings ItemDef ItemIconRegistry ItemStatBonusDef LoginLayout LoginScreens LoginSettings LootSettings
LootTableDef LootTableEntry MinimapIconRegistry MinimapMarkerDef MinimapRegionTint MinimapSettings MonsterAiSettings
MonsterDef NetSecurity NpcDef NpcVendorStock OutpostQuestDef PriceBase PriceFloorRecovery ProgressionSettings
ProjectileDef ProjectileSettings QuestBoardSettings QuestCaps QuestItemDrop RankFactor RankLevelRanges RankModifier
RankRequirements RarityDef RarityMult ReasonCodeStrings RegionDef RegionDefaults ReliableMessageCaps ResourceRegen
RunBonus ServerDirectory ServerLoop SessionSettings SkillDef SkillEvolution SkillModifierDef SkillTree
SkillTreeSettings SpawnPoints StartingValuesM1 StatDef StatusEffectDef StatusModifierDef StatusSettings
TargetLockSettings TelemetrySettings TutorialSettings UiStrings UnlockSchedule.

**Carried over from M2** (not blockers for M3; list them in 3a when they move):
- **The balance phase and the M2 exit run** ("5 bots reach level 30 within 4 h"; the 20-minute runs reached levels
  3-5 with placeholder numbers): deferred to the end of the project by the user's decision of 2026-10-30. They include
  the Warrior K pass, the mage and assist bot deaths, the bosses' stun resistance, and the Blood loss fill rate.
- **Pending `TODO(open)` calls from M2 PRs:**
  - WP-M2-5: points back when a table change drops a learned node; respec and a weapon a cleared unlock allowed.
  - WP-M2-6: the bot's deliver offers, and a board that always carries a kill offer.
  - WP-M2-8: the family threshold and the build-up resistance readings.
- **The second-client WP and the 3-4 player playtests at 100 and 200 ms** (from M1).
- **Zone art** (CC0 models for the six field types and the Warlord): the archived art session.
- **Known placeholders:**
  - interact key T;
  - NPCs wear class avatars;
  - the faint uncolored telegraph;
  - melee chain hits without a forward step.

## 4. Spec sections to read per area (before briefing a WP)
| Area | Spec | Notes |
|---|---|---|
| Jobs, job XP, job trees | 5.1 (R-5.1-01..04) | the job list, main and secondary, XP weights and diminishing returns, free respec; decision 2 makes the pick permanent |
| Gathering, nodes, tools, tier conversion | 5.2 (R-5.2-01..04), 11.6 (resource node markers), 4.2 (R-4.2-03 node demand) | harvest is a channel; fishing has no entity (not in M3 unless the Adventurer is chosen) |
| Forge engine, molds, refining | 5.3 (R-5.3-01..03, -06) | `ForgeSession` states, `QualityCalc`, `IForgeChallenge`, seeds logged; contracts plug in later |
| Enchanting | 5.3 (R-5.3-04), 3.4 ("enchant bonus from a config table by level") | +1, no change or -1; never destroyed; materials always consumed |
| Gems | 5.3 (R-5.3-05), 4.1 (gem slots), 14.1 (R-14.1-04: Star Gems are a different family, M6) | socket rates and removal are Open (decision 11) |
| Items, containers, properties | 4.1 (R-4.1-01..04, M2 item model and implementation), 9.13 | molds and tools in the Safe bag equipment area; properties JSON is versioned |
| Consumables, toxicity and satiety | 3.4 (R-3.4-03, `ConsumptionMeters`), 3.7 (R-3.7-05) | effects list Open (decision 3) |
| Economy, sinks | 4.2 (R-4.2-01, -04), 12.5 | vendors sell basic tools and starter molds; failed conversions and enchant material loss are sinks |
| Stolen inputs | 4.4 (R-4.4-03), 6.1 (R-6.1-03) | taint inheritance now, honor loss in M5 |
| Save classes and transactions | 7.1 (R-7.1-01, -02, -05), 8.2 | craft, enchant, harvest, consumable use and job XP are class 2; confirm after the commit |
| Events and tables | 8.1, 8.2, 8.5 ("Crafting / jobs" row) | the 8.5 names `MoldDef`, `RecipeDef`, `QualityThresholds`, `QualityCaps`, `EnchantOdds`, `ConversionOdds`, `GatherNodes`, `ToolDef`, `JobDef`, `JobTree`, `JobXpWeights` |
| HUD and menus | 9.3 (`cast_bar`, `minimap` job nodes, shortcut row JB), 9.13 window pattern | menus are opened by the shortcut row and hotkeys; reason-code toasts |
| Unlock schedule | 12.3 | jobs and gathering, crafting at levels 12-18 |
| Contracts (interfaces only) | 5.4 | M4; the forge session's owner and output recipient stay separate, inputs come from a reservation |
| Achievements | 3.6 (R-3.6-05) | crafting and forging achievements without exp |
| Anti-bot | 7.3, 5.2 implementation | server-validated channel, diminishing returns, telemetry |

## 5. Decisions the user must make (Open in the spec; ask before the wave that needs them)
Each decision gives its rule ids, the question and the orchestrator's recommended answer with the reason. The plan
decides none of them except decision 2's first point, which the user decided in the outline review. Record every
answer in the spec section named and in `13-decision-log.md` in the PR that implements it. **Every number is a
placeholder** (section 2).

### Before wave 0
1. **The two jobs of M3** (spec 12.2 says "two jobs" and does not name them; R-5.1-02, R-5.1-03).
   - **Recommendation: Blacksmith and Jeweler.** Between them they cover every item of the M3 row:
     - gathering: the Blacksmith mines minerals from rocks, and gems "may be found while mining" (R-5.2-01);
     - the forge engine: Blacksmith weapons, armor and molds, and Jeweler jewelry for the ring and amulet slots that
       already exist;
     - enchanting: by "the item's job" (R-5.3-04), so both jobs enchant what they make;
     - gems: the Jeweler refines gems and sockets them (R-5.3-05, -06).
   - **Alternative: Blacksmith and Botanist** (herbs from plants, potions). That gives crafted consumables, but nobody
     could serve gems, which the M3 row requires.
   - **The secondary job:** with two jobs, both may be main or secondary. Which jobs may be secondary is data
     (`JobDef.allowedAsSecondary`).
2. **Picking a job** (R-5.1-03; R-5.3-01 starter molds; 12.3: jobs at levels 12-18).
   - **Decided by the user on 2026-10-30, in the review of this plan's outline: jobs cannot be changed.** Once a player
     has picked the main and the secondary job, there is no way to change them for now.
   - WP-M3-1 records this in spec 5.1 and the decision log. It replaces the 5.1 implementation note "Slots only point
     to records, so changing jobs never loses progress": the per-job records stay, the slots are written once.
   - Still to ask:
     - **where and from which level** the pick happens. Recommendation: a job master NPC in the city (a new
       `NpcInteraction` `jobs`, the outpost gate and range), from level 12 (12.3), out of combat;
     - **whether the main and secondary are picked together.** Recommendation: separately, each one final when made,
       so a player can wait before choosing a second;
     - **a confirmation step** in the window, since the pick is permanent;
     - **starter molds** granted once per job at the pick (R-5.3-01), idempotent by `jobStart:{character}:{job}`.
3. **Consumable use in M3, and the potion and food effects** (R-3.4-03; R-3.7-05 "the concrete list of effects and
   their numbers is Open"; 12.5 "consumable use (M3)").
   - **Recommendation:** consumable use ships in M3.
   - **Using an item is an action** (a new action kind with `ActionDef` rows: a short startup, no stamina, refused
     during another action, cancelable like a skill), so drinking in a fight is a commitment (souls-like pacing). It
     is **allowed in combat**.
   - Placeholder effects for the three vendor goods: the small HP potion heals a flat amount at once and adds TOX; the
     small MP potion restores MP and adds TOX; bread adds SAT and a short regeneration buff (a status, source Food).
   - Refusals `TooToxic` (the potion's TOX would pass the cap) and `TooFull` (bread over the SAT cap; R-3.4-03 "you
     cannot overeat").
   - Used from the inventory window and from one quick-item key bound to a chosen stack; the hotbar stays skills.
   - The question for the user is whether it is an action or instant, and whether it is allowed in combat; the
     numbers are placeholders.
4. **Item model v3 for crafting** (R-4.1-01, R-5.2-02, R-5.3-02; 10.4 [M3] "whether molds can be traded").
   - **Recommendation:**
     - **three material tiers** in M3, placed over the field rings (T1 in rings 1-2, T2 in ring 3, T3 in ring 4);
     - **materials carry stat rows** ("give stats to crafts");
     - a crafted equipment instance stores a **stats snapshot** in properties v2 (R-5.3-02 "item stats = materials
       used + a quality bonus"; spec 5.3 "output is a stats snapshot"), which replaces the `ItemStatBonusDef` list for
       that instance; dropped gear keeps its fixed list;
     - **tools count while they are in the Safe bag's equipment area** (R-4.1-02 puts tools there), so there is no
       tool equipment slot;
     - **gem slots** are a count per equipment `ItemDef` row, dropped gear included;
     - **molds are tradable**: they are products (R-5.4-01 lists "mold" among contract actions); the flag stays inert
       until trade (M4).
5. **Resource nodes** (R-5.2-02, R-5.2-03; architecture rule 9; 9.3 minimap job nodes).
   - **Recommendation:**
     - node types per ring and tier: ore T1 to T3 and one gem vein type, mapped from the existing markers plus a few
       new ones;
     - **nodes are visible to everyone**, since they are not hidden entities; only players with the job get the
       prompt, and others are refused with `JobRequired`;
     - each node depletes after one harvest and respawns after a delay scaled by the demand loop, never to zero;
     - the job-node markers of 9.3 show on the minimap for the player's jobs;
     - the harvest channel breaks on damage, a movement input or a dodge (spec 5.2 "interruptible by
       damage/movement").

### Before wave 1
6. **Wood and weapon-grip sourcing** (R-5.1-02: "wood gathering and grip materials need a new home (Open)"; 10.4 [M3]).
   The M3 exit needs it: a normal sword mold has a grip slot (R-5.3-01).
   - **Recommendation:** in M3, grip slots accept **monster-drop craft materials** (hide, bone, sinew from the field
     zone's beasts and the cyclops). R-4.1-01 already makes monster drops craft materials, so this adds loot rows,
     not a job.
   - Wood keeps its Open status until a Botanist or Adventurer job lands.
   - Alternative: tree nodes with the axe (R-5.2-02 names it) for the Blacksmith, which stretches "minerals from
     rocks".
7. **Where and how crafting happens** (R-5.3-01, R-5.3-02, R-5.2-04; the spec does not place the forge).
   - **Recommendation:**
     - **stations**: a forge and a jeweler's bench, NPC markers with new `NpcInteraction` kinds, in the city and at the
       mid-zone outpost; crafting is out of combat, through the outpost gate and range;
     - **forge actions** smelt, hit and cool as `ForgeActionDef` rows, each adding a bounded number to quality, up to
       a placeholder count per session;
     - the quality components bounded in `QualityCaps`, with the small extra random term of R-5.3-02 as one more
       bounded entry;
     - `QualityThresholds` mapping quality to common up to legendary (the forge reaches epic and legendary, spec 4.1);
     - conversion (R-5.2-04) as a recipe type with placeholder odds per tier and a daily cap per character.
8. **Job XP split and diminishing returns** (R-5.1-04: "the XP split between gathering and crafting will be decided
   later"; 10.4 [M3]).
   - **Recommendation:** placeholder `JobXpWeights` (gather 1, refine 1, craft 2, conversion 1, enchant 1, socket 1)
     times a difficulty factor from the node or recipe tier.
   - Diminishing returns when a character repeats the easiest recipe or node type in a sliding window.
   - Numbers in the balance phase.
9. **Job skill trees** (R-5.1-04: "each job has its own skill tree giving a few craft bonuses"; no tree is designed).
   - **Recommendation:** a short design talk with the user, a temporary design session as in M2, before WP-M3-7.
   - Without one: 4 to 6 placeholder nodes per job (yield percent, quality bonus, enchant odds, socket odds, job XP),
     one point per job level, free respec (R-5.1-04).

### Before wave 2
10. **Enchanting** (R-5.3-04; 3.4 "Enchant bonus is computed from a config table by level").
    - **Recommendation:**
      - **"The item's job"** is the job of the recipe that makes the item: weapons and armor are Blacksmith, rings
        and amulets Jeweler, and dropped gear maps by category the same way. In M3 only a character with that job can
        enchant (contracts are M4).
      - The **material** is an enchant stone, mined and rarely dropped.
      - **Odds** are placeholder weights of +1, no change and -1 per current level, with job tree bonuses capped.
      - The **bonus** per level is a percent added to the item's stat lines, read when stats are computed, never
        baked into the item.
11. **Gems** (R-5.3-05: socketing, success rates and gem removal are Jeweler services; 10.4 [M3] "Jeweler socket
    success rates and gem removal").
    - **Recommendation:**
      - **rough gems** come from mining and monster drops, and are cut by the Jeweler's refine recipe (R-5.3-06);
      - placeholder **socket success rates** by gem tier;
      - a **failed socket consumes the gem** and leaves the item untouched (never destroyed, like enchanting);
      - **removal** by a Jeweler returns the gem intact and costs a placeholder material, a small sink;
      - a gem tier ladder with one stat row per tier and gem kind;
      - a stolen gem taints the item (R-5.3-05).
12. **The forge minigame** (R-5.3-02 and spec 5.3 `IForgeChallenge`; the spec gives no design).
    - **Recommendation:** one challenge in M3, a timing bar for the Blacksmith's hit action. The server issues its
      parameters and seed; the client plays and sends timestamped inputs; the server scores them on its own timeline
      within a latency tolerance.
    - Its contribution is capped (R-5.3-02). The Jeweler uses no challenge in M3, and the slot stays pluggable per job.

## 6. Work packages
Each WP has its own session in plan mode, its own branch and PR, and a file-level plan in the PR. Waves group WPs that
touch disjoint areas and can run at the same time; the order inside a wave does not matter.

Every WP:
- that adds a table adds its schema, validator rules and spec update, with `[Optional]` fields on shared tables other
  branches add rows to;
- gives every transaction the four tests;
- gives every refusal a reason code and a `ReasonCodeStrings` row;
- adds new events to spec 8.1, transactions to 8.2 and tables to 8.5;
- gives every window the 9.13 pattern (UI Toolkit, a `HudLayout` row, `UiStrings`, reason-code toasts, "while the
  pointer is on the window the mouse buttons are not attack or parry");
- confirms every value-moving operation only after its save committed (rule 5), and logs every seed (rule 6).

Protocol and migration numbers below are **proposals in wave order**. The orchestrator assigns the final ones at merge
time (section 7).

### Wave 0: foundations (parallel; nothing in it depends on another M3 WP)
**WP-M3-1 Jobs core (spec 5.1; decisions 1, 2, 8).**
- **Data.** `JobDef` (the two jobs of decision 1: `id`, `netId`, `nameId`, `allowedAsSecondary`, `starterItemIds`,
  `minLevel`) and `JobXpWeights` (per activity, difficulty factor, the diminishing-returns window). The job level
  curve is a placeholder; its table name is the WP plan's proposal, since spec 8.5 names none.
- **Server.** `src/GameServer/Jobs/JobService` is the **only writer of job progress**:
  - the permanent pick of the main and the secondary slot (decision 2): one write per slot, refused afterwards with
    `JobAlreadyChosen`; also `JobLevelTooLow`, `JobNotSecondary`, `InCombat`;
  - job XP from the activity events with weights and diminishing returns, `JobLevelUp`, job points (spent by WP-M3-7);
  - `IJobQuery` (has job, job level), read by harvesting, the forge, enchanting and gems.
- **The pick** is an interaction `jobs` on a job master NPC through `OutpostInteractions` (new `NpcDef` row and an npc
  marker in the city). The job request is class 2 but value-moving, so it saves at once and answers after the commit,
  like the skill tree. The starter molds are granted in the same operation through `InventoryService.TryAdd` (key
  `jobStart:{character}:{job}`); the rows come from WP-M3-2, and whichever WP merges second wires them.
- **Events.** This WP **owns the activity event records**, so producers and the consumer run in parallel:
  `Events/CraftingEvents.cs` with `Harvested`, `Crafted`, `Refined`, `Converted`, `Enchanted`, `GemSocketed`,
  `GemRemoved`, `ItemUsed`, and `JobChosen`/`JobLevelUp`. Each carries the character, the job, the activity, the
  tier or difficulty and the operation key.
- **Achievements.** New `AchievementCategory` values for gathering and crafting (R-3.6-05), counted by
  `AchievementService` from the same events.
- **Migration V0012** `job_progress`: one row per (character, job) with level, xp and points; a unique slot per
  character for main and secondary; `job_ops`, the audit and idempotency rows.
- **Protocol 26** (reliable ids from 34): `JobState` (the jobs, levels, xp, points), `JobRequest`, `JobResult`; a
  `ReliableMessageCaps` row and an `InputRouter` route.
- **Client.** The Jobs window v1 (`UI/Menus/Jobs`, `HudLayout` `jobs_window`, `Hotkeys` `toggleJobs`, the JB shortcut
  button) shows the jobs with level and XP bar; at the job master the pick with its confirmation. Engine-free
  `Entities/Core/ClientJobs`, also compiled into the bots.
- **Bot step** `takeJob`; **smoke step** `pick-job` (a test-only level seed through `DbMigrate --set-level`).
- **Tests.** Job XP with diminishing returns as pure functions (`Shared/Simulation/Jobs/JobMath`); the four
  transaction tests on the pick (a second pick refused, a double submit, a crash between apply and commit, two picks
  at once); event idempotency (a repeated `Harvested` adds nothing).
- **Shares** `IJobQuery` and the event records with WP-M3-4, -5, -6, -8 and -9; the starter item rows with WP-M3-2.
- **Done:** a level-12 character picks Blacksmith as main at the job master, the pick survives a relogin, a second pick
  is refused, and a test event raises its job XP.

**WP-M3-2 Item model v3: materials, tools, molds, properties, reservations (spec 4.1, 5.3; decision 4).**
- **Data.**
  - `ItemDef` v3 (`[Optional]`): material `tier`, `materialTags` (the slot filters of molds: ore, ingot, grip, gem,
    enchantStone...), `gemSlots` for equipment, `jobId` (the item's job for enchanting, decision 10).
  - `MaterialStatDef` (material to stat contributions).
  - `ToolDef` (tool item, tool type pickaxe or later axe and rod, tier, yield bonus; R-5.2-02).
  - Mold items as MoldOrTool rows; `MoldDef` itself belongs to WP-M3-6, so this WP adds only the item rows and
    `MoldDef` references them.
  - Vendor stock rows for a pickaxe and the starter molds (R-4.2-01: "starter molds, and basic tools at fixed prices").
- **Typed properties v2.** `Shared/Simulation/Items/ItemProperties`: `v`, `enchant`, `gems[]`, `craftedStats[]`,
  `quality`, `crafterId`, `originalOwner`, `ownerHistory`, `tainted`. It reads v1 and writes v2 deterministically. The
  server parses once per change, never per tick (no allocations in hot paths).
- **Stats.** `IEquipmentStatSource.GetEquippedBonuses` returns the crafted snapshot when an instance has one,
  otherwise the `ItemStatBonusDef` list scaled by rarity as today. Enchant and gem sources plug into the same interface
  later (WP-M3-8, -9).
- **Reservations.** `InventoryService.Reserve`/`Release` (the API of spec 4.1, R-5.3-03, R-2.1-10):
  - in memory, keyed by an operation key, over stacks (counts) or instances;
  - a reserved unit cannot be moved, split, sold, delivered, equipped or used (`ItemReserved`);
  - released on cancel, on disconnect or a lost session, and on world shutdown;
  - consumed by a later remove that names the reservation.

  Nothing is written for a reservation, so a crash loses nothing and duplicates nothing.
- **Vendors** create instances for instance categories (`VendorHandler` through `InventoryService`).
- **Taint helper** `ItemRules.TaintsOutput(inputs)` (R-4.4-03 "any" rule; honor loss stays TODO(M5)).
- **Protocol 27:** `InventoryContents` carries each instance's typed properties (enchant, gems, crafted stats, quality,
  crafter id, tainted) and the reserved flag per slot; append only.
- **Client.** Tooltips show crafted stats, quality and crafter (`DisplayIdentity`: the crafter name reaches clients
  only as an `EntityIdentity`, rule 8; until then "Crafted"). Reserved slots draw dimmed.
- **Tests.** Properties round trip (v1 in, v2 out, unknown fields kept); stat source with and without a snapshot;
  reservation rules (a reserved stack refuses every move kind, release on disconnect, consume after reserve); vendor
  instance purchase transaction tests.
- **Shares** `ItemProperties` and `Reserve`/`Release` with WP-M3-5, -6, -8 and -9; `ItemDef.jobId` with WP-M3-8.
- **Done:** a character buys a pickaxe, which lands in the Safe bag equipment area; a test reserves three ore, a move
  of them is refused, and releasing them allows it.

**WP-M3-3 Consumable use (spec 3.4 R-3.4-03, 3.7 R-3.7-05, 12.5; decision 3).**
- **Data.** `ConsumableDef` (item, the action it plays, heal or restore amounts, status applied, toxicity and satiety
  cost, cooldown); `ActionDef` rows for drinking and eating with a new action kind (`UseItem`); `Hotkeys`
  `quickItem`.
- **Server.** A `UseItemRequest {requestId, container, slot}` starts the action through `ActionInput.Request`:
  - refused with `TooToxic`, `TooFull`, `ItemNotUsable`, `ItemReserved`, `OnCooldown`, or in another action;
  - at the action's effect point, `InventoryService.TryRemove` with reason `ItemUsed` (an item sink, class 2, key per
    request) and the effects (heal, restore, status with source Potion or Food through the `StatusSystem`, the
    `ConsumptionMeters` raise);
  - answered after the commit (`UseItemResult`); `ItemUsed` published.

  Prediction plays the action like a skill.
- **Protocol 28:** `UseItemRequest`, `UseItemResult`, and the quick-item binding if it is server state (the WP plan
  decides whether the binding is client only).
- **Client.** Right-click or double-click a consumable in the inventory window; the quick-item key and its HUD slot
  (`HudLayout` row); toasts for refusals.
- **Economy.** `EconomySimScenario.usedPerPlayerHour` becomes live; the sink cover line counts the vendor goods used.
- **Bot step** `useItem` (drink below an HP threshold); **smoke step** `drink-potion` (TOX rises, HP rises).
- **Tests.** The four transaction tests on use (a double submit uses one potion); meter caps as pure functions; an
  `ActionMachine` test that use is interrupted like a skill; a prediction test.
- **Done:** a character buys and drinks an HP potion in combat, HP and TOX rise, and the potion is gone after a
  relogin.

**WP-M3-4 Resource nodes in the world (spec 5.2 R-5.2-03, 11.6, 4.2 R-4.2-03; decision 5).**
- **Data.** `NodeDef` (`id`, `netId`, job, tool type, minimum tool tier, node tier, yield table, respawn ms, channel
  ms, `visualId`) and `GatherNodes` (marker id to `NodeDef`, like `SpawnPoints`); validator: every resource node
  marker has exactly one row (`UnusedNode`), every row a marker, tiers within the tool ladder.
- **World.** Optional new markers in `FieldZoneGreybox` if the decided placement needs more than the 12. The export,
  the `DataValidator --world` checks, and `WorldLoader.CheckConfigMarkers` for nodes.
- **Server.** A `ResourceNode` entity per row (`EntityKind.ResourceNode`, snapshot kind 6) with state available,
  depleted or respawning and `respawnAt` from the world clock (no timers). It is never a combatant and never a lock
  target. `DemandService` gets counters per (node type, region) from `Harvested`, and scales the node's respawn delay
  like monster respawns (floor kept). A test-only `Deplete` entry point stands in for WP-M3-5's harvest.
- **Protocol 29:** the snapshot spawn of nodes with their state (spawn definition id `NodeDef.netId`, a state bit).
- **Client.** Greybox node visuals (`EntityVisualDef` rows: a rock, a vein, depleted look); the minimap's job-node
  markers (`MinimapMarkerDef`, 9.3) for the player's jobs; an "in reach" prompt hook for WP-M3-5.
- **Tests.** World load with nodes, validator rules, respawn scaling as a pure function, the 50-bot `m2-zone` run
  with nodes loaded under the tick budget.
- **Shares** the node entity and the prompt hook with WP-M3-5; the demand counters with the simulator (WP-M3-12).
- **Done:** a player walks the zone and sees ore nodes; a test depletes one and it respawns after the scaled delay.

**WP-M3-12 skeleton: the M3 loop and exit run (spec 11.5, 12.5).** Like WP-M2-12's wave 0 skeleton:
- an `m3-loop` behaviour and an `m3-exit` scenario in `BotScenario`/`BotBehaviour`, with pluggable steps that each WP
  fills;
- placeholder steps that report "not yet";
- a `report` section for job XP and item creation or destruction by reason.

The exit criterion is measurable from the first merge.

### Wave 1: gathering, the forge and job trees (after wave 0 merged)
**WP-M3-5 Harvesting (spec 5.2 R-5.2-01, -02; decisions 5, 8).**
- **Flow.** A `HarvestRequest {requestId, nodeEntityId}` from the interact key on a node in reach:
  - the gate: node visible and available, in range, the job (`IJobQuery`, `JobRequired`), a tool of the type and tier
    in the Safe bag equipment area (`ToolRequired`, `ToolTierTooLow`), out of another action;
  - then a **channel**: a new action kind or a server-kept channel, the WP plan chooses; broken by damage, a movement
    input or a dodge (`HarvestInterrupted`);
  - at its end the server rolls the yield (seeded, logged) from node tier, job level, tool bonus, job tree bonus
    (`IJobBonusQuery`, a zero stub until WP-M3-7), and diminishing returns on repeats;
  - gem finds on mining nodes come from the yield table;
  - `InventoryService.TryAdd` with reason `Harvest` (class 2, key per request), the node depletes, and `Harvested` is
    published.

  The answer comes after the commit. Two harvesters on one node in one tick: the lower entity id wins, the other gets
  `NodeDepleted`.
- **Data.** Yield tables (`NodeYieldEntry`: node, item, weight, count range, minimum job level); the ore T1 to T3,
  rough gem and enchant stone material rows; placeholder numbers.
- **Protocol 30:** `HarvestRequest`, `HarvestResult`; the channel shown on the `cast_bar` (9.3).
- **Client.** The node prompt ("Press T to mine"), the cast bar, a refusal toast, a harvest toast.
- **Bot step** `gather` (walk to the nearest available node of its job, harvest N times); **smoke step** `gather-ore`.
- **Telemetry.** `Harvested` with node, region, yield and seed (anti-bot input, 7.3).
- **Tests.** The yield roll and diminishing returns as pure functions; the four transaction tests (a double submit
  harvests once, a crash before the commit leaves the node available and the bag unchanged after restart, two
  players on one node); a channel interruption test.
- **Done:** a Blacksmith with a pickaxe mines ore in ring 1, the ore is in the bag after a relogin, the node respawns.

**WP-M3-6 Forge engine (spec 5.3 R-5.3-01..03, -06, 5.2 R-5.2-04; decisions 6, 7).** It may ship in two PRs: the
engine with tests, then the window and content.
- **Data.**
  - `MoldDef` (mold item, job, slots with material tag filters and counts, allowed actions, output item, minimum job
    level);
  - `RecipeDef` (type `craft`, `refine`, `convert` or `mold`; job; inputs; output; minimum job level);
  - `ForgeActionDef`, `QualityThresholds`, `QualityCaps`, `ConversionOdds` (per tier, success percent, daily cap).
  - Blacksmith starter content: a sword, an armor piece, their molds, the T1 ingot refine recipe, a mold recipe;
    the grip materials of decision 6 as loot rows on the field monsters.
- **Server.** `src/GameServer/Crafting/ForgeService` on the world thread, with one `ForgeSession` per character.
  `Created → MaterialsReserved → Running → Finished | Cancelled`:
  - **Start** at a station (the forge and bench `NpcInteraction` kinds, out of combat) names a mold or recipe and the
    material per slot. It checks job, job level, slot filters and the mold in the Safe bag (`MoldRequired`,
    `SlotMaterialInvalid`, `JobLevelTooLow`), and reserves the inputs (WP-M3-2).
  - **Actions** add their bounded numbers.
  - **Finish** runs the challenge (`IForgeChallenge`; a no-challenge stub here, WP-M3-10 adds the first), then the
    pure `Shared/Simulation/Crafting/QualityCalc` (materials + actions + job tree + minigame + server roll + the small
    bounded term, each capped), the rarity from `QualityThresholds`, and the stats snapshot from the materials plus
    the quality bonus. One operation consumes the reservation and creates the item with its properties, the taint
    when an input is stolen, and the crafter.
  - Class 2 with an immediate save; answered and published (`Crafted`, `Refined`, `Converted`) only after the commit.
  - **Cancel**, a disconnect, leaving the station's range or entering combat releases the reservation. Nothing is
    consumed and the result is never shown before the commit, so a crash cannot reroll a shown result.
  - **Conversion** (R-5.2-04): T to T+1 only, a seeded success roll; failure consumes the inputs (a sink); the daily
    cap per character from `ConversionOdds`.
- **Migration V0013** `craft_ops`: operation key as primary key, kind (craft, refine, convert, mold; enchant and socket
  later), inputs, output instance, quality, rarity, seed, save id. WP-M3-8 and -9 reuse it.
- **M4 hooks.** `ForgeSession` keeps `owner` and `outputRecipient` separate and reads inputs only from a reservation,
  so a contract can own a session and escrow can supply the inputs (spec 5.4); `TODO(M4)` at both.
- **Protocol 31:** `ForgeStart`, `ForgeAction`, `ForgeFinish`, `ForgeCancel`, `ForgeState`, `ForgeResult`.
- **Client.** The forge window v1 (`UI/Menus/Forge`, `HudLayout` `forge_window`): mold or recipe list, a slot picker
  from the bag filtered by tag, action buttons, the quality preview the server sends, the result toast.
- **Bot steps** `craft`, `refine`, `convert`; **smoke step** `forge-sword` and `equip-crafted`.
- **Tests.** `QualityCalc` with a grid of cases and each component at its cap; rarity mapping; crafted stats; the
  conversion roll; the four transaction tests on craft, refine, convert and mold (a crash between the reservation and
  the commit leaves the inputs untouched, a double finish creates one item, two sessions cannot reserve one stack);
  a stolen input taints the output.
- **Shares** the stations and `craft_ops` with WP-M3-8 and -9; `IForgeChallenge` with WP-M3-10; recipes with WP-M3-9
  (refine).
- **Done:** a Blacksmith forges a sword from mined ore and a hide grip at the city forge, equips it and its crafted
  stats show in `OwnStats`.

**WP-M3-7 Job skill trees (spec 5.1 R-5.1-04; decision 9).**
- **Data.** `JobTree` (job, node, row, max level, cost, bonus kind and value per level: yield percent, quality bonus,
  enchant odds, socket odds, job XP), from the design talk or placeholders.
- **Server.** Learn and respec (free, R-5.1-04) through `JobService` (still the only writer of job progress): saved at
  once with a `job_ops` row, answered after the commit. `IJobBonusQuery` returns the bonuses at the start of an
  activity (spec 5.1 "computed when an activity starts"), capped (rule 2). A migration (main+1) only if the learned
  nodes need a table beside `job_progress`.
- **Protocol** main+1: `JobTreeRequest`, `JobTreeResult`, nodes in `JobState`.
- **Client.** A tree tab in the Jobs window (the skill tree window's pattern).
- **Tests.** The four transaction tests on learn and respec; bonus caps; a harvest yield with a bonus.
- **Done:** a Blacksmith spends a job point on the yield node and the next harvest's yield roll reads it.

### Wave 2: enchanting, gems, the minigame and the loop end to end
**WP-M3-8 Enchanting (spec 5.3 R-5.3-04, 3.4; decision 10).**
- **Data.** `EnchantOdds` (per current level, the weights of +1, 0 and -1; the job tree cap), `EnchantBonus` (per
  level, the percent added to the item's stat lines), `EnchantMaterial` (per item tier, the stones consumed).
- **Server.** An enchant request at a station for an equipment instance in the Safe bag equipment area or Equipped:
  - the item's job (`ItemDef.jobId` or its recipe's) must be the character's (`JobRequired`);
  - the materials are reserved, then always consumed (a sink, R-4.2-04); the seeded roll (logged) gives +1, no change
    or -1, never below +0 or above +9; the item is never destroyed;
  - one operation in `craft_ops`, answered after the commit; `Enchanted` published; `EquipmentChanged` when the item
    is equipped, so the stats rebuild.

  The stat source `Enchant` reads `EnchantBonus` at stat time (spec 3.4), through `IEquipmentStatSource`.
- **Protocol** main+1: `EnchantRequest`, `EnchantResult`; the level in the item's properties (WP-M3-2).
- **Client.** An enchant tab at the station window; "+3" on the item name and tooltip.
- **Economy.** The simulator's enchant Monte Carlo: the distribution of attempts and stones to reach +N; lost stones
  counted as an item sink.
- **Bot step** `enchant`; **smoke step** `enchant-sword`.
- **Tests.** The roll and bounds as pure functions; the four transaction tests; the stats of +0 and +3.
- **Done:** a Blacksmith enchants its sword, the result and the stones gone survive a relogin, and the stats follow
  the level.

**WP-M3-9 Gems and the Jeweler (spec 5.3 R-5.3-05, -06, 14.1; decision 11).**
- **Data.** `GemDef` (gem kind, tier, stat rows), `GemSocketOdds` (per tier, the success percent, job tree cap,
  removal cost); rough gem and cut gem rows; the Jeweler's refine recipes and a ring and an amulet recipe (forge
  engine); gem drops on loot tables.

  Equipment gems are a material family of their own (tag `gem`), never the Star Gems of 14.1 (M6), which a future
  `StarGemDef` keeps apart.
- **Server.** Socket and remove at the jeweler's bench (`GemService`, through `InventoryService`), each one class 2
  operation in `craft_ops`, answered after the commit:
  - **socket** (Jeweler only): the item has a free gem slot, a seeded success roll, failure consumes the gem
    (decision 11), success writes the gem into the item's properties; a stolen gem taints the item;
  - **remove**: returns the gem intact for the placeholder cost.

  `GemSocketed` and `GemRemoved` are published. The stat source `Gem` through `IEquipmentStatSource`.
- **Protocol** main+1: `GemRequest`, `GemResult`.
- **Client.** A socket tab at the bench; the gems in the tooltip.
- **Bot step** `socket`; **smoke step** `socket-gem` (a Jeweler smoke character).
- **Tests.** The socket roll as a pure function; the four transaction tests on socket and remove; stats with gems;
  taint.
- **Done:** a Jeweler cuts a rough gem and sockets it into a sword; the sword's stats include the gem.

**WP-M3-10 Forge challenge v1 (spec 5.3 R-5.3-02, `IForgeChallenge`; decision 12).**
- **Server.** The challenge generates its parameters and seed when the session runs; the client sends timestamped
  inputs (`ForgeChallengeInput`); the server validates them against its own timeline with a latency tolerance (the
  view clock and clock-sync limits of D4, `DefenceTimeline`), scores them, and caps the contribution in `QualityCaps`.
  A dropped or impossible input scores the minimum, never fails the craft.
- **Data.** `ForgeChallengeDef` (per job: kind, parameters, tolerance).
- **Client.** The minigame in the forge window (timing bar), heavy on client presentation.
- **Protocol** main+1.
- **Tests.** Scoring as a pure function; inputs at 100 and 200 ms round trip score like at 0; forged timestamps
  outside the tolerance score the minimum.
- **Done:** a Blacksmith plays the timing bar and a better score raises quality within the cap.

**WP-M3-11 HUD and menus v3, and the unlock schedule (spec 9, 12.3).**
- `UnlockSchedule` rows for jobs and gathering at level 12 and crafting after (12.3), with their prompts (the job
  master, the first node in reach, the first forge).
- The `cast_bar` for gather and forge channels if the owners did not finish it.
- Tooltips: quality, crafter, +N enchant, gems, tainted, reserved. Comparing with the equipped item stays out.
- The minimap job-node polish; the JB button and window placement at 1280 x 720 (no window over the minimap or run
  tracker, the 9.13 layout test).
- Client mostly, with the `HudLayout` rows each window owns.

**WP-M3-12 Bots, smoke, economy simulator and the M3 exit run (spec 11.5, 12.5, 10.2; continuous, skeleton in
wave 0).**
- Bot steps from every WP: takeJob, gather, craft, refine, convert, enchant, socket, useItem. The `m3-exit` scenario
  runs the exit criterion end to end on a fresh character (with a test level seed to 12), and `m3-loop` mixes it with
  the M2 loop.
- **Simulator.**
  - New sinks: vendor tools and molds, consumable use, failed conversions, lost enchant stones, failed sockets,
    removal cost.
  - Monte Carlo of the enchant odds and of conversion arbitrage (R-5.2-04: can T1 bought into T3 beat T3 gathered?).
  - The node demand loop settle check.
  - `--check` stays green in CI.
- **Report.** `BotClient report --kind economy` adds item creation and destruction by reason (harvest, craft, used,
  failed conversion, enchant), job XP per activity and the harvest yields.
- **Done:** the exit criterion run is a command; the economy check passes with the M3 rows.

### Continuous
- **QA of main after each wave** (the role file), findings to fixers automatically; the smoke harness grows a step per
  feature.
- **The user's play sessions** keep feeding findings; the orchestrator rebuilds the play stack after merges.
- **Spec upkeep:** every decision goes into the spec file and `13-decision-log.md` in the PR that implements it.

## 7. Dependencies and merge order
- **Wave 0 WPs are independent.** Their shared surfaces:
  - WP-M3-1 owns the activity event records, `IJobQuery` and `JobState`;
  - WP-M3-2 owns `ItemProperties`, `Reserve`/`Release` and the material, tool and mold rows;
  - WP-M3-3 owns the use-item action kind;
  - WP-M3-4 owns the node entity.

  The starter mold grant (WP-M3-1) needs WP-M3-2's rows: the second to merge wires them. WP-M3-4's demand counters
  read `Harvested` from WP-M3-1's records; until WP-M3-1 merges it uses a test publisher.
- **Wave 1 needs wave 0 merged:**
  - WP-M3-5 needs jobs (1), tools and reservations (2) and nodes (4);
  - WP-M3-6 needs jobs (1) and reservations and properties (2);
  - WP-M3-7 needs only jobs (1) and may start as soon as WP-M3-1 merges, after decision 9.

  WP-M3-5 and WP-M3-6 share no files but both add a station or node interaction; they agree in their plans on the
  `NpcInteraction` values.
- **Wave 2 needs wave 1:**
  - WP-M3-8 and WP-M3-9 need the forge engine's stations and `craft_ops` (6);
  - WP-M3-10 needs `IForgeChallenge` (6);
  - WP-M3-11 comes last, since it presents everything;
  - WP-M3-12 grows with each wave.
- **Protocol.**
  - Every WP that adds a message or a snapshot field takes **main+1 at merge time**. The numbers in section 6 are
    proposals in wave order from protocol 26 and reliable id 34.
  - When two PRs take the same number, the orchestrator fixes the order and the later PR renumbers and rebases (the
    M2 practice).
  - Reliable messages stay append-only MemoryPack with a `ReliableMessageCaps` row and an `InputRouter` route; hot
    unreliable messages stay bit-packed.
- **Migrations.**
  - Append only, one per WP that needs a table, from V0012, on the branch's own databases (`--db-suffix`).
  - `MigrationRunner` applies gaps and late lower versions, but a WP that merges after another took its number
    renames to main+1 before merge.
  - Never edit an applied migration.
- **Tables.** New fields on shared tables (`ItemDef`, `NpcDef`, `LootTableEntry`, `NpcVendorStock`, `ActionDef`,
  `AchievementDef`) are `[Optional]`, since other branches add rows to them.

## 8. Risks to watch
- **Rerolling through a crash or a disconnect.** If a result were shown before its commit, a crash would roll back the
  inputs and let the player try again. Every harvest, forge, enchant, conversion and socket result is answered only
  after its save committed. Reservations are memory only and released when nothing committed, and the seed is
  logged.
- **Duplication through reservations.** A reserved unit must be unusable by every other path (move, split, sell,
  deliver, equip, use). The four tests plus a concurrency test per path; the user merges nothing without them.
- **Lost forge sessions** (R-2.1-10). Disconnect, linger, kick and shutdown during a session must release the
  reservation and leave nothing half-created.
- **Gathering bots** (10.3, 7.3). Server-validated channel, diminishing returns, yield tied to job level, telemetry per
  harvest; behaviour scoring stays offline (TODO 7.3).
- **An exit that cannot be tested.** With placeholder numbers, the first craftable sword must need only tier-1
  materials, a level-12 Blacksmith, a vendor pickaxe and a grip that drops in ring 1 or 2. WP-M3-12's `m3-exit` checks
  it from wave 1.
- **The minigame under latency.** Validate on the server's timeline with the D4 view clock and a tolerance; test at
  100 and 200 ms.
- **M4 compatibility.** Contracts need a forge session owned by a contract, the output to the customer, inputs from
  escrow and portfolio counters from `Crafted`/`Enchanted`. Keep owner and recipient apart and inputs from a
  reservation now.
- **Equipment gems versus Star Gems** (14.1): different families, different tables, no shared socket code.
- **Permanent job picks** (decision 2). A pick made by mistake cannot be undone, so the window confirms, the server
  is idempotent, and support repairs go through the admin tools and the audit (`job_ops`).
- **Scope creep from M4 and M5.** Contracts, portfolio, the Merchant, trade of molds, honor loss for stolen inputs,
  the other six jobs, fishing and wood: each gets an interface and a `TODO(M4)`/`TODO(M5)`/`TODO(open)`, not the
  system.
- **Souls-like feel.** Drinking and harvesting are actions with commitment; check them in the 100 and 200 ms play.

## 9. Handover checklist for the M3 orchestrator
1. Read:
   - `CLAUDE.md`, this plan, and `docs/plans/m2-core-loop-plan.md` (process history, section 3a);
   - the memory files (`english-only`, `orchestrator-roles`, `placeholder-animations`, `own-dev-databases`,
     `play-stack-smoke-permission`, `unity-editmode-for-data-rows`, `unity-batchmode-short-path`);
   - the role files in `.claude/agents/`;
   - spec 5.1-5.3, 4.1, 12 and 13.
2. Verify main: `git log`, open PRs, CI on main, `docker compose up -d --build --wait`, `dotnet test Mmorpg.sln`, one
   smoke run through QA. Write the first dated entry of section 3a.
3. Confirm with the user the acceptance checks of section 1 and that balance stays deferred.
4. Put decisions 1 to 5 of section 5 to the user in one message, with the recommendations. Decision 2 already has the
   user's answer that jobs cannot be changed; ask only its remaining points. Record the answers in the spec and the
   decision log before briefing wave 0.
5. Brief wave 0 as four sessions in plan mode (Opus 5.5 high, own worktrees): WP-M3-1, -2, -3, -4. Each brief carries:
   the spec sections of section 4, the WP text of section 6, the interfaces it shares, the standing rules of section 2,
   the proposed protocol and migration numbers, and "done" as written. Start WP-M3-12's skeleton with them.
6. Before wave 1, put decisions 6 to 9 to the user. Offer a short design session for the job trees (decision 9). Brief
   WP-M3-5, -6 and -7.
7. Before wave 2, put decisions 10 to 12. Brief WP-M3-8, -9, -10, -11 and the rest of -12.
8. After each wave: QA of main, fixers for findings, rebuild the play stack, ask the user to play, update section 3a.
9. When the `m3-exit` run passes on main with QA's evidence, ask the user to close M3 and write the M4 handover plan
   in the same shape.
10. Shared notes folder of the M2 orchestrator (hand-over notes and QA evidence per session):
    `C:\Users\Matheus\AppData\Local\Claude-Data\claude_pzs8sxrjxfjjc\side-session-notes\local_e83ffa86-5a4b-4fcf-bdb1-e3f42cd0c7bd\`.
