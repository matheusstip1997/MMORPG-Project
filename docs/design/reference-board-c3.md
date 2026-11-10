# Reference board: C3, the classic medieval city

Status: **art reference, v1 (2026-11-10)**. A board for the third main city of the main map and for the medieval kit it defines. **Every colour, size and photo choice here is a proposal** for the designer to review. The drawing is `reference-board-c3.svg` (same folder). The biome around the city is on the Reed marsh board in `reference-boards-w3.md`, and the method is the one of `reference-board-c1.md`.

This file is **not the spec** and decides nothing. Points that need a decision are under "Questions for the designer".

## What is decided and what is a proposal

**Decided** (spec):
- **C3 is a classic medieval city** with walls and buildings, with its own building kit, one of three (R-2.3-09, E3, E6).
- **A main city has full walls and one gate per road**, and the gate is where the safe zone starts (R-2.3-08, R-6.3-07).
- **Cities sit on the 50 cm grid and face N, E, S or W** (R-11.6-06). **Only service buildings have interiors** (R-11.6-05).
- **Streets are 6 m wide, lanes 3 m, bridges 5 m** (R-11.6-03). The world is built at x1.25-1.5 scale around a 1.8 m character (R-11.6-01, R-11.6-02).
- **The art rules of 9.14:** toon style, a muted world, the fade and roofs off, outlines on characters and interactables only.

**Proposals under review** (spec 10.4), which this board follows as written in `city-layouts.md`:
- **The kit's materials:** grey stone, timber frames, tiled roofs, battlements, a keep. Warm tile is the kit's mark from above.
- **The plan of C3:** four gates (north, west, east, south); two main streets that cross at the central plaza and split the city into four quarters; a moat around the walls, fed by the marsh's river, with a 5 m bridge at each gate; the market in the north-west, the crafting quarter in the north-east, the raid gate and arena hall in the south-east with the tutorial gate beyond it, and the keep (scenery) and houses in the south-west; stone walls with towers at the gates.
- **The sizes** of the measurement sheet in `art-direction.md` and its 45-degree guideline for what the camera hides.

## Kit palette

The swatches are the colours as they should look on screen in the Reed marsh's soft light. They are not texture values. Saturation is the S of HSV.

| Swatch | Hex | Role | Where | Saturation | Value |
|---|---|---|---|---|---|
| Warm tile roof | `#A27460` | roof | every roof; the kit's mark from above | 41% | 64% |
| Tile in shade | `#7E5C50` | roof | the shaded roof side | 37% | 49% |
| Grey stone | `#8C8A84` | wall | the city wall, towers, the keep, ground floors | 6% | 55% |
| Stone in shade | `#6A6966` | wall | shaded stone, wall bases | 4% | 42% |
| Timber frame | `#5E4B3E` | wall | the frames of upper floors, doors, beams | 34% | 37% |
| Infill plaster | `#D6CCB6` | wall | between the timbers | 15% | 84% |
| Cobbled street | `#857F74` | ground | the two main streets and the lanes | 13% | 52% |
| Cobbles in shade | `#66625C` | ground | lanes and the shaded side of a street | 10% | 40% |
| Plaza paving | `#A39E92` | ground | the central plaza, the market square | 10% | 64% |
| Lane earth | `#7A6C5A` | ground | yards and back lanes | 26% | 48% |
| Bridge planks | `#8A7A62` | ground | the decks of the four gate bridges | 29% | 54% |
| Moat water | `#56665E` | water | the moat (unwalkable), fed by the marsh's river | 16% | 40% |
| Moss and ivy | `#6C775B` | prop | wall bases at the moat, old roofs | 24% | 47% |
| Ironwork | `#3F4347` | prop | gate fittings, shop signs, lamps | 11% | 28% |
| Straw and baskets | `#B8A878` | prop | market goods, carts, stable yards | 35% | 72% |
| Cloth (cream) | `#D8CFB8` | accent | market awnings | 15% | 85% |
| Banner (dusty red) | `#985E58` | accent | banners, shop signs | 42% | 60% |
| Banner (dusty blue) | `#5E7088` | accent | banners, shop signs | 31% | 53% |

- **The kit is grey with a warm top:** stone, cobbles and paving are at 4-13%, and the roofs at 37-41%.
- **Timber on plaster is the strongest contrast in the kit** (49). It is on the upper floors.
- `city-layouts.md` names the materials. The hex values, the cobbles, the plank decks of the gate bridges and the banners are this board's proposal.
- The tile and the stone are the colours used on the W3 boards for the towns that borrow this kit.

## Findings

The numbers are colour differences (CIE76 Delta E; higher is easier to tell apart), as on the other boards. They compare flat colours and show where to look first; they are not a pass or fail.

### The tile roof reads against everything beside it

`city-layouts.md` makes the roof the mark of each kit. The tile roof against what lies beside it:

| Beside the roof | Warm tile roof | Tile in shade |
|---|---|---|
| Cobbled street `#857F74` | 19 | 17 |
| Cobbles in shade `#66625C` | 24 | 14 |
| Plaza paving `#A39E92` | 23 | 27 |
| Lane earth `#7A6C5A` | 16 | 11 |
| Grey stone `#8C8A84` | 22 | 22 |
| Moat water `#56665E` | 30 | 22 |
| Marsh wet earth `#6A5E4C` | 20 | 11 |
| Marsh reed bed (straw) `#B5A878` | 25 | 34 |

- **The lit side of a roof is 16-30 from every ground, wall and water beside it.** This is the first of the three kits whose roof stands out everywhere: the water kit's slate is 8-15 from water, and the bazaar kit's flat roof is 2-12 from sand, walls and streets.
- The shaded side is closest to the lane earth and to the marsh's wet earth (11).
- A pitched roof has a lit and a shaded side (13 apart), so from above it is two tones with a ridge between them. A flat roof and flat ground do not have that.
- In the plan drawing roofs cover about 52% of the ground. From above this city is warm roofs with grey lines between them.

### A red warning on the street is close to the roof's colour

The red warning (R-3.3-04), as the game blends it today over each of the city's grounds, against the roof and the banner:

| Ground under a red warning | The warning on it | Against the warm tile roof | Against tile in shade | Against a dusty red banner |
|---|---|---|---|---|
| Cobbled street | `#AC6E63` | 8 | 16 | 7 |
| Cobbles in shade | `#9F5850` | 15 | 17 | 7 |
| Plaza paving | `#BB877B` | 9 | 20 | 16 |
| Lane earth | `#A75F4E` | 14 | 19 | 10 |
| Bridge planks | `#AE6A54` | 11 | 20 | 12 |

- **On a cobbled street the red warning is 8 from the tile roof.** The same warning is 25 from the bare cobbles under it, so it is three times closer to the roof beside it than to the ground it lies on. On the plaza paving it is 9 from the roof.
- For comparison, that warning on cobbles is 32 from the water kit's slate roof `#6E7479` and 40 from the bazaar kit's pale dome `#DCD2BC`.
- **The hues are close:** the tile is at about 18 degrees of hue and the red warning at about 6; the yellow warning is at about 44. The tile is far less saturated than the warning colour itself (41% against 79%), but the warning is see-through, and over grey cobbles it lands at about the tile's strength.
- **What still tells them apart:** a roof has two tones, a ridge and a fixed place; a warning is flat, has its own shape and icon, and is on screen only while the attack runs (spec 9.3).
- **The fade makes it harder.** In a street the roof on the camera side is faded most of the time (see below). A faded tile roof is a see-through warm shape over the street, which is also what a red warning is. How the fade looks is Open (10.4), and this is a case for it (questions 1 and 2).
- A dusty red banner is 7 from the warning on cobbles. Banners are small.

### Grey stone on grey cobbles

| Edge | Difference |
|---|---|
| grey stone against cobbled street | 5 |
| stone in shade against cobbles in shade | 3 |
| grey stone against plaza paving | 8 |
| cobbled street against plaza paving | 12 |
| cobbled street against lane earth | 9 |
| cobbled street against marsh causeway road | 4 |
| bridge planks against cobbled street | 9 |
| bridge planks against moat water | 19 |
| moat water against grey stone | 18 |
| moat water against marsh wet earth | 14 |
| moat water against marsh dark shallow water | 3 |
| timber frame against infill plaster | 49 |
| infill plaster against grey stone | 26 |

- **A stone wall and the street at its foot differ by 5**, and by 3 in the shade. The bazaar kit had the same finding in clay (6). The world has no outlines (R-9.14-05), so the foot of a wall needs something else: a darker base course, a kerb, a gutter, or a paler street (question 3).
- **The street inside a gate and the causeway outside it differ by 4.** The ground does not mark the gate; the moat and the bridge do.
- **The moat is clear:** 18 against the wall and 14 against the marsh's bank. That is less than the water's edge in C1 (21-34). The moat is the marsh's own water (3), as the plan says.
- **The plank deck of a gate bridge** is 19 from the moat and 9 from the street it leads to.

### Warnings on the city's ground

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| M1 greybox ground (today) | `#4b554b` | 39 | 43 | 42 | 36 | 17 |
| Cobbled street | `#857F74` | 25 (64%) | 29 (67%) | 34 (81%) | 27 (76%) | 9 (52%) |
| Cobbles in shade | `#66625C` | 31 (80%) | 37 (87%) | 37 (89%) | 35 (96%) | 13 (78%) |
| Plaza paving | `#A39E92` | 21 (53%) | 22 (53%) | 29 (70%) | 22 (61%) | 5 (30%) |
| Lane earth | `#7A6C5A` | 27 (69%) | 31 (74%) | 41 (98%) | 31 (87%) | 12 (73%) |
| Bridge planks | `#8A7A62` | 24 (62%) | 27 (63%) | 40 (96%) | 28 (76%) | 11 (64%) |

- The warnings keep 53-98% on these grounds, more than on the pale stone of C1 (43-69%) or the sunlit clay of C2 (39-54% for red and yellow). Grey cobbles are a fair ground.
- The red warning is the exception, for the reason above: it is clear against the ground and close to the roofs.
- PvP is off in a normal city (R-6.3-01), so fights here are rare. Guards attack bad-honor players (question 9).

### The medieval kit beside the water kit

- Both kits are stone below and timber above. If the water kit gets pale panels between its timbers (question 2 of the C1 board), the upper floors of the two kits are nearly the same.
- What tells C3 from C1 is then the roof (tile against slate), the stone (grey against pale) and the water (a moat outside the walls against canals inside).
- From above that is enough: the roof is most of the picture. From the side, in a square, the two kits are close (question 5).

## What the camera hides

The drawings show this to scale. The rule is the 45-degree guideline of `art-direction.md`, as on the C1 board, where the table of heights and distances is. The guideline is a proposal.

The frame in the plan drawing (20 m wide at the near edge, 42 m at the far edge, 23 m deep) is the table "What the camera shows" in `art-direction.md`. That table is computed from placeholder camera values (distance 18 m, vertical field of view 40 degrees), which are Open (10.4). If they change, the frame and the distances below change with them. The 45-degree pitch is fixed (R-2.2-05).

**Streets and the plaza**
- **Lanes (3 m):** the row on the camera side always fades.
- **Main streets (6 m):** a 2-floor row on the camera side always fades, as in C1.
- **Looking along a main street,** nothing stands between the camera and the character, and nothing fades. The two main streets run north-south and east-west, so a camera turned north or south looks along one, and a camera turned east or west along the other.
- **The plaza:** a square wider than twice the eaves height has a middle where nothing fades: more than 16 m for 2-floor houses. The drawing's plaza is 24 by 18 m.

**Roofs**
- **Eaves to the street:** a roof pitched at 45 degrees or less hides nothing more than its eaves do.
- **Gables to the street:** a gable hides as much as its peak is tall. A 2-floor house 8 m deep with a 45-degree roof has its peak at 12 m, which covers a 6 m street and reaches the row across it. Gabled fronts are the usual picture of a medieval street (question 4).
- **Jettied upper floors** (an upper floor that juts out over the street) change little at this camera angle. The blockers follow the ground floor.
- Roofs fill about half the screen. Chimneys, dormers and ridge lines are what tells one building from the next.
- Every building needs four finished sides, because the camera turns all the way round.

**The wall, the moat and the gates**
- **A moat as wide as the wall is tall keeps the outside clear.** With an 8 m moat and a wall of 6-8 m, a player on the outer bank is never close enough for the wall to hide them. The drawing uses 8 m; the plan gives no width (question 7).
- **Inside, the wall fades** for a player within 6-8 m of it when the camera looks in from outside.
- **On a gate bridge** the player is within reach of the gate tower (up to about 15 m), so the tower fades whenever the camera looks out from the city along the bridge.
- **Gate passages:** the arch over the player has to be a part that can fade or lift off (R-9.14-02).
- **The wall walk:** the top of the wall passes over the gate passage, which one walkable layer cannot hold. The battlements and anyone on them are scenery (question 6).
- **The tutorial gate** is a fifth gate, beyond the south-east quarter. The plan gives a bridge to "each gate", so it needs a fifth bridge over the moat (question 7).

**The keep**
- Scenery, as the plan says: it is not a service building, so it has no inside (R-11.6-05).
- The measurement sheet gives a landmark building up to about 15 m. A keep that tall fades for a player within 15 m on its far side, which is much of the south-west quarter around it.
- The camera never shows its top from close by. On screen it is a wide stone base, and a roof or battlements when seen from across the quarter.

## Kit pieces

What the kit needs to build C3 as proposed. Sizes are from the measurement sheet in `art-direction.md`.

| Piece | Notes |
|---|---|
| House, 1 and 2 floors | A closed facade on all four sides. About 4 m per floor. Stone ground floor, timber frame with plaster above, tile roof. |
| Service building | A shell whose roof and upper floor can be hidden (R-9.14-02). Doors at least 2 m wide and 3 m tall, corridors at least 2.5 m. Used for the crafting halls, shops with NPCs, the guild hall, the raid gate and arena hall, and the inn. |
| Roofs | Pitched tile roofs with chimneys, dormers and ridge shapes. |
| City wall, battlements | Grey stone, about 6-8 m. It fades when it hides the player. The battlements are scenery. |
| Gate tower and passage | Up to about 15 m, at each of the four road gates; a smaller tutorial gate. The arch over the passage can fade or lift off. |
| Gate bridge | 5 m walkable width over the moat; railings are blockers outside the 5 m. Nothing walkable passes under it. |
| Moat | Unwalkable water around the walls, with a stone edge on the wall side and a bank on the marsh side. |
| Keep | Scenery in the south-west quarter. A closed shell that fades. |
| Plaza | The crossing of the two main streets, and the places for the respawn point, the outpost and the fast travel point. |
| Market | Awnings, counters for NPC vendors, and marked places for player stalls. |
| Street furniture | Signposts at crossings (R-9.14-07), hanging shop signs, banners, wells, carts, barrels. |

Open points that change this list are under the questions: what the inn is for, how fast travel looks, whether mail and the auction need a building, and how many stalls a main city allows.

## The districts of the proposed plan

- **The two main streets:** north-south and east-west, from gate to gate, which fits the grid. Each is about as long as the city (about 170 m).
- **Plaza (centre):** where the streets cross. A new or returning player starts here, with a view along both streets.
- **Market (north-west):** vendors and player stalls. A market square has the same rule as the plaza: wider than 16 m to have a clear middle.
- **Crafting quarter (north-east):** forge, jeweler's bench and job masters.
- **Raid gate and arena hall (south-east):** one large service building, with the tutorial gate beyond it.
- **Keep and houses (south-west):** the keep is scenery, so this is the one quarter without a service. It is where the city can be quiet.
- **The four gates:** north to T9, west to T10, east to M6 and south to T11. Each has a tower, a passage and a bridge.

## The towns that borrow this kit

M5 (Snow taiga), T7 (Frost peaks), T8 (Storm cliffs), T9 (Ash woods), T10 (Palm coast) and T11 (Fern jungle) use the medieval kit with their biome's dressing (`city-layouts.md` E7). The numbers are on the W3 boards:

- The tile roof is 22-48 from the main ground of each of those biomes.
- It is the kit with the widest spread of climates, from snow to a tropical beach. Snow on the roofs covers the tile (question 6 of the W3 boards).
- In the Ash woods and on the Storm cliffs the tile is the warmest colour in sight.

## Photo references

Links to free-licence files on Wikimedia Commons, each looked at before it was listed. No image is stored in the repository. They are references for materials, shapes and layout, not a target for detail: the game is stylized toon.

**The whole look: tile roofs inside a wall**
- [Carcassonne from the air](https://commons.wikimedia.org/wiki/File:1_carcassonne_aerial_2016_%28cropped%29.jpg): a walled town from the air: roofs packed inside a wall with towers. Chensiyuan, CC BY-SA 4.0.
- [Korčula from the air](https://commons.wikimedia.org/wiki/File:Kor%C4%8Dula,_Croatia.jpg): a walled town of tile roofs seen from above. Quintin Soloviev, CC BY 4.0.
- [Dülmen from the air](https://commons.wikimedia.org/wiki/File:D%C3%BClmen,_Ortsansicht_--_2014_--_4328.jpg): tile roofs from above, with the streets as gaps between them. Dietmar Rabich, CC BY-SA 4.0.
- [Bregenz roofs from the Martinsturm](https://commons.wikimedia.org/wiki/File:Blick_vom_Martinsturm_auf_Bregenz_04.jpg): tile roofs from a tower: lit and shaded sides, ridges and chimneys. H.Helmlechner, CC BY-SA 4.0.
- [Plain clay tiles](https://commons.wikimedia.org/wiki/File:Biberschwanz-Kronendeckung_20090619.jpg): plain tiles close up: the surface of the roof. Mätes II., CC BY-SA 3.0.
- [A dormer in a tile roof](https://commons.wikimedia.org/wiki/File:Biberschwanz_Dach_-_panoramio.jpg): a dormer in an old tile roof. Richard Mayer, CC BY 3.0.

**Streets and squares**
- [The Shambles, York](https://commons.wikimedia.org/wiki/File:The_Shambles,_York_-_geograph.org.uk_-_2971409.jpg): a lane between jettied timber houses. John M Wheatley, CC BY-SA 2.0.
- [Elbstrasse, Lauenburg](https://commons.wikimedia.org/wiki/File:Elbstra%C3%9Fe_%28Lauenburg%29.jpg): a cobbled street between timber-framed houses with gables to the street. Agentakt, CC BY 4.0.
- [A lane in Rothenburg ob der Tauber](https://commons.wikimedia.org/wiki/File:Rothenburg_ob_der_Tauber_Gasse_811.jpg): a lane with eaves to the street. GFreihalter, CC BY-SA 4.0.
- [Timber Frame and Jettied Floor House, Aldbury](https://commons.wikimedia.org/wiki/File:Timber_Frame_and_Jettied_Floor_House,_Aldbury_-_geograph.org.uk_-_1578022.jpg): a jettied timber-framed house under a tile roof. Ian Petticrew, CC BY-SA 2.0.
- [Piries Place timber framing in Horsham, West Sussex, England](https://commons.wikimedia.org/wiki/File:Piries_Place_timber_framing_in_Horsham,_West_Sussex,_England.jpg): timber and plaster close up. Acabashi, CC BY-SA 4.0.
- [Kutná Hora old city, pavement](https://commons.wikimedia.org/wiki/File:Kutn%C3%A1_hora_old_city,_pavement_design_-_cz.jpg): a cobbled street: the ground. Lysippos, CC BY 3.0.
- [Herzogenaurach, Marktplatz 4](https://commons.wikimedia.org/wiki/File:Herzogenaurach_-_Marktplatz_4_-_01.jpg): a timber-framed house on a market square. Howwi, CC BY-SA 3.0.
- [Old Town Market Square in Kraków, 2024](https://commons.wikimedia.org/wiki/File:Old_Town_Market_Square_in_Krak%C3%B3w,_2024_%2801%29.jpg): market stalls on a wide square. Bahnfrend, CC BY-SA 4.0.
- [Maulbronn Monastery, timber-framed building](https://commons.wikimedia.org/wiki/File:00_0187_Maulbronn_-_Kloster_Maulbronn.jpg): a large timber-framed building under a tile roof: the size of a service building. W. Bulach, CC BY-SA 4.0.

**Walls, gates and the moat**
- [Aigues-Mortes, a fortified town](https://commons.wikimedia.org/wiki/File:Aigues_Mortes_-_a_fortified_town_%2849711092361%29.jpg): a town wall with towers, from outside. Mike McBey, CC BY 2.0.
- [Inside Aigues-Mortes](https://commons.wikimedia.org/wiki/File:Inside_Aigues_Mortes_%2849711143151%29.jpg): the wall from inside, with the stairs to the wall walk. Mike McBey, CC BY 2.0.
- [Rothenburg ob der Tauber, the wall walk](https://commons.wikimedia.org/wiki/File:Rothenburg_ob_der_Tauber_Wehrgang-20130816-RM-143922.jpg): a wall with a roofed wall walk, and the tile roofs behind it. Reinhold Möller Ermell, CC BY-SA 4.0.
- [Rothenburg ob der Tauber, on the wall walk](https://commons.wikimedia.org/wiki/File:Rothenburg_ob_der_Tauber_Wehrgang-20130816-RM-145713.jpg): the view from a wall walk over the roofs. Reinhold Möller Ermell, CC BY-SA 4.0.
- [Vischering Castle, the bridge over the moat](https://commons.wikimedia.org/wiki/File:L%C3%BCdinghausen,_Burg_Vischering,_Hauptburg_--_2022_--_9955.jpg): a timber bridge over a moat to a gate. Dietmar Rabich, CC BY-SA 4.0.
- [Rheintorturm, Konstanz](https://commons.wikimedia.org/wiki/File:Konstanz_Rheintorturm_%281%29_%289509019849%29.jpg): a gate tower. Gerhard Giebener, CC BY 2.0.
- [Rothenburg ob der Tauber, town wall and former moat](https://commons.wikimedia.org/wiki/File:Rothenburg_ob_der_Tauber,_Stadtmauer,_ehem._Wassergraben_Kummereck_bis_W%C3%BCrzburger_Tor,_001.jpg): a town wall above its moat, now dry. Tilman2007, CC BY-SA 3.0.
- [Norman keep, Cardiff Castle](https://commons.wikimedia.org/wiki/File:Norman_Keep,_Cardiff_Castle_-_geograph.org.uk_-_7290285.jpg): a keep. Alan Hughes, CC BY-SA 2.0.

**Details**
- [Herbal shop sign, Győr](https://commons.wikimedia.org/wiki/File:Herb%C3%A1ria,_herbal_shop_sign,_2018_Gy%C5%91r.jpg): a wrought-iron hanging shop sign. Globetrotter19, CC BY-SA 3.0.
- [Schongau, the roof of the town wall](https://commons.wikimedia.org/wiki/File:Schongau,_Dach_der_Stadtmauer_bei_M%C3%BCnzgasse_46,_1.jpeg): a tile roof on a town wall, seen from above. Renardo la vulpo, CC0.
- [Roof of the Bürgerspital, Steyr](https://commons.wikimedia.org/wiki/File:Dach_des_Steyrer_Buergerspitals_1.jpg): an old tile roof with moss and a chimney. Christoph Waghubinger (Lewenstein), CC BY-SA 4.0.
- [Postoloprty, old pavement](https://commons.wikimedia.org/wiki/File:Postoloprty,_old_pavement_-_cz.jpg): cobbles close up. Lysippos, CC BY-SA 3.0.

## Questions for the designer

Nothing below is decided here.

1. **Red warnings and tile roofs:** a red warning on cobbles is 8 from the roof's colour. Does the tile move away from red (browner, greyer, more ochre), or are the warning's shape and icon enough?
2. **The fade on tile roofs** (Open in 10.4): a faded roof over a street is a see-through warm shape. Does that decide between dither and transparency, or should a faded roof lose its colour?
3. **Grey stone on grey cobbles:** what tells the foot of a wall from the street (5)?
4. **Roof direction:** eaves to the street (hides less) or gables to the street (the usual medieval look, hides more)? This is question 3 of the C1 board.
5. **The medieval kit and the water kit:** both are stone with timber above. What besides the roof colour tells them apart from the side?
6. **The wall walk and the keep:** are the battlements scenery only? How tall is the keep, given that the sheet's limit is about 15 m?
7. **The moat and the bridges:** how wide is the moat (the drawing uses 8 m)? Are the bridge decks timber or stone? Does the tutorial gate get its own bridge?
8. **Banners:** heraldic banners are usually red, blue, yellow and green, the four warning hues. The board has dusty red and dusty blue. Is that acceptable in a city (question 4 of the C2 board)?
9. **Fights in a city:** do guard fights need warnings that read between tile roofs?
10. **Open points of the spec that change the kit list:** what the inn is for (R-11.6-05), how fast travel looks, whether mail and the auction need a building, and the stall cap of a main city (R-4.5-03).
11. **The review of the C3 plan** (10.4) is still open, and this board follows the plan as written.

## Not covered

- Interiors: floors, walls seen from above with the roof off, furniture.
- NPCs and guards as models.
- The biome dressing of the medieval kit for M5 and T7-T11.
- M6's scrap look and the Ember caldera (W4).
- A to-scale plan of the whole city, which `city-layouts.md` leaves until the plan is accepted.
