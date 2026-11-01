# Bot loop steps: the contract (WP-M2-12, WP-M3-12)

One file per step. A WP makes its step real by replacing **that file's body** and nothing else in the loop. The M2 steps
and what they do are in the BotClient README ("The M2 loop"); this file is the contract every step follows, and the M3
step table.

## Behaviours, entries, scripts
- A `BotBehaviour` row with `loopSteps` plays them in order, round after round (`Loop/BotLoop.cs`). Without them the bot
  is the M1 brain alone (`field-combat`, CI).
- **An entry is `"<step>"` or `"<step>:<argument>"`** (WP-M3-12): `takeJob:blacksmith`, `buy:m3.pickaxeT1`, `level:12`,
  `equip:crafted`, `fight:12` (the kills of that fight). The registry (`Loop/BotStepRegistry.cs`) creates one step object per entry and passes it the argument
  (empty without one). A step that takes no argument ignores it. A step that cannot read its argument throws at the
  start of the run (`LevelStep`, `EquipStep`).
- **A missing row is "not yet", not an error.** An argument that names a row the tables do not have yet (WP-M3-2's
  `m3.pickaxeT1` before it merges) makes the step return `NotAvailable` with the reason. The validator only checks that
  the step id is known and the argument not empty.
- **A loop** (`loopOnce` false) needs a `fight` entry and each entry once (two entries of one step with different
  arguments are fine: `buy` and `buy:m3.pickaxeT1`).
- **A script** (`loopOnce` true, the M3 exit run): the entries play once, in order, and each entry's outcome is kept
  (`BotLoop.Outcomes`: done, not yet with the reason, failed, timed out). A script may repeat an entry and needs no
  `fight`. After the last entry the loop is `Finished` and the bot holds still; `loadtest` ends the run once every
  bot's script finished and, with `BotScenario.requireStepsDone`, passes only when every entry is done.

## The step contract (`Loop/IBotStep.cs`, `Loop/BotStepContext.cs`)
- **Ticking.** `Begin(context)` when the step becomes current, then `Tick(context, ref command)` once per client tick.
  - It returns `Running` (the command it filled is sent), `Done`, `NotAvailable` or `Failed`.
  - Anything but `Running` hands over to the next step in the same tick and leaves the command alone.
  - A step running past `loopStepTimeoutMs` is ended and counted as `timedOut`.
- **What the statuses mean.**
  - `Done`: it did its job, or there was nothing to do (with a counter: `sell.nothingToSell`).
  - `NotAvailable`: the game cannot do it yet (protocol or content not landed). Return it through
    `context.NotAvailable(id, why)`: it logs once per run, and a script prints `why` as the "not yet" reason. Name the WP
    that lands what is missing in `why`.
  - `Failed`: it tried and could not (a refusal: count it as `<step>.refused.<reason>`), or the entry asked for
    something it could not get (`buy:<item>` without the gold). Return it through `context.Failed(why)` so a script
    prints why beside the entry. The loop goes on either way.
- **Reading the world** goes only through the context, which holds the Unity client's engine-free state (ClientCore,
  `src/Tools/ClientCore`), never server code. The world client keeps these from the first message on, so read the kept
  state, not only `OnReliable` (QA M2 wave 0 F2).
  - `context.Entities`: ClientCore's `EntityRegistry`, every entity of the snapshots with `Kind`, `DefId` and position
    samples (NPCs; **resource nodes** as `EntityKind.ResourceNode` with their state once WP-M3-4 lands; `DefId` is
    `NodeDef.netId`).
  - `context.Inventory`: `ClientInventory` (`InventoryContents`: the Normal bag in `Slots`, every other container in
    `Containers`, the Safe bag equipment area with tools and molds included; WP-M3-2 adds the typed properties per
    instance: crafted stats, quality, enchant, gems, tainted, reserved).
  - `context.Outposts` (NPCs, vendor and outpost requests, committed gold), `context.Progression` (level, exp, rank,
    points), `context.SkillTree`, `context.Quests`.
  - **`context.Jobs`** is ClientCore's `ClientJobs` (`Entities/Core/ClientJobs`, WP-M3-1), fed by the world client from
    the first message on: the `takeJob`, `learnJob`, `gather`, `refine` and other job steps read the character's jobs, job
    levels, points and learned tree nodes from it (`Held`, `InSlot`, `Learned`), and send `Request`/`TreeRequest` through it.
  - `context.View` (predicted and server-confirmed position, meters, monsters, own drops, action idle), `context.Own`
    (level, exp, gold), `context.Data` (tables, markers with npc `defId`s, grid), `context.InCombat`.
  - `context.ApproachNpc(wanted, ref command, …)` walks to an NPC for its interaction range (a job master, a forge, a
    bench: the NPCs of the new `NpcInteraction` kinds).
- **Acting.** Fill the tick's `BotCommand` (`context.Walk(goal, command)` walks the grid path), or send a reliable
  request with `context.SendReliable(ReliableMessages.Write(id, message))`. The server needs a `ReliableMessageCaps` row
  and an `InputRouter` route for the id (R-2.5-07). Answers come back through `OnReliable(context, id, message)`, which
  every step gets for every reliable message, after the world client's own handling. Keep the request id and wait for
  the answer with that id (`BuyStep`, `EquipStep`): value-moving answers come after the commit.
- **Reporting.** `context.Count("<step>.<what>", n)`. The loop itself counts `<step>.done`, `.notAvailable`, `.failed`,
  `.timedOut` and the rounds. All of it lands in `summary.json` under `loop` and per bot (`botsDetail[].loop`); a
  script's outcomes under `botsDetail[].script` and `scripts`.
- Everything runs on the bots' simulation thread, one call at a time.

## The M3 steps (WP-M3-12 placeholders)
Every M3 step id is already in `BotBehaviour.LoopStepIds` and the registry: a WP replaces the body of its file only.
WP-M3-3 made `useItem` real (an argument names the item to use at once; without one, the HP threshold).

| Step | File | Today | Made real by | Exit run entry |
|---|---|---|---|---|
| `level` | `LevelStep.cs` | plays: done when the own level reaches the argument, failed below it (`level.below`: the seed did not apply) | WP-M3-12 | `level:16` (both) |
| `takeJob` | `TakeJobStep.cs` | plays: walks to the job master and picks the job as main (or as secondary once a different main is held); done with `takeJob.held`, `takeJob.tooLow` below the job's `minLevel`, `takeJob.waiting` without a job state; failed with `takeJob.refused.<reason>` | WP-M3-1 | `takeJob:blacksmith`, `takeJob:jeweler` |
| `learnJob` | `LearnJobStep.cs` | plays: spends the job's points (argument a `JobDef` id, else the main job) on the first node in table order that can take a level, one `JobTreeRequest` at a time, while points are left; done with `learnJob.nothing` (no points, nothing to learn, the job not held) or `learnJob.waiting` (no job state); failed with `learnJob.refused.<reason>`; counter `learnJob.levels` | WP-M3-7 | `learnJob:blacksmith`, `learnJob:jeweler` |
| `buy` with an argument | `BuyStep.cs` | plays: holds at least one of the item in any container, else buys one; failed without the gold or a vendor; not yet while the item or a vendor row is missing | WP-M3-2 adds `m3.pickaxeT1` and its vendor row | `buy:m3.pickaxeT1`, `buy:m2.potionHpSmall` |
| `gather` | `GatherStep.cs` | plays: walks to a node of an own job with its tool held and harvests it `gather:<N>` times (or a `NodeDef` id once); done with `gather.noJob`, `gather.noTool`, `gather.noNode` (failed in a script) | WP-M3-5 (nodes: WP-M3-4) | `gather:6` (the refines' ore, `BotTablesTests`) |
| `refine` | `RefineStep.cs` | plays (WP-M3-6, `ForgeRun.cs`): at a station of its job, the first refine recipe it can make now (or `refine:<RecipeDef id>`), once; `refine.made`, `refine.noMaterials` (failed in a script), `refine.refused.<reason>`; "not yet" while no refine recipe of a held job makes anything (the Jeweler's, as `craft` with its output-less molds) | WP-M3-6 (ore); WP-M3-9 (the Jeweler's rough gem) | `refine` (both) |
| `craft` | `CraftStep.cs` | plays (WP-M3-6): the first mold in its Safe bag it can fill (or `craft:<MoldDef id>`, also a mold recipe), each allowed forge action once, then finish; `craft.made` | WP-M3-6 | `craft` |
| `convert` | `ConvertStep.cs` | plays (WP-M3-6): the first conversion it can make; a failure is done (`convert.convertFailed`) | WP-M3-6 | (loop only) |
| `equip` with `crafted` | `EquipStep.cs` | plays (WP-M3-6): the newest instance with a crafter, whatever its rarity; failed without one (`equip.noCrafted`) | WP-M3-6, with WP-M3-2's properties | `equip:crafted` |
| `enchant` | `EnchantStep.cs` | not yet | WP-M3-8 | `enchant` |
| `socket` | `SocketStep.cs` | not yet | WP-M3-9 | `socket` |
| `useItem` | `UseItemStep.cs` | plays (WP-M3-3): below `loopDrinkBelowPercent` drinks a held HP potion; `useItem:<item>` uses that item at once | WP-M3-3 | `useItem:m2.potionHpSmall` (right after the fight: in combat) |

- A placeholder keeps its argument and shows it in the reason. Each file's summary says what the real step should do;
  the WP's plan decides the details.
- **Job choice without an argument** (`m3-loop`'s `takeJob`): the `JobDef` rows in id order, round robin by bot index,
  the suggested rule for WP-M3-1. Below the job's `minLevel` the step is done with `takeJob.tooLow` (not failed), so the
  M3 loop plays the M2 steps until level 12.
- **Open for WP-M3-9:** where the Jeweler's rough gem comes from (decision 11: mining or monster drops). Its exit row has
  no entry before `refine` yet; the WP adds them (or a test seed).
- A step name must not change without the orchestrator: the exit rows and the parallel WPs use these.

## The test every step needs
The M2 steps' tests are the model (`tests/GameServer.Tests/Bots/BotLoopStepTests.cs`): a step context over the
repository's tables with nothing connected (the `Bot` helper), an NPC or node in the client's registry from a snapshot as
the server writes it, and the server's answers applied to the kept client state as the world client does. Each step's
test covers:
- the request it sends (ids, counts, quoted totals) and that it waits for the answer with its request id;
- done with the right counters after the answer;
- every refusal counted as `<step>.refused.<reason>` and `Failed`;
- "nothing to do" done at once without walking or sending;
- its argument (if it takes one).

The loop tests (`BotStepRegistryTests`: the `m3-loop` round and the exit scripts) change only when a step stops being a
placeholder: update the expected outcome of that entry there. An outcome that depends on content another WP lands (a
`buy:<item>` row) is read from the tables there, never assumed (`BuyWithNothingConnected`).
