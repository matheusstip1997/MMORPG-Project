# MMORPG specification index

This folder is the source of truth for the game's design and implementation rules. Read the file for the system you are changing before you change it. Rules are numbered (`R-<section>-<n>`, for example `R-3.3-02`) so code, tests, and reviews can reference them. Rules marked **Open** are not decided: do not invent them, ask.

**Start here for Milestone 1:** `m1-quick-spec.md`.

| File | Contents |
|---|---|
| `m1-quick-spec.md` | the short version of what Milestone 1 needs |
| `01-overview-architecture.md` | game summary, stack, principles, solution layout, threading and tick loop, network protocol, event bus |
| `02-foundation-systems.md` | accounts, realms and sessions, networking and movement, world/grid/interest/instances/regions (with default flags by area), visibility, network security |
| `03-combat-classes-progression.md` | action machine, lag compensation and hits, projectiles, poise/dodge/parry, stats and damage formula, classes and skills, exp and rank bands, status effects |
| `04-items-inventory-economy.md` | items and containers, gold sources and sinks, prices, demand, fees, NPC vendors, loot and death, stolen items, trade/auction/shops |
| `05-jobs-crafting-contracts-quests.md` | jobs, gathering/refining/conversion, forge/enchant/gems, contracts and portfolio, quests and runs |
| `06-pk-law-guilds-arena-raids-social.md` | PvP/honor, disguise robe, arrest/jail/guards, guilds/territories/sieges, social services, ranked arena, raids |
| `07-operations-platform.md` | save classes, global and realm databases, telemetry, bot control, admin tools, Steam, security |
| `08-cross-system-reference.md` | event catalog, transaction catalog, how systems talk, region flags, config tables |
| `09-client-ui-hud-login.md` | HUD layout and element registry, bindings, modes, config, login screens, raid window |
| `10-phases-testing-risks-open-items.md` | phases, testing, risks, open items tagged by milestone |
| `11-technical-setup-delivery.md` | Unity version and structure, serialization, environments and CI, AWS cost plan, load tests, UnityExporter |
| `12-delivery-plan-milestones.md` | milestones, unlock schedule, Milestone 1 backlog, release gates, Milestone 1 starting values |
| `13-decision-log.md` | dated decisions and the sections they changed |
| `14-constellar-bystial-book.md` | Constellar system (star points, the five-slice board, Star Gems; M6) and the Bystial book (kill-count cards, book slots by rank, family bonuses; M2) |

Flow diagrams are Mermaid (rendered by GitHub and most editors). The HUD map image is in `img/`.
