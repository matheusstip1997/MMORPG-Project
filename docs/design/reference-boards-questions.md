# Art reference boards: the questions in one list, with the designer's answers

Status: **answered, v2 (2026-11-10)**. The six reference boards end with 50 questions for the designer, and several repeat from board to board. This file merges them into **34 distinct questions**. **The designer answered all 34 on 2026-11-10**, and each answer stands under its question. Five points stay open after the answers; they are listed under "What the answers set in motion". **Seven more questions, Q35 to Q41, come from the W4 board; the designer answered them on 2026-11-10 as well.** **Twelve more, Q42 to Q53, come from the characters and monsters board, and are answered too.** **Three follow-up questions to Q53, Q54 to Q56, are answered as well, and so are two on the armour tier, Q57 and Q58, which replace the marks of Q44.** **Four more, Q59 to Q62, were answered on 2026-11-12 after the designer shared a picture of the Slayer; Q60 reopens the outline's form of Q42.** **Five more, Q63 to Q67, were answered later on 2026-11-12, after the Slayer was drawn as a 2D anime model sheet: they replace the detailed look of Q59, the first half of Q60 and the proportions of Q56 and Q62, and widen the medium example of Q57.**

- The answers are the designer's decisions. The findings, the options and the "Changes" lines are the boards' author's and are not decisions.
- `city-layouts.md` (decisions E8 to E15), the six boards and the scripts in `tools/art-boards` have been changed to match the answers, and so have the spec, the decision log and `biome-sheets.md` (decisions C6 to C14). **Not yet:** the answers that change how the client draws (Q1, Q7, Q8, Q9), places in the city for mail and the auction (part of Q27), and the game itself.

- The boards are `reference-boards-w1.md`, `reference-board-c1.md`, `reference-boards-w2.md`, `reference-board-c2.md`, `reference-boards-w3.md` and `reference-board-c3.md` (same folder), and later `reference-board-w4.md` and `reference-board-figures.md`. The numbers quoted here are theirs.
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
- **Answer (designer, 2026-11-10):** **"I will share later"** (the designer's words). No reference was named then; **the designer named four with Q53.**

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

## The W4 board: seven questions, answered

`reference-board-w4.md` (the Ember caldera and M6) was written after the first 34 answers. Its questions are new, and **the designer answered them on 2026-11-10 as well**.

### Q35. How strong and how large lava may be
- **Asked on:** W4 (1).
- **Found:** the lava's glow is at 75% saturation, among the warning colours (62-79%), and lava is about 14% of the screen as lines and pools. At the crater it would fill much of the screen. Q7 kept fires as small points.
- **Options:** lava keeps that strength as lines and pools; lava gets weaker; large lava areas such as the crater lake are limited.
- **Changes:** the caldera's palette and the crater.
- **Answer (designer, 2026-11-10):** **Keep the strength, limit the area.** Lava keeps its glow and stays thin lines and small pools. A large lava area such as the crater's lake is kept to a small part of the screen.

### Q36. A red warning beside lava
- **Asked on:** W4 (2).
- **Found:** on black rock the red warning is 11 from the lava's cooling crust and 49 from the rock under it. Q1 changes the warning on light ground; here the ground is dark.
- **Options:** the crust moves away from dark red (darker, browner); the warning gets an edge on dark ground too.
- **Changes:** one swatch, or the scope of Q1.
- **Answer (designer, 2026-11-10):** **Move the crust.** The cooling crust becomes darker and browner, away from the red warning. Warnings stay as they are on dark ground.

### Q37. The caldera's light from below
- **Asked on:** W4 (3).
- **Found:** the sheet lights the biome from the lava and not from a sky. Q29 accepted a dim ambient light plus glowing props as the Fungal hollow's one light.
- **Options:** the same reading holds for the caldera; the caldera gets a single light source.
- **Changes:** one light preset (R-9.14-04).
- **Answer (designer, 2026-11-10):** **Yes.** The caldera's one preset is a dim ambient light, and the lava's glow is part of the look.

### Q38. Cover and the fade in M6
- **Asked on:** W4 (4).
- **Found:** the fade removes what hides the player's own character (R-9.14-02), and who else it applies to is Open (10.4). As written, a wall hides another player from me while their own camera sees through it. In M6 PvP is always on and the layout is built on cover.
- **Options:** cover works that way; the fade also shows other players behind walls; M6 is part of the fade prototype of Q9.
- **Changes:** how cover works in the lawless city, and the open fade item.
- **Answer (designer, 2026-11-10):** **Test it with the fade.** M6's case joins the fade prototype of Q9: one street is built and tried with two players before anything is fixed.

### Q39. The foot of a scrap wall
- **Asked on:** W4 (5).
- **Found:** a scrap wall is 8-14 from the street at its foot. Q20 gave the bazaar and medieval kits a dark band at the foot of walls.
- **Options:** the band applies to the scrap look too; scrap needs none.
- **Changes:** M6's wall pieces.
- **Answer (designer, 2026-11-10):** **Yes, a dark band.** Scrap walls also get a darker band where they meet the ground.

### Q40. The crater
- **Asked on:** W4 (6).
- **Found:** the camera never shows the horizon, so the volcano is never seen as a mountain. On screen the crater is a rim, which is a cliff, with a lake of lava below.
- **Options:** a rim with a lava lake seen from above; players go down into the crater; something else.
- **Changes:** the caldera's landmark.
- **Answer (designer, 2026-11-10):** **A rim, with the lava seen in part.** Players walk the crater's rim, which is a cliff. The lava lake lies below and only its edge shows on screen. Nobody goes down.

### Q41. The reviews behind the W4 board
- **Asked on:** W4 (7).
- **Found:** the Ember caldera's sheet was not among the 19 accepted with Q31, M6's layout is a proposal, and M6's services are Open (10.4). The board follows them as written.
- **Options:** accept the sheet and the layout; change them.
- **Changes:** a changed sheet or layout changes the board.
- **Answer (designer, 2026-11-10):** **Both are accepted, with these answers:** the Ember caldera's sheet and M6's layout. M6's services stay open.

## The characters and monsters board: twelve questions, answered

`reference-board-figures.md` (how large a figure is on screen, its black shape, and outlines and figure colours on the 20 grounds) was written after the W4 answers. Its questions are new, and **the designer answered them on 2026-11-10 as well**. The answers are decisions A9 to A19 in `art-direction.md`.

### Q42. The outline on dark and on light ground
- **Asked on:** Figures (1).
- **Found:** no single outline colour is clear on all 20 main grounds. A dark outline is 7-19 from the five darkest (Ember caldera, Fungal hollow, Old forest, Storm cliffs, Fern jungle). A light one is 5-6 from snow, 13 from the Steam springs' terraces and 19-22 from the pale sands.
- **Options:** an outline that changes with the ground; a two-tone outline, dark with a thin light rim; a dark outline plus a contact shadow; decide in a prototype.
- **Changes:** how every figure and interactable is drawn.
- **Answer (designer, 2026-11-10; its form on characters was reopened by Q60):** **A two-tone outline.** A dark line with a thin light rim outside it, the same on every figure on every ground, so one of the two always reads. Its two tones and widths are still to be tuned.

### Q43. Hostility or rarity in the outline
- **Asked on:** Figures (2). It was Open in 10.4.
- **Found:** a coloured outline has the same contrast problem on every ground, and a red or yellow one would sit beside the warning hues.
- **Options:** the outline stays neutral; hostility in the outline; rarity in the outline of loot only; leave it open.
- **Changes:** the outline, and where hostility and rarity are shown.
- **Answer (designer, 2026-11-10):** **The outline stays neutral.** It only separates a figure from the ground. Hostility and rarity are shown elsewhere (name plates, markers, loot glow).

### Q44. The armour tier in the silhouette
- **Asked on:** Figures (3).
- **Found:** the tier decides what a player can parry (R-3.3-04). The board's drawing marks it by shoulder width alone: steps of about 11 and 15 px at 1080p, 8 and 10 px at 720p, and about two thirds of that near the top edge of the screen.
- **Options:** shoulder width plus headgear; shoulder width alone; not in the silhouette; decide with sketches.
- **Changes:** what every armour set has to keep.
- **Answer (designer, 2026-11-10; its marks were replaced by Q57 and Q58):** **Shoulders plus headgear.** The tier reads from the shoulder width and from a second mark on the head, which the camera shows best. Examples, not designs: a full helmet for heavy, a hood or cap for medium, a bare head or a hat for light.

### Q45. A Mage and an Assist with a staff
- **Asked on:** Figures (4).
- **Found:** both classes can carry a staff (`skill-trees.json`). With the same weapon their black shapes differ by the armour alone, about 11 px of shoulder width.
- **Options:** a staff shape and clothing per class; staff shape only; clothing only; the armour is enough.
- **Changes:** the staffs and the class garments.
- **Answer (designer, 2026-11-10):** **Staff shape and clothing.** Each of the two classes gets its own staff shape and its own garment or headgear shape.

### Q46. How much larger than life
- **Asked on:** Figures (5).
- **Found:** R-9.14-06 says heads, hands and weapons are "slightly" larger than real life. At real size a hand is about 10 px and a sword blade about 7 px wide at 1080p.
- **Options:** weapons about 1.5 times and hands about 1.25 times; everything about 1.25 times; everything about 1.5 times; decide with concept art.
- **Changes:** every character and weapon model, and the word "slightly" in the rule.
- **Answer (designer, 2026-11-10):** **Weapons about 1.5 times real size, heads and hands about 1.25 times.** A blade is then about 10 px wide and a hand about 12 px. For the head, 6 heads tall already gave a 30 cm head, about 1.25 times a real one. **Since Q64 (2026-11-12) the head part holds again as first answered.** In between, **the head part was replaced by Q56:** with about 4.5 to 5 heads tall the head is about 36-40 cm. Whether a long weapon (a staff, a bow) also grows in length is left to the concept art.

### Q47. Monster heights
- **Asked on:** Figures (6).
- **Found:** the data has only a body radius, and the size classes are defined by it (R-2.2-08). No height is defined.
- **Options:** a height range per size class; a height per monster in the data; no rule; leave it open.
- **Changes:** how tall monsters are modelled.
- **Answer (designer, 2026-11-10):** **A height range per size class,** as a guide for artists. The numbers are still to come, as a proposal for the designer.

### Q48. Families in several biomes
- **Asked on:** Figures (7).
- **Found:** a family that lives in several biomes cannot keep one tone. Beasts live in 12 biomes, and fur brown is 4 from the Reed marsh's wet earth and 54 from snow.
- **Options:** a local variant per biome with a family marking; a tone per biome; one family tone; decide with the monster designs.
- **Changes:** how many variants a family needs.
- **Answer (designer, 2026-11-10):** **A local variant with a family marking.** Each biome gets its own variant (a snow wolf, a marsh snake) in a tone that contrasts with that biome's ground, and the family keeps one marking or shape that is the same everywhere.

### Q49. Elementals
- **Asked on:** Figures (8).
- **Found:** elementals are made of their biome's material (ice, sand, steam, storm), so on purpose they have the colour of their ground. They are the signature family of four biomes and a shared family of two more.
- **Options:** a core in another colour; the outline and a glow are enough; test in the engine.
- **Changes:** the elementals' designs.
- **Answer (designer, 2026-11-10):** **A core in another colour.** Besides the outline and a glow, an elemental has a core, eyes or cracks in a colour that contrasts with the ground, so there is always a point to aim at.

### Q50. Element colours
- **Asked on:** Figures (9).
- **Found:** the ten elements have no colours in the data. Fire, lightning, ice or water, and poison naturally take red-orange, yellow, blue and green, which are the four warning hues.
- **Options:** natural hues, kept apart from warnings by form; shifted hues; decide with the effects work.
- **Changes:** spell effects and elemental monsters.
- **Answer (designer, 2026-11-10):** **Natural hues, a different form.** Element effects keep their natural colours. Warnings stay apart by form: flat shapes on the ground with a clear edge, drawn above effects in the air (Q8), while element effects are in the air, small and soft (Q7). The colours themselves are not chosen yet.

### Q51. The disguise robe
- **Asked on:** Figures (10).
- **Found:** the robe hides the name, the honor and the red-name flag (R-6.2-01), and an observer who fails the Detect check sees "a generic hooded figure" (R-6.2-03). Whether the shape still shows the class and the armour tier is not said.
- **Options:** it hides class and tier; it hides the class and the tier shows; it hides nothing of the shape; leave it open.
- **Changes:** what other players can read from a robed figure, including what it can parry.
- **Answer (designer, 2026-11-10):** **It hides the class and the armour tier.** One robe shape for everyone covers the body, so neither can be read from the shape. The option as it was put also said that a weapon shows only when it is drawn; the spec has no rule about drawing a weapon, and attacking removes the robe (R-6.2-04), so that part is not taken as a decision. **Not asked:** what an observer sees who passes the Detect check, or who is in the wearer's party or guild.

### Q52. The smallest screen
- **Asked on:** Figures (11).
- **Found:** a character is 104 px tall at 1080p, about 77 px on a Steam Deck's screen (800p) and 69 px at 720p.
- **Options:** 1080p is the target and smaller is supported; 720p is a target; 1080p is the minimum; leave it open.
- **Changes:** how large and simple the marks of class, weapon and tier must be.
- **Answer (designer, 2026-11-10):** **1080p is the target, smaller screens are supported.** Shapes and marks are tuned for 1080p. 720p and the Steam Deck's screen must stay playable, but marks are not designed for a 69 px character.

### Q53. Style references
- **Asked on:** Figures (12). It repeats Q32.
- **Found:** Q32 was answered "I will share later", so the boards name no reference.
- **Options:** name them now; later.
- **Changes:** what the boards and the art are held against.
- **Answer (designer, 2026-11-10):** **"Grand Chase Mobile, Tree of Savior, Ragnarook, Final Fantasy. Are good one for the reference"** (the designer's words; "Ragnarook" is read here as Ragnarok). They are references for the look only: nothing from them is used or copied. Which Final Fantasy, what the references are for, and how they go with the proportions were asked afterwards: Q54 to Q56.

## After Q53: three follow-up questions, answered

Q53 left three points open. They were asked in the art reference session, not on a board, and **the designer answered them on 2026-11-10**. The proportions are decision A20 in `art-direction.md`.

### Q54. Which Final Fantasy
- **Asked on:** follow-up to Q53.
- **Found:** the series looks very different from game to game.
- **Options:** Final Fantasy XIV; the Tactics and Ivalice look; the classic jobs in general; the pixel and chibi games.
- **Changes:** what "Final Fantasy" stands for among the references.
- **Answer (designer, 2026-11-10):** **Final Fantasy XIV.**

### Q55. What the references are for
- **Asked on:** follow-up to Q53.
- **Found:** Q53 named four references without saying what each is a reference for.
- **Options (several could be chosen):** characters; class costumes and weapons; monsters; the world, its colours and effects.
- **Changes:** which parts of the art are held against the references.
- **Answer (designer, 2026-11-10):** **All four:** characters, class costumes and weapons, monsters, and the world, its colours and effects. The answer is for the four references together; which reference leads for which part was not asked.

### Q56. The proportions beside the references
- **Asked on:** follow-up to Q53.
- **Found:** A7 and R-9.14-06 said about 6 heads tall. As far as the boards' author knows, Ragnarok, Tree of Savior and Grand Chase Mobile draw their characters much shorter, about 3 to 4 heads, with large heads; Final Fantasy XIV draws them tall.
- **Options:** keep 6 heads; in between, about 4.5 to 5; short, about 3 to 4; decide with concept sketches.
- **Changes:** every character model, R-9.14-06, the head size in R-11.6-01, and the figures board.
- **Answer (designer, 2026-11-10; replaced by Q64 on 2026-11-12):** **In between: about 4.5 to 5 heads tall.** The head is then about 36-40 cm on the 1.8 m character, 30-33 px on screen at 1080p instead of 25. The 1.8 m height and the 40 cm body radius stay as they are. **This replaces the head part of Q46:** the head follows these proportions, and "about 1.25 times real size" holds for hands only (the designer confirmed this with the plan).

## After a concept sketch: two questions, answered

On 2026-11-10 the designer looked at a concept sketch of the Slayer class in an anime style, drawn for the four style references and the proportions of Q56, and **approved its direction**. The sketch is a scratch drawing and is not in the repository. It raised two points about the armour tier, and **the designer answered both the same day**. The answers are decision A21 in `art-direction.md`.

### Q57. The armour tier on a slim body
- **Asked on:** the Slayer concept sketch. It reopens Q44.
- **Found:** with about 4.5 heads tall and the look of the references, the body stays slim: the sketch's shoulders are about 0.47 m across, against the 0.60 m the figures board drew for medium armour. So the tier does not read from the width of the body, as Q44 assumed.
- **Options:** the tier reads from the armour pieces; heavier tiers keep a visibly broader body; decide after sketches of the four root classes.
- **Changes:** what every armour set has to keep, and R-9.14-06.
- **Answer (designer, 2026-11-10; its medium example was widened by Q65 on 2026-11-12):** **By the armour pieces.** Bodies stay slim. The tier reads from how much plate there is: big shoulder plates and a helmet for heavy, one plate and bracers for medium, cloth only for light. **This replaces the "shoulder width" of Q44.**

### Q58. Headgear and the face
- **Asked on:** the Slayer concept sketch. It reopens Q44.
- **Found:** Q44's examples gave medium armour a hood or a cap. The sketch used a headband with long ties, so that the face and the hair stay visible, which the anime look depends on. From the game's camera a headband is a weaker mark than a hood.
- **Options:** faces stay visible; hoods and helmets stay as decided; decide after sketches of the four root classes.
- **Changes:** the head marks of the tiers, and R-9.14-06.
- **Answer (designer, 2026-11-10):** **Faces stay visible.** Head marks are light: headbands, circlets, ties, small hats, open hoods. Only heavy armour may get a full helmet. **This replaces the headgear examples of Q44.**

## The Slayer picture: four questions, answered

On 2026-11-12 the designer shared a picture of the Slayer, made with an image tool from the concept sketch, as **what the art should be**: a detailed anime render with believable materials and cinematic light, shown from the front, from the back and in an action pose. The picture is not in the repository. It differed from four earlier decisions, and **the designer answered each point the same day**. The answers are decisions A22 and A23 in `art-direction.md`; the review of the picture and the briefs for the next classes are in `class-art-style.md`.

### Q59. What the picture is the target for
- **Asked on:** the Slayer picture.
- **Found:** the picture is a detailed, semi-realistic anime render with cinematic light. A1 and R-9.14-01 describe a stylized toon game with cel shading and simple colour areas for the world, and characters with an anime look.
- **Options:** concept and promotional art only; the in-game look too; designs now, the game's look decided with the first model.
- **Changes:** how characters are modelled and lit in the game, and R-9.14-01.
- **Answer (designer, 2026-11-12; replaced by Q63 later the same day):** **The in-game look too.** Characters in the game should look like the picture: detailed materials and realistic light.

### Q60. The outline
- **Asked on:** the Slayer picture. It reopens Q42.
- **Found:** the picture has no outline. The figures board measured that a dark coat disappears on dark ground and white hair on snow, which led to the two-tone outline of Q42.
- **Options:** illustrations have no outline and characters keep an edge for play; keep the two-tone outline as decided; no outline at all.
- **Changes:** R-9.14-05, and how a figure separates from the ground.
- **Answer (designer, 2026-11-12; its first sentence was replaced by Q66 later the same day):** **Keep an edge for play.** Illustrations have no outline. In the game, characters keep a thin edge that separates them from the ground, an outline or a rim light, tuned when the first model is in the engine. **This reopens the form that Q42 fixed:** the two-tone outline is now one candidate.

### Q61. The world
- **Asked on:** the Slayer picture.
- **Found:** the picture's background is detailed and strongly coloured. The rules keep the world muted, with a saturation limit per biome, so that gameplay colours stand out (R-9.14-03).
- **Options:** the detailed look for characters only; for the world too; decide later.
- **Changes:** whether the biome and city boards stay valid.
- **Answer (designer, 2026-11-12; the detailed look was replaced by Q63 and Q67 later the same day; the world part stands):** **Characters only.** Characters, monsters and gear get the detailed look. The world stays muted with its saturation limits, and the biome boards stay valid.

### Q62. The proportions in the picture
- **Asked on:** the Slayer picture.
- **Found:** the figure in the picture is about 6 heads tall. Q56 decided about 4.5 to 5.
- **Options:** follow the picture; keep 4.5 to 5; about 6 for illustrations and 4.5 to 5 in the game.
- **Changes:** A20 and R-9.14-06.
- **Answer (designer, 2026-11-12; replaced by Q64 later the same day):** **Keep 4.5 to 5.** The picture's style is right, but its body is too tall. Nothing changes in the rules.

## The Slayer model sheet: five questions, answered

Later on 2026-11-12 the Slayer was drawn again as a **2D anime model sheet**: clean dark line art, flat cel shading with simple shadows, and a front and a back view on a plain ground. It was made with a local image tool (ComfyUI with the FLUX.2 klein 4B model) from the designer's picture. It was a scratch picture then and is stored since as `class-sheets/slayer.png`. The designer preferred it to the detailed picture: **"I liked this one more"**. It differed from the decisions of the same morning, and **the designer answered each point**. The answers are decisions A24 to A26 in `art-direction.md`.

### Q63. What the model sheet is the target for
- **Asked on:** the Slayer model sheet. It replaces Q59.
- **Found:** the sheet is a flat 2D anime drawing with line art and cel shading. Q59 had made a detailed render with realistic light the look of the game.
- **Options:** the game's look too; the look of the class sheets and illustrations only.
- **Changes:** A22 and R-9.14-01.
- **Answer (designer, 2026-11-12):** **The game's look too.** Characters in the game should look like the sheet: an anime drawing with clear lines and flat shading. The detailed look of Q59 is withdrawn.

### Q64. The proportions on the model sheet
- **Asked on:** the Slayer model sheet. It replaces Q56 and Q62.
- **Found:** the figure on the sheet is about 6 heads tall. Q56 decided about 4.5 to 5, and Q62 kept that against the picture.
- **Options:** about 6 heads; about 4.5 to 5. The designer asked to see both first, and the Slayer was drawn at about 4.5 heads in the same style for the comparison. A second question asked whether the answer holds in the game or on the sheets only.
- **Changes:** A20, R-9.14-06 and the head size in R-11.6-01.
- **Answer (designer, 2026-11-12):** **About 6 heads, in the game too.** Sheets and game models match. The answer of Q56 is withdrawn: the head is about 30 cm on the 1.8 m character again, as first decided on 2026-11-09.

### Q65. The shoulder plates of medium armour
- **Asked on:** the Slayer model sheet.
- **Found:** the Slayer wears medium armour and carries a small plate on each shoulder. Q57 named "one plate and bracers" as the medium example, and the image tool does not draw a single plate reliably.
- **Options:** two small plates are fine; keep one plate.
- **Changes:** the medium example of A21 and R-9.14-06.
- **Answer (designer, 2026-11-12):** **Two small plates are fine.** Heavy armour stays apart by its big shoulder plates and its helmet (Q57, Q58).

### Q66. The outline on the model sheet
- **Asked on:** the Slayer model sheet. It replaces the first sentence of Q60.
- **Found:** the sheet has dark line art. Q60 said that illustrations have no outline, and left the form of the edge in the game to the engine.
- **Options:** line art in pictures only, with the edge in the game still tuned in the engine; a dark outline in the game too.
- **Changes:** R-9.14-05.
- **Answer (designer, 2026-11-12):** **Line art in pictures only.** Illustrations and class sheets are drawn with dark line art. The form of the edge in the game stays open until the first model is in the engine, as Q60 left it.

### Q67. Monsters and gear
- **Asked on:** the Slayer model sheet.
- **Found:** Q61 gave the detailed look to characters, monsters and gear. The sheet shows a character only.
- **Options:** characters, monsters and gear; characters only, with monsters and gear left open.
- **Changes:** what A24 covers.
- **Answer (designer, 2026-11-12):** **Characters, monsters and gear.** The same set as before. The world stays muted with clear shapes, as Q61 decided.

## Every board question, mapped

| Board | Its questions, in order |
|---|---|
| W1 | 1 to Q3, 2 to Q1, 3 to Q4, 4 to Q18, 5 to Q32, 6 to Q33, 7 to Q31 |
| C1 | 1 to Q17, 2 to Q21, 3 to Q11, 4 to Q9, 5 to Q2, 6 to Q27, 7 to Q31 |
| W2 | 1 to Q1, 2 to Q28, 3 to Q12 and Q14, 4 to Q13, 5 to Q29, 6 to Q19, 7 to Q23, 8 to Q31 |
| C2 | 1 to Q20, 2 to Q19, 3 to Q10, 4 to Q5, 5 to Q22, 6 to Q2, 7 to Q27, 8 to Q31 |
| W3 | 1 to Q1, 2 to Q28, 3 to Q8, 4 to Q12, Q13, Q15 and Q16, 5 to Q3 and Q7, 6 to Q18, 7 to Q26, 8 to Q30, 9 to Q31 |
| C3 | 1 to Q6, 2 to Q9, 3 to Q20, 4 to Q11, 5 to Q21, 6 to Q14 and Q24, 7 to Q25, 8 to Q5, 9 to Q2, 10 to Q27, 11 to Q31 |
| W4 | 1 to Q35, 2 to Q36, 3 to Q37, 4 to Q38, 5 to Q39, 6 to Q40, 7 to Q41 |
| Figures | 1 to Q42, 2 to Q43, 3 to Q44, 4 to Q45, 5 to Q46, 6 to Q47, 7 to Q48, 8 to Q49, 9 to Q50, 10 to Q51, 11 to Q52, 12 to Q53 |
| Follow-up to Q53 (asked in the session, on no board) | Q54, Q55, Q56 |
| The Slayer concept sketch (not in the repository) | Q57, Q58 |
| The designer's Slayer picture of 2026-11-12 (not in the repository) | Q59, Q60, Q61, Q62 |
| The Slayer model sheet of 2026-11-12 (`class-sheets/slayer.png`) | Q63, Q64, Q65, Q66, Q67 |

## What the answers set in motion

This section lists what each answer asks of someone, and whether it is done.

**Changes to how the client draws (for the orchestrator to plan; not done, and not rules yet)**
- **Q1:** warnings get a dark edge or a stronger fill on light ground.
- **Q8:** attack warnings are drawn above air effects.
- **Q7:** lightning flashes get a strength limit.
- **Q9:** one city street is built as a prototype to choose the fade's look.

**Decisions for the spec (done on 2026-11-10, except mail and the auction)**
- **Q3, Q5:** a saturation number per biome, and muted warning hues on cloth and banners (R-9.14-03).
- **Q13:** the hollow tree and the stepped temple are walk-in landmarks (R-2.3-15, R-11.6-05).
- **Q12, Q14, Q15, Q16:** canyon floors and the pass are wider than their walls are tall; nothing is walkable on top of the statue, the ribs, the mushroom or the wall walk; stepped landmarks are low steps; reed beds are low and shallow marsh water is unwalkable (R-2.3-15).
- **Q28:** roads in sand, gravel, ash and snow are marked with posts or poles and with edge stones or cairns (R-9.14-07).
- **Q29, Q30:** the Fungal hollow's preset is ambient light plus glow (R-9.14-04); the light at the Palm coast cliff blends over the ramps (R-2.3-11).
- **Q10, Q11, Q17 to Q26:** the kit and city answers (R-2.3-14).
- **Q27:** the inn is a social place that may get a food seller, and fast travel is a travel stone (R-2.3-07, R-11.6-05). **Not in the spec yet:** places in the city for mail and the auction. The spec says the M4 plan decides (R-4.5-03, 10.4), so this answer and that plan still have to be brought together.
- **Q31:** the 19 sheets and the 3 city plans are accepted with these answers (R-2.3-14, R-2.3-15). The open list (10.4) points to the boards and to this file.
- **The decision log (13):** one row dated 2026-11-10.
- **Q31, in two more rules:** R-3.4-09 and R-14.2-08 say that each biome's main elements and families stand as written on the accepted sheets. That was 19 sheets with these answers, and is all 20 since Q41.

**Changes to the design files and the boards (done with the answers)**
- **`city-layouts.md`:** the water kit's upper floors are planks only (Q21); the bazaar kit gets a whiter dome and the glazed tile accent (Q19, Q22); walls in the bazaar and medieval kits get a dark band at the foot (Q20); roof direction is mixed by place (Q11); the cloth roof is in panels (Q10); snow lies in patches (Q18); a pale edge stays between roof and water (Q17).
- **The boards:** C1 drops its infill panel swatch; the dome swatch changes on the W2, C2 and W3 boards, and their roof tables are recomputed; C2 and C3 get a swatch for the wall-foot band; W2 names M2's kit; the questions at the end of each board point to this file.
- **Q34:** the scripts are committed under `tools/art-boards`.
- **`biome-sheets.md`:** 19 sheets are marked as accepted, and the answers that change a sheet (Q12 to Q16, Q28, Q29, Q30) are worked in as decisions C6 to C14.

**Still open after this round**
- **Q32:** the designer will share style references later. (Named since: Q53.)
- **Q3:** the limit number of each biome. Each board's strongest swatch is a starting point.
- **Q1:** dark edge or stronger fill, and from which ground on.
- **Q9:** dither or transparency, after the prototype.
- **Q27:** the stall numbers, with economy tuning.
- **Q38:** how cover works in M6, after the fade prototype.
- **M6's services** (10.4), which Q41 leaves open.

**What the W4 answers set in motion (Q35 to Q41)**
- **The W4 board (done):** the lava's cooling crust is a darker, browner `#5A3226`, and its tables are recomputed (Q36); scrap walls get a band at the foot (Q39); the board carries each answer under its question.
- **`biome-sheets.md` (done):** the Ember caldera's sheet is accepted, so all 20 are (Q41); lava keeps its strength and stays thin lines and small pools (Q35); the caldera's light is ambient plus glow (Q37); the crater is a rim with the lava seen in part (Q40). Decisions C15 to C18.
- **`city-layouts.md` (done):** M6's layout is accepted (Q41) and scrap walls get the dark band (Q39). Decisions E16 and E17.
- **The spec (done):** it holds these answers in R-2.3-14, R-2.3-15, R-9.14-03, R-9.14-04, R-3.4-09, R-14.2-08, 10.4 and a second decision log row dated 2026-11-10.
- **Not done, for the orchestrator to plan:** M6's case in the fade prototype (Q38).

**What the figures answers set in motion (Q42 to Q53)**
- **The figures board (done):** each answer stands under its question. Its tables and its drawing are unchanged: they show what was found before the answers.
- **`art-direction.md` (done):** decisions A9 to A19, and its open list.
- **The spec:** the answers go into R-9.14-03, R-9.14-05, R-9.14-06, R-2.2-08, R-14.2-08, R-6.2-01, 10.4 and a decision log row dated 2026-11-10, in the spec change made together with this one.
- **Still open after these answers:**
  - **Q42:** the two tones and the widths of the outline.
  - **Q47:** the height numbers of each size class.
  - **Q50:** the colour of each element.
  - **Q51:** what an observer sees who passes the Detect check, or who is in the wearer's party or guild.
  - **Q53:** answered since by Q54 to Q56 (Final Fantasy XIV; references for characters, costumes and weapons, monsters and the world; about 4.5 to 5 heads tall, replaced by Q64 on 2026-11-12).
- **Not done, for the orchestrator to plan:** the two-tone outline in the client (Q42), and anything in the client that the 1080p target (Q52) asks for.

**What the follow-up answers set in motion (Q54 to Q56)**
- **`art-direction.md` (done):** decision A20, about 4.5 to 5 heads tall, which replaces the 6 heads of A7 and the head size of B1; the style references with what they are for.
- **The figures board (done):** its head row and its silhouettes use a 38 cm head; the text names the references and the new proportions.
- **The spec:** R-9.14-06, the head size in R-11.6-01, 10.4 and a decision log row dated 2026-11-10, in the spec change made together with this one.
- **Still open:** which reference leads for which part of the art (Q55).
- **Replaced on 2026-11-12:** the proportions of Q56, by Q64 (see the model sheet answers below). The figures board uses the 30 cm head again.

**What the sketch answers set in motion (Q57, Q58)**
- **`art-direction.md` (done):** decision A21, which replaces A11: the armour tier reads from the armour pieces on a slim body, and faces stay visible.
- **The figures board (done):** a note under its question 3 and in its findings on the armour tier. Its drawing still marks the tiers by shoulder width, as it was drawn before the answers.
- **The spec:** R-9.14-06 and a decision log row dated 2026-11-10, in the spec change made together with this one.
- **Still open:** which pieces mark each tier in the end (the ones named are examples), and whether they read from the game's camera at 104 px. The sketch is a front view.

**What the picture answers set in motion (Q59 to Q62)**
- **`class-art-style.md` (done):** the review of the picture, the style in words, and a brief for the Slayer and the four root classes.
- **`art-direction.md` (done):** decisions A22 (the detailed look of characters, monsters and gear; the world stays muted) and A23 (no outline in illustrations; an edge in the game, tuned in the engine).
- **The figures board (done):** a note under its question 1. Its measurements of outlines and tones against the 20 grounds stay valid; they are what the edge has to solve.
- **The spec:** R-9.14-01, R-9.14-05, 10.4 and a decision log row dated 2026-11-12, in the spec change made together with this one.
- **Still open, for the first model in the engine:** the form of the edge (an outline or a rim light, and whether the two-tone outline stays); how the detailed look is built and what it costs with many players on screen; how it reads at about 104 px.
- **Not asked:** whether loot, resource nodes, doors and portals keep the two-tone outline. Until asked, Q42 stands for them.
- **Replaced later the same day:** the detailed look (Q59, Q61) by Q63 and Q67, the first sentence of Q60 by Q66, and the proportions (Q62) by Q64.

**What the model sheet answers set in motion (Q63 to Q67)**
- **`art-direction.md` (done):** decisions A24 (anime line art with flat cel shading for characters, monsters and gear, which replaces A22; line art in illustrations), A25 (about 6 heads tall, which replaces A20) and A26 (two small shoulder plates allowed on medium armour).
- **`class-art-style.md` (done, in a change of its own):** rewritten for the new style, with how the sheets are made and the briefs of the Slayer and the four root classes.
- **The figures board (done):** its head row and its silhouettes use the 30 cm head again; notes under its questions 1, 3, 5 and 12.
- **The spec:** R-9.14-01, R-9.14-05, R-9.14-06, the head size in R-11.6-01, 10.4 and a decision log row dated 2026-11-12, in the spec change made together with this one.
- **Still open, for the first model in the engine:** the form of the edge in the game (Q60, Q66); how 3D models get the line-art look and what it costs with many players on screen; how it reads at about 104 px.
- **Done since:** the five class sheets are stored in `docs/design/class-sheets/` (Git LFS) as reference pictures, on the designer's decision of 2026-11-12. Their sources and terms are in `class-art-style.md` and `THIRD_PARTY_LICENSES.md`.
