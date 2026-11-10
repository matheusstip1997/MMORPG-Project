# Reference board: characters and monsters

Status: **art reference, v1 (2026-11-10)**. A board for the figures of the game: how large a character or a monster is on screen, what its black shape has to carry, and how an outline and a figure's colours read on the 20 grounds of the biome boards. **Every colour, shape and example size here is a proposal** for the designer to review. The drawing is `reference-board-figures.svg` (same folder).

This file is **not the spec** and decides nothing. It holds no character design: the figures in the drawing are schematic. The numbers are built by `tools/art-boards` the same way as the other boards (see "The exact method" in `reference-boards-w1.md`); colour differences are CIE76 Delta E between flat colours, and higher is easier to see. They show where to look first; they are not a pass or fail.

**Every size on screen follows from the placeholder camera values** in `ClientCamera` (distance 18 m, look-at height 1 m, vertical field of view 40 degrees), which are Open (10.4). If they change, the sizes change with them. The 45-degree pitch is fixed (R-2.2-05).

## What is decided and what is open

**Decided** (spec):
- **Style:** stylized toon with an anime look for the characters; today's anime-style avatars are placeholders (R-9.14-01).
- **Proportions:** about 6 heads tall, with heads, hands and weapons slightly larger than real life. Classes and weapons must be told apart by silhouette (R-9.14-06).
- **Size:** a character is 1.8 m tall with a body radius of 40 cm (R-11.6-01).
- **Outlines:** players, NPCs, monsters, bosses, loot, resource nodes, doors and portals get one; the world gets none (R-9.14-05).
- **Armour tier per class:** Warrior heavy, Assist medium, Archer and Mage light (R-3.5-08). The tier decides which warnings a player can parry (R-3.3-04).
- **Monster size classes:** small (body radius up to 60 cm), large (up to 100 cm) and boss (over 100 cm) (R-2.2-08).
- **Monster families by biome:** each biome has a signature family and one or two shared ones (R-14.2-08); which ones is on the accepted biome sheets (R-2.3-15).

**Guidelines of `art-direction.md`** (proposals, not rules): each class and weapon type should be recognisable as a black shape at 100 px; the armour tier should read from the silhouette; one outline width and style for everything.

**Open:**
- **The outline's colour** for hostility or rarity (10.4).
- **The camera values** (10.4).
- **Style references:** the designer will share them later (Q32 of `reference-boards-questions.md`), so this board names none.
- **Element colours:** the ten elements have no colour in the data.

## Findings

### How large a figure is on screen

At the middle of the screen, where the player's own character always stands:

{{SIZES}}

- **One metre across is {{PXPERM}} px at 1080p, and one pixel is {{CMPERPX}} cm.**
- **A character is {{CHARPX}} px tall and {{CHARWPX}} px wide,** about one and a half times as tall as wide. A standing person is more than twice as tall as wide. The 45-degree camera shortens heights and leaves widths as they are, so every figure looks stocky.
- **So width reads more than height:** shoulders, a shield, anything held out to the side. Height differences are shortened to about 0.7 of their size.
- `art-direction.md` says "about 90-100 px". The exact projection with its placeholder values gives 104 px at 1080p and 69 px at 720p.
- **A hand is 10 px and a sword blade 7 px wide at their real size**, and 7 and 4 px at 720p. This is why the rule enlarges heads, hands and weapons; how much is question 5.
- **Monsters:** the footprint of the smallest monster today is as wide as a character, and the largest boss is 222 px wide, more than twice a character's height. **Monster heights are not in the data**, only the body radius (question 6).

The same character at other places on the screen:

{{DEPTH}}

- **Height hardly changes across the screen (89-104 px), width does (41-88 px).** A figure at the far edge is less than half as wide as one at the near edge.
- Other players and monsters are seen at every one of these sizes. A shape has to work at the far edge, where it is narrowest.

### The armour tier in the black shape

The drawing gives the three tiers these shoulder widths. They are this board's proposal; no rule gives a width.

{{SHOULDERS}}

- **The steps between the tiers are about 11 and 15 px at 1080p, and 8 and 10 px at 720p.** At the far edge of the screen they are about half of that.
- The tier decides what a player can parry, so it has to read on the player's own character and on others.
- **A Mage and an Assist can both carry a staff** (`skill-trees.json`). With the same weapon, the black shapes differ by the armour alone: about 11 px of shoulder width (question 4).
- **Weapons carry the class.** Sword and shield, bow, staff and gloves are four different shapes. Gloves are the smallest mark: two discs of about 23 px.
- **From this camera the top of a figure shows most:** the head and the shoulders. Hats, hoods, shoulder plates and things on the back are where a class can be marked. The drawing uses none of these, because none is decided.

### One outline on 20 grounds

A dark and a light outline against the main ground of every biome, from the darkest to the lightest:

{{OUTLINE}}

- **A dark outline is 7-19 from the five darkest grounds** (Ember caldera, Fungal hollow, Old forest, Storm cliffs, Fern jungle) and 33 or more from the others.
- **A light outline is 5-6 from snow, 13 from the Steam springs' terraces and 19-22 from the pale sands.** On the Dry steppe and the Red sands it is 28-30, and 37 or more on the darker grounds.
- **No single outline colour is clear on all 20 grounds.** The grounds in the middle take either.
- Ways out that would have to be chosen (question 1): an outline that changes with the ground under the figure; a two-tone outline, dark with a thin light rim; or a contact shadow under every figure in addition to the outline.
- **A coloured outline has the same problem on every ground.** Whether hostility or rarity changes the outline's colour is Open (10.4). A colour for "hostile" would also sit beside the warning hues (question 2).
- The designer already answered the same kind of question for warnings: on light ground they get a dark edge or a stronger fill (Q1).

### A figure's tones against the grounds

Four base tones a character could wear, against every main ground:

{{TONES}}

- **Every tone disappears somewhere:**
  - steel is 8 from the Ash woods' grey ash, 13 from heather and 15 from the canyons' gravel in shadow;
  - leather is 4 from the Reed marsh's wet earth and 14 from the mud of the Fern jungle and the litter of the Amber woods;
  - pale cloth is 5 from the Steam springs' terraces, 8-11 from the pale sands and 14-19 from the steppe, the Red sands and snow;
  - dark cloth is 7 from the Storm cliffs' basalt, 10 from the Fungal hollow's soil and 15-17 from the Old forest, the caldera and the jungle.
- **A figure in one tone is not safe on the whole map. A figure with a light and a dark part always has one part that reads.** Player characters go everywhere, so their gear should carry both.
- **The Reed marsh is C3's start biome**, and leather on wet earth is the weakest pair on this table (4).

### Monsters on their home ground

A tone for each family, as a starting point to measure with:

{{FAMILIES}}

Each biome's families against its main ground:

{{HOME}}

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

The shapes are schematic: a head, shoulders, a torso, arms, legs and a weapon. They are not character designs.

## Placeholder art today

`THIRD_PARTY_LICENSES.md` lists the placeholders in the game now: one sample anime avatar per root class, a few weapons (a two-handed sword, a crossbow, a staff, a wand, a round shield) and three monsters (a demon, an alien and a cyclops). R-9.14-01 keeps the avatars as placeholders. This board does not review them.

## Photo references

Links to free-licence files on Wikimedia Commons, each looked at before it was listed, with the author or source and the licence that Commons states. No image is stored in the repository (Q33), so `THIRD_PARTY_LICENSES.md` has no new row. They are references for shape and bulk, not for style: the designer will name style references later (Q32).

**People seen from above**
{{REFS:above}}

**Armour tiers**
{{REFS:armour}}

**Weapons**
{{REFS:weapons}}

## Questions for the designer

Nothing below is decided here.

1. **The outline on dark and on light ground:** one colour cannot be clear on all 20 grounds. Does the outline change with the ground, is it two-tone, or does something else separate a figure from the ground?
2. **Hostility or rarity in the outline** (Open in 10.4): if the outline carries a colour, how does it stay clear on every ground and apart from the warning hues?
3. **The armour tier in the silhouette:** is shoulder width the mark, and are steps of about 11 and 15 px (8 and 10 at 720p) enough? What else marks the tier?
4. **A Mage and an Assist with a staff:** what tells them apart besides the armour?
5. **How much larger than life are hands and weapons?** R-9.14-06 says "slightly". At real size a hand is 10 px and a blade 7 px wide at 1080p.
6. **Monster heights:** only the body radius is defined. How tall is a small monster, a large one and a boss?
7. **Families in several biomes:** a tone per biome (a snow wolf, a marsh snake), or one family tone with a marking that contrasts?
8. **Elementals have their ground's colour:** are the outline and a glow enough, or do they get a core in another colour?
9. **Element colours:** do the effects of fire, lightning, ice and poison use the warning hues, and what keeps them apart from warnings?
10. **The disguise robe** (R-6.2-01) hides the name, the honor and the red-name flag. Does it also hide the class and the armour tier in the silhouette?
11. **The smallest screen:** is 720p, with a 69 px character, a target?
12. **Style references** (Q32) are still to come.

## Not covered

- Character designs: faces, hair, costumes, the 32 classes one by one.
- Monster designs.
- Animation, and how a figure reads while it moves.
- Name plates, bars and markers over a figure (spec 9).
- Mounts and pets.
- A review of the placeholder art.
