# Reference board: C1, the water city

Status: **art reference, v1 (2026-11-10)**. A board for the first main city of the main map and for the water kit it defines. **Every colour, size and photo choice here is a proposal** for the designer to review. The drawing is `reference-board-c1.svg` (same folder). The biome around the city is on the Green vale board in `reference-boards-w1.md`.

This file is **not the spec** and decides nothing. Points that need a decision are under "Questions for the designer".

## What is decided and what is a proposal

**Decided** (spec):
- **C1 is a water city:** canals with flowing water run through it, and it has a port where ships can dock (R-2.3-09, E4). It has its own building kit, one of three (E3).
- **A main city has full walls and one gate per road**, and the gate is where the safe zone starts (R-2.3-08, R-6.3-07).
- **Cities sit on the 50 cm grid and face N, E, S or W** (R-11.6-06). **Only service buildings have interiors** (R-11.6-05).
- **Streets are 6 m wide, lanes 3 m, bridges 5 m** (R-11.6-03). The world is built at x1.25-1.5 scale around a 1.8 m character (R-11.6-01, R-11.6-02).
- **The art rules of 9.14:** toon style, a muted world, the fade and roofs off, outlines on characters and interactables only.

**Proposals under review** (spec 10.4), which this board follows as written in `city-layouts.md`:
- **The kit's materials:** pale stone quays and bridges, timber upper floors, grey slate roofs, water wheels, mooring posts.
- **The plan of C1:** a port in the north, a main canal south to a central basin with two side canals, the plaza on the basin, the market on the quay between plaza and port, the crafting quarter by the east gate, the raid gate and arena hall by the south-west gate, and a tutorial gate in the west.
- **The sizes** of the measurement sheet in `art-direction.md` (doors, floors, walls, towers) and its 45-degree guideline for what the camera hides.

## Kit palette

The swatches are the colours as they should look on screen in the Green vale's light. They are not texture values. Saturation is the S of HSV.

| Swatch | Hex | Role | Where | Saturation | Value |
|---|---|---|---|---|---|
| Slate roof | `#6E7479` | roof | every roof; the kit's mark from above | 9% | 47% |
| Slate in shade | `#565C61` | roof | the shaded roof side | 11% | 38% |
| Pale quay stone | `#BEB8AA` | ground | quays, bridges, ground floors, the city wall | 11% | 75% |
| Stone in shade | `#9A958A` | wall | shaded stone, wall bases | 10% | 60% |
| Street paving | `#A29D93` | ground | streets, lanes, the plaza | 9% | 64% |
| Dock planks | `#8B7A66` | ground | piers and jetties in the port | 27% | 55% |
| Timber frame | `#7A6652` | wall | upper floors | 33% | 48% |
| Infill panel | `#D2C9B6` | wall | between the timbers (not in city-layouts.md) | 13% | 82% |
| Door and shutter wood | `#5F4E40` | wall | doors, shutters, water wheels | 33% | 37% |
| Canal water | `#5F7F84` | water | canals and the basin (unwalkable) | 28% | 52% |
| Sea | `#56707A` | water | the port (unwalkable) | 30% | 48% |
| Waterline algae | `#6C775B` | accent | a band on quay walls at the water | 24% | 47% |
| Mooring post | `#5A4B40` | prop | posts, bollards, dock piles | 29% | 35% |
| Ironwork | `#3F4347` | prop | rings, lamps, gate fittings | 11% | 28% |
| Cloth (cream) | `#D8CFB8` | accent | awnings, furled sails | 15% | 85% |
| Cloth (dusty red) | `#9A6A62` | accent | awnings, banners | 36% | 60% |
| Cloth (dusty blue) | `#6C8094` | accent | awnings, banners | 27% | 58% |

- The kit is grey: stone, slate and paving are all at 9-11%. The timber and the cloth carry the warmth.
- **Cloth** is where a city can show colour. The three cloth swatches stay under 40%, well below the warning colours (62-79%).
- **The infill panel** is this board's addition. `city-layouts.md` says "timber upper floors" and does not say whether the walls between the timbers are planks or pale panels (question 2).

## Findings

The numbers are colour differences (CIE76 Delta E; higher is easier to tell apart), as on the W1 boards. They compare flat colours and show where to look first; they are not a pass or fail.

### The slate roof merges with the water

`city-layouts.md` makes the roof the mark of each kit, because the camera mostly shows the tops of things. The slate roof against what lies beside it:

| Beside the roof | Slate roof | Slate in shade |
|---|---|---|
| Street paving `#A29D93` | 19 | 28 |
| Pale quay stone `#BEB8AA` | 29 | 38 |
| Dock planks `#8B7A66` | 18 | 22 |
| Canal water `#5F7F84` | 10 | 15 |
| Sea `#56707A` | 8 | 10 |
| Vale short grass `#8A8E60` | 30 | 35 |
| Vale dirt path `#9B8468` | 24 | 29 |

- Against paving, stone, planks and the vale's ground the roof reads well (18-38).
- **Against canal water and the sea it is close (8-15).** Slate grey and grey-teal water are near each other in lightness and in hue.
- Where a quay lies between a house and the canal, the pale stone separates the two (29 on each side). The problem is a building that stands **directly in the water**: a warehouse on the dock edge, a house with a water wheel, a house built over a canal.
- This matters more here than anywhere else, because water is what makes this city different (question 1).

### Edges

| Edge | Difference |
|---|---|
| canal water against pale quay stone | 29 |
| canal water against street paving | 21 |
| sea against dock planks | 25 |
| sea against pale quay stone | 34 |
| street paving against pale quay stone | 10 |
| street paving against vale dirt path | 16 |

- **The water's edge is clear** against quay stone, paving and planks. A player sees where walking ends.
- **Paving and quay stone are close (10).** Both are walkable, so nothing breaks. If a street and a quay should read as different places, they need more than this.
- **A city street against the vale's dirt road (16):** the change of ground at a gate is visible and modest.

### Warnings on the city's ground

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| M1 greybox ground (today) | `#4b554b` | 39 | 43 | 42 | 36 | 17 |
| Pale quay stone | `#BEB8AA` | 18 (47%) | 18 (43%) | 27 (64%) | 19 (52%) | 3 (18%) |
| Street paving | `#A29D93` | 21 (54%) | 23 (53%) | 29 (69%) | 22 (62%) | 5 (30%) |
| Dock planks | `#8B7A66` | 24 (61%) | 28 (64%) | 39 (93%) | 28 (78%) | 10 (61%) |

- On pale stone and paving the four warnings keep 43-69% of the difference they have on today's M1 ground, as on the other light grounds of the W1 boards.
- PvP is off in a normal city (R-6.3-01), so fights here are rare. Guards attack bad-honor players (question 5).

## What the camera hides in a street

The second and third drawings of the board show this to scale. The rule used is the guideline in `art-direction.md`: an object on the camera side hides the character's feet when it is taller than its distance from the character, and the head when it is taller than that distance plus about 1.7 m. The guideline is a proposal, so the numbers below move if it does.

| Object | Height | Hides the feet within | Hides the head within |
|---|---|---|---|
| House, 1 floor | 4 m to the eaves | 4 m | 2.3 m |
| House, 2 floors | 8 m to the eaves | 8 m | 6.3 m |
| City wall | 6-8 m | 6-8 m | 4.3-6.3 m |
| Tower or landmark building | up to 15 m | 15 m | 13.3 m |

The distance is measured from the object's face, along the direction the camera looks.

What follows for the three kinds of street, when the camera looks across them:

| Place | Between the facades | What fades |
|---|---|---|
| Lane | 3 m | The row on the camera side always fades. |
| Street | 6 m | A 2-floor row on the camera side always fades. A 1-floor row stays solid while the player keeps to the far 2 m of the street. |
| Canal street | 12 m (quay 3 m, canal 6 m, quay 3 m) | From the far quay the row across the canal is 9 m or more away, so a 2-floor row stays solid. From the near quay it fades. |
| Square | any | A square wider than twice the eaves height has a middle where nothing fades: more than 16 m for 2-floor houses. |

When the camera looks **along** a street, no building stands between it and the character, and nothing fades.

What this means for the kit:
- **Facades are seen from across the canals and from the squares.** In lanes and streets the player sees roofs, the street and a faded row.
- **The fade will be on screen most of the time in a city.** Its look is Open (10.4: dither or transparency, and who it applies to), and the city is the place to judge it.
- **Roof pitch:** a roof whose eaves face the street, pitched at 45 degrees or less, hides nothing more than its eaves already do. A gable that faces the street hides as much as its peak is tall. The tall gabled fronts in the port references below would hide more than the same house turned sideways.
- **Gate passages:** under a gate arch the arch is between the camera and the character. It has to be a part that can fade or lift off, like a roof (R-9.14-02).
- **Every building needs four finished sides** and a finished roof, because the camera turns all the way round.

## Kit pieces

What the kit needs to build C1 as proposed. Sizes are from the measurement sheet in `art-direction.md`.

| Piece | Notes |
|---|---|
| House, 1 and 2 floors | A closed facade on all four sides. About 4 m per floor, 4-8 m to the eaves. Stone ground floor, timber upper floor, slate roof. |
| Service building | A shell whose roof and upper floor can be hidden (R-9.14-02). Doors at least 2 m wide and 3 m tall, corridors at least 2.5 m. Used for the crafting halls, shops with NPCs, the guild hall, the raid gate and arena hall, and the inn. |
| Roofs | Slate. Chimneys, dormers and ridge shapes are what tells one building from the next from above. |
| Quay | Pale stone edge with a low kerb, mooring posts and rings. Low parts (up to about 1.2 m) never fade. The water beside it is unwalkable. |
| Canal bridge | 5 m walkable width over a 6 m canal; parapets are blockers outside the 5 m. Nothing walkable passes under it. A humped deck stays under 30 degrees (R-11.6-04). |
| City wall, tower, gate | Wall about 6-8 m, towers up to about 15 m. Both fade when they hide the player. Two road gates (south-west and east) and a smaller tutorial gate (west). |
| Port | Piers of dock planks, warehouses (closed facades), moored ships. Ships are scenery; their masts fade. |
| Water wheel | On the side canal of the crafting quarter. It turns with the water, which flows toward the sea. |
| Plaza | The edge of the central basin, and the places for the respawn point, the outpost and the fast travel point. |
| Market | Awnings, counters for NPC vendors, and marked places for player stalls. |
| Street furniture | Signposts at crossings (R-9.14-07), lamps, wells, crates and barrels. |

Open points that change this list are under the questions: what the inn is for, how fast travel looks, whether mail and the auction need a building, and how many stalls a main city allows.

## The districts of the proposed plan

- **Port (north):** the sea is the edge of the map. Docks, warehouses and a harbour square. The widest water and the tallest props (masts) are here, so this is where the roof and water finding matters most.
- **Canals:** the main canal runs north-south and the side canals east-west, which fits the grid. The bridges are the routes across.
- **Plaza (centre):** on the basin. A new or returning player always starts here, so it is the first picture of the city. It needs to be wide enough to have a middle where nothing fades.
- **Market:** along the quay between the plaza and the port. Stalls on a quay face the canal, so they are seen from across the water.
- **Crafting quarter (east gate):** forge, jeweler's bench and job masters on a side canal with water wheels.
- **Raid gate and arena hall (south-west gate):** one large service building.
- **Tutorial gate (west):** small and apart from the main roads. Every way through the level-10 gate needs a visible mark (`open-world-content.md`).

## Photo references

Links to free-licence files on Wikimedia Commons, each looked at before it was listed. No image is stored in the repository. They are references for materials, shapes and layout, not a target for detail: the game is stylized toon.

**The whole look: slate, timber and water**
- [0 Monschau - La Rur et la passerelle de l'hôtel zum Stern](https://commons.wikimedia.org/wiki/File:0_Monschau_-_La_Rur_et_la_passerelle_de_l%27h%C3%B4tel_zum_Stern_%281%29.JPG): slate roofs, timber fronts and a river with a footbridge: the kit's three materials together. Jean-Pol GRANDMONT, CC BY-SA 3.0.
- [Monschau panorama](https://commons.wikimedia.org/wiki/File:Monschau_panorama.jpg): the same town from above: slate roofs as the camera would see them. ZH2010, CC BY 3.0.
- [5 of 10 - La Petite France, Strasbourg - FRANCE](https://commons.wikimedia.org/wiki/File:5_of_10_-_La_Petite_France,_Strasbourg_-_FRANCE.jpg): timber upper floors standing directly in the water. Ignaz Wiradi, CC BY-SA 3.0.
- [Paris vu du Panthéon-2008](https://commons.wikimedia.org/wiki/File:Paris_vu_du_Panth%C3%A9on-2008.jpg): a city of grey roofs from above: streets as gaps between the roofs. Coyau, CC BY-SA 3.0.
- [Bad Vilbel, Brunnen- und Bädermuseum](https://commons.wikimedia.org/wiki/File:Bad_Vilbel,_Brunnen-_und_B%C3%A4dermuseum_%281%29.jpg): a large timber-framed hall on a square: the size of a service building. Karsten Ratzke, CC0.

**Canals, quays and bridges**
- [Canal @ Thiou @ Old Town @ Annecy](https://commons.wikimedia.org/wiki/File:Canal_@_Thiou_@_Old_Town_@_Annecy_%2848744489443%29.jpg): a canal between stone quays with houses on both sides: the canal street. Guilhem Vellut, CC BY 2.0.
- [Two white boats moored in a canal in Bruges, by Linh Nguyen](https://commons.wikimedia.org/wiki/File:Two_white_boats_moored_in_a_canal_in_Bruges,_by_Linh_Nguyen.jpg): houses rising from the water with boats moored at their feet. Linh Nguyen, CC0.
- [Rimini - Ponte di Tiberio e centro storico - 2024-09-28 20-34-19 001](https://commons.wikimedia.org/wiki/File:Rimini_-_Ponte_di_Tiberio_e_centro_storico_-_2024-09-28_20-34-19_001.jpg): a pale stone arch bridge over calm water. Terragio67, CC BY-SA 4.0.
- [Water mill wheel at Allerford](https://commons.wikimedia.org/wiki/File:Water_mill_wheel_at_Allerford_-_geograph.org.uk_-_3736159.jpg): a water wheel on a stone mill. M J Richardson, CC BY-SA 2.0.

**Port**
- [Honfleur Vieux Bassin 8](https://commons.wikimedia.org/wiki/File:Honfleur_Vieux_Bassin_8.jpg): tall narrow houses around a harbour basin, gables and slate to the water: the plaza on the basin. Zairon, CC BY-SA 4.0.
- [Tall ships in Charlestown harbour from Quay Road](https://commons.wikimedia.org/wiki/File:Tall_ships_in_Charlestown_harbour_from_Quay_Road_-_geograph.org.uk_-_7856541.jpg): tall ships at a stone quay: the moored ships and their masts. Nick Smith, CC BY-SA 2.0.
- [Bryggen, Old Wharf of Bergen - 2013.08 - panoramio](https://commons.wikimedia.org/wiki/File:Bryggen,_Old_Wharf_of_Bergen_-_2013.08_-_panoramio.jpg): a row of gabled timber warehouses on a wharf. rheins, CC BY 3.0.
- [Boats and crane In Gdansk](https://commons.wikimedia.org/wiki/File:Boats_and_crane_In_Gdansk.jpg): a harbour crane, a ship and gabled fronts on the water. John Samuel, CC BY-SA 4.0.

**Walls, gates and streets**
- [Sirmione - Castello Scaligero](https://commons.wikimedia.org/wiki/File:Sirmione_-_Castello_Scaligero_%284890039027%29.jpg): walls and towers rising from water. Elliott Brown, CC BY 2.0.
- [Wycker vestingwerken met Duitse of Akerpoort en ravelijn De Raaf, detail kopie Maquette van Maastricht, collectie Centre Céramique](https://commons.wikimedia.org/wiki/File:Wycker_vestingwerken_met_Duitse_of_Akerpoort_en_ravelijn_De_Raaf,_detail_kopie_Maquette_van_Maastricht,_collectie_Centre_C%C3%A9ramique.JPG): a scale model of a town wall, gate and bridge seen from above, close to the game's camera. Kleon3, CC BY-SA 4.0.
- [Tykocin, Dom, XVIII XIX, KsP](https://commons.wikimedia.org/wiki/File:Tykocin,_Dom,_XVIII_XIX,_KsP.jpg): a cobbled street: paving as ground. Krzysztof Popławski, CC BY 4.0.
- [Stratford upon Avon Rother Market Square fruit vegetable stall Warwickshire England](https://commons.wikimedia.org/wiki/File:Stratford_upon_Avon_Rother_Market_Square_fruit_vegetable_stall_Warwickshire_England.jpg): market stalls under awnings on a square. Acabashi, CC BY-SA 4.0.

## Questions for the designer

Nothing below is decided here.

1. **Slate roofs and water:** the kit's roof colour is close to the water's. Should the slate become lighter or warmer, should the water change, or do buildings always keep a quay or a pale edge between roof and water?
2. **Upper floors:** timber only, or a timber frame with pale panels between the timbers?
3. **Roof direction:** eaves to the street (hides less) or gables to the street (hides more, and is the look of many harbour towns)?
4. **The fade in cities** (Open in 10.4): in lanes and 6 m streets the camera-side row is faded most of the time. Is that acceptable, and does it change which look the fade should have?
5. **Fights in a city:** do guard fights need warnings that read on pale stone and paving?
6. **Open points of the spec that change the kit list:** what the inn is for (R-11.6-05), how fast travel looks, whether mail and the auction need a building, and the stall cap of a main city (R-4.5-03).
7. **The review of the C1 plan** (10.4) is still open, and this board follows the plan as written. The style references and the question of storing pictures are questions 5 and 6 of `reference-boards-w1.md`.

## Not covered

- Interiors: floors, walls seen from above with the roof off, furniture.
- NPCs, guards and ships as models.
- The bazaar kit (C2), the medieval kit (C3) and M6's scrap look.
- The biome dressing of the water kit for M1, M3, T1 and T2.
- A to-scale plan of the whole city, which `city-layouts.md` leaves until the plan is accepted.
