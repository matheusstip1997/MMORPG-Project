# Milestone 1 quick spec (for day-to-day work)

This is the short version of what Milestone 1 needs. The full rules are in the numbered spec files; rule IDs (for example `R-3.3-02`) point to them.

## Scope
- A small **safe city** (hub) with a small **combat area** next to it, **two monster types** and **one boss**; placeholder assets.
- The **4 root classes** (Warrior, Archer, Mage, Assist), each with a fixed starting kit of 5 skills from its root tree plus its own normal attack (`docs/design/skills-m1-proposal.md`); no skill tree yet; the class gauge is shown **locked** (it unlocks at level 100).
- Login screens (title, server selection with one realm, character selection, creation as a stub), a minimal HUD, basic drops and inventory, persistence, telemetry, the dev environment, CI, and the bot smoke test.

## Out of scope for M1
Crafting, jobs, the economy loops, trade, auction, quests and runs, PK and honor, guilds, arena, raids. Do not build them yet.

## Rules that M1 must follow
- Server authoritative; config-driven values; event bus; `InventoryService` is the only writer of items; atomic, idempotent operations (sections 1.4, 4.1).
- 20 Hz tick, input frames with sub-tick timestamps, prediction and reconciliation (1.6, 1.7, 2.2).
- Actions: startup, active, recovery; movement stops during actions; facing locked at start; input buffer; no heavy attacks; attacks and skills cost stamina (3.1).
- Dodge with i-frames and its own charge pool; parry with its own charge pool, colored attacks (red/yellow/blue), uncolored damage reduction; poise and stagger (3.3).
- Lag compensation: pending hits for monsters hitting players, position rewind for players hitting monsters, 200 ms cap (3.1).
- Damage formula v1 and stat modifiers (3.4); projectiles with `maxHits` and swept collision (3.2).
- Network security basics: signed tickets, encrypted reliable channel, integrity tag on client inputs, rate limits (2.5).
- Save classes: synchronous transfers, class 2 every 1 s, volatile state every 10 s, ordering barrier (7.1).
- HUD layout and element registry (9.2, 9.3), login screens (9.10).

## Starting values
See section 12.7 (`StartingValuesM1`). Every number is a placeholder in config.

## Backlog
Workstreams A-K in section 12.4, in the suggested order: repo/CI/persistence, networking and session, world and movement, combat core, classes and monsters, inventory/HUD/tools, tuning and playtests.

## Exit criteria
- 4 root classes playable; boss beatable by 3-4 players.
- Combat feels right at 100 and 200 ms of simulated latency (round trip; spec 12.4, user decision 2026-10-26); tick p99 within budget with 50 bots.
- No duplication in inventory concurrency tests; telemetry recorded; CI green; one-command dev environment.
- Real combat numbers defined for one class and stored in config.
