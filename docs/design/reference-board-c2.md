# Reference board: C2, the caravan bazaar city

Status: **art reference, v2 (2026-11-10)**, updated with the designer's answers of 2026-11-10. A board for the second main city of the main map and for the bazaar kit it defines. **Every colour, size and photo choice here is a proposal** for the designer to review. The drawing is `reference-board-c2.svg` (same folder). The biome around the city is on the Red sands board in `reference-boards-w2.md`, and the method is the one of `reference-board-c1.md`.

This file is **not the spec**. **The designer answered its questions on 2026-11-10.** Where a paragraph below calls a point open and cites a question, the answer under that question, at the end of the file, settles it. The colours themselves stay proposals.

## What is decided and what is a proposal

**Decided** (spec):
- **C2 is a caravan bazaar city** with its own building kit, one of three (R-2.3-09, E3, E5).
- **A main city has full walls and one gate per road**, and the gate is where the safe zone starts (R-2.3-08, R-6.3-07).
- **Cities sit on the 50 cm grid and face N, E, S or W** (R-11.6-06). **Only service buildings have interiors** (R-11.6-05).
- **Streets are 6 m wide, lanes 3 m** (R-11.6-03). The world is built at x1.25-1.5 scale around a 1.8 m character (R-11.6-01, R-11.6-02).
- **The art rules of 9.14:** toon style, a muted world, the fade and roofs off, outlines on characters and interactables only.

**Accepted by the designer on 2026-11-10**, with the answers at the end of this file (`city-layouts.md`):
- **The plan of C2:** three gates (north, north-east, south); one long covered bazaar street from the north gate to the south gate, whose cloth roof hides when the player walks under it; the plaza in the middle around a well; a caravan yard inside each gate; the crafting quarter east of the plaza; the raid gate and arena hall west of it; a tutorial gate in the west; streets in the shade of awnings and high clay walls.

**Still a proposal** (spec 10.4):
- **The kit's materials,** with the designer's answers worked in: clay walls with a darker band at the foot, white domes and flat roofs, cloth awnings, carved wooden screens, caravan yards, and a muted teal glazed tile as the one colour accent. White domes are the kit's mark from above.
- **The sizes** of the measurement sheet in `art-direction.md` and its 45-degree guideline for what the camera hides.

## Kit palette

The swatches are the colours as they should look on screen in the Red sands' light. They are not texture values. Saturation is the S of HSV.

| Swatch | Hex | Role | Where | Saturation | Value |
|---|---|---|---|---|---|
| White dome | `#F0ECE2` | roof | lime-washed domes; the kit's mark from above | 6% | 94% |
| Dome in shade | `#C6C1B6` | roof | the shaded side of a dome | 8% | 78% |
| Flat roof (clay) | `#C0A688` | roof | flat roofs and parapets | 29% | 75% |
| Clay wall | `#B4977A` | wall | house walls, the city wall | 32% | 71% |
| Clay in shade | `#8C7460` | wall | shaded walls, lanes | 31% | 55% |
| Wall base band | `#64503F` | wall | a darker band at the foot of every wall | 37% | 39% |
| Carved wood | `#6E5540` | wall | screens, doors, beams | 42% | 43% |
| Packed street | `#A38A70` | ground | streets and lanes | 31% | 64% |
| Street in shade | `#7C6A5C` | ground | under awnings and between high walls | 26% | 49% |
| Plaza paving | `#BFAF98` | ground | the plaza around the well | 20% | 75% |
| Yard sand | `#C4A886` | ground | caravan yards (the Red sands' tan sand) | 32% | 77% |
| Cloth (cream) | `#DCCFB2` | accent | awnings, the bazaar's cloth roof | 19% | 86% |
| Cloth (dusty red) | `#A0665A` | accent | awnings, rugs | 44% | 63% |
| Cloth (ochre) | `#B39A62` | accent | awnings, rugs | 45% | 70% |
| Cloth (indigo) | `#5F6F8C` | accent | awnings, rugs | 32% | 55% |
| Glazed tile (teal) | `#5F8A88` | accent | dome tips, door frames | 31% | 54% |
| Well water | `#5F8482` | water | the well on the plaza | 28% | 52% |
| Palm frond | `#6F7F58` | prop | palms in yards and at the well | 31% | 50% |
| Rope and basket | `#A58F70` | prop | stacked goods, baskets, ropes | 32% | 65% |

- **Almost everything built or walked on is one clay colour:** walls, flat roofs, streets and yards are all tan at 26-32%.
- **Cloth and carved wood carry the contrast.** The cloth swatches are at 19-45%, below the warning colours (62-79%).
- **The glazed tile** was this board's addition, and the designer kept it (question 5).
- **The white dome and the wall base band** are the designer's answers (questions 1 and 2). Their hex values are this board's proposal.
- `city-layouts.md` names the materials; the hex values for domes, clay and cloth are this board's proposal.

## Findings

The numbers are colour differences (CIE76 Delta E; higher is easier to tell apart), as on the other boards. They compare flat colours and show where to look first; they are not a pass or fail.

### A clay city on sand is one colour

| Edge | Difference |
|---|---|
| clay wall against packed street | 6 |
| clay in shade against street in shade | 6 |
| clay wall against yard sand | 7 |
| clay wall against red sands tan sand | 7 |
| packed street against yard sand | 12 |
| packed street against plaza paving | 14 |
| packed street against red sands caravan road | 3 |
| carved wood against clay wall | 26 |
| wall base band against clay wall | 29 |
| wall base band against packed street | 24 |
| wall base band against street in shade | 11 |

- **A wall and the street at its foot differ by 6**, in the sun and in the shade. A wall and the sand outside the city differ by 7. For comparison, the water's edge in C1 is at 21-34.
- **The street inside the gate and the caravan road outside it differ by 3.** The gate itself has to mark the change.
- The world has no outlines (R-9.14-05), so by colour alone a player cannot tell where the street ends and a wall begins.
- **The designer chose a darker band at the foot of walls** (question 1). The proposed band is 29 from the wall above it and 24 from a sunlit street. Against a street in shade it is 11, so in shaded lanes the band needs to be darker still, or the lane lighter.
- Carved wood stands out from clay (26). It is the kit's darkest material.

### Roofs from above

`city-layouts.md` makes the roof the mark of each kit. A dome and a flat roof against what lies beside them:

| Beside the roof | White dome | Dome in shade | Flat roof (clay) |
|---|---|---|---|
| Packed street `#A38A70` | 37 | 23 | 11 |
| Street in shade `#7C6A5C` | 48 | 33 | 25 |
| Plaza paving `#BFAF98` | 23 | 10 | 6 |
| Yard sand `#C4A886` | 29 | 18 | 2 |
| Red sands terracotta sand `#B58A6C` | 39 | 27 | 12 |
| Clay wall `#B4977A` | 33 | 20 | 6 |
| Flat roof (clay) `#C0A688` | 28 | 16 | |

- **The flat roof disappears:** 2 against the yard sand, 6 against a wall and the plaza paving, 11-12 against a sunlit street and terracotta sand. Only over a shaded street does it stand out (25).
- **The white dome reads everywhere:** 23 on the plaza paving, 29 on yard sand, 37-48 over streets, and 28 against its own flat roof.
- The dome was a pale cream `#DCD2BC` before the designer asked for a whiter one (question 2). It then read only moderately: 13 on the plaza paving, 18 on yard sand, 27-39 over streets and 17 against its own flat roof.
- From above, a block of flat roofs and the street between them are close to one surface. The domes, the cloth and the shade in the streets are what draws the plan (question 2).

### Warnings on the city's ground

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| M1 greybox ground (today) | `#4b554b` | 39 | 43 | 42 | 36 | 17 |
| Packed street | `#A38A70` | 20 (52%) | 23 (53%) | 38 (91%) | 25 (70%) | 9 (53%) |
| Street in shade | `#7C6A5C` | 26 (67%) | 32 (75%) | 40 (95%) | 33 (90%) | 12 (72%) |
| Plaza paving | `#BFAF98` | 18 (46%) | 18 (42%) | 31 (73%) | 19 (54%) | 5 (27%) |
| Yard sand | `#C4A886` | 17 (44%) | 17 (39%) | 35 (85%) | 20 (56%) | 7 (41%) |

- On the sunlit grounds red and yellow keep 39-54% of the difference they have on today's M1 ground. In shaded streets they keep 67-75%.
- PvP is off in a normal city (R-6.3-01), so fights here are rare. Guards attack bad-honor players (question 6).

## What the camera hides

The drawings show this to scale. The rule is the 45-degree guideline of `art-direction.md`, as on the C1 board, where the table of heights and distances is. The guideline is a proposal.

**The covered bazaar street**
- Looking **across** the street, the row on the camera side fades, as in any 6 m street with 2-floor houses.
- Looking **along** the street, the cloth roof lies between the camera and the character. `city-layouts.md` already says it hides when the player walks under it.
- So inside the covered street something is always hidden, whichever way the camera looks. In C1, looking along a street keeps everything solid.
- **The cloth roof is seen from outside:** from the plaza, the yards and the side lanes. Inside, the picture is the stalls, the goods and the far wall.
- **The designer's answer** (question 3): the roof is made of panels, all panels over the street hide while a player is under it, and the cloth's shade stays on the ground, so the street still reads as covered.

**Lanes, roofs and yards**
- **Lanes between high clay walls:** 3 m wide, so the row on the camera side always fades.
- **Flat roofs:** a flat roof hides as much as its edge is high, parapet included.
- **Domes:** a dome whose centre is at least about one and a half times its radius from the roof edge hides nothing more than the roof edge does. A dome at the edge of a roof hides more.
- **Caravan yards:** a yard wider than twice the height of the walls around it has a middle where nothing fades. The drawing's 20 m yard between 1-floor rows is clear everywhere except close to the camera-side wall.
- **Gate passages:** the arch over the player has to be a part that can fade or lift off (R-9.14-02).

**The roofscape**
- Roofs fill most of the screen, and flat roofs are plain rectangles. They need their own dressing to be worth looking at: parapets, domes, stair heads, rugs, jars, drying cloth.
- Roofs are not walkable (one walkable layer).
- Every building needs four finished sides, because the camera turns all the way round.

## Kit pieces

What the kit needs to build C2 as proposed. Sizes are from the measurement sheet in `art-direction.md`.

| Piece | Notes |
|---|---|
| House, 1 and 2 floors | A closed clay facade on all four sides. About 4 m per floor. A flat roof with a parapet; some with a dome. |
| Service building | A shell whose roof and upper floor can be hidden (R-9.14-02). Doors at least 2 m wide and 3 m tall, corridors at least 2.5 m. Used for the crafting halls, shops with NPCs, the guild hall, the raid gate and arena hall, and the inn. |
| Roofs | Flat roofs with parapets, white domes, and roof dressing. |
| Wall base band | A darker band at the foot of every wall, where it meets the street. |
| Cloth roof and awnings | The bazaar street's roof, in panels that can hide, and awnings over lanes and stalls. |
| Carved wooden screens | Windows, balconies and doors. |
| City wall, tower, gate | Clay, about 6-8 m; towers up to about 15 m. Both fade when they hide the player. Three road gates (north, north-east, south) and a smaller tutorial gate (west). |
| Caravan yard | An open yard inside each gate: troughs, hitching posts, stacked goods. Pack animals are scenery. |
| Plaza and well | The well, and the places for the respawn point, the outpost and the travel stone (fast travel). |
| Market | Counters for NPC vendors and marked places for player stalls along the bazaar street. |
| Street furniture | Signposts at crossings (R-9.14-07), jars, baskets, rugs, lamps, palms. |

The designer's answers on the inn, fast travel, mail and the auction, and stalls are under question 7. They say what the inn is, and they add a travel stone, a mailbox and an auction house to this list.

## The districts of the proposed plan

- **The bazaar street:** it runs north-south from gate to gate, which fits the grid, and it is about as long as the city (about 170 m). Vendors and player stalls line it.
- **Plaza (centre):** around the well, where the street from the north-east gate meets the bazaar. A new or returning player starts here. The plaza paving is the palest ground, and the dome reads least on it (23).
- **Caravan yards:** one inside each of the three gates. They are the first picture of the city for a traveller, and the widest open ground in it.
- **The street from the north-east gate:** buildings face N, E, S or W (R-11.6-06), so a street toward the north-east runs in straight legs with right-angle turns.
- **Crafting quarter:** east of the plaza, toward the north-east gate.
- **Raid gate and arena hall:** west of the plaza. One large service building.
- **Tutorial gate (west):** small and apart from the main roads. Every way through the level-10 gate needs a visible mark (`open-world-content.md`).

## Photo references

Links to free-licence files on Wikimedia Commons, each looked at before it was listed. No image is stored in the repository. They are references for materials, shapes and layout, not a target for detail: the game is stylized toon.

**The whole look: clay, domes and flat roofs**
- [Ait Zineb - Ksar of Aït Benhaddou - 20190716180605](https://commons.wikimedia.org/wiki/File:Ait_Zineb_-_Ksar_of_A%C3%AFt_Benhaddou_-_20190716180605.jpg): a clay town from above: flat roofs, parapets and lanes as the camera would see them. C messier, CC BY-SA 4.0.
- [Kashan Bazzar by Hossein Emami](https://commons.wikimedia.org/wiki/File:Kashan_Bazzar_by_Hossein_Emami.jpg): a bazaar's roof: clay domes in rows, with dyed cloth drying between them. Hosseinemami1988, CC BY-SA 4.0.
- [Kashan Bazaar roof panorama](https://commons.wikimedia.org/wiki/File:Kashan_Bazaar_roof_panorama.jpg): clay domes close up: the shape and surface of a dome. Phillip Maiwald (Nikopol), CC BY-SA 3.0.
- [Rooftops of Yazd - panoramio](https://commons.wikimedia.org/wiki/File:Rooftops_of_Yazd_-_panoramio.jpg): a dome among flat roofs: how a dome marks one building in a clay roofscape. Alen Ištoković, CC BY 3.0.
- [Ksar Ghardaia place marché 1](https://commons.wikimedia.org/wiki/File:Ksar_Ghardaia_place_march%C3%A9_1.jpg): a clay town above an arcaded market square: the plaza. Camille Gillet, CC BY-SA 4.0.

**The bazaar: cloth, shade and screens**
- [Covered Souk Semmarine, Marrakech, 2008](https://commons.wikimedia.org/wiki/File:Covered_Souk_Semmarine,_Marrakech,_2008.jpg): a covered market street: the roof's striped light on the ground and the stalls along it. DimiTalen, CC0.
- [Ait Zineb - Ksar of Aït Benhaddou - 20190716174408](https://commons.wikimedia.org/wiki/File:Ait_Zineb_-_Ksar_of_A%C3%AFt_Benhaddou_-_20190716174408.jpg): a clay lane hung with cloth and goods: awnings and stalls between clay walls. C messier, CC BY-SA 4.0.
- [Tok-i-Sarrafon Bukhara](https://commons.wikimedia.org/wiki/File:Tok-i-Sarrafon_Bukhara.jpg): a domed market crossing over a street: a service building in the kit's shapes. Ymblanter, CC BY-SA 4.0.
- [Moucharabiehs 1](https://commons.wikimedia.org/wiki/File:Moucharabiehs_1.jpg): carved wooden screens. Delphine Montagne, CC BY-SA 4.0.

**Caravan yards**
- [Caravanserai in Kirkuk 01](https://commons.wikimedia.org/wiki/File:Caravanserai_in_Kirkuk_01.jpg): a caravan yard with arcades on all sides. Levi Clancy, CC BY-SA 4.0.
- [Courtyard of the caravanserai Mahyar by Eugène Flandin](https://commons.wikimedia.org/wiki/File:Courtyard_of_the_caravanserai_Mahyar_by_Eug%C3%A8ne_Flandin.jpg): an old drawing of a caravan yard with animals and goods: the yard in use. Eugène Flandin, public domain.
- [Caravan of camels resting below Zion Gate, Jerusalem](https://commons.wikimedia.org/wiki/File:CARAVAN_OF_CAMELS_RESTING_BELOW_ZION_GATE_OUTSIDE_THE_WALLS_OF_THE_OLD_CITY_OF_JERUSALEM_DURING_THE_OTTOMAN_ERA._%D7%A6%D7%99%D7%9C%D7%95%D7%9D_%D7%90%D7%95%D7%A8%D7%97%D7%AA_%D7%92%D7%9E%D7%9C%D7%99%D7%9D_%D7%91%D7%9E%D7%A0%D7%95%D7%97%D7%94,_%D7%9C%D7%99%D7%93_%D7%A9%D7%A2%D7%A8_%D7%A6%D7%99%D7%95%D7%9F_%D7%91%D7%A2%D7%99%D7%A8_%D7%94%D7%A2%D7%AA%D7%99%D7%A7%D7%94_%D7%91%D7%99%D7%A8%D7%95%D7%A9%D7%9C%D7%99%D7%9D,_%D7%91.jpg): pack animals resting by a city wall. AMERICAN COLONY, public domain.

**Walls and gates**
- [Ota Darvaza, the west gate of Khiva](https://commons.wikimedia.org/wiki/File:Ota_Darvaza_aka_West_Gates,_Khiva_%28Ota_Darvaza,_%D0%90%D1%82%D0%B0-%D0%B4%D0%B0%D1%80%D0%B2%D0%B0%D0%B7%D0%B0%29.jpg): a clay gate between two round towers. Petar Milošević, CC BY-SA 4.0.
- [City Walls of Itchan Kala (Khiva)](https://commons.wikimedia.org/wiki/File:City_Walls_of_Itchan_Kala_%28Khiva%29.jpg): sloped clay city walls with towers. Adam Harangozó, CC BY-SA 4.0.
- [Itchan Kala wall 02](https://commons.wikimedia.org/wiki/File:Itchan_Kala_wall_02.jpg): a clay wall and the town behind it, seen from above. Bgag, CC0.

## Questions for the designer, and the answers

The designer answered these on 2026-11-10. The questions stand as they were asked, so that the references to them in this file still hold. The full list is `reference-boards-questions.md`, and the "Q" numbers refer to it.

1. **One clay colour:** walls, flat roofs, streets and sand are within 2-12 of each other. What tells the foot of a wall from the street, and a roof from the ground: light and shade only, a darker band at the foot of walls, paler roofs?
   - **Answer (Q20):** a darker band at the foot of every wall.
2. **The dome as the kit's mark:** it is the only roof that reads, and only moderately. Does it get a different tone (also question 6 of `reference-boards-w2.md`), and which buildings carry one?
   - **Answer (Q19):** a different tone: whiter, a lime-washed white. Which buildings carry a dome was not asked and stays open.
3. **The cloth roof:** one unbroken roof or panels with gaps? How much of it hides when the player is under it? Does its shade stay on the ground while it is hidden?
   - **Answer (Q10):** panels. All panels over the street hide while a player is under it, and the shade stays on the ground.
4. **Cloth colours:** dusty red, ochre and indigo are muted versions of three warning hues, and a cloth roof is a large area. The guideline to avoid large areas of the warning hues is written for biome palettes. Does it apply to a city's cloth?
   - **Answer (Q5):** muted warning hues are fine on cloth and banners, at any size.
5. **Glazed tile:** may the kit have one muted colour accent (dome tips, door frames) that `city-layouts.md` does not list?
   - **Answer (Q22):** keep it.
6. **Fights in a city:** do guard fights need warnings that read on sunlit clay and paving?
   - **Answer (Q2, Q1):** no. City ground is chosen for its look, and the warning itself changes on light ground.
7. **Open points of the spec that change the kit list:** what the inn is for (R-11.6-05), how fast travel looks, whether mail and the auction need a building, and the stall cap of a main city (R-4.5-03).
   - **Answer (Q27):** the inn is a "social place and we may add some food seller" (the designer's words). Fast travel is a travel stone on the plaza, the same object in all three cities. Mail and the auction get places in the city, a mailbox and an auction house; the spec leaves that point to the M4 plan, so the two still have to be brought together. The stall number is left to economy tuning.
8. **The review of the C2 plan** (10.4) is still open, and this board follows the plan as written.
   - **Answer (Q31):** accepted, with these answers.

## Not covered

- Interiors: floors, walls seen from above with the roof off, furniture.
- NPCs, guards and pack animals as models.
- The medieval kit (C3) and M6's scrap look.
- The biome dressing of the bazaar kit for M4, T4, T5 and T6.
- A to-scale plan of the whole city, which `city-layouts.md` leaves until the plan is accepted.
