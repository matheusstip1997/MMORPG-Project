# Reference boards: build step W2 (C2 and the rest of the west continent)

Status: **art reference, v2 (2026-11-10)**, updated with the designer's answers of 2026-11-10. Five biome boards for the second build step of the main map (`world-map.md` W2): Old forest, Fungal hollow, Dry steppe, Red sands and Bone canyons. **Every colour, material note and photo choice here is a proposal** for the designer to review. The drawing is `reference-boards-w2.svg` (same folder).

This file is **not the spec**. It follows the art rules of spec 9.14 and 11.6 and the sheets in `biome-sheets.md`, which the designer accepted on 2026-11-10 together with the answers at the end of this file. **The designer answered this file's questions on that day.** Where a paragraph below calls a point open and cites a question, the answer under that question settles it. The colours themselves stay proposals.

The boards are built and checked the same way as `reference-boards-w1.md`: see "What a board holds" and "How the palettes were checked" there. In short, the swatches are on-screen colours in the biome's light, saturation is the S of HSV, and the warning numbers are colour differences (CIE76 Delta E; higher is easier to see) between the bare ground and the ground under each warning, with a percentage against today's M1 greybox ground `#4b554b`. They compare flat colours and show where to look first; they are not a pass or fail.

New on these boards: each palette has a **road swatch**, checked against the ground beside it, and the drawing shows two kits, because W2 uses both.

## Findings across the five boards

### Saturation

| Biome | Average by share | Highest ground swatch | Highest swatch |
|---|---|---|---|
{{SAT}}

- All five stay below the least saturated warning colour (green, 62%).
- **Red sands is the most saturated of the five** (36% on average, with rock and clay at 46-49%). Of the ten boards so far only Amber woods is higher (41%).

### Warnings on each ground

The reference row for every board:

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
{{BASELINE}}

- **The two dark biomes are the best grounds so far.** On the Old forest's dark earth and the Fungal hollow's dark soil every warning is as clear as today or clearer (102-141%).
- **The three dry biomes are light and warm, and red and yellow suffer.** On the steppe's pale grass yellow keeps 35%, the lowest value on any board so far. On the Red sands' tan sand yellow keeps 39% and red 44%; on the lit gravel of the canyons both keep 48%.
- **Blue holds up everywhere** (79-120%), except on the pale mycelium threads (65%).
- **Red sands is a start biome.** C2's tutorial area is in it (`world-map.md` D10), so a new player's first fights happen on the ground where red and yellow are weakest (question 1).
- The tables per biome are in each board.

### Colour vision

The closest pair of the four warnings on each biome's main ground, by colour alone (simulation as on the W1 boards):

| Biome | Main ground | Normal | Protan | Deutan | Tritan |
|---|---|---|---|---|---|
{{CVD}}

- On the dark grounds the pairs are a little further apart than today. On pale grass and sand they are closer, as on snow. Each warning has its own shape as well (R-3.3-04, spec 9.3).

### Roads against the ground

R-9.14-07 asks for roads clearly drawn into the ground. The difference between each board's road swatch and the two largest grounds beside it:

| Biome | Road | Against the main ground | Against the second ground |
|---|---|---|---|
{{ROAD}}

- **In sand and gravel a road is hard to tell by colour:** 8-15 in Red sands and Bone canyons. A packed road in a desert is the same material as the ground.
- On the steppe the track is clear against grass (23) and close to cracked earth (9).
- A road in these biomes needs more than a colour: edge stones, posts, ruts or a darker centre line (question 2).

### Kit roofs against the ground

W2 uses both kits. The drawing uses these colours: {{KIT}}. The slate and the kit materials are from `city-layouts.md`; the dome and wall colours are this board's proposal for the kit's domes and clay walls.

| Biome | Settlement and kit | Slate roof | White dome |
|---|---|---|---|
{{KITCHECK}}

- **The dome on pale ground:** the designer asked for a whiter dome (question 6). The white dome is 26 from the steppe's grass and 29 from the Red sands' tan sand, on which C2 itself stands. The earlier pale cream dome `#DCD2BC` was at 16 and 18.
- **M2 in the Old forest** uses the water kit, as the designer decided (question 7). Its slate roof is 25 from the dark earth.
- **T3 in the Fungal hollow:** slate is clear against the dark soil (27) and merges with the packed path (7).
- T5 in the Bone canyons uses the bazaar kit, so the low slate value there (6) does not apply.

## The boards

### Old forest (`main.oldforest`)

**From the sheet:** deep forest, the darkest ground on the west continent. Dim green-grey shade with shafts of light in clearings. A hollow ancient tree big enough to walk into is the landmark. The roads here are dangerous (`open-world-content.md` F1).

**Palette**

{{PALETTE:oldforest}}

**Light:** {{LIGHT:oldforest}}.

**Warnings on this ground**

{{CHECK:oldforest}}

**Ground, seen from the camera**
- Dark earth, moss and ferns. On the dark earth the warnings are clearer than today.
- **Green on green:** the green warning keeps 80% on moss, 63% on ferns and 59% in a lit clearing. The greens here are dim and greyed for that reason.
- **Clearings** are the lightest ground and the place for large monsters and the field boss, which need open ground (R-2.2-08). A clearing floor nearer to earth than to bright moss keeps the warnings stronger where they are needed most.
- The whole biome is dim. Dark creatures on dark earth have only their outline (R-9.14-05).

**Props and the fade**
- **Huge old trees:** they fade. Under a closed canopy most of the screen fades most of the time, more than in Amber woods. Trunks and ground carry the picture.
- **Giant roots as low walls:** up to 1.2 m they never fade. They are blockers that turn freely, so their collision is approximate (R-11.6-06).
- **Fallen trunks and stone markers:** low ones never fade.
- **Spider webs:** visual only (R-2.3-10).
- **The hollow tree:** a player inside it is under its trunk, so the trunk above has to lift off like a roof (R-9.14-02). R-11.6-05 lists only service buildings as interiors (question 4).

**Settlement:** M2, a medium city with a palisade and three gates, in the water kit (question 7); see the roof finding above.

**Who lives here:** beasts, with plants and aberrations as the shared families; earth.

**Not drawn:** wood as a resource is Open (10.4 [M3]).

**Photo references**
{{REFS:oldforest}}

### Fungal hollow (`main.fungal`)

**From the sheet:** a giant mushroom grove. Dusk-like, with soft glowing spores as the main light. One enormous mushroom with a path spiralling around its base is the landmark. The roads here are dangerous.

**Palette**

{{PALETTE:fungal}}

**Light:** {{LIGHT:fungal}}.

**Warnings on this ground**

{{CHECK:fungal}}

**Ground, seen from the camera**
- Spongy dark soil, the best ground for warnings on any board (102-141%).
- **Pale mycelium threads** are thin lines on that soil. As lines they cost little; as large pale patches they would weaken the warnings (45-65%).
- **The caps are this biome's roofs.** The camera sees them from above as large discs in muted violet, teal and cream, and they cover a good part of the ground.

**The glow**
- The light comes from many small sources (spores, moss) and not from a sun. The preset is a dim ambient light plus glowing props (question 5).
- The sheet keeps the glow soft so that effects still stand out. The glowing moss and the teal caps are at 28-29% saturation, against 62-79% for the warnings.
- **Teal lies between the blue and the green warning in hue.** It is used on props and accents here, not as ground.
- Spore clouds are visual only (R-2.3-10).

**Props and the fade**
- **Giant mushroom caps:** they fade. A player under a cap is hidden by it, so caps fade often.
- **Puffballs, glowing moss, rotting logs:** low, never fade.
- **The enormous mushroom:** it fades. A path around its base fits the one-layer grid while it stays on the ground. A path that climbs the stem in a spiral would pass over itself, which one layer cannot hold (question 3).
- The hollow lies lower than its neighbours, with a rim and ramps (R-11.6-04).

**Settlement:** T3, a small open town in the water kit. Pale quay stone would be the lightest thing on this ground.

**Who lives here:** aberrations, with plants as the shared family; poison.

**Photo references**
{{REFS:fungal}}

### Dry steppe (`main.steppe`)

**From the sheet:** dry grassland. Bright, hot and slightly hazy. A giant weathered statue lying on its side is the landmark. The roads here are safe from monsters.

**Palette**

{{PALETTE:steppe}}

**Light:** {{LIGHT:steppe}}.

**Warnings on this ground**

{{CHECK:steppe}}

**Ground, seen from the camera**
- Open and flat, with few tall props, so the screen is almost all ground. The variety has to come from the ground itself: tufts, cracks, stones, bare patches.
- **Pale dry grass is the weakest ground for yellow so far (35%)**, and green keeps 46% on it.
- Cracked earth, the track and the mud are darker and better.

**Props and the fade**
- **Lone thorn trees and nomad tent frames:** taller than 2 m, so they fade.
- **Animal skulls and stone cairns:** low, never fade.
- **Watering holes:** the water is unwalkable; the mud rim is the darkest ground here.
- **The fallen statue:** it fades where it hides the player. Nothing is walkable on top of it, and its collider must not count as ground (spec 11.6, the open art item about a ground layer).

**Settlement:** T4, a small open town in the bazaar kit. See the dome finding above.

**Who lives here:** humanoids (raiders), with beasts as the shared family; wind.

**Photo references**
{{REFS:steppe}}

### Red sands (`main.redsands`)

**From the sheet:** the medium desert and C2's biome. Strong high sun with warm haze. A half-buried obelisk is the landmark. The roads here are safe from monsters.

**Palette**

{{PALETTE:redsands}}

**Light:** {{LIGHT:redsands}}.

**Warnings on this ground**

{{CHECK:redsands}}

**Ground, seen from the camera**
- Sand in dusty tan and dusty terracotta, with darker red rock. The sheet asks for a muted, dusty tone, not bright red or yellow; the sand swatches are at 32-40%.
- **Red and yellow are weak on this sand** (39-49%), and this is where new players of C2 start.
- Sand in shade and the packed road are a little better. Blue keeps 85-97%.
- **Dunes** are walkable up to about 30 degrees; rock outcrops are cliffs (R-11.6-04).

**Props and the fade**
- **Rock outcrops:** the tall ones fade.
- **Palm clusters at wells:** they fade.
- **Dry bushes and clay jars:** low, never fade.
- **Half-buried walls:** low stretches never fade; taller ones do.
- **The obelisk:** a landmark of up to about 15 m, which fades.

**Settlement:** C2, the caravan bazaar city, with its tutorial area beside it. C2 gets its own board.

**Who lives here:** beasts (scorpions, lizards), with undead as the shared family; earth. Sand-coloured creatures on sand have only their outline.

**Photo references**
{{REFS:redsands}}

### Bone canyons (`main.canyons`)

**From the sheet:** red-rock canyons. Hard light on the rims and deep cool shadow on the canyon floors. A giant ribcage that the main path runs through is the landmark. The roads here are safe from monsters.

**Palette**

{{PALETTE:canyons}}

**Light:** {{LIGHT:canyons}}.

**Warnings on this ground**

{{CHECK:canyons}}

**Ground, seen from the camera**
- The floors are gravel, mostly in shadow. Shadowed gravel is a fair ground for warnings (70-92%); lit gravel is weaker for red and yellow (48%).
- The walls are layered rock in brown-red and cream. The red is the most saturated swatch here (46%) and stays on walls, not on the ground.
- Bleached bone is the lightest thing on the board.

**Canyon walls and the fade**
- A wall on the camera side hides the player within a distance equal to its height (the 45-degree guideline of `art-direction.md`). In a canyon narrower than its walls are tall, one wall is always between the camera and the player.
- The guideline lists cliffs among the things that must be able to fade. A canyon wall is terrain, so it would have to be built as pieces that can fade, or the floors have to be wider than the walls are tall (question 3).
- A cliff is at least 3 m tall (R-11.6-04).
- **Large monsters** need open ground with no gap under about 4 m (R-2.2-08). The wyverns of the rims and the field boss need the wide parts of the canyon.

**Props and the fade**
- **Giant ribs and the ribcage over the main path:** the ribs fade when they hide the player. Nothing is walkable on top of them, and their colliders must not count as ground.
- **Rope bridges:** decoration only, as the sheet says (one walkable layer).
- **Rock arches:** paths go around them.
- **Mine scaffolds and vulture perches:** tall, so they fade.

**Settlement:** T5, a small open town in the bazaar kit.

**Who lives here:** undead, with dragons (wyverns nesting on the rims) as the shared family; earth and dark.

**Photo references**
{{REFS:canyons}}

## Borders of W2

From `biome-sheets.md` (proposals), with a 20-30 m blend of ground, props and light (R-2.3-11):

| Border | Natural edge | What blends |
|---|---|---|
| amber - old forest | trees grow taller and darker; an old boundary wall | leaf litter to dark earth; gold light to green-grey shade |
| lakes - fungal | a boggy outflow stream | wet grass to dark soil; bright light to dusk |
| rime - fungal | a cliff step down from the ice, with ramps | snow to dark soil, at a cliff |
| old forest - fungal | the hollow's rim; trees thin and mushrooms take over | dark earth to dark soil; canopy to caps |
| old forest - steppe | the tree line | dark earth to pale grass; shade to full sun |
| fungal - steppe | the hollow's rim; a ramp up onto dry ground | dark soil to pale grass |
| steppe - red sands | a dry riverbed | pale grass to tan sand |
| red sands - canyons | the canyon rim: cliffs with ramps down | sand to gravel in shadow |

The largest jumps in lightness are from the Old forest and the Fungal hollow to the steppe, and from the Rime coast down into the hollow. The warnings change strength across those blends.

## Photo references: how they were picked

As on the W1 boards: free-licence files on Wikimedia Commons, found with the Commons search, each looked at before it was listed, each with the author and licence that Commons states. They are **links only**. Nothing is stored in the repository or used in the game, so `THIRD_PARTY_LICENSES.md` has no new row. The Fungal hollow has no real-world twin, so its references are small fungi to be scaled up.

## Questions for the designer, and the answers

The designer answered these on 2026-11-10. The questions stand as they were asked, so that the references to them in this file still hold. The full list is `reference-boards-questions.md`, and the "Q" numbers refer to it.

1. **Warnings on light ground** (also question 2 of the W1 boards): Red sands is a start biome, and red and yellow are at 39-49% on its sand. Does that change the answer?
   - **Answer (Q1):** the warning changes: it gets a dark edge or a stronger fill on light ground. The palettes stay. Which of the two is not decided yet.
2. **Roads on sand and gravel:** the road colour is 8-15 from the ground. What makes a road "clearly drawn" (R-9.14-07) in a desert or a canyon?
   - **Answer (Q28):** posts or poles, and edge stones or cairns.
3. **One walkable layer and the fade, in three places:** can canyon walls fade, or are canyon floors wider than the walls are tall? Does the path at the giant mushroom stay on the ground? Is nothing walkable on the fallen statue and the ribs?
   - **Answer (Q12):** canyon floors are wider than their walls are tall, and the walls stay plain terrain.
   - **Answer (Q14):** nothing is walkable on the fallen statue, on the ribs or up the giant mushroom. The path at the mushroom stays on the ground.
4. **The hollow tree:** is it a walk-in place whose trunk lifts off like a roof, although R-11.6-05 lists only service buildings as interiors?
   - **Answer (Q13):** yes. It is a walk-in place whose trunk hides like a building's roof. R-11.6-05 needs an exception for it.
5. **The Fungal hollow's light:** is a dim ambient light plus many glowing props what "one fixed light per biome" (R-9.14-04) means here?
   - **Answer (Q29):** yes. The preset is a dim ambient light, and the glowing props are part of the look.
6. **Pale domes on pale ground:** the bazaar kit's roof is close to sand and dry grass. Does the dome get a different tone, or does something else mark the kit?
   - **Answer (Q19):** the dome gets a different tone: whiter, a lime-washed white.
7. **M2's kit** (Open in 10.4): the roof finding above gives numbers for both choices.
   - **Answer (Q23):** the water kit.
8. **The review of the five sheets** (10.4) is still open, and these boards follow the sheets as written.
   - **Answer (Q31):** accepted, with these answers.

## Not covered

- C2, the caravan bazaar city, and the detailed dressing of M2, T3, T4 and T5.
- Characters, monsters, UI and the world map.
- The W3 biomes (the island and the east continent) and the Ember caldera.
- How the colours behave in the engine. The swatches are targets, and the cel shader and the light presets have to reach them.
