# Art direction: style, camera and size rules

Status: **design decisions, v2 (2026-11-10)**, from a design talk with the game designer. It covers the first two topics of the art and map work: style and camera (A1-A8), and size measurements (B1-B7). **v2 adds the decisions on characters and monsters (A9-A19, 2026-11-10).** The biome sheets, the city layouts, what fills the space between towns, and names come next. The world layout is in `world-map.md` (same folder).

This file is **not the spec**. Rules quoted below live in `docs/spec/`. The "guidelines" are proposals for artists and level builders that follow from the decisions; their numbers are starting points to tune with the art, not config values. The spec changes this needs are listed at the end.

## What the spec already fixes

- **Camera** (R-2.2-05; table `ClientCamera`):
  - a fixed **45-degree pitch**, centred on the character;
  - the player turns it **horizontally all the way around**, never vertically.
  - Placeholder values: distance 18 m, look-at height 1 m, vertical field of view 40 degrees.
- **What players see:** only what the camera shows (R-2.3-03, R-2.4-01). Hidden entities are never sent (R-2.4-05).
- **Attack warnings** (R-3.3-04, spec 9):
  - colored ground areas, **red, yellow, blue and green**, each with its own **shape** as well;
  - shown only while a monster performs a colored attack. Default attacks have no warning.
- **Accessibility** (spec 9): anything important is shown with **shape or text as well as color**; there is a colorblind palette option.
- **Visuals follow the action timings in config**, never the other way round (CLAUDE.md). Skill animations are placeholders, swappable by data.

## What the camera shows (with the placeholder values)

| Measure | Value | Why it matters for art |
|---|---|---|
| Ground in view | a trapezoid about **20 m wide at the bottom** of the screen, **42 m wide at the top**, and **23 m deep** (from 6 m behind the character, on the camera side, to 17 m ahead) | A screen covers only one small area at a time: about one small town, or a quarter of a main city. |
| Character height on screen | about **90-100 px** on a 1080p screen | Faces barely show, so silhouettes, colors and weapons identify a character. |
| Sky and horizon | **never in view** (the top edge of the screen looks 25 degrees down) | No skybox work. Distant landmarks don't guide players (see "Navigation"). |
| What fills the screen | the **ground**, **roofs** and **tree tops** | The ground textures and the top surfaces of things matter more than walls and fronts. |
| Turning | every object is seen from **all sides** | Nothing has a hidden back side, so every building and prop needs all four faces finished. |

If `ClientCamera` changes, these numbers change with it. The 45-degree pitch doesn't change.

## Decisions (designer, 2026-11-09)

| # | Decision | Notes |
|---|---|---|
| A1 | **Art style: stylized toon with anime touches.** Cel shading, clear shapes and simple color areas for the world. The characters keep an anime look, and the current anime-style 3D avatars fit as placeholders. | |
| A2 | **Occlusion: fade, and roofs off.** Anything between the camera and the player's own character **dissolves to see-through**. Entering a building **hides its roof and upper floor**. | Tall art is allowed (city walls, big trees) because it fades. |
| A3 | **A muted world with bright gameplay.** The environment stays muted, with a saturation limit per biome. The bright colors belong to gameplay: attack warnings, spell effects, loot glow, name plates and the HUD. | For example, the caldera's lava glows, but its ground is dark rock rather than bright red. |
| A4 | **Lighting: one fixed light per biome for now.** Each biome has one designed light and fog mood. Day/night and weather stay **Open** for later. | If they come, they run on the world clock (UTC, architecture rule 6) and must keep the attack warnings readable. |
| A5 | **The camera keeps a fixed distance** (no player zoom). Everyone sees the same area, which is fair in PvP, and the interest area of R-2.3-03 keeps one size. | The distance itself (18 m) is still a placeholder to tune. |
| A6 | **Outlines on characters and interactables only:** players, NPCs, monsters, bosses, loot, resource nodes, doors and portals. The world gets none. | Goes with A3: anything you can fight or use stands out. |
| A7 | **Character proportions: stylized heroic.** About 6 heads tall, with heads, hands and weapons slightly larger than real life. | For readability at about 90-100 px. Classes and weapons must be told apart by silhouette. |
| A8 | **Navigation: minimap, signs and paths.** Players find their way with the minimap (already in the game), a full-screen world map later, signposts at road crossings, and roads clearly drawn into the ground. Landmarks belong on the maps, not the horizon. | The world map window isn't in spec 9 yet. |

## Decisions (designer, 2026-11-10): characters and monsters

The designer's answers to the twelve questions of `reference-board-figures.md` (Q42 to Q53 in `reference-boards-questions.md`). The board's measurements are behind each one.

| # | Decision | Notes |
|---|---|---|
| A9 | **A two-tone outline:** a dark line with a thin light rim outside it, the same on every figure and interactable on every ground. | Q42. Adds to A6. No single outline colour is clear on all 20 biome grounds; of two tones, one always reads. The two tones and the widths are still to be tuned. |
| A10 | **The outline stays neutral.** Its colour carries neither hostility nor rarity; it only separates a figure from the ground. Hostility and rarity are shown elsewhere (name plates, markers, loot glow). | Q43. Closes the open point of A6. |
| A11 | **The armour tier reads from the black shape: shoulder width plus headgear.** | Q44. The tier decides what a player can parry (R-3.3-04). Examples, not designs: a full helmet for heavy, a hood or cap for medium, a bare head or a hat for light. |
| A12 | **A Mage and an Assist with a staff are told apart by the staff's shape and by clothing:** each class has its own staff shape and its own garment or headgear shape. | Q45. Both can carry a staff, and their armour differs by one tier only. |
| A13 | **How much larger than life: weapons about 1.5 times, heads and hands about 1.25 times.** | Q46. Makes A7's "slightly" exact. A blade is then about 10 px wide and a hand about 12 px at 1080p. The 30 cm head of B1 is already about 1.25 times a real one. Whether a long weapon (a staff, a bow) also grows in length is left to the concept art. |
| A14 | **Monster heights: a height range per size class,** as a guide for artists. | Q47. Adds to B5, which defines the classes by body radius only. The numbers are still to come, as a proposal for the designer. |
| A15 | **A monster family in several biomes: a local variant with a family marking.** Each biome gets its own variant in a tone that contrasts with that biome's ground, and the family keeps one marking or shape that is the same everywhere. | Q48. For example a snow wolf and a marsh snake. |
| A16 | **Elementals have a core in another colour:** besides the outline and a glow, a core, eyes or cracks that contrast with the ground. | Q49. Elementals are made of their biome's material, so their body has the ground's colour on purpose. |
| A17 | **Element effects keep their natural hues, and warnings stay apart by form.** Warnings are flat shapes on the ground with a clear edge, drawn above effects in the air; element effects are in the air, small and soft. | Q50, with Q7 and Q8. Goes with A3. The colour of each element is not chosen yet. |
| A18 | **The disguise robe hides the class and the armour tier:** one robe shape for everyone covers the body. | Q51. Adds to R-6.2-01. Others cannot read from the shape what the wearer can parry. Not asked: what an observer sees who passes the Detect check (R-6.2-03), or who is in the wearer's party or guild (R-6.2-04). |
| A19 | **The art is tuned for 1080p; smaller screens are supported.** 720p and the Steam Deck's screen must stay playable, but marks are not designed for a 69 px character. | Q52. A character is 104 px tall at 1080p with the placeholder camera values. |

**Style references** (Q53; not a rule): the designer named **Grand Chase Mobile, Tree of Savior, Ragnarok and Final Fantasy** as good references. Which Final Fantasy, and what each one is a reference for, was not said. They are references for the look only; nothing from them is used or copied.

## Guidelines for artists and level builders (proposals)

**Occlusion (A2):**
- **The 45-degree rule.** An object on the camera side hides the character's feet when it is taller than its distance from the character, and the head when it is taller than that distance plus about 1.7 m. Because the camera turns, any side can be the camera side.
- **What must be able to fade:** every object taller than about 2 m that a player can stand next to. That covers trees, walls, pillars, cliffs and building fronts.
- **What can stay solid:** low props (fences, rocks, crates) that are short enough never to hide a character.
- **Buildings you can enter** are built in layers: the ground floor, then the upper floor and roof as separate parts that can be hidden.
- **Faded objects still block movement and sight** (R-2.4-01). The fade is visual only.

**Colors (A3):**
- **Biome palettes avoid large areas of the warning hues.** Where a biome needs one (the red caldera, the blue ice), use a darker and less saturated version, and keep the full-strength hue for gameplay.
- **Saturation limit:** a starting point is that the environment stays clearly below the saturation of the warnings, to be tuned per biome on the biome sheets.
- **Check every biome in the colorblind palette** with all four warning colors and shapes on its ground.

**Outlines (A6, A9, A10):**
- One outline width and style for everything that gets an outline.
- The outline is two-tone: a dark line with a thin light rim outside it, the same on every ground (A9). Its two tones and its widths are still to be tuned.
- The outline's color stays neutral (A10). Hostility and rarity are shown elsewhere (name plates, markers, loot glow), and there by shape or text as well as color (spec 9).

**Characters (A7):**
- **Silhouette first:** each class and each weapon type should be recognisable as a black shape at 100 px.
- **Armor tier** (light, medium, heavy, R-3.3-04) should read from the silhouette. Parry rules depend on it.

**Light (A4):**
- **One preset per biome:** sun direction and color, ambient light and fog color.
- **Blending:** at a biome border the presets blend over a short distance. The blend width is set on the biome sheets.

**Navigation (A8):**
- **Roads:** main roads are drawn clearly into the ground, so a player can follow them without the minimap.
- **Signposts** stand at every road crossing and point to the nearest cities and towns.

## Size measurements

### What the code already fixes

- **Walkability grid** (`grid.bin`, spec 11.6): 50 cm cells.
- **Agent radius:** the exporter makes every cell unwalkable that touches a blocker grown by the agent radius, `agentRadiusCm` 40. The code marks it as a placeholder "until character size is defined"; B1 now defines it.
- **Body radius:** player bodies are 40 cm in radius (`ClassDef.bodyRadiusCm`). Monster bodies today are 45-90 cm, and the two bosses are 120 and 140 cm (`MonsterDef.bodyRadiusCm`). Living monsters block movement (R-2.2-07).
- **Blockers** are boxes that only turn in **90-degree steps** (`ServerBlocker`, `CmBox`).
- **Ground:** height is a ray down onto any collider that isn't a blocker. **Any ground is walkable unless a blocker covers it.** There's no slope limit in the exporter.
- **One walkable layer** (overlapping surfaces Open, 10.4 [M1]).
- **No jump input** (the input frame has no jump bit). Jump Strike and Jump Kick are dash skills along the ground.
- **The narrowest walkable gap.** A gap between two blockers needs 2 x 40 cm plus one whole cell. That is **1.3 m** at best and **1.8 m** however the gap lines up with the grid.

### Decisions (designer, 2026-11-09)

| # | Decision | Notes |
|---|---|---|
| B1 | **Reference character: 1.8 m tall**, body radius **40 cm** (80 cm wide). With A7 (about 6 heads), one head is about 30 cm. | Confirms `agentRadiusCm` 40 and `ClassDef.bodyRadiusCm` 40. |
| B2 | **Heroic world scale, x1.25-1.5:** doors, stairs, furniture and props are larger than real life, so they read from the camera and fit the body on the grid. | |
| B3 | **Roads 6 m, paths 3 m, bridges 5 m** (walkable width). | Bridges stay narrower than the roads that lead to them, so they remain chokepoints (`world-map.md` D8). |
| B4 | **Gentle terrain with cliff walls.** Walkable slopes go up to about 30 degrees; anything steeper is a **cliff marked with blockers**. Terraces are joined by ramps and stairs, and a cliff is at least **3 m** tall so it reads as a wall from the camera. | There's no jumping, and the grid has one layer (no caves under hills or paths under bridges). |
| B5 | **Monster size classes:** small (radius up to 60 cm), large (up to 100 cm) and boss (over 100 cm). **Large monsters and bosses are placed only on open ground**, with no gap narrower than about 4 m near their camp or arena. | How the server stops a large body squeezing through a narrow gap stays with the orchestrator (see Open). |
| B6 | **Only service buildings have interiors:** crafting halls, the inn, shops with NPCs, the guild hall and the raid gate hall. Homes and other buildings are closed facades. | Each interior has a roof and upper floor that can be hidden (A2). |
| B7 | **Cities sit on the grid; nature is free.** Buildings, walls and city props snap to the 50 cm grid and face N/E/S/W, so their collision is exact. Rocks, trees and cliffs can turn freely and get approximate box blockers. | Follows from the 90-degree blocker boxes. |

### Measurement sheet for artists and level builders (proposals from B1-B7)

| Thing | Size | From |
|---|---|---|
| Character | 1.8 m tall, 80 cm wide (collision), head about 30 cm | B1, A7 |
| Narrowest walkable gap anywhere | **1.8 m** (anything narrower may close on the grid) | grid + B1 |
| Door (walkable) | at least **2 m** wide, 3 m tall | B2 |
| Interior corridor | at least 2.5 m wide | B2 |
| Interior floor height | about 4 m per floor (one floor of a house) | B2 |
| Stair step | 25-30 cm high, at least 40 cm deep; stairs as wide as the door they lead to | B2, B4 |
| Ramp, walkable slope | up to about 30 degrees | B4 |
| Cliff (unwalkable, blocker) | steeper than 30 degrees, at least 3 m tall | B4 |
| Low prop that never needs to fade | up to about 1.2 m (fence, crate, rock) | A2 |
| Main road / path / bridge (walkable width) | 6 m / 3 m / 5 m; bridge railings are blockers outside the 5 m | B3 |
| House | 1-2 floors, about 4-8 m to the eaves | B2 |
| City wall | about 6-8 m, fades when it hides the player | A2 |
| Tower or landmark building | up to about 15 m, fades | A2 |
| Clear space around a large monster camp | no gap under about 4 m nearby | B5 |
| Clear space for a boss arena | no gap under about 4 m inside; the arena is sized for the boss's attacks (`HitShapeDef`) plus room to dodge | B5 |
| Grid snap (cities) | 50 cm, rotation in 90-degree steps | B7 |

The settlement sizes from `world-map.md` are main city about 170 m, medium city about 90 m and small town about 45 m. At B2 scale, a small town is roughly one screen.

## Open

- **Large bodies at narrow gaps** (B5): today the walkability grid is built for one 40 cm body. Options include a second grid for large bodies, a check of the monster's radius at each step, or keeping big monsters away from narrow places only (B5). This is for the orchestrator, spec 2.2 and 11.6.
- **A slope check in the exporter** (B4): whether the exporter or the `DataValidator` should flag ground steeper than 30 degrees that no blocker covers.
- **Day/night cycle and weather** (A4): if, when, and whether weather has any gameplay effect.
- **The full-screen world map window** (A8): its milestone and layout (spec 9).
- **The final camera values:** distance and field of view in `ClientCamera` (A5). They're placeholders, and every number in "What the camera shows" follows from them.
- **The outline:** its two tones and its widths (A9). Whether its colour carries hostility or rarity is answered: it does not (A10).
- **Monster heights:** the height range of each size class (A14).
- **Element colours:** the colour of each of the ten elements (A17).
- **The disguise robe seen through:** what an observer sees of the shape who passes the Detect check, or who is in the wearer's party or guild (A18).
- **The style references:** which Final Fantasy, what each reference is for, and how they go with A7's 6 heads.
- **The fade:** its exact look (dither or transparency), and whether it applies to the local player only or also to a locked target or party members.

## Spec updates needed (for the orchestrator)

This file does not change the spec. The orchestrator should update the following once it is accepted:
- **1 (overview):** the art style (A1).
- **2.2 (R-2.2-05):** no player zoom; the camera distance is fixed by config (A5).
- **9 (client UI and HUD):**
  - the occlusion fade and roof hiding (A2), as client rendering rules;
  - outlines on characters and interactables (A6);
  - the world map window as a future item (A8).
- **2.2 (R-2.2-07):** monster size classes, and large monsters and bosses only on open ground (B5).
- **11.6 (UnityExporter):**
  - the agent radius is now defined as 40 cm (B1), so the "placeholder" comment in `SampleWorldBuilder` can go;
  - the slope and cliff rule (B4), and whether the exporter checks it;
  - cities on the grid and nature free (B7).
- **10.4 (open items):**
  - day/night and weather (A4);
  - large bodies at narrow gaps (B5);
  - the other open items listed above.
- **13 (decision log):** one row dated 2026-11-09 for A1-A8 and B1-B7.

The decisions A9-A19 (2026-11-10) are written into the spec by the spec change made together with this one: R-9.14-05 (A9, A10), R-9.14-06 (A11, A12, A13, A19), R-2.2-08 (A14), R-14.2-08 (A15, A16), R-9.14-03 (A17), R-6.2-01 (A18), 10.4 (the open points above) and one decision log row dated 2026-11-10.
