# 14. Constellar System and Bystial Book

Two progression systems decided with the team on 2026-10-25 (decision log, section 13). Neither is part of Milestone 1:
the Bystial book ships in **M2**, the Constellar system in **M6** (section 12.2). Every number here is a placeholder
in config until its milestone's tuning. Rules marked **Open** are not decided: do not invent them, ask.

## 14.1 Constellar system
A circular ("pizza") board of passive bonuses, separate from the skill tree (section 3.5). The character starts at
the center and spends **star points** outward along the slices they want.

**Rules**
- **R-14.1-01** **Board shape:** one board per character, a circle cut into **5 slices**, one theme each: **physical
  offense, magic offense, defense, vitality and regeneration, stamina and mobility** (no utility slice: jobs and loot
  bonuses live elsewhere). Rings go from the center outward and bonuses grow with the ring. The **center node is free**
  and owned from character creation. A node can be bought only when it is **adjacent to an owned node**, so every
  allocation is a path from the center. Slices connect to each other **only at the outer ring**, through a few
  **bridge nodes**. **Open:** the number of rings and of nodes per ring.
- **R-14.1-02** **Star points** are their own currency, **separate from skill points** (R-3.5-04) and **per character**.
  They come from several sources, each a row of a config table: **level milestones** (every N levels), **achievements**,
  **rank-ups**, **first kills of bosses**, and **selected quests**. The total budget a character can earn **fills one to
  two slices**, never the whole board, so every character chooses a direction. Each grant is idempotent (one per
  source event).
- **R-14.1-03** **Node types:** **stat nodes** (a flat or percent bonus to one stat of section 3.4; every multiplier is
  capped per node and per board, architecture rule 2) and **gem slots**, which are fewer and sit farther from the
  center, so the strongest gems need commitment to a slice.
- **R-14.1-04** **Star Gems** are an **item family of their own**, distinct from the equipment gems of R-5.3-05 (no
  Jeweler, no equipment socket). A Star Gem has a **tier**; by tier it gives a **fixed stat bundle**, a **triggered
  effect** (for example "on a critical hit, +X stamina"), or both. They **drop from monsters** through the loot tables,
  mainly from higher-tier monsters; they are **tradeable** and live in the **Normal bag**, so they **drop on PvE death**
  (R-4.1-02) and follow the stolen-item rules of section 4.4 like any bag item. The player **sockets and removes them
  freely out of combat**; removal returns the gem intact to the bag. **Open:** the tier ladder, the effect list, and
  whether a slot accepts any tier or only its own.
- **R-14.1-05** **Respec is free, any time out of combat**, with the same blocks as the skill tree (R-3.5-04: not in
  combat, instances or boss fights). It refunds every star point and returns every socketed gem to the Normal bag.
  **Open:** what happens when the bag has no room for the gems (proposal: refuse the respec with a reason code).
- **R-14.1-06** **Milestone:** the board, its UI and its effects ship in **M6** with class depth. Star Gem rows may
  enter the loot tables from M2 as data, but they do nothing until M6.

**Implementation**
- Config: `ConstellarBoard` (nodes with slice, ring, adjacency, type, stat and value or gem slot; bridge nodes),
  `StarPointSources` (source kind, trigger, points), `StarGemDef` (tier, stats, triggered effect, loot references),
  `ConstellarCaps` (per-node and per-board caps).
- State (realm database): `constellar_nodes` (character, node, allocated at), `constellar_gems` (character, slot node,
  gem instance), `star_points` (earned, spent, ledger with idempotency keys). Save class 2 (section 7.1) for
  allocations; gem moves go through `InventoryService` like any item move (architecture rule 4), in one transaction.
- `ConstellarService` is the only writer of allocations and star points. It consumes `LevelUp`, `RankChanged`,
  `AchievementDone`, `Killed` (first boss kills) and `QuestStepDone` for grants; emits `StarPointsGained`,
  `ConstellarChanged`. `StatBlock` gets the source types `constellar` (nodes) and `starGem`; triggered effects reuse
  the `PassiveTriggers` mechanism of section 3.5.
- Client: a board window (section 9, M6) reading the same `ConstellarBoard` table; allocation and socketing are
  requests with reason codes (`NotAdjacent`, `NoStarPoints`, `SlotOccupied`, `WrongGemTier`, `InCombat`).

**Talks to** reads `StatBlock`, `InventoryService`, `Rank`; emits `StarPointsGained`, `ConstellarChanged`; consumed by
Stats, Telemetry.

## 14.2 Bystial book
A bestiary that rewards hunting the same monster: cards earned by kill counts, placed in a book for bonuses.

**Rules**
- **R-14.2-01** **Kill counters:** the server keeps an idempotent **kill count per character and monster type**
  (the same counters the achievements of R-3.6-05 use), persisted with the character.
- **R-14.2-02** **Earning a card is guaranteed, not a drop.** The first card of a monster type appears when the
  counter reaches that type's first milestone (placeholder: **100 kills, normal card**). The card **evolves to the
  next tier at each later milestone** (placeholder: **500 kills, rare**, and so on); tiers and thresholds are config
  per monster type, and **bosses have much lower thresholds** since they are killed rarely. **Open:** the tier ladder
  beyond rare and the exact thresholds.
- **R-14.2-03** **A card is progression state, not an item:** it lives only in the book, is **bound to the character**,
  and is **never in a bag, dropped, traded or stolen**. There are no duplicate cards: hunting more evolves the card.
- **R-14.2-04** **Book slots grow with rank** (F to SS, a table of slots per rank). **One card per monster type** can
  be placed at a time. **Open:** the slot count per rank.
- **R-14.2-05** **Effects:** a placed card gives a **small stat bonus** plus **one effect themed after the monster**
  (for example the demon's card raises fire resistance; a boss card carries a triggered effect). Every monster type
  belongs to a **family** (demons, beasts, ...); placing several cards of one family adds a **family set bonus** at
  config thresholds. All bonuses are capped (architecture rule 2). **Open:** the family list, the effect list, and
  whether card bonuses apply in the arena (section 6.6 uses class templates).
- **R-14.2-06** **Placing and swapping cards is free out of combat**, with the same blocks as R-3.5-04.
- **R-14.2-07** **Milestone:** counters, cards and the book ship in **M2** with the core loop.

**Implementation**
- Config: `BystialCardDef` (monster type, family, tiers with `killsRequired`, stat bonus and effect per tier),
  `BystialSlots` (slots per rank), `BystialFamilyBonus` (family, cards placed, bonus).
- The kill counters exist since WP-M2-3 (spec 3.6): `monster_kill_counts` (migration V0005), kept by `AchievementService`
  through `ProgressionStore` (`KillCount(character, MonsterDef.id)`); the book reads them and does not count kills again.
- State (realm database): `monster_kill_counts` (character, monster type, count), `bystial_book` (character, slot,
  monster type, tier earned, placed). Save class 2; the book changes in one transaction with an audit row.
- `BystialService` is the only writer. It consumes `Killed` (idempotent per kill, by the same counter the achievements
  use), evolves cards at milestones, and handles place and swap requests with reason codes (`NoFreeSlot`,
  `CardNotEarned`, `AlreadyPlaced`, `InCombat`); emits `CardEarned`, `CardEvolved`, `BookChanged`. `StatBlock` gets
  the source type `bystialCard`; family bonuses are recomputed on every book change.
- Client: a book window (section 9, M2) with the card collection, counters toward the next tier and the slots.

**Talks to** consumes `Killed`; reads `Rank`; emits `CardEarned`, `CardEvolved`, `BookChanged`; consumed by Stats,
Achievements, Telemetry.
