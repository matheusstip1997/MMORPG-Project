# Job skill trees: Blacksmith and Jeweler (M3)

This is the design talk with the user on 2026-10-30 for the M3 job skill trees (spec 5.1 R-5.1-04, M3 plan decision 9).
It feeds WP-M3-7 (`docs/plans/m3-jobs-crafting-plan.md` section 6). Decisions marked **(decided)** were made by the
user. Points marked **Open** are undecided and must not be invented.

The nodes are in `docs/design/job-trees.json`. It has one entry per node: job, id, name, row, column, prerequisites,
max level, cost per level, bonus kind, the activities it feeds, and the value per level. Every number in both files
is a **placeholder**: the user decided on 2026-10-30 that balance is deferred to the end of the project. This talk
decides the shapes, not the tuning. This file and the JSON change nothing in the spec. WP-M3-7, and the WP that owns
job XP for decision 7, write the decided rules into spec 5.1 and `13-decision-log.md`.

## 1. Decisions (2026-10-30)

| # | Decision | Spec |
|---|---|---|
| 1 | **Size.** About 8 nodes per job: Blacksmith 9 nodes and 32 levels, Jeweler 9 nodes and 35 levels. Most nodes have max level 3 to 5; unlocks have 1. **Each level costs 1 point.** The job level cap is 20 with 1 point per level (`JobSettings`), so a full tree holds about 1.5 times the budget and the player chooses about two thirds of it. It is a craft tree, not a combat tree. **(decided)** | R-5.1-04 |
| 2 | **Rows.** 4 rows (0 to 3), each opened by a minimum job level: **1, 5, 10, 15**. A link between two nodes is a **prerequisite**: learn the first to the level given before the second, as in the class trees (R-3.5-04). **(decided)** | R-5.1-04, R-3.5-04 |
| 3 | **No shared trunk.** Each job has its own nodes, and no node belongs to both jobs. In M3 the Jeweler gathers nothing (all four node types are the Blacksmith's, spec 5.2), so a shared gathering trunk does not fit. Both trees follow **one template**: a "Job XP +%" node in row 0, then one branch per activity of the job. **(decided)** | R-5.1-04 |
| 4 | **The node list** in section 2. Each node has one bonus kind and the activities it feeds (`JobXpWeights` activity ids). Every bonus kind has a **cap on the sum of its sources**, and `IJobBonusQuery` returns the capped value when an activity starts (spec 5.1, architecture rule 2). This includes **gem tiers and gem conversion by the Jeweler** (Facet Master, R-5.2-04 on gems) and **Extra Strike** raising the forge action count per session (the count WP-M3-6 caps). **(decided)** | R-5.2-04, R-5.3-01..05 |
| 5 | **Recipe tiers.** A **Master Molds** node (max level 1, row 3) unlocks **making** the job's T2 and T3 mold recipes. It does not gate **using** a mold, because molds are tradable items (M3 plan decision 4). Using a mold needs only its job level. **(decided)** | R-5.3-01 |
| 6 | **Respec.** Free, with **all points back at once** (R-5.1-04). It is done from the Jobs window anywhere, like the class tree, and refused **in combat and during a harvest or forge session**. A **confirmation** step comes first. It clears unlocks, but molds already made stay the player's. **(decided)** | R-5.1-04 |
| 7 | **The secondary job.** It has its own full tree, and its points come from its own level. Its limits (decided by the user in this talk, a change to R-5.1-03): **(decided)** <br>a. **it needs more XP per level**: the job level curve times `secondaryXpPercent`, placeholder **150**; <br>b. **its level can never pass the main job's level**; <br>c. while it is at the main job's level, its XP is **banked, up to one level's worth** (placeholder), and the rest is dropped. When the main job levels up, the banked XP is applied at once. | R-5.1-03 |
| 8 | **The tree tab in the Jobs window** (the skill tree window's pattern, spec 9.13) shows: the rows with their level gates and links; each node's level out of its max and its cost; the bonus now and at the next level; the unspent points; Learn and Respec buttons (Respec with its confirmation); and a summary of the job's active bonuses with their caps. **(decided)** | 9.13 |
| 9 | **Deferred to M4:** contract bonuses (lower fees, more contract slots), selling the job's services to others through contracts (R-5.3-05, R-5.4), and any tree bonus to the minigame score, whose own cap stays (R-5.3-02). **(decided)** | R-5.3-02, 5.4 |
| 10 | **Learning works any time**, like the class tree (no combat or session refusal). A running harvest or forge session keeps the bonuses it read when it started. **(decided)** | R-5.1-04, 5.1 |
| 11 | **A respec resets one job's tree**: one Respec button per job. **(decided)** | R-5.1-04 |
| 12 | **Firm Hammer feeds craft only**, not the Blacksmith's own refine recipes (ore to ingot). No Blacksmith node feeds refine. **(decided)** | R-5.3-02 |
| 13 | **`gemFindPercent`** is percentage points added to the gem chance. WP-M3-5's yield roll decides how it maps onto the yield table's gem entries (`NodeYieldEntry`). **(decided)** | R-5.2-01 |

## 2. The trees

Column is only the tab's layout. In the JSON, the requirement field is `requires`, which points at the node and level
needed. Value per level is a placeholder.

### Blacksmith (mining, forge, conversion, enchanting)
| Row (job level) | Node | Bonus kind | Activities | Max | Requires | Per level |
|---|---|---|---|---|---|---|
| 0 (1) | Diligent Smith | `jobXpPercent` | all its activities | 3 | | +5 % |
| 0 (1) | Prospector | `yieldPercent` | harvest | 5 | | +4 % |
| 0 (1) | Firm Hammer | `qualityBonus` | craft | 5 | | +2 |
| 1 (5) | Keen Eye | `gemFindPercent` | harvest | 3 | Prospector 1 | +1 pp |
| 1 (5) | Transmuter | `conversionOddsPercent` | convert | 5 | | +2 pp |
| 2 (10) | Extra Strike | `actionCount` | craft | 2 | Firm Hammer 1 | +1 action |
| 2 (10) | Salvager | `keepInputsPercent` | convert (on failure) | 3 | Transmuter 1 | 10 % |
| 2 (10) | Runesmith | `enchantOddsPercent` | enchant | 5 | | +1 pp |
| 3 (15) | Master Molds | `unlockMoldTiers` | craft (mold recipes) | 1 | | T2, T3 |

### Jeweler (gem refining, jewelry, sockets, gem conversion, enchanting)
| Row (job level) | Node | Bonus kind | Activities | Max | Requires | Per level |
|---|---|---|---|---|---|---|
| 0 (1) | Diligent Jeweler | `jobXpPercent` | all its activities | 3 | | +5 % |
| 0 (1) | Gem Cutter | `qualityBonus` | refine (gems) | 5 | | +2 |
| 0 (1) | Fine Setting | `qualityBonus` | craft (rings, amulets) | 5 | | +2 |
| 1 (5) | Steady Hand | `socketOddsPercent` | gemSocket | 5 | | +2 pp |
| 1 (5) | Facet Master | `conversionOddsPercent` | convert (gems) | 5 | | +2 pp |
| 2 (10) | Gentle Extraction | `removalCostPercent` | gemRemove | 3 | Steady Hand 1 | -10 % |
| 2 (10) | Salvager | `keepInputsPercent` | convert (on failure) | 3 | Facet Master 1 | 10 % |
| 2 (10) | Runecarver | `enchantOddsPercent` | enchant | 5 | | +1 pp |
| 3 (15) | Master Molds | `unlockMoldTiers` | craft (mold recipes) | 1 | | T2, T3 |

`pp` means percentage points added to a chance. `qualityBonus` is in the units of `QualityCalc`'s skill-tree term
(R-5.3-02). The placeholder caps per kind are in the JSON's `caps`. The enchant cap is the one in R-5.3-04.

## 3. Reasoning

- **A small tree for a small budget.** 20 points at job level 20 buy a few real bonuses. Over-filling the tree by
  about half makes the player choose: a mining Blacksmith and a forging Blacksmith end up different.
- **Level-gated rows** reuse the class tree's idea (`minCharLevel` per row), so the window and the rules stay one
  pattern. With rows at 1, 5, 10 and 15, the last row comes late in the job's 20 levels.
- **One template, not a shared trunk.** A shared trunk would need an activity both jobs share, and in M3 they share
  only conversion and enchanting. A common shape (job XP first, a branch per activity) keeps the two trees readable
  side by side.
- **Mold unlocks gate making, not using.** Molds are tradable products (M3 plan decision 4, R-5.4-01), so a buyer must
  be able to use one. The node is what makes a Blacksmith or Jeweler the source of higher-tier molds.
- **A free respec** (R-5.1-04) that works where the class tree's does. Refusing it during a harvest or forge session
  keeps a session's bonuses fixed: they are read when the activity starts (spec 5.1).
- **The secondary job** stays a full job with its own tree. It levels slower and never above the main, so the main
  job stays the character's identity. Banking one level means time on the secondary is never wasted, but it cannot
  run far ahead.

## 4. What WP-M3-7 needs from this

- The `JobTree` rows come from section 2 and the JSON: job, node, row, column, prerequisites, max level, cost per
  level, bonus kind, activities, and value per level. The row level gates are one list (`rowMinJobLevel`).
- New bonus kinds feed systems that are built in parallel:
  - `yieldPercent`, `gemFindPercent`: harvesting, WP-M3-5;
  - `qualityBonus`, `actionCount`, `conversionOddsPercent`, `keepInputsPercent`, `unlockMoldTiers`: the forge, WP-M3-6;
  - `enchantOddsPercent`: enchanting, WP-M3-8;
  - `socketOddsPercent`, `removalCostPercent`: gems, WP-M3-9. Gem tiers and gem conversion need WP-M3-9's `GemDef`
    tiers.
- Decision 7 changes job XP (`JobMath`, `JobService`, `JobSettings`: `secondaryXpPercent`, the bank size), which is
  WP-M3-1's code. The orchestrator decides which WP carries it, and that WP updates R-5.1-03.

## 5. Open

Nothing from this talk is Open. Every number in both files is a placeholder for the balance phase.
