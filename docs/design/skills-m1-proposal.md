# Skills: M1 starting kits, gauge placeholder, M2 skill tree outline

Design discussion with the user on 2026-10-22, for backlog E (spec 12.4) and the M2 skill tree (spec 12.2).
Decisions marked **(decided)** were confirmed by the user and are written into the spec and the decision log
(section 13) in the same change. Every number in this document is an M1 placeholder for backlog K.

The skill trees themselves live in `docs/design/skill-trees.json`, edited with `src/Tools/SkillTreeEditor` (see its
README). That file is the design source for the M2 tree; this document summarises it.

## 1. Decisions (2026-10-22)

| # | Decision | Spec |
|---|---|---|
| 1 | Skills cost stamina **and a small MP cost** (`mpCost`); normal attacks, dodge and parry cost no MP. Refusal `NoMp`. | R-3.1-04, 9 |
| 2 | Heals, buffs, debuffs, cleanses and ground areas pick who they affect with **hit shapes plus a target filter** (self, allies, enemies), aimed like attacks. No tab-target. In M1 "allies" are all other players (PvP is off, no parties yet). | R-3.1-07 |
| 3 | **Location-targeted skills:** the player picks a point (cursor) within the skill's max range; the point is **locked when the skill starts**, like the facing, and the effect lands there (for example after a channel). The Heal works this way. | R-3.1-07 |
| 4 | M1 kits: **5 skills per root class** on **Q E R F + 1**, one fixed layout per class (an `ActionBindings` row per class), plus the class's own normal attack. The player rearranges the hotbar with the M2 skill tree. | 12.4 E, 9.6 |
| 5 | The M1 kits are taken from the user's **root skill trees** (section 2). | 12.4 E |
| 6 | **Skills can require weapon types** (any of a list). A class line lists the weapon types it can use; some are unlocked by tree nodes. A skill whose weapon is not equipped stays learned and on the hotbar, its slot is greyed and a press is refused with `WrongWeapon`. | R-3.5-07, 9 |
| 7 | **No weapon swapping:** the Archer uses a bow **or** dual daggers, the Assist a staff **or** gloves; each skill needs its weapon. The M1 Archer uses a bow, the M1 Assist a staff. | R-3.5-07 |
| 8 | **Elements (10):** Fire, Ice, Water, Wind, Earth, Lightning, Holy, Dark, Blood, Poison. Build-ups use their element's resistance: Burn fire, Cold ice, Lightning lightning, Blood loss blood, Poison poison. | R-3.4-07, R-3.8-10 |
| 9 | **Guardian is removed.** Royal Knight's finals: **General** (sword and shield, physical, tank/support) and **Fire Heart** (big sword, fire, DPS). | R-3.5-02 |
| 10 | **Armor tiers of the roots** (light / medium / heavy, R-3.3-05 as changed by WP6): **Warrior heavy, Assist medium, Archer light, Mage light.** A learned **tree passive can set another tier** (the Knight's Combat Training sets medium); one tier at a time, the last armor passive on the line wins. | R-3.3-05, R-3.5-08 |
| 11 | **Class gauge in M1: a locked frame only** (greyed, lock, tooltip "unlocks at level 100"). No `ClassGauge` system, no `gaugeDelta`, no gauge message in M1. | 12.4 E, 9.3 |
| 12 | **A gauge is class data, not a tree node:** it unlocks by itself at level 100 and costs no points. | R-3.5-02 |
| 13 | **Dragoon gauge is Dragon Awakening:** each hit landed raises dragon power, damage taken lowers it, at 100% it grants buffs (Open). Fire Heart: **Spirit of Fire**; Light Storm: **Charges** (details in 3.5). | R-3.5-02 |
| 14 | **Tree links:** a line between two skills of a tree is a **prerequisite** (learn the first before the second). A skill **evolves only when it says so** (`evolvesFrom`), from a skill of an earlier tier of the same class line. | R-3.5-04 |
| 15 | **Evolution choices:** one base skill can have several evolutions on one class line (at any later tier); the player takes one of them. | R-3.5-04 |
| 16 | **Evolving loses the old skill's points** (no refund); the evolved skill costs its own points from level 1. | R-3.5-04 |
| 17 | **Level 1:** a new character has its normal attack and 1 skill point (one point per level from level 1); the first skill is the player's choice from the root tree. | R-3.5-04 |

## 2. M1 starting kits

Rules for all four kits:
- Baseline from spec 12.7: normal attack 10 stamina, skills 15-30 stamina; pool 100, regeneration 20/s after 1 s.
  MP costs are sized to the `ClassDef` pools (Warrior 30, Archer 40, Mage 100, Assist 80).
- Timings are **Startup / Channel / Active / Recovery** in ms, each rounded up to whole ticks (R-3.1-01, D2).
- Every damaging hit carries poise damage and a flinch (R-3.1-08); flinch power and duration follow the existing
  rows (power about equal to poise damage, 300-500 ms) unless noted.
- **No player skill has a parry color in M1** (colors on player skills matter only in PvP, which is off).
- Skills cancel into a dodge from Recovery, like the current placeholders, unless noted.
- Normal attacks are chains of 4 hits (R-3.1-10, user decisions 2026-10-26; rows `attack.<class>.2` to `.4`): the LMB rows below are hit 1; hits 2 and 3 have 50 ms shorter startups (hit 3: 110%, poise 12), hit 4 is a heavier finisher (melee 400/0/150/450 and a wider cone, ranged 450/0/100/450; 150%, poise 25). 10 stamina per hit.

### Warrior (sword and shield, heavy armor)
| Key | Skill | Category (R-3.1-07) | Build | S/C/A/R ms | CD | Stam / MP | Hit |
|---|---|---|---|---|---|---|---|
| LMB | Sword Attack (normal attack, hit 1 of 4) | simple | cone 2.2 m, 120° (current `attack.basic`) | 300/0/100/300 | – | 10 / 0 | 100% physical, poise 10 |
| Q | Slash | simple | cone 2.5 m, 180° | 300/0/100/350 | 4 s | 20 / 3 | 130%, poise 20 |
| E | Head Cut | simple | narrow box 3 m x 1.2 m | 350/0/100/400 | 5 s | 20 / 3 | 160%, poise 35 |
| R | Piercing Attack | simple | capsule 3.2 m, radius 0.5 m | 400/0/100/400 | 6 s | 25 / 4 | 170%, poise 30 |
| F | Jump Strike | dash + strike | 4 m dash in Startup, then a circle of 1.5 m; **super armor in Startup** | 450/0/150/450 | 8 s | 30 / 5 | 180%, poise 40 |
| 1 | Stamina buff | buff | self: stamina regeneration up, 15 s | 300/0/100/300 | 30 s | 15 / 8 | – |

Not in the M1 kit: Upper Cut (close to Head Cut), Provoke (needs threat in the monster AI), Increase Def (passive).

### Archer (bow, light armor)
| Key | Skill | Category | Build | S/C/A/R ms | CD | Stam / MP | Hit |
|---|---|---|---|---|---|---|---|
| LMB | Shot (normal attack, hit 1 of 4) | simple | one arrow (`arrow`) | 350/0/100/300 | – | 10 / 0 | 100% physical |
| Q | Piercing Shot | simple | one arrow, single target (maxHits 1) | 500/0/100/350 | 5 s | 20 / 4 | 200%, poise 25 |
| E | Multi Shot | simple | 3 arrows in the same direction at 0, 150, 300 ms of Active | 300/0/350/300 | 6 s | 25 / 5 | 70% each |
| R | Heavy Shot | channel + projectile | heavy arrow when the channel completes | 200/1000/100/350 | 12 s | 30 / 10 | 300%, poise 50 |
| F | Back Step | dash | 4 m **backward** (opposite the facing), no i-frames | 50/0/250/150 | 6 s | 15 / 4 | – |
| 1 | Dash | dash | 5 m, **no dodge charge**, no i-frames; direction rule as the dodge's (WP6) | 50/0/200/150 | 10 s | 20 / 5 | – |

Not in the M1 kit: Stab and Double Cut (dual daggers), Bomb Throw (location-targeted, M2).
Piercing Shot, Heavy Shot and Multi Shot also accept dual crossbows in the tree (decision 2026-10-23, spec R-3.5-07); the M1
Archer uses a bow, and arrows are unlimited (no ammo).

**Status (WP9):** the Warrior and Archer kits above are in the tables (`attack.<class>`, `skill.<class>.<name>`), with
the Warrior's slot 1 filled by WP12; the Archer's Dash goes along the facing until the dodge's direction
rule is decided (Open, spec 3.1).

### Mage (staff, light armor; carries the M1 Burn build-up, spec 3.8)
| Key | Skill | Category | Build | S/C/A/R ms | CD | Stam / MP | Hit |
|---|---|---|---|---|---|---|---|
| LMB | Magic Bolt (normal attack, hit 1 of 4) | simple | slow short bolt, 14 m | 350/0/100/300 | – | 10 / 0 | 100% magic |
| Q | Fire Spark | channel + projectile | small flame bolt | 100/300/100/300 | 3 s | 15 / 6 | 140% fire, **Burn +25** |
| E | Ice Shard | channel + projectile | ice shard | 100/400/100/300 | 4 s | 18 / 7 | 150% ice, poise 20 |
| R | Wind Cut | channel + projectile | piercing cut, 3 hits, -20% per hit | 100/400/100/300 | 5 s | 20 / 8 | 120% wind |
| F | Flamethrower | channel + ground area | cone 3.5 m, 30° each side, 2 s, a tick every 250 ms | 100/300/100/300 | 10 s | 25 / 15 | 25% fire per tick, Burn +5 per tick, never flinches |
| 1 | Magic Barrier | buff | self: damage taken -20%, flinch resist up, 15 s | 300/0/100/300 | 30 s | 15 / 20 | – |

Not in the M1 kit: Ice Pike (can swap in), Rock Throw (needs an explosion on impact), Concentration (MP regeneration
while channeling). Cold (Ice Shard) is not an M1 build-up.

**Status (WP12):** the Mage kit above, the Assist kit below and the Warrior's Stamina buff are in the tables
(`skill.<class>.<name>`), wired to the WP11 build-ups (Burn on Fire Spark and the Flamethrower, Simple Cleanse's
reduction) and ground areas. Flash's blind waits for the M2 status list.

### Assist (staff, medium armor)
| Key | Skill | Category | Build | S/C/A/R ms | CD | Stam / MP | Effect |
|---|---|---|---|---|---|---|---|
| LMB | Staff Strike (normal attack, hit 1 of 4) | simple | cone 2 m | 300/0/100/350 | – | 10 / 0 | 100% magic |
| Q | Holy Strike | channel + strike | thin capsule (laser) 6 m, radius 0.4 m | 100/500/100/300 | 4 s | 20 / 8 | 160% holy, poise 20 |
| E | Heal Wave | channel + heal | circle 5 m around the caster, **self and allies** | 100/600/100/300 | 10 s | 25 / 20 | heal 150% of Matk |
| R | Flash | channel + strike | circle 3 m around the caster | 100/400/100/300 | 8 s | 20 / 8 | 130% holy, poise 25; the 1 s blind comes with the M2 status list |
| F | Simple Cleanse | channel + build-up reduction | circle 4 m, self and allies | 100/400/100/300 | 15 s | 15 / 15 | every build-up meter -40 |
| 1 | Life Regeneration | channel + buff | circle 6 m, self and allies | 100/500/100/300 | 30 s | 15 / 20 | HP regeneration up, 20 s |

Not in the M1 kit: Heal (location-targeted, M2), Punch Strike and Jump Kick (gloves).

### What backlog E needs
Effect kinds stay as they are (no new kind). New fields, tables and rules:
1. `ActionDef.mpCost` (validator: 0 for attacks, dodge, parry), reason code **`NoMp`**; the start check becomes
   cooldown, charges, stamina, MP (nothing spent on a refusal).
2. A **target filter** (`targets`: `self`, `allies`, `enemies`) on `Heal`, `ApplyStatus`, `RemoveStatus` and
   `GroundArea`, and `HitShapeDef` rows allowed on those kinds (a `self` effect needs no shape). `GroundArea` damage
   per tick uses `damagePercent` and `damageKind`; with `allies` it heals per tick instead (M2 needs it; no M1 kit
   skill uses it).
3. **Heal amount** on `Heal` (for example `healPercent` of Matk).
4. A **direction offset** on `Dash` (Back Step: 180 degrees). The Archer's Dash follows the dodge's direction rule.
5. **Element** on damaging effects (`Strike`, `Projectile`, `GroundArea`), from the 10 elements; `ElementCaps` and the
   resistance stats in `StatDef` move to the decided list (TODO(open) there today).
6. **Build-up changes per hit** (Fire Spark, Flamethrower: Burn) and a **build-up reduction** effect on allies
   (Simple Cleanse), from the D6 build-up framework.
7. **Status rows** (D6 `StatusEffectDef`): Stamina buff, Magic Barrier, Life Regeneration, Burn.
8. **`ActionBindings` per class** (`classId` key): normal attack, dodge, parry and the 5 skills on Q E R F 1; a
   character uses its class's row. Each class gets its own normal attack action.
9. Several `Projectile` effects on one action at different offsets (Multi Shot) already work with the model; check the
   per-owner cap.
10. `ClassDef` armor tier: Warrior heavy, Assist medium, Archer light, Mage light (into WP6's `armorClass`).
11. Not needed in M1: location targeting (cursor point in the input frame), `WrongWeapon` (no gear in M1, each class
    has its weapon), `ClassGauge`.

Assets: one animation per skill and normal attack (24 actions: 20 kit skills and 4 normal attacks; placeholders from the existing CC0 libraries are fine),
VFX for projectiles (arrow, heavy arrow, flame bolt, ice shard, wind cut, magic bolt), ground and area effects
(flamethrower cone, heal wave, flash, cleanse, regeneration), status icons for the buff row (4), hotbar icons for the
same 24 actions, a staff, a bow, a sword and shield for the weapon slots.

## 3. Class gauge in M1
The gauge frame of the HUD (spec 9.3, `gauge`) is shown greyed with a lock and the tooltip "Class gauge: unlocks at
level 100" (text from `UiStrings`). Nothing fills it in M1, so there is no `ClassGauge` system, no `gaugeDelta` field and
no `ClassGaugeUpdate` message yet. The gauge's description is class data in `docs/design/skill-trees.json` (and later
in `ClassDef` or a gauge table).

## 4. M2 skill tree outline
**Shape.** One tree per class tier: the root (from level 1), the branch (30), the sub-branch (50) and the final class
(80); 4 + 8 + 16 + 32 = 60 trees, all present (empty or not) in `docs/design/skill-trees.json`. A class line is the
four trees from the root to a final class; nothing is reset on a class change (R-3.5-04).

**Nodes.** Kinds: active, passive, weapon unlock (one level). Default max level 5. A node may have a minimum
character level (blank = the tree's unlock level), an element, and the weapon types it needs (a weapon unlock lists
the types it unlocks).

**Links.** Prerequisites inside a tree (learn first, at a required level, default 1). Evolutions from a skill of an
earlier tier of the same line (R-3.5-05); several evolutions of one base are a choice. Evolving loses the old points.

**Points.** One per level from level 1, about 130 at the maximum level. Current content (kinds are a first guess the
user is reviewing):

| Line (to) | Skills on the line | Left after evolutions | Active / passive | Points to max after evolutions | Points if every node is maxed |
|---|---|---|---|---|---|
| Fire Heart | 32 | 16 | 12 / 3 | 76 | 156 |
| General | 33 | 14 | 11 / 2 | 66 | 161 |
| Dragoon | 32 | 17 | 13 / 3 | 81 | 156 |
| Light Storm | 31 | 17 | 14 / 2 | 81 | 151 |
| Berserker (no finals yet) | 22 | 14 | 13 / 0 | 66 | 106 |
| Sword Master (no finals yet) | 23 | 15 | 12 / 1 | 67 | 107 |
| Archer, Mage, Assist (roots only) | 8 | 8 | 8 / 0 | 40 | 40 |

Since evolving loses points, a player who learns a base before evolving it spends up to the right-hand column; the
budget of about 130 forces choices on full lines. Evolution choices so far: Jump Strike (Shield Sequence, Flame Strike,
Spear Shot), Increase Def (Knight's Outh, Courage, Defense Penetration, Dragon's Skin), Shield Strike (Shield
Thurst, Protection).

**Implementation pointers (spec 3.5).** `SkillTree` rows follow the design file: `nodeId`, `classId`,
`minCharLevel`, `prereqs[]` (with levels), `maxLevel`, `pointCost` (1), `kind`, `evolvesFrom`, plus `weapons[]` and
`element`. `SkillDef` inheritance for evolutions (R-3.5-05). The editor can export these rows when M2 starts.

**State of the trees.** Warrior: root, both branches, all four sub-branches, finals Fire Heart, General, Dragoon, Light
Storm (Slayer, Fencer, Barbarian, Highlander to do). Archer, Mage, Assist: roots only. Unnamed nodes: "Needs to think
in a name" (Light Storm), "Manoeuver" (Sword Master).

## 5. Open questions
Left open by the user (not decided, not invented):
- **New mechanics in the trees:** stun, knockback, blind, paralyze, provoke/aggro (threat), riposte, MP regeneration
  while channeling (Concentration), an explosion on projectile impact (Rock Throw). With the M2 status list.
- **Weapons:** when a character can change its equipped weapon (M2 with gear); the weapon types list (the design
  file has a working list); max range of each location-targeted skill.
- **Points:** max level per node (5 for now), point cost per level (1), the budget per final line.
- **Gauges:** Dragon Awakening's buffs at 100%, the Light Storm overcharge buffs, the General's War Specialist, and the
  gauges of the classes without one (M6).
- Already Open in the spec, unchanged: primary attribute formulas (R-3.4-01), the status list and numbers (M2), the 12th
  hotbar key and where the armed/robe/mount/pet toggles live (M7), the arena skill-point pool (M7), the skill tree
  screen (M4), whether a parry costs stamina (WP6).
