# City layouts (main map)

Status: **design sketch, v1 (2026-11-09)**, from a design talk with the game designer. The decisions (E1-E7) are the designer's. The **plans per city are proposals to review**: the designer defines each main city, and nothing in a plan is settled until they accept it.

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

## Services by tier (E1)

| Service | Main city (C1-C3) | Medium city (M1-M5) | Small town (T1-T11) |
|---|---|---|---|
| Respawn point (D7) | yes | yes | yes |
| Adventurer Guild outpost (sell, rank up, quests) | yes | yes | yes |
| NPC vendors | several | several | one |
| Forge and jeweler's bench | yes | yes | no |
| Player shop stalls | yes (the largest cap) | a few | no |
| Job master | yes | no | no |
| Fast travel (R-2.3-01) | yes | no | no |
| Raid gate (D13) | yes | no | no |
| Arena queue (R-6.6-01) | yes | Open | no |
| Guards (R-6.3-01) | yes | yes | Open (`world-map.md` O5) |
| Walls (E2) | full wall, a gate per main road | palisade, a gate per road | none |

**The lawless city M6** is a medium city by size, but its services are Open: the black market is there (R-4.4-02), there are no guards, and whether it has an outpost, crafting stations or stalls isn't decided.

## Kits and which settlement uses which (E3, E7)

The materials are proposals.

| Kit | Main city | Look (proposal) | Also used by (proposal) |
|---|---|---|---|
| Water kit | C1 | pale stone quays and bridges, timber upper floors, grey slate roofs, water wheels, mooring posts | M1 (moor), M2 (old forest), M3 (rime coast), T1 (amber woods), T2 (lakes), T3 (fungal hollow) |
| Bazaar kit | C2 | clay walls, domes and flat roofs, cloth awnings, carved wooden screens, caravan yards | M4 and T6 (dune isle), T4 (steppe), T5 (canyons) |
| Medieval kit | C3 | grey stone, timber frames, tiled roofs, battlements, a keep | M5 (taiga), T7 (frost peaks), T8 (storm cliffs), T9 (ash woods), T10 (palm coast), T11 (jungle) |
| Scrap look | M6 | salvaged wood and metal, chains, scorched cloth, built on black rock | M6 only |

- **Biome dressing on top:** each borrowed kit gets its biome's materials and props from `biome-sheets.md`. For example, M3 uses the water kit with snow on the roofs and ice on the quays, and T11 uses the medieval kit overgrown with vines.
- **M2 is a judgement call:** by road it is slightly nearer to C2 (901 m) than to C1 (1,055 m), but a forest town fits the water kit's timber better than desert clay.
- **Roofs matter most.** The camera mostly shows the tops of things, so each kit needs a roof shape and color that identifies it at a glance: slate grey for the water kit, pale domes for the bazaar kit, warm tile for the medieval kit. All stay muted (A3).

## Main city plans (proposals to review)

Each plan follows the roads that reach the city on `world-map.svg`. A main city is about 170 m across, about 4 to 8 screens across, and a player crosses it in about 40 s.

**Shared rules for all three:**
- **A central plaza** holds the respawn point, the Adventurer Guild outpost and the fast travel point, so a new or returning player always starts at the same place.
- **A street 6 m wide** leads from the plaza to each gate.
- **Districts along the streets:** a market (vendors and player stalls), a crafting quarter (forge, jeweler's bench, job masters), and a hall for the raid gate and the arena queue.
- **The tutorial gate** is a small separate gate to the city's tutorial area (`world-map.md` D10), away from the main roads.
- **Service buildings** have interiors (B6), and everything else is a closed facade.

### C1, the water city (Green vale)

- **Roads and gates:** a south-west gate (road to M2) and an east gate (road to M1). The north side is the sea.
- **Port (north):** docks with moored ships, warehouses and a harbour square. The ships are scenery: fast travel isn't by ship, because C2 is inland (see Open).
- **Canals:** a main canal runs from the port south to a basin in the middle of the city, with two side canals. The water flows toward the sea.
  - Canals are unwalkable water, about 6 m wide, with low quay edges.
  - Bridges over them are 5 m wide, and nothing walkable passes under them, so they fit the one-layer grid.
- **Plaza:** on the central basin.
- **Market:** along the quay between the plaza and the port.
- **Crafting quarter:** by the east gate, on a side canal with water wheels.
- **Raid gate and arena hall:** by the south-west gate.
- **Tutorial gate:** west, toward the tutorial area south-west of the city.
- **Chokepoints inside:** the canal bridges. There is no PvP inside (R-6.3-01), so they only shape the routes.

### C2, the caravan bazaar city (Red sands)

- **Roads and gates:** a north gate (road to M2), a north-east gate (road to T4) and a south gate (road to T5).
- **The bazaar street:** one long covered street from the north gate to the south gate, lined with vendors and player stalls. Its cloth roof hides when the player walks under it (A2).
- **Plaza:** in the middle, where the street from the north-east gate meets the bazaar, around a well.
- **Caravan yards:** an open yard inside each gate, with pack animals and stacked goods.
- **Crafting quarter:** east of the plaza, toward the north-east gate.
- **Raid gate and arena hall:** west of the plaza.
- **Tutorial gate:** west, toward the tutorial area.
- **Shade:** streets run under awnings and between high clay walls, which fade when they hide the player.

### C3, the classic medieval city (Reed marsh)

- **Roads and gates:** four gates, north (road to T9), west (road to T10), east (road to M6) and south (road to T11).
- **Plan:** two main streets cross at the central plaza and split the city into four quarters.
- **Moat:** the marsh's river feeds a moat around the walls, with a 5 m bridge at each gate.
- **Quarters:**
  - north-west: the market;
  - north-east: the crafting quarter;
  - south-east: the raid gate and arena hall, with the tutorial gate beyond it;
  - south-west: the keep (scenery) and houses.
- **Walls:** stone, with towers at the gates. They fade when they hide the player.

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

## M6, the lawless city (proposal)

- **Size and walls:** medium-sized, with scrap walls and a gate per road (3 roads: north-west, north-east and south-west).
- **The warning at the gates** (`world-map.md` D11): each gate is where the protection warning shows, so the gates must look clearly different from a normal city's (chains, scorched banners, no guards).
- **The black market** (R-4.4-02): in the middle, around a forge-lit yard.
- **No safe square:** PvP is always on, so the layout has cover and more than one way out of every yard.

## Open

- **Review of the plans** by the designer: each city plan and template is a proposal.
- **M6's services:** an outpost, crafting stations or stalls in the lawless city.
- **The arena queue in medium cities** (R-6.6-01 says "cities").
- **Guards in small towns** (`world-map.md` O5).
- **How fast travel looks** in each main city (a gate, a stone, a guild service).
- **The kit for M2:** water kit or bazaar kit.
- **City plan drawings:** a to-scale drawing of each main city, once the plans are accepted.
- **Mail and the auction:** whether they need a place in the city (a mailbox, an auction house) or work from windows anywhere. The M4 plan decides.

## Spec updates needed (for the orchestrator)

This file does not change the spec. The orchestrator should update the following once it is accepted:
- **2.3 (world):** the three settlement tiers and their services (E1), and walls by tier (E2).
- **4.2 and 5.3 (outposts and stations):** which tier has which NPC kinds (E1).
- **4.5 (player shops):** stall caps by tier (E1).
- **6.3 (law):** the gate as the edge of the safe zone (E2); M6's services (Open).
- **6.6 (arena):** which cities have `arenaQueue`.
- **10.4 (open items):** the open list above.
- **13 (decision log):** one row dated 2026-11-09 for E1-E7.
