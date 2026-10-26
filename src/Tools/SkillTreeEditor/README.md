# Skill Tree Editor

A single HTML page for designing the class skill trees (spec 3.5). It edits one file,
`docs/design/skill-trees.json`, which holds every class (4 roots, 8 branches, 16 sub-branches, 32 finals) and every
skill node. It has no build step and no dependencies, and it works offline.

## Use
1. Open `src/Tools/SkillTreeEditor/index.html` in **Chrome or Edge** (double-click the file).
2. **Open** `docs/design/skill-trees.json`, or drop the file on the page. In Chrome and Edge, **Save** (Ctrl+S) writes
   back into the same file. Other browsers download a copy instead.
3. Commit the JSON like any other design change. One class or skill per line keeps the diffs readable.

## Editing
- **Classes:** click a class on the left. The middle shows its whole line, root to that class, one tree per tier.
  Click a tree header to edit the class: unlock level, armor tier (blank means the earlier tier's), the weapons allowed in the main and the secondary slot (a character carries two: rapier + pistol, dagger + dagger; leave the secondary empty for a two-handed weapon), elements,
  role, gauge (class data, not a tree node: it unlocks at level 100), note, and add or delete a sub-class.
- **Skills:** double-click an empty cell to add one. Drag a skill to move it. Click it to edit it on the right: name,
  kind (active, buff, passive, weapon unlock; a buff counts as an active skill), max level, minimum character level (blank means the tree's unlock
  level), description, elements (one or more, for example fire and blood), and the weapons it needs, in either slot (for a weapon unlock: the weapons it unlocks).
- **Arts** (R-3.5-09, M6): from the second class tier on, every skill has three arts, extra features that turn on by
  themselves when a stat condition holds: as the skill starts, or for a passive whenever the stats change (on while it holds). In the skill panel, **+ Art** adds one: its kind, the
  condition (`stat > value` or `stat > other stat`) and what it does. **Copy from** takes the arts of the skill it evolves
  from (evolved skills have their own arts otherwise). The badge on a node shows how many it has (green at 3/3). Root-class
  skills and weapon unlocks have no arts. The condition stats and the art kinds are lists under **Lists**.
- **Requires (learn first):** grey arrows inside a tree. Select a skill, then Ctrl+click another skill of the same tree
  to add or remove the requirement, or use the list on the right.
- **Evolves from:** dashed amber lines between tiers. Pick the base skill from the dropdown, which only offers skills
  of earlier tiers on the same line. Several evolutions of one base on one line are a choice: the player takes one
  (decision 2026-10-22).
- Undo and redo (Ctrl+Z, Ctrl+Y). Unsaved changes survive a closed tab as a draft in the browser (offered on the next
  start).
- **Ids** (`class.skillName`) follow the name until you edit the id by hand. References are renamed with it.

## Checks
Shown on each skill and class, and for the whole file under **Checks**:
- errors: unknown or missing links, more than three arts on a skill, an evolution from outside the earlier tiers of the line, prerequisite loops,
  duplicate ids, a requirement above the required skill's max level;
- warnings: placeholder names, arts on a root skill or a weapon unlock, an art kind or condition stat not in the lists, a weapon unlock with more than one level, a skill needing a weapon its class line never
  gets, a min level below the tree's unlock level, two skills with the same name in one tree, a final class whose
  line needs more points than the budget;
- info: evolution choices, skills of the second tier and later still missing their three arts (one line per class), active-skill counts outside the target range, missing descriptions.

The budget and targets (points at max level, active skills per class) and the element and weapon lists are under
**Lists**.

`docs/design/skill-trees.json` was first converted from the Skill Tree Maker exports in the design folder (the
"Evolves from" lines in the descriptions became links; the kind of each skill is a first guess to review).
