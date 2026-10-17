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
- **[M1]** Real combat numbers for one class (replace the starting values in 12.7), then the other three roots.
- **[M2]** Build-up values (gains, effect damage and durations, repeat-penalty window, decay delay and rate, boss caps); concrete status effect list and numbers (stack caps, cleanse/strip priority, buff row capacity); exp, band, and rank-requirement curves; NPC vendor stock and prices; monster recommended levels.
- **[M3]** Wood and weapon-grip sourcing; job XP split between gathering and crafting; Jeweler socket success rates and gem removal; whether molds can be traded.
- **[M4]** Loot window lengths after testing; character creation screen (appearance options, restoring a deleted character); screens not mocked yet (outpost quest board and run builder, forge, auction and shops, inventory, skill tree, jobs, journal, contracts, guild, map, death/jail overlays).
- **[M5]** Honor weights and decay; jail-time formula; Bandit stealing rules and combat bonuses; Guard rewards and advantage; Merchant carry capacity.
- **[M6]** Gauges for the classes still on placeholders; check class identity before level 100.
- **[M7]** Arena skill-point pool, lives per mode, rating and season values, builds per mode, minimum class tier for ranked, whether spawn protection ends early on attacking; raid lockouts, raid list, boss loot tables, rewards; the 12th hotbar key; where the armed, robe, mount, and pet toggles live.
- **[M8]** Guild alignment and war declaration rules; siege objectives and caps; what restricted gold can buy; territory list; the siege page.
- **[Later]** When to open a second (regional) realm; Global Accelerator; minimap marker set; movable HUD elements; onboarding and tutorial content details; chat moderation tooling; localization; key-binding menu; controller support.
- **[Before alpha]** Team ownership and revenue agreement; Terms of Service, EULA, privacy policy, age rating; monetization limited to cosmetics.
- **[Tuning]** All economy and law values: simulate first, tune with telemetry, finalize in the closed alpha.
