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
  Click a tree header to edit the class: unlock level, armor tier (blank means the earlier tier's), weapons, elements,
  role, gauge (class data, not a tree node: it unlocks at level 100), note, and add or delete a sub-class.
- **Skills:** double-click an empty cell to add one. Drag a skill to move it. Click it to edit it on the right: name,
  kind (active, passive, weapon unlock), max level, minimum character level (blank means the tree's unlock
  level), description, element, and the weapons it needs (for a weapon unlock: the weapons it unlocks).
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
- errors: unknown or missing links, an evolution from outside the earlier tiers of the line, prerequisite loops,
  duplicate ids, a requirement above the required skill's max level;
- warnings: placeholder names, a weapon unlock with more than one level, a skill needing a weapon its class line never
  gets, a min level below the tree's unlock level, two skills with the same name in one tree, a final class whose
  line needs more points than the budget;
- info: evolution choices, active-skill counts outside the target range, missing descriptions.

The budget and targets (points at max level, active skills per class) and the element and weapon lists are under
**Lists**.

`docs/design/skill-trees.json` was first converted from the Skill Tree Maker exports in the design folder (the
"Evolves from" lines in the descriptions became links; the kind of each skill is a first guess to review).
