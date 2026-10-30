# World map sketch (main map)

Status: **design sketch, v5 (2026-10-30)**, from a design talk with the game designer over their own drawing. The drawing is in `world-map.svg` (same folder), to scale.

This file is **not the spec**. It records what the designer decided for the main map and what is still open. Rules quoted below live in `docs/spec/`. Where the sketch needs the spec to change, the change is listed under "Spec updates needed", and nothing here overrides the spec until those updates land.

All names (cities, towns, biomes) are **placeholders**.

## Frame and scale

- **Scale:** 1 drawing unit = 2.5 m. The frame is 3.6 x 2.5 km, north is up, and land is about 4.25 km² (two continents and an island).
- **Speed:** on foot 4 m/s (`StartingValuesM1.moveSpeedCmPerSec` 400), so 1 km is 4.2 min. Mounted is +60 %, 1 km in 2.6 min.
- **The scale target (designer's choice):** "medium", about 10 minutes on foot between main cities by road. Measured: C1-C2 8.2 min, C2-C3 9.2 min, C1-C3 14.5 min, average 10.6 min. R-2.3-01 says the map size is not used for sizing yet, so this is a design target only.
- **Not included:** the existing world `data/world/m1` (the city, the M1 combat area and `field1`). It stays the **test/dev zone** and is not part of the main map (designer decision).
- **Size comparison:** field 1 of the dev world (400 x 400 m) is drawn in the frame's top right.

## Decisions (designer, 2026-10-30)

| # | Decision | Rules it touches |
|---|---|---|
| D1 | One continuous main map (R-2.3-01): a west continent, an east continent and a small island between them, joined by three bridges. | R-2.3-01 |
| D2 | **Three main cities, all start cities:** C1 (north-west), C2 (south-west), C3 (south-east). Fast travel only between them (R-2.3-01). | R-2.3-01, R-5.1-02 |
| D3 | Six medium cities (M1-M6) and eleven small towns (T1-T11), joined by the main roads of the drawing. | R-2.3-01 |
| D4 | **M6 is the lawless city.** | R-6.3-01, R-4.4-02 |
| D5 | **Zones have no level bands.** Monsters are placed by **rank**, not by level (see "Spec updates needed": this conflicts with how spec 3.6 and 12.8 work today). | R-3.6-02, R-3.6-03 |
| D6 | **20 small biomes** (0.08-0.33 km² each). The designer set five: a medium desert, a small desert, two ice biomes and one fire biome. The other fifteen are the sketch's proposals, which the designer accepted. | none (new) |
| D7 | **A player respawns at the nearest city or town.** | spec 3.1 player respawn |
| D8 | **Bridges are short chokepoints, 130-150 m** (the straits were narrowed; first drawn at 140-475 m). | R-2.3-05 (region flags) |
| D9 | **No territories on the map for now.** The world will be split into areas for guild war in the milestone that builds territories and sieges (M8), not in this sketch. | R-6.4-02, R-6.4-04, 10.4 [M8] territory list (stays Open) |
| D10 | **One tutorial area per start city**, next to it and in its biome. The level-10 gate keeps new players in the start city and its tutorial area. | R-6.5-01, R-12.3-01, R-2.3-06 |
| D11 | **Lawless M6:** entering shows a warning, and inside it the new-player protection is off (M6 stays "open to everyone" as R-6.3-01 says). | R-6.3-01, R-6.5-01 |
| D12 | **No dungeons on the main map for now.** No portal spots are reserved. | R-2.3-02, R-6.5-01 |
| D13 | **Raid gates in the three main cities only** (`raidStart`): the party gathers there and the raid instance opens from the gate. | R-6.7-01, R-6.7-02, R-2.3-02 |
| D14 | **The jail releases players at the nearest main city.** How that works with the city guards is still open (O3). | R-6.3-04 |
| D15 | **Build order: grow from one start** (see "Build order"). | spec 12 |

## Settlements

| Id | Kind | Biome | Area type (`RegionDefaults`) | Role |
|---|---|---|---|---|
| C1 | main city, start | Green vale | normalCity | start, fast travel, raid gate, arena queue (R-6.6-01), tutorial area next to it |
| C2 | main city, start | Red sands | normalCity | as C1 |
| C3 | main city, start | Reed marsh | normalCity | as C1 |
| M1 | medium city | Heather moor | normalCity | |
| M2 | medium city | Old forest | normalCity | |
| M3 | medium city | Rime coast | normalCity | west end of the north bridge |
| M4 | medium city | Dune isle | normalCity | between the two south bridges |
| M5 | medium city | Snow taiga | normalCity | east end of the north bridge |
| M6 | medium city, **lawless** | Ember caldera | lawlessCity | PvP always on, black market (R-4.4-02) |
| T1-T11 | small towns | see the regions table | normalCity (proposal) | respawn points (D7); whether a small town has guards, outposts or services is open (O5) |

Approximate sizes as drawn: a main city about 170 m across, a medium city about 90 m, a small town about 45 m.

## Regions

The **ids are proposals** (`main.<biome>`), and the area type of each biome is `openWorld`. The **monster rank** of a region is open (O1): no level band is given (D5). A **gate** is a road link to a neighbour across a bridge. The **gathering focus** is a proposal for R-5.2-01 (ore and gems for the Blacksmith/Jeweler, herbs for the Botanist, fishing for the Adventurer). Wood is Open (10.4 [M3]).

| Id | Name | Biome | Milestone (build step) | Size | Neighbours and gates | Hosts |
|---|---|---|---|---|---|---|
| `main.vale` | Green vale | meadows | W1 (M3) | 0.21 km² | heather, amber | **C1**, C1 tutorial area; herbs, stream fishing |
| `main.heather` | Heather moor | windy moorland | W1 (M3) | 0.28 km² | vale, amber, lakes, rime | M1; herbs |
| `main.amber` | Amber woods | autumn forest | W1 (M3) | 0.26 km² | vale, heather, lakes, oldforest | T1; herbs, wood (Open) |
| `main.lakes` | Lake country | lakes (one lake as a landmark, unwalkable water) | W1 (M3) | 0.23 km² | heather, amber, rime, fungal | T2; fishing, herbs |
| `main.rime` | Rime coast | **ice 1** | W1 (M3) | 0.27 km² | heather, lakes, fungal; **north bridge** to taiga | M3; ore, sea fishing |
| `main.oldforest` | Old forest | deep forest | W2 | 0.31 km² | amber, fungal, steppe | M2; herbs, wood (Open) |
| `main.fungal` | Fungal hollow | giant mushroom grove | W2 | 0.23 km² | lakes, rime, oldforest, steppe | T3; herbs |
| `main.steppe` | Dry steppe | dry grassland | W2 | 0.24 km² | oldforest, fungal, redsands | T4; herbs |
| `main.redsands` | Red sands | **desert, medium** | W2 | 0.24 km² | steppe, canyons | **C2**, C2 tutorial area; ore, gems |
| `main.canyons` | Bone canyons | red-rock canyons | W2 | 0.15 km² | redsands; **south-west bridge** to duneisle | T5; ore, gems |
| `main.duneisle` | Dune isle | **desert, small** (the island) | W3 | 0.14 km² | **south-west bridge** to canyons, **south-east bridge** to palms | M4, T6; gems, sea fishing |
| `main.palms` | Palm coast | tropical shore | W3 | 0.21 km² | taiga, ashwoods, marsh, jungle; **south-east bridge** to duneisle | T10; sea fishing, herbs |
| `main.jungle` | Fern jungle | jungle | W3 | 0.13 km² | palms, marsh | T11; herbs, wood (Open) |
| `main.marsh` | Reed marsh | marsh, rivers | W3 | 0.26 km² | palms, jungle, ashwoods, springs | **C3**, C3 tutorial area; herbs, river fishing |
| `main.ashwoods` | Ash woods | burnt forest | W3 | 0.18 km² | taiga, palms, marsh, ember, springs | T9; ore |
| `main.springs` | Steam springs | hot pools | W3 | 0.10 km² | ember, ashwoods, marsh | (no settlement); herbs, gems |
| `main.taiga` | Snow taiga | snowy pine forest | W3 | 0.27 km² | frostpeaks, cliffs, ember, ashwoods, palms; **north bridge** to rime | M5; wood (Open), ore |
| `main.frostpeaks` | Frost peaks | **ice 2** | W3 | 0.33 km² | taiga, cliffs | T7; high-tier ore |
| `main.cliffs` | Storm cliffs | basalt sea cliffs | W3 | 0.08 km² | frostpeaks, taiga, ember | T8; ore, sea fishing |
| `main.ember` | Ember caldera | **fire** (a volcano as a landmark) | W4 (M5) | 0.16 km² | taiga, cliffs, ashwoods, springs | **M6 lawless**; rare ore and gems (worth the PvP risk) |

**Biome layout:**
- **Climate:** cold in the north (rime coast, frost peaks, snow taiga, heather moor), hot in the south (deserts, canyons, steppe, palm coast, jungle).
- **The fire area:** the caldera has the ash woods and steam springs as buffers between it and the C3 start marsh.
- **Starts:** each start city has a different, readable start biome: meadows (C1), desert (C2), marsh (C3).
- **R-2.3-06 gates (D10):** the start cities and their tutorial areas have `minLevel` 0; every other region needs `minLevel` 10. The gate is one way, so nobody gets stuck.
- **The exporter limit:** 20 biomes plus the city, town and tutorial regions stays well under the 255-region limit of `grid.bin` v2 (region index u8). But the whole map is one world, so it needs streaming (R-2.3-04), see O8.

## Travel and chokepoints

Measured along the drawn roads (shortest road path).

| From - to | Road | On foot | Mounted | Route |
|---|---|---|---|---|
| C1 - C2 | 1,956 m | 8.2 min | 5.1 min | C1 - M2 - C2 |
| C2 - C3 | 2,206 m | 9.2 min | 5.7 min | C2 - T5 - SW bridge - T6 - M4 - SE bridge - T10 - C3 |
| C1 - C3 | 3,473 m | 14.5 min | 9.0 min | C1 - M1 - M3 - north bridge - M5 - T9 - C3 |
| C1 - M6 | 3,064 m | 12.8 min | 8.0 min | through the north bridge and M5 |
| C2 - M6 | 2,958 m | 12.3 min | 7.7 min | through the island and C3 |
| C3 - M6 | 753 m | 3.1 min | 2.0 min | direct |
| C1 - M1 / M2 / M3 | 676 / 1,055 / 1,628 m | 2.8 / 4.4 / 6.8 min | | |
| C2 - M2 / M4 | 901 / 1,163 m | 3.8 / 4.8 min | | |
| C3 - M4 / M5 | 1,042 / 1,100 m | 4.3 / 4.6 min | | |
| M3 - M5 (north bridge) | 745 m | 3.1 min | 1.9 min | |

Straight-line distances between the main cities are 1.5 km (C1-C2), 1.8 km (C2-C3) and 2.3 km (C1-C3).

**Chokepoints:**
- **The bridges** (D8): north M3-M5 about 133 m, south-west T5-T6 about 138 m, south-east M4-T10 about 146 m. They are the only links between the continents. West to east goes either over the north bridge or over the island (two bridges). Each crossing takes 33-37 s on foot. A crossing is a natural spot for ambushes. When the guild war areas are drawn (D9), the bridges are natural borders.
- **M4 on the island** sits between two bridges, so it controls the southern route.
- **The lawless M6 is off the main routes.** C3 - M5 runs through T9 and never through M6, and nobody has to cross the lawless city to travel.
- **Fast travel** links only C1, C2 and C3 (R-2.3-01). The Merchant moves goods on foot over these roads and bridges (R-5.1-02).

**Bridges and the heightmap:** the bridges cross unwalkable sea and nothing passes under them, so they fit the one-layer heightmap. Overlapping surfaces are still Open (10.4 [M1]).

## Build order (D15, "grow from one start")

| Step | Content | Milestone |
|---|---|---|
| W1 | C1, its tutorial area and its ring: Green vale, Heather moor, Amber woods, Lake country, Rime coast (M1, M3, T1, T2). Covers herbs, fishing and ore for the jobs. | M3 (jobs and crafting) |
| W2 | C2, its tutorial area and the rest of the west continent: Old forest, Fungal hollow, Dry steppe, Red sands, Bone canyons (M2, T3, T4, T5) | to assign |
| W3 | C3, its tutorial area, the island and the east continent except the caldera: the three bridges, M4, M5, T6-T11, and fast travel between the three cities | to assign |
| W4 | Ember caldera and M6, the lawless city (warning, black market) | M5 (law) |
| W5 | Split the world into guild war areas (territories) and place the siege entrances | M8 (territories and sieges) |

Each step stays small enough to test with 10-15 players. The orchestrator assigns W2 and W3 to milestones in spec 12.

## Open questions

| # | Question | Notes |
|---|---|---|
| O1 | **How monster rank maps to regions** (D5) | Which ranks live where; how the exp band rule (R-3.6-03) works when zones have no level. |
| O2 | **Respawn details** (D7) | Does a small town count, and does a bad-honor player respawn in a normal city where guards attack them (R-6.3-01)? Death in M6? Dead bad-honor players during the arrest window (R-6.3-03)? |
| O3 | **Jail release and the guards** (D14) | A released player still has bad honor (honor reset to the black-market threshold, R-6.3-04), and guards in normal cities attack bad-honor players (R-6.3-01). Options: a short grace period at the city gate (new config value), release just outside the walls, release at M6, or decide in M5. |
| O4 | **Guild war areas** (D9) | How the world splits into territories (R-6.4-02 "the map is divided into territories"), which cities stay neutral (the start cities, the lawless M6?), and where each siege instance (R-6.4-04) is entered. Decided in M8 (10.4 [M8] territory list). |
| O5 | **Small town services** | Guards, outposts (spec 4.2), a respawn point, a quest board? |
| O6 | **Dungeons** (D12) | When and where they return; there are no portal spots now. |
| O7 | **The protection warning in M6** (D11) | The text (a `UiStrings` row) and whether the warning repeats. |
| O8 | **Streaming and world split** | The map is one continuous world, which needs terrain streaming (R-2.3-04). Whether the server runs it as one world is open. |
| O9 | **Final names** | All names are placeholders. |
| O10 | **Wood** | Which biome holds wood depends on the wood-source decision (10.4 [M3]). |

## Spec updates needed (for the orchestrator)

This PR does not change the spec. These spec sections and decision-log rows need updating when the sketch is accepted:

- **2.3 (world)**
  - R-2.3-01: the layout as decided (D1-D3).
  - Regions and area types for the main map, the bridges as chokepoints, and the m1 world named as the dev/test zone.
  - R-2.3-02: no dungeon portals for now (D12); raids from city gates (D13).
- **3.1 (player respawn)**
  - The nearest city or town (D7) replaces "the city's player spawn marker" once the main map exists.
- **3.6 (progression) and 12.8 (field zone)**
  - Monsters placed by rank, zones without level bands (D5): `MonsterDef.level`, the recommended level range per zone and the band rule (R-3.6-03) need an answer (O1). This is the biggest conflict with the spec as written.
- **6.3 (law)**
  - D11 (the protection drops in M6, R-6.3-01 vs R-6.5-01).
  - D14 and O3 (the jail release point, R-6.3-04).
- **6.4 (guilds)**
  - No change now. The guild war areas are drawn in M8 (D9), and the 10.4 [M8] territory list stays Open.
- **6.5 (onboarding)**
  - One tutorial area per start city (D10).
- **6.7 (raids)**
  - Raid gates in the three main cities (D13).
- **12.2, 12.3 and 12.7 (delivery plan)**
  - The build steps W1-W5 (D15).
  - R-12.3-01 today defines the tutorial as the dev world's city, M1 combat area and ring 1. For the main map it becomes each start city and its tutorial area.
- **5.2 (gathering)**
  - The gathering focus per biome (proposal) once the jobs land in M3.
- **10.4 (open items)**
  - Add O1-O10; O4 goes under the existing [M8] territory list.
- **13 (decision log)**
  - One row dated 2026-10-30 for D1-D15.
