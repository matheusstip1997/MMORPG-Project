# Class art: style guide and briefs

Status: **style guide, v1 (2026-11-12)**. On 2026-11-12 the designer shared a picture of the Slayer (front, back and an action pose) as **what the art should be**, and asked for it to be improved and for the next classes to keep the same style. This file holds a review of that picture, the style in words, and a brief for the Slayer and the four root classes.

- The picture is the designer's and is **not in the repository**. It was made with an image tool from a concept sketch of the Slayer.
- The briefs are written as plain descriptions for a chat-style image tool that takes a text and a reference picture (the designer's answer on the tool).
- **This file is not the spec.** The designer's answers are marked as such. Every costume, colour and hair choice in the briefs is a **proposal** of this file's author for the designer to change.
- No brief names a game, a film or an artist. The four style references of `art-direction.md` (Q53) guided the traits; nothing from them is copied.

## What the designer decided with the picture (2026-11-12)

| Point | Answer |
|---|---|
| The picture's look | **It is the look of the game's characters too**, not only of illustrations: detailed materials and believable light, with an anime face. |
| The world | **Characters only.** Characters, monsters and gear get the detailed look. The world stays muted, with its saturation limits (R-9.14-03), and the biome boards stay valid. |
| The outline | **Illustrations have none. In the game, characters keep a thin edge** that separates them from the ground, an outline or a rim light, tuned when the first model is in the engine. |
| Proportions | **About 4.5 to 5 heads tall stays** (A20). The picture's figure is about 6 heads tall; the briefs ask for the shorter body. |

The first three change earlier decisions; they are recorded in `art-direction.md` and the spec by a separate change.

## Review of the Slayer picture

**What works, and should carry over to every class:**
- **A silhouette of a few large shapes:** the spiky hair, the flying scarf, the long coat and the swords. These still read when the figure is small.
- **A light part and a dark part** (white hair, dark coat), so one of them reads on any ground (`reference-board-figures.md`).
- **The face and the hair are visible** (A21).
- **One class colour** (red) on a dark, neutral costume.
- **Materials that say what they are:** worn leather, heavy cloth, dull steel, brass trims.
- **The torn coat hem with its red lining**, which gives the figure movement without a pose.

**What to improve:**

1. **Proportions.** The figure is about 6 heads tall. Decided: about 4.5 to 5, with a larger head, a compact body and shorter legs (A20).
2. **Two swords in every view.** The Slayer fights with a sword in each hand (`skill-trees.json`). The front view shows one sword and an empty hand, the back view two, the action pose one.
3. **The views do not show the same costume.** The shoulder plates differ between front and back, the scarf turns into a hood at the back, and the action pose has heavier leg armour and other boots. A model sheet has to match from every side.
4. **Too much plate for medium armour.** Two large shoulder plates and big knee plates read close to heavy. Decided: the tier reads from how much plate there is, and medium is one plate and bracers (A21). Proposal: one shoulder plate, on the sword arm; bracers; no knee plates.
5. **The scarf's red is at full strength.** Cloth may use the warning hues only in muted versions (R-9.14-03), because bright red on the ground means "an attack lands here". Proposal: a deep, muted crimson for scarf, sash and lining; the bright red stays in small things (the eyes, the channel of the blades).
6. **Small details.** Scratches, stitching, small buckles and gold filigree cannot be seen at the size a character has on screen (about 104 px tall at 1080p), and each one has to be modelled. Fewer and larger details.
7. **Light and background.** The sunset and the scenery change the costume's colours and hide its outline. A model sheet needs even, neutral light and a plain background. The scenic picture is worth keeping as a second, separate picture per class. Its strong colours are not the game's world, which stays muted.
8. **The layout of a sheet.** Front and back in the same standing pose, at the same scale, with the feet on one line. The action pose is a separate picture.
9. **Things an image tool gets wrong, to check each time:** hands and the number of fingers; how a hand holds a hilt; a sword's guard and grip; one-sided things (a shoulder plate, a sash) swapped to the other side in the back view; marks that look like writing.

## The style in words

This is what every class sheet keeps the same.

- **Rendering:** an anime character as a high-quality 3D game model. Clean forms, materials that look hand-painted but believable (leather, cloth, metal), soft realistic light. No outline in the picture.
- **Face:** anime, with large expressive eyes and a small nose and mouth. The face and the hair stay visible. Only heavy armour may hide the face under a full helmet (A21).
- **Proportions:** about 4.5 to 5 heads tall, a large head, a slim compact body (A20). Hands about 1.25 times real size, weapons about 1.5 times (A13).
- **Armour tier by its pieces (A21):** heavy has big shoulder plates and a helmet; medium has one plate and bracers; light is cloth only. Examples, not final designs.
- **Shape:** a few large shapes that read from far away. Each class has a shape of its own in its weapon and in one more thing (a scarf, a hat, a shield, a cloak).
- **Colour:** a dark or neutral costume with **one class colour**, deep and muted on cloth. Bright colour only in small accents. One part of the figure light, one part dark.
- **Mage and Assist** can both carry a staff, so each has its own staff shape and its own garment and head mark (A12).
- **Sheet:** front and back, same pose, same scale, plain light grey background, even light, a soft shadow under the feet.

## How to use the briefs

1. Open a new chat with the image tool and **attach the Slayer picture**. Say that it is the style reference only.
2. Paste **the shared style text**, then **one class text**.
3. Check the result against the list at the end of this file. Ask for corrections in the same chat ("the back view must show the same single shoulder plate on the right shoulder").
4. When the sheet is right, ask for the action picture in the same chat, so that the tool keeps the character.
5. Use the first good sheet of a class as an extra reference for classes of the same line.

### The shared style text (paste first, every time)

```
Use the attached picture only as a reference for the rendering style: the way materials, light and
the anime face are painted. Do not copy its character, its background or its proportions.

Make a character model sheet for a fantasy action game. An anime character rendered as a high-quality
3D game model: clean sculpted forms, hand-painted-looking but believable leather, cloth and metal,
soft realistic light. Anime face with large expressive eyes and a small nose and mouth. The face and
the hair are fully visible.

Proportions: the whole figure is about 4.5 to 5 heads tall, with a large head, a slim compact body
and short legs. The hands are slightly oversized. Weapons are clearly oversized, about one and a half
times their real size.

Show the same character twice, side by side, at the same scale, standing on one ground line: a front
view on the left and a back view on the right. Same relaxed standing pose in both. Exactly the same
costume, armour and weapons in both views; anything worn on one side stays on that same side of the
body in the back view.

Plain light grey studio background. Even neutral light. A soft shadow under the feet. No scenery,
no text, no logo, no frame, no outline around the figure.

The costume is built from a few large shapes that read from far away, with only a few large details:
no fine scratches, no filigree, no tiny buckles. Cloth colours are deep and muted. Bright colour
appears only in small accents. One part of the figure is light and another part is dark.
```

### The action picture (a second request in the same chat)

```
Now show the same character, with exactly the same costume and weapons, in one action pose:
[the pose of the class]. The background may be a soft, muted landscape that stays out of focus.
Keep the proportions of the model sheet: about 4.5 to 5 heads tall.
```

### The game camera check (a third request, optional)

```
Now show the same character small, seen from above at 45 degrees, standing on plain grey ground,
as a game camera would see it. Same costume and weapons.
```

## The briefs

Each brief starts from the class data (`skill-trees.json`); the look is a proposal.

### Slayer (improved)

- **Data:** tier 3, from the Sword Master; medium armour; a sword in each hand; element blood; damage.
- **Shape of its own:** two long swords and a long scarf.
- **Class colour:** deep crimson.

```
The class: a Slayer, a fast duelist who fights with two swords and blood magic. Medium armour.

A young fighter with spiky silver-white hair and crimson eyes, a calm, confident look. A thin red
headband with two long ties that hang behind the head.

A long coat of dark charcoal-blue heavy cloth, worn open, reaching the calves, with a torn hem and a
deep crimson lining. Under it a dark fitted vest with a row of brass buttons, dark trousers, a
leather belt with one brass buckle and a deep crimson sash hanging at the right hip. A long, wide
scarf of deep muted crimson around the neck, with one long tail hanging down the back.

Armour: one layered steel shoulder plate on the right shoulder only. Steel bracers on both forearms.
Dark fingerless gloves. Tall plain brown leather boots. No other plate, no knee armour, no helmet.

Weapons: two identical long straight swords, one held in each hand, points toward the ground. Broad
pale steel blades with one narrow bright red channel down the middle, brass guards, dark red grips.
Both swords are visible in the front view and in the back view.
```

Action pose: `a low forward lunge with both swords crossed in front, the scarf and the coat streaming behind`.

### Warrior

- **Data:** root class; heavy armour; sword and shield; no element.
- **Shape of its own:** a large round shield and broad shoulder plates.
- **Class colour:** deep blue. Proposal.

```
The class: a Warrior, a steady front-line fighter with sword and shield. Heavy armour.

A young fighter with short, tousled dark brown hair and steady amber eyes, a determined look.
An open-faced steel helmet that leaves the whole face and the fringe visible.

Armour: a steel breastplate, two large rounded steel shoulder plates, steel gauntlets, steel knee
plates and armoured boots. The steel is dull and plain, with a few brass rims. Under the armour a
tunic of deep muted blue that shows at the upper arms and as a knee-length skirt, and a short deep
blue cape that reaches the waist.

Weapons: one broad straight sword in the right hand, and on the left arm a large round steel shield
with a brass rim and a plain deep blue face. The shield is as wide as the body. Both are visible in
the front view and in the back view.
```

Action pose: `shield raised in front, sword drawn back to strike, feet planted wide`.

Note: heavy armour is the one tier that may wear a full helmet (A21). The brief keeps the face open so that the class art shows a face; a closed helmet can be a gear variant.

### Archer

- **Data:** root class; light armour; bow (dual daggers as the other weapon); no element; damage.
- **Shape of its own:** a tall bow and a quiver.
- **Class colour:** forest green. Proposal.

```
The class: an Archer, a quick ranged fighter with a bow. Light armour: cloth and soft leather only,
no metal plates.

A young archer with auburn hair tied in a short ponytail and green eyes, an alert look. A small
soft cap with one long feather.

A short hooded cloak of deep forest green, the hood down on the shoulders, reaching the waist. Under
it a pale cream tunic with a brown leather chest strap, a leather belt with one pouch, fitted brown
trousers and soft knee-high leather boots. A leather arm guard on the left forearm and a three-finger
leather glove on the right hand. No plate, no helmet.

Weapons: a tall curved wooden bow, as tall as the character, held in the left hand, and a leather
quiver of arrows with green feathers on the back. Both are visible in the front view and in the
back view.
```

Action pose: `bow fully drawn, aiming to the side, the cloak swept back`.

### Mage

- **Data:** root class; light armour; staff (wand as the other weapon; a shield or a book in the other hand); elements fire, ice, water, wind, earth and lightning.
- **Shape of its own:** a wide-brimmed pointed hat and a tall staff with a large crystal.
- **Class colour:** deep violet. Proposal.

```
The class: a Mage, a caster of elemental magic with a staff. Light armour: cloth only, no metal
plates, no leather armour.

A young mage with long, straight pale blonde hair and violet eyes, a curious look. A wide-brimmed
pointed hat of deep violet with a plain band; the brim does not hide the face.

A long robe of deep muted violet reaching the ankles, with wide sleeves, a pale cream lining and
pale cream trim at the hem and cuffs. A short shoulder cape of the same violet. A cloth belt with a
small closed book hanging from it. Simple dark ankle boots.

Weapon: a tall wooden staff, taller than the character, held upright in the right hand. Its head is
a large, clear, pale crystal held in a wooden crescent. The crystal is colourless, because the
class uses six elements. The staff is visible in the front view and in the back view.
```

Action pose: `staff raised overhead, the free hand stretched forward, the robe and hair lifted`.

### Assist

- **Data:** root class; medium armour; staff or gloves; element holy.
- **Shape of its own:** a staff with a large open ring, and a short hooded mantle.
- **Class colour:** ivory with warm gold. Proposal.

```
The class: an Assist, a support fighter who heals and protects with holy magic. Medium armour.

A young support fighter with shoulder-length chestnut hair and warm golden-brown eyes, a kind,
attentive look. A thin gold circlet across the forehead.

A knee-length coat of ivory heavy cloth with a dark teal lining and a wide dark teal sash at the
waist. A short mantle over the shoulders with an open hood lying on the back. Dark teal trousers
and sturdy brown boots.

Armour: one rounded steel shoulder plate on the left shoulder only, with a warm gold rim. Steel
bracers on both forearms. No other plate, no helmet.

Weapon: a staff as tall as the character, held in the right hand. Its head is a large open gold
ring with a small pale light in its centre. The staff is shorter and sturdier than a mage's staff.
It is visible in the front view and in the back view.
```

Action pose: `staff planted on the ground with the ring glowing softly, the free hand open toward an ally`.

Note: the Assist can also fight with gloves. A second sheet without the staff, with large padded steel-backed gloves, would show that.

## Check after each picture

- About 4.5 to 5 heads tall? Measure: the height of the head from the chin to the top of the skull, into the whole height.
- The same costume, armour and weapons in the front view and the back view?
- One-sided things (a shoulder plate, a sash, a shield) on the same side of the body in both views?
- Every weapon of the class visible, and held in a way a hand can hold it?
- The right amount of plate for the tier: big shoulder plates and a helmet for heavy, one plate and bracers for medium, none for light?
- The face and the hair visible?
- One class colour, deep and muted on cloth; bright colour only in small accents?
- A light part and a dark part?
- Five fingers on each hand; no marks that look like writing?
- A plain background and even light?

## Open

- **The reference picture is not in the repository.** Whether it and the accepted sheets are stored here (Git LFS), and under which terms of the image tool they may be used in a commercial game, is for the designer and the orchestrator to settle. This file treats them as reference pictures, not as game assets.
- **Class colours, hair and costumes** in the briefs are proposals.
- **How the detailed look is built in the game** (materials, cost with many players on screen, how it reads at about 104 px) and **the form of the edge** around characters are for the first model in the engine.
- **The classes above the roots** have no brief yet.
