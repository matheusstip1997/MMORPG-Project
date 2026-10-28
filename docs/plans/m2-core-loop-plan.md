# Plan: Milestone 2, the core loop (handover to the M2 orchestrator)

Prepared 2026-10-26 by the M1 orchestrator at the user's request, while M1 was closing (main at the merge of PR #65).
It is written for a fresh orchestrator session: read it with `CLAUDE.md`, `docs/spec/00-index.md`, `docs/spec/12-delivery-plan-milestones.md`
and the memory files, then start with the handover checklist at the end. Everything here follows the spec; where the
spec says **Open**, this plan lists the question for the user and never answers it.

## 1. Goal and exit criteria (spec 12.2)
**M2: core loop.** Selling drops at outposts (the only gold source), gear and stats, exp and rank, outpost quests and
runs, the tutorial for levels 1-10, the first field zone, the basic skill tree.
**Exit:** a new character can play from level 1 to about 30 through the loop; the economy simulator runs with
placeholder values.

Proposed acceptance checks, to confirm with the user in the first session:
- A scripted bot (and a human) goes from level 1 to 30 on main: kills, picks up, sells at the outpost, buys from the
  NPC vendor, builds and claims runs, learns and respecs skills, equips gear, advances to a tier-1 class at 30.
- Every value-moving operation added in M2 (sale, vendor purchase, run claim, learn/respec, class advance, equip,
  card placement) has its integration tests: double submit, retry, crash in the middle, concurrent access (rule 5).
- The economy dashboard telemetry shows faucet and sink totals per hour; the simulator reports no unbounded loop with
  the placeholder tables (spec 12.5).
- The QA smoke harness covers the loop steps; the 50-bot load run still meets the tick budget with the field zone loaded.
- Bystial book: counters, cards, book window (R-14.2-07 puts it in M2).

## 2. Roles and process (carried over from M1; see `docs/plans/m1-combat-core-plan.md`)
- **Orchestrator**: splits work into packages (WPs), briefs sessions, reviews PRs, relays interface changes between
  parallel packages, dispatches QA findings to fixers without waiting, keeps this plan current. **The user merges every PR.**
- **WP sessions**: one per WP, own worktree and branch, **plan mode first**: the session writes a file-level plan and
  waits for the user's approval before coding (user rule since wave 2 of M1). Model Opus 5.5 at high effort for every
  session the orchestrator starts (user, 2026-10-24).
- **Fixer** and **QA**: role files `.claude/agents/fixer.md` and `.claude/agents/qa.md`. Visible sessions, never
  background subagents. QA findings go to a fixer automatically; design calls go to the user.
- **Temporary design sessions** (like the M1 skills talks) may edit only `docs/design/**` and `src/Tools/SkillTreeEditor/**`
  and report decisions to the orchestrator, who writes them into the spec and the decision log.
- Standing rules (memory files): everything in English; branches with a new migration use their own databases
  (`DbMigrate --dev --db-suffix <x>`, `Run-QaSmoke.ps1 -DbSuffix <x>`); sessions may stop the user's composed play
  stack for a smoke run with exactly `docker compose stop authapi gameserver adminpanel` and must restore it with
  exactly `docker compose up -d --wait` (allow rules exist for those two commands and for `docker compose up -d --build --wait`);
  after a merge that changes server code or a table schema the orchestrator rebuilds the stack without asking.
- Working agreement from M1: whoever merges later takes protocol main+1; a PR that changes a table schema also updates
  the validator and the spec; placeholders (art, numbers) are data so a swap never touches code; the user's play sessions
  drive a findings loop (finding → fixer or small WP → PR), which continues through M2.

## 3a. Where we are (kept by the M2 orchestrator)
- **2026-10-27:** M2 started. Main at the merge of PR #70 (M1 closed), CI green, no open PRs, the play stack current.
  Decisions 1-5 of section 5 answered by the user (recorded in spec 4.1, 3.4, 3.5, 3.6, 10.4, 12.8 and the decision
  log; exp and rank numbers are explicit placeholders). Wave 0 briefed: WP-M2-1, -2, -3, -4 and the WP-M2-12 skeleton
  (leveling bot), each in plan mode. The Warrior K pass runs after WP-M2-2 merges.
- **2026-10-27 (later):** Wave 0 landed. Merged in this order: PR #76 (WP-M2-12 skeleton: the M2 loop with pluggable
  steps, chain presses, `m2-exit` run), #74 (WP-M2-1 items: instances, containers, equipment, starter weapon, V0004,
  protocol 16), #75 (WP-M2-4 outposts: NPCs from markers, selling with per-unit quotes, demand loop, vendor, gold and
  ledger, V0006, protocol 17), #73 (WP-M2-3 exp: level-ups, skill points, achievement counters, outpost rank-up,
  V0005, protocol 18), #72 (WP-M2-2 stats v2: attributes and `AttributeFormula`, gear and level sources, `OwnStats`,
  TOX and SAT, V0007, protocol 19). Each later PR was rebased by its session onto the merged main and reviewed again
  before the user merged it. Fixer PRs from the review and CI: #77 (the bot m2-loop test made deterministic), #78
  (CI timeout flakes: a loot test reading the wrong container, a crowd test counting lost NPC spawns, and a real
  server bug where a character regenerating while its logout save was in flight held its lock for 10 s), #79 (bot
  back-off recovery follows the QA rule: `BotBehaviour.reengageAbovePercent`, `backOffRecoverMs`). Main at a7094d0,
  no open PRs, the play stack rebuilt on it. Wave 0 QA of main started (session `qa-m2-wave0`, report to the shared
  notes folder as `qa-m2-wave0.md`); findings go to fixers, then the user plays. Next: the Warrior K pass (decision
  12), decisions 6-9 of section 5, then the wave 1 briefs (WP-M2-5, -6, -7, -8). Known placeholders to remember while
  playing: interact key T, NPCs wear class avatars, vendor goods do nothing when used (M3), E to D rank-up needs 4
  monster types and 2 outposts (the field zone of WP-M2-7).
- **2026-10-27 (wave 0 fixes):** Wave 0 QA passed (`qa-m2-wave0.md`: every check, the 5-bot `m2-exit` cut at tick
  p99 0.36 ms, 1.2 kills per bot-minute on the M1 field). Its findings: F1 the inventory window layout (fixed, PR #81:
  one tab line, the equipment strip below the grid, body-size slot labels, a layout test on a real panel); F2 the
  loop steps `sell`, `buy` and `equip` still placeholders, no `GoldBalance` reader, entry messages unseen by the loop
  (fixer session `fix-bot-loop-steps`, which also adds a `rankUp` step); F3 the exit criterion "5 bots reach level 30
  within 4 h" out of reach on the M1 field (user decision: keep the criterion, validate it with the field zone's
  higher-level monsters of WP-M2-7; decision log). From CI: PR #82 (bot chain presses reach the server behind or ahead
  of the prediction; the m2-loop test fights a tougher demon). Main at d2ac72d, the play stack on it, the user plays.
  Observations kept for a convenient PR: O1 xUnit2000 in `SaleWorldTests.cs:46` (in the F2 fixer), O2 bot deaths
  (input to the K pass), O3 `rank_history.counters` lacks monster types, O4 a sale without an interact counts no
  visit. Next unchanged: the Warrior K pass, decisions 6-9, the wave 1 briefs.
- **2026-10-27 (wave 1 decided):** the user played wave 0 on main and found the systems good. Decisions 6-9 of
  section 5 answered (recommendations approved; 6 changed: all eight tier-1 class rows, skills only for the root trees
  and for Swordman and Knight as placeholders for tests; recorded in spec 3.5, 3.7, 3.8, 5.5, 14.2, 10.4 and the
  decision log). The Warrior K pass (decision 12) is deferred to a balance phase after wave 2: its session was
  cancelled before any change. Wave 1 briefed: WP-M2-5, -6, -7 and -8, each in plan mode; the F2 fixer
  (`fix-bot-loop-steps`) runs alongside. Finished wave 0 sessions archived; the QA evidence is kept in the shared
  notes folder (`qa-m2-wave0-evidence`).
- **2026-10-28 (wave 1 landed):** all four wave 1 PRs reviewed by the orchestrator and merged by the user in this
  order: PR #89 (WP-M2-5 skill tree v1 part 1: learn, upgrade, evolve, respec, class advance at 30, saved hotbar, the
  eight tier-1 class rows; protocol 20, realm V0008), PR #87 (WP-M2-8 Bystial book; protocol 21, V0009), PR #90
  (WP-M2-5 part 2: every node of the four root trees plus Swordman and Knight has a `SkillDef` row, placeholders with
  `TODO(WP-M2-9)` where a mechanic is missing), PR #88 (WP-M2-6 outpost quests and runs; protocol 22, V0010), PR #91
  (fixer: `UdpLinkRelayTests` tolerates runner stalls) and PR #86 (WP-M2-7 the first field zone as a greybox, four
  rings, seven monster types and the Ashen Warlord, mid-zone outpost, grid v2 run-length, manifest v3; in its rebase
  the zone monsters got families, Bystial card rows and quest item drops). Main b9986fe: protocol 22, realm schema
  V0010. Three of the PRs had taken protocol 20 and V0008 at the same time, so the orchestrator assigned the numbers
  and each later PR renumbered and rebased once. Two CI flakes surfaced and were fixed in the same wave (#90 fixed
  the skill-tree end-to-end test's hotbar read; #91 the relay timing). The play stack was rebuilt after each merge.
  Pending user calls recorded in the PRs: WP-M2-6's forfeit per step, one kill per step and delivery-in-one-call
  readings and its two questions (should the bot's run step accept deliver offers; should a board always carry a kill
  offer); WP-M2-8's family threshold and build-up resistance readings; WP-M2-5's two `TODO(open)` items (points back
  when a table change drops a learned node; respec and a weapon a cleared unlock allowed). The CC0 art for the zone
  monsters is a follow-up session (downloads approved by the user, nothing committed yet). Next: QA of main after
  wave 1, the user plays, decisions 10 and 11 of section 5, then the wave 2 briefs.

## 3. Where M1 left things (main at PR #65, 2026-10-26)
Done: backlog A-J of spec 12.4. Transport, snapshots, sessions and login; world export, collision and regions (city and
combat area); movement with prediction; the shared `ActionMachine` with effects, hit shapes, projectiles, dash, 4-hit
normal attack chains (R-3.1-10), stamina, dodge and parry charges, i-frames, parry colors, poise, flinch and stagger;
elements, statuses, build-ups (Burn), ground areas, heals and target filters; the four root kits; monster AI, two
monsters and the boss; `InventoryService` v1 (Normal bag, stackables only, audit, idempotency), loot tables and ground
drops, pickup; HUD v1 with inventory window, name plates, target lock (R-2.2-06), telegraphs, chat; persistence
(realm migrations V0001 characters, V0002 items, V0003 admin actions), event bus, telemetry; BotClient with link relay
and the 50-bot run; combat debug tools and table hot reload; admin panel v0; the one-command dev stack.

Tables on main: ActionBindings ActionDef ActionEffectDef AdminSettings AuthApiLimits BotBehaviour BotLinkProfile
BotScenario BuffIconRegistry BuildUpDef CharacterCreationConfig CharacterSlots ChatSettings ClassDef ClientCamera
ClientPrediction ContainerDef DamageConstants DevTools EffectBuildUpDef ElementCaps EntityVisualDef EventBusLimits
GroundAreaSettings HitShapeDef Hotkeys HudLayout HudSettings HudThemeTokens InventorySettings InventoryWindowSettings
ItemDef ItemIconRegistry LoginLayout LoginScreens LoginSettings LootSettings LootTableDef LootTableEntry
MonsterAiSettings MonsterDef NetSecurity ProjectileDef ProjectileSettings RankFactor RarityDef ReasonCodeStrings
RegionDef RegionDefaults ReliableMessageCaps ResourceRegen ServerDirectory ServerLoop SessionSettings SpawnPoints
StartingValuesM1 StatDef StatusEffectDef StatusModifierDef TargetLockSettings TelemetrySettings UiStrings.
Server folders: AI Admin Combat Dev Entities Events Host Items Law Net Persistence Sessions Social Telemetry World.
Tools: BotClient ClientCore DataValidator DbMigrate DevConnect SkillTreeEditor. Protocol 15 after the parry PR.

Carried over from M1 (not blockers for M2 but on the list):
- **Real combat numbers for one class** (M1 exit criterion, deferred by the user on 2026-10-26 for gameplay feel):
  either a short K pass for the Warrior or a spec 12 amendment moving it to the start of M2, recorded in the decision log.
- **Playtests at 100 and 200 ms with 3-4 players** and the group boss fight: need the parked "second client" WP
  (client overrides for Auth URL and game host, a Windows development player build with `MMORPG_DEV_LOGIN`, a compose
  publish-address variable with the R-2.5-02 dev-only exception) and the playtest protocol of spec 12.4 K.
- Known placeholders: the faint uncolored telegraph (off by data when real monster animations land), bots not pressing
  through the attack chain, melee chain hits with no forward advance (user note 2026-10-27: with the real attack
  animations each melee hit of the chain should carry a small forward step; today the character stays in place and it
  feels flat; solve it with the real animations, as a per-hit `Dash` distance in the chain rows or root motion kept in
  step with the server, never as a client-only offset), Flash's blind (TODO(M2)), the dodge/parry open items of spec 10.4 [M1] (parry cost, what a
  parry does to the attacker, boss phases and threat, overlapping walkable surfaces in the exporter).
- The server input apply rule change (catch-up on backlog, PR #67) was confirmed by the user on 2026-10-27 after play;
  body collision (R-2.2-07, PR #68: monsters block, players pass through) landed the same day and was confirmed in play.

## 4. Spec sections to read per area (before briefing a WP)
| Area | Spec | Notes |
|---|---|---|
| Items, containers, equipment, instances | 4.1 (R-4.1-01..04), 9.13 | stackables only today; instances, Safe bag, equipment area, Quest bag are M2 |
| Economy, faucet, demand, prices, vendors | 4.2 (R-4.2-01..07), 12.5 | outpost sale is class 1 (synchronous); FeeService exists in spirit only (fees are M4) |
| Loot | 4.3 M1 notes | party loot and death bags are M4 |
| Stats and formulas | 3.4 (R-3.4-01..07) | primary attributes and their formulas are TODO(open) |
| Classes, skill tree, advancement | 3.5 (R-3.5-01..08), design `docs/design/skill-trees.json`, `skills-m1-proposal.md` | arts are M6 |
| Exp, rank, achievements | 3.6 (R-3.6-01..05) | curves and requirements are config, numbers Open |
| Statuses and build-ups | 3.7, 3.8, 10.4 [M2] | concrete lists and numbers Open |
| Quests and runs | 5.5 (R-5.5-01..04) | exp only, server-generated, demand-scaled |
| World, regions, instances, streaming | 2.3, 11 (exporter) | the field zone needs more regions and towns, no instances yet |
| Characters, creation | 2.1, 9.10 | creation screen "designed later" (10.4 lists it under M4); M2 keeps the placeholder |
| HUD and menus | 9.3, 9.4, 9.12, 9.13 | exp bar, minimap, TOX/SAT, buff icons, Safe and Quest tabs, equipment area |
| Bystial book | 14.2 (R-14.2-01..07) | thresholds, slots, families Open |
| Unlock schedule | 12.3 | levels 1-30: what the tutorial introduces and when |
| Operations, save classes | 7.1, 7.4 | class 1 for sales, class 2 for exp, learn, book |

## 5. Decisions the user must make (Open in the spec; ask before the WP that needs them)
Before wave 0 (**all five decided 2026-10-27**, user approval of the orchestrator's recommendations; the text of
record is spec 4.1 "M2 item model", 3.4 "M2 primary attributes", 3.5 R-3.5-07, 3.6 "M2 placeholder curves", 12.8
and the decision log; the exp and rank numbers are placeholders by the user's explicit note):
1. **Item model**: rarity tier list (R-4.1-01 names "common" only), equipment slots and the two weapon slots (R-3.5-07),
   which gear stats exist at M2 without crafting (fixed per `ItemDef` row, since the forge is M3), gem slots and
   enchant level stay as fields but inert until M3.
2. **Primary attributes** STR, DEX, STA, INT per level per class and the formulas to derived stats (R-3.4-01/02): a
   placeholder formula is fine if the user says so; it must be written down.
3. **Exp curve, band bonus and penalty curves, rank requirements per rank** (R-3.6-02/03/04): placeholders allowed
   (spec 12.5), but the shapes (how many kills per level around 1-30) decide whether the exit criterion is playable.
4. **Field zone content**: size, biome, monster roster for levels 1-30 with recommended levels (R-3.6 implementation),
   how many outposts and NPC vendors, whether the zone is authored in Unity by the team or generated as a sample.
5. **Weapon change rule** (R-3.5-07, Open for M2 with gear): when can the equipped weapon set change (out of combat
   only? at the Safe bag?).
Before wave 1 (**all four decided 2026-10-27**, user approval of the orchestrator's recommendations with one change on
6: every tier-1 class row exists, but skills ship only for the root trees and for Swordman and Knight as placeholders
for tests; the text of record is spec 3.5 "M2 wave 1 decisions", 3.7 "M2 status list", 3.8 "M2 numbers", 5.5 "M2
parameters", 14.2 "M2 decisions", 10.4 and the decision log; every number is a placeholder for the balance phase):
6. **Skill tree numbers**: max level per node, point cost, points budget per tier-1 class, the minimum levels, which
   tier-1 classes ship in M2 (all eight, or the four the kits lead to), the mechanics named by the trees (stun,
   knockback, blind, paralyze, provoke and threat, riposte, MP regeneration while channeling, explosion on impact).
7. **Status list and build-up values** (10.4 [M2]): the concrete statuses the tier-1 skills need, stack caps, cleanse
   and strip priority, buff row capacity.
8. **Run and quest parameters**: step bonus, forfeit penalty, active quest cap, which activities the generator offers.
9. **Bystial book**: tier ladder beyond rare, thresholds per type and for bosses, slots per rank, family list, effect list.
Before wave 2:
10. **Tutorial content** for levels 1-10 and what "leave the tutorial at 10" means in the world (a separate area or
    the city with gates), the minimap marker set, NPC vendor stock and prices.
11. **Character creation**: keep the placeholder (name and root class) through M2, or design the screen now.
12. **The M1 numbers criterion**: decided (spec 12.2 at the M1 close, 12.8): a Warrior K pass at the start of M2, scheduled after WP-M2-2 merges.
    **Deferred 2026-10-27** (user decision): the numbers stay placeholders; balance is a later phase of M2, after wave 2,
    for all four classes and the field zone's monsters together (spec 12.8). The session started for it was cancelled
    before any change.

## 6. Work packages
Each WP: own session in plan mode, own branch and PR, file-level plan in the PR. Waves group WPs that touch disjoint
areas and can run at the same time; the order inside a wave does not matter. Every WP that adds a table adds its schema,
validator rules and spec update; every transaction gets the four integration tests; every refusal gets a reason code
and a `ReasonCodeStrings` row; new events go into spec 8.1; new tables into 8.5.

### Wave 0: foundations (parallel; nothing in it depends on another M2 WP)
**WP-M2-1 Item instances, containers and equipment (G, spec 4.1).** Instance rows with a unique id and the versioned
JSONB properties field; `ItemDef` categories equipment, consumable instance, quest item, material; `RarityDef` tiers
(decision 1); containers Safe bag (equipment area and valuables area), Quest bag, Equipped, with `ContainerDef`
capacities and accepted categories; equipment slots and the two weapon slots (R-3.5-07) as data; `InventoryService`
`Move(container)`, `Equip`/`Unequip` as audited idempotent operations; migration V0004 (append only); the `Bag` shared
rules extended for instance stacks; `InventoryContents` carries containers (protocol bump, MemoryPack append only);
stolen and non-tradable flags carried but inert (M4/M5). Tests: the four transaction tests per operation, bag rules.
Done: a character equips a placeholder sword from the Safe bag and its stats change (with WP-M2-2's hook).

**WP-M2-2 Stats v2 (spec 3.4).** Primary attributes per class and level (decision 2), derived stats from
`StatBlock` sources: class base, level, gear (from WP-M2-1's equipped instances through an interface the two WPs agree
on in their plans), statuses; `StatsChanged` event (8.1) and the own-entity stats message for the HUD (9.4); toxicity
and satiety meters as fields with decay from timestamps (R-3.4-03), no consumers yet; `StatDef` completed; every
multiplier capped (rule 2). Tests: pure formula tests with a grid of cases; a stats sync test.
Done: the HUD resource panel shows TOX and SAT; equipping gear changes Atk on the server and the client.

**WP-M2-3 Exp, level, rank and achievements (spec 3.6).** `ExpService` consuming `Killed` (per contributor; parties
are M4, so the killer), `ExpCurve`, `RankLevelRanges`, `ExpBandCurves`, `RankModifier`, `LevelPenalty`, `LevelUp` event,
skill point per level (R-3.5-04) as a counter the skill tree spends later; `AchievementService` counters (idempotent
per event, the same counters R-14.2-01 reuses), `AchievementDef` v0, `RankRequirements`, `RankService.TryRankUp` as an
outpost interaction (needs WP-M2-4's outpost entity; stub the interaction behind an interface until it lands);
monster recommended level range in `MonsterDef`; telemetry. Save class 2 for exp. HUD: exp bar and level on the
resource panel, level-up toast, rank on the character detail panel.
Done: killing demons levels a character to 5 in the dev scene; a rank-up at the outpost works end to end in a test.

**WP-M2-4 Outposts, selling, demand, vendors (spec 4.2, 12.5).** Outpost and vendor NPC entities from exported
markers (world: `npc` markers in the exporter and a new `NpcDef` table, name proposed here, not yet in spec 8.5), an interaction request with range and a reason code
(`OutOfRange`); `PriceService.Quote`/`Sell` (per-unit sequential multiplier, floor, recovery) as a **class 1 synchronous
transaction** through `InventoryService` (remove items, add gold, audit, commit before confirming); gold on the character
row (migration, append only) and in the HUD; `DemandService` decayed counters per type and region, the once-a-minute
control loop with clamped and smoothed multipliers feeding `SpawnController` (respawn scale, floor) and the price and
reward multipliers; NPC vendor purchase (gold destroyed, item created) with `NpcVendorStock`; `SinkLedger`; economy
telemetry snapshots (R-4.2-07) and a `report` command in BotClient or a small tool that prints faucet and sink totals.
Tables: `PriceBase`, `RarityMult`, `PriceFloorRecovery`, `DemandCurve`, `NpcVendorStock`. Client: an outpost window v1
(quote, confirm, result) and a vendor window v1 built like the inventory window (9.13). Tests: price quote and demand
loop as pure functions, sale transaction tests, a simulation test that the multiplier recovers.
Done: a character sells demon horns, sees the quote fall, buys a potion, and the telemetry shows the gold created and destroyed.

### Wave 1: progression and content (after wave 0; WP-M2-7 can start with wave 0 if the world team is free)
**WP-M2-5 Skill tree v1 (spec 3.5, design `skill-trees.json`).** A generator from the design file to `SkillTree`,
`SkillDef` (inheritance with overrides, resolved by a validation tool), `SkillEvolution` and the class rows of tier 1;
learn, upgrade, evolve, respec and class advance as atomic idempotent transactions with the checks of R-3.5-04 and the
reason codes (`SkillNotLearned`, `RespecBlockedCombat`, level and prerequisite refusals); one point per level from
WP-M2-3; weapon requirements and `WrongWeapon` with WP-M2-1's equipped weapon set; the hotbar becomes per character
(learned skills placed by the player; `ActionBindings` stays the default layout); `ClassAdvanced` recomputes stats and
the loadout; passives as stat modifiers from a fixed trigger list. The tier-1 skills themselves: the design file's
nodes for the classes shipping in M2 (decision 6), implemented as `ActionDef`/effect rows with placeholder clips, in
one WP per root if the count is large (split in the plan). Client: a skill tree window v1 (learn, upgrade, respec,
advance, drag to the hotbar). Tests: transaction tests, generator round trip, a prediction test that a learned skill
predicts like a kit skill.
Done: a level-30 character advances to a tier-1 class, learns its first skill and uses it from the hotbar.

**WP-M2-6 Outpost quests and runs (spec 5.5).** Server-generated activities (kill X, deliver X, return a quest item)
from the demand board; `Run` and `RunStep` state (migration, append only), accept (rewards locked), progress from
`Killed`, `ItemObtained`, `ItemDelivered`, forfeit with penalty, claim paying exp through `ExpService` with the step
bonus (`RunBonus`, `ForfeitPenalty`, `QuestCaps`, `OutpostQuestDef`); quest items in the Quest bag (WP-M2-1); the
board as a cached snapshot. Client: quest board and run builder v1 at the outpost, a run tracker on the HUD (9.3).
Tests: run lifecycle transaction tests, claim idempotency, exp per step.
Done: a character builds a two-step run, completes it and claims the exp.

**WP-M2-7 The first field zone (C, F, spec 2.3, 11).** World authoring in Unity: the zone next to the combat area
with its regions (open world flags), towns or outposts, spawn points for a monster roster covering levels 1-30
(decision 4; several types reusing the M1 AI with new `MonsterDef` rows, recommended levels, loot tables, placeholder
art from the CC0 packs), resource node markers as data only (gathering is M3); exporter v1: several regions, region
borders drawn by the dev scene, larger grid, the overlapping-surfaces answer if the zone needs bridges (spec 10.4 [M1]);
terrain streaming on the client if the zone is larger than one scene (R-2.3-04); interest and grid settings checked
with the 50-bot run in the zone. Tests: exporter round trip, DataValidator on the world, a bot run in the zone.
Done: a character walks from the city gate through the zone and meets monsters of rising level.

**WP-M2-8 Bystial book (spec 14.2).** `monster_kill_counts` and `bystial_book` (migration), `BystialService`
consuming `Killed` idempotently (shared counters with WP-M2-3's achievements, agree in the plans), `BystialCardDef`,
`BystialSlots`, `BystialFamilyBonus` (decision 9), card evolution at milestones, place and swap with reason codes
(`NoFreeSlot`, `CardNotEarned`, `AlreadyPlaced`, `InCombat`), `StatBlock` source `bystialCard`, events. Client: the book
window (collection, counters, slots). Tests: counter idempotency, evolution thresholds, placement transactions.
Done: after 100 demon kills a card appears, placing it changes a stat.

### Wave 2: the loop end to end
**WP-M2-9 Statuses and mechanics the tier-1 skills need (spec 3.7, 3.8, 10.4 [M2]).** The concrete status list and
numbers (decision 7), cleanse and strip priority, on-hit statuses riding on strikes (so they can be dodged), the tree
mechanics of decision 6 that are new effect kinds (stun, knockback, blind including Flash's, paralyze, provoke and
threat in the monster AI, riposte, MP regeneration while channeling, explosion on projectile impact), build-up values.
Shared code first, data per skill after. Tests: `ActionMachine` and world tests per mechanic.

**WP-M2-10 Tutorial and unlock schedule (spec 12.3).** Data-driven onboarding: an `UnlockSchedule` table (new, name proposed here; level →
systems introduced, prompts, UI elements shown), contextual prompts like the pickup prompt from `UiStrings`, the tutorial
area or gates (decision 10), the minimap v0 with the marker set, the "leave the tutorial at 10" transition. Client-heavy;
server side only what gates need (region flags or level checks on the gate interaction).

**WP-M2-11 HUD and menus v2 (spec 9).** Exp bar, level and rank, TOX and SAT meters, buff icons (`BuffIconRegistry`),
the run tracker, the Safe and Quest bag tabs and the equipment area in the inventory window, stack splitting, the
shortcut row with the menu buttons (inventory, skills, book, quests), the character sheet with stats, item tooltips
with stats. Each window follows the 9.13 pattern (UI Toolkit, `HudLayout` row, `UiStrings`, reason-code toasts).

**WP-M2-12 Economy simulator, dashboard and the leveling bot (spec 12.5, 10.2, J).** `EconomySimulator` tool
(Monte Carlo over sell prices, demand loop stability, vendor sinks with the M2 tables), a dashboard query or report
over the economy telemetry (faucet and sink totals, prices, multipliers), a BotClient scenario that plays the loop
(fight, pick up, sell, buy, run, learn) and the `m2-exit` run from level 1 to 30; bots press through the attack chain
(carried over). Done: the exit criterion run is a command.

### Continuous
- **QA of main after each wave** (the role file), findings to fixers automatically; the smoke harness grows a step per
  loop feature (sell, buy, run, learn, equip, book).
- **The user's play sessions** keep feeding findings; the orchestrator rebuilds the play stack after merges.
- **Spec upkeep**: every decision above goes into the spec file and `13-decision-log.md` in the PR that implements it.

## 7. Dependencies and merge order
- Wave 0 WPs are independent, but WP-M2-1 and WP-M2-2 share the gear-to-stats interface: both plans name it; the second
  to merge adapts. WP-M2-3 needs WP-M2-4's outpost for the rank-up interaction; it stubs the interaction behind an
  interface until #4 lands.
- Wave 1 needs wave 0 merged: WP-M2-5 needs points (3), weapons (1) and stats (2); WP-M2-6 needs exp (3), the Quest bag
  (1) and the outpost (4); WP-M2-8 needs the counters of (3) and stats (2); WP-M2-7 only needs the exporter and may start early.
- Wave 2 needs wave 1: WP-M2-9 before the tier-1 skills that use its mechanics are considered done; WP-M2-10 and
  WP-M2-11 last, since they present everything; WP-M2-12 grows with each wave (start its skeleton in wave 0).
- Protocol: every WP that adds a message or snapshot field takes main+1 at merge time; reliable messages stay
  append-only MemoryPack with a `ReliableMessageCaps` row; hot unreliable messages stay bit-packed.
- Migrations: append only, one per WP, dev databases per branch.

## 8. Risks to watch (spec 10.3 applied to M2)
- The exit criterion is a content and curve question as much as code: without a plausible exp curve and a zone with
  enough monsters, "1 to 30" is unplayable. Decide 3 and 4 early and put the leveling bot in from wave 0.
- Empty-systems fallbacks: at 10-15 players the demand loop and the outpost board must behave with one seller; the
  simulator checks it.
- Scope creep from M3-M5 systems (crafting, trade, parties, loot bags): the plan names where each is deferred; a WP that
  needs one adds the interface and a TODO(M3/M4), not the system.
- Duplication: every new value-moving transaction gets the four tests before merge; the user merges nothing without them.
- Souls-like feel under latency stays a standing risk: the field zone and the new skills go through the 100/200 ms play.

## 9. Handover checklist for the M2 orchestrator
1. Read `CLAUDE.md`, this plan, `docs/plans/m1-combat-core-plan.md` (process history), the memory files
   (`english-only`, `orchestrator-roles`, `placeholder-animations`, `own-dev-databases`, `play-stack-smoke-permission`),
   the role files in `.claude/agents/`, spec 12 and 13. Check that the plans and role files are committed on main; the M1
   orchestrator kept them in its worktree until the user asked for a PR.
2. Verify main: `git log`, open PRs, CI on main, `docker compose up -d --build --wait`, `dotnet test`, one smoke run
   through QA. Confirm the pending user decisions of section 3 (input apply rule, the numbers criterion).
3. Put decisions 1-5 of section 5 to the user in one message, with recommendations; record the answers in the spec and the
   decision log before briefing wave 0.
4. Brief wave 0 as four sessions in plan mode (Opus 5.5 high, own worktrees), each with: the spec sections of section 4,
   the WP text of section 6, the interfaces it shares, the standing rules of section 2, and "done" as written.
5. Start WP-M2-12's skeleton (the leveling bot) with wave 0 so the exit criterion is measurable from the first merge.
6. After each wave: QA of main, fixers for findings, rebuild the play stack, ask the user to play, update this plan's
   "where we are".
7. Shared notes folder of the M1 orchestrator (hand-over notes per session):
   `C:\Users\Matheus\AppData\Local\Claude-Data\claude_pzs8sxrjxfjjc\side-session-notes\local_6ddb0ba9-c4d8-42d4-b253-0d6189225c80\`.
