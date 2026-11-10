# Reference board: build step W4 (Ember caldera and M6, the lawless city)

Status: **art reference, v1 (2026-11-10)**. A board for the fourth build step of the main map (`world-map.md` W4): the Ember caldera, which is the last of the 20 biomes, and M6, the lawless city, in its scrap look. **Every colour, material note and photo choice here is a proposal** for the designer to review. The drawing is `reference-board-w4.svg` (same folder).

This file is **not the spec** and decides nothing. It is built and checked the same way as the other boards: see "What a board holds", "How the palettes were checked" and "The exact method" in `reference-boards-w1.md`. The numbers are colour differences (CIE76 Delta E; higher is easier to see) between flat colours. They show where to look first; they are not a pass or fail.

New on this board: the table of every biome is complete (20 of 20), and a table puts each warning beside the lava.

## What is decided and what is a proposal

**Decided** (spec and designer):
- **The Ember caldera is the fire biome**, with a volcano as its landmark (`world-map.md` D6), and it hosts M6.
- **M6 is the lawless city:** PvP is always on and it is open to everyone (R-6.3-01), and it has no guards (R-2.3-07). The black market is there, for bad-honor players only (R-4.4-02). Entering shows a warning, and the new-player protection is off inside (`world-map.md` D11).
- **M6 gets its own scrap-built look** (`city-layouts.md` E7).
- **The lava glows and the ground is dark rock** rather than bright red (the example in R-9.14-03).
- **The designer's answers of 2026-11-10 that apply here** (`reference-boards-questions.md`):
  - attack warnings are drawn above air effects (Q8);
  - fires stay small points, and flashes stay soft (Q7);
  - a road in ash is marked with posts or poles and with edge stones or cairns (Q28);
  - each biome gets its own saturation number (Q3);
  - nothing is walkable on top of things that pass over walkable ground (Q14);
  - cloth may use muted warning hues (Q5).

**Proposals under review** (spec 10.4), which this board follows as written:
- **The Ember caldera's sheet** in `biome-sheets.md`. The designer accepted 19 sheets on 2026-11-10; this one was not among them, because it had no board yet.
- **M6's layout** in `city-layouts.md`: medium-sized, scrap walls with a gate per road (three roads), the black market in the middle around a forge-lit yard, no safe square, cover and more than one way out of every yard. **M6's services are Open.**

## Findings

### Saturation

| Board | Average by share | Highest ground swatch | Highest swatch |
|---|---|---|---|
| Ember caldera | 27% | 50% (Rock lit by lava) | 75% (Lava (glow)) |
| M6, the lawless city | 29% | 56% (Forge-lit ground) | 67% (Forge glow) |

- **The lava is the most saturated thing on any board:** the glow is at 75%, the cooling crust at 69% and the core at 63%. The warning colours are at 62-79%, so the lava is among them.
- R-9.14-03 names the caldera's lava as the place where the world may glow. The designer kept fires as small points (Q7). Lava is lines and pools: 14% of the screen on this palette (question 1).
- **Saturation reads high on dark colours.** "Rock lit by lava" is at 50% and M6's forge-lit ground at 56%, but both are dark (36-43% value). They are dark red-browns, not bright colours.

### Warnings on each ground

The reference row:

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| M1 greybox ground (today) | `#4b554b` | 39 | 43 | 42 | 36 | 17 |

- **Black rock is the best ground for warnings of all 20 biomes:** every warning is clearer than today (117-147%).
- Grey ash is level with today (88-107%), and the ash road a little under (78-97%).
- Red is the weakest on rock that the lava lights (81%), which is where red matters most (see below).
- The tables are in each block.

### Every biome, from the darkest to the lightest

The main ground of each of the 20 biomes. L is the lightness of the ground (CIE L*, 0 black to 100 white), and the percentages are what each warning keeps of today's difference.

| Biome | Main ground | L | Red | Yellow | Blue | Green |
|---|---|---|---|---|---|---|
| Ember caldera | Black volcanic rock `#2B2826` | 16 | 125% | 137% | 117% | 147% |
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

- The Ember caldera is the darkest ground on the map (L 16). The Snow taiga beside it is one of the lightest (L 92).
- The designer's answer for the light end of this table is to change the warning itself (Q1).

### A red warning beside the lava

Each warning, as the game blends it today over a ground, against a colour that lies beside it:

| Ground under the warning | Beside it | Red | Yellow | Blue | Green |
|---|---|---|---|---|---|
| Black volcanic rock | Cooling crust `#7A3426` | 11 | 42 | 68 | 67 |
| Black volcanic rock | Rock lit by lava `#5C3A2E` | 29 | 43 | 55 | 54 |
| Black volcanic rock | Lava (glow) `#D06A34` | 30 | 33 | 94 | 76 |
| Dark-brown rock | Cooling crust `#7A3426` | 10 | 42 | 67 | 65 |
| Grey ash | Lava (glow) `#D06A34` | 33 | 35 | 88 | 71 |
| Bare rock street | Rusted sheet roof `#7E5440` | 26 | 33 | 59 | 51 |
| Bare rock street | Scorched cloth `#6E4034` | 23 | 40 | 58 | 57 |
| Forge-lit ground | Rusted sheet roof `#7E5440` | 26 | 35 | 51 | 42 |

- **A red warning on black rock is 11 from the lava's cooling crust**, and 10 on dark-brown rock. It is 49 from the bare rock under it, so against the ground it is very clear, and against the edge of a lava line it is close.
- The glowing lava itself is further away (30 from the red warning, 33 from the yellow one). The glow is much brighter than a warning.
- **What still tells them apart:** lava is unwalkable and drawn as thin lines and pools with a bright core; a warning is a flat shape with an icon, on walkable ground, and only while the attack runs (spec 9.3).
- The designer's answer on warnings (Q1) covers light ground. Here the ground is dark and the neighbour is the problem (question 2).
- In M6 the red warning is 26 from a rusted roof and 23 from scorched cloth. Those are far enough. C3's tile roof was at 8.

### Colour vision

The closest pair of the four warnings on each main ground, by colour alone (simulation as on the W1 boards):

| Board | Main ground | Normal | Protan | Deutan | Tritan |
|---|---|---|---|---|---|
| M1 greybox ground (today) | `#4b554b` | 38 (red-yellow) | 15 (yellow-green) | 9 (red-green) | 13 (blue-green) |
| Ember caldera | Black volcanic rock | 45 (yellow-green) | 19 (yellow-green) | 13 (red-green) | 14 (blue-green) |
| M6, the lawless city | Bare rock street | 45 (yellow-green) | 19 (yellow-green) | 13 (red-green) | 14 (blue-green) |

- On black rock the warnings are further apart than today under every kind of colour vision.

### Edges

| Edge | Difference |
|---|---|
| lava (glow) against black volcanic rock | 70 |
| lava (glow) against rock lit by lava | 49 |
| cooling crust against black volcanic rock | 39 |
| cooling crust against dark-brown rock | 33 |
| grey ash against black volcanic rock | 21 |
| obsidian against black volcanic rock | 7 |
| scrap wall against bare rock street | 14 |
| scrap wall against packed ash lane | 8 |
| rusted sheet roof against salvaged plank roof | 15 |
| forge glow against lava (glow) | 16 |
| scorched cloth against cooling crust | 13 |

- **The lava's edge is the clearest edge on any board** (49-70). A player sees where walking ends.
- **Obsidian on black rock is 7.** Shards of black glass on black rock are close to invisible as flat colours. They need a highlight from the lava's glow, or they are only shapes.
- **A scrap wall is 14 from a bare rock street and 8 from an ash lane.** The designer's dark band at the foot of walls (Q20) was decided for the bazaar and medieval kits (question 5).
- **Forge glow and lava glow are one family** (16). M6's light and the caldera's light are the same colour, which fits a city built on the caldera.

### Roads against the ground

| Board | Road | Against the main ground | Against the second ground |
|---|---|---|---|
| Ember caldera | Ash road `#6A625C` | 26 (Black volcanic rock) | 20 (Dark-brown rock) |
| M6, the lawless city | Packed ash lane `#5E5855` | 22 (Bare rock street) | 11 (Scrap plank floor) |

- **An ash road on black rock is clear** (20-26), unlike the roads in sand and snow. The designer's road marks (Q28) still apply, and the roads here are dangerous (`open-world-content.md` F1).
- In M6 an ash lane is clear on bare rock (22) and closer to a plank floor (11).

### The scrap look against the ground

The drawing uses these colours: scrap look: rusted sheet roof `#7E5440`, scrap look: plank roof `#5A4A3E`, scrap look: scrap wall `#4E4640`, scrap look: forge glow `#E0904A`. `city-layouts.md` names the materials ("salvaged wood and metal, chains, scorched cloth, built on black rock"); the hex values are this board's proposal.

| Where | Settlement and look | Rusted sheet roof | Plank roof |
|---|---|---|---|
| Ember caldera | M6: scrap look | 32 main, 13 closest (Rock lit by lava) | 19 main, 10 closest (Grey ash) |
| M6, the lawless city | M6: scrap look | 32 main, 6 closest (Forge-lit ground) | 19 main, 6 closest (Scrap plank floor) |

- **A rusted roof reads on black rock** (32), and so does a plank roof (19). From above M6 is rust and wood patches on black.
- **Rust disappears in forge light:** a rusted roof is 6 from ground that a forge lights. Around the black market's yard the roofs and the lit ground come close.
- **A plank roof is 6 from a plank floor.** Salvaged boards overhead and underfoot are the same material. A walkway of boards beside a low plank roof needs something to part them: height, an edge, a shadow.

## The boards

### Ember caldera (`main.ember`)

**From the sheet:** fire. A dim red-orange glow from below, with a dark sky mood and floating embers. The volcano crater is the landmark. The roads here are dangerous.

**Palette**

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Black volcanic rock | `#2B2826` | ground | 30% | 12% | 17% |
| Dark-brown rock | `#3D322C` | ground | 18% | 28% | 24% |
| Grey ash | `#5A5654` | ground | 12% | 7% | 35% |
| Ash road | `#6A625C` | ground | 6% | 13% | 42% |
| Rock lit by lava | `#5C3A2E` | ground | 6% | 50% | 36% |
| Lava (glow) | `#D06A34` | lava | 8% | 75% | 82% |
| Lava (core) | `#EDB458` | lava | 2% | 63% | 93% |
| Cooling crust | `#7A3426` | lava | 4% | 69% | 48% |
| Obsidian | `#1E1F24` | prop | 10% | 17% | 14% |
| Scorched banner | `#6E4034` | accent | 4% | 53% | 43% |

**Light:** Glow from below `#E8824A`, Shadow tint `#2A2630`, Smoke haze `#4A3E3C`.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Black volcanic rock | `#2B2826` | 49 (125%) | 58 (137%) | 49 (117%) | 53 (147%) | 29 (169%) |
| Dark-brown rock | `#3D322C` | 42 (109%) | 52 (122%) | 48 (116%) | 49 (137%) | 25 (148%) |
| Grey ash | `#5A5654` | 34 (88%) | 42 (98%) | 38 (91%) | 39 (107%) | 15 (91%) |
| Ash road | `#6A625C` | 30 (78%) | 37 (86%) | 37 (90%) | 35 (97%) | 13 (77%) |
| Rock lit by lava | `#5C3A2E` | 31 (81%) | 44 (103%) | 52 (124%) | 48 (134%) | 24 (140%) |

**Ground, seen from the camera**
- Black and dark-brown volcanic rock, with grey ash. The sheet keeps the ground dark, so that red belongs to the lava and to the warnings.
- **The lava** is drawn as thin glowing lines and pools, and it is unwalkable. Its cooling crust is the dark red that comes close to a red warning.
- **Lava lines cut the ground into strips.** Large monsters need open ground with no gap under about 4 m (R-2.2-08), and the dragons and demons here are large. Their camps and the field boss need ground without lava lines.

**The glow from below**
- The sheet lights the biome from the lava and not from a sky. The preset here is a dim ambient light, with the lava as the glow.
- The designer accepted that reading for the Fungal hollow (Q29). Whether it holds here too is question 3: light from below also turns the usual shading upside down.
- **Floating embers** are an air effect over the ground. Warnings are drawn above them (Q8), and each ember is a small point (Q7).

**Props and the fade**
- **Lava channels with stone slabs across:** a slab is a path over unwalkable lava, so each crossing is a chokepoint. Paths are 3 m wide (R-11.6-03).
- **Chained bridges at ground level:** at ground level, as the sheet says, so they fit the one walkable layer. A bridge is 5 m wide (R-11.6-03).
- **Obsidian shards:** the tall ones fade. On black rock they are hard to see (above).
- **Scorched banners:** a dark red-brown cloth, muted (Q5).
- **The crater:** the camera never shows the horizon, so the volcano is never seen as a mountain. On screen the crater is a rim, which is a cliff with blockers (R-11.6-04), and a lake of lava below it. At the rim the lava would fill much of the screen (questions 1 and 6).

**A rest spot:** `open-world-content.md` gives each dangerous-road biome about one rest spot, shown as a campfire, a well or a shelter with a clear mark of its safe edge. A campfire does not stand out in a land of fire, so here the mark of the edge has to do the work.

**Settlement:** M6, below.

**Who lives here:** demons, with dragons and elementals as the shared families; fire. A fire-coloured creature beside lava and a dark creature on black rock both rely on their outline (R-9.14-05).

**Gathering:** rare ore and gems, which the map calls worth the PvP risk. Nodes are interactables, so they have an outline.

**Photo references**
- [Lava field near Tinajo](https://commons.wikimedia.org/wiki/File:Tinajo,_view_across_a_lava_field_to_Monta%C3%B1a_Teneza.JPG): a field of black lava rock: the ground. Dguendel, CC BY 4.0.
- [Lava fields near Yaiza](https://commons.wikimedia.org/wiki/File:Lava_fields_near_Yaiza.jpg): a field of rough dark-brown lava. Averater, CC BY-SA 3.0.
- [Lava from Kilauea](https://commons.wikimedia.org/wiki/File:Lava_from_Kilauea.jpg): glowing lava seen from above: bright lines between dark crust. Sasquatch (as Commons assumes), CC BY-SA 3.0.
- [Lava channel with overflows](https://commons.wikimedia.org/wiki/File:Image-Lava_channel_with_overflows_edit_3.jpg): a lava channel with its cooling crust. Brocken Inaglory, CC BY-SA 3.0.
- [Erta Ale lava lake, 2001](https://commons.wikimedia.org/wiki/File:Erta-ale_lac-de-lave_2001.jpg): a lava lake in a crater, with glowing cracks: the landmark. Hervé Sthioul, CC BY 2.5.
- [Lava lake, Nyiragongo](https://commons.wikimedia.org/wiki/File:Lava_Lake_Nyiragongo_2.jpg): a crater's lava lake under smoke; far brighter than the board. Cai Tjeenk Willink (Caitjeenk), CC BY-SA 3.0.
- [Crater rim, Vesuvius](https://commons.wikimedia.org/wiki/File:Crater_rim_volcano_Vesuvius_-_Campania_-_Italy_-_July_9th_2013_-_08.jpg): a crater's rim and inner wall. Norbert Nagel, CC BY-SA 3.0.
- [Obsidian Dome](https://commons.wikimedia.org/wiki/File:Obsidian_Dome_%28GeoDIL_number_-_560%29.jpg): a slope of obsidian. Dexter Perkins, CC0.
- [Clapper bridge, Graiguenamanagh](https://commons.wikimedia.org/wiki/File:Clapper_bridge_Graiguenamanagh_January_2024.jpg): stone slabs across a stream: the slab crossings over lava. A.-K. D., CC BY-SA 4.0.
- [Campfire](https://commons.wikimedia.org/wiki/File:Campfire_%282642914480%29.jpg): sparks rising from a fire: the floating embers. Tony Webster, CC BY 2.0.
- [Tattered flag](https://commons.wikimedia.org/wiki/File:Tattered_Flag_%286963955191%29.jpg): a tattered flag: the banners. Michael Coghlan, CC BY-SA 2.0.

### M6, the lawless city

**From `city-layouts.md` (a proposal):** medium-sized, about 90 m across, which is two to three screens. Scrap walls with a gate per road: north-west, north-east and south-west. The black market is in the middle, around a forge-lit yard. There is no safe square, and the layout has cover and more than one way out of every yard.

**Palette**

The shares are a rough share of the screen inside the city.

| Swatch | Hex | Role | Share | Saturation | Value |
|---|---|---|---|---|---|
| Bare rock street | `#2B2826` | ground | 22% | 12% | 17% |
| Packed ash lane | `#5E5855` | ground | 12% | 10% | 37% |
| Scrap plank floor | `#6A5646` | ground | 8% | 34% | 42% |
| Forge-lit ground | `#6E4630` | ground | 6% | 56% | 43% |
| Rusted sheet roof | `#7E5440` | roof | 20% | 49% | 49% |
| Salvaged plank roof | `#5A4A3E` | roof | 12% | 31% | 35% |
| Scrap wall | `#4E4640` | wall | 8% | 18% | 31% |
| Dark iron and chains | `#44464A` | prop | 5% | 8% | 29% |
| Scorched cloth | `#6E4034` | accent | 4% | 53% | 43% |
| Forge glow | `#E0904A` | accent | 3% | 67% | 88% |

**Light:** Forge glow `#E8924E`, Shadow tint `#2A2630`, Smoke haze `#4A3E3C`. It is the caldera's light, with forges in place of lava.

**Warnings on this ground**

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
| Bare rock street | `#2B2826` | 49 (125%) | 58 (137%) | 49 (117%) | 53 (147%) | 29 (169%) |
| Packed ash lane | `#5E5855` | 33 (85%) | 41 (95%) | 38 (91%) | 38 (106%) | 15 (89%) |
| Scrap plank floor | `#6A5646` | 30 (77%) | 37 (87%) | 45 (108%) | 38 (104%) | 16 (97%) |
| Forge-lit ground | `#6E4630` | 27 (68%) | 37 (88%) | 54 (129%) | 44 (121%) | 23 (133%) |

- Warnings belong to monster attacks (R-3.1-05). Whether monsters ever fight inside M6 is not written, so this table matters only if they do. Players' own attacks have no warning.

**From above**
- Rusted sheet and salvaged planks on black rock, lit by forges. The roofs are about a third of the picture.
- **The gates** have to look clearly different from a normal city's (`city-layouts.md`): chains, scorched banners, no guards. They are also where the protection warning shows (D11).
- **Scrap walls** fade when they hide the player, like any wall.

**Cover and the fade**
- The fade removes what stands between the camera and the player's **own** character (R-9.14-02). Whether it also applies to other players is Open (10.4).
- As the rule is written, a wall between my camera and another player stays solid. That player's own camera fades the same wall for them. So one player can see while the other cannot, until the second turns the camera.
- Everywhere else that is a detail. In M6, where PvP is always on and the layout is built on cover, it decides how cover works (question 4).
- The designer will choose the fade's look on a prototype street (Q9). M6 is a second case for that test.

**Pieces the look needs**

| Piece | Notes |
|---|---|
| Scrap house | A closed shell of salvaged wood and metal, on all four sides. Rusted sheet or plank roof. |
| Scrap wall and gate | A wall of mixed scrap, and three gates with chains and scorched banners. |
| Forge yard | The black market's yard, lit by forges. More than one way out. |
| Walkways | Plank floors over rough rock, and bare rock streets. |
| Dressing | Chains, anchors of iron, scorched cloth, crates. |

Which service buildings M6 has is Open (10.4), so this list has none.

**Photo references**
- [A shanty town seen from above](https://commons.wikimedia.org/wiki/File:ASC_Leiden_-_F._van_der_Kraaij_Collection_-_28_-_06_-_A_shanty_town_seen_from_above_from_Ducor_Hotel_-_Monrovia,_Liberia_-_1975.tif): sheet-metal roofs packed together, seen from above. Fred van der Kraaij, CC BY-SA 4.0.
- [Panorama of a shanty town](https://commons.wikimedia.org/wiki/File:ASC_Leiden_-_NSAG_-_van_Dis_5_-_092_-_Un_panorama_d%27un_bidonville_de_nombreuses_cabanes_aux_toits_de_chaume_-_Ghana_-_25_f%C3%A9vrier-2_mars_1962.tif): sheet roofs of many sizes, seen from a low height. Maarten van Dis, CC BY-SA 4.0.
- [Corrugated iron fence](https://commons.wikimedia.org/wiki/File:Corrugated_iron_fence_-_geograph.org.uk_-_1907531.jpg): a wall of rusted sheet along a lane: the scrap wall. ceridwen, CC BY-SA 2.0.
- [Pondalowie Bay shacks](https://commons.wikimedia.org/wiki/File:Pondalowie_Bay_shacks.jpg): shacks of boards and sheet metal. Peterdownunder, CC BY-SA 4.0.
- [Rusty corrugated iron sheet](https://commons.wikimedia.org/wiki/File:Rusty_corrugated_iron_sheet,_Mullaghslin_Glebe_-_geograph.org.uk_-_4827638.jpg): a rusted sheet. Kenneth  Allen, CC BY-SA 2.0.
- [Rusted corrugated sheet wall](https://commons.wikimedia.org/wiki/File:Wellblechschuppen,_H.J._Braun_Tonbergbau_e.K.,_Rulandsweg_5,_53347_Alfter-Heidgen_02.jpg): rust on sheet metal close up: the roof colour. KaiKemmann, CC BY-SA 4.0.
- [Piled high in the scrapyard](https://commons.wikimedia.org/wiki/File:Piled_high_in_the_scrapyard_-_geograph.org.uk_-_643382.jpg): piles of scrap. John M, CC BY-SA 2.0.
- [Heavy chains](https://commons.wikimedia.org/wiki/File:Heavy_chains_%285063154753%29.jpg): heavy rusted chain. Dave Nakayama, CC BY 2.0.
- [Traditional Blacksmith Forge](https://commons.wikimedia.org/wiki/File:Traditional_Blacksmith_Forge.jpg): a forge fire lighting a workshop: the forge glow. Bellina 09, CC BY-SA 4.0.
- [Night market stalls under lanterns](https://commons.wikimedia.org/wiki/File:DSCF1221_Busy_night_market_stalls_and_outdoor_dining_under_glowing_lanterns_and_a_decorative_Chinese-style_gate.jpg): stalls lit at night: the mood of the black market's yard, without its colours. PattayaPatrol, CC BY-SA 4.0.

## Borders of W4

From `biome-sheets.md` (proposals), with a 20-30 m blend of ground, props and light (R-2.3-11):

| Border | Natural edge | What blends |
|---|---|---|
| ash woods - ember | the ash deepens and the first lava cracks appear | grey ash (L 58) to black rock (L 16); smoke to glow |
| springs - ember | the terraces end at the caldera's outer slope | pale terraces (L 85) to black rock; steam to smoke |
| taiga - ember | melting snow and bare wet rock; the heat keeps the snow back | snow (L 92) to black rock |
| cliffs - ember | black rock on both sides; the glow starts | basalt (L 27) to black rock: the ground hardly changes, the light does |

- **Snow taiga to Ember caldera is the largest jump in lightness on the map** (L 92 to L 16), over one screen. The warnings go from their weakest ground to their strongest across it.
- The light changes most at the Storm cliffs, where the ground stays almost the same.

## Photo references: how they were picked

As on the other boards: free-licence files on Wikimedia Commons, found with `tools/art-boards/Find-PhotoRefs.ps1`, each looked at before it was listed, each with the author and licence that Commons states. They are **links only** (Q33). Nothing is stored in the repository, so `THIRD_PARTY_LICENSES.md` has no new row.

- Photos of lava are far brighter than the board. The palette is the target.
- The scrap look has no real-world twin in one photo. Its references are parts: roofs of sheet metal from above, rust, chains, a forge.

## Questions for the designer

Nothing below is decided here.

1. **How strong and how large may lava be?** The glow is at 75%, among the warning colours, and lava is 14% of the screen as lines and pools. At the crater it would fill much of the screen. The answer on fires (Q7) kept them as small points.
2. **The red warning beside lava:** it is 11 from the cooling crust. Does the crust move away from dark red (darker, browner), or does the warning get an edge on dark ground too? The answer on warnings (Q1) covers light ground.
3. **The light from below:** is a dim ambient light plus the lava's glow the one fixed light of this biome (R-9.14-04), as for the Fungal hollow (Q29)?
4. **Cover and the fade in M6:** does a wall hide another player from me while their own camera sees through it? Is M6 part of the fade prototype (Q9)?
5. **The foot of a scrap wall:** it is 8-14 from the street. Does the dark band at the foot of walls (Q20) apply to the scrap look?
6. **The crater:** is it a rim with a lava lake below, seen from above? Do players go down into it?
7. **The reviews:** the Ember caldera's sheet and M6's layout are proposals, and M6's services are Open (10.4). This board follows them as written.

## Not covered

- To-scale drawings of M6. Its layout is a proposal and its services are Open, so a plan would invent both.
- Characters, monsters, UI and the world map.
- How the colours behave in the engine. The swatches are targets, and the cel shader and the light presets have to reach them.
