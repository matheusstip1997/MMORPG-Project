# Reference boards: build step W3 (C3, the island and the east continent)

Status: **art reference, v2 (2026-11-10)**, updated with the designer's answers of 2026-11-10. Nine biome boards for the third build step of the main map (`world-map.md` W3): Dune isle, Palm coast, Fern jungle, Reed marsh, Ash woods, Steam springs, Snow taiga, Frost peaks and Storm cliffs. **Every colour, material note and photo choice here is a proposal** for the designer to review. The drawing is `reference-boards-w3.svg` (same folder).

This file is **not the spec**. It follows the art rules of spec 9.14 and 11.6 and the sheets in `biome-sheets.md`, which the designer accepted on 2026-11-10 together with the answers at the end of this file. **The designer answered this file's questions on that day.** Where a paragraph below calls a point open and cites a question, the answer under that question settles it. The colours themselves stay proposals.

The boards are built and checked the same way as `reference-boards-w1.md` and `reference-boards-w2.md`: see "What a board holds" and "How the palettes were checked" in the first. In short, the swatches are on-screen colours in the biome's light, saturation is the S of HSV, and the warning numbers are colour differences (CIE76 Delta E; higher is easier to see) between the bare ground and the ground under each warning, with a percentage against today's M1 greybox ground `#4b554b`. They compare flat colours and show where to look first; they are not a pass or fail.

New on these boards: a table of **every biome board so far** (19 of the 20 biomes), and notes on the two things W3 adds to the map besides biomes: the three bridges and the cliff wall between Palm coast and Snow taiga.

## Findings across the nine boards

### Saturation

| Biome | Average by share | Highest ground swatch | Highest swatch |
|---|---|---|---|
| Dune isle | 22% | 26% (Wet sand) | 34% (Boat timber, crates) |
| Palm coast | 28% | 31% (Low undergrowth) | 38% (Thatch) |
| Fern jungle | 30% | 36% (Wet leaf litter) | 36% (Wet leaf litter) |
| Reed marsh | 27% | 34% (Reed bed (straw)) | 34% (Reed bed (straw)) |
| Ash woods | 10% | 13% (Scorched earth) | 59% (Ember) |
| Steam springs | 18% | 19% (Terrace (soft pink)) | 50% (Mineral stain (rust)) |
| Snow taiga | 15% | 26% (Needle litter) | 35% (Trunk) |
| Frost peaks | 9% | 13% (Packed snow (shade)) | 41% (Prayer flag (faded)) |
| Storm cliffs | 16% | 25% (Sea grass) | 59% (Brazier fire) |

- All nine stay below the least saturated warning colour (green, 62%).
- **This is the least saturated set so far:** five of the nine average under 20%, and the grounds of the snow, ash and basalt biomes are close to grey.
- **The two fires are the most saturated swatches** (59%): the embers of the Ash woods and the signal braziers of the Storm cliffs. They are small glowing points, and their orange lies between the red and the yellow warning in hue (question 5).

### Warnings on each ground

The reference row for every board:

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| M1 greybox ground (today) | `#4b554b` | 39 | 43 | 42 | 36 | 17 |

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
| Fungal hollow | Spongy dark soil `#3A3438` | 22 | 112% | 128% | 102% | 141% |
| Old forest | Dark earth `#3F3A33` | 25 | 108% | 117% | 112% | 128% |
| Storm cliffs | Black-grey basalt `#3E4044` | 27 | 108% | 120% | 95% | 128% |
| Fern jungle | Dark mud `#4A4036` | 28 | 99% | 109% | 113% | 122% |
| Reed marsh | Wet earth `#6A5E4C` | 41 | 78% | 83% | 105% | 95% |
| Bone canyons | Gravel floor (shadow) `#766E6E` | 47 | 70% | 80% | 79% | 92% |
| Amber woods | Leaf litter (rust) `#94694E` | 48 | 52% | 65% | 113% | 93% |
| Heather moor | Heather `#8A7A8C` | 53 | 59% | 76% | 60% | 92% |
| Lake country | Wet grass `#76865F` | 54 | 76% | 63% | 103% | 57% |
| Green vale | Short grass (olive) `#8A8E60` | 58 | 66% | 53% | 105% | 55% |
| Ash woods | Grey ash `#8E8B86` | 58 | 60% | 63% | 71% | 72% |
| Red sands | Sand (dusty tan) `#C4A886` | 70 | 44% | 39% | 85% | 56% |
| Dry steppe | Pale dry grass `#C2B88C` | 75 | 48% | 35% | 84% | 46% |
| Dune isle | Cream sand `#D9CBAA` | 82 | 43% | 33% | 71% | 45% |
| Palm coast | Pale sand `#DDCBA4` | 82 | 43% | 31% | 75% | 45% |
| Steam springs | Terrace (cream) `#DDD2BC` | 85 | 43% | 34% | 63% | 46% |
| Rime coast | Packed snow `#E3E6E7` | 91 | 44% | 37% | 49% | 47% |
| Snow taiga | Fresh snow `#E4E8EA` | 92 | 44% | 37% | 48% | 47% |
| Frost peaks | Deep snow `#ECEFF1` | 94 | 43% | 37% | 48% | 46% |

- **Lightness explains most of it.** Below about L 30 every warning is about as clear as today or clearer (95-141%). From about L 70 red and yellow are under half.
- **Eight of the 19 biomes** have a main ground where red and yellow both keep under half: Red sands, Dry steppe, Dune isle, Palm coast, Steam springs, Rime coast, Snow taiga and Frost peaks. Two of the three start biomes are not among them; Red sands is.
- **Hue matters in the middle:** red is weakest for its lightness on the Amber woods' rust litter (52%), and green on the grass of the Green vale and Lake country (55-57%).
- This table is the short form of question 2 of the W1 boards, which is still open (question 1 here).

### Colour vision

The closest pair of the four warnings on each biome's main ground, by colour alone (simulation as on the W1 boards):

| Biome | Main ground | Normal | Protan | Deutan | Tritan |
|---|---|---|---|---|---|
| M1 greybox ground (today) | `#4b554b` | 38 (red-yellow) | 15 (yellow-green) | 9 (red-green) | 13 (blue-green) |
| Dune isle | Cream sand | 18 (red-yellow) | 7 (yellow-green) | 3 (red-green) | 7 (blue-green) |
| Palm coast | Pale sand | 18 (red-yellow) | 7 (yellow-green) | 3 (red-green) | 7 (blue-green) |
| Fern jungle | Dark mud | 41 (yellow-green) | 17 (yellow-green) | 11 (red-green) | 14 (blue-green) |
| Reed marsh | Wet earth | 35 (red-yellow) | 15 (yellow-green) | 9 (red-green) | 12 (blue-green) |
| Ash woods | Grey ash | 27 (red-yellow) | 10 (yellow-green) | 5 (red-green) | 10 (blue-green) |
| Steam springs | Terrace (cream) | 18 (red-yellow) | 7 (yellow-green) | 3 (red-green) | 7 (blue-green) |
| Snow taiga | Fresh snow | 16 (red-yellow) | 5 (yellow-green) | 2 (red-green) | 6 (blue-green) |
| Frost peaks | Deep snow | 15 (red-yellow) | 5 (yellow-green) | 2 (red-green) | 6 (blue-green) |
| Storm cliffs | Black-grey basalt | 41 (yellow-green) | 16 (yellow-green) | 10 (red-green) | 13 (blue-green) |

- On the dark grounds the pairs are as far apart as today or a little further. On sand, terraces and snow they are at about half of today's distance or less, as on the Rime coast. Each warning has its own shape as well (R-3.3-04, spec 9.3).

### Roads against the ground

R-9.14-07 asks for roads clearly drawn into the ground. The difference between each board's road swatch and the two largest grounds beside it:

| Biome | Road | Against the main ground | Against the second ground |
|---|---|---|---|
| Dune isle | Packed sand road `#BFAE8E` | 10 (Cream sand) | 3 (Dune in shade) |
| Palm coast | Sandy road `#C2AC88` | 11 (Pale sand) | 23 (Low undergrowth) |
| Fern jungle | Plank walkway `#8A7A62` | 25 (Dark mud) | 21 (Fern cover) |
| Reed marsh | Causeway road `#8E8676` | 16 (Wet earth) | 21 (Reed bed (straw)) |
| Ash woods | Trodden road `#77706A` | 10 (Grey ash) | 17 (Scorched earth) |
| Steam springs | Worn mineral path `#B5A894` | 15 (Terrace (cream)) | 40 (Dark rock) |
| Snow taiga | Trampled snow road `#BFC0BC` | 15 (Fresh snow) | 12 (Snow in pine shade) |
| Frost peaks | Scree path `#8E9092` | 35 (Deep snow) | 19 (Packed snow (shade)) |
| Storm cliffs | Worn road `#66625C` | 16 (Black-grey basalt) | 23 (Wet stone) |

- **Sand, ash and snow hide a road:** 3-11 on Dune isle and Palm coast, 10 in the Ash woods, 12-15 in the Snow taiga. A trampled road in snow, ash or sand is the same material as the ground.
- **Dune isle and Snow taiga have dangerous roads** (`open-world-content.md` F1), so these are roads a player has to follow while watching for camps.
- Built or bare roads are clear: the jungle's plank walkway (21-25), the marsh's causeway (16-21), the scree path of the Frost peaks against snow (19-35).
- This is question 2 of the W2 boards again, now for snow and ash as well (question 2 here).

### Kit roofs against the ground

W3 uses the medieval kit everywhere except on Dune isle, which uses the bazaar kit (`city-layouts.md` E7). The drawing uses these colours: medieval kit: warm tile roof `#A27460`, medieval kit: grey stone `#8C8A84`, medieval kit: timber frame `#5E4B3E`, bazaar kit: white dome `#F0ECE2`. `city-layouts.md` names the materials ("grey stone, timber frames, tiled roofs", "warm tile"); the hex values are this board's proposal.

| Biome | Settlement and kit | Warm tile roof | White dome |
|---|---|---|---|
| Dune isle | M4, T6: bazaar kit | 33 main, 17 closest (Wet sand) | 17 main, 9 closest (Shell beach) |
| Palm coast | T10: medieval kit | 33 main, 15 closest (Dark wet sand) | 20 main, 20 closest (Pale sand) |
| Fern jungle | T11: medieval kit | 30 main, 14 closest (Plank walkway) | 66 main, 42 closest (Sunlit patch) |
| Reed marsh | C3: medieval kit | 20 main, 13 closest (Plank path) | 53 main, 33 closest (Reed bed (straw)) |
| Ash woods | T9: medieval kit | 22 main, 20 closest (Trodden road) | 36 main, 21 closest (Pale ash drift) |
| Steam springs | no settlement | - | - |
| Snow taiga | M5: medieval kit | 46 main, 23 closest (Needle litter) | 7 main, 7 closest (Fresh snow) |
| Frost peaks | T7: medieval kit | 48 main, 26 closest (Scree path) | 7 main, 7 closest (Deep snow) |
| Storm cliffs | T8: medieval kit | 37 main, 24 closest (Worn road) | 67 main, 46 closest (Sea grass) |

- **The warm tile roof reads on every W3 ground** (20-48 against the main ground). It is weakest where C3 stands: 20 against the marsh's wet earth and 13 against a plank path.
- **The dome on the palest sand:** the designer asked for a whiter dome (question 6 of the W2 boards). On Dune isle the white dome is 17 from the cream sand and 9 from a shell beach; the earlier pale cream dome was at 7 and 4. M4 and T6 are still the bazaar-kit settlements where the dome reads least (the Red sands are at 29).
- **Snow on the roofs:** `city-layouts.md` gives "snow on the roofs" as the example of biome dressing. In the Snow taiga and the Frost peaks that covers the tile, the one colour that marks the kit from above. The designer's answer (question 6): snow lies in patches, so the tile still shows on every building.
- **A warm roof is a muted red-orange.** A town of tile roofs is a large area close to the red warning's hue; the C3 board will have to check it.

## The boards

### Dune isle (`main.duneisle`)

**From the sheet:** the small desert, on the island between the two south bridges. Bright sea light with wind-blown sand in the air. A lighthouse at the island's south end is the landmark. The roads here are dangerous (`open-world-content.md` F1).

**Palette**

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Cream sand | `#D9CBAA` | ground | 38% | 22% | 85% |
| Dune in shade | `#B9AA8E` | ground | 14% | 23% | 73% |
| Shell beach | `#DAD3C4` | ground | 8% | 10% | 85% |
| Wet sand | `#A39478` | ground | 6% | 26% | 64% |
| Packed sand road | `#BFAE8E` | ground | 6% | 26% | 75% |
| Dune grass | `#A3A474` | prop | 10% | 29% | 64% |
| Shallow sea | `#6E979A` | water | 8% | 29% | 60% |
| Bleached driftwood | `#B5AC9E` | prop | 5% | 13% | 71% |
| Boat timber, crates | `#77624E` | prop | 3% | 34% | 47% |
| Lighthouse white | `#E8E4DA` | accent | 2% | 6% | 91% |

**Light:** Sun `#FFF6E0`, Shadow tint `#8E9AAC`, Blown sand `#E6DCC6`.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Cream sand | `#D9CBAA` | 17 (43%) | 14 (33%) | 30 (71%) | 16 (45%) | 4 (24%) |
| Dune in shade | `#B9AA8E` | 19 (48%) | 18 (42%) | 33 (78%) | 19 (54%) | 6 (33%) |
| Shell beach | `#DAD3C4` | 17 (43%) | 15 (36%) | 25 (59%) | 17 (47%) | 2 (10%) |
| Wet sand | `#A39478` | 21 (53%) | 21 (50%) | 36 (87%) | 22 (62%) | 8 (47%) |
| Packed sand road | `#BFAE8E` | 18 (46%) | 17 (40%) | 33 (80%) | 19 (52%) | 6 (35%) |

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
- [Sand ripples and dune grass, Ameland](https://commons.wikimedia.org/wiki/File:Sand_rippels_and_dune_grass,_Ameland_island,_Netherlands.jpg): pale rippled sand with dune grass: the ground. Hartmut Schmidt Heidelberg, CC BY-SA 4.0.
- [Marram Grass, Dawlish Warren](https://commons.wikimedia.org/wiki/File:Marram_Grass,_Dawlish_Warren.jpg): dune grass holding a dune. Partonez, CC BY-SA 4.0.
- [Sand ripples](https://commons.wikimedia.org/wiki/File:Sand_ripples_-_geograph.org.uk_-_2535979.jpg): ripples at the foot of a grassy dune. Jonathan Wilkins, CC BY-SA 2.0.
- [Oyster shells on Whitstable beach](https://commons.wikimedia.org/wiki/File:Oyster_shells_on_Whitstable_beach.jpg): a shell beach close up. Y.ssk, CC BY-SA 4.0.
- [Fence on the beach](https://commons.wikimedia.org/wiki/File:Fence_on_the_beach_-_geograph.org.uk_-_736510.jpg): a sand fence half buried. David Lally, CC BY-SA 2.0.
- [Bleached driftwood tree, Agate Beach](https://commons.wikimedia.org/wiki/File:Bleached_driftwood_tree_below_the_bluff,_Agate_Beach,_Sue-meg_State_Park,_California.jpg): bleached driftwood. Z3lvs, CC0.
- [Wreck on the beach at Sailean a' Gharbh-uisge](https://commons.wikimedia.org/wiki/File:Wreck_on_the_beach_at_Sailean_a%27_Gharbh-uisge_-_geograph.org.uk_-_1340898.jpg): the ribs of a wooden wreck on the shore. Nick Wilson, CC BY-SA 2.0.
- [Sand dunes and Spurn High Lighthouse](https://commons.wikimedia.org/wiki/File:Sand_dunes_and_Spurn_High_Lighthouse_-_geograph.org.uk_-_3203205.jpg): a lighthouse behind dune grass: the landmark. Jonathan Thacker, CC BY-SA 2.0.

### Palm coast (`main.palms`)

**From the sheet:** a tropical shore. Warm and bright, with soft shadows. A shipwreck cove is the landmark. The roads here are safe from monsters.

**Palette**

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Pale sand | `#DDCBA4` | ground | 30% | 26% | 87% |
| Low undergrowth | `#78895E` | ground | 14% | 31% | 54% |
| Dark wet sand | `#9C8B72` | ground | 10% | 27% | 61% |
| Sandy road | `#C2AC88` | ground | 6% | 30% | 76% |
| Turquoise shallows | `#7FB0A8` | water | 14% | 28% | 69% |
| Deeper sea | `#5C8A92` | water | 6% | 37% | 57% |
| Palm frond | `#6F8558` | prop | 8% | 34% | 52% |
| Coral rock | `#B7A999` | prop | 6% | 16% | 72% |
| Palm trunk, hut timber | `#84705A` | prop | 4% | 32% | 52% |
| Thatch | `#B6A070` | accent | 2% | 38% | 71% |

**Light:** Sun `#FFF3D6`, Shadow tint `#8A94A0`, Haze `#DDE6DC`.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Pale sand | `#DDCBA4` | 17 (43%) | 13 (31%) | 31 (75%) | 16 (45%) | 5 (29%) |
| Low undergrowth | `#78895E` | 29 (75%) | 26 (60%) | 44 (105%) | 19 (54%) | 12 (74%) |
| Dark wet sand | `#9C8B72` | 21 (55%) | 23 (55%) | 37 (89%) | 24 (68%) | 9 (51%) |
| Sandy road | `#C2AC88` | 18 (45%) | 16 (38%) | 35 (84%) | 19 (53%) | 7 (40%) |

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
- [Koh Mak (island), Thailand, Palm trees on the beach](https://commons.wikimedia.org/wiki/File:Koh_Mak_%28island%29,_Thailand,_Palm_trees_on_the_beach.jpg): palms, pale sand and soft shallows. Vyacheslav Argenberg, CC BY 4.0.
- [Koh Mak (island), Thailand, Palms on the beach](https://commons.wikimedia.org/wiki/File:Koh_Mak_%28island%29,_Thailand,_Palms_on_the_beach.jpg): palms leaning over the beach: thin trunks, high crowns. Vyacheslav Argenberg, CC BY 4.0.
- [An empty beach at dawn](https://commons.wikimedia.org/wiki/File:DSC_6671_A_tranquil_empty_beach_at_dawn_with_gentle_waves_lapping_the_shore_under_a_cloudy_sky.jpg): dark wet sand at the waterline. PattayaPatrol, CC BY-SA 4.0.
- [Bora Bora from the air](https://commons.wikimedia.org/wiki/File:Boraboraluft_gerade.jpg): shallows from above; the board's turquoise is softer. Makemake at de.wikipedia, CC BY-SA 3.0.
- [Traditional Ngalawa outrigger canoe on the sand at Diani Beach](https://commons.wikimedia.org/wiki/File:Traditional_Ngalawa_outrigger_canoe_on_the_sand_at_Diani_Beach.jpg): a beached outrigger canoe. Lebu Ayiga, CC BY-SA 4.0.
- [Fisherman sea hut](https://commons.wikimedia.org/wiki/File:Fisherman_sea_hut_%285619123723%29.jpg): a fishing hut on stilts. Andrew Kudrin, CC BY 2.0.
- [Navagio Beach Shipwreck](https://commons.wikimedia.org/wiki/File:Navagio_Beach_Shipwreck.JPG): a wreck on the sand of a cove: the landmark. Badgernet, CC BY-SA 3.0.
- [Navagio, Zante 01](https://commons.wikimedia.org/wiki/File:Navagio,_Zante_01.jpg): the same cove from above; the water is far stronger than the board's. Wilnel José Verdú Guerrero, CC BY-SA 4.0.

### Fern jungle (`main.jungle`)

**From the sheet:** jungle. Humid green shade with bright spots where the canopy opens. An overgrown stepped temple is the landmark. The roads here are safe from monsters.

**Palette**

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Dark mud | `#4A4036` | ground | 26% | 27% | 29% |
| Fern cover | `#587048` | ground | 20% | 36% | 44% |
| Wet leaf litter | `#6A5C44` | ground | 8% | 36% | 42% |
| Plank walkway | `#8A7A62` | ground | 6% | 29% | 54% |
| Sunlit patch | `#8A9460` | ground | 4% | 35% | 58% |
| Broad-leaf canopy | `#3F5A3C` | prop | 16% | 33% | 35% |
| Moss-covered stone | `#6E7862` | prop | 8% | 18% | 47% |
| Vines and trunks | `#54483A` | prop | 6% | 31% | 33% |
| Giant flower (pink) | `#B8808A` | accent | 3% | 30% | 72% |
| Carved stone (bare) | `#8E8C7E` | accent | 3% | 11% | 56% |

**Light:** Sun spot `#F4EFC4`, Shadow tint `#4E6654`, Humid haze `#7C8C78`.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Dark mud | `#4A4036` | 39 (99%) | 46 (109%) | 47 (113%) | 44 (122%) | 21 (125%) |
| Fern cover | `#587048` | 37 (95%) | 33 (77%) | 49 (118%) | 23 (64%) | 17 (101%) |
| Wet leaf litter | `#6A5C44` | 31 (78%) | 35 (81%) | 47 (113%) | 34 (95%) | 16 (97%) |
| Plank walkway | `#8A7A62` | 24 (62%) | 27 (63%) | 40 (96%) | 28 (76%) | 11 (64%) |
| Sunlit patch | `#8A9460` | 26 (68%) | 22 (51%) | 45 (107%) | 18 (49%) | 12 (72%) |

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
- [Rainforest debris after the tree falls](https://commons.wikimedia.org/wiki/File:Rainforest_debris_after_the_tree_falls_%283455830090%29.jpg): the forest floor: mud, litter and fallen wood. Tatters ✾, CC BY 2.0.
- [Ferns, Moss, Vines Dominica Rainforest 6395](https://commons.wikimedia.org/wiki/File:Ferns,_Moss,_Vines_Dominica_Rainforest_6395.jpg): ferns, moss and vines: the fern cover. Paul Harrison, CC BY-SA 4.0.
- [Muddy rainforest trail, Mentawai Islands](https://commons.wikimedia.org/wiki/File:Muddy_rainforest_trail,_Mentawai_Islands.jpg): a muddy trail: the dark mud. Andrian salis, CC BY-SA 4.0.
- [Boardwalk, Quinault Rainforest](https://commons.wikimedia.org/wiki/File:Boardwalk,_Quinault_Rainforest_%283604346019%29.jpg): a plank walkway at ground level. KimonBerlin, CC BY-SA 2.0.
- [Entwined tree, Kbal Spean](https://commons.wikimedia.org/wiki/File:Kbal_Spean_-_003_Entwined_Tree_%288584728986%29.jpg): thick hanging vines. Photo Dharma, CC BY 2.0.
- [Colossal head, La Venta park, Villahermosa](https://commons.wikimedia.org/wiki/File:Villahermosa,_Parque-Museo_La_Venta,_Colossal_Head_%2820686566115%29.jpg): a carved stone head among trees. Arian Zwegers, CC BY 2.0.
- [Calakmul, Structure I](https://commons.wikimedia.org/wiki/File:Calakmul,_Structure_I_%2814386283483%29.jpg): a stepped temple with trees growing on it: the landmark. Arian Zwegers, CC BY 2.0.
- [Rafflesia arnoldii, Batang Palupuah](https://commons.wikimedia.org/wiki/File:Rafflesia_Arnoldii_Batang_Palupuah_Indonesia.jpg): a giant flower; the real one is red, the board's is a dusty pink. Maizal Chaniago, CC BY-SA 4.0.

### Reed marsh (`main.marsh`)

**From the sheet:** marsh and rivers, and C3's biome. Soft, misty morning light. A great willow on an island in the river is the landmark. The roads here are safe from monsters.

**Palette**

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Wet earth | `#6A5E4C` | ground | 22% | 28% | 42% |
| Reed bed (straw) | `#B5A878` | ground | 16% | 34% | 71% |
| Reed bed (olive) | `#868A5E` | ground | 14% | 32% | 54% |
| Plank path | `#93826A` | ground | 6% | 28% | 58% |
| Causeway road | `#8E8676` | ground | 5% | 17% | 56% |
| Dark shallow water | `#4E5E58` | water | 18% | 17% | 37% |
| Open river | `#62787A` | water | 5% | 20% | 48% |
| Willow leaf | `#8A9668` | prop | 6% | 31% | 59% |
| Stilt-house timber | `#6E5E4E` | prop | 5% | 29% | 43% |
| Reed bundle, thatch | `#C6B88C` | accent | 3% | 29% | 78% |

**Light:** Soft sun `#F6EEDC`, Shadow tint `#7E8C8C`, Mist `#CFD6CE`.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Wet earth | `#6A5E4C` | 31 (78%) | 35 (83%) | 44 (105%) | 34 (95%) | 15 (88%) |
| Reed bed (straw) | `#B5A878` | 20 (50%) | 16 (38%) | 39 (94%) | 18 (50%) | 9 (52%) |
| Reed bed (olive) | `#868A5E` | 26 (68%) | 24 (55%) | 44 (106%) | 21 (57%) | 12 (71%) |
| Plank path | `#93826A` | 23 (58%) | 25 (59%) | 39 (92%) | 26 (72%) | 10 (57%) |
| Causeway road | `#8E8676` | 24 (61%) | 26 (62%) | 35 (83%) | 26 (71%) | 8 (48%) |

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
- [Reed cutters' tea time in the marshes](https://commons.wikimedia.org/wiki/File:Reed_cutters%27_tea_time_in_the_marshes_-_geograph.org.uk_-_1803210.jpg): a straw reed bed at the water's edge. Evelyn Simak, CC BY-SA 2.0.
- [Reed bed in drainage channel, Barsham Marshes](https://commons.wikimedia.org/wiki/File:Reed_bed_in_drainage_channel,_Barsham_Marshes_-_geograph.org.uk_-_3338346.jpg): reeds standing in dark water. Evelyn Simak, CC BY-SA 2.0.
- [Marsh in the old salt pans of Oléron](https://commons.wikimedia.org/wiki/File:Zone_de_marais_anciennes_salines_Ol%C3%A9ron.JPG): mist over a marsh: the light. Babouchkaya, CC BY-SA 3.0.
- [Gloury path](https://commons.wikimedia.org/wiki/File:Gloury_path_-_geograph.org.uk_-_899093.jpg): planks laid over boggy ground. Richard Webb, CC BY-SA 2.0.
- [Bundles of reeds on the dike](https://commons.wikimedia.org/wiki/File:Bundles_of_reeds_on_the_dike_-_geograph.org.uk_-_1803149.jpg): reed bundles. Evelyn Simak, CC BY-SA 2.0.
- [The reed cutters' boat](https://commons.wikimedia.org/wiki/File:The_reed_cutters%27_boat_-_geograph.org.uk_-_1803187.jpg): a reed cutters' boat in the reeds. Evelyn Simak, CC BY-SA 2.0.
- [Stilt houses on the Chao Phraya, 1889](https://commons.wikimedia.org/wiki/File:Thai_Stilt_Houses_on_Chao_Phraya_River_in_Bangkok_1889.png): stilt houses over water. unknown author, public domain.
- [Weeping Willow trees on the bank of the Witham](https://commons.wikimedia.org/wiki/File:Weeping_Willow_trees_on_the_bank_of_the_Witham_-_geograph.org.uk_-_410953.jpg): willows on a river bank. Brian Green, CC BY-SA 2.0.
- [Wicker fish traps](https://commons.wikimedia.org/wiki/File:%22Bobo%22_pangbobo_traditional_structural_Philippine_fish_traps_October_11_2026_Leyte_Third_District_Uswag_Artesano_01.jpg): fish traps. P1898, CC BY 4.0.

### Ash woods (`main.ashwoods`)

**From the sheet:** a burnt forest. Grey daylight through thin smoke, with drifting ash. A burnt watchtower is the landmark. The roads here are safe from monsters.

**Palette**

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Grey ash | `#8E8B86` | ground | 34% | 6% | 56% |
| Scorched earth | `#4E4844` | ground | 14% | 13% | 31% |
| Pale ash drift | `#B4B1AA` | ground | 12% | 6% | 71% |
| Trodden road | `#77706A` | ground | 6% | 11% | 47% |
| Charcoal trunk | `#2E2C2C` | prop | 16% | 4% | 18% |
| Charred timber | `#4A403A` | prop | 6% | 22% | 29% |
| Dead wood (grey) | `#6E6862` | prop | 4% | 11% | 43% |
| Smoke-stained stone | `#7C7A78` | prop | 2% | 3% | 49% |
| New green shoots | `#7E9A5E` | accent | 4% | 39% | 60% |
| Ember | `#C98452` | accent | 2% | 59% | 79% |

**Light:** Sun through smoke `#EDE4D6`, Shadow tint `#6E6E78`, Smoke `#9A9690`.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Grey ash | `#8E8B86` | 23 (60%) | 27 (63%) | 30 (71%) | 26 (72%) | 7 (41%) |
| Scorched earth | `#4E4844` | 38 (96%) | 46 (107%) | 42 (101%) | 43 (118%) | 19 (111%) |
| Pale ash drift | `#B4B1AA` | 19 (49%) | 20 (47%) | 26 (61%) | 20 (56%) | 3 (18%) |
| Trodden road | `#77706A` | 27 (70%) | 33 (77%) | 35 (83%) | 32 (88%) | 11 (63%) |

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
- [Forest floor after wildfire](https://commons.wikimedia.org/wiki/File:Forest_floor_after_wildfire.jpg): ash, scorched earth and charred trunks: the ground. Thyrome, CC BY 4.0.
- [Clearing in the forest, after wildfire](https://commons.wikimedia.org/wiki/File:Clearing_in_the_forest,_after_wildfire.jpg): a burnt clearing in grey. Thyrome, CC BY 4.0.
- [Boreal pine forest after fire](https://commons.wikimedia.org/wiki/File:Boreal_pine_forest_after_fire.JPG): bare burnt trunks without crowns. Hannu, public domain.
- [Willard Fire: effects of a low-severity fire](https://commons.wikimedia.org/wiki/File:Willard_Fire-_Effects_of_low_severity_fire_%2814712412216%29.jpg): a path beside burnt ground: the road against ash. Coconino National Forest, CC BY-SA 2.0.
- [Burnt fence posts after a wildfire](https://commons.wikimedia.org/wiki/File:20130821-FS-LSC-0169_%289631690048%29.jpg): a charred fence. U.S. Department of Agriculture, CC BY 2.0.
- [Regrowth after forest fire](https://commons.wikimedia.org/wiki/File:Regrowth_after_forest_fire.jpg): green shoots among black trunks. Cooper Phyllis, U.S. Fish and Wildlife Service, public domain.
- [Hot embers 2](https://commons.wikimedia.org/wiki/File:Hot_embers_2.jpg): embers. MUuslimabonu, CC0.

### Steam springs (`main.springs`)

**From the sheet:** hot pools. Bright but diffuse light, with white steam. A large stepped terrace of pools is the landmark. The roads here are safe from monsters. There is no settlement.

**Palette**

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Terrace (cream) | `#DDD2BC` | ground | 28% | 15% | 87% |
| Terrace (soft pink) | `#D4B8AC` | ground | 14% | 19% | 83% |
| Dark rock | `#4C4A4C` | ground | 14% | 3% | 30% |
| Worn mineral path | `#B5A894` | ground | 6% | 18% | 71% |
| Milky pool | `#A9CCC8` | water | 16% | 17% | 80% |
| Mineral column | `#C8C0B0` | prop | 6% | 12% | 78% |
| Hot-spring plants | `#7C9068` | prop | 5% | 28% | 56% |
| Deck wood | `#7E6A58` | prop | 4% | 30% | 49% |
| Pool rim (ochre) | `#C4A070` | accent | 4% | 43% | 77% |
| Mineral stain (rust) | `#B8805C` | accent | 3% | 50% | 72% |

**Light:** Diffuse sun `#FBF4E8`, Shadow tint `#9A9AA8`, Steam `#E6E8E6`.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Terrace (cream) | `#DDD2BC` | 17 (43%) | 15 (34%) | 26 (63%) | 17 (46%) | 2 (15%) |
| Terrace (soft pink) | `#D4B8AC` | 16 (41%) | 17 (40%) | 27 (65%) | 20 (56%) | 3 (20%) |
| Dark rock | `#4C4A4C` | 38 (97%) | 47 (109%) | 38 (92%) | 43 (119%) | 18 (108%) |
| Worn mineral path | `#B5A894` | 19 (48%) | 19 (45%) | 31 (73%) | 20 (56%) | 5 (29%) |

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
- [The Travertine terraces of Pamukkale](https://commons.wikimedia.org/wiki/File:The_Travertine_terraces_of_Pamukkale.jpg): pale mineral terraces. Slyronit, CC BY-SA 4.0.
- [Walking on the travertines](https://commons.wikimedia.org/wiki/File:Walking_on_the_travertines_%2816122883508%29.jpg): a milky pool on a terrace, with people for scale. shankar s., CC BY 2.0.
- [Badab-e Surt, Mazandaran](https://commons.wikimedia.org/wiki/File:Badab_e_Surt,_Mazanderan.jpg): stepped terraces with ochre and rust rims: the landmark. Samaee, CC BY 3.0.
- [Badab-e Surt: terrace rims](https://commons.wikimedia.org/wiki/File:Badab-e_Surt_13961207_16.jpg): terrace rims seen from above. Gholamreza Rezaei Aghozgole غلامرضا رضایی آغوزگله, CC BY 4.0.
- [Mammoth Hot Springs](https://commons.wikimedia.org/wiki/File:Mammoth_Hot_Springs_%2878970%29.jpg): a stepped terrace in steam. Rhododendrites, CC BY-SA 4.0.
- [Shadows and steam at Mammoth Hot Springs](https://commons.wikimedia.org/wiki/File:Shadows_%26_steam_at_Mammoth_Hot_Springs_%2840443099521%29.jpg): a wooden walkway in steam. Yellowstone National Park, public domain.
- [Tufa Towers at Mono Lake](https://commons.wikimedia.org/wiki/File:Tufa_Towers_at_Mono_Lake.jpeg): mineral columns. Samartur, CC BY-SA 3.0.
- [Arch Steam Vent](https://commons.wikimedia.org/wiki/File:Arch_Steam_Vent_%289_August_2011%29_2.jpg): a steam vent in pale ground. James St. John, CC BY 2.0.
- [Noboribetsu hot spring valley](https://commons.wikimedia.org/wiki/File:Noboribetsu_%26_Usuzan_-_Noboribetsu7128.jpg): a milky pool, dark rock and steam. lumoplank, CC0.

### Snow taiga (`main.taiga`)

**From the sheet:** a snowy pine forest on a plateau. Cold and clear, with long blue-grey shadows. A frozen waterfall is the landmark. The roads here are dangerous.

**Palette**

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Fresh snow | `#E4E8EA` | ground | 26% | 3% | 92% |
| Snow in pine shade | `#A9B6C6` | ground | 18% | 15% | 78% |
| Trampled snow road | `#BFC0BC` | ground | 8% | 2% | 75% |
| Frozen stream | `#B4CCD2` | ground | 5% | 14% | 82% |
| Needle litter | `#5E5546` | ground | 5% | 26% | 37% |
| Snow-laden pine | `#3F5444` | prop | 20% | 25% | 33% |
| Trunk | `#5C4A3C` | prop | 6% | 35% | 36% |
| Cabin logs | `#7A6450` | prop | 5% | 34% | 48% |
| Grey rock | `#7A7E82` | prop | 4% | 6% | 51% |
| Fur and hide | `#9A8468` | accent | 3% | 32% | 60% |

**Light:** Sun `#FFF4E4`, Shadow tint `#7E90AC`, Haze `#D8E0E8`.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Fresh snow | `#E4E8EA` | 17 (44%) | 16 (37%) | 20 (48%) | 17 (47%) | 1 (6%) |
| Snow in pine shade | `#A9B6C6` | 20 (51%) | 22 (53%) | 19 (46%) | 21 (59%) | 3 (20%) |
| Trampled snow road | `#BFC0BC` | 18 (47%) | 19 (44%) | 23 (56%) | 19 (52%) | 2 (11%) |
| Frozen stream | `#B4CCD2` | 20 (51%) | 20 (46%) | 20 (48%) | 18 (49%) | 2 (13%) |
| Needle litter | `#5E5546` | 33 (86%) | 39 (91%) | 44 (106%) | 37 (103%) | 17 (98%) |

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
- [Snow-covered Conifers](https://commons.wikimedia.org/wiki/File:Snow-covered_Conifers_-_geograph.org.uk_-_334273.jpg): snow-laden conifers. Anne Burgess, CC BY-SA 2.0.
- [Mountain trail in winter, West Virginia](https://commons.wikimedia.org/wiki/File:Wv-mountain-trail-winter-snow-trees_-_West_Virginia_-_ForestWander.jpg): a trail between snow-laden spruces. ForestWander, CC BY-SA 3.0 us.
- [Log pile on bridleway between Northwood Cottages and Warren Barn](https://commons.wikimedia.org/wiki/File:Log_pile_on_bridleway_between_Northwood_Cottages_and_Warren_Barn_-_geograph.org.uk_-_1660658.jpg): a log pile beside a trampled snow road. Dave Spicer, CC BY-SA 2.0.
- [Oscar Brown Cabin, 1985](https://commons.wikimedia.org/wiki/File:Oscar_Brown_Cabin_1985_%288da971e4-ccab-40fc-9cbc-4031ed2c98a0%29.jpeg): a cabin in snow. NPS Photo, public domain.
- [Frozen Pond on Ashdown Forest](https://commons.wikimedia.org/wiki/File:Frozen_Pond_on_Ashdown_Forest_-_geograph.org.uk_-_319781.jpg): a frozen pond in a forest. Robin Webster, CC BY-SA 2.0.
- [Frozen river in Telemark](https://commons.wikimedia.org/wiki/File:Frozen_river_in_Telemark.jpg): a frozen stream. Ernst Vikne, CC BY-SA 2.0.
- [Ice in the Partnach gorge](https://commons.wikimedia.org/wiki/File:Partnachklamm_rb.jpg): a frozen waterfall: the landmark. Richard Bartz, CC BY-SA 2.5.
- [Bearded seal hide stretched for drying](https://commons.wikimedia.org/wiki/File:%22Oogruk%22_%28bearded_seal%29_hide_stretched_for_drying_%2832735813193%29.jpg): a hide on a drying frame. The U.S. National Archives, public domain.

### Frost peaks (`main.frostpeaks`)

**From the sheet:** the second ice biome and the largest biome on the map. Harsh high-altitude light with blowing snow. The twin peaks, with a pass between them, are the landmark. The roads here are dangerous.

**Palette**

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Deep snow | `#ECEFF1` | ground | 34% | 2% | 95% |
| Packed snow (shade) | `#B4C0D0` | ground | 16% | 13% | 82% |
| Bare grey rock | `#74787C` | ground | 14% | 6% | 49% |
| Glacier ice | `#C4D8DC` | ground | 10% | 11% | 86% |
| Scree path | `#8E9092` | ground | 5% | 3% | 57% |
| Rock spire (dark) | `#4A4E54` | prop | 10% | 12% | 33% |
| Ice wall (deep) | `#9CBCC6` | prop | 5% | 21% | 78% |
| Shelter stone, timber | `#6E6258` | prop | 3% | 20% | 43% |
| Prayer flag (faded) | `#B07868` | accent | 2% | 41% | 69% |
| Frozen remains | `#C9C2B2` | accent | 1% | 11% | 79% |

**Light:** Harsh sun `#FFFAF0`, Shadow tint `#7088AE`, Blowing snow `#E4EAF0`.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Deep snow | `#ECEFF1` | 17 (43%) | 16 (37%) | 20 (48%) | 17 (46%) | 1 (8%) |
| Packed snow (shade) | `#B4C0D0` | 19 (49%) | 21 (49%) | 19 (45%) | 20 (56%) | 3 (16%) |
| Bare grey rock | `#74787C` | 28 (71%) | 33 (78%) | 29 (70%) | 31 (85%) | 10 (57%) |
| Glacier ice | `#C4D8DC` | 19 (48%) | 18 (42%) | 20 (48%) | 17 (47%) | 1 (8%) |
| Scree path | `#8E9092` | 23 (60%) | 27 (64%) | 27 (64%) | 26 (71%) | 6 (37%) |

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
- [Sastrugi, Svalbard](https://commons.wikimedia.org/wiki/File:Svalbard-Sastrugi-Snow.jpg): wind-packed snow: the ground. Bjørn Christian Tørrissen, CC BY-SA 4.0.
- [Ice wall at Camp Tuto in blowing snow](https://commons.wikimedia.org/wiki/File:Ice_wall_Camp_Tuto_blowing_snow_%2813381359384%29.jpg): an ice wall and blowing snow. NASA ICE, public domain.
- [Grey Glacier - Ice Wall](https://commons.wikimedia.org/wiki/File:Grey_Glacier_-_Ice_Wall.jpg): glacier ice; the board's ice is greyer. Felipe.alarcon, CC BY-SA 3.0.
- [Glaciers and peaks of the Cascade Range](https://commons.wikimedia.org/wiki/File:Cascade_Lakes_Scenic_Byway_-_Glaciers_and_Peaks_of_the_Cascade_Mountains_-_NARA_-_7717520.jpg): dark rock spires in snow, seen from above. unknown author, public domain.
- [The Col du Bonnet du Prêtre under snow](https://commons.wikimedia.org/wiki/File:Col_du_Bonnet_du_Pr%C3%AAtre_enneig%C3%A9_depuis_Col_du_Mottet_%28f%C3%A9vrier_2026%29.JPG): a snowy pass. Florian Pépellin, CC BY-SA 4.0.
- [Prayer flags at Muktinath](https://commons.wikimedia.org/wiki/File:Mukti-naath_01.jpg): prayer flags in their five colours. Prabin Shrestha 615, CC BY-SA 4.0.
- [Tanglang La pass](https://commons.wikimedia.org/wiki/File:Tanglanglapass.jpg): a high pass with flags. Deeptrivia (as Commons assumes), CC BY-SA 3.0.
- [A shepherds' stone hut on the Peruvian high plain](https://commons.wikimedia.org/wiki/File:Choza_%22Wualle%22_%28quechua%29_de_piedra_de_pastores_en_el_paramo_peruano,_Catac,_Huar%C3%A1z.jpg): a round stone shelter. PieroAraujoChávez, CC BY-SA 4.0.

### Storm cliffs (`main.cliffs`)

**From the sheet:** basalt sea cliffs, the smallest biome on the map. A dark storm sky mood: low grey light with flashes (visual only). A field of hexagonal basalt columns stepping down to the sea is the landmark. The roads here are dangerous.

**Palette**

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Black-grey basalt | `#3E4044` | ground | 34% | 9% | 27% |
| Wet stone | `#2E3238` | ground | 14% | 18% | 22% |
| Worn column tops | `#5C5E60` | ground | 10% | 4% | 38% |
| Sea grass | `#6E7A5C` | ground | 8% | 25% | 48% |
| Worn road | `#66625C` | ground | 6% | 10% | 40% |
| Storm sea | `#44565E` | water | 12% | 28% | 37% |
| White surf | `#D8DEE0` | water | 5% | 4% | 88% |
| Wreck timber | `#5A4C42` | prop | 5% | 27% | 35% |
| Rusted chain, anchor | `#7A5644` | accent | 3% | 44% | 48% |
| Brazier fire | `#C98452` | accent | 3% | 59% | 79% |

**Light:** Storm light `#C8D0D8`, Shadow tint `#4A5262`, Sea mist `#8A949C`.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Black-grey basalt | `#3E4044` | 42 (108%) | 51 (120%) | 40 (95%) | 46 (128%) | 21 (125%) |
| Wet stone | `#2E3238` | 48 (122%) | 58 (135%) | 42 (100%) | 51 (142%) | 25 (151%) |
| Worn column tops | `#5C5E60` | 33 (86%) | 40 (95%) | 34 (83%) | 37 (102%) | 14 (83%) |
| Sea grass | `#6E7A5C` | 31 (79%) | 30 (70%) | 42 (101%) | 24 (67%) | 12 (73%) |
| Worn road | `#66625C` | 31 (80%) | 37 (87%) | 37 (89%) | 35 (96%) | 13 (78%) |

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
- [Basalt columns, Staffa](https://commons.wikimedia.org/wiki/File:Basalt_columns,_Staffa_-_geograph.org.uk_-_2452237.jpg): basalt columns at the sea. Richard Webb, CC BY-SA 2.0.
- [Basalt columns and surf, Staffa](https://commons.wikimedia.org/wiki/File:Basalt_columns,_Staffa_-_geograph.org.uk_-_2452227.jpg): surf against basalt. Richard Webb, CC BY-SA 2.0.
- [Giant's Causeway from above](https://commons.wikimedia.org/wiki/File:Giant%27s_Causeway_From_Above_01.jpg): a field of columns stepping down to the sea, seen from above: the landmark. Spike, CC BY-SA 4.0.
- [Waves on dark rock, Færder national park](https://commons.wikimedia.org/wiki/File:MOUTMARKA_F%C3%A6rder_nasjonalpark_T%C3%B8nsbergfjorden_2023-10-11_%28Tj%C3%B8me_Oslofjorden_Norway%29_Svaberg_Fj%C3%A6re_Hav_Vind_B%C3%B8lger_Sj%C3%B8spr%C3%B8yt_Mot_vest_Kveldslys_%28Rocky_coastline_Archipelago_Wind_Waves_Turbulent_Sea_spray_splash_Low_sun%29_IMG_3150.jpg): wet dark stone and white surf. Wolfmann, CC BY-SA 4.0.
- [Storm surf](https://commons.wikimedia.org/wiki/File:Another_Storm_IMG_5906_%2815399307554%29.jpg): storm surf. Ronnie Robertson, CC BY-SA 2.0.
- [Sea Pinks or Thrift on the cliff top above Nanjizal](https://commons.wikimedia.org/wiki/File:Sea_Pinks_or_Thrift_on_the_cliff_top_above_Nanjizal_-_geograph.org.uk_-_6847642.jpg): sea thrift on a cliff top above the surf. Rod Allday, CC BY-SA 2.0.
- [Guillemots at Berry Head](https://commons.wikimedia.org/wiki/File:Guillemots_at_Berry_Head.jpg): sea birds on cliff ledges. Partonez, CC BY-SA 4.0.
- [The wreck of the Cromdale](https://commons.wikimedia.org/wiki/File:Cromdale_%28ship,_1891%29_-_SLV_H99.220-1170.jpg): a wrecked sailing ship on rocks, masts standing. unknown author, public domain.

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
