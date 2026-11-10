# Art reference boards: the questions in one list, with the designer's answers

Status: **answered, v2 (2026-11-10)**. The six reference boards end with 50 questions for the designer, and several repeat from board to board. This file merges them into **34 distinct questions**. **The designer answered all 34 on 2026-11-10**, and each answer stands under its question. Five points stay open after the answers; they are listed under "What the answers set in motion".

- The answers are the designer's decisions. The findings, the options and the "Changes" lines are the boards' author's and are not decisions.
- Nothing has been changed to match the answers yet: not the spec, not the design files, not the boards and not the game.

- The boards are `reference-boards-w1.md`, `reference-board-c1.md`, `reference-boards-w2.md`, `reference-board-c2.md`, `reference-boards-w3.md` and `reference-board-c3.md` (same folder). The numbers quoted here are theirs.
- "Asked on" gives the board and its question number, for example "W2 (3)".
- "Options" lists only what the boards themselves name. Another answer is always possible.
- The table at the end maps every board question to this list.
- This file is **not the spec**. An answer becomes a rule only when the orchestrator writes it into the spec.

## Where an answer reaches furthest

This order is a suggestion from the boards' author, not a ranking of importance.

1. **Q1, warnings on light ground.** It touches 8 of the 19 biomes and all three cities.
2. **Q9, the look of the fade.** In cities, forests, canyons and passes it is on screen most of the time.
3. **Q31, the reviews of the biome sheets and city plans.** Every board follows them as written, so a change there changes a board.
4. **Q3, the saturation limit.** It is one number that every palette is held against.
5. **Q20, wall against street.** It decides how two of the three city kits are built.

## Warnings and colour

### Q1. Warnings on light ground
- **Asked on:** W1 (2), W2 (1), W3 (1).
- **Found:** the warning alphas were tuned for the dark M1 ground. On 8 of the 19 biomes the main ground leaves red and yellow under half of today's difference: Red sands, Dry steppe, Dune isle, Palm coast, Steam springs, Rime coast, Snow taiga and Frost peaks. The lowest value is yellow on Palm coast's sand (31%). Red sands is a start biome.
- **Options:** the warnings change with the ground (a stronger alpha, a dark edge); light biomes keep their fighting places on darker ground; the shape and icon are enough.
- **Changes:** the palettes of the eight biomes, or the warning itself. No rule covers this today.
- **Answer (designer, 2026-11-10):** **Change the warning.** Warnings get a dark edge or a stronger fill on light ground. The biome palettes stay as proposed.

### Q2. Fights in a city
- **Asked on:** C1 (5), C2 (6), C3 (9).
- **Found:** PvP is off in a normal city (R-6.3-01), but guards attack bad-honor players. Warnings keep 43-69% on C1's pale stone and paving, 39-54% (red and yellow) on C2's sunlit clay, and 53-98% in C3, where the red warning is close to the roofs (Q6).
- **Options:** city ground is tuned for warnings too; or cities are judged on looks alone.
- **Changes:** how much Q1 binds the three kits.
- **Answer (designer, 2026-11-10):** **No, Q1 covers it.** The changed warning has to read on city ground too. City ground is chosen for its look.

### Q3. The saturation limit
- **Asked on:** W1 (1); part of W3 (5).
- **Found:** R-9.14-03 asks for a limit per biome, and the number is Open (10.4). The boards' averages run from 8% to 41%, and the most saturated swatch of a board from 23% (Heather moor) to 59% (the embers and braziers of W3). The warning colours are at 62-79%.
- **Options:** one number for every biome; a number per biome; a separate allowance for small glowing accents.
- **Changes:** which palettes pass. Amber woods (Q4) and the fires (Q7) are the edge cases.
- **Answer (designer, 2026-11-10):** **A number per biome.** Each biome gets its own limit, taken from its board. The numbers are not written yet.

### Q4. Amber woods
- **Asked on:** W1 (3).
- **Found:** it is the most saturated board (41% on average), and red keeps 52% on its rust leaf litter, the weakest red for a ground of that lightness.
- **Options:** keep it; mute it further.
- **Changes:** the Amber woods palette only.
- **Answer (designer, 2026-11-10):** **Keep it.** The Amber woods palette stays as proposed.

### Q5. Cloth and banners in the warning hues
- **Asked on:** C2 (4), C3 (8).
- **Found:** C2's cloth is dusty red, ochre and indigo (19-45%), and its cloth roof is a large area. C3 has dusty red and dusty blue banners (31-42%). The guideline to avoid large areas of the warning hues is written for biome palettes.
- **Options:** the guideline applies to cities too; muted cloth is acceptable; small banners are acceptable and large cloth is not.
- **Changes:** the accent colours of the bazaar and medieval kits.
- **Answer (designer, 2026-11-10):** **Yes, muted is fine.** Cloth and banners may use muted warning hues, at any size.

### Q6. Red warnings and tile roofs
- **Asked on:** C3 (1).
- **Found:** on cobbles the red warning is 8 from the tile roof's colour and 25 from the cobbles under it. The tile's hex is the boards' proposal.
- **Options:** the tile moves away from red (browner, greyer, more ochre); the warning's shape and icon are enough.
- **Changes:** the roof colour of the medieval kit, which seven settlements use.
- **Answer (designer, 2026-11-10):** **Keep the tile.** The warm tile stays as proposed.

### Q7. Fire and flashes
- **Asked on:** W3 (5).
- **Found:** embers (Ash woods) and braziers (Storm cliffs) are at 59%, between the red and the yellow warning in hue. The Storm cliffs' sheet has lightning flashes (visual only).
- **Options:** fires stay small points at that strength, or get weaker; a flash is limited in strength, or does not happen while a warning is on the ground.
- **Changes:** two accents and one effect.
- **Answer (designer, 2026-11-10):** **Small fires, soft flashes.** Fires stay at that strength as small points only. A flash is kept weak enough that warnings stay readable.

### Q8. Things in the air
- **Asked on:** W3 (3).
- **Found:** blown sand, mist, smoke and drifting ash, steam, and blowing snow lie between the camera and the ground in five W3 biomes. They are visual only (R-2.3-10), but they lie over warnings too.
- **Options:** warnings are drawn above these layers; the layers have a density limit.
- **Changes:** how those five biomes' air effects are built.
- **Answer (designer, 2026-11-10):** **Warnings on top.** Attack warnings are drawn above the air effects.

## The fade

### Q9. The look of the fade, and how often it shows
- **Asked on:** C1 (4), C3 (2).
- **Found:** when the camera looks across a lane, or across a 6 m street with 2-floor houses, the row on the camera side is always faded. A faded tile roof is a see-through warm shape over the street. The fade's look is Open (10.4: dither or transparency, and who it applies to).
- **Options:** dither or transparency; a faded roof keeps or loses its colour; streets are widened or houses lowered so that less fades.
- **Changes:** the Open fade item of 10.4, and street widths and house heights in all three kits.
- **Answer (designer, 2026-11-10):** **Test it in the engine.** One city street is built as a prototype, and dither or transparency is chosen there.

### Q10. The cloth roof of the bazaar street
- **Asked on:** C2 (3).
- **Found:** inside the covered street something is always hidden: the camera-side row when looking across, the cloth roof when looking along.
- **Options:** one unbroken roof or panels with gaps; the whole roof hides or only the part near the player; the cloth's shade stays on the ground while it is hidden, or not.
- **Changes:** the bazaar kit's cloth pieces.
- **Answer (designer, 2026-11-10):** **Panels, and the whole street hides.** The cloth roof is made of panels. All panels over the street hide while a player is under it, and the shade stays on the ground.

### Q11. Roof direction
- **Asked on:** C1 (3), C3 (4).
- **Found:** a roof with its eaves to the street, pitched at 45 degrees or less, hides nothing more than its eaves. A gable to the street hides as much as its peak is tall: 12 m for a 2-floor house 8 m deep.
- **Options:** eaves to the street; gables to the street (the look of harbour and medieval towns); a mix by district.
- **Changes:** the house pieces of the water and medieval kits.
- **Answer (designer, 2026-11-10):** **Mix by place.** Gables on squares and wide streets where there is room; eaves in lanes and 6 m streets.

### Q12. Canyon walls and the mountain pass
- **Asked on:** W2 (3), W3 (4).
- **Found:** a wall on the camera side hides the player within a distance equal to its height. In a canyon or pass narrower than its walls are tall, one wall is always in the way. A wall of terrain cannot fade unless it is built as pieces.
- **Options:** canyon and pass walls are built as pieces that fade; or floors are wider than the walls are tall.
- **Changes:** how Bone canyons and the pass of the Frost peaks are laid out.
- **Answer (designer, 2026-11-10):** **Wide floors.** Canyon floors and the pass are wider than their walls are tall. The walls stay plain terrain.

## One walkable layer and the landmarks

### Q13. Landmarks a player walks into
- **Asked on:** W2 (4), W3 (4).
- **Found:** the hollow tree of the Old forest and the stepped temple of the Fern jungle could have an inside. R-11.6-05 lists only service buildings as interiors.
- **Options:** they are walk-in places whose top lifts off like a roof; or they are closed shapes.
- **Changes:** two landmarks, and whether R-11.6-05 needs an exception.
- **Answer (designer, 2026-11-10):** **Walk-in, and the top lifts off.** A player can walk into the hollow tree and the stepped temple; the trunk or the temple roof hides like a building's roof.

### Q14. Things nothing walks on
- **Asked on:** W2 (3), C3 (6).
- **Found:** the fallen statue, the giant ribs over the canyon path and the wall walk of C3 sit above ground that is walked on, and a path that spirals up the giant mushroom would pass over itself. One layer cannot hold both.
- **Options:** nothing is walkable on top of any of them (the boards assume this); or one of them is redesigned so that nothing passes under it.
- **Changes:** four landmarks and the battlements.
- **Answer (designer, 2026-11-10):** **Yes, all scenery.** None of the four is walkable on top. The battlements and anyone on them are scenery.

### Q15. Steps
- **Asked on:** W3 (4).
- **Found:** a step under 3 m is not a cliff (R-11.6-04). The terraces of the Steam springs, the basalt columns of the Storm cliffs and the stepped temple are fields of steps.
- **Options:** each step is a ramp or a stair; the steps are taller and fewer, as cliffs with ramps between them; the stepped area is scenery.
- **Changes:** three landmarks.
- **Answer (designer, 2026-11-10):** **Low steps as stairs.** Many low steps, walkable as stairs or as ramps of up to 30 degrees.

### Q16. Reed beds and shallow water in the marsh
- **Asked on:** W3 (4).
- **Found:** real reeds are 2-3 m tall, which hides a character. The sheet does not say whether the marsh's shallow water is walkable; the pools of the Steam springs and the lake of Lake country are not.
- **Options:** reed beds are low, or stands that fade, or not walkable; shallow water is walkable or not.
- **Changes:** the ground of C3's start biome.
- **Answer (designer, 2026-11-10):** **Low reeds, water blocked.** Walkable reed beds are knee to waist high and never fade. Shallow water is unwalkable.

## Kits and roofs

### Q17. Slate roofs and water
- **Asked on:** C1 (1).
- **Found:** the water kit's slate roof is 8-15 from canal water and the sea.
- **Options:** the slate becomes lighter or warmer; the water changes; buildings always keep a quay or a pale edge between roof and water.
- **Changes:** the water kit's roof or C1's water.
- **Answer (designer, 2026-11-10):** **A pale edge always.** The colours stay. Every building keeps a quay or a pale stone edge between its roof and the water.

### Q18. One roof colour per kit, or biome dressing
- **Asked on:** W1 (4), W3 (6).
- **Found:** slate is 12 from the Heather moor's heather and 5 from the Rime coast's frozen shingle. `city-layouts.md` gives "snow on the roofs" as biome dressing, which covers the slate in the Rime coast and the tile in the Snow taiga and Frost peaks.
- **Options:** a kit keeps one roof colour everywhere; the biome dressing may change or cover it.
- **Changes:** whether the roof still marks the kit in M1, M3, M5 and T7.
- **Answer (designer, 2026-11-10):** **Partly covered.** Snow lies on roofs in patches, so the kit's roof colour still shows on every building.

### Q19. The pale dome
- **Asked on:** W2 (6), C2 (2).
- **Found:** the dome is the only roof of the bazaar kit that reads, and it is close to pale ground: 7 from Dune isle's sand, 13 from C2's plaza paving, 16 from the steppe's grass, 18 from the Red sands' sand.
- **Options:** the dome gets a different tone; something else marks the kit; only some buildings carry a dome.
- **Changes:** the bazaar kit's roof, in C2, M4, T4, T5 and T6.
- **Answer (designer, 2026-11-10):** **A different tone: whiter.** The dome becomes a lime-washed white, clearly lighter than sand and clay.

### Q20. Wall against street
- **Asked on:** C2 (1), C3 (3).
- **Found:** a clay wall and the street at its foot differ by 6 in C2; a stone wall and the cobbles by 5 in C3 (3 in the shade). C2's flat roofs are 2-12 from sand, walls and streets. The world has no outlines (R-9.14-05).
- **Options:** light and shade only; a darker band or base course at the foot of walls; a kerb or gutter; paler roofs or a paler street.
- **Changes:** the wall and street pieces of the bazaar and medieval kits.
- **Answer (designer, 2026-11-10):** **A dark band at the wall foot.** Every wall gets a darker base course or band where it meets the ground.

### Q21. Timber upper floors, and the medieval kit beside the water kit
- **Asked on:** C1 (2), C3 (5).
- **Found:** `city-layouts.md` gives the water kit "timber upper floors" and the medieval kit "timber frames". If both get pale panels between the timbers, their upper floors are nearly the same, and the roof, the stone and the water are what tells them apart.
- **Options:** the water kit's upper floors are timber only; both kits have frames with panels, and the roof colour is enough.
- **Changes:** the upper floors of the water kit.
- **Answer (designer, 2026-11-10):** **The water kit has planks only.** Its upper floors are timber boards without pale panels. Timber frames with panels belong to the medieval kit.

### Q22. A glazed tile accent in the bazaar kit
- **Asked on:** C2 (5).
- **Found:** the C2 board adds one muted teal accent (dome tips, door frames) that `city-layouts.md` does not list.
- **Options:** keep it; drop it.
- **Changes:** one accent.
- **Answer (designer, 2026-11-10):** **Keep it.** The bazaar kit has a muted teal glazed tile as its one colour accent.

### Q23. M2's kit
- **Asked on:** W2 (7). Open in 10.4.
- **Found:** in the Old forest a slate roof is 25 from the dark earth and a pale dome 60.
- **Options:** water kit; bazaar kit.
- **Changes:** the look of M2.
- **Answer (designer, 2026-11-10):** **The water kit.**

### Q24. The keep
- **Asked on:** C3 (6).
- **Found:** the plan makes the keep scenery. The measurement sheet gives a landmark building up to about 15 m, and a keep that tall fades for a player within 15 m on its far side.
- **Options:** the keep stays within about 15 m; it is taller and the sheet gets an exception.
- **Changes:** one building and its quarter.
- **Answer (designer, 2026-11-10):** **Within about 15 m.** The keep follows the sheet's limit.

### Q25. The moat and the gate bridges of C3
- **Asked on:** C3 (7).
- **Found:** the plan gives no moat width. A moat as wide as the wall is tall keeps the outer bank out of the wall's fade; the drawing uses 8 m. The tutorial gate is a fifth gate.
- **Options:** the moat's width; timber or stone bridge decks; a fifth bridge for the tutorial gate, or the gate moves.
- **Changes:** C3's wall, moat and gates.
- **Answer (designer, 2026-11-10):** **About 8 m wide, timber decks, five bridges.** The tutorial gate gets its own bridge.

### Q26. The three sea bridges
- **Asked on:** W3 (7).
- **Found:** the north bridge joins a water-kit city and a medieval-kit city, and the south-east bridge a bazaar-kit city and a medieval-kit town. No board has a bridge deck colour. The bridges are the map's ambush spots.
- **Options:** each bridge takes the kit of one end; the bridges get a look of their own.
- **Changes:** three bridges, and the ground a bridge fight happens on.
- **Answer (designer, 2026-11-10):** **A look of their own.** The three bridges share one neutral style (stone and timber), with a deck dark enough for warnings.

### Q27. Open points of the spec that change the kit lists
- **Asked on:** C1 (6), C2 (7), C3 (10).
- **Found:** four open points decide which pieces each kit needs: what the inn is for (R-11.6-05), how fast travel looks, whether mail and the auction need a building, and the stall cap of a main city (R-4.5-03).
- **Options:** none named; these are spec decisions.
- **Changes:** the piece list of all three kits.
- **Answer (designer, 2026-11-10):** **Settled here, in four parts.** The inn: "Social place and we may add some food seller" (the designer's words). Fast travel: a travel stone on the plaza, the same object in all three cities. Mail and the auction: places in the city, a mailbox object and an auction house. Stalls: no number now, left to economy tuning; the market is built so that rows of stall places can be added or removed.

## Roads and light

### Q28. Roads that are the same material as the ground
- **Asked on:** W2 (2), W3 (2).
- **Found:** R-9.14-07 asks for roads clearly drawn into the ground. The road is 8-15 from the ground in Red sands and Bone canyons, and 3-15 in Dune isle, Palm coast, Ash woods and Snow taiga. Dune isle and Snow taiga have dangerous roads.
- **Options:** edge stones, posts or poles, cairns, ruts, a darker centre line.
- **Changes:** the road pieces of six biomes.
- **Answer (designer, 2026-11-10):** **Posts or poles, and edge stones or cairns.**

### Q29. The Fungal hollow's light
- **Asked on:** W2 (5).
- **Found:** the sheet's light comes from glowing spores and not from a sun. R-9.14-04 asks for one fixed light per biome.
- **Options:** a dim ambient light plus many glowing props counts as the one light; or the biome gets a single light source.
- **Changes:** one light preset.
- **Answer (designer, 2026-11-10):** **Yes.** The biome's one preset is a dim ambient light, and the glowing props are part of the look.

### Q30. The light at the cliff between Palm coast and Snow taiga
- **Asked on:** W3 (8).
- **Found:** this border has no blend (R-2.3-11), and light presets blend over the same distance as the ground. A ramp up the cliff is 5-10 m long.
- **Options:** the light changes over the length of a ramp; it changes at a line.
- **Changes:** one border.
- **Answer (designer, 2026-11-10):** **Over the ramp.** The light blends over the length of each ramp, 5-10 m.

## Process

### Q31. The reviews that are still open
- **Asked on:** W1 (7), W2 (8), W3 (9), C1 (7), C2 (8), C3 (11).
- **Found:** the 19 biome sheets of these boards and the three city plans are proposals under review (10.4). Every board follows them as written.
- **Options:** accept a sheet or plan; change it.
- **Changes:** a changed sheet or plan changes its board.
- **Answer (designer, 2026-11-10):** **Accepted, with these answers.** The 19 sheets and the 3 city plans are accepted as written, except where an answer in this file changes them.

### Q32. Style references
- **Asked on:** W1 (5).
- **Found:** the design files name no game, film or artist as a style reference.
- **Options:** name references; keep the rules of 9.14 as the only measure.
- **Changes:** what the boards are held against.
- **Answer (designer, 2026-11-10):** **"I will share later"** (the designer's words). No reference is named yet.

### Q33. Pictures in the boards
- **Asked on:** W1 (6).
- **Found:** the boards link to free-licence photos on Wikimedia Commons and store none.
- **Options:** links only; store free-licence pictures with the boards, with credits, Git LFS and rows in `THIRD_PARTY_LICENSES.md`.
- **Changes:** how the boards are kept.
- **Answer (designer, 2026-11-10):** **Links only.**

### Q34. The script behind the tables
- **Asked on:** none; it comes from the review of the boards.
- **Found:** the script that computes the tables is not in the repository. `reference-boards-w1.md` has the exact method instead.
- **Options:** the written method is enough; the script is committed (PowerShell 5.1, outside game code), and where.
- **Changes:** whether a changed swatch can be recomputed by running a file.
- **Answer (designer, 2026-11-10):** **Commit it under tools/.**

## Every board question, mapped

| Board | Its questions, in order |
|---|---|
| W1 | 1 to Q3, 2 to Q1, 3 to Q4, 4 to Q18, 5 to Q32, 6 to Q33, 7 to Q31 |
| C1 | 1 to Q17, 2 to Q21, 3 to Q11, 4 to Q9, 5 to Q2, 6 to Q27, 7 to Q31 |
| W2 | 1 to Q1, 2 to Q28, 3 to Q12 and Q14, 4 to Q13, 5 to Q29, 6 to Q19, 7 to Q23, 8 to Q31 |
| C2 | 1 to Q20, 2 to Q19, 3 to Q10, 4 to Q5, 5 to Q22, 6 to Q2, 7 to Q27, 8 to Q31 |
| W3 | 1 to Q1, 2 to Q28, 3 to Q8, 4 to Q12, Q13, Q15 and Q16, 5 to Q3 and Q7, 6 to Q18, 7 to Q26, 8 to Q30, 9 to Q31 |
| C3 | 1 to Q6, 2 to Q9, 3 to Q20, 4 to Q11, 5 to Q21, 6 to Q14 and Q24, 7 to Q25, 8 to Q5, 9 to Q2, 10 to Q27, 11 to Q31 |

## What the answers set in motion

Nothing below is done by this file. It lists what each answer asks of someone.

**Changes to how the client draws (for the orchestrator to plan)**
- **Q1:** warnings get a dark edge or a stronger fill on light ground.
- **Q8:** attack warnings are drawn above air effects.
- **Q7:** lightning flashes get a strength limit.
- **Q9:** one city street is built as a prototype to choose the fade's look.

**Decisions for the spec (the orchestrator chooses the rule each belongs in)**
- **Q3:** a saturation number per biome (R-9.14-03).
- **Q13:** the hollow tree and the stepped temple are walk-in landmarks. R-11.6-05 lists only service buildings as interiors today.
- **Q12, Q15, Q16:** canyon floors and the pass are wider than their walls are tall; stepped landmarks are low stairs; reed beds are low and shallow marsh water is unwalkable.
- **Q28:** roads in sand, gravel, ash and snow are marked with posts or poles and with edge stones or cairns (R-9.14-07).
- **Q29, Q30:** the Fungal hollow's preset is ambient light plus glow (R-9.14-04); the light at the Palm coast cliff blends over the ramps (R-2.3-11).
- **Q23, Q24, Q25, Q26:** M2 uses the water kit; the keep stays within about 15 m; C3's moat is about 8 m wide with five timber-decked bridges; the three sea bridges share one style of their own.
- **Q27:** the inn is a social place that may get a food seller; fast travel is a travel stone; mail and the auction have places in the city. **The spec says the M4 plan decides mail and the auction (R-4.5-03, 10.4), so this answer and that plan have to be brought together.**
- **Q31:** the 19 sheets and the 3 city plans are accepted with these answers. The open list (10.4) has no pointer to the boards or to this file yet.
- **The decision log (13):** the answers of 2026-11-10.

**Changes to the design files and the boards (art reference work)**
- **`city-layouts.md`:** the water kit's upper floors are planks only (Q21); the bazaar kit gets a whiter dome and the glazed tile accent (Q19, Q22); walls in the bazaar and medieval kits get a dark band at the foot (Q20); roof direction is mixed by place (Q11); the cloth roof is in panels (Q10); snow lies in patches (Q18); a pale edge stays between roof and water (Q17).
- **The boards:** C1 drops its infill panel swatch; the dome swatch changes on the W2, C2 and W3 boards, and their roof tables are recomputed; C2 and C3 get a swatch for the wall-foot band; W2 names M2's kit; the questions at the end of each board point to this file.
- **Q34:** the scripts are committed under `tools/`.

**Still open after this round**
- **Q32:** the designer will share style references later.
- **Q3:** the limit number of each biome. Each board's strongest swatch is a starting point.
- **Q1:** dark edge or stronger fill, and from which ground on.
- **Q9:** dither or transparency, after the prototype.
- **Q27:** the stall numbers, with economy tuning.
