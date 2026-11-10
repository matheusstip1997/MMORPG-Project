# Biome sheets (main map)

Status: **design sketch, v2 (2026-11-10)**, from a design talk with the game designer (2026-11-09) and the designer's answers to the art reference questions (2026-11-10, `reference-boards-questions.md`). The rules C1-C14 are the designer's decisions. **19 of the 20 sheets are accepted** (C6), with the answers C7-C14 worked in. **The Ember caldera's sheet is still a proposal**: nothing in it is settled until the designer accepts it.

This file is **not the spec**. The map and the region ids are in `world-map.md`, and the style, camera and size rules (A1-A8, B1-B7) are in `art-direction.md` (same folder). All names are placeholders. The monster **rank** of each biome is still open (`world-map.md` O1). Each sheet has a reference board with a palette and colour checks (`reference-boards-w1.md`, `reference-boards-w2.md`, `reference-boards-w3.md`, `reference-board-w4.md`); the colours on the boards are proposals.

## What the spec already fixes

- **Monster families** (`MonsterFamily`, spec 14.2): demon, beast, aberration, undead, humanoid, elemental, dragon, plant. The Bystial book gives set bonuses for cards of one family.
- **Elements** (R-3.4-07, `ElementCaps`): fire, ice, water, wind, earth, lightning, holy, dark, blood, poison. Each has its own resistance stat.
- **Gathering** (R-5.2-01): ore and gems (Blacksmith, Jeweler), herbs (Botanist), fishing (Adventurer). Wood is Open (10.4 [M3]).
- **Attack warnings** use red, yellow, blue and green (R-3.3-04), so biome palettes stay away from those hues at full strength (A3).

## Decisions (designer, 2026-11-09)

| # | Decision | Notes |
|---|---|---|
| C1 | **Each biome has one signature family and 1-2 shared families.** Every family lives in at least 3 biomes, so a card set can be hunted in more than one place. **The 8 families are a placeholder list: more may be added.** | The sheets use today's 8. A new family gets a signature biome when it is added. |
| C2 | **The biome's theme sets the main element** of its monsters (1-2 per biome), so players can prepare resistances. Neutral biomes mostly use physical damage. | R-3.4-07 |
| C3 | **Biomes are visual only for now.** No cold, heat or hazard rules. Gameplay effects stay **Open**. | Nothing new for the spec. |
| C4 | **Borders follow a natural edge with a short blend:** a river, ridge, road or cliff where possible, and a 20-30 m blend of ground, props and light (about one screen). | The light presets of A4 blend over the same distance. |
| C5 | **Palm coast meets Snow taiga at a cliff wall with ramps and no blend.** The taiga sits on a plateau above the coast. | The one border where two climates would clash (about 250 m long). The cliff follows B4: at least 3 m tall, marked with blockers. |

## Decisions (designer, 2026-11-10)

From the designer's answers to the art reference questions. The "Q" numbers refer to `reference-boards-questions.md`, which has each question and what the boards found. In the spec they are R-2.3-15, R-2.3-11, R-9.14-04 and R-9.14-07.

| # | Decision | Notes |
|---|---|---|
| C6 | **The sheets of 19 biomes are accepted** as written, with the answers below. The Ember caldera's sheet is not among them. | Q31. The Ember caldera had no reference board when the answers were given. |
| C7 | **Canyon floors and the mountain pass are wide.** The floors of the Bone canyons and the pass of the Frost peaks are wider than their walls are tall, so the walls stay plain terrain and never have to fade. | Q12. |
| C8 | **Two landmarks are walk-in places:** the hollow tree of the Old forest and the stepped temple of the Fern jungle. Their top hides like a building's roof. | Q13. An exception to "only service buildings have interiors" (R-11.6-05). |
| C9 | **Nothing is walkable on top** of the fallen statue, the giant ribs or the giant mushroom. The path at the mushroom stays on the ground. | Q14. The one walkable layer. |
| C10 | **Stepped landmarks are low steps,** walkable as stairs or as ramps of up to 30 degrees: the terraces of the Steam springs, the basalt columns of the Storm cliffs and the outside of the stepped temple. | Q15. |
| C11 | **The Reed marsh:** walkable reed beds are low (knee to waist) and never fade. Shallow marsh water is unwalkable. | Q16. |
| C12 | **Where a road is the same material as the ground beside it** (sand, gravel, ash, snow), it is marked with posts or poles and with edge stones or cairns. | Q28. Red sands, Bone canyons, Dune isle, Palm coast, Ash woods, Snow taiga. |
| C13 | **The Fungal hollow's light** is a dim ambient light, and the glowing props are part of the look. That counts as the biome's one light. | Q29. |
| C14 | **At the cliff between Palm coast and Snow taiga the light blends over the length of each ramp.** | Q30. The ground has no blend there (C5). |

## How to read a sheet

- **Light:** the biome's one fixed light and fog mood (A4).
- **Ground and palette:** muted colors (A3). The ground matters most, because the camera mostly shows ground and tops of things.
- **Props:** signature objects. Anything taller than about 2 m must be able to fade (A2).
- **Landmark:** one place for the minimap and the world map (A8). The camera never shows the horizon, so it is a map marker and a place to arrive at, not something seen from afar.
- **Monsters:** signature family, shared families, main element (C1, C2).
- **Gathering:** the proposal from `world-map.md`.

## West continent

### Green vale (`main.vale`): meadows, C1 start
- **Light:** warm late-morning sun, clear air, almost no fog.
- **Ground and palette:** short grass in soft olive and straw tones, dirt paths, wildflower patches in pale colors. The grass must not be a strong green, because green is a warning color.
- **Props:** low stone walls, hay bales, fruit trees, wooden fences, small farm sheds.
- **Landmark:** an old windmill on a low hill.
- **Monsters:** signature **beast**; shared plant. Main element: none (physical).
- **Gathering:** herbs, stream fishing.
- **Sound:** birds, wind in grass, distant farm animals.

### Heather moor (`main.heather`): windy moorland
- **Light:** overcast and cool, with light mist near the ground.
- **Ground and palette:** heather in dusty purple-grey, dry grass, bare peat, grey rock.
- **Props:** standing stones, burial mounds, wind-bent thorn trees, peat cuttings, broken cart wheels.
- **Landmark:** a ring of standing stones.
- **Monsters:** signature **undead** (the mounds); shared beast. Main elements: wind, dark.
- **Gathering:** herbs.
- **Sound:** steady wind, crows.

### Rime coast (`main.rime`): ice 1
- **Light:** low, pale sun; cold white haze toward the sea.
- **Ground and palette:** packed snow, grey-white ice with a slight cyan tint, dark wet rock, frozen shingle beach. The ice must stay grey-white, not strong blue.
- **Props:** ice blocks pushed up on the shore, frozen fishing racks, driftwood, frosted boulders, ice-covered boat hulls.
- **Landmark:** a ship frozen into the ice.
- **Monsters:** signature **elemental** (ice); shared beast. Main elements: ice, water.
- **Gathering:** ore, sea fishing (ice holes).
- **Sound:** creaking ice, sea wind, gulls.

### Amber woods (`main.amber`): autumn forest
- **Light:** soft golden afternoon light through the leaves.
- **Ground and palette:** fallen leaves in rust, ochre and brown; mossy roots; pale birch trunks. Keep the orange muted so it doesn't read as a warning.
- **Props:** birch and maple trees, leaf piles, mushrooms on logs, hunters' stands, beehives.
- **Landmark:** a giant fallen tree that a path runs under.
- **Monsters:** signature **plant**; shared beast. Main element: earth.
- **Gathering:** herbs, wood (Open).
- **Sound:** rustling leaves, woodpeckers.

### Lake country (`main.lakes`): lakes
- **Light:** bright and clear, with a light haze over the water.
- **Ground and palette:** wet grass, pebble shores, reeds, grey-teal water (unwalkable).
- **Props:** jetties, upturned boats, fishing nets on poles, willows, stepping-stone rows across shallow inlets.
- **Landmark:** the big lake, with a shrine on a small island (seen from the shore, not reachable for now).
- **Monsters:** signature **humanoid** (river raiders); shared beast. Main element: water.
- **Gathering:** fishing, herbs.
- **Sound:** lapping water, frogs, waterfowl.

### Old forest (`main.oldforest`): deep forest
- **Light:** dim green-grey shade with shafts of light in clearings.
- **Ground and palette:** dark earth, moss, ferns, thick roots. The darkest ground on the west continent.
- **Props:** huge old trees (they fade), fallen trunks, overgrown stone markers, giant roots as low walls, spider webs.
- **Landmark:** a hollow ancient tree big enough to walk into. It is a walk-in place, and its trunk hides like a roof (C8).
- **Monsters:** signature **beast**; shared plant, aberration. Main element: earth.
- **Gathering:** herbs, wood (Open).
- **Sound:** owls, creaking trunks, distant howls.

### Fungal hollow (`main.fungal`): giant mushroom grove
- **Light:** dusk-like, with soft glowing spores as the main light. The preset is a dim ambient light, and the glow is part of the look (C13).
- **Ground and palette:** spongy dark soil, pale mycelium threads, mushrooms in muted violet, teal and cream. The glow stays soft, so effects still stand out.
- **Props:** giant mushroom caps (they fade), puffballs, glowing moss, rotting logs, spore clouds (visual only).
- **Landmark:** one enormous mushroom with a path spiralling around its base. The path stays on the ground, and nothing walks up the mushroom (C9).
- **Monsters:** signature **aberration**; shared plant. Main element: poison.
- **Gathering:** herbs.
- **Sound:** dripping, soft pops, low hum.

### Dry steppe (`main.steppe`): dry grassland
- **Light:** bright, hot and slightly hazy.
- **Ground and palette:** pale dry grass, cracked earth, scattered grey stones.
- **Props:** lone thorn trees, animal skulls, nomad tent frames, stone cairns, watering holes.
- **Landmark:** a giant weathered statue lying on its side. Nothing is walkable on top of it (C9).
- **Monsters:** signature **humanoid** (raiders); shared beast. Main element: wind.
- **Gathering:** herbs.
- **Sound:** dry wind, insects, hooves far off.

### Red sands (`main.redsands`): desert, medium, C2 start
- **Light:** strong high sun with warm haze.
- **Ground and palette:** sand in dusty terracotta and tan, with darker red rock outcrops. The sand must be a muted, dusty tone, not bright red or yellow.
- **Props:** rock outcrops, dry bushes, half-buried walls, clay jars, palm clusters at wells.
- **Landmark:** a half-buried obelisk.
- **Monsters:** signature **beast** (scorpions, lizards); shared undead (buried ruins). Main element: earth.
- **Gathering:** ore, gems.
- **Sound:** wind over sand, distant chimes at the wells.

### Bone canyons (`main.canyons`): red-rock canyons
- **Light:** hard light on the rims, deep cool shadow on the canyon floors.
- **Ground and palette:** layered rock in brown-red and cream, gravel floors, bleached bone. The canyon floors are wider than the walls are tall, so the walls are plain terrain and never hide the player (C7).
- **Props:** giant ribs and vertebrae, rope bridges as decoration only (one walkable layer), rock arches that paths go around, vulture perches, mine scaffolds.
- **Landmark:** a giant ribcage that the main path runs through. Nothing is walkable on the ribs (C9).
- **Monsters:** signature **undead**; shared dragon (wyverns nesting on the rims). Main elements: earth, dark.
- **Gathering:** ore, gems.
- **Sound:** echoing wind, falling pebbles, vultures.

## Island

### Dune isle (`main.duneisle`): desert, small
- **Light:** bright sea light, with wind-blown sand in the air.
- **Ground and palette:** pale cream sand (lighter than Red sands), rippled dunes, shell beaches.
- **Props:** dune grass, wrecked boats, smugglers' crates, rope fences, bleached driftwood.
- **Landmark:** a lighthouse at the island's south end.
- **Monsters:** signature **elemental** (sand); shared humanoid (smugglers). Main elements: earth, wind.
- **Gathering:** gems, sea fishing.
- **Sound:** surf on both sides, wind, rigging.

## East continent

### Palm coast (`main.palms`): tropical shore
- **Light:** warm and bright, with soft shadows.
- **Ground and palette:** pale sand, dark wet sand at the waterline, low green undergrowth in muted tones, turquoise shallows kept soft.
- **Props:** palm trees, tide pools, fishing huts on stilts, coral rocks, beached canoes.
- **Landmark:** a shipwreck cove.
- **Monsters:** signature **humanoid** (pirates); shared beast. Main element: water.
- **Gathering:** sea fishing, herbs.
- **Sound:** surf, gulls, palm leaves.

### Fern jungle (`main.jungle`): jungle
- **Light:** humid green shade with bright spots where the canopy opens.
- **Ground and palette:** dark mud, giant ferns, vines, moss-covered stone.
- **Props:** broad-leafed trees (they fade), hanging vines, carved stone heads, rope-and-plank walkways at ground level, giant flowers.
- **Landmark:** an overgrown stepped temple. It is a walk-in place whose roof hides, and its outside is low steps (C8, C10).
- **Monsters:** signature **plant**; shared beast, aberration. Main element: poison.
- **Gathering:** herbs, wood (Open).
- **Sound:** insects, bird calls, dripping leaves.

### Reed marsh (`main.marsh`): marsh and rivers, C3 start
- **Light:** soft, misty morning light.
- **Ground and palette:** wet earth, reed beds in straw and olive, dark shallow water, plank paths. Walkable reed beds are low (knee to waist) and never fade, and the shallow water is unwalkable (C11).
- **Props:** plank walkways, reed bundles, stilt houses, fish traps, willows.
- **Landmark:** a great willow on an island in the river.
- **Monsters:** signature **beast** (frogs, snakes); shared plant. Main element: water.
- **Gathering:** herbs, river fishing.
- **Sound:** frogs, reeds in the wind, slow water.

### Ash woods (`main.ashwoods`): burnt forest
- **Light:** grey daylight through thin smoke, with drifting ash.
- **Ground and palette:** grey ash, charcoal trunks, a few orange embers as accents, new green shoots here and there.
- **Props:** burnt trunks, collapsed cabins, ash drifts, charred fences, smoking stumps.
- **Landmark:** a burnt watchtower.
- **Monsters:** signature **demon** (lesser); shared undead. Main elements: fire, dark.
- **Gathering:** ore.
- **Sound:** crackling embers, hollow wind.

### Steam springs (`main.springs`): hot pools
- **Light:** bright but diffuse, with white steam.
- **Ground and palette:** pale mineral terraces in cream and soft pink, milky pools (unwalkable), dark rock.
- **Props:** terraced pools, steam vents (visual only), mineral columns, wooden bathing decks, hot-spring plants.
- **Landmark:** a large stepped terrace of pools. The terraces are low steps, walkable as stairs or ramps (C10).
- **Monsters:** signature **elemental** (steam); shared demon. Main elements: water, fire.
- **Gathering:** herbs, gems.
- **Sound:** bubbling, hissing vents.

### Snow taiga (`main.taiga`): snowy pine forest
- **Light:** cold and clear, with long blue-grey shadows.
- **Ground and palette:** trampled snow, dark green pines, brown trunks, frozen streams.
- **Props:** snow-laden pines (they fade), log piles, hunters' cabins, frozen ponds, fur racks.
- **Landmark:** a frozen waterfall.
- **Monsters:** signature **beast** (wolves, bears); shared humanoid (hunter clans). Main element: ice.
- **Gathering:** wood (Open), ore.
- **Sound:** crunching snow, wind in pines, wolves.

### Frost peaks (`main.frostpeaks`): ice 2
- **Light:** harsh high-altitude light with blowing snow.
- **Ground and palette:** deep snow, bare grey rock, glacier ice in grey-white with a slight cyan tint.
- **Props:** rock spires, ice walls, prayer flags, mountain shelters, frozen remains.
- **Landmark:** the twin peaks, with a pass between them. The pass is wider than its walls are tall (C7).
- **Monsters:** signature **dragon** (frost wyrms); shared elemental. Main elements: ice, wind.
- **Gathering:** high-tier ore.
- **Sound:** howling wind, cracking ice.

### Storm cliffs (`main.cliffs`): basalt sea cliffs
- **Light:** dark storm sky mood: low grey light with flashes (visual only).
- **Ground and palette:** black-grey basalt, wet stone, sparse sea grass, white surf below.
- **Props:** basalt columns, wrecked masts, signal braziers, chains and anchors, sea-bird nests.
- **Landmark:** a field of hexagonal basalt columns stepping down to the sea. The columns are low steps, walkable as stairs or ramps (C10).
- **Monsters:** signature **elemental** (storm); shared aberration (things from the sea). Main elements: lightning, wind.
- **Gathering:** ore, sea fishing.
- **Sound:** thunder, heavy surf, gale.

### Ember caldera (`main.ember`): fire, the lawless city M6

**This sheet is still a proposal** (C6). Its reference board is `reference-board-w4.md`, with seven open questions (Q35 to Q41).
- **Light:** dim red-orange glow from below, with a dark sky mood and floating embers.
- **Ground and palette:** black and dark-brown volcanic rock, grey ash, lava as thin glowing lines and pools (unwalkable). The ground stays dark, and the red belongs to the lava and to attack warnings.
- **Props:** lava channels with stone slabs across, obsidian shards, chained bridges at ground level, scorched banners, makeshift scrap buildings around M6.
- **Landmark:** the volcano crater.
- **Monsters:** signature **demon**; shared dragon, elemental. Main element: fire.
- **Gathering:** rare ore and gems.
- **Sound:** rumbling, lava hiss, distant forge hammers from M6.

## Family and element coverage (check of C1 and C2)

| Family | Signature in | Shared in | Biomes |
|---|---|---|---|
| beast | vale, old forest, red sands, marsh, taiga | heather, rime, amber, lakes, steppe, palms, jungle | 12 |
| plant | amber, jungle | vale, old forest, fungal, marsh | 6 |
| undead | heather, canyons | red sands, ash woods | 4 |
| elemental | rime, dune isle, springs, cliffs | frost peaks, ember | 6 |
| humanoid | lakes, steppe, palms | dune isle, taiga | 5 |
| aberration | fungal | old forest, jungle, cliffs | 4 |
| dragon | frost peaks | canyons, ember | 3 |
| demon | ash woods, ember | springs | 3 |

Every family is in at least 3 biomes (C1).

| Element | Main element in |
|---|---|
| fire | ash woods, springs, ember |
| ice | rime, taiga, frost peaks |
| water | rime, lakes, palms, marsh, springs |
| wind | heather, steppe, dune isle, frost peaks, cliffs |
| earth | amber, old forest, red sands, canyons, dune isle |
| lightning | cliffs |
| dark | heather, canyons, ash woods |
| poison | fungal, jungle |
| holy | none |
| blood | none |

**Holy and blood have no home biome** in this proposal (see Open).

## Borders (C4)

Each border between two neighbouring biomes, with the natural edge proposed for it.

| Border | Natural edge |
|---|---|
| vale - heather | a low ridge; grass gives way to heather on the far slope |
| vale - amber | a stream with fords |
| heather - amber | the tree line |
| heather - lakes | the moor drains into the first ponds |
| heather - rime | the snow line on a gentle rise |
| amber - lakes | the lake shore |
| amber - old forest | trees grow taller and darker over the blend; an old boundary wall |
| lakes - rime | a frozen inlet |
| lakes - fungal | a boggy outflow stream |
| rime - fungal | a cliff step down from the ice into the hollow, with ramps |
| old forest - fungal | the hollow's rim; trees thin and mushrooms take over |
| old forest - steppe | the tree line |
| fungal - steppe | the hollow's rim; a ramp up onto dry ground |
| steppe - red sands | a dry riverbed |
| red sands - canyons | the canyon rim: cliffs with ramps down |
| canyons - dune isle | the south-west bridge |
| dune isle - palms | the south-east bridge |
| rime - taiga | the north bridge |
| palms - taiga | **a cliff wall with ramps and no blend** (decided, C5); the taiga sits on a plateau above the coast; the light blends over the length of each ramp (decided, C14) |
| palms - ash woods | a river |
| palms - marsh | the river mouth; sand turns to mud |
| palms - jungle | the undergrowth thickens; a stream |
| jungle - marsh | the ground gets wetter; the first plank paths |
| marsh - ash woods | the river |
| marsh - springs | warm streams running down into the marsh |
| ash woods - taiga | the edge of the old fire: burnt trunks among snowy pines |
| ash woods - ember | the ash deepens and the first lava cracks appear |
| ash woods - springs | steam vents among the burnt trunks |
| springs - ember | the terraces end at the caldera's outer slope |
| taiga - frost peaks | the tree line; the ground rises |
| taiga - cliffs | pines thin onto bare basalt |
| taiga - ember | melting snow and bare wet rock; the heat keeps the snow back |
| frost peaks - cliffs | the mountains fall to the sea |
| cliffs - ember | black rock on both sides; the glow starts |

Every border's natural edge except palms - taiga is a proposal. On the map, the tropical shore touches the snowy forest along about 250 m, and a blend would look wrong there, so the designer chose the cliff wall (C5).

## Open

- **Review of the Ember caldera's sheet** by the designer. The other 19 sheets are accepted (C6). The natural edge of each border (except palms - taiga) is still a proposal.
- **Holy and blood** have no home biome: give them one (for example holy on the Heather moor's stone ring), or keep them for specific monsters and bosses.
- **New monster families** (C1): which ones are added, and their signature biomes.
- **Monster ranks per biome** (`world-map.md` O1).
- **Biome gameplay effects and hazards** (C3): cold, heat, lava, thin ice.
- **Landmarks on `world-map.svg`:** 19 sheets are accepted (C6), so their landmarks can be added to the drawing; the Ember caldera's follows its sheet.
- **Wood** (10.4 [M3]): the sheets mark four forest biomes as candidates.

## Spec updates needed (for the orchestrator)

This file does not change the spec. The orchestrator should update the following once it is accepted:
- **14.2 (Bystial book):** the family list is a placeholder that may grow (C1); each biome has a signature family and shared ones.
- **3.4 (R-3.4-07) or 3.6:** a biome's theme sets its monsters' main element (C2), as a content rule for `MonsterDef` rows.
- **2.3 (world):** borders follow natural edges with a 20-30 m blend (C4); the cliff between Palm coast and Snow taiga (C5).
- **10.4 (open items):** biome gameplay effects (C3), new families, and holy and blood home biomes.
- **13 (decision log):** one row dated 2026-11-09 for C1-C5.

The decisions of 2026-11-10 (C6-C14) are in the spec: R-2.3-15 (C6-C11), R-9.14-07 (C12), R-9.14-04 (C13), R-2.3-11 (C14), and one decision log row dated 2026-11-10. R-3.4-09 and R-14.2-08 still call each biome's main elements and families a proposal; with 19 sheets accepted that is true only for the Ember caldera, and those two rules have not been changed.
