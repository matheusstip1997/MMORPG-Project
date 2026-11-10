# Reference board: characters and monsters

Status: **art reference, v3 (2026-11-10)**, updated with the designer's answers to its questions and redrawn with the proportions the designer chose afterwards (about 4.5 to 5 heads tall). A board for the figures of the game: how large a character or a monster is on screen, what its black shape has to carry, and how an outline and a figure's colours read on the 20 grounds of the biome boards. **Every colour, shape and example size here is a proposal** for the designer to review. The drawing is `reference-board-figures.svg` (same folder).

This file is **not the spec** and decides nothing. The designer's answers of 2026-11-10 stand under the questions at the end, and as decisions A9 to A19 in `art-direction.md`; the findings above them are as they were when the questions were asked. It holds no character design: the figures in the drawing are schematic. The numbers are built by `tools/art-boards` the same way as the other boards (see "The exact method" in `reference-boards-w1.md`); colour differences are CIE76 Delta E between flat colours, and higher is easier to see. They show where to look first; they are not a pass or fail.

**Every size on screen follows from the placeholder camera values** in `ClientCamera` (distance 18 m, look-at height 1 m, vertical field of view 40 degrees), which are Open (10.4). If they change, the sizes change with them. The camera values and the monster radii are those of `ClientCamera` and `MonsterDef` on 2026-11-10; the build warns when the tables no longer match. The 45-degree pitch is fixed (R-2.2-05).

## What is decided and what is open

**Decided** (spec):
- **Style:** stylized toon with an anime look for the characters; today's anime-style avatars are placeholders (R-9.14-01).
- **Proportions:** about 4.5 to 5 heads tall (the designer's answer of 2026-11-10, A20 in `art-direction.md`; it was about 6 heads when this board was first written), with hands and weapons larger than real life. Classes and weapons must be told apart by silhouette (R-9.14-06).
- **Size:** a character is 1.8 m tall with a body radius of 40 cm (R-11.6-01).
- **Outlines:** players, NPCs, monsters, bosses, loot, resource nodes, doors and portals get one; the world gets none (R-9.14-05).
- **Armour tier per class:** Warrior heavy, Assist medium, Archer and Mage light (R-3.5-08). The tier decides which warnings a player can parry (R-3.3-04).
- **Monster size classes:** small (body radius up to 60 cm), large (up to 100 cm) and boss (over 100 cm) (R-2.2-08).
- **Monster families by biome:** each biome has a signature family and one or two shared ones (R-14.2-08); which ones is on the accepted biome sheets (R-2.3-15).

**Guidelines of `art-direction.md`** (proposals, not rules): each class and weapon type should be recognisable as a black shape at 100 px; the armour tier should read from the silhouette; one outline width and style for everything.

**Open when this board was written** (see the answers at the end):
- **The outline's colour** for hostility or rarity (10.4). Answered: the outline stays neutral (Q43).
- **The camera values** (10.4). Still open.
- **Style references:** the designer will share them later (Q32 of `reference-boards-questions.md`), so this board names none. Named on 2026-11-10 (Q53, question 12 below).
- **Element colours:** the ten elements have no colour in the data. The effects keep their natural hues (Q50); the colours themselves are not chosen yet.

## Findings

### How large a figure is on screen

At the middle of the screen, where the player's own character always stands:

| What | Real size | At 1080p | At 720p |
|---|---|---|---|
| Character, standing | 1.8 m tall | 104 px | 69 px |
| Character, across the shoulders | 0.8 m wide | 67 px | 45 px |
| Head | 0.38 m (4.5 to 5 heads tall) | 32 px | 21 px |
| A hand | 12 cm (example) | 10 px | 7 px |
| A sword blade held upright | 1 m long (example) | 60 px | 40 px |
| The same blade, across | 8 cm wide (example) | 7 px | 4 px |
| A staff | 2 m tall (example) | 117 px | 78 px |
| A round shield | 0.6 m across (example) | 50 px | 33 px |
| Small monster, the smallest in the data | body radius 45 cm | 71 px | 48 px |
| Small monster, at the limit | body radius 60 cm | 95 px | 63 px |
| Large monster, at the limit | body radius 100 cm | 159 px | 106 px |
| Boss, the largest in the data | body radius 140 cm | 222 px | 148 px |

- **One metre across is 82 px at 1080p, and one pixel is 1.2 cm.**
- **A character is 104 px tall and 67 px wide,** about one and a half times as tall as wide. A standing person is more than twice as tall as wide. The 45-degree camera shortens heights and leaves widths as they are, so every figure looks stocky.
- **So width reads more than height:** shoulders, a shield, anything held out to the side. Height differences are shortened to about 0.7 of their size.
- `art-direction.md` says "about 90-100 px". The exact projection with its placeholder values gives 104 px at 1080p and 69 px at 720p.
- **A hand is 10 px and a sword blade 7 px wide at their real size**, and 7 and 4 px at 720p. This is why the rule enlarges heads, hands and weapons; how much is question 5.
- **The head is 32 px wide** at 1080p and 21 px at 720p. That is a 38 cm head, the middle of what 4.5 to 5 heads tall gives (36-40 cm). With 6 heads it was 30 cm and 25 px.
- **On the light tier the head is nearly as wide as the shoulders** in the drawing (32 px against 39 px), so there the shoulders stand out less than they did with the smaller head. Headgear (question 3) gains room.
- **Monsters:** the footprint of the smallest monster today is as wide as a character, and the largest boss is 222 px wide, more than twice a character's height. **Monster heights are not in the data**, only the body radius (question 6).

The same character at other places on the screen:

| Where the character stands | Height on screen | Width on screen |
|---|---|---|
| Near the bottom edge of the screen (6 m toward the camera) | 94 px | 88 px |
| In the middle, where the own character always is | 104 px | 67 px |
| 8 m ahead | 98 px | 51 px |
| Near the top edge of the screen (12 m ahead) | 94 px | 45 px |

- **Height hardly changes across the screen (94-104 px), width does (45-88 px).** A figure near the top edge is about half as wide as one near the bottom edge.
- Other players and monsters are seen at every one of these sizes. A shape has to work near the top edge, where it is narrowest.
- **Beyond about 12.9 m ahead the head leaves the screen.** At 16 m only the feet still show, so the table stops at 12 m.

### The armour tier in the black shape

The drawing gives the three tiers these shoulder widths. They are this board's proposal; no rule gives a width.

| Armour tier | Shoulder width in the drawing | At 1080p | At 720p |
|---|---|---|---|
| light | 0.46 m | 39 px | 26 px |
| medium | 0.60 m | 50 px | 34 px |
| heavy | 0.78 m | 65 px | 44 px |

- **The steps between the tiers are about 11 and 15 px at 1080p, and 8 and 10 px at 720p.** Near the top edge of the screen they are about two thirds of that.
- The tier decides what a player can parry, so it has to read on the player's own character and on others.
- **A Mage and an Assist can both carry a staff** (`skill-trees.json`). With the same weapon, the black shapes differ by the armour alone: about 11 px of shoulder width (question 4).
- **Weapons carry the class.** Sword and shield, bow, staff and gloves are four different shapes. Gloves are the smallest mark: two discs of about 23 px.
- **From this camera the top of a figure shows most:** the head and the shoulders. Hats, hoods, shoulder plates and things on the back are where a class can be marked. The drawing uses none of these, because none is decided.

### One outline on 20 grounds

A dark and a light outline against the main ground of every biome, from the darkest to the lightest:

| Biome | Main ground | L | Dark outline | Light outline |
|---|---|---|---|---|
| Ember caldera | Black volcanic rock `#2B2826` | 16 | 7 | 78 |
| Fungal hollow | Spongy dark soil `#3A3438` | 22 | 11 | 73 |
| Old forest | Dark earth `#3F3A33` | 25 | 15 | 70 |
| Storm cliffs | Black-grey basalt `#3E4044` | 27 | 16 | 68 |
| Fern jungle | Dark mud `#4A4036` | 28 | 19 | 67 |
| Reed marsh | Wet earth `#6A5E4C` | 41 | 33 | 55 |
| Bone canyons | Gravel floor (shadow) `#766E6E` | 47 | 36 | 48 |
| Amber woods | Leaf litter (rust) `#94694E` | 48 | 46 | 52 |
| Heather moor | Heather `#8A7A8C` | 53 | 43 | 44 |
| Lake country | Wet grass `#76865F` | 54 | 50 | 45 |
| Green vale | Short grass (olive) `#8A8E60` | 58 | 54 | 43 |
| Ash woods | Grey ash `#8E8B86` | 58 | 47 | 37 |
| Red sands | Sand (dusty tan) `#C4A886` | 70 | 64 | 30 |
| Dry steppe | Pale dry grass `#C2B88C` | 75 | 69 | 28 |
| Dune isle | Cream sand `#D9CBAA` | 82 | 74 | 19 |
| Palm coast | Pale sand `#DDCBA4` | 82 | 75 | 22 |
| Steam springs | Terrace (cream) `#DDD2BC` | 85 | 75 | 13 |
| Rime coast | Packed snow `#E3E6E7` | 91 | 80 | 6 |
| Snow taiga | Fresh snow `#E4E8EA` | 92 | 80 | 6 |
| Frost peaks | Deep snow `#ECEFF1` | 94 | 83 | 5 |

- **A dark outline is 7-19 from the five darkest grounds** (Ember caldera, Fungal hollow, Old forest, Storm cliffs, Fern jungle) and 33 or more from the others.
- **A light outline is 5-6 from snow, 13 from the Steam springs' terraces and 19-22 from the pale sands.** On the Dry steppe and the Red sands it is 28-30, and 37 or more on the darker grounds.
- **No single outline colour is clear on all 20 grounds.** The grounds in the middle take either.
- Ways out that would have to be chosen (question 1): an outline that changes with the ground under the figure; a two-tone outline, dark with a thin light rim; or a contact shadow under every figure in addition to the outline.
- **A coloured outline has the same problem on every ground.** Whether hostility or rarity changes the outline's colour is Open (10.4). A colour for "hostile" would also sit beside the warning hues (question 2).
- The designer already answered the same kind of question for warnings: on light ground they get a dark edge or a stronger fill (Q1).

### A figure's tones against the grounds

Four base tones a character could wear, against every main ground:

| Biome | L | Steel plate `#8C929A` | Leather and mail `#6E5A46` | Pale cloth `#CEC6B2` | Dark cloth `#3C4250` |
|---|---|---|---|---|---|
| Ember caldera | 16 | 44 | 27 | 64 | 16 |
| Fungal hollow | 22 | 38 | 24 | 59 | 10 |
| Old forest | 25 | 37 | 18 | 56 | 15 |
| Storm cliffs | 27 | 33 | 22 | 55 | 7 |
| Fern jungle | 28 | 35 | 14 | 52 | 17 |
| Reed marsh | 41 | 26 | 4 | 40 | 25 |
| Bone canyons | 47 | 15 | 16 | 35 | 22 |
| Amber woods | 48 | 33 | 14 | 37 | 39 |
| Heather moor | 53 | 13 | 26 | 34 | 27 |
| Lake country | 54 | 28 | 23 | 30 | 41 |
| Green vale | 58 | 30 | 25 | 27 | 46 |
| Ash woods | 58 | 8 | 22 | 23 | 32 |
| Red sands | 70 | 29 | 32 | 15 | 53 |
| Dry steppe | 75 | 32 | 37 | 14 | 57 |
| Dune isle | 82 | 32 | 43 | 8 | 61 |
| Palm coast | 82 | 35 | 43 | 11 | 63 |
| Steam springs | 85 | 30 | 45 | 5 | 61 |
| Rime coast | 91 | 31 | 54 | 16 | 64 |
| Snow taiga | 92 | 32 | 55 | 17 | 64 |
| Frost peaks | 94 | 34 | 57 | 19 | 67 |

- **Every tone disappears somewhere:**
  - steel is 8 from the Ash woods' grey ash, 13 from heather and 15 from the canyons' gravel in shadow;
  - leather is 4 from the Reed marsh's wet earth and 14 from the mud of the Fern jungle and the litter of the Amber woods;
  - pale cloth is 5 from the Steam springs' terraces, 8-11 from the pale sands and 14-19 from the steppe, the Red sands and snow;
  - dark cloth is 7 from the Storm cliffs' basalt, 10 from the Fungal hollow's soil and 15-17 from the Old forest, the caldera and the jungle.
- **A figure in one tone is not safe on the whole map. A figure with a light and a dark part always has one part that reads.** Player characters go everywhere, so their gear should carry both.
- **The Reed marsh is C3's start biome**, and leather on wet earth is the weakest pair on this table (4).

### Monsters on their home ground

A tone for each family, as a starting point to measure with:

| Family | Tone | Hex | Saturation | Value | Signature family of |
|---|---|---|---|---|---|
| beast | Fur brown | `#6E5A48` | 35% | 43% | Green vale, Old forest, Red sands, Reed marsh, Snow taiga |
| plant | Leaf green | `#5E7046` | 38% | 44% | Amber woods, Fern jungle |
| undead | Bone | `#B9B4A2` | 12% | 73% | Heather moor, Bone canyons |
| humanoid | Leather and mail | `#6E5A46` | 36% | 43% | Lake country, Dry steppe, Palm coast |
| aberration | Violet grey | `#6C5C7A` | 25% | 48% | Fungal hollow |
| dragon | Scale teal | `#4A6868` | 29% | 41% | Frost peaks |
| demon | Dark red-brown | `#6A3A34` | 51% | 42% | Ash woods, Ember caldera |
| elemental | The material of its biome (ice, sand, steam, storm) | - | - | - | Rime coast, Dune isle, Steam springs, Storm cliffs |

- **Fur brown and "Leather and mail" are the same colour to the eye** (a difference of 1), so beasts and humanoids share a tone here and their cells below hardly differ. The table gives a starting tone per family to measure with, not a colour that tells the families apart.

Each biome's families against its main ground:

| Biome | Main ground | Signature family | Shared families |
|---|---|---|---|
| Ember caldera | Black volcanic rock `#2B2826` | demon 26 | dragon 29; elemental (the biome's own material) |
| Fungal hollow | Spongy dark soil `#3A3438` | aberration 25 | plant 37 |
| Old forest | Dark earth `#3F3A33` | beast 18 | plant 30; aberration 29 |
| Storm cliffs | Black-grey basalt `#3E4044` | elemental (the biome's own material) | aberration 23 |
| Fern jungle | Dark mud `#4A4036` | plant 28 | beast 14; aberration 28 |
| Reed marsh | Wet earth `#6A5E4C` | beast 4 | plant 19 |
| Bone canyons | Gravel floor (shadow) `#766E6E` | undead 28 | dragon 16 |
| Amber woods | Leaf litter (rust) `#94694E` | plant 29 | beast 15 |
| Heather moor | Heather `#8A7A8C` | undead 29 | beast 25 |
| Lake country | Wet grass `#76865F` | humanoid 23 | beast 24 |
| Green vale | Short grass (olive) `#8A8E60` | beast 25 | plant 14 |
| Ash woods | Grey ash `#8E8B86` | demon 36 | undead 17 |
| Red sands | Sand (dusty tan) `#C4A886` | beast 32 | undead 14 |
| Dry steppe | Pale dry grass `#C2B88C` | humanoid 37 | beast 37 |
| Dune isle | Cream sand `#D9CBAA` | elemental (the biome's own material) | humanoid 43 |
| Palm coast | Pale sand `#DDCBA4` | humanoid 43 | beast 44 |
| Steam springs | Terrace (cream) `#DDD2BC` | elemental (the biome's own material) | demon 58 |
| Rime coast | Packed snow `#E3E6E7` | elemental (the biome's own material) | beast 54 |
| Snow taiga | Fresh snow `#E4E8EA` | beast 54 | humanoid 55 |
| Frost peaks | Deep snow `#ECEFF1` | dragon 54 | elemental (the biome's own material) |

- **A family that lives in several biomes cannot keep one tone.** Beasts live in 12 biomes, and fur brown is 4 from the Reed marsh's wet earth, 18 from the Old forest's earth and 54 from snow (question 7).
- **Elementals are made of their biome's material** (ice, sand, steam, storm). They are the signature family of four biomes and a shared family of two more, and on purpose they have the colour of the ground. They rely on the outline, on glow and on movement (question 8).
  - Three of their home grounds are pale (Rime coast, Dune isle, Steam springs), where a dark outline works (74-80).
  - The fourth is the Storm cliffs' basalt, where a dark outline is 16.
- **Other weak pairs:** dragons in the Bone canyons (16), bone on the Red sands (14) and on ash (17), plants on the Green vale's grass (14), beasts in the Fern jungle and the Amber woods (14-15).
- **Large monsters and bosses stand on open ground** (R-2.2-08), which on several biomes is the lightest ground: snowfields, sand, terraces.
- **Elements have no colours yet.** Fire, lightning, ice and water, and poison naturally take red-orange, yellow, blue and green, which are the four warning hues. R-9.14-03 counts spell effects among the bright things of the game, as it does the warnings (question 9).

## The drawing

`reference-board-figures.svg` has three parts:

1. **Sizes at pixel scale:** one pixel of the drawing is one pixel of a 1080p screen. A character with a 100 px ruler, and the footprints of four monsters from the smallest in the data to the largest boss.
2. **The root classes as black shapes,** at screen size and enlarged. They are built as the camera sees them: widths at full size, heights and depths shortened by the 45-degree pitch. The Assist is drawn twice, with a staff and with gloves.
3. **One figure with a dark and with a light outline** on the lightest, a middle and the darkest main ground.

The shapes are schematic: a head, shoulders, a torso, arms, legs and a weapon. They are drawn about 4.75 heads tall, with a 38 cm head. They are not character designs.

## Placeholder art today

`THIRD_PARTY_LICENSES.md` lists the placeholders in the game now: one sample anime avatar per root class, a few weapons (a two-handed sword, a crossbow, a staff, a wand, a round shield) and three monsters (a demon, an alien and a cyclops). R-9.14-01 keeps the avatars as placeholders. This board does not review them.

## Photo references

Links to free-licence files on Wikimedia Commons, each looked at before it was listed, with the author or source and the licence that Commons states. No image is stored in the repository (Q33), so `THIRD_PARTY_LICENSES.md` has no new row. They are references for shape and bulk, not for style; the designer's style references are under question 12.

**People seen from above**
- [Busy street seen from above, Vieux-Quebec](https://commons.wikimedia.org/wiki/File:Busy_street_seen_from_above,_Vieux-Qu%C3%A9bec.jpg): people in a street seen from high up: heads, shoulders and what they carry are what shows. Wilfredor, CC BY-SA 4.0.
- [People on a beach seen from above](https://commons.wikimedia.org/wiki/File:People_on_the_beach,_Man_o%27_War_Cove_seen_from_above,_West_Lulworth,_2024.jpg): small figures from far above: at this size a person is a shape and a shadow. DimiTalen, CC0.
- [Armoured fighters in a square, Mdina](https://commons.wikimedia.org/wiki/File:MEDIEVAL_MDINA_%283470324263%29.jpg): armoured and unarmoured people side by side, seen from a little above. KNOW MALTA by Peter Grima, CC BY-SA 2.0.

**Armour tiers**
- [A suit of plate armour, Spis Castle](https://commons.wikimedia.org/wiki/File:Spisska_Hrad_030.jpg): plate armour on a stand: the heavy tier. JoJan, CC BY 3.0.
- [Pair of tournament pauldrons](https://commons.wikimedia.org/wiki/File:Pair_of_Tournament_Pauldrons_%28Shoulder_Defenses%29_MET_161318.jpg): shoulder plates: where heavy armour adds the most width. The Metropolitan Museum of Art (the armour is by Lorenz Helmschmied), CC0.
- [Hauberk, 15th century](https://commons.wikimedia.org/wiki/File:European,_15th_century_-_Hauberk_-_1916.1543_-_Cleveland_Museum_of_Art.tif): a mail shirt: the medium tier. Cleveland Museum of Art, CC0.
- [Italian brigandine body armour](https://commons.wikimedia.org/wiki/File:Italian_brigandine_body_armour_%2845283922515%29.jpg): a brigandine: the medium tier, with a clear chest shape. Thomas Quine, CC BY 2.0.
- [Gambeson](https://commons.wikimedia.org/wiki/File:Gambeson.jpg): a padded coat: the light tier. David Ring, CC0.
- [Knights](https://commons.wikimedia.org/wiki/File:Rytieri_-_knights_-_panoramio.jpg): two people in plate, for the bulk that armour adds to a body. Miroslav Blaho, CC BY-SA 3.0.

**Weapons**
- [Longsword, Levoca town hall](https://commons.wikimedia.org/wiki/File:Spi%C5%A1_Museum_in_Levo%C4%8Da_-_Levo%C4%8Da_Town_Hall_permanent_exhibit_-_Longsword,_second_half_of_the_14th_century_and_15th_century.jpg): a longsword: its length against a person. ZemplinTemplar, CC BY-SA 4.0.
- [Gauntlet for the right hand](https://commons.wikimedia.org/wiki/File:Gauntlet_for_the_Right_Hand_MET_29.150.91h_002Sept2014.jpg): an armoured glove. The Metropolitan Museum of Art, CC0.

## Questions for the designer, and the answers

The designer answered these on 2026-11-10. The questions stand as they were asked, so that the references to them in this file still hold. The full list is `reference-boards-questions.md`, and the "Q" numbers refer to it. The answers are also decisions A9 to A19 in `art-direction.md`.

1. **The outline on dark and on light ground:** one colour cannot be clear on all 20 grounds. Does the outline change with the ground, is it two-tone, or does something else separate a figure from the ground?
   - **Answer (Q42):** a two-tone outline. A dark line with a thin light rim outside it, the same on every figure on every ground, so one of the two always reads. Its two tones and widths are still to be tuned.
2. **Hostility or rarity in the outline** (Open in 10.4): if the outline carries a colour, how does it stay clear on every ground and apart from the warning hues?
   - **Answer (Q43):** the outline stays neutral. It only separates a figure from the ground. Hostility and rarity are shown elsewhere (name plates, markers, loot glow).
3. **The armour tier in the silhouette:** is shoulder width the mark, and are steps of about 11 and 15 px (8 and 10 at 720p) enough? What else marks the tier?
   - **Answer (Q44):** shoulders plus headgear. The tier reads from the shoulder width and from a second mark on the head, which the camera shows best. Examples, not designs: a full helmet for heavy, a hood or cap for medium, a bare head or a hat for light.
4. **A Mage and an Assist with a staff:** what tells them apart besides the armour?
   - **Answer (Q45):** staff shape and clothing. Each of the two classes gets its own staff shape and its own garment or headgear shape.
5. **How much larger than life are hands and weapons?** R-9.14-06 says "slightly". At real size a hand is 10 px and a blade 7 px wide at 1080p.
   - **Answer (Q46):** weapons about 1.5 times real size, hands about 1.25 times. A blade is then about 10 px wide and a hand about 12 px at 1080p. The head follows the proportions the designer chose afterwards (question 12): about 4.5 to 5 heads tall gives a head of 36-40 cm. Whether a long weapon (a staff, a bow) also grows in length is left to the concept art.
6. **Monster heights:** only the body radius is defined. How tall is a small monster, a large one and a boss?
   - **Answer (Q47):** a height range per size class, as a guide for artists. The numbers are still to come, as a proposal for the designer.
7. **Families in several biomes:** a tone per biome (a snow wolf, a marsh snake), or one family tone with a marking that contrasts?
   - **Answer (Q48):** a local variant with a family marking. Each biome gets its own variant in a tone that contrasts with that biome's ground, and the family keeps one marking or shape that is the same everywhere.
8. **Elementals have their ground's colour:** are the outline and a glow enough, or do they get a core in another colour?
   - **Answer (Q49):** a core in another colour. Besides the outline and a glow, an elemental has a core, eyes or cracks in a colour that contrasts with the ground, so there is always a point to aim at.
9. **Element colours:** do the effects of fire, lightning, ice and poison use the warning hues, and what keeps them apart from warnings?
   - **Answer (Q50):** natural hues, a different form. Element effects keep their natural colours. Warnings stay apart by form: flat shapes on the ground with a clear edge, drawn above effects in the air (Q8), while element effects are in the air, small and soft (Q7).
10. **The disguise robe** (R-6.2-01) hides the name, the honor and the red-name flag. Does it also hide the class and the armour tier in the silhouette?
   - **Answer (Q51):** the robe hides the class and the armour tier. One robe shape for everyone covers the body, so neither can be read from the shape. What an observer sees who passes the Detect check (R-6.2-03), or who is in the wearer's party or guild (R-6.2-04), was not asked.
11. **The smallest screen:** is 720p, with a 69 px character, a target?
   - **Answer (Q52):** 1080p is the target, smaller screens are supported. Shapes and marks are tuned for 1080p. 720p and the Steam Deck's screen must stay playable, but marks are not designed for a 69 px character.
12. **Style references** (Q32) are still to come.
   - **Answer (Q53):** the designer named four: **Grand Chase Mobile, Tree of Savior, Ragnarok and Final Fantasy** ("Are good one for the reference"). They are references for the look only; nothing from them is used or copied.
   - **Three follow-up answers (Q54 to Q56):** Final Fantasy means **Final Fantasy XIV**. The four are references for **characters, class costumes and weapons, monsters, and the world, its colours and effects**. And **characters are about 4.5 to 5 heads tall**, in between the 6 heads decided before and the much shorter figures of three of the references; the 1.8 m height and the body size stay as they are. This board's size table and drawing use the new proportions.

## Not covered

- Character designs: faces, hair, costumes, the 32 classes one by one.
- Monster designs.
- Animation, and how a figure reads while it moves.
- Name plates, bars and markers over a figure (spec 9).
- Mounts and pets.
- A review of the placeholder art.
