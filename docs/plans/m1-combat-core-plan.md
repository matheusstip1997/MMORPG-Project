# Plan: rest of the M1 combat core (backlog D3, D4) and the first monster

Status (2026-10-23): waves 1-3 merged (PRs #14-#18, #20-#22), plus the skill design (#19) and the hand-bone test
fix (#23). Wave 4 is ready to start; a second skills discussion is running and may change WP9. This session is the
orchestrator: each work package (WP) below runs in its own session, worktree, branch and PR. Packages in the same wave
touch disjoint files and can run at the same time.

## Roles
- **Orchestrator** (this session): splits work into packages, briefs sessions, reviews their PRs, relays interface
  changes between parallel packages, and sends problems to the fixer and validation requests to QA.
- **Work package sessions**: one per WP, in plan mode. Each writes its plan and waits for the user's approval before
  coding, then opens a PR to main. The user merges.
- **Fixer** (`.claude/agents/fixer.md`): gets review or QA findings, reproduces them, fixes the root cause on a
  `fix/<slug>` branch from origin/main with a regression test, and opens a PR. It stops and returns a plan for anything
  larger than a local fix (more than 3 files, a protocol, schema or migration change, a spec conflict, an Open rule).
- **QA** (`.claude/agents/qa.md`): when the orchestrator asks, starts the local stack, runs every suite and the live
  scenarios, collects evidence, and reports pass/fail plus findings for the fixer. It never changes code.
- **Skill design** (temporary session): discusses the class kits and the skill tree with the user, writes
  `docs/design/skills-m1-proposal.md`, and reports to the orchestrator; its report briefs backlog E.
- Fixer and QA work runs as visible sessions in the sidebar (user, 2026-10-23), each briefed to follow its role
  file, so the user can watch and talk to them. QA findings go to a fixer session automatically, without waiting for
  the user; design calls stay with the user.
- Everything is written in English.

## Where we are
Done: A, B (transport, snapshots, sessions, login), C (world, grid, exporter, collision), D1 (movement, prediction),
D2 (ActionMachine, effects, dash), D5 (StatBlock, DamageCalc, resource meters, not yet on entities), H, entity visuals.

What blocks playing a fight today:
- Strike and Projectile effects fire into `NoActionEffectSink` (`src/GameServer/Combat/ActionRunner.cs`): nothing is hit.
- Server entities have no stats, HP, poise or faction; snapshots carry no HP.
- There is nothing to hit: no monsters, and in M1 players cannot damage players (`PvpRules`, spec 6: both must be past
  the first class change at level 30).
- Stamina, dodge/parry charges, i-frames and parry are TODO(D4) in `ActionDef`.

## Wave 1 (parallel)

### WP1 Hit shapes (Shared only)
- `Shared/Simulation/Combat/HitShapes`: cone, box, capsule in integer cm against a target circle (radius), relative to
  the attacker's position and locked facing (`FixedTrig`); swept segment vs circle for projectiles (spec 3.2).
- Table `HitShapeDef` (one row per shape, `effectId` [Ref `ActionEffectDef`], kind, offset, size fields), because
  config rows have no nested objects (spec 3.1). Validator: every Strike has at least one shape; only Strike and
  GroundArea have shapes; sizes in range. Update `ActionEffectDef` docs (TODO(D3) shapes) and spec 3.1.
- Placeholder rows for the existing strikes in `data/tables/HitShapeDef.json`.
- Tests: geometry grid of cases (edges, behind, at range, every facing octant), validator rules.
- Done: `dotnet test` green; the table loads in `ConfigLoader` and `DataValidator`.

### WP2 Combatants and the first monster (server + snapshot + client spawn)
- `Combatant` state on `Entity`: `StatBlock`, `ResourceMeters` (HP, MP, stamina), faction (player, monster), alive/dead,
  poise `{current, max, lastHitTick, immuneUntilTick}`, `flinchImmuneUntilTick`, short position history ring (for the
  lag compensation of WP4, filled in phase 2).
- `ClassDef` v0 (4 root rows): base attributes, `heavyArmor` (R-3.3-05), `flinchResist`, `flinchDurationPercent`,
  `flinchImmunityMs` (R-3.3-07), poise max. Players get their combatant from their character's `ClassId` at spawn.
- `MonsterDef` v0: stats, HP, poise, flinch fields, visual id (`EntityVisualDef`), action set for later AI. Two monster
  types and the boss rows can be stubs; this WP needs one.
- `SpawnPoints` table + markers in the exported world; basic `SpawnController` (phase 7) with the 30 s respawn
  (spec 12.7); death and respawn in phase 8 (monsters only; player death is WP4). No AI yet: monsters stand still.
- Snapshot: HP bucket for others, exact HP/MP/stamina for the own entity (spec 1.7 snapshots, spec 9 HUD data sources);
  protocol version bump. Client: monsters spawn with their visuals (existing `EntityVisualDef` path).
- Done: server and Unity tests green; in the dev scene a monster stands at its spawn point.

### WP3 Stamina and charges in the ActionMachine (Shared, predicted)
- `ActionDef` fields `staminaCost`, `chargePool` (none, dodge, parry), `iFramesStartMs`, `iFramesMs` (dodge),
  and validator rules (i-frames inside the action; dodge 250 ms of i-frames per spec 12.7).
- `ChargeMeter` (spec 3.3) computed on read; dodge 3 charges / 4 s, parry 3 / 5 s from `StartingValuesM1`.
- The machine reads stamina and charges through a small interface passed to `ActionMachine.Step`, spends them on start
  (parry spends its charge even if it later fails, R-3.3-03), refuses with new `ActionReason`s (`NotEnoughStamina`,
  `NoCharges`) and their `ReasonCodeStrings` rows. Client prediction uses the same code; charges reconcile from the
  snapshot like the action state.
- Done: Shared and Unity tests green; pressing dodge four times in a row is refused the fourth time on both sides.

## Wave 2 (after WP1 and WP2 are merged; WP3 any time)

### WP4 HitResolver, poise and flinch (server phase 5)
- Strike effects queued in phase 3, resolved in phase 5 in entity-id order (spec 3.1): shapes vs grid neighbours,
  filters (faction, `PvpRules.CanDamage` stub that refuses players vs players in M1, dead targets), rewind of monster
  positions for player attacks (cap 200 ms, R-3.1-03), then per target: i-frames → parry (hooks, filled by WP6) →
  `DamageCalc` → `PoiseSystem` → `FlinchSystem` (`ActionMachine.Interrupt`) → apply HP → `HitLanded`, `Damaged`,
  `Staggered`, `Flinched`, `Killed`, `Died`. Sets `LastCombatAtMs`. Seeded `IRng` for crits.
- A hot hit message to observers in interest (target, amount, crit flag) so clients can show damage numbers later (I);
  the client logs it and shows the monster's HP bucket change.
- Done: a player kills the standing monster with normal attacks; it respawns after 30 s; integration test over a real
  LiteNetLib client.

### WP5 Projectiles (server phase 4 + client)
- `ProjectileDef` table (speed, range, radius, `maxHits`, `stopsOnWorld`) referenced from Projectile effects.
- `ProjectileSystem`: pooled, capped per owner, swept collision against the world grid and grid neighbours, no double
  hits, resolves through WP4's per-target resolve.
- Hot spawn message to interested clients; clients simulate the straight path and fast-forward late spawns; hit and
  despawn events. Visual: a placeholder arrow.
- Done: `skill.arrowShot` hits the monster at range and stops at walls.

## Wave 3 (prepared; three sessions in parallel, each plans first)
State on main after wave 2: strikes and projectiles hit monsters through `HitResolver` (damage, poise, flinch, death,
respawn, hot hit message 4, projectile messages 3 and 5, protocol 5). Monsters stand still. I-frames are checked at
resolve time on the server, parry is a no-op hook, and a dead player stays dead.

### WP6 Defence on the defender's timeline (D4, rest)
- Pending hit for monster attacks on players: waits up to the defender's half RTT, capped at 200 ms, for a dodge or
  parry input judged by input timestamps (R-3.1-03, R-3.3-02). PvP attacker rewind of 100 ms stays a TODO (no PvP in M1).
- `ParrySystem`: charge spent at start (already in the machine), colors and uncolored reduction (R-3.3-04, changed by
  the user on 2026-10-22: red none, yellow heavy, blue medium or heavy, green anyone), armor class light/medium/heavy
  from `ClassDef` (R-3.3-05; `armorClass` replaces `heavyArmor`; which class wears which is Open, decided in the skill
  design session), `parryClassPve/Pvp` on `ActionDef` (R-3.3-06), results enum to the client (spec 3.3),
  `ParryAttempted` event, reason texts. WP6 updates spec 3.3 and the decision log in the same change.
- Telegraph color in `ActionStarted` and its client message (R-3.1-05; 3 bits now that there are four colors).
- Clock-sync limits on input timestamps (R-3.1-03).
- Owns: `HitResolver`/`StrikeResolver` steps 1-2, ActionDef parry fields, the parry result message. Tests drive a
  monster's ActionMachine directly; it does not need WP7's AI.

### WP7 Monster AI and the second monster type (F)
- Aggro through `IVisibilityQuery`, chase with the shared `MovementSim` and grid collision, attack by driving the same
  `ActionMachine` from AI (ActionRunner TODO(F)), leash back to the spawn, deterministic in entity-id order.
- `MonsterDef` action set; validator: every monster attack has `startupMs >= MonsterAttackStartupMinMs` (R-3.1-03),
  placed on `MonsterDef` so `ActionDef.cs` stays WP6's.
- Two monster types with different attack patterns (rows, actions, effects, hit shapes); the boss is the next wave.
- Needs a decision before it merges: **player death and respawn** is Open (`GameWorld.cs`: where, when, with what HP).
  The WP7 plan must ask the user and record the answer in the spec.
- Owns: monster entity, AI folder, phase 7 AI step, `MonsterDef` and monster rows. Conflicts with WP6 are limited to
  `ActionRunner` (WP6 adds the color to `ActionStarted`; WP7 drives monster machines) and the data JSON files.

### WP-QA0 QA smoke harness (J, part)
- An editor `-executeMethod` entry point that, against a running local stack, logs in with the dev login, enters the
  world, walks to the demon, attacks until it dies, fires an arrow at a wall, and saves screenshots plus a JSON result
  per step (under `artifacts/qa/`). Runs without `-nographics` so the QA agent can look at the screenshots.
- A PowerShell script that starts the stack with generated secrets (as `UI/README.md` does), runs the harness and the
  live end-to-end test, and stops everything.
- Owns only new files under `unity/Assets/Game/Dev/Qa/` (or `Tests/`) and `tools/qa/`; no gameplay code.

### After wave 3
- QA run of main with the harness; findings go to the fixer.

## Wave 4 (ready; three sessions in plan mode, each plans first)
State on main after wave 3: monsters `m1.demon` and `m1.alien` aggro, chase, attack and leash (`GameServer/AI`);
`m1.cyclopsBoss` is a stub row. Monster hits on players are pending hits judged on the defender's timeline
(`DefenceTimeline`, `PendingHits`); parry colors red/yellow/blue/green with `ClassDef.armorClass` (`ParrySystem`,
`ParryColors`); dead players respawn after 5 s at the city spawn; protocol 7. The QA smoke harness
(`tools/qa/Run-QaSmoke.ps1`) runs the login-to-kill scenario in about 94 s.

Before wave 4: one QA session runs the smoke harness and every suite on main (first check of waves 1-3 together);
its findings go to a fixer session.

Order: WP8 and WP10 can start at once. WP9 starts after the second skills discussion reports, since its decisions may
change the Warrior and Archer kits.
Input: the skill design (PR #19, merged 2026-10-22): `docs/design/skills-m1-proposal.md` (kits, decisions, "What
backlog E needs"), spec 3.1/3.4/3.5 as changed there. No new effect kind is needed.

**Animations and VFX are placeholders for now (user, 2026-10-22); the real ones come later.** Every skill, normal
attack and boss attack gets its own action id and its own entry in the visual data (`EntityVisualDef` / animation
sets), even when several share one placeholder clip. The final animation then replaces data and assets only, never
code or timings (visuals follow the config timings, spec 12.1). Each WP lists the placeholders it used in its PR, and
`THIRD_PARTY_LICENSES.md` stays current.

### WP8 Elements, status effects, heal and target filter (D6, first part)
- Elements: the 10 of R-3.4-07 in `ElementCaps` and the resistance stats of `StatDef` (both still have the old list),
  an `element` field on Strike, Projectile and GroundArea effects, DamageCalc step 5 wired in `HitResolver`.
- `StatusEffectDef` table and a status runtime in phase 6: apply, refresh, expire by tick, stat modifiers through
  `StatBlock` (source type buff/debuff), regeneration changes; rows for the four kit statuses (staminaBuff,
  magicBarrier, lifeRegeneration, burn as a debuff placeholder) plus the "one buff, one debuff" of 12.4 D.
- `Heal` effect with a heal amount (percent of Matk), `ApplyStatus` / `RemoveStatus` resolved.
- Target filter `targets` (self, allies, enemies) on Heal, ApplyStatus, RemoveStatus and GroundArea; `HitShapeDef`
  rows allowed on them (self needs none). In M1, allies are all other players.
- The own entity's statuses reach the client for the buff row (spec 9); protocol bump.
- Owns: ActionEffectDef element/targets/heal fields, StatusEffectDef, phase 6. Ground areas and build-ups are WP11.

### WP9 Class kits, part 1 (E): costs, bindings, Warrior and Archer
- `ActionDef.mpCost` and `NoMp` (start check: cooldown, charges, stamina, MP; nothing spent on a refusal), predicted
  on the client like stamina.
- `ActionBindings` keyed by `classId`, one row per class with its own normal attack; a character uses its class's
  row; client hotbar binding per class.
- Dash direction offset (Back Step: 180 degrees); the Archer's Dash uses no dodge charge.
- `ClassDef.armorClass` data: Warrior heavy, Assist medium, Archer light, Mage light (decided 2026-10-22).
- Warrior kit (Slash, Head Cut, Piercing Attack, Jump Strike) and Archer kit (Piercing Shot, Multi Shot, Heavy Shot,
  Back Step, Dash): ActionDef, ActionEffectDef, HitShapeDef and ProjectileDef rows; placeholder animations through
  EntityVisualDef. The Warrior's Stamina buff slot waits for WP8's statuses (wave 5).
- Owns: ActionDef mpCost, ActionBindings, the Dash offset field, the Warrior/Archer rows. Expect JSON conflicts with
  WP8 and WP10 and one field conflict with WP8 in ActionEffectDef.

### WP10 The boss (F)
- One boss type (`m1.cyclopsBoss`, a stub row today) on top of WP7's AI: telegraphed attacks with WP6's colors
  (red, yellow, blue, green), super armor phases, a large poise bar, its own spawn point and the 10 min respawn
  (spec 12.7), beatable by 3-4 players.
- Boss data (MonsterDef row, actions, effects, shapes, visuals) and any AI pattern the boss needs beyond WP7.
- Owns: boss rows and boss-specific AI code. No HUD boss bar yet (I).

## Wave 5 (started 2026-10-24, after waves 1-4 merged: PRs #14-#31)
WP11 and WP12 run in parallel in plan mode. WP11 owns the engine (GroundArea runtime, BuildUpDef, gain and reduction
fields); WP12 owns every kit row and ActionBindings. Whoever merges second wires Fire Spark (Burn +25), Flamethrower
(area cone, Burn +5 per tick) and Simple Cleanse (-40). Open for the user: the dodge's direction rule (movement keys or
facing); the Archer's Dash follows it.

Also started 2026-10-24, in parallel (user): WP13 HUD v1 (I; client UI, boss engagement and local chat are questions
for the user in its plan) and WP14 inventory v1, loot and ground drops (G; server, migration, pickup, minimal client; the
inventory window comes after WP13). Protocol: WP11, WP13 and WP14 may all bump; whoever merges later takes main + 1.
Remaining after these: J (BotClient and the 20-bot test, combat debug tools, admin panel v0), A leftovers (all services
in compose, table hot reload, staging deploy with the user), the inventory window, then K tuning with players.

## Wave 6 (started 2026-10-25; main 0f5bfd1, protocol 11, PRs #14-#38 merged)
- QA of main after waves 4-5 (all four classes through the smoke, HUD review); findings go to a fixer session.
- WP15 BotClient v1, latency and loss simulation, 20-bot CI smoke, 50-bot tick p99 run (J).
- WP16 inventory window with move requests (G client part).
- Local chat (I follow-up, session started by WP13, approved by the user) plans first.
- Wave 7 (2026-10-26, merged): WP17 combat debug tools and table hot reload (#52), WP18 admin panel v0 and the
  one-command dev environment (#51, kick reason codes #53), skill design update (#50), flaky chat test (#54). QA of
  main after wave 6 found no game bug (12/12 smoke runs, 50-bot p99 2.63 ms); its harness and UI findings F1-F6 are
  with the fixer (branch `fix/qa-wave6`).
- Play feedback round 1 (2026-10-26, user played main, all merged and confirmed in play): #56 dodge dash (input
  direction, backwards with none; Archer Dash same rule; decision closes the dodge direction TODO(open)) and grounded
  locomotion (root Y baked by the builder); #57 pickup prompt; #58 target lock on Tab (R-2.2-06, facing lock; design
  answers in its plan); #59 direction-aware locomotion blend. Standing permission: sessions may stop the composed
  play stack for smoke runs (allow rules in the user's settings for the two exact compose commands).
- User direction (2026-10-26): balance and real numbers (K) are deferred; the focus is gameplay feel, driven by the
  user's play sessions (findings go to fixers or small WPs). Design note recorded in spec 3.3/9.3: uncolored attacks
  are the monsters' default attacks and get no telegraph in the final game; M1 keeps the faint uncolored telegraph as a
  placeholder until the real monster animations land.
- Play feedback round 2 (2026-10-26, merged): #60 spec (uncolored attacks = default monster attacks, no telegraph
  in the final game; K deferred), #61 stagger marker on monster name plates, #62 relay test deflake, #63 4-hit normal
  attack chain for all four classes (R-3.1-10; hit 4 finisher; bots do not press through the chain yet, fold into the
  next BotClient change), #64 harness fix for the double-Tab clear (a second Tab within 300 ms of the locking Tab
  clears instead of cycling: rule as designed, user may revisit). Dev-env rule: after a merge that changes server code
  or a table schema, `docker compose up -d --build --wait` (the orchestrator does it without asking).
- M2 handover: `docs/plans/m2-core-loop-plan.md` (prepared 2026-10-26 for the M2 orchestrator: goal, roles, state of
  main, spec map, user decisions, WPs in three waves, dependencies, risks, checklist).
- Later: K tuning. Backlog A-J is complete; the remaining M1 exit criteria are human ones: combat feel at 100 and
  200 ms with 3-4 players, and real combat numbers for one class in the tables. Needs the user to pick the class and
  schedule playtests; a session can prepare the playtest protocol, the tuning sheet per table, and a first proposed
  number set to react to.
- K preparation, parked (user decision 2026-10-26, single host is enough for solo play): WP "second client" = client
  command-line overrides for the Auth URL and game host, a Windows development player build with `MMORPG_DEV_LOGIN`,
  a compose publish-address variable (R-2.5-02 dev-only exception on a private network), docs. Needed when the 3-4
  player playtests of K start (spec 12.4 K, 12.1 staging). Latency on one host: the bot link relay or clumsy.
- Design in progress with the user: skill arts (section 3.5, from the second class; three arts per skill unlocked by
  stat checks), to be written into the spec once the open questions are answered.

### Wave 5 outline (original)
- WP11 Ground areas and the build-up framework with Burn only (D6, second part): GroundArea runtime with the
  target filter, damage and poise per tick, never flinch (R-3.3-09); build-up gain per hit, decay, element resistance
  (R-3.8-10), Burn at the threshold; a build-up reduction effect (Simple Cleanse).
- WP12 Class kits, part 2 (E): Mage and Assist kits, and the Warrior's Stamina buff, on WP8 and WP11.
- Then HUD v1 (I), inventory and drops (G), bot client and tools (J), tuning (K).

## Open points for the user (from the skill design)
- Element tags on Fencer, Barbarian, Berserker and Sword Master: in the editor's file but not final (user,
  2026-10-22); keep them out of the spec 3.5 class table until the user confirms them.

## Open points to settle before or during the work
- `HitLanded` for a hit absorbed by i-frames (existing TODO(open) in `CombatEvents.cs`).
- Payload of `Damaged`/`HitLanded` beyond what spec 3.1 names (element, crit): WP4 adds only what it needs and marks the
  rest TODO(open).
- Whether WP2's HP bucket size (bits) is a config value or a protocol constant: proposal, a protocol constant, since
  both sides must agree on the layout.
- Working agreement: every WP touches more than 3 files. Proposal: the WP session writes its file-level plan in its PR
  description and starts at once, since this plan is the approval; say if you want to approve each one first.
