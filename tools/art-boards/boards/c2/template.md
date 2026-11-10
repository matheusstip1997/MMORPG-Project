# Reference board: C2, the caravan bazaar city

Status: **art reference, v1 (2026-11-10)**. A board for the second main city of the main map and for the bazaar kit it defines. **Every colour, size and photo choice here is a proposal** for the designer to review. The drawing is `reference-board-c2.svg` (same folder). The biome around the city is on the Red sands board in `reference-boards-w2.md`, and the method is the one of `reference-board-c1.md`.

This file is **not the spec** and decides nothing. Points that need a decision are under "Questions for the designer".

## What is decided and what is a proposal

**Decided** (spec):
- **C2 is a caravan bazaar city** with its own building kit, one of three (R-2.3-09, E3, E5).
- **A main city has full walls and one gate per road**, and the gate is where the safe zone starts (R-2.3-08, R-6.3-07).
- **Cities sit on the 50 cm grid and face N, E, S or W** (R-11.6-06). **Only service buildings have interiors** (R-11.6-05).
- **Streets are 6 m wide, lanes 3 m** (R-11.6-03). The world is built at x1.25-1.5 scale around a 1.8 m character (R-11.6-01, R-11.6-02).
- **The art rules of 9.14:** toon style, a muted world, the fade and roofs off, outlines on characters and interactables only.

**Proposals under review** (spec 10.4), which this board follows as written in `city-layouts.md`:
- **The kit's materials:** clay walls, domes and flat roofs, cloth awnings, carved wooden screens, caravan yards. Pale domes are the kit's mark from above.
- **The plan of C2:** three gates (north, north-east, south); one long covered bazaar street from the north gate to the south gate, whose cloth roof hides when the player walks under it; the plaza in the middle around a well; a caravan yard inside each gate; the crafting quarter east of the plaza; the raid gate and arena hall west of it; a tutorial gate in the west; streets in the shade of awnings and high clay walls.
- **The sizes** of the measurement sheet in `art-direction.md` and its 45-degree guideline for what the camera hides.

## Kit palette

The swatches are the colours as they should look on screen in the Red sands' light. They are not texture values. Saturation is the S of HSV.

{{PALETTE}}

- **Almost everything built or walked on is one clay colour:** walls, flat roofs, streets and yards are all tan at 26-32%.
- **Cloth and carved wood carry the contrast.** The cloth swatches are at 19-45%, below the warning colours (62-79%).
- **The glazed tile** is this board's addition and is not in `city-layouts.md` (question 5).
- `city-layouts.md` names the materials; the hex values for domes, clay and cloth are this board's proposal.

## Findings

The numbers are colour differences (CIE76 Delta E; higher is easier to tell apart), as on the other boards. They compare flat colours and show where to look first; they are not a pass or fail.

### A clay city on sand is one colour

{{EDGE}}

- **A wall and the street at its foot differ by 6**, in the sun and in the shade. A wall and the sand outside the city differ by 7. For comparison, the water's edge in C1 is at 21-34.
- **The street inside the gate and the caravan road outside it differ by 3.** The gate itself has to mark the change.
- The world has no outlines (R-9.14-05), so by colour alone a player cannot tell where the street ends and a wall begins. Light and shade, a darker band at the foot of walls, or paler roofs would have to do it (question 1).
- Carved wood stands out from clay (26). It is the kit's darkest material.

### Roofs from above

`city-layouts.md` makes the roof the mark of each kit. A dome and a flat roof against what lies beside them:

{{ROOF}}

- **The flat roof disappears:** 2 against the yard sand, 6 against a wall and the plaza paving, 11-12 against a sunlit street and terracotta sand. Only over a shaded street does it stand out (25).
- **The pale dome is the only roof that reads**, and only moderately: 13 on the plaza paving, 18 on yard sand, 27-39 over streets. Against its own flat roof it is at 17.
- From above, a block of flat roofs and the street between them are close to one surface. The domes, the cloth and the shade in the streets are what draws the plan (question 2).

### Warnings on the city's ground

{{CHECK}}

- On the sunlit grounds red and yellow keep 39-54% of the difference they have on today's M1 ground. In shaded streets they keep 67-75%.
- PvP is off in a normal city (R-6.3-01), so fights here are rare. Guards attack bad-honor players (question 6).

## What the camera hides

The drawings show this to scale. The rule is the 45-degree guideline of `art-direction.md`, as on the C1 board, where the table of heights and distances is. The guideline is a proposal.

**The covered bazaar street**
- Looking **across** the street, the row on the camera side fades, as in any 6 m street with 2-floor houses.
- Looking **along** the street, the cloth roof lies between the camera and the character. `city-layouts.md` already says it hides when the player walks under it.
- So inside the covered street something is always hidden, whichever way the camera looks. In C1, looking along a street keeps everything solid.
- **The cloth roof is seen from outside:** from the plaza, the yards and the side lanes. Inside, the picture is the stalls, the goods and the far wall.
- How much of the cloth hides (the whole street's roof or the part near the player) belongs to the open fade (10.4). If the cloth's pattern of light and shade stays on the ground while the cloth is hidden, the street still reads as covered (question 3).

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
| Roofs | Flat roofs with parapets, pale domes, and roof dressing. |
| Cloth roof and awnings | The bazaar street's roof, in panels that can hide, and awnings over lanes and stalls. |
| Carved wooden screens | Windows, balconies and doors. |
| City wall, tower, gate | Clay, about 6-8 m; towers up to about 15 m. Both fade when they hide the player. Three road gates (north, north-east, south) and a smaller tutorial gate (west). |
| Caravan yard | An open yard inside each gate: troughs, hitching posts, stacked goods. Pack animals are scenery. |
| Plaza and well | The well, and the places for the respawn point, the outpost and the fast travel point. |
| Market | Counters for NPC vendors and marked places for player stalls along the bazaar street. |
| Street furniture | Signposts at crossings (R-9.14-07), jars, baskets, rugs, lamps, palms. |

Open points that change this list are under the questions: what the inn is for, how fast travel looks, whether mail and the auction need a building, and how many stalls a main city allows.

## The districts of the proposed plan

- **The bazaar street:** it runs north-south from gate to gate, which fits the grid, and it is about as long as the city (about 170 m). Vendors and player stalls line it.
- **Plaza (centre):** around the well, where the street from the north-east gate meets the bazaar. A new or returning player starts here. The plaza paving is the palest ground, and the dome reads worst on it (13).
- **Caravan yards:** one inside each of the three gates. They are the first picture of the city for a traveller, and the widest open ground in it.
- **The street from the north-east gate:** buildings face N, E, S or W (R-11.6-06), so a street toward the north-east runs in straight legs with right-angle turns.
- **Crafting quarter:** east of the plaza, toward the north-east gate.
- **Raid gate and arena hall:** west of the plaza. One large service building.
- **Tutorial gate (west):** small and apart from the main roads. Every way through the level-10 gate needs a visible mark (`open-world-content.md`).

## Photo references

Links to free-licence files on Wikimedia Commons, each looked at before it was listed. No image is stored in the repository. They are references for materials, shapes and layout, not a target for detail: the game is stylized toon.

**The whole look: clay, domes and flat roofs**
{{REFS:look}}

**The bazaar: cloth, shade and screens**
{{REFS:bazaar}}

**Caravan yards**
{{REFS:yards}}

**Walls and gates**
{{REFS:walls}}

## Questions for the designer

Nothing below is decided here.

1. **One clay colour:** walls, flat roofs, streets and sand are within 2-12 of each other. What tells the foot of a wall from the street, and a roof from the ground: light and shade only, a darker band at the foot of walls, paler roofs?
2. **The dome as the kit's mark:** it is the only roof that reads, and only moderately. Does it get a different tone (also question 6 of `reference-boards-w2.md`), and which buildings carry one?
3. **The cloth roof:** one unbroken roof or panels with gaps? How much of it hides when the player is under it? Does its shade stay on the ground while it is hidden?
4. **Cloth colours:** dusty red, ochre and indigo are muted versions of three warning hues, and a cloth roof is a large area. The guideline to avoid large areas of the warning hues is written for biome palettes. Does it apply to a city's cloth?
5. **Glazed tile:** may the kit have one muted colour accent (dome tips, door frames) that `city-layouts.md` does not list?
6. **Fights in a city:** do guard fights need warnings that read on sunlit clay and paving?
7. **Open points of the spec that change the kit list:** what the inn is for (R-11.6-05), how fast travel looks, whether mail and the auction need a building, and the stall cap of a main city (R-4.5-03).
8. **The review of the C2 plan** (10.4) is still open, and this board follows the plan as written.

## Not covered

- Interiors: floors, walls seen from above with the roof off, furniture.
- NPCs, guards and pack animals as models.
- The medieval kit (C3) and M6's scrap look.
- The biome dressing of the bazaar kit for M4, T4, T5 and T6.
- A to-scale plan of the whole city, which `city-layouts.md` leaves until the plan is accepted.
