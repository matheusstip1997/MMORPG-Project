# Reference board: build step W4 (Ember caldera and M6, the lawless city)

Status: **art reference, v1 (2026-11-10)**. A board for the fourth build step of the main map (`world-map.md` W4): the Ember caldera, which is the last of the 20 biomes, and M6, the lawless city, in its scrap look. **Every colour, material note and photo choice here is a proposal** for the designer to review. The drawing is `reference-board-w4.svg` (same folder).

This file is **not the spec** and decides nothing. It is built and checked the same way as the other boards: see "What a board holds", "How the palettes were checked" and "The exact method" in `reference-boards-w1.md`. The numbers are colour differences (CIE76 Delta E; higher is easier to see) between flat colours. They show where to look first; they are not a pass or fail.

New on this board: the table of every biome is complete (20 of 20), and a table puts each warning beside the lava.

## What is decided and what is a proposal

**Decided** (spec and designer):
- **The Ember caldera is the fire biome**, with a volcano as its landmark (`world-map.md` D6), and it hosts M6.
- **M6 is the lawless city:** PvP is always on, it is open to everyone, and bad-honor players are safe from guards there (R-6.3-01). The black market is there, for bad-honor players only (R-4.4-02). Entering shows a warning, and the new-player protection is off inside (`world-map.md` D11).
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
- **M6's layout** in `city-layouts.md`: medium-sized, scrap walls with a gate per road (three roads), the black market in the middle around a forge-lit yard, no safe square, cover and more than one way out of every yard, and gates with no guards. **M6's services are Open.**

## Findings

### Saturation

| Board | Average by share | Highest ground swatch | Highest swatch |
|---|---|---|---|
{{SAT}}

- **The lava is the most saturated thing on any board:** the glow is at 75%, the cooling crust at 69% and the core at 63%. The warning colours are at 62-79%, so the lava is among them.
- R-9.14-03 names the caldera's lava as the place where the world may glow. The designer kept fires as small points (Q7). Lava is lines and pools: 14% of the screen on this palette (question 1).
- **Saturation reads high on dark colours.** "Rock lit by lava" is at 50% and M6's forge-lit ground at 56%, but both are dark (36-43% value). They are dark red-browns, not bright colours.

### Warnings on each ground

The reference row:

| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |
|---|---|---|---|---|---|---|
{{BASELINE}}

- **Black rock is the best ground for warnings of all 20 biomes:** every warning is clearer than today (117-147%).
- Grey ash is level with today (88-107%), and the ash road a little under (78-97%).
- Red is the weakest on rock that the lava lights (81%), which is where red matters most (see below).
- The tables are in each block.

### Every biome, from the darkest to the lightest

The main ground of each of the 20 biomes. L is the lightness of the ground (CIE L*, 0 black to 100 white), and the percentages are what each warning keeps of today's difference.

| Biome | Main ground | L | Red | Yellow | Blue | Green |
|---|---|---|---|---|---|---|
{{ALLGROUNDS}}

- The Ember caldera is the darkest ground on the map (L 16). The Snow taiga beside it is one of the lightest (L 92).
- The designer's answer for the light end of this table is to change the warning itself (Q1).

### A red warning beside the lava

Each warning, as the game blends it today over a ground, against a colour that lies beside it:

{{WARNVS}}

- **A red warning on black rock is 11 from the lava's cooling crust**, and 10 on dark-brown rock. It is 49 from the bare rock under it, so against the ground it is very clear, and against the edge of a lava line it is close.
- The glowing lava itself is further away (30 from the red warning, 33 from the yellow one). The glow is much brighter than a warning.
- **What still tells them apart:** lava is unwalkable and drawn as thin lines and pools with a bright core; a warning is a flat shape with an icon, on walkable ground, and only while the attack runs (spec 9.3).
- The designer's answer on warnings (Q1) covers light ground. Here the ground is dark and the neighbour is the problem (question 2).
- In M6 the red warning is 26 from a rusted roof and 23 from scorched cloth. Those are far enough. C3's tile roof was at 8.

### Colour vision

The closest pair of the four warnings on each main ground, by colour alone (simulation as on the W1 boards):

| Board | Main ground | Normal | Protan | Deutan | Tritan |
|---|---|---|---|---|---|
{{CVD}}

- On black rock the warnings are further apart than today under every kind of colour vision.

### Edges

{{EDGE}}

- **The lava's edge is the clearest edge on any board** (49-70). A player sees where walking ends.
- **Obsidian on black rock is 7.** Shards of black glass on black rock are close to invisible as flat colours. They need a highlight from the lava's glow, or they are only shapes.
- **A scrap wall is 14 from a bare rock street and 8 from an ash lane.** The designer's dark band at the foot of walls (Q20) was decided for the bazaar and medieval kits (question 5).
- **Forge glow and lava glow are one family** (16). M6's light and the caldera's light are the same colour, which fits a city built on the caldera.

### Roads against the ground

| Board | Road | Against the main ground | Against the second ground |
|---|---|---|---|
{{ROAD}}

- **An ash road on black rock is clear** (20-26), unlike the roads in sand and snow. The designer's road marks (Q28) still apply, and the roads here are dangerous (`open-world-content.md` F1).
- In M6 an ash lane is clear on bare rock (22) and closer to a plank floor (11).

### The scrap look against the ground

The drawing uses these colours: {{KIT}}. `city-layouts.md` names the materials ("salvaged wood and metal, chains, scorched cloth, built on black rock"); the hex values are this board's proposal.

| Where | Settlement and look | Rusted sheet roof | Plank roof |
|---|---|---|---|
{{KITCHECK}}

- **A rusted roof reads on black rock** (32), and so does a plank roof (19). From above M6 is rust and wood patches on black.
- **Rust disappears in forge light:** a rusted roof is 6 from ground that a forge lights. Around the black market's yard the roofs and the lit ground come close.
- **A plank roof is 6 from a plank floor.** Salvaged boards overhead and underfoot are the same material. A walkway of boards beside a low plank roof needs something to part them: height, an edge, a shadow.

## The boards

### Ember caldera (`main.ember`)

**From the sheet:** fire. A dim red-orange glow from below, with a dark sky mood and floating embers. The volcano crater is the landmark. The roads here are dangerous.

**Palette**

{{PALETTE:ember}}

**Light:** {{LIGHT:ember}}.

**Warnings on this ground**

{{CHECK:ember}}

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
{{REFS:ember}}

### M6, the lawless city

**From `city-layouts.md` (a proposal):** medium-sized, about 90 m across, which is two to three screens. Scrap walls with a gate per road: north-west, north-east and south-west. The black market is in the middle, around a forge-lit yard. There is no safe square, and the layout has cover and more than one way out of every yard.

**Palette**

The shares are a rough share of the screen inside the city.

{{PALETTE:m6}}

**Light:** {{LIGHT:m6}}. It is the caldera's light, with forges in place of lava.

**Warnings on this ground**

{{CHECK:m6}}

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
{{REFS:m6}}

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
