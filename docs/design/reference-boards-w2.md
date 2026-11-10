# Reference boards: build step W2 (C2 and the rest of the west continent)

Status: **art reference, v1 (2026-11-10)**. Five biome boards for the second build step of the main map (`world-map.md` W2): Old forest, Fungal hollow, Dry steppe, Red sands and Bone canyons. **Every colour, material note and photo choice here is a proposal** for the designer to review. The drawing is `reference-boards-w2.svg` (same folder).

This file is **not the spec** and decides nothing. It follows the art rules of spec 9.14 and 11.6 and the sheets in `biome-sheets.md`, which are proposals under review (spec 10.4): if a sheet changes, its board changes with it. Points that need a decision are under "Questions for the designer".

The boards are built and checked the same way as `reference-boards-w1.md`: see "What a board holds" and "How the palettes were checked" there. In short, the swatches are on-screen colours in the biome's light, saturation is the S of HSV, and the warning numbers are colour differences (CIE76 Delta E; higher is easier to see) between the bare ground and the ground under each warning, with a percentage against today's M1 greybox ground `#4b554b`. They compare flat colours and show where to look first; they are not a pass or fail.

New on these boards: each palette has a **road swatch**, checked against the ground beside it, and the drawing shows two kits, because W2 uses both.

## Findings across the five boards

### Saturation

| Biome | Average by share | Highest ground swatch | Highest swatch |
|---|---|---|---|
| Old forest | 27% | 37% (Ferns) | 37% (Ferns) |
| Fungal hollow | 15% | 10% (Spongy dark soil) | 29% (Glowing moss) |
| Dry steppe | 27% | 34% (Cracked earth) | 34% (Cracked earth) |
| Red sands | 36% | 40% (Sand (terracotta)) | 49% (Clay jar) |
| Bone canyons | 24% | 26% (Gravel floor (lit)) | 46% (Rock (brown-red)) |

- All five stay below the least saturated warning colour (green, 62%).
- **Red sands is the most saturated of the five** (36% on average, with rock and clay at 46-49%). Of the ten boards so far only Amber woods is higher (41%).

### Warnings on each ground

The reference row for every board:

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| M1 greybox ground (today) | `#4b554b` | 39 | 43 | 42 | 36 | 17 |

- **The two dark biomes are the best grounds so far.** On the Old forest's dark earth and the Fungal hollow's dark soil every warning is as clear as today or clearer (102-141%).
- **The three dry biomes are light and warm, and red and yellow suffer.** On the steppe's pale grass yellow keeps 35%, the lowest value on any board so far. On the Red sands' tan sand yellow keeps 39% and red 44%; on the lit gravel of the canyons both keep 48%.
- **Blue holds up everywhere** (79-120%), except on the pale mycelium threads (65%).
- **Red sands is a start biome.** C2's tutorial area is in it (`world-map.md` D10), so a new player's first fights happen on the ground where red and yellow are weakest (question 1).
- The tables per biome are in each board.

### Colour vision

The closest pair of the four warnings on each biome's main ground, by colour alone (simulation as on the W1 boards):

| Biome | Main ground | Normal | Protan | Deutan | Tritan |
|---|---|---|---|---|---|
| M1 greybox ground (today) | `#4b554b` | 38 (red-yellow) | 15 (yellow-green) | 9 (red-green) | 13 (blue-green) |
| Old forest | Dark earth | 42 (yellow-green) | 18 (yellow-green) | 12 (red-green) | 14 (blue-green) |
| Fungal hollow | Spongy dark soil | 43 (yellow-green) | 17 (yellow-green) | 11 (red-green) | 14 (blue-green) |
| Dry steppe | Pale dry grass | 20 (red-yellow) | 9 (yellow-green) | 4 (red-green) | 8 (blue-green) |
| Red sands | Sand (dusty tan) | 22 (red-yellow) | 9 (yellow-green) | 4 (red-green) | 8 (blue-green) |
| Bone canyons | Gravel floor (shadow) | 32 (yellow-green) | 11 (yellow-green) | 6 (red-green) | 11 (blue-green) |

- On the dark grounds the pairs are a little further apart than today. On pale grass and sand they are closer, as on snow. Each warning has its own shape as well (R-3.3-04, spec 9.3).

### Roads against the ground

R-9.14-07 asks for roads clearly drawn into the ground. The difference between each board's road swatch and the two largest grounds beside it:

| Biome | Road | Against the main ground | Against the second ground |
|---|---|---|---|
| Old forest | Trodden path `#6B5E4C` | 18 (Dark earth) | 14 (Moss) |
| Fungal hollow | Packed path `#6A6468` | 21 (Spongy dark soil) | 6 (Soil with mycelium) |
| Dry steppe | Worn track `#8F7C60` | 23 (Pale dry grass) | 9 (Cracked earth) |
| Red sands | Packed caravan road `#A08468` | 14 (Sand (dusty tan)) | 8 (Sand (terracotta)) |
| Bone canyons | Path gravel `#9C8B7C` | 15 (Gravel floor (shadow)) | 8 (Gravel floor (lit)) |

- **In sand and gravel a road is hard to tell by colour:** 8-15 in Red sands and Bone canyons. A packed road in a desert is the same material as the ground.
- On the steppe the track is clear against grass (23) and close to cracked earth (9).
- A road in these biomes needs more than a colour: edge stones, posts, ruts or a darker centre line (question 2).

### Kit roofs against the ground

W2 uses both kits. The drawing uses these colours: water kit: slate roof `#6E7479`, bazaar kit: pale dome `#DCD2BC`, bazaar kit: clay wall `#B4977A`, bazaar kit: carved wood `#6E5540`. The slate and the kit materials are from `city-layouts.md`; the dome and wall colours are this board's proposal for "pale domes" and "clay walls".

| Biome | Settlement and kit | Slate roof | Pale dome |
|---|---|---|---|
| Old forest | M2: water or bazaar kit (Open) | 25 main, 18 closest (Trodden path) | 60 main, 34 closest (Clearing in light) |
| Fungal hollow | T3: water kit | 27 main, 7 closest (Packed path) | 64 main, 12 closest (Mycelium threads) |
| Dry steppe | T4: bazaar kit | 38 main, 18 closest (Mud at the waterhole) | 16 main, 16 closest (Pale dry grass) |
| Red sands | C2: bazaar kit | 34 main, 22 closest (Sand in shade) | 18 main, 18 closest (Sand (dusty tan)) |
| Bone canyons | T5: bazaar kit | 6 main, 6 closest (Gravel floor (shadow)) | 39 main, 20 closest (Gravel floor (lit)) |

- **Pale domes on pale ground:** the bazaar kit's roof is close to the steppe's grass (16) and to the Red sands' tan sand (18). C2 itself stands on that sand (question 6).
- **M2 in the Old forest** (its kit is Open): a slate roof is 25 from the dark earth and a pale dome 60. Pale domes would be the brightest shapes in the darkest biome.
- **T3 in the Fungal hollow:** slate is clear against the dark soil (27) and merges with the packed path (7).
- T5 in the Bone canyons uses the bazaar kit, so the low slate value there (6) does not apply.

## The boards

### Old forest (`main.oldforest`)

**From the sheet:** deep forest, the darkest ground on the west continent. Dim green-grey shade with shafts of light in clearings. A hollow ancient tree big enough to walk into is the landmark. The roads here are dangerous (`open-world-content.md` F1).

**Palette**

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Dark earth | `#3F3A33` | ground | 28% | 19% | 25% |
| Moss | `#56603F` | ground | 22% | 34% | 38% |
| Ferns | `#66754A` | ground | 10% | 37% | 46% |
| Trodden path | `#6B5E4C` | ground | 6% | 29% | 42% |
| Clearing in light | `#7E8458` | ground | 4% | 33% | 52% |
| Root and bark | `#4E4338` | prop | 12% | 28% | 31% |
| Deep canopy | `#3E4A36` | prop | 10% | 27% | 29% |
| Stone marker | `#777A70` | prop | 4% | 8% | 48% |
| Pale fungus | `#B8A98A` | accent | 2% | 25% | 72% |
| Spider web | `#C9CCC0` | accent | 2% | 6% | 80% |

**Light:** Sun shaft `#F2ECC8`, Shadow tint `#56645C`, Fog `#6F7A6E`.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Dark earth | `#3F3A33` | 42 (108%) | 50 (117%) | 47 (112%) | 46 (128%) | 23 (135%) |
| Moss | `#56603F` | 37 (95%) | 36 (83%) | 50 (120%) | 29 (80%) | 18 (106%) |
| Ferns | `#66754A` | 33 (86%) | 30 (70%) | 49 (117%) | 23 (63%) | 16 (94%) |
| Trodden path | `#6B5E4C` | 30 (78%) | 35 (83%) | 44 (105%) | 34 (96%) | 15 (88%) |
| Clearing in light | `#7E8458` | 28 (72%) | 25 (59%) | 45 (109%) | 21 (59%) | 13 (77%) |

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

**Settlement:** M2, a medium city with a palisade and three gates. Its kit is Open (water or bazaar); see the roof finding above.

**Who lives here:** beasts, with plants and aberrations as the shared families; earth.

**Not drawn:** wood as a resource is Open (10.4 [M3]).

**Photo references**
- [Old-growth Oak-Linden-Hornbeam forest - Bialowieza forest](https://commons.wikimedia.org/wiki/File:Old-growth_Oak-Linden-Hornbeam_forest_-_Bialowieza_forest.tif): a huge old trunk in dim old-growth forest: the size of the trees and the dark ground. Bouke ten Cate, CC BY 4.0.
- [Ferns and moss on the forest floor beside OR-35, Oregon Route 35 east of Mount Hood](https://commons.wikimedia.org/wiki/File:Ferns_and_moss_on_the_forest_floor_beside_OR-35,_Oregon_Route_35_east_of_Mount_Hood.jpg): ferns and moss on the forest floor: the ground cover. Z3lvs, CC0.
- [Buttress roots](https://commons.wikimedia.org/wiki/File:Buttress_roots.JPG): buttress roots standing like low walls. Patti Neumann, CC BY-SA 3.0.
- [Old Tree Trunk Hollow in the Center](https://commons.wikimedia.org/wiki/File:Old_Tree_Trunk_Hollow_in_the_Center.jpg): a hollow trunk wide enough to walk through: the landmark in small. Inforested, CC BY-SA 4.0.
- [Epping Forest High Beach Essex England - rotting fallen tree trunk](https://commons.wikimedia.org/wiki/File:Epping_Forest_High_Beach_Essex_England_-_rotting_fallen_tree_trunk.jpg): a fallen trunk under moss. Acabashi, CC BY-SA 4.0.
- [Forest-ray-of-sunshine-sunbeams](https://commons.wikimedia.org/wiki/File:Forest-ray-of-sunshine-sunbeams_%2823957956609%29.jpg): shafts of light in a dark wood: the light mood. www.Pixel.la Free Stock Photos, CC0.
- [Ruined Wall, Coate Moor](https://commons.wikimedia.org/wiki/File:Ruined_Wall,_Coate_Moor_-_geograph.org.uk_-_1004031.jpg): moss-covered stone blocks beside a path: the overgrown stone markers. Mick Garratt, CC BY-SA 2.0.
- [Moss-covered dry stone wall, Achnabreck Forest](https://commons.wikimedia.org/wiki/File:Moss-covered_dry_stone_wall,_Achnabreck_Forest_-_geograph.org.uk_-_7185583.jpg): a moss-covered stone wall in a forest: the old boundary wall at the Amber woods border. Mick Garratt, CC BY-SA 2.0.
- [Spider web](https://commons.wikimedia.org/wiki/File:Spider_web_%2854778634575%29.jpg): a spider web with dew. U.S. Fish and Wildlife Service - Midwest Region, public domain.

### Fungal hollow (`main.fungal`)

**From the sheet:** a giant mushroom grove. Dusk-like, with soft glowing spores as the main light. One enormous mushroom with a path spiralling around its base is the landmark. The roads here are dangerous.

**Palette**

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Spongy dark soil | `#3A3438` | ground | 32% | 10% | 23% |
| Soil with mycelium | `#5B5558` | ground | 14% | 7% | 36% |
| Packed path | `#6A6468` | ground | 6% | 6% | 42% |
| Mycelium threads | `#B9B3A6` | ground | 6% | 10% | 73% |
| Cap (violet) | `#7A6688` | prop | 14% | 25% | 53% |
| Cap (teal) | `#5E8380` | prop | 8% | 28% | 51% |
| Cap (cream) | `#D2C6AE` | prop | 8% | 17% | 82% |
| Stem and gills | `#A89F96` | prop | 4% | 11% | 66% |
| Rotting log | `#4A3F37` | prop | 4% | 26% | 29% |
| Glowing moss | `#6E9A8C` | accent | 4% | 29% | 60% |

**Light:** Spore glow `#CFE3D6`, Shadow tint `#4A4660`, Fog `#5E5A70`.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Spongy dark soil | `#3A3438` | 44 (112%) | 55 (128%) | 42 (102%) | 51 (141%) | 24 (143%) |
| Soil with mycelium | `#5B5558` | 34 (86%) | 43 (100%) | 36 (86%) | 40 (111%) | 16 (92%) |
| Packed path | `#6A6468` | 30 (77%) | 38 (89%) | 33 (79%) | 36 (100%) | 13 (74%) |
| Mycelium threads | `#B9B3A6` | 19 (48%) | 19 (45%) | 27 (65%) | 19 (54%) | 3 (19%) |

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
- [Amethyst Deceiver - Laccaria amethystea - Violetter Lacktrichterling - Laccaria amethystina - 02](https://commons.wikimedia.org/wiki/File:Amethyst_Deceiver_-_Laccaria_amethystea_-_Violetter_Lacktrichterling_-_Laccaria_amethystina_-_02.jpg): a violet cap: the muted violet, to be scaled up. Norbert Nagel, CC BY-SA 3.0.
- [Entoloma hochstetteri Lindeman](https://commons.wikimedia.org/wiki/File:Entoloma_hochstetteri_Lindeman.png): a blue-green cap: the teal. Haydenrjones, CC BY-SA 4.0.
- [PanellusStipticusAug12 2009](https://commons.wikimedia.org/wiki/File:PanellusStipticusAug12_2009.jpg): fungi glowing green on a log in the dark: the soft glow as a light source. Ylem, public domain.
- [Fluorescent Entoloma in Colombia](https://commons.wikimedia.org/wiki/File:Fluorescent_Entoloma_in_Colombia.jpg): glowing gills seen from below: what a giant cap looks like from under it. Alan Rockefeller, CC BY 4.0.
- [Mycelium](https://commons.wikimedia.org/wiki/File:%D0%9C%D1%96%D1%86%D0%B5%D0%BB%D1%96%D0%B9.jpg): white mycelium threads in dark soil: the ground. Ірина Бучнєва, CC BY 4.0.
- [Lycoperdon utriforme, Mosaic Puffball 2010 G1](https://commons.wikimedia.org/wiki/File:Lycoperdon_utriforme,_Mosaic_Puffball_2010_G1.jpg): a puffball. George Chernilevsky, public domain.
- [Hypholoma fasciculare 47456](https://commons.wikimedia.org/wiki/File:Hypholoma_fasciculare_47456.jpg): a cluster of caps on a mossy log: how caps crowd and overlap. This image was created by user John S. Harper (jsharper) at , CC BY-SA 3.0.
- [2025-10-18 D500-1145 Achim-Lammerts Lactarius-spec](https://commons.wikimedia.org/wiki/File:2025-10-18_D500-1145_Achim-Lammerts_Lactarius-spec.jpg): a single wide cap from above: the shape the camera sees. Achim Lammerts (Syntaxys), CC BY-SA 4.0.

### Dry steppe (`main.steppe`)

**From the sheet:** dry grassland. Bright, hot and slightly hazy. A giant weathered statue lying on its side is the landmark. The roads here are safe from monsters.

**Palette**

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Pale dry grass | `#C2B88C` | ground | 42% | 28% | 76% |
| Cracked earth | `#A8926F` | ground | 20% | 34% | 66% |
| Dense tufts | `#9E9670` | ground | 10% | 29% | 62% |
| Worn track | `#8F7C60` | ground | 6% | 33% | 56% |
| Mud at the waterhole | `#7A6A55` | ground | 3% | 30% | 48% |
| Grey stone, cairn | `#9A9890` | prop | 7% | 6% | 60% |
| Thorn-tree leaf | `#7C8060` | prop | 4% | 25% | 50% |
| Tent poles, bark | `#6B5D4C` | prop | 4% | 29% | 42% |
| Bone | `#DDD6C4` | accent | 2% | 11% | 87% |
| Waterhole | `#6F8480` | water | 2% | 16% | 52% |

**Light:** Sun `#FFF4D8`, Shadow tint `#8F8C9C`, Haze `#E4DCC4`.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Pale dry grass | `#C2B88C` | 19 (48%) | 15 (35%) | 35 (84%) | 16 (46%) | 7 (39%) |
| Cracked earth | `#A8926F` | 20 (51%) | 20 (48%) | 39 (95%) | 23 (63%) | 9 (55%) |
| Dense tufts | `#9E9670` | 22 (57%) | 21 (48%) | 39 (94%) | 21 (57%) | 9 (54%) |
| Worn track | `#8F7C60` | 23 (60%) | 26 (60%) | 41 (99%) | 27 (75%) | 11 (66%) |
| Mud at the waterhole | `#7A6A55` | 27 (69%) | 31 (73%) | 42 (102%) | 31 (87%) | 13 (77%) |

**Ground, seen from the camera**
- Open and flat, with few tall props, so the screen is almost all ground. The variety has to come from the ground itself: tufts, cracks, stones, bare patches.
- **Pale dry grass is the weakest ground for yellow so far (35%)**, and green keeps 46% on it.
- Cracked earth, the track and the mud are darker and better.

**Props and the fade**
- **Lone thorn trees and nomad tent frames:** taller than 2 m, so they fade.
- **Animal skulls and stone cairns:** low, never fade.
- **Watering holes:** the water is unwalkable; the mud rim is the darkest ground here.
- **The fallen statue:** it fades where it hides the player. Nothing is walkable on top of it, and its collider must not count as ground (spec 11.6, the open art item about a ground layer).

**Settlement:** T4, a small open town in the bazaar kit. See the pale dome finding above.

**Who lives here:** humanoids (raiders), with beasts as the shared family; wind.

**Photo references**
- [Mongolian Steppes](https://commons.wikimedia.org/wiki/File:Mongolian_Steppes_%286228096139%29.jpg): open dry steppe: the pale grass as almost the whole picture. Francisco Anzola, CC BY 2.0.
- [Cracked earth after prolonged drought. 2020](https://commons.wikimedia.org/wiki/File:Cracked_earth_after_prolonged_drought._2020.jpg): cracked earth with a few green tufts. Rosser1954, CC BY-SA 4.0.
- [Graceful Giant of the Savanna (Giraffe) 01](https://commons.wikimedia.org/wiki/File:Graceful_Giant_of_the_Savanna_%28Giraffe%29_01.jpg): a lone flat-topped thorn tree in dry grass. Ssemmanda will, CC BY-SA 4.0.
- [Graceful Giant of the Savanna (Giraffe) 03](https://commons.wikimedia.org/wiki/File:Graceful_Giant_of_the_Savanna_%28Giraffe%29_03.jpg): a dirt track through dry grass: the road against the ground. Ssemmanda will, CC BY-SA 4.0.
- [Camel skull in the steppe near Lake Elton](https://commons.wikimedia.org/wiki/File:%D0%A7%D0%B5%D1%80%D0%B5%D0%BF_%D0%B2%D0%B5%D1%80%D0%B1%D0%BB%D1%8E%D0%B4%D0%B0_-_%22%D0%BA%D0%BE%D0%BC%D0%BF%D0%B0%D1%81%22_%D1%83_%D1%81%D1%82%D0%B5%D0%BF%D1%83._%D0%95%D0%BA%D1%81%D0%BF%D0%B5%D0%B4%D0%B8%D1%86%D1%96%D1%8F_%D0%B2_%D0%BD%D0%B0%D0%BF%D1%96%D0%B2%D0%BF%D1%83%D1%81%D1%82%D0%B5%D0%BB%D1%96_%D0%97%D0%B0%D0%B2%D0%BE%D0%BB%D0%B6%D0%B6%D1%8F,_%D1%81%D0%BE%D0%BB%D0%BE%D0%BD%D0%B5_%D0%BE%D0%B7._%D0%95%D0%BB%D1%8C%D1%82%D0%BE%D0%BD.jpg): an animal skull on dry ground. Євгенія Яніш, CC BY 4.0.
- [Kazakh Yurt frame](https://commons.wikimedia.org/wiki/File:Kazakh_Yurt_frame.jpg): the bare frame of a nomad tent. cea +, CC BY 2.0.
- [Ovoo in Gobi Desert](https://commons.wikimedia.org/wiki/File:Ovoo_in_Gobi_Desert.jpg): a stone cairn with cloth on open ground. Bernard Gagnon, CC0.
- [Waterhole Okaukuejo Camp in Etosha National Park](https://commons.wikimedia.org/wiki/File:Waterhole_Okaukuejo_Camp_in_Etosha_National_Park.jpg): a waterhole with its bare rim. Hka1987, CC0.
- [Luxor Ramesseum R18](https://commons.wikimedia.org/wiki/File:Luxor_Ramesseum_R18.jpg): a fallen giant statue: the landmark. Marc Ryckaert, CC BY-SA 4.0.

### Red sands (`main.redsands`)

**From the sheet:** the medium desert and C2's biome. Strong high sun with warm haze. A half-buried obelisk is the landmark. The roads here are safe from monsters.

**Palette**

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Sand (dusty tan) | `#C4A886` | ground | 38% | 32% | 77% |
| Sand (terracotta) | `#B58A6C` | ground | 22% | 40% | 71% |
| Sand in shade | `#9A7C68` | ground | 8% | 32% | 60% |
| Packed caravan road | `#A08468` | ground | 6% | 35% | 63% |
| Red rock outcrop | `#8A5A4A` | prop | 12% | 46% | 54% |
| Rock in shade | `#6A463C` | prop | 4% | 43% | 42% |
| Dry bush | `#8C8762` | prop | 4% | 30% | 55% |
| Palm frond | `#6F7F58` | prop | 3% | 31% | 50% |
| Clay jar | `#A66F54` | accent | 2% | 49% | 65% |
| Well water | `#5F8482` | water | 1% | 28% | 52% |

**Light:** Sun `#FFF2D0`, Shadow tint `#94879A`, Haze `#E8D4B4`.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Sand (dusty tan) | `#C4A886` | 17 (44%) | 17 (39%) | 35 (85%) | 20 (56%) | 7 (41%) |
| Sand (terracotta) | `#B58A6C` | 17 (43%) | 21 (49%) | 40 (97%) | 26 (73%) | 10 (61%) |
| Sand in shade | `#9A7C68` | 21 (53%) | 26 (61%) | 39 (93%) | 29 (80%) | 10 (61%) |
| Packed caravan road | `#A08468` | 20 (52%) | 23 (54%) | 40 (96%) | 26 (73%) | 10 (61%) |

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
- [Wadi Rum, Jordan](https://commons.wikimedia.org/wiki/File:Wadi_Rum,_Jordan_%285584953003%29.jpg): red sand between rock outcrops, seen from above: close to the game's camera. NASA Goddard Space Flight Center, CC BY 2.0.
- [Wadi Rum - 2261824000](https://commons.wikimedia.org/wiki/File:Wadi_Rum_-_2261824000.jpg): a red rock outcrop with dry bushes at its foot. twiga-swala, CC BY-SA 2.0.
- [Red sand of the Wadi Rum desert](https://commons.wikimedia.org/wiki/File:Red_sand_of_the_Wadi_Rum_desert.jpg): red sand with rock in the distance: the sand's upper limit of colour; the board is dustier. John Romano D'Orazio, CC BY-SA 4.0.
- [Leafy shrub on open desert sand near the Colorado River, La Paz County, Arizona](https://commons.wikimedia.org/wiki/File:Leafy_shrub_on_open_desert_sand_near_the_Colorado_River,_La_Paz_County,_Arizona.jpg): a dry bush on open sand. Z3lvs, CC0.
- [Ruins of desert Cathay - personal narrative of explorations in Central Asia and westernmost China (1912)](https://commons.wikimedia.org/wiki/File:Ruins_of_desert_Cathay_-_personal_narrative_of_explorations_in_Central_Asia_and_westernmost_China_%281912%29_%2814803015043%29.jpg): old posts and walls half buried in sand: the half-buried walls. Internet Archive Book Images, No restrictions.
- [Clay pot workshop 20220916120359](https://commons.wikimedia.org/wiki/File:Clay_pot_workshop_20220916120359.jpg): clay jars stacked by a mud wall. Philtom12, CC BY-SA 4.0.
- [49 Palms Oasis](https://commons.wikimedia.org/wiki/File:49_Palms_Oasis_%2853732413071%29.jpg): a cluster of palms in dry rocky ground: the palms at a well. Joshua Tree National Park, public domain.
- [A water well in Merzouga Desert](https://commons.wikimedia.org/wiki/File:A_water_well_in_Merzouga_Desert.jpg): a desert well with a timber frame. Yair Haklai, CC BY-SA 4.0.
- [Fallen obelisk. Carnac LCCN2004672044](https://commons.wikimedia.org/wiki/File:Fallen_obelisk._Carnac_LCCN2004672044.jpg): a fallen obelisk among ruins: the landmark. Good, Frank Mason, 1839-1928, photographer, public domain.

### Bone canyons (`main.canyons`)

**From the sheet:** red-rock canyons. Hard light on the rims and deep cool shadow on the canyon floors. A giant ribcage that the main path runs through is the landmark. The roads here are safe from monsters.

**Palette**

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Gravel floor (shadow) | `#766E6E` | ground | 28% | 7% | 46% |
| Gravel floor (lit) | `#B09A82` | ground | 14% | 26% | 69% |
| Path gravel | `#9C8B7C` | ground | 6% | 21% | 61% |
| Rock (brown-red) | `#8C5E4C` | prop | 18% | 46% | 55% |
| Rock (cream) | `#CDBB9C` | prop | 12% | 24% | 80% |
| Rim rock (lit) | `#B98E72` | prop | 8% | 38% | 73% |
| Bleached bone | `#E2DCCB` | prop | 8% | 10% | 89% |
| Scaffold timber | `#6E5A48` | prop | 3% | 35% | 43% |
| Dry scrub | `#7F7C5E` | accent | 2% | 26% | 50% |
| Rope and cloth | `#A58F70` | accent | 1% | 32% | 65% |

**Light:** Sun (hard) `#FFF0D0`, Shadow tint `#6E7890`, Haze `#D9C6AE`.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Gravel floor (shadow) | `#766E6E` | 27 (70%) | 34 (80%) | 33 (79%) | 33 (92%) | 11 (64%) |
| Gravel floor (lit) | `#B09A82` | 19 (48%) | 20 (48%) | 34 (82%) | 22 (62%) | 7 (40%) |
| Path gravel | `#9C8B7C` | 21 (54%) | 25 (58%) | 34 (81%) | 25 (71%) | 7 (44%) |

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
- [Red Rock Canyon State Park - Flickr - docentjoyce](https://commons.wikimedia.org/wiki/File:Red_Rock_Canyon_State_Park_-_Flickr_-_docentjoyce.jpg): cliffs in layers of red and cream rock: the walls. Joyce Cory on Flickr as docentjoyce, CC BY 2.0.
- [Wash in Golden Canyon, Death Valley National Park, California](https://commons.wikimedia.org/wiki/File:Wash_in_Golden_Canyon,_Death_Valley_National_Park,_California.jpg): a gravel wash between canyon walls: the floor and the path. Christian David, CC BY-SA 4.0.
- [Streambed of Red Rock Wash in the Red Rock Canyon NCA](https://commons.wikimedia.org/wiki/File:Streambed_of_Red_Rock_Wash_in_the_Red_Rock_Canyon_NCA.jpg): a dry streambed of gravel: the floor as ground. Thomas Farley, CC0.
- [Valley of the Whales - Wadi al Hitan - skeleton](https://commons.wikimedia.org/wiki/File:Valley_of_the_Whales_-_Wadi_al_Hitan_-_skeleton.jpg): a whale skeleton lying in a desert valley: giant bones on the ground. Khaled Mokhtar, CC BY-SA 4.0.
- [Bragar whale bone arch 02](https://commons.wikimedia.org/wiki/File:Bragar_whale_bone_arch_02.jpg): an arch made of whale bones over a gate: ribs over a path. PaulT (Gunther Tschuch), CC BY-SA 4.0.
- [Bridge at Diablo looking down](https://commons.wikimedia.org/wiki/File:Bridge_at_Diablo_looking_down.tif): a rope bridge across a gorge. U.S. National Park Service, public domain.
- [Red Rock View Through Natural Arch](https://commons.wikimedia.org/wiki/File:Red_Rock_View_Through_Natural_Arch_%2819356047114%29.jpg): a natural rock arch. BLM Nevada, CC BY 2.0.
- [Himalayan Griffon Vulture (Gyps himalayensis) Perched above the world, where silence rules the skies 01](https://commons.wikimedia.org/wiki/File:Himalayan_Griffon_Vulture_%28Gyps_himalayensis%29_Perched_above_the_world,_where_silence_rules_the_skies_01.jpg): a vulture on a perch. Shiv's fotografia, CC BY-SA 4.0.
- [ReedGoldMine](https://commons.wikimedia.org/wiki/File:ReedGoldMine.jpg): a timber frame over a mine shaft: the mine scaffolds. Bang-Ning Hsu, CC BY-SA 2.0.

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

## Questions for the designer

Nothing below is decided here.

1. **Warnings on light ground** (also question 2 of the W1 boards): Red sands is a start biome, and red and yellow are at 39-49% on its sand. Does that change the answer?
2. **Roads on sand and gravel:** the road colour is 8-15 from the ground. What makes a road "clearly drawn" (R-9.14-07) in a desert or a canyon?
3. **One walkable layer and the fade, in three places:** can canyon walls fade, or are canyon floors wider than the walls are tall? Does the path at the giant mushroom stay on the ground? Is nothing walkable on the fallen statue and the ribs?
4. **The hollow tree:** is it a walk-in place whose trunk lifts off like a roof, although R-11.6-05 lists only service buildings as interiors?
5. **The Fungal hollow's light:** is a dim ambient light plus many glowing props what "one fixed light per biome" (R-9.14-04) means here?
6. **Pale domes on pale ground:** the bazaar kit's roof is close to sand and dry grass. Does the dome get a different tone, or does something else mark the kit?
7. **M2's kit** (Open in 10.4): the roof finding above gives numbers for both choices.
8. **The review of the five sheets** (10.4) is still open, and these boards follow the sheets as written.

## Not covered

- C2, the caravan bazaar city, and the detailed dressing of M2, T3, T4 and T5.
- Characters, monsters, UI and the world map.
- The W3 biomes (the island and the east continent) and the Ember caldera.
- How the colours behave in the engine. The swatches are targets, and the cel shader and the light presets have to reach them.
