# Reference boards: build step W1 (C1's ring)

Status: **art reference, v2 (2026-11-10)**, updated with the designer's answers of 2026-11-10. Five biome boards for the first build step of the main map (`world-map.md` W1): Green vale, Heather moor, Amber woods, Lake country and Rime coast. **Every colour, material note and photo choice here is a proposal** for the designer to review. The drawing is `reference-boards-w1.svg` (same folder).

This file is **not the spec**. It follows the art rules of spec 9.14 and 11.6 and the sheets in `biome-sheets.md`, which the designer accepted on 2026-11-10 together with the answers at the end of this file. **The designer answered this file's questions on that day.** Where a paragraph below calls a point open and cites a question, the answer under that question settles it. The colours themselves stay proposals.

## What a board holds

- **Palette:** about ten swatches with a hex value, a role (ground, prop, accent or water) and a rough share of the screen. The swatches are the colours as they should look on screen in the biome's light. They are not texture values.
- **Light:** the biome's one fixed preset (R-9.14-04) as three colours: the sun, the tint of shadows, and the fog.
- **Ground, props and landmark:** taken from the biome sheet, with what each means for the camera, the fade and the walkability grid.
- **Settlement:** which W1 settlement stands in the biome and how the water kit is dressed for it (`city-layouts.md` E7).
- **Warning check:** how the four warning colours look on the biome's ground (see below).
- **Photo references:** links to free-licence photos on Wikimedia Commons. No image is stored in the repository.

## What the boards follow

- **Style:** stylized toon, with cel shading and simple colour areas (R-9.14-01). The photos are references for materials, shapes and mood. They are not a target for detail.
- **A muted world with bright gameplay** (R-9.14-03): red, yellow, blue and green at full strength belong to the attack warnings (R-3.3-04), effects, loot and the HUD.
- **The camera** has a fixed 45-degree pitch (R-2.2-05), so it shows the ground, roofs and tree tops, never the horizon. One screen is about 20-42 m wide and 23 m deep, from 6 m behind the character to 17 m ahead. These sizes are the table "What the camera shows" in `art-direction.md`, computed from placeholder camera values (distance 18 m, vertical field of view 40 degrees). The final values are Open (10.4).
- **Every camera finding moves if those values change.** That holds for these boards and for the W2, W3, C1, C2 and C3 boards: the 15 m of the island shrine, a bridge as a quarter of the screen, the frames in the city drawings and the fade tables of the city boards.
- **The fade** (R-9.14-02): anything taller than about 2 m that a player can stand next to must be able to fade. Props up to about 1.2 m never need to (guideline in `art-direction.md`).
- **Sizes** (R-11.6-01 to R-11.6-06): roads 6 m, paths 3 m, bridges 5 m; walkable slopes up to about 30 degrees, steeper ground is a cliff of at least 3 m; one walkable layer and no jumping.
- **Borders** (R-2.3-11): a natural edge and a 20-30 m blend.

## How the palettes were checked

- **Saturation** is the S of HSV. For comparison, the warning colours in `HudThemeTokens` have a saturation of 79% (red), 76% (yellow), 76% (blue) and 62% (green).
- **Warnings on the ground:** each warning colour was blended over each ground swatch the way the client does it today, with the colour and alpha from `HudThemeTokens`, in linear light. The number is the colour difference between the bare ground and the ground under the warning (CIE76 Delta E; higher is easier to see). The percentage compares it with the same warning on the M1 greybox ground `#4b554b`, which the alphas were tuned for.
- **Colour vision:** the four blended warnings were run through a simulation of the three kinds of colour blindness (Machado 2009, full strength), and the table gives the difference between the two warnings that end up closest.
- **Limits of the check:** it compares flat colours. In the game the light, the shadows, the warning's shape and icon, and its motion all change how well it reads. The numbers show where to look first; they are not a pass or fail.

### The exact method

The scripts that compute the tables and draw the drawings are in `tools/art-boards` (`Build-ArtBoards.ps1`; its `-Check` compares every board with its inputs). This is what they do, so that every number can also be recomputed by hand from the hex values. The W2, W3, C1, C2 and C3 boards use the same steps.

1. **sRGB to linear light:** a channel `c` in 0-1 becomes `c / 12.92` if `c <= 0.04045`, else `((c + 0.055) / 1.055) ^ 2.4`.
2. **A warning on a ground:** per channel, in linear light, `alpha * warning + (1 - alpha) * ground`. The colours and alphas are the `HudThemeTokens` values of 2026-11-10, with alpha as the hex byte divided by 255: red `#e0402f` alpha `59`, yellow `#f2c23a` alpha `5c`, blue `#3b8bf2` alpha `61`, green `#4ac26a` alpha `61`, uncoloured `#d8d8d8` alpha `2f`.
3. **Linear RGB to XYZ** (sRGB primaries, D65):
   - `X = 0.4124564 R + 0.3575761 G + 0.1804375 B`
   - `Y = 0.2126729 R + 0.7151522 G + 0.0721750 B`
   - `Z = 0.0193339 R + 0.1191920 G + 0.9503041 B`
4. **XYZ to CIE L\*a\*b\*** with the D65 white `(0.95047, 1, 1.08883)`: each of X, Y and Z is divided by its white value, then `f(t) = t ^ (1/3)` if `t > 0.008856`, else `7.787 t + 16/116`. `L = 116 f(Y) - 16`, `a = 500 (f(X) - f(Y))`, `b = 200 (f(Y) - f(Z))`.
5. **Colour difference:** CIE76, the straight-line distance in L\*a\*b\*. A warning number is the distance between the bare ground and the ground under the warning, both unrounded. A roof, road or edge number is the distance between two swatches.
6. **Percentages:** 100 times the difference on the swatch, divided by the difference of the same warning on `#4b554b`. Both are unrounded; the result is rounded to a whole number.
7. **Saturation and value:** HSV on the sRGB values, `S = (max - min) / max` and `V = max`. The average by share is the sum of `S * share` over a board's swatches, with each share as a fraction of 1.
8. **Colour vision:** each blended warning (linear RGB, unrounded) is multiplied by the matrix below, clamped to 0-1, and the four coloured warnings are compared in pairs with steps 3 to 5. The table gives the smallest of the six distances and names the pair. The matrices are those of Machado, Oliveira and Fernandes (2009) for severity 1.0, by rows:
   - protan: `(0.152286, 1.052583, -0.204868)`, `(0.114503, 0.786281, 0.099216)`, `(-0.003882, -0.048116, 1.051998)`
   - deutan: `(0.367322, 0.860646, -0.227968)`, `(0.280085, 0.672501, 0.047413)`, `(-0.011820, 0.042940, 0.968881)`
   - tritan: `(1.255528, -0.076749, -0.178779)`, `(-0.078411, 0.930809, 0.147602)`, `(0.004733, 0.691367, 0.303900)`
9. **Rounding:** every table number is rounded to a whole number as the last step. Where a board prints the hex of a blended colour, it is the linear result converted back to sRGB and rounded per channel.

## Findings across the five boards

### Saturation

| Biome | Average by share | Highest ground swatch | Highest swatch |
|---|---|---|---|
{{SAT}}

- All five stay below the least saturated warning colour (green, 62%).
- **Amber woods is the outlier.** An autumn forest needs rust and ochre, and those sit at 44-51%. If the saturation limit of R-9.14-03 is one number for all biomes, this biome sets it (question 1).

### Warnings on each ground

The reference row for every board:

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
{{BASELINE}}

- **Light ground weakens every warning.** On snow and ice all four colours keep 37-49% of the difference they have today. On straw-coloured ground (dry grass, reeds, ochre leaf litter) yellow keeps 39-49% and red 50-55%.
- **Dark ground holds up best:** bare peat and wet rock are level with today, and bare earth and mud keep 76-111%.
- **Green on grass** keeps 55% on the vale's olive grass and 57% on Lake country's wet grass. The sheets already keep the grass away from a strong green, and these values are with that done.
- **Blue holds up best:** 84-113% on every grass, straw, leaf litter and earth ground. It drops on the grey and white ones: 60% on heather, 66% on pebbles, 49% on snow.
- **The uncoloured area** all but disappears on snow (5%). It is an M1 placeholder (spec 9.3: uncoloured attacks have no warning in the final game), so this matters only while it exists.
- The tables per biome are in each board.

### Colour vision

The closest pair of the four warnings on each biome's main ground, by colour alone:

| Biome | Main ground | Normal | Protan | Deutan | Tritan |
|---|---|---|---|---|---|
{{CVD}}

- Red and green are already close for deutan vision on today's ground, which is why each warning has its own shape as well (R-3.3-04, spec 9.3). The biomes change little, except **snow**, where every pair gets closer.
- The guideline in `art-direction.md` asks to check every biome in the colorblind palette. Spec 9.8 names a colorblind palette option, and `data/tables` has no values for it yet, so that check waits for the palette.

### The water kit's slate roofs

`city-layouts.md` proposes slate grey roofs as the mark of the water kit, which all five biomes use. The drawing uses these colours for the kit: {{KIT}}. The table gives the difference between the slate and each biome's ground:

| Biome | Against the main ground | Closest ground swatch |
|---|---|---|
{{KITCHECK}}

- **Heather moor:** slate sits close to the heather (12), so M1's roofs may not stand out from the ground around the palisade (question 4).
- **Rime coast:** slate is clear against snow (43) and merges with the frozen shingle (5). The kit's example for M3 puts snow on the roofs, which would hide the slate on a white ground. The designer's answer (question 4): snow lies in patches, so the slate still shows on every building.

## The boards

### Green vale (`main.vale`)

**From the sheet:** meadows, C1's biome. Warm late-morning sun, clear air, almost no fog. An old windmill on a low hill is the landmark.

**Palette**

{{PALETTE:vale}}

**Light:** {{LIGHT:vale}}.

**Warnings on this ground**

{{CHECK:vale}}

**Ground, seen from the camera**
- Grass fills more than half of the screen. It is olive and straw, as the sheet asks, because green is a warning colour.
- The main road and the paths are the dirt colour. The road must read against the grass without the minimap (R-9.14-07), so the path stays warmer and less green than the grass around it.
- Wildflowers are pale patches, small enough not to read as loot or as a warning.

**Props and the fade**
- **Low stone walls and fences:** up to 1.2 m, so they never fade. They are long thin blockers. Blockers turn in 90-degree steps, so a wall laid north-south or east-west collides exactly and a diagonal one only roughly (R-11.6-06).
- **Hay bales:** a round bale at the heroic scale of x1.25-1.5 is 1.5 m or more, which is over the height that never fades. Low stacks of small bales stay under it.
- **Fruit trees and farm sheds:** taller than 2 m, so they fade. Sheds are closed facades (R-11.6-05).
- **The windmill:** a landmark building of up to about 15 m, which fades. The camera sees its cap and sails from above. The hill's sides stay under 30 degrees, or they are cliffs.

**Settlement:** C1, the water city, with its tutorial area beside it. C1 gets its own board. Every way out of the level-10 gate needs a visible mark (`open-world-content.md`).

**Who lives here:** beasts, with plants as the shared family; no main element. Creatures stand on olive and straw, so body colours away from those tones read best under the outline (R-9.14-05).

**Photo references**
{{REFS:vale}}

### Heather moor (`main.heather`)

**From the sheet:** windy moorland. Overcast and cool, with light mist near the ground. A ring of standing stones is the landmark.

**Palette**

{{PALETTE:heather}}

**Light:** {{LIGHT:heather}}.

**Warnings on this ground**

{{CHECK:heather}}

**Ground, seen from the camera**
- The greyest of the five boards: dusty purple-grey heather broken by dry grass and patches of bare peat.
- Red and blue are the weakest warnings on heather (59% and 60%). The purple stays grey so that neither sinks further into it.
- **Mist:** it lies near the ground, where the warnings are drawn. Mist drawn over a warning would lower its contrast further, so the warnings need to stay on top of it, or the mist needs to stay thin where fights happen.

**Props and the fade**
- **Standing stones:** 2-4 m, so they fade. The ring is the landmark and a place to arrive at, not something seen from afar.
- **Burial mounds:** they are terrain. A mound with sides up to 30 degrees is walkable; a steeper one is a blocker. They cannot be entered (one walkable layer, no dungeons on the main map).
- **Wind-bent thorn trees:** they fade.
- **Peat cuttings:** a real peat bank is a step of about 1 m. That is too steep to walk and lower than the 3 m a cliff needs (R-11.6-04), so a cutting is either drawn flat into the ground with low stacks of turf beside it, or its edge gets blockers.
- **Broken cart wheels:** low, never fade.

**Settlement:** M1, a medium city with a palisade and two gates, in the water kit with moor dressing. See the slate roof finding above.

**Who lives here:** undead (from the mounds), with beasts as the shared family; wind and dark.

**Photo references**
{{REFS:heather}}

### Amber woods (`main.amber`)

**From the sheet:** autumn forest. Soft golden afternoon light through the leaves. A giant fallen tree that a path runs under is the landmark.

**Palette**

{{PALETTE:amber}}

**Light:** {{LIGHT:amber}}.

**Warnings on this ground**

{{CHECK:amber}}

**Ground, seen from the camera**
- Fallen leaves in rust, ochre and brown are the main ground. This is the board whose ground sits closest to two warning colours: red on the rust litter keeps 52% of today's difference and yellow on the ochre litter 41%.
- The sheet asks to keep the orange muted. The swatches do, and they are still the most saturated of the five.
- Bare earth is the darkest ground here and holds the warnings better than the leaf litter, so it suits the places where fights happen: camps and the field boss arena.

**Props and the fade**
- **Birch and maple trees:** in a forest the player is under the canopy most of the time, so much of the screen is fading most of the time. The trunks and the ground then carry the picture. Sparse crowns and clearings keep more of the canopy visible.
- **Leaf piles, mushrooms on logs, beehives:** low, never fade.
- **Hunters' stands:** 3-5 m, so they fade. They are scenery and cannot be climbed (one walkable layer).
- **The fallen tree over the path:** this fits the one-layer grid as long as nothing is walkable on top of the trunk. The trunk fades when it hides the player. Its collider must not count as ground, or the exporter would lift the path onto it (spec 11.6, the open art item about a ground layer).

**Settlement:** T1, a small open town, in the water kit with autumn dressing.

**Who lives here:** plants, with beasts as the shared family; earth. Plant monsters in litter colours would vanish into the ground, so they need their own tones under the outline.

**Not drawn:** wood as a resource is Open (10.4 [M3]), so the board has no wood nodes.

**Photo references**
{{REFS:amber}}

### Lake country (`main.lakes`)

**From the sheet:** lakes. Bright and clear, with a light haze over the water. The big lake with a shrine on a small island is the landmark, seen from the shore and not reachable for now.

**Palette**

{{PALETTE:lakes}}

**Light:** {{LIGHT:lakes}}.

**Warnings on this ground**

{{CHECK:lakes}}

**Ground, seen from the camera**
- About a quarter of the screen is water, which is unwalkable. It is grey-teal and stays grey, so it never reads as a blue warning.
- The shore is the place to get right: wet grass, then reeds, then pebbles, then water. That order also shows the player where walking ends.
- Reeds are as pale as straw, where yellow is at its weakest (39%).

**Props and the fade**
- **Jetties:** walkable planks over water fit the one-layer grid. A jetty a player walks on is at least 1.8 m wide (the narrowest gap that is always walkable), and 3 m if it counts as a path.
- **Stepping stones:** there is no jumping, so a row of stones a player crosses must be one unbroken walkable strip on the grid. The stones are the look of a ford.
- **Fishing nets on poles:** thin and taller than 2 m. They fade, or they stand where no path passes behind them.
- **Willows:** they fade. **Upturned boats:** low, never fade.
- **Fishing spots** are marked places on the shore (`open-world-content.md` F5).
- **The island shrine:** the camera sees only 17 m ahead of the character. The island must lie within about 15 m of a shore path to be on screen at all. Further out, it exists only on the maps.

**Settlement:** T2, a small open town, in the water kit with jetties and nets as dressing.

**Who lives here:** humanoids (river raiders), with beasts as the shared family; water.

**Photo references**
{{REFS:lakes}}

### Rime coast (`main.rime`)

**From the sheet:** ice 1. Low, pale sun and a cold white haze toward the sea. A ship frozen into the ice is the landmark.

**Palette**

{{PALETTE:rime}}

**Light:** {{LIGHT:rime}}.

**Warnings on this ground**

{{CHECK:rime}}

**Ground, seen from the camera**
- The lightest ground of the five, and the one where the warnings are weakest: all four keep less than half of today's difference on snow and ice.
- The ice is grey-white with a slight cyan tint, as the sheet asks. Its saturation is 7%.
- Dark wet rock is level with today and frozen shingle keeps 67-80%. They suit the camps and the field boss arena.
- The edge of the walkable ice against the dark sea must be clear, because the sea is unwalkable.

**Props and the fade**
- **Ice blocks pushed up on the shore:** the big ones are over 2 m and fade.
- **Frozen fishing racks:** tall wooden frames, so they fade.
- **Driftwood and frosted boulders:** low, never fade.
- **Ice-covered boat hulls and the frozen ship:** the masts and rigging fade. The board treats the ship as scenery; the sheet does not say whether its deck is walkable.
- **Ice holes** are the sea fishing spots.

**Settlement:** M3, a medium city with a palisade and three gates at the west end of the north bridge, with its gate facing the bridge. `city-layouts.md` gives it as the example of dressing: the water kit with snow on the roofs and ice on the quays. See the slate roof finding above.

**Who lives here:** ice elementals, with beasts as the shared family; ice and water. An ice creature on ice has only its outline and its darker parts to stand out.

**Photo references**
{{REFS:rime}}

## Borders inside W1

From `biome-sheets.md` (proposals), with a 20-30 m blend of ground, props and light (R-2.3-11):

| Border | Natural edge | What blends |
|---|---|---|
| vale - heather | a low ridge | olive grass to heather; warm sun to overcast |
| vale - amber | a stream with fords | grass to leaf litter; fruit trees to birch |
| heather - amber | the tree line | heather to litter; grey light to gold |
| heather - lakes | the moor drains into the first ponds | peat and bog water to wet grass and reeds |
| heather - rime | the snow line on a gentle rise | heather to snow in patches; mist to sea haze |
| amber - lakes | the lake shore | litter to pebbles and reeds |
| lakes - rime | a frozen inlet | lake water to ice; wet grass to shingle |

The largest jump in lightness is from heather to snow. The warnings change strength across that blend.

## Photo references: how they were picked

- All are files on Wikimedia Commons under a free licence: public domain, CC0, CC BY or CC BY-SA. Each line names the author and the licence as Commons states them.
- They were found with the Commons search and each one was looked at before it was listed.
- They are **links only**. Nothing was downloaded into the repository, and none is used in the game, so `THIRD_PARTY_LICENSES.md` has no new row.
- If a picture is ever copied into the repository or into a board file, CC BY and CC BY-SA need the author's credit, CC BY-SA also applies to a changed copy, and the file needs its row in `THIRD_PARTY_LICENSES.md` (question 6).

## Questions for the designer, and the answers

The designer answered these on 2026-11-10. The questions stand as they were asked, so that the references to them in this file still hold. The full list is `reference-boards-questions.md`, and the "Q" numbers refer to it.

1. **The saturation limit** (R-9.14-03, Open in 10.4): is it one number for every biome or a number per biome? The proposed palettes peak at 23% (Heather moor) to 51% (Amber woods).
   - **Answer (Q3):** a number per biome, taken from its board. The numbers are not written yet.
2. **Warnings on light ground:** the warning alphas were tuned for the dark M1 ground. On snow, ice and straw-coloured ground they are much weaker. Should the warnings change with the ground (a stronger alpha, a dark edge), should light biomes keep their fighting places on darker ground, or is the shape and icon enough? No rule covers this today.
   - **Answer (Q1):** the warning changes: it gets a dark edge or a stronger fill on light ground. The palettes stay. Which of the two is not decided yet.
3. **Amber woods:** is the board muted enough, given that its ground is the closest of the five to the red and yellow warnings?
   - **Answer (Q4):** keep it.
4. **Slate roofs** on the Heather moor and under snow on the Rime coast: does the water kit keep one roof colour everywhere, or may the biome dressing change it?
   - **Answer (Q18):** snow lies on roofs in patches, so the kit's roof colour still shows on every building.
5. **Style references:** the design files name no game, film or artist as a style reference. `THIRD_PARTY_LICENSES.md` mentions one character illustration, which is not in the repository. Are there references the boards should be held against?
   - **Answer (Q32):** "I will share later" (the designer's words).
6. **Pictures in the boards:** links only, as here, or should free-licence pictures be stored with the boards (with credits, Git LFS and rows in `THIRD_PARTY_LICENSES.md`)?
   - **Answer (Q33):** links only.
7. **The review of the five sheets** (10.4) is still open, and these boards follow the sheets as written.
   - **Answer (Q31):** accepted, with these answers.

## Not covered

- C1, the water city, and the detailed dressing of M1, M3, T1 and T2.
- Characters, monsters, UI and the world map.
- The other 15 biomes.
- How the colours behave in the engine. The swatches are targets, and the cel shader and the light presets have to reach them.
