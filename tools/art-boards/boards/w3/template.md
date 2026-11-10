# Reference boards: build step W3 (C3, the island and the east continent)

Status: **art reference, v2 (2026-11-10)**, updated with the designer's answers of 2026-11-10. Nine biome boards for the third build step of the main map (`world-map.md` W3): Dune isle, Palm coast, Fern jungle, Reed marsh, Ash woods, Steam springs, Snow taiga, Frost peaks and Storm cliffs. **Every colour, material note and photo choice here is a proposal** for the designer to review. The drawing is `reference-boards-w3.svg` (same folder).

This file is **not the spec**. It follows the art rules of spec 9.14 and 11.6 and the sheets in `biome-sheets.md`, which the designer accepted on 2026-11-10 together with the answers at the end of this file. **The designer answered this file's questions on that day.** Where a paragraph below calls a point open and cites a question, the answer under that question settles it. The colours themselves stay proposals.

The boards are built and checked the same way as `reference-boards-w1.md` and `reference-boards-w2.md`: see "What a board holds" and "How the palettes were checked" in the first. In short, the swatches are on-screen colours in the biome's light, saturation is the S of HSV, and the warning numbers are colour differences (CIE76 Delta E; higher is easier to see) between the bare ground and the ground under each warning, with a percentage against today's M1 greybox ground `#4b554b`. They compare flat colours and show where to look first; they are not a pass or fail.

New on these boards: a table of **every biome board so far** (19 of the 20 biomes), and notes on the two things W3 adds to the map besides biomes: the three bridges and the cliff wall between Palm coast and Snow taiga.

## Findings across the nine boards

### Saturation

| Biome | Average by share | Highest ground swatch | Highest swatch |
|---|---|---|---|
{{SAT}}

- All nine stay below the least saturated warning colour (green, 62%).
- **This is the least saturated set so far:** five of the nine average under 20%, and the grounds of the snow, ash and basalt biomes are close to grey.
- **The two fires are the most saturated swatches** (59%): the embers of the Ash woods and the signal braziers of the Storm cliffs. They are small glowing points, and their orange lies between the red and the yellow warning in hue (question 5).

### Warnings on each ground

The reference row for every board:

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
{{BASELINE}}

- **Two dark biomes read as well as today or better:** on the Storm cliffs' basalt (95-128%) and the Fern jungle's dark mud (99-122%).
- **Reed marsh is the best of the three start grounds.** On its wet earth the warnings keep 78-105%. The start ground of C1 keeps 53-105% and that of C2 39-85%.
- **Five biomes have a pale main ground** where red, yellow and green keep under half: Dune isle, Palm coast, Steam springs, Snow taiga and Frost peaks.
- **Yellow on Palm coast's pale sand keeps 31%**, the lowest value on any board so far.
- **On snow blue is weak too** (45-56%), as on the Rime coast. On sand and mineral terraces blue holds up better (59-84%).
- **Green on green:** the green warning keeps 64% on the jungle's ferns, 57% on the marsh's olive reeds and 54% on the Palm coast's undergrowth.
- The tables per biome are in each board.

### Every biome board so far

The main ground of each of the 19 biomes on the three sets of boards, from the darkest to the lightest. L is the lightness of the ground (CIE L*, 0 black to 100 white), and the percentages are what each warning keeps of today's difference.

| Biome | Main ground | L | Red | Yellow | Blue | Green |
|---|---|---|---|---|---|---|
{{ALLGROUNDS}}

- **Lightness explains most of it.** Below about L 30 every warning is about as clear as today or clearer (95-141%). From about L 70 red and yellow are under half.
- **Eight of the 19 biomes** have a main ground where red and yellow both keep under half: Red sands, Dry steppe, Dune isle, Palm coast, Steam springs, Rime coast, Snow taiga and Frost peaks. Two of the three start biomes are not among them; Red sands is.
- **Hue matters in the middle:** red is weakest for its lightness on the Amber woods' rust litter (52%), and green on the grass of the Green vale and Lake country (55-57%).
- This table is the short form of question 2 of the W1 boards, which is still open (question 1 here).

### Colour vision

The closest pair of the four warnings on each biome's main ground, by colour alone (simulation as on the W1 boards):

| Biome | Main ground | Normal | Protan | Deutan | Tritan |
|---|---|---|---|---|---|
{{CVD}}

- On the dark grounds the pairs are as far apart as today or a little further. On sand, terraces and snow they are at about half of today's distance or less, as on the Rime coast. Each warning has its own shape as well (R-3.3-04, spec 9.3).

### Roads against the ground

R-9.14-07 asks for roads clearly drawn into the ground. The difference between each board's road swatch and the two largest grounds beside it:

| Biome | Road | Against the main ground | Against the second ground |
|---|---|---|---|
{{ROAD}}

- **Sand, ash and snow hide a road:** 3-11 on Dune isle and Palm coast, 10 in the Ash woods, 12-15 in the Snow taiga. A trampled road in snow, ash or sand is the same material as the ground.
- **Dune isle and Snow taiga have dangerous roads** (`open-world-content.md` F1), so these are roads a player has to follow while watching for camps.
- Built or bare roads are clear: the jungle's plank walkway (21-25), the marsh's causeway (16-21), the scree path of the Frost peaks against snow (19-35).
- This is question 2 of the W2 boards again, now for snow and ash as well (question 2 here).

### Kit roofs against the ground

W3 uses the medieval kit everywhere except on Dune isle, which uses the bazaar kit (`city-layouts.md` E7). The drawing uses these colours: {{KIT}}. `city-layouts.md` names the materials ("grey stone, timber frames, tiled roofs", "warm tile"); the hex values are this board's proposal.

| Biome | Settlement and kit | Warm tile roof | White dome |
|---|---|---|---|
{{KITCHECK}}

- **The warm tile roof reads on every W3 ground** (20-48 against the main ground). It is weakest where C3 stands: 20 against the marsh's wet earth and 13 against a plank path.
- **The dome on the palest sand:** the designer asked for a whiter dome (question 6 of the W2 boards). On Dune isle the white dome is 17 from the cream sand and 9 from a shell beach; the earlier pale cream dome was at 7 and 4. M4 and T6 are still the bazaar-kit settlements where the dome reads least (the Red sands are at 29).
- **Snow on the roofs:** `city-layouts.md` gives "snow on the roofs" as the example of biome dressing. In the Snow taiga and the Frost peaks that covers the tile, the one colour that marks the kit from above. The designer's answer (question 6): snow lies in patches, so the tile still shows on every building.
- **A warm roof is a muted red-orange.** A town of tile roofs is a large area close to the red warning's hue; the C3 board will have to check it.

## The boards

### Dune isle (`main.duneisle`)

**From the sheet:** the small desert, on the island between the two south bridges. Bright sea light with wind-blown sand in the air. A lighthouse at the island's south end is the landmark. The roads here are dangerous (`open-world-content.md` F1).

**Palette**

{{PALETTE:duneisle}}

**Light:** {{LIGHT:duneisle}}.

**Warnings on this ground**

{{CHECK:duneisle}}

**Ground, seen from the camera**
- Pale cream sand, lighter and greyer than the Red sands (22% against 32%), with rippled dunes and shell beaches.
- **Red, yellow and green are weak on all of it** (33-54% on dry sand). Wet sand at the waterline is the best ground here (50-87%).
- The island is narrow, with sea on both sides, so the sea is often on screen. It is the only strong colour contrast on the board.
- **Dunes** are walkable up to about 30 degrees (R-11.6-04).

**Things in the air**
- Wind-blown sand is a layer between the camera and the ground. It is visual only (R-2.3-10), but it lies over warnings too (question 3).

**Props and the fade**
- **Dune grass, driftwood, crates, rope fences:** low, never fade.
- **Wrecked boats:** a hull lying on its side is 2-3 m tall, so it fades.
- **The lighthouse:** a real lighthouse is taller than the 15 m the measurement sheet gives a landmark building. Either way it fades, and the camera never shows its top, so on screen it is a base and a wall. Its beam is an effect.

**Settlement:** M4, a medium city with a palisade and two gates, between the two bridges, and T6, a small open town. Both use the bazaar kit; see the dome finding above.

**Who lives here:** elementals (sand), with humanoids (smugglers) as the shared family; earth and wind. A sand elemental on sand has only its outline (R-9.14-05).

**Photo references**
{{REFS:duneisle}}

### Palm coast (`main.palms`)

**From the sheet:** a tropical shore. Warm and bright, with soft shadows. A shipwreck cove is the landmark. The roads here are safe from monsters.

**Palette**

{{PALETTE:palms}}

**Light:** {{LIGHT:palms}}.

**Warnings on this ground**

{{CHECK:palms}}

**Ground, seen from the camera**
- Pale sand, dark wet sand at the waterline, and low green undergrowth inland.
- **Yellow keeps 31% on the pale sand**, the lowest value on any board. Red keeps 43% and green 45%.
- The undergrowth and the wet sand are better (54-105%), so the fighting places inland read better than the beach.
- **Turquoise shallows:** the sheet keeps them soft; the swatch is at 28%. In hue, turquoise lies between the blue and the green warning. Water is not fought on, so no warning is drawn over it, but a warning at the waterline has it beside it.

**Props and the fade**
- **Palm trees:** a thin trunk and a crown at 6-8 m. They fade. A crown hides far less ground than a broad tree.
- **Fishing huts on stilts:** scenery; nothing is walkable under them (one walkable layer).
- **Tide pools, coral rocks, beached canoes:** low, never fade.
- **The shipwreck:** it fades where it hides the player. The sheet does not say whether its deck is walkable; a walkable deck needs a ramp and nothing walkable below it.

**Settlement:** T10, a small open town in the medieval kit, at the east end of the south-east bridge. Grey stone and tile on a tropical beach is the kit's furthest stretch from its home; the biome dressing has to carry it (thatch, stilts, palm timber).

**Who lives here:** humanoids (pirates), with beasts as the shared family; water.

**Photo references**
{{REFS:palms}}

### Fern jungle (`main.jungle`)

**From the sheet:** jungle. Humid green shade with bright spots where the canopy opens. An overgrown stepped temple is the landmark. The roads here are safe from monsters.

**Palette**

{{PALETTE:jungle}}

**Light:** {{LIGHT:jungle}}.

**Warnings on this ground**

{{CHECK:jungle}}

**Ground, seen from the camera**
- Dark mud, as good a ground as today's (99-122%).
- **Fern cover is the second ground, and green keeps 64% on it.** In a sunlit patch green keeps 49% and yellow 51%.
- This is the most saturated of the nine boards (30% on average), and what is not mud is mostly green. The greens are dim and greyed for that reason, as in the Old forest.

**Props and the fade**
- **Broad-leafed trees and hanging vines:** they fade. Under a closed canopy most of the screen fades most of the time, as in the Old forest.
- **Giant ferns:** as ground cover they stay under about 1.2 m and never fade. Ferns taller than the character would have to fade or stand off the paths.
- **Rope-and-plank walkways:** at ground level, as the sheet says, so they fit the one-layer grid. They are the biome's road, and they are clear against mud and ferns (21-25).
- **Carved stone heads:** the tall ones fade.
- **Giant flowers:** the one warm accent, a dusty pink at 30%. It stays away from the red warning in strength, not in hue.
- **The stepped temple:** steps and terraces fit the heightmap, and terraces are joined by ramps and stairs (R-11.6-04). Whether players climb it, and whether it has an inside, is question 4.

**Settlement:** T11, a small open town in the medieval kit, overgrown with vines (`city-layouts.md`). The tile roof is 30 from the mud.

**Who lives here:** plants, with beasts and aberrations as the shared families; poison. Green plant monsters on fern cover have only their outline.

**Not drawn:** wood as a resource is Open (10.4 [M3]).

**Photo references**
{{REFS:jungle}}

### Reed marsh (`main.marsh`)

**From the sheet:** marsh and rivers, and C3's biome. Soft, misty morning light. A great willow on an island in the river is the landmark. The roads here are safe from monsters.

**Palette**

{{PALETTE:marsh}}

**Light:** {{LIGHT:marsh}}.

**Warnings on this ground**

{{CHECK:marsh}}

**Ground, seen from the camera**
- Wet earth, reed beds in straw and olive, dark shallow water and plank paths.
- **Wet earth is a good ground** (78-105%), and this is where new players of C3 start.
- **Straw reed beds are the weak ground:** yellow keeps 38% and red and green 50%.
- **Water** covers about a quarter of the screen (dark shallows and the river). The sheet does not say whether it is walkable (question 4). The pools of the Steam springs and the lake of Lake country are marked unwalkable.

**Reeds and the mist**
- **Real reeds are 2-3 m tall.** A walkable reed bed that tall hides the character completely. So a reed bed is either low (knee to waist), or a stand that fades, or not walkable (question 4).
- **Mist** is a layer over the ground, like the blown sand of Dune isle (question 3). The sheet keeps it soft.

**Props and the fade**
- **Plank walkways:** over wet ground and shallow water, 3 m wide as paths. Nothing walkable passes under them.
- **Stilt houses:** scenery, with nothing walkable under them.
- **Willows:** they fade. A willow's hanging crown hides more ground than its height suggests.
- **Reed bundles, fish traps:** low, never fade.
- **The great willow:** it fades. Its island needs a plank path or a ford.

**Settlement:** C3, the classic medieval city, with a moat fed by the river and a 5 m bridge at each of its four gates, and its tutorial area beside it. C3 gets its own board.

**Who lives here:** beasts (frogs, snakes), with plants as the shared family; water.

**Photo references**
{{REFS:marsh}}

### Ash woods (`main.ashwoods`)

**From the sheet:** a burnt forest. Grey daylight through thin smoke, with drifting ash. A burnt watchtower is the landmark. The roads here are safe from monsters.

**Palette**

{{PALETTE:ashwoods}}

**Light:** {{LIGHT:ashwoods}}.

**Warnings on this ground**

{{CHECK:ashwoods}}

**Ground, seen from the camera**
- Grey ash at 6% saturation, the most neutral ground on any board. Every warning keeps 60-72% on it, with no weak hue.
- Scorched earth is as good as today's ground (96-118%). Pale ash drifts are the weak spots (47-61%).
- **The road is hard to tell from the ash** (10).

**Embers and smoke**
- **Embers** are the one strong colour: small orange points at 59%, between the red and the yellow warning in hue. As points they cost little. A bed of embers as large as a warning would be read as one (question 5).
- **Drifting ash and thin smoke** are layers over the ground (question 3).
- **New green shoots** (39%) are the only green.

**Props and the fade**
- **Burnt trunks:** bare poles with no crown. They fade, but they hide little, so this forest fades far less than the Old forest or the jungle.
- **Charcoal trunks on grey ash** are a strong light-dark contrast, and it is in the props, not the ground.
- **Collapsed cabins, charred fences, ash drifts, smoking stumps:** low, never fade.
- **The burnt watchtower:** it fades.

**Settlement:** T9, a small open town in the medieval kit, on the road from C3 to M5. The tile roof is the only warm colour in sight (22 from the ash).

**Who lives here:** lesser demons, with undead as the shared family; fire and dark.

**Photo references**
{{REFS:ashwoods}}

### Steam springs (`main.springs`)

**From the sheet:** hot pools. Bright but diffuse light, with white steam. A large stepped terrace of pools is the landmark. The roads here are safe from monsters. There is no settlement.

**Palette**

{{PALETTE:springs}}

**Light:** {{LIGHT:springs}}.

**Warnings on this ground**

{{CHECK:springs}}

**Ground, seen from the camera**
- Pale mineral terraces in cream and soft pink, with milky pools (unwalkable) and dark rock.
- **The terraces are weak ground for red, yellow and green** (34-56%). Red keeps 41% on the pink terrace.
- **Dark rock is as good as today's ground** (92-119%), and it is 14% of the screen. Fighting places on dark rock would read well; on a terrace they would not.
- The pools are the only cool colour, at 17%.

**Terraces**
- A terrace step under 3 m is not a cliff (R-11.6-04), so each step is either a ramp of up to 30 degrees or a stair. A field of low steps needs many of them, or the steps are taller and fewer (question 4).
- The pools are unwalkable, so the walkable ground is the rims and the dry terraces between them.

**Things in the air**
- **Steam** is white, over pale ground, in diffuse light. It is visual only (R-2.3-10), and it lowers contrast where contrast is already low (question 3).

**Props and the fade**
- **Mineral columns:** the tall ones fade.
- **Wooden bathing decks:** walkable at ground level.
- **Steam vents, hot-spring plants:** low, never fade.

**Who lives here:** elementals (steam), with demons as the shared family; water and fire. A white steam elemental in white steam has only its outline.

**Photo references**
{{REFS:springs}}

### Snow taiga (`main.taiga`)

**From the sheet:** a snowy pine forest on a plateau. Cold and clear, with long blue-grey shadows. A frozen waterfall is the landmark. The roads here are dangerous.

**Palette**

{{PALETTE:taiga}}

**Light:** {{LIGHT:taiga}}.

**Warnings on this ground**

{{CHECK:taiga}}

**Ground, seen from the camera**
- Snow in the open and blue-grey snow in the shade of the pines.
- **All four warnings keep about half or less on snow** (37-59%), in the sun and in the shade. The uncoloured area almost disappears (6-20%); it is an M1 placeholder, as the W1 boards note.
- **Needle litter under the pines is the one good ground** (86-106%), at 5% of the screen.
- **The trampled road is close to the snow beside it** (12-15), and the roads here are dangerous.
- **Frozen streams and ponds:** drawn as walkable ground here, like the ice of the Rime coast. Thin ice as a hazard is Open (R-2.3-10).

**Props and the fade**
- **Snow-laden pines:** they fade. From above a pine is a dark green disc under snow, and in a dense stand most of the screen fades.
- **Log piles, fur racks:** low ones never fade.
- **Hunters' cabins:** scenery, unless one is a service building.
- **The frozen waterfall:** a wall of ice on a cliff. It is seen from the side the camera looks at, and it fades from the other.

**Settlement:** M5, a medium city with a palisade and three gates at the east end of the north bridge, in the medieval kit. See the snow-on-roofs finding above.

**Who lives here:** beasts (wolves, bears), with humanoids (hunter clans) as the shared family; ice. A grey wolf on blue-grey snow has little contrast besides its outline.

**Not drawn:** wood as a resource is Open (10.4 [M3]).

**Photo references**
{{REFS:taiga}}

### Frost peaks (`main.frostpeaks`)

**From the sheet:** the second ice biome and the largest biome on the map. Harsh high-altitude light with blowing snow. The twin peaks, with a pass between them, are the landmark. The roads here are dangerous.

**Palette**

{{PALETTE:frostpeaks}}

**Light:** {{LIGHT:frostpeaks}}.

**Warnings on this ground**

{{CHECK:frostpeaks}}

**Ground, seen from the camera**
- Deep snow is the lightest ground on any board (L 94). All four warnings keep 37-48% on it.
- **Bare grey rock is the better ground** (70-85%), at 14% of the screen. Wind-swept rock is where a fight reads.
- **Glacier ice** has a slight cyan tint (11%), as the sheet asks. It is no better than snow (42-48%).
- **The scree path is the clearest road of the nine** (35 against snow).

**Things in the air**
- **Blowing snow** is white over white ground. It is visual only (R-2.3-10) and lies over warnings (question 3).

**Peaks, walls and the fade**
- The camera never shows the horizon, so **the twin peaks are never seen as peaks**. On screen the pass is a path between two cliff walls.
- A wall on the camera side hides the player within a distance equal to its height (the 45-degree guideline of `art-direction.md`). In a pass narrower than its walls are tall, one wall is always in the way. This is the canyon question of the W2 boards again (question 4 here).
- **Rock spires and ice walls:** tall, so they fade, or they are cliffs.
- **Prayer flags:** a line of flags over a path is between the camera and the player, so it fades or hangs beside the path. Real prayer flags are blue, white, red, green and yellow, which are the four warning hues. The swatch is a faded one.
- **Mountain shelters, frozen remains:** low ones never fade.

**Large monsters:** frost wyrms are the signature family, and large monsters need open ground with no gap under about 4 m (R-2.2-08). That is the snowfields and the glacier, the weakest grounds for warnings.

**Settlement:** T7, a small open town in the medieval kit. See the snow-on-roofs finding above.

**Who lives here:** dragons (frost wyrms), with elementals as the shared family; ice and wind. White creatures on snow have only their outline.

**Photo references**
{{REFS:frostpeaks}}

### Storm cliffs (`main.cliffs`)

**From the sheet:** basalt sea cliffs, the smallest biome on the map. A dark storm sky mood: low grey light with flashes (visual only). A field of hexagonal basalt columns stepping down to the sea is the landmark. The roads here are dangerous.

**Palette**

{{PALETTE:cliffs}}

**Light:** {{LIGHT:cliffs}}.

**Warnings on this ground**

{{CHECK:cliffs}}

**Ground, seen from the camera**
- Black-grey basalt and wet stone, with sparse sea grass. With the Fungal hollow this is the best ground for warnings (95-142% on basalt and wet stone).
- **The whole biome is dark and grey.** Dark creatures on basalt have only their outline, and the light does not help.
- **White surf** is the lightest thing on the board. It is below the cliffs, on water.

**Flashes**
- The sheet's flashes are visual only. A flash that lights the whole screen changes every ground colour for a moment, warnings included (question 5).
- The light preset is fixed (R-9.14-04); a flash is an effect on top of it.

**Columns, cliffs and the fade**
- **Column tops as ground:** a field of columns at different heights is a field of steps. As in the Steam springs, steps under 3 m have to be ramps or stairs (question 4).
- **The sea cliff:** the camera looks down a cliff on its far side and through it on its near side. A cliff edge needs blockers.
- **Tall columns and wrecked masts:** they fade.
- **Chains, anchors, nests:** low, never fade.
- **Signal braziers:** the same orange as the Ash woods' embers, as points of light on dark ground.

**Settlement:** T8, a small open town in the medieval kit. Grey stone on grey basalt; the tile roof is the warmest thing in the biome (37 from the basalt).

**Who lives here:** elementals (storm), with aberrations (things from the sea) as the shared family; lightning and wind.

**Photo references**
{{REFS:cliffs}}

## The three bridges

W3 builds the three bridges (`world-map.md` D8): north (M3 - M5, about 133 m), south-west (T5 - T6, about 138 m) and south-east (M4 - T10, about 146 m).

- **A bridge is 5 m wide** (R-11.6-03), and the screen is about 20 m wide at its near edge and 42 m at its far edge (the table "What the camera shows" in `art-direction.md`, from placeholder camera values that are Open in 10.4). So a bridge is a quarter of the screen's width at most, as long as those values hold, and **the rest of the picture is sea** for the 33-37 seconds a crossing takes.
- **Railings** are blockers outside the 5 m. Up to about 1.2 m they never fade.
- **Towers, arches and gatehouses** on a bridge fade when they hide the player. An arch over the deck is over the player, like a gate passage.
- **Whose kit is each bridge?** The north bridge joins a water-kit city and a medieval-kit city. The south-east bridge joins a bazaar-kit city and a medieval-kit town. Only the south-west bridge has the bazaar kit at both ends (question 7).
- **The bridges are the map's ambush spots.** A fight on a bridge happens on the deck's colour, which no board has yet.
- Nothing passes under the bridges, so they fit the one-layer grid (`world-map.md`).

## The cliff between Palm coast and Snow taiga

The one border with no blend (R-2.3-11, decision C5): a cliff wall with ramps, with the taiga on a plateau above the coast.

- **A ramp is where the two biomes meet.** At 30 degrees a ramp up a 3 m cliff is about 5 m long, and up a 6 m cliff about 10 m. Every other border blends ground, props and light over 20-30 m.
- **The light changes there too.** The light presets blend over the same distance as the ground (R-2.3-11), and here there is no blend. The designer's answer (question 8): the light blends over the length of each ramp.
- **The largest jump on the map:** pale sand under warm light below (L 82), snow under cold light above (L 92). Both grounds are weak for warnings, so the ramps are a poor place for a fight to read.
- **The cliff and the camera:** seen from the coast with the camera looking toward the taiga, the cliff face is a wall across the screen. Seen from the plateau, the edge is a line with the coast below it.

## Borders of W3

From `biome-sheets.md` (proposals, except palms - taiga), with a 20-30 m blend of ground, props and light (R-2.3-11):

| Border | Natural edge | What blends |
|---|---|---|
| canyons - dune isle | the south-west bridge | none: the sea is between them |
| dune isle - palms | the south-east bridge | none: the sea is between them |
| rime - taiga | the north bridge | none: the sea is between them |
| palms - taiga | **a cliff wall with ramps and no blend** (decided, C5) | nothing; see above |
| palms - jungle | the undergrowth thickens; a stream | pale sand to dark mud; bright light to green shade |
| palms - marsh | the river mouth; sand turns to mud | pale sand to wet earth; clear light to mist |
| palms - ash woods | a river | pale sand to grey ash; warm light to smoke |
| jungle - marsh | the ground gets wetter; the first plank paths | dark mud to wet earth; canopy to reeds |
| marsh - ash woods | the river | wet earth to grey ash; mist to smoke |
| marsh - springs | warm streams running down into the marsh | wet earth to pale terraces; mist to steam |
| ash woods - springs | steam vents among the burnt trunks | grey ash to pale terraces; smoke to steam |
| ash woods - taiga | the edge of the old fire: burnt trunks among snowy pines | grey ash to snow |
| taiga - frost peaks | the tree line; the ground rises | snow to deeper snow; pines to bare rock |
| taiga - cliffs | pines thin onto bare basalt | snow to black-grey basalt |
| frost peaks - cliffs | the mountains fall to the sea | snow to black-grey basalt |

- **The largest jumps in lightness** are from the snow of the taiga and the Frost peaks to the basalt of the Storm cliffs (L 92-94 to L 27), and from the Palm coast's sand to the jungle's mud (L 82 to L 28). The warnings change strength across those blends.
- **Mist, smoke and steam meet** where the marsh, the Ash woods and the Steam springs touch. The three air layers have to stay distinct over a short distance.
- The four borders with the Ember caldera (ash woods, springs, taiga, cliffs) belong to W4.

## Photo references: how they were picked

As on the W1 and W2 boards: free-licence files on Wikimedia Commons, found with the Commons search, each looked at before it was listed, each with the author and licence that Commons states. They are **links only**. Nothing is stored in the repository or used in the game, so `THIRD_PARTY_LICENSES.md` has no new row.

- Some photos are far more saturated than the boards (tropical water, glacier ice, the giant flower). The notes say where; the palettes are the target, not the photos.
- The search found nothing worth listing for the burnt cabins and watchtower of the Ash woods or the signal braziers of the Storm cliffs.

## Questions for the designer, and the answers

The designer answered these on 2026-11-10. The questions stand as they were asked, so that the references to them in this file still hold. The full list is `reference-boards-questions.md`, and the "Q" numbers refer to it.

1. **Warnings on light ground** (question 2 of the W1 boards, question 1 of the W2 boards): the table of all 19 biomes shows eight with a main ground where red and yellow keep under half, five of them in W3. Does the table change the answer?
   - **Answer (Q1):** the warning changes: it gets a dark edge or a stronger fill on light ground. The palettes stay. Which of the two is not decided yet.
2. **Roads in snow, ash and sand:** the road colour is 3-15 from the ground in four W3 biomes, two of them with dangerous roads. What makes a road "clearly drawn" (R-9.14-07) there: poles, cairns, ruts, a darker centre?
   - **Answer (Q28):** posts or poles, and edge stones or cairns.
3. **Things in the air:** blown sand, mist, smoke and drifting ash, steam, and blowing snow lie between the camera and the ground in five W3 biomes. Are warnings drawn above them, and how dense may they be?
   - **Answer (Q8):** attack warnings are drawn above the air effects. A density limit was not chosen.
4. **One walkable layer and the fade, in five places:** are reed beds walkable, and how tall are they? Is shallow marsh water walkable? Are the terrace steps of the Steam springs and the column steps of the Storm cliffs ramps, stairs or cliffs? Do players climb the stepped temple, and does it have an inside? Is the pass between the twin peaks wider than its walls are tall?
   - **Answer (Q16):** reed beds are low (knee to waist), walkable and never fade. Shallow marsh water is unwalkable.
   - **Answer (Q15):** the terraces, the columns and the outside of the temple are low steps, walkable as stairs or as ramps of up to 30 degrees.
   - **Answer (Q13):** the temple is a walk-in place whose roof hides like a building's roof. R-11.6-05 needs an exception for it.
   - **Answer (Q12):** the pass is wider than its walls are tall, and the walls stay plain terrain.
5. **Fire and flashes:** may embers and braziers be as strong as 59%, between the red and the yellow warning in hue? How strong may a lightning flash be while warnings are on the ground?
   - **Answer (Q7):** fires stay at that strength as small points only. A flash is kept weak enough that warnings stay readable.
6. **Snow on roofs:** in the Snow taiga and the Frost peaks, does snow cover the tile that marks the medieval kit, or does the tile stay visible (also question 4 of the W1 boards)?
   - **Answer (Q18):** snow lies on roofs in patches, so the tile still shows on every building.
7. **The bridges:** which kit builds each bridge, and what colour is a bridge deck?
   - **Answer (Q26):** a look of their own: one neutral style of stone and timber on all three bridges, with a deck dark enough for warnings.
8. **The cliff between Palm coast and Snow taiga:** where does the light change, given that this border has no blend?
   - **Answer (Q30):** over the ramp. The light blends over the length of each ramp, 5-10 m.
9. **The review of the nine sheets** (10.4) is still open, and these boards follow the sheets as written.
   - **Answer (Q31):** accepted, with these answers.

## Not covered

- C3, the classic medieval city, and the detailed dressing of M4, M5 and T6-T11.
- The Ember caldera and M6 (W4).
- Characters, monsters, UI and the world map.
- How the colours behave in the engine. The swatches are targets, and the cel shader and the light presets have to reach them.
