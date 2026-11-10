# City layouts (main map)

Status: **design sketch, v2 (2026-11-10)**, from a design talk with the game designer (2026-11-09) and the designer's answers to the art reference questions (2026-11-10, `reference-boards-questions.md`). The decisions (E1-E17) are the designer's. **The three main city plans are accepted** (E15), and so is **M6's layout** (E16). The medium city and small town templates are still **proposals to review**.

This file is **not the spec**. The map and the settlement ids are in `world-map.md`. The style and size rules (A1-A8, B1-B7) are in `art-direction.md` and the biomes in `biome-sheets.md` (both in PR #179). All names are placeholders.

## What the spec already fixes

- **Normal cities:** PvP is off, and guards attack bad-honor players (R-6.3-01).
- **The lawless city:** PvP always on, open to everyone, bad-honor players safe from guards (R-6.3-01); it holds the black market, for bad-honor players only (R-4.4-02).
- **Fast travel** only between the 3 main cities (R-2.3-01).
- **Raids** start only in cities (R-6.7-02, flag `raidStart`), and the designer put the raid gates in the 3 main cities (`world-map.md` D13).
- **The arena queue** only from cities (R-6.6-01, flag `arenaQueue`).
- **NPC kinds today** (`NpcKind`): outpost (sell loot, rank up, quests), vendor, job master, forge, jeweler's bench.
- **Player shop stalls** are entities in the world, with a cap per city (spec 4.5).
- **Respawn** at the nearest city or town (`world-map.md` D7).
- **Sizes** (`world-map.md`, `art-direction.md`):
  - a main city is about 170 m across, a medium city about 90 m, a small town about 45 m;
  - one screen shows about 20-42 m across;
  - streets are 6 m wide, lanes 3 m, bridges 5 m;
  - doors are at least 2 m wide;
  - buildings snap to the 50 cm grid and face N/E/S/W (B7);
  - only service buildings have interiors (B6);
  - roofs and tall walls fade when they hide the player (A2).

## Decisions (designer, 2026-11-09)

| # | Decision | Notes |
|---|---|---|
| E1 | **Three tiers of services** (table below): main cities have everything, medium cities have the outpost, vendors, both crafting stations and a few player stalls, and small towns have an outpost, one vendor and the respawn point. | Travel to a bigger city stays meaningful, which supports the Merchant job (R-5.1-02). |
| E2 | **Walls by tier:** main cities have full walls, medium cities have a palisade, and small towns are open. **Every walled settlement has one gate per road** that reaches it. The gate is where the safe zone starts. | Medium cities: 2 gates for M1 and M4, 3 gates for M2, M3 and M5. |
| E3 | **Three separate building kits**, one per main city, each with its own culture. | The most art work of the options; chosen for character. |
| E4 | **C1 is a water city:** canals with flowing water run through it, and it has a port where ships can dock. | C1 touches the north coast on the map, so the port faces north. |
| E5 | **C2 is a caravan bazaar city.** | |
| E6 | **C3 is a classic medieval city** with walls and buildings. | |
| E7 | **Medium cities and small towns borrow the nearest main city's kit**, reduced, with their own biome's materials and props on top. **M6 gets its own scrap-built look.** | Three kits, plus M6, cover the map. |

## Decisions (designer, 2026-11-10)

From the designer's answers to the art reference questions. The "Q" numbers refer to `reference-boards-questions.md`, which has each question and what the boards found.

| # | Decision | Notes |
|---|---|---|
| E8 | **M2 uses the water kit.** | Q23. This was open. |
| E9 | **Kit details.** Water kit: the upper floors are timber boards without pale panels, and every building keeps a quay or a pale stone edge between its roof and the water. Bazaar kit: the dome is whiter, a lime-washed white, and the kit has a muted teal glazed tile as its one colour accent. Bazaar and medieval kits: every wall has a darker band at its foot. | Q17, Q19, Q20, Q21, Q22. The hex values on the reference boards are proposals. |
| E10 | **Roofs.** Roof direction is mixed by place: gables on squares and wide streets where there is room, eaves to the street in lanes and 6 m streets. Snow lies on roofs in patches, so a kit's roof colour still shows on every building. | Q11, Q18. |
| E11 | **The bazaar's cloth roof is made of panels.** All panels over the street hide while a player is under it, and the shade stays on the ground. Cloth and banners may use muted versions of the warning hues. | Q10, Q5. |
| E12 | **C3's walls.** The moat is about 8 m wide. Each of the five gates, the tutorial gate included, has a bridge with a timber deck. The battlements are scenery. The keep stays within about 15 m. | Q25, Q14, Q24. |
| E13 | **The three sea bridges share a look of their own:** one neutral style of stone and timber, with a deck dark enough for attack warnings. | Q26. They take no city's kit. |
| E14 | **Services.** The inn is a "social place and we may add some food seller" (the designer's words). Fast travel is a travel stone on the plaza, the same object in all three main cities. Mail and the auction have places in a main city: a mailbox and an auction house. The number of stall places is left to economy tuning, and markets are built so that rows of stall places can be added or removed. | Q27. **The spec says the M4 plan decides mail and the auction (R-4.5-03, 10.4). This answer and that plan still have to be brought together.** |
| E15 | **The three main city plans are accepted,** with the decisions above. | Q31. |
| E16 | **M6's layout is accepted** as written below: medium-sized, scrap walls with a gate per road, the black market in the middle around a forge-lit yard, no safe square, cover and more than one way out of every yard. | Q41. M6's services stay Open. How cover works with the fade is tried in the fade prototype (Q38). |
| E17 | **Scrap walls get a darker band at the foot,** like the walls of the bazaar and medieval kits (E9). | Q39. On a bare rock street the band is as dark as the rock; the W4 board has the numbers. |

## Services by tier (E1)

| Service | Main city (C1-C3) | Medium city (M1-M5) | Small town (T1-T11) |
|---|---|---|---|
| Respawn point (D7) | yes | yes | yes |
| Adventurer Guild outpost (sell, rank up, quests) | yes | yes | yes |
| NPC vendors | several | several | one |
| Forge and jeweler's bench | yes | yes | no |
| Player shop stalls | yes (the largest cap) | a few | no |
| Job master | yes | no | no |
| Fast travel (R-2.3-01): a travel stone on the plaza (E14) | yes | no | no |
| Inn: a social place, may get a food seller (E14) | yes | not decided | not decided |
| Mailbox and auction house (E14) | yes | not decided | not decided |
| Raid gate (D13) | yes | no | no |
| Arena queue (R-6.6-01) | yes | Open | no |
| Guards (R-6.3-01) | yes | yes | Open (`world-map.md` O5) |
| Walls (E2) | full wall, a gate per main road | palisade, a gate per road | none |

**The lawless city M6** is a medium city by size, but its services are Open: the black market is there (R-4.4-02), there are no guards, and whether it has an outpost, crafting stations or stalls isn't decided.

## Kits and which settlement uses which (E3, E7)

The materials are proposals, with the designer's decisions of 2026-11-10 (E9) worked in. Which settlement borrows which kit is a proposal too, except M2 (E8).

| Kit | Main city | Look (proposal, with E9) | Also used by (proposal, with E8) |
|---|---|---|---|
| Water kit | C1 | pale stone quays and bridges, upper floors of timber boards (no pale panels), grey slate roofs, water wheels, mooring posts; a quay or pale stone edge between every roof and the water | M1 (moor), M2 (old forest), M3 (rime coast), T1 (amber woods), T2 (lakes), T3 (fungal hollow) |
| Bazaar kit | C2 | clay walls with a darker band at the foot, white domes and flat roofs, cloth awnings, carved wooden screens, caravan yards, a muted teal glazed tile as the one colour accent | M4 and T6 (dune isle), T4 (steppe), T5 (canyons) |
| Medieval kit | C3 | grey stone with a darker base course at the foot of walls, timber frames with plaster, tiled roofs, battlements, a keep | M5 (taiga), T7 (frost peaks), T8 (storm cliffs), T9 (ash woods), T10 (palm coast), T11 (jungle) |
| Scrap look | M6 | salvaged wood and metal, chains, scorched cloth, built on black rock; walls with a darker band at the foot (E17) | M6 only |

- **Biome dressing on top:** each borrowed kit gets its biome's materials and props from `biome-sheets.md`. For example, M3 uses the water kit with snow in patches on the roofs (E10) and ice on the quays, and T11 uses the medieval kit overgrown with vines.
- **M2 uses the water kit** (E8). By road it is slightly nearer to C2 (901 m) than to C1 (1,055 m), but a forest town fits the water kit's timber better than desert clay.
- **Roofs matter most.** The camera mostly shows the tops of things, so each kit needs a roof shape and color that identifies it at a glance: slate grey for the water kit, white domes for the bazaar kit (E9), warm tile for the medieval kit. All stay muted (A3), and the biome dressing never covers a roof fully (E10).
- **The sea bridges** are in no kit: the three of them share one neutral style of stone and timber (E13).

## Main city plans (accepted 2026-11-10, E15)

Each plan follows the roads that reach the city on `world-map.svg`. A main city is about 170 m across, about 4 to 8 screens across, and a player crosses it in about 40 s.

**Shared rules for all three:**
- **A central plaza** holds the respawn point, the Adventurer Guild outpost and the travel stone for fast travel (E14), so a new or returning player always starts at the same place.
- **A street 6 m wide** leads from the plaza to each gate.
- **Districts along the streets:** a market (vendors and player stalls), a crafting quarter (forge, jeweler's bench, job masters), and a hall for the raid gate and the arena queue.
- **An inn, a mailbox and an auction house** (E14). Where each stands in a city is not set.
- **Roofs:** gables on squares and wide streets where there is room, eaves to the street in lanes and 6 m streets (E10).
- **The tutorial gate** is a small separate gate to the city's tutorial area (`world-map.md` D10), away from the main roads.
- **Service buildings** have interiors (B6), and everything else is a closed facade.

### C1, the water city (Green vale)

- **Roads and gates:** a south-west gate (road to M2) and an east gate (road to M1). The north side is the sea.
- **Port (north):** docks with moored ships, warehouses and a harbour square. The ships are scenery: fast travel isn't by ship, because C2 is inland (see Open).
- **Canals:** a main canal runs from the port south to a basin in the middle of the city, with two side canals. The water flows toward the sea.
  - Canals are unwalkable water, about 6 m wide, with low quay edges.
  - Bridges over them are 5 m wide, and nothing walkable passes under them, so they fit the one-layer grid.
  - Every building keeps a quay or a pale stone edge between its roof and the water (E9).
- **Plaza:** on the central basin.
- **Market:** along the quay between the plaza and the port.
- **Crafting quarter:** by the east gate, on a side canal with water wheels.
- **Raid gate and arena hall:** by the south-west gate.
- **Tutorial gate:** west, toward the tutorial area south-west of the city.
- **Chokepoints inside:** the canal bridges. There is no PvP inside (R-6.3-01), so they only shape the routes.

### C2, the caravan bazaar city (Red sands)

- **Roads and gates:** a north gate (road to M2), a north-east gate (road to T4) and a south gate (road to T5).
- **The bazaar street:** one long covered street from the north gate to the south gate, lined with vendors and player stalls. Its cloth roof is made of panels, which all hide while a player is under it (A2); the shade stays on the ground (E11).
- **Plaza:** in the middle, where the street from the north-east gate meets the bazaar, around a well.
- **Caravan yards:** an open yard inside each gate, with pack animals and stacked goods.
- **Crafting quarter:** east of the plaza, toward the north-east gate.
- **Raid gate and arena hall:** west of the plaza.
- **Tutorial gate:** west, toward the tutorial area.
- **Shade:** streets run under awnings and between high clay walls, which fade when they hide the player.

### C3, the classic medieval city (Reed marsh)

- **Roads and gates:** four gates, north (road to T9), west (road to T10), east (road to M6) and south (road to T11).
- **Plan:** two main streets cross at the central plaza and split the city into four quarters.
- **Moat:** the marsh's river feeds a moat about 8 m wide around the walls, with a 5 m timber-decked bridge at each of the five gates, the tutorial gate included (E12).
- **Quarters:**
  - north-west: the market;
  - north-east: the crafting quarter;
  - south-east: the raid gate and arena hall, with the tutorial gate beyond it;
  - south-west: the keep (scenery, within about 15 m, E12) and houses.
- **Walls:** stone, with a darker base course (E9) and towers at the gates. They fade when they hide the player. The battlements are scenery (E12).

## Medium city template (proposal)

- **Size:** about 90 m across, two to three screens.
- **Palisade** with a gate per road: 2 gates for M1 and M4, 3 gates for M2, M3 and M5.
- **One square** in the middle with the respawn point and the outpost.
- **Around the square:** vendors, the forge, the jeweler's bench and a few places for player stalls.
- **At a bridge** (M3, M4 and M5 each sit at a bridge end): the gate faces the bridge, so the city reads as its gatehouse.

## Small town template (proposal)

- **Size:** about 45 m across, roughly one screen.
- **Open,** with no wall. The main road runs through it.
- **On the road:** the outpost, one vendor and the respawn point, with a few house facades and a well or shrine in the middle.
- **Safe zone edge:** if a town is a safe zone, the edge needs a visible mark (paving, banners), because there is no wall. Whether towns have guards is open.

## M6, the lawless city (layout accepted 2026-11-10, E16; services Open)

- **Size and walls:** medium-sized, with scrap walls and a gate per road (3 roads: north-west, north-east and south-west).
- **The warning at the gates** (`world-map.md` D11): each gate is where the protection warning shows, so the gates must look clearly different from a normal city's (chains, scorched banners, no guards).
- **The black market** (R-4.4-02): in the middle, around a forge-lit yard.
- **No safe square:** PvP is always on, so the layout has cover and more than one way out of every yard.

## Open

- **Review of the templates** by the designer: the medium city and small town templates are proposals. The three main city plans and M6's layout are accepted (E15, E16).
- **Cover in M6:** how a wall hides one player from another is tried in the fade prototype (Q38 of `reference-boards-questions.md`).
- **M6's services:** an outpost, crafting stations or stalls in the lawless city.
- **The arena queue in medium cities** (R-6.6-01 says "cities").
- **Guards in small towns** (`world-map.md` O5).
- **City plan drawings:** a to-scale drawing of each main city. The plans are accepted now, so these can be drawn.
- **Mail and the auction:** the designer chose places in a main city, a mailbox and an auction house (E14). The spec says the M4 plan decides, so the two still have to be brought together. Whether medium cities and small towns have them is not decided.
- **The inn in medium cities and small towns** (E14 covers the main cities).
- **Which buildings carry a dome** in the bazaar kit.
- **The colours of the kits:** the hex values on the reference boards (`reference-board-c1.md`, `-c2.md`, `-c3.md`) are proposals.
- **The fade's look in a city:** the designer will choose it on a prototype of one city street (Q9 of `reference-boards-questions.md`).

## Spec updates needed (for the orchestrator)

This file does not change the spec. The orchestrator should update the following once it is accepted:
- **2.3 (world):** the three settlement tiers and their services (E1), and walls by tier (E2).
- **4.2 and 5.3 (outposts and stations):** which tier has which NPC kinds (E1).
- **4.5 (player shops):** stall caps by tier (E1).
- **6.3 (law):** the gate as the edge of the safe zone (E2); M6's services (Open).
- **6.6 (arena):** which cities have `arenaQueue`.
- **10.4 (open items):** the open list above.
- **13 (decision log):** one row dated 2026-11-09 for E1-E7.

The decisions E16 and E17 (M6, 2026-11-10) **are in the spec**: R-2.3-14, and a second decision log row dated 2026-11-10.

The decisions of 2026-11-10 (E8-E15) **are in the spec**, with one exception:
- **R-2.3-14:** M2's kit (E8), the kit details (E9), roofs (E10), the bazaar's cloth roof (E11), C3's moat, bridges and keep (E12), the look of the three sea bridges (E13), and the main city plans as accepted (E15).
- **R-2.3-07 and R-11.6-05:** fast travel as a travel stone on the plaza, what the inn is, and stall places left to economy tuning (E14).
- **R-9.14-03:** cloth and banners may use muted warning hues (part of E11).
- **10.4 and 13:** the open list is brought in line, and the decision log has one row dated 2026-11-10.
- **Not in the spec yet:** places in a main city for mail and the auction (part of E14). R-4.5-03 and 10.4 say the M4 plan decides; the plan and this answer still have to be brought together.
