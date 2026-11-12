# Class art: style guide and briefs

Status: **style guide, v2 (2026-11-12)**. It holds the style of the class art in words, how the class sheets are made, and the briefs of the Slayer and the four root classes.

**v2 replaces v1 of the same day.** v1 described a detailed 3D look, after a picture of the Slayer that the designer had shared. Later that day the Slayer was drawn again as a **2D anime model sheet**, and the designer preferred it: "I liked this one more". This file now describes that style.

- **No picture is in the repository.** The designer's picture, the Slayer sheet and the four root class sheets are scratch pictures (see "Open").
- **This file is not the spec.** The designer's answers are marked as such; they are decisions A24 to A26 in `art-direction.md` (Q63 to Q67 in `reference-boards-questions.md`). Every costume, colour and hair choice in the briefs is a **proposal** of this file's author for the designer to change.
- No brief names a game, a film or an artist. The four style references of `art-direction.md` (Q53) guided the traits; nothing from them is copied.

## What the designer decided (2026-11-12)

| Point | Answer |
|---|---|
| The look | **Anime with clean dark line art and flat cel shading**, as on the Slayer sheet. It is the look of the game too, not only of the sheets (A24). |
| What it covers | **Characters, monsters and gear.** The world stays muted, with its saturation limits (R-9.14-03), and the biome boards stay valid (A24). |
| Lines | **Sheets and illustrations carry dark line art.** In the game the form of the edge around a character stays open: an outline or a rim light, tuned when the first model is in the engine (A23, A24). |
| Proportions | **About 6 heads tall**, in the game and on the sheets. The head is about 30 cm on the 1.8 m character (A25). The designer compared the Slayer at about 6 heads and at about 4.5 heads. |
| Medium armour | **Two small shoulder plates are fine** (A26). Heavy armour stays apart by big shoulder plates and a helmet; light armour is cloth only (A21). |

## The style in words

Every class sheet keeps this the same.

- **Drawing:** a flat 2D anime drawing. Clean dark lines of one weight around every shape. Flat colour areas with one simple shadow tone and few highlights. No painted textures, no realistic light.
- **Face:** anime, with large sharp eyes and a small nose and mouth. The face and the hair stay visible. Only heavy armour may hide the face under a full helmet (A21).
- **Proportions:** about 6 heads tall, a slim body (A25). Heads and hands about 1.25 times real size, weapons about 1.5 times (A13).
- **Armour tier by its pieces** (A21, A26): heavy has big shoulder plates and a helmet; medium has one or two small shoulder plates and bracers; light is cloth only. Examples, not final designs.
- **Shape:** a few large shapes that read from far away. Each class has a shape of its own in its weapon and in one more thing (a scarf, a hat, a shield, a cloak).
- **Colour:** one class colour on a plain costume, deep and muted on cloth (R-9.14-03). Bright colour only in small accents. One part of the figure light, one part dark, so one of them reads on any ground (`reference-board-figures.md`).
- **Mage and Assist** can both carry a staff, so each has its own staff shape and its own garment and head mark (A12).
- **Sheet:** a front view on the left and a back view on the right, the same standing pose, the same scale, the feet on one line, a plain light grey background, a soft shadow under the feet. An action pose is a separate picture.

## How the sheets are made

The sheets are drawn with **ComfyUI**, a free image program that runs on the designer's computer, and the model **FLUX.2 klein 4B**. Nothing is sent to a service.

| File | What it is | Licence | Source |
|---|---|---|---|
| `flux-2-klein-4b.safetensors` (7.2 GB) | The image model, distilled | Apache 2.0 | Hugging Face, `Comfy-Org/flux2-klein` (the model is Black Forest Labs' `FLUX.2-klein-4B`) |
| `qwen_3_4b.safetensors` (7.5 GB) | The text encoder | Apache 2.0 | The same repository |
| `flux2-vae.safetensors` (0.3 GB) | The image decoder | Apache 2.0 | The same repository |

- **Licence:** Apache 2.0 allows commercial use. The licence was read on the repository pages on 2026-11-12.
- **Models to avoid:** FLUX.2 klein 9B and FLUX dev are marked non-commercial. ComfyUI's own template for this model takes the decoder from a repository marked non-commercial; the file above comes from the klein repository instead.
- The model files are on the designer's computer, not in this repository, and no third-party file is added by this guide.

**Settings:** 1472 x 1088 pixels, 8 steps (4 for a first look), guidance 1, sampler `euler`, the scheduler `Flux2Scheduler`. A sheet takes about 20 to 30 seconds on the designer's graphics card. A reference picture is scaled to one megapixel and passed in as a `ReferenceLatent`.

**The steps for a new class:**

1. **Pass the Slayer sheet as the only reference picture.** It is the style anchor: with it the model keeps the line art, the shading, the layout and the body proportions. From a text alone the model draws a different, rounder style.
2. **Send the shared style text plus one class text** (below). Draw two or three versions with different seeds and take the best.
3. **Correct with short edit requests.** Pass the chosen sheet as the reference and ask for one change at a time ("recolour the cap of the left figure to green; change nothing else"). Long lists of changes are followed only in part.
4. **Assemble what the model will not draw.** It treats a held item as one object and tends to move it from one view to the other instead of drawing it twice. Three fixes with an image editor worked:
   - join the front view of one version with the back view of another (the layout is the same in every version);
   - mirror the back view when the model has drawn it with the shield and the weapon on the wrong arms;
   - copy a part from the front view, such as the head of a staff, onto the back view.
5. **Check the sheet** against the list at the end of this file.

**What this model gets wrong, to check each time:**
- held items missing in the back view, or a plain stick in place of the class's staff;
- headgear that differs between the views (a helmet in front, a cap behind);
- the back view drawn as a mirror image, so that a shield changes arms;
- "only one shoulder plate" is not followed: it draws a plate on each shoulder;
- details of the Slayer leaking into another class (a red channel on the blade, a scarf);
- **body proportions cannot be changed while the Slayer sheet is the reference.** The model keeps that body. A shorter figure came only from a text without a reference, and then in a simpler design.

The same texts work in a chat-style image tool that takes a reference picture: attach the Slayer sheet, say that it is the style guide only, and paste the shared text and one class text.

### The shared style text (first, every time)

```
Use image 1 only as the style guide. Draw a different character as a model sheet in exactly the same
anime drawing style: the same clean dark line art, the same flat cel shading with simple shadows, the
same level of detail, the same plain light grey background and the same layout: a front view on the
left and a back view on the right, at the same scale, in the same relaxed standing pose with the feet
apart, with exactly the same costume and weapons in both views. Do not draw the white-haired swordsman
of image 1 and do not give the new character his scarf, his coat or his swords. No scenery, no text,
no logo, no frame.
```

## The briefs

Each brief starts from the class data (`skill-trees.json`); the look is a proposal. "Still off" lists what the first sheets of 2026-11-12 do not get right yet.

### Slayer (the style anchor)

- **Data:** tier 3, from the Sword Master; medium armour; a sword in each hand; element blood; damage.
- **Shape of its own:** two long swords and a long scarf.
- **Class colour:** deep crimson.
- **How it was drawn:** from the designer's own picture as the reference, with a request for a model sheet and the text below. The request also asked for a body of about 4.5 heads and for one shoulder plate; the model followed neither. The sheet came out about 6 heads tall with a plate on each shoulder, and the designer then chose both (A25, A26). The text below says what the sheet shows.

```
The character is a Slayer, a fast duelist with two swords: a young fighter with spiky silver-white
hair and crimson eyes, calm and confident, the face fully visible. A thin red headband with two long
ties hanging behind the head. A long open coat of dark charcoal-blue heavy cloth reaching the calves,
with a torn hem and a deep crimson lining. A dark fitted vest with brass buttons, dark trousers, a
leather belt with one brass buckle, a deep crimson sash at the right hip. A long wide scarf of deep
muted crimson around the neck with one long tail down the back. A small steel shoulder plate on each
shoulder, steel bracers on both forearms, dark fingerless gloves, tall plain brown leather boots, no
knee armour, no helmet.

Two identical long straight swords, one held in each hand, points toward the ground: broad pale steel
blades with one narrow bright red channel down the middle, brass guards, dark red grips. Both swords
are visible in both views.
```

Action pose, for a later picture: `a low forward lunge with both swords crossed in front, the scarf and the coat streaming behind`.

### Warrior

- **Data:** root class; heavy armour; sword and shield; no element.
- **Shape of its own:** a large round shield, big shoulder plates and a helmet.
- **Class colour:** deep blue. Proposal.

```
The new character is a Warrior, a steady front-line fighter in heavy armour. A young man with short
tousled dark brown hair and steady amber eyes, a determined look, the face fully visible. An
open-faced steel helmet. A steel breastplate, two large rounded steel shoulder plates, steel
gauntlets, steel knee plates and armoured boots; the steel is dull and plain with a few brass rims.
Under the armour a tunic of deep muted blue that shows at the upper arms and as a knee-length skirt,
and a short deep blue cape that reaches the waist. Weapons: one broad straight sword in the right
hand, and on the left arm a large round steel shield with a brass rim and a plain deep blue face, as
wide as the body. The sword and the shield are visible in both views.
```

- **Still off:** the cape, the gauntlets and the armoured boots are not drawn; the sword is darker in the back view. The back view was mirrored by hand so that the shield stays on the left arm.
- Heavy armour is the one tier that may wear a full helmet (A21). The brief keeps the face open so that the class art shows a face; a closed helmet can be a gear variant.

### Archer

- **Data:** root class; light armour; bow (dual daggers as the other weapon); no element; damage.
- **Shape of its own:** a tall bow, a quiver and a short hooded cloak.
- **Class colour:** forest green. Proposal.

```
The new character is an Archer, a quick ranged fighter in light armour: cloth and soft leather only,
no metal plates. A young woman with auburn hair tied in a short ponytail and green eyes, an alert
look, the face fully visible. A small soft cap with one long feather. A short hooded cloak of deep
forest green with the hood down on the shoulders, reaching the waist. Under it a pale cream tunic
with a brown leather chest strap, a leather belt with one pouch, fitted brown trousers and soft
knee-high leather boots. A leather arm guard on the left forearm. Weapons: a tall curved wooden bow,
almost as tall as the character, held in the left hand, and a leather quiver of arrows with green
feathers on the back. The bow and the quiver are visible in both views. No sword.
```

- **Still off:** the cap's green differs a little between the views; the quiver shows over the right shoulder in front and over the left shoulder behind; the bow is in the right hand, not the left.

### Mage

- **Data:** root class; light armour; staff (wand as the other weapon; a shield or a book in the other hand); elements fire, ice, water, wind, earth and lightning.
- **Shape of its own:** a wide-brimmed pointed hat and a tall staff with a large crystal.
- **Class colour:** deep violet. Proposal.

```
The new character is a Mage, a caster of elemental magic in light armour: cloth only, no metal
plates. A young woman with long straight pale blonde hair and violet eyes, a curious look, the face
fully visible. A wide-brimmed pointed hat of deep violet with a plain band; the brim does not hide
the face. A long robe of deep muted violet reaching the ankles, with wide sleeves, a pale cream
lining and pale cream trim at the hem and cuffs, and a short shoulder cape of the same violet. A
cloth belt with a small closed book hanging from it. Simple dark ankle boots. Weapon: a tall wooden
staff, taller than the character, held upright in the right hand; its head is a large clear pale
crystal held in a wooden crescent. The staff is visible in both views. No sword.
```

- **Still off:** nothing that stands out. The sheet joins the front view of one version with the back view of another. The crystal is colourless on purpose, because the class uses six elements.

### Assist

- **Data:** root class; medium armour; staff or gloves; element holy.
- **Shape of its own:** a staff with a large open ring, and a hooded mantle.
- **Class colour:** ivory with warm gold. Proposal.

```
The new character is an Assist, a support fighter who heals and protects with holy magic, in medium
armour. A young man with shoulder-length chestnut hair and warm golden-brown eyes, a kind attentive
look, the face fully visible. A thin gold circlet across the forehead. A knee-length coat of ivory
heavy cloth with a dark teal lining and a wide dark teal sash at the waist, and a short mantle over
the shoulders with an open hood lying on the back. Dark teal trousers and sturdy brown boots. A small
steel shoulder plate with a gold rim on each shoulder, and steel bracers on both forearms; no helmet.
Weapon: a sturdy staff as tall as the character, held in the right hand; its head is a large open
gold ring with a small pale light in its centre. The staff is visible in both views. No sword.
```

- **Still off:** the sleeves are short in front and long behind; the right hand wears a full glove in front and a fingerless one behind; the ring is closed with a bright disc, not open with a small light. The ring of the back view was copied from the front view by hand.
- The Assist can also fight with gloves. A second sheet without the staff, with large padded steel-backed gloves, would show that.

## Check after each sheet

- The same drawing style as the Slayer sheet: dark lines, flat colour, one shadow tone?
- About 6 heads tall? Measure the head from the chin to the top of the skull, into the whole height.
- The same costume, armour, headgear and weapons in the front view and the back view?
- One-sided things (a shield, a sash, a quiver) on the same side of the body in both views?
- Every weapon of the class visible in both views, and held in a way a hand can hold it?
- The right amount of plate for the tier: big shoulder plates and a helmet for heavy, one or two small plates and bracers for medium, none for light?
- The face and the hair visible?
- One class colour, deep and muted on cloth; bright colour only in small accents?
- A light part and a dark part?
- Nothing of the Slayer on another class?
- Five fingers on each hand; no marks that look like writing?
- A plain background?

## Open

- **Where the pictures live.** The Slayer sheet is the style anchor of every later sheet, and it is a scratch picture on the designer's computer. Whether it and the accepted sheets are stored in this repository (Git LFS) is for the designer to decide.
- **The terms of the first picture.** The Slayer sheet was drawn from the designer's own picture, which came from another image tool. The model used here allows commercial use; under which terms that first picture may be used is not settled. This file treats all the pictures as reference pictures, not as game assets.
- **The first sheets are not final.** The "still off" points above, and the class colours, hair and costumes, which are proposals.
- **How 3D models get this look in the game** (toon shading, drawn lines), what it costs with many players on screen and how it reads at about 104 px, and **the form of the edge** around characters, are for the first model in the engine.
- **The classes above the roots** have no brief yet, and neither have monsters or gear.
