# Open-world content: what fills the space between towns

Status: **design sketch, v1 (2026-11-09)**, from a design talk with the game designer. The decisions (F1-F5) are the designer's. The numbers and placement rules under "Guidelines" are **proposals to review**.

This file is **not the spec**. The map is in `world-map.md`; the style and size rules in `art-direction.md` and the biomes in `biome-sheets.md` (PR #179); the settlements in `city-layouts.md` (PR #180). All names are placeholders.

## What the spec and the data already fix

- **Monster camps:** monsters spawn at `monsterSpawn` markers grouped into camps (`SpawnController`). They respawn after 30 s, bosses after 10 min (`StartingValuesM1`, placeholders).
- **Aggro and leash** (`MonsterDef`, placeholders): a monster notices a player at 8-14 m and gives up the chase at 15-18 m from its spawn point. Bosses leash at 25-30 m.
- **Resource nodes** (R-5.2-01, R-5.2-03): a node depletes after one harvest and respawns on a delay scaled by demand. Only players with the job can harvest.
- **The dev field zone, for comparison:** 0.16 km² with 28 camps (85 monsters), 14 resource nodes, one field boss and two outposts.
- **Safe zones today exist only in cities:** PvP off and guards (R-6.3-01). In the open world PvP is allowed (R-2.3-05 region flags).
- **Respawn** at the nearest city or town (`world-map.md` D7).
- **One screen** shows about 20-42 m across (`art-direction.md`), and walking covers 4 m/s.
- **Large monsters and bosses** stand only on open ground (`art-direction.md` B5).

## Decisions (designer, 2026-11-09)

| # | Decision | Notes |
|---|---|---|
| F1 | **Roads are safe from monsters near the start cities only.** Safe roads: the three start biomes and the biomes next to them (11 biomes). In the other 9, camps may sit on or next to the road. | The list is below. "Safe" means safe from monsters; open-world PvP still applies on every road. |
| F2 | **Camps are destinations:** a camp about every 120-150 m off-road, roughly 8-12 per biome, about 200 camps and 600 monsters on the whole map. | Quiet ground lies between camps. The count can grow with the population. |
| F3 | **Every biome has a field boss arena, guarded resource spots and lore spots.** | 20 field bosses in total, built over time. |
| F4 | **Rest spots are small safe places far from cities, safe from monsters and from PvP.** They have no services and no respawn point. | This needs new rules, because safe zones today exist only in cities (see Open). |
| F5 | **Resource nodes sit mostly in off-road clusters:** a few single low-tier nodes near roads and towns, and the better tiers in clusters off-road, some of them guarded. | R-5.2-01, R-5.2-03 |

### Safe-road and dangerous-road biomes (F1)

| | Biomes |
|---|---|
| **Safe roads (11)** | Green vale, Heather moor, Amber woods (around C1); Red sands, Dry steppe, Bone canyons (around C2); Reed marsh, Palm coast, Fern jungle, Ash woods, Steam springs (around C3) |
| **Dangerous roads (9)** | Rime coast, Lake country, Old forest, Fungal hollow, Dune isle, Snow taiga, Frost peaks, Storm cliffs, Ember caldera |

What follows from the map:
- **Every trip between two main cities crosses one dangerous stretch:** C1-C2 through the Old forest (M2), C2-C3 over Dune isle (M4), and C1-C3 over the Rime coast and Snow taiga (M3, the north bridge, M5).
- **Every bridge has a dangerous-road biome on at least one end.**
- **The Merchant** (R-5.1-02) therefore has a reason to travel with an escort.

## Guidelines (proposals to review)

### Road rhythm

- **Something to see every 30-60 s of walking** (every 120-240 m): a signpost at a crossing, a bridge, a rest spot, a lore spot, or a camp visible from the road.
- **Signposts** stand at every road crossing (`art-direction.md` A8).

### Camps (F1, F2)

- **A camp is a visible place:** tents, a nest, a ruin or a burrow that fits the biome's signature family (`biome-sheets.md`). Monsters aren't scattered at random.
- **Size:** about 3 monsters per camp on average, on ground about 15-20 m across (less than one screen).
- **Spacing:** 120-150 m between camps off-road, so two camps never pull together (the longest leash is 18 m for normal monsters).
- **In safe-road biomes:** every spawn point is at least **25 m from the road's edge**, so no aggro range (up to 14 m) reaches the road with room to spare.
- **In dangerous-road biomes:** some camps sit on or beside the road. A camp on the road should leave a way around it off-road, so a low-rank player isn't fully blocked.
- **Around towns:** no spawn point within 25 m of a settlement's edge or gate.
- **Camp count by biome size** (about one per 17,000 m² of open ground):

| Biome size | Biomes | Camps each |
|---|---|---|
| small (up to 0.15 km²) | Storm cliffs, Steam springs, Fern jungle, Dune isle, Bone canyons | 4-8 |
| medium (0.16-0.25 km²) | Ember caldera, Ash woods, Green vale, Palm coast, Lake country, Fungal hollow, Dry steppe, Red sands | 8-12 |
| large (over 0.25 km²) | Amber woods, Reed marsh, Rime coast, Snow taiga, Heather moor, Old forest, Frost peaks | 12-16 |

### Places in every biome (F3)

| Place | Count per biome | Rules |
|---|---|---|
| **Field boss arena** | 1 | Open ground with no gap under about 4 m (B5), away from roads and towns by more than the boss's leash (30 m). The arena is a visible place: a ring of stones, a crater, a nest. |
| **Guarded resource spot** | 1-2 | A rich cluster of ore, herbs or fishing with a camp on it. The gathering kind follows the biome's gathering focus (`world-map.md`). |
| **Lore spot** | 2-3 | A ruin, statue, shrine or old battlefield with no gameplay. One of them is the biome's landmark (`biome-sheets.md`). |
| **Rest spot** | about 1 in each dangerous-road biome | See below. |

### Rest spots (F4)

- **Where:** in the 9 dangerous-road biomes, on a road, where the walk between two settlements is long (about 2 minutes or more). About 8-10 on the whole map; the exact spots are chosen when the camps are placed.
- **Size:** about 15 m across, less than one screen.
- **Look:** a campfire, a well or a wayside shelter in the biome's style, with a clear mark of the safe edge (paving, a ring of stones, banners), because there is no wall.
- **Safe from monsters:** no spawn point within 25 m, as for towns.
- **Safe from PvP:** needs the new rules listed under Open.
- **Nothing else:** no vendor, outpost or respawn point.

### Resource nodes (F5)

- **Near roads and towns:** single nodes of the lowest tier, enough for a new gatherer to learn.
- **Off-road clusters:** 3-5 nodes together, with the better tiers further from roads.
- **Guarded clusters:** the best nodes of each biome are in the guarded resource spots.
- **Fishing:** at marked spots on shores, rivers and lakes, since water itself is unwalkable.

### Edges of the world

- **The sea is the map's edge everywhere.** The map is two continents and an island, so there are no invisible walls: every edge is a beach, a cliff or a reef.
- **The level-10 gate** around each start city and its tutorial area (`world-map.md` D10) needs a visible mark at every way out, as the dev zone's gate has.

## Open

- **Rest spot rules** (F4). A no-PvP place without guards is new, and these need an answer before it is built:
  - **Region flags:** which area type a rest spot is (R-2.3-05 has none for it today).
  - **Entering while in combat:** whether a player who is fighting another player can step in and become safe.
  - **Player killers:** whether bad-honor players may use it, and whether the arrest of a dead bad-honor player (R-6.3-02) works inside it.
  - **Monsters:** whether a monster chasing a player stops at the edge.
- **Review of the guideline numbers:** the 25 m road distance, the camp counts and the place counts.
- **A world check for safe roads:** whether `DataValidator --world` should fail when a spawn point is closer than 25 m to a road in a safe-road biome. This needs roads to be marked in the world data.
- **Field boss rules:** respawn time (10 min today, a placeholder), and whether a boss kill is shared by everyone who fought.
- **Monster ranks per biome** (`world-map.md` O1). The safe-road split suggests lower ranks in the 11 safe-road biomes and higher ranks in the 9 others.
- **Population scaling** (F2): whether camp counts change with the number of players online, and how.

## Spec updates needed (for the orchestrator)

This file does not change the spec. The orchestrator should update the following once it is accepted:
- **2.3 (world):**
  - safe-road and dangerous-road biomes (F1);
  - a new area type for rest spots in the region flags table (F4).
- **3.1 (monsters) and 12 (delivery plan):** camps as destinations, the spacing and the distance from roads and towns (F1, F2), as content rules for the main map.
- **5.2 (gathering):** node placement (F5) and guarded resource spots (F3).
- **6.3 (law):** rest spots as no-PvP places and their rules (F4).
- **11.6 (world data):** roads marked in the world data, if the safe-road check is wanted.
- **10.4 (open items):** the open list above.
- **13 (decision log):** one row dated 2026-11-09 for F1-F5.
