# 10. Phases, Testing, Risks, Open Items

## 10.1 Suggested phases
1. **Foundation (including a first HUD with the elements of section 9, the three environments, CI, and the UnityExporter of section 11):** `Shared` library, tick loop, `World` + grid + interest, `IVisibilityQuery` interface, Steam auth, persistence, **inventory service with audit**, config tables, stat modifier system, event bus, **telemetry**, admin tools, bot-client load tester, `UnityExporter` for collision/regions.
2. **Core gameplay:** action/combat state machine, hit shapes, projectiles, poise, parry/dodge, damage formula, a slice of the class tree, drops, selling at outposts, skill tree.
3. **Progression and crafting:** exp, rank, achievements, jobs, gathering, forge, enchant, gems, quests and runs.
4. **Economy and social:** demand loops, fee service, trade, auction, shops, contracts, portfolio, loot bags and party loot, party finder.
5. **PK, guilds, arena:** honor, flag, stolen items, black market, arrest, jail, lawless city, robe, guilds, territories, sieges, reputation, guard hire, ranked arena and seasons.
6. **Scale and polish:** instances at load, raids, bosses, status effects, monster AI, tuning from telemetry.
The detailed milestone plan, unlock schedule, and the Milestone 1 backlog are in section 12.
Hard-to-retrofit items belong in phase 1: the inventory service, config-driven data, shared protocol/data library, modifier sources, event bus, telemetry, `IVisibilityQuery`, region flags, `FeeService`.

## 10.2 Testing strategy
- **Unit:** damage, quality, price quotes, exp, honor, loot roll, fee split (all pure functions).
- **Integration:** every transaction in 8.2 including crash-in-the-middle, double submit, and concurrent claim.
- **Simulation:** Monte Carlo for enchant odds, conversion arbitrage, price decay, demand loop stability; economy faucet/sink balance.
- **Load:** bot clients (hundreds), siege-size instance, crowded city, many simultaneous arena matches.
- **Exploit tests:** duplication, honor farming, alt loops, ban evasion.

## 10.3 Main risks
Scope (about 25 interlocking systems); empty systems at low population (auction, shops, demand loops: plan fallbacks); economy tuning needs real data (config + telemetry); content volume (animations, VFX, monsters, 32 classes); souls-like combat feel under latency; duplication exploits; identity leaks around disguise; bots at gathering and farming.

## 10.4 Open items (tagged with the milestone that needs them)
- **[M2, carried from M1 at its close on 2026-10-27]** Real combat numbers for one class (replace the starting values in 12.7), then the other three roots.
- **[M1]** Whether a parry costs stamina: R-3.1-02 says actions cost stamina, spec 12.7 lists costs only for attacks, skills and the dodge (the M1 parry costs 0 until decided).
- **[M1]** What a successful parry does to the attacker (poise damage, stagger, a riposte window): in M1 it only voids the hit (spec 3.3 ParrySystem).
- **[M1]** Boss phases and threat: whether a boss changes attacks or behaviour at HP thresholds, and whether it switches targets by threat while its target is valid (the M1 boss has neither; spec 3.1, WP10).
- **[M1]** How world data represents overlapping walkable surfaces (bridges, multi-floor interiors, caves): `UnityExporter` v0 plans a heightmap, which holds one height per point; server collision and movement validation need the same answer. Still open after the first field zone (WP-M2-7): its layout has no bridge or other overlapping surface.
- **[M2]** Decided 2026-10-27 as placeholders (spec 3.5 "M2 wave 1 decisions"): the skill-tree mechanics the shipping trees name (stun, provoke and threat, blind, MP regeneration while channeling, an explosion on projectile impact; knockback, paralyze and riposte wait for their tier-2 classes), built by WP-M2-9 (2026-10-28, spec 3.7 M2 implementation), max level and point cost per node, the minimum levels and the point budget. Also decided 2026-10-27: when a character can change its equipped weapon and the weapon type list (spec 4.1 M2 item model, R-3.5-07). Still open: the max range of each location-targeted skill (set on the skill row as each is implemented).
- **[M2]** Decided 2026-10-27 as placeholders: build-up values and effects (spec 3.8 "M2 numbers"); the status list, stack caps, cleanse and strip priority and the buff row capacity (spec 3.7 "M2 status list"); run and quest parameters (spec 5.5 "M2 parameters"); the Bystial book ladder, slots, families and effects (spec 14.2 "M2 decisions"). Still open: NPC vendor stock and prices (plan decision 10, before wave 2). Exp, band and rank-requirement curves and the monster recommended levels have placeholder shapes since 2026-10-27 (spec 3.6, 12.8); the real numbers, with the Warrior's (12.8), are the balance phase's tuning item.
- **[M2]** Decided 2026-10-28 (user): the tutorial is ring 1 with a level-10 gate at the first pass and a data-driven
  `UnlockSchedule` (spec 12.3); the minimap v0 marker set (spec 9.3); the M2 vendor stock, three placeholder rows at
  every vendor (spec 4.2); the character creation contract, four root classes and an opaque appearance blob, the
  placeholder screen through M2 (spec 9.10). What stays open: the screen design and appearance options (with the
  avatar art pipeline), the vendor numbers (balance phase), job nodes and territory colors on the minimap (M3, guilds).
- **[M3]** Wood and weapon-grip sourcing; job XP split between gathering and crafting; Jeweler socket success rates and gem removal; whether molds can be traded.
- **[M4]** Loot window lengths after testing; character creation screen (appearance options, restoring a deleted character); screens not mocked yet (outpost quest board and run builder, forge, auction and shops, inventory, skill tree, jobs, journal, contracts, guild, map, death/jail overlays).
- **[M5]** Honor weights and decay; jail-time formula; Bandit stealing rules and combat bonuses; Guard rewards and advantage; Merchant carry capacity.
- **[M6]** Gauges for the classes still on placeholders (and the Dragon Awakening buffs, the Light Storm overcharge buffs, the General's War Specialist); check class identity before level 100.
- **[M7]** Arena skill-point pool, lives per mode, rating and season values, builds per mode, minimum class tier for ranked, whether spawn protection ends early on attacking; raid lockouts, raid list, boss loot tables, rewards; the 12th hotbar key; where the armed, robe, mount, and pet toggles live.
- **[M8]** Guild alignment and war declaration rules; siege objectives and caps; what restricted gold can buy; territory list; the siege page.
- **[Later]** When to open a second (regional) realm; Global Accelerator; movable HUD elements; chat moderation tooling; localization; key-binding menu; controller support.
- **[Before alpha]** Team ownership and revenue agreement; Terms of Service, EULA, privacy policy, age rating; monetization limited to cosmetics.
- **[Tuning]** All economy and law values: simulate first, tune with telemetry, finalize in the closed alpha.
