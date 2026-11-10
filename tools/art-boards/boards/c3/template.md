# Reference board: C3, the classic medieval city

Status: **art reference, v2 (2026-11-10)**, updated with the designer's answers of 2026-11-10. A board for the third main city of the main map and for the medieval kit it defines. **Every colour, size and photo choice here is a proposal** for the designer to review. The drawing is `reference-board-c3.svg` (same folder). The biome around the city is on the Reed marsh board in `reference-boards-w3.md`, and the method is the one of `reference-board-c1.md`.

This file is **not the spec**. **The designer answered its questions on 2026-11-10.** Where a paragraph below calls a point open and cites a question, the answer under that question, at the end of the file, settles it. The colours themselves stay proposals.

## What is decided and what is a proposal

**Decided** (spec):
- **C3 is a classic medieval city** with walls and buildings, with its own building kit, one of three (R-2.3-09, E3, E6).
- **A main city has full walls and one gate per road**, and the gate is where the safe zone starts (R-2.3-08, R-6.3-07).
- **Cities sit on the 50 cm grid and face N, E, S or W** (R-11.6-06). **Only service buildings have interiors** (R-11.6-05).
- **Streets are 6 m wide, lanes 3 m, bridges 5 m** (R-11.6-03). The world is built at x1.25-1.5 scale around a 1.8 m character (R-11.6-01, R-11.6-02).
- **The art rules of 9.14:** toon style, a muted world, the fade and roofs off, outlines on characters and interactables only.

**Accepted by the designer on 2026-11-10**, with the answers at the end of this file (`city-layouts.md`):
- **The plan of C3:** four gates (north, west, east, south); two main streets that cross at the central plaza and split the city into four quarters; a moat about 8 m wide around the walls, fed by the marsh's river, with a 5 m timber-decked bridge at each of the five gates; the market in the north-west, the crafting quarter in the north-east, the raid gate and arena hall in the south-east with the tutorial gate beyond it, and the keep (scenery) and houses in the south-west; stone walls with towers at the gates.

**Still a proposal** (spec 10.4):
- **The kit's materials,** with the designer's answers worked in: grey stone with a darker base course at the foot of walls, timber frames, tiled roofs, battlements, a keep. Warm tile is the kit's mark from above.
- **The sizes** of the measurement sheet in `art-direction.md` and its 45-degree guideline for what the camera hides.

## Kit palette

The swatches are the colours as they should look on screen in the Reed marsh's soft light. They are not texture values. Saturation is the S of HSV.

{{PALETTE}}

- **The kit is grey with a warm top:** stone, cobbles and paving are at 4-13%, and the roofs at 37-41%.
- **Timber on plaster is the strongest contrast in the kit** (49). It is on the upper floors.
- `city-layouts.md` names the materials. The hex values, the cobbles and the banners are this board's proposal.
- **The wall base band and the timber bridge decks** are the designer's answers (questions 3 and 7).
- The tile and the stone are the colours used on the W3 boards for the towns that borrow this kit.

## Findings

The numbers are colour differences (CIE76 Delta E; higher is easier to tell apart), as on the other boards. They compare flat colours and show where to look first; they are not a pass or fail.

### The tile roof reads against everything beside it

`city-layouts.md` makes the roof the mark of each kit. The tile roof against what lies beside it:

{{ROOF}}

- **The lit side of a roof is 16-30 from every ground, wall and water beside it.** This is the first of the three kits whose roof stands out everywhere: the water kit's slate is 8-15 from water, and the bazaar kit's flat roof is 2-12 from sand, walls and streets.
- The shaded side is closest to the lane earth and to the marsh's wet earth (11).
- A pitched roof has a lit and a shaded side ({{LITSHADE}} apart), so from above it is two tones with a ridge between them. A flat roof and flat ground do not have that.
- In the plan drawing roofs cover about {{COVER}} of the ground. From above this city is warm roofs with grey lines between them.

### A red warning on the street is close to the roof's colour

The red warning (R-3.3-04), as the game blends it today over each of the city's grounds, against the roof and the banner:

{{RED}}

- **On a cobbled street the red warning is 8 from the tile roof.** The same warning is 25 from the bare cobbles under it, so it is three times closer to the roof beside it than to the ground it lies on. On the plaza paving it is 9 from the roof.
- For comparison, that warning on cobbles is {{REDCMP}}.
- **The hues are close:** the tile is at about 18 degrees of hue and the red warning at about 6; the yellow warning is at about 44. The tile is far less saturated than the warning colour itself (41% against 79%), but the warning is see-through, and over grey cobbles it lands at about the tile's strength.
- **What still tells them apart:** a roof has two tones, a ridge and a fixed place; a warning is flat, has its own shape and icon, and is on screen only while the attack runs (spec 9.3).
- **The fade makes it harder.** In a street the roof on the camera side is faded most of the time (see below). A faded tile roof is a see-through warm shape over the street, which is also what a red warning is. How the fade looks is Open (10.4), and this is a case for it (questions 1 and 2).
- A dusty red banner is 7 from the warning on cobbles. Banners are small.
- **The designer's answers** (questions 1 and 2): the tile stays, the warning itself changes on light ground, and the fade's look is chosen on a prototype of one city street.

### Grey stone on grey cobbles

{{EDGE}}

- **A stone wall and the street at its foot differ by 5**, and by 3 in the shade. The bazaar kit had the same finding in clay (6). The world has no outlines (R-9.14-05), so the foot of a wall needs something else: a darker base course, a kerb, a gutter, or a paler street (question 3).
- **The street inside a gate and the causeway outside it differ by 4.** The ground does not mark the gate; the moat and the bridge do.
- **The designer chose a darker base course at the foot of walls** (question 3). The proposed band is 27 from the wall above it and 24 from a sunlit street. Against cobbles in shade it is 12, so in shaded lanes it needs to be darker still.
- **The moat is clear:** 18 against the wall and 14 against the marsh's bank. That is less than the water's edge in C1 (21-34). The moat is the marsh's own water (3), as the plan says.
- **The plank deck of a gate bridge** is 19 from the moat and 9 from the street it leads to.

### Warnings on the city's ground

{{CHECK}}

- The warnings keep 53-98% on these grounds, more than on the pale stone of C1 (43-69%) or the sunlit clay of C2 (39-54% for red and yellow). Grey cobbles are a fair ground.
- The red warning is the exception, for the reason above: it is clear against the ground and close to the roofs.
- PvP is off in a normal city (R-6.3-01), so fights here are rare. Guards attack bad-honor players (question 9).

### The medieval kit beside the water kit

- Both kits are stone below and timber above.
- **The designer's answer** (question 5): the water kit's upper floors are timber boards without pale panels, and timber frames with plaster belong to the medieval kit. So the upper floors tell the two kits apart from the side.
- From above, the roof (tile against slate), the stone (grey against pale) and the water (a moat outside the walls against canals inside) tell C3 from C1.

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
- **A moat as wide as the wall is tall keeps the outside clear.** With an 8 m moat and a wall of 6-8 m, a player on the outer bank is never close enough for the wall to hide them. The designer set the moat at about 8 m (question 7), which is what the drawing uses.
- **Inside, the wall fades** for a player within 6-8 m of it when the camera looks in from outside.
- **On a gate bridge** the player is within reach of the gate tower (up to about 15 m), so the tower fades whenever the camera looks out from the city along the bridge.
- **Gate passages:** the arch over the player has to be a part that can fade or lift off (R-9.14-02).
- **The wall walk:** the top of the wall passes over the gate passage, which one walkable layer cannot hold. The battlements and anyone on them are scenery, as the designer confirmed (question 6).
- **The tutorial gate** is a fifth gate, beyond the south-east quarter. The designer gave it its own bridge over the moat (question 7).

**The keep**
- Scenery, as the plan says: it is not a service building, so it has no inside (R-11.6-05).
- The measurement sheet gives a landmark building up to about 15 m, and the designer keeps the keep within that (question 6). A keep that tall fades for a player within 15 m on its far side, which is much of the south-west quarter around it.
- The camera never shows its top from close by. On screen it is a wide stone base, and a roof or battlements when seen from across the quarter.

## Kit pieces

What the kit needs to build C3 as proposed. Sizes are from the measurement sheet in `art-direction.md`.

| Piece | Notes |
|---|---|
| House, 1 and 2 floors | A closed facade on all four sides. About 4 m per floor. Stone ground floor, timber frame with plaster above, tile roof. |
| Service building | A shell whose roof and upper floor can be hidden (R-9.14-02). Doors at least 2 m wide and 3 m tall, corridors at least 2.5 m. Used for the crafting halls, shops with NPCs, the guild hall, the raid gate and arena hall, and the inn. |
| Roofs | Pitched tile roofs with chimneys, dormers and ridge shapes. |
| City wall, battlements | Grey stone with a darker base course, about 6-8 m. It fades when it hides the player. The battlements are scenery. |
| Gate tower and passage | Up to about 15 m, at each of the four road gates; a smaller tutorial gate. The arch over the passage can fade or lift off. |
| Gate bridge | 5 m walkable width over the moat, with a timber deck; one at each of the five gates; railings are blockers outside the 5 m. Nothing walkable passes under it. |
| Moat | Unwalkable water around the walls, about 8 m wide, with a stone edge on the wall side and a bank on the marsh side. |
| Keep | Scenery in the south-west quarter. A closed shell that fades. |
| Plaza | The crossing of the two main streets, and the places for the respawn point, the outpost and the travel stone (fast travel). |
| Market | Awnings, counters for NPC vendors, and marked places for player stalls. |
| Street furniture | Signposts at crossings (R-9.14-07), hanging shop signs, banners, wells, carts, barrels. |

The designer's answers on the inn, fast travel, mail and the auction, and stalls are under question 10. They say what the inn is, and they add a travel stone, a mailbox and an auction house to this list.

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
- It is the kit with the widest spread of climates, from snow to a tropical beach. Snow lies on the roofs in patches, so the tile still shows (question 6 of the W3 boards).
- In the Ash woods and on the Storm cliffs the tile is the warmest colour in sight.

## Photo references

Links to free-licence files on Wikimedia Commons, each looked at before it was listed. No image is stored in the repository. They are references for materials, shapes and layout, not a target for detail: the game is stylized toon.

**The whole look: tile roofs inside a wall**
{{REFS:look}}

**Streets and squares**
{{REFS:streets}}

**Walls, gates and the moat**
{{REFS:walls}}

**Details**
{{REFS:details}}

## Questions for the designer, and the answers

The designer answered these on 2026-11-10. The questions stand as they were asked, so that the references to them in this file still hold. The full list is `reference-boards-questions.md`, and the "Q" numbers refer to it.

1. **Red warnings and tile roofs:** a red warning on cobbles is 8 from the roof's colour. Does the tile move away from red (browner, greyer, more ochre), or are the warning's shape and icon enough?
   - **Answer (Q6):** keep the tile. The warning itself changes on light ground (Q1).
2. **The fade on tile roofs** (Open in 10.4): a faded roof over a street is a see-through warm shape. Does that decide between dither and transparency, or should a faded roof lose its colour?
   - **Answer (Q9):** to be tested in the engine. One city street is built as a prototype, and the fade's look is chosen there.
3. **Grey stone on grey cobbles:** what tells the foot of a wall from the street (5)?
   - **Answer (Q20):** a darker band, a base course, at the foot of every wall.
4. **Roof direction:** eaves to the street (hides less) or gables to the street (the usual medieval look, hides more)? This is question 3 of the C1 board.
   - **Answer (Q11):** mixed by place: gables on squares and wide streets where there is room, eaves in lanes and 6 m streets.
5. **The medieval kit and the water kit:** both are stone with timber above. What besides the roof colour tells them apart from the side?
   - **Answer (Q21):** the water kit's upper floors are timber boards without pale panels. Timber frames with panels belong to the medieval kit.
6. **The wall walk and the keep:** are the battlements scenery only? How tall is the keep, given that the sheet's limit is about 15 m?
   - **Answer (Q14):** the battlements and anyone on them are scenery.
   - **Answer (Q24):** the keep stays within about 15 m.
7. **The moat and the bridges:** how wide is the moat (the drawing uses 8 m)? Are the bridge decks timber or stone? Does the tutorial gate get its own bridge?
   - **Answer (Q25):** about 8 m wide, timber decks, five bridges. The tutorial gate gets its own bridge.
8. **Banners:** heraldic banners are usually red, blue, yellow and green, the four warning hues. The board has dusty red and dusty blue. Is that acceptable in a city (question 4 of the C2 board)?
   - **Answer (Q5):** muted warning hues are fine on cloth and banners, at any size.
9. **Fights in a city:** do guard fights need warnings that read between tile roofs?
   - **Answer (Q2, Q1):** no. City ground is chosen for its look, and the warning itself changes on light ground.
10. **Open points of the spec that change the kit list:** what the inn is for (R-11.6-05), how fast travel looks, whether mail and the auction need a building, and the stall cap of a main city (R-4.5-03).
   - **Answer (Q27):** the inn is a "social place and we may add some food seller" (the designer's words). Fast travel is a travel stone on the plaza, the same object in all three cities. Mail and the auction get places in the city, a mailbox and an auction house; the spec leaves that point to the M4 plan, so the two still have to be brought together. The stall number is left to economy tuning.
11. **The review of the C3 plan** (10.4) is still open, and this board follows the plan as written.
   - **Answer (Q31):** accepted, with these answers.

## Not covered

- Interiors: floors, walls seen from above with the roof off, furniture.
- NPCs and guards as models.
- The biome dressing of the medieval kit for M5 and T7-T11.
- M6's scrap look and the Ember caldera (W4).
- A to-scale plan of the whole city, which `city-layouts.md` leaves until the plan is accepted.
