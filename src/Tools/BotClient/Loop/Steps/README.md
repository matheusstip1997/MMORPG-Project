# Bot loop steps: the contract (WP-M2-12, WP-M3-12, WP-M4-12)

One file per step. A WP makes its step real by replacing **that file's body** and nothing else in the loop. The M2 steps
and what they do are in the BotClient README ("The M2 loop"); this file is the contract every step follows, the M3
step table, the pairs of M4 and the M4 step table.

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
| `refine` | `RefineStep.cs` | plays (WP-M3-6, `ForgeRun.cs`): at a station of its job, the first refine recipe it can make now (or `refine:<RecipeDef id>`), once; `refine.made`, `refine.noMaterials` (failed in a script), `refine.refused.<reason>`; the Jeweler cuts a rough gem at the bench (WP-M3-9, `m3.cut.rubyT1` first) | WP-M3-6 (ore); WP-M3-9 (gem cuts) | `refine` (both) |
| `craft` | `CraftStep.cs` | plays (WP-M3-6): the first mold in its Safe bag it can fill whose output the bot can wear (an armor output of another tier is skipped, R-4.1-05, WP-M4-13; or `craft:<MoldDef id>`, also a mold recipe, whatever its tier), each allowed forge action once, then finish; `craft.made`; a hit plays the forge timing bar (WP-M3-10: a press near the centre, off by up to `forgeBarErrorPermille`; `craft.barHits`, `craft.barScore`, `craft.barOutcome.<outcome>`) | WP-M3-6, WP-M3-10 | `craft` |
| `convert` | `ConvertStep.cs` | plays (WP-M3-6): the first conversion it can make; a failure is done (`convert.convertFailed`) | WP-M3-6 | (loop only) |
| `equip` with `crafted` | `EquipStep.cs` | plays (WP-M3-6): the newest instance with a crafter, whatever its rarity; failed without one (`equip.noCrafted`) | WP-M3-6, with WP-M3-2's properties | `equip:crafted` |
| `equip` with an item id or `wrongTier` | `EquipStep.cs` | plays (WP-M4-13, R-4.1-05): `equip:<ItemDef id>` equips the first bag piece of that item (failed without one, `equip.noItem`, or on a refusal); `equip:wrongTier` sends the first bag armor piece of another tier than the bot's (`SkillTreeRules.EffectiveArmor`) and is done only on `ArmorTierMismatch` (`equip.wrongTierRefused`; failed without one, `equip.noWrongTier`). The plain `equip` and `equip:crafted` skip armor of another tier | WP-M4-13 | `m4-armor` |
| `enchant` | `EnchantStep.cs` | plays (WP-M3-8): the first item of a held job below +9 (Equipped first; or `enchant:<ItemDef id>`), its stones in the Normal bag, at a station of the item's job, one `EnchantRequest`, waits for the answer (after the commit); counters `enchant.attempt`, `enchant.up`/`.same`/`.down`, `enchant.refused.<reason>` (failed); `enchant.noItem`, `enchant.noStones`, `enchant.noStation` (done in a loop, failed in a script) | WP-M3-8 | `enchant` (after `fight:until=m3.enchantStone@m1.spawn.combat.a`, the fight step's `until=<item>[x<n>]` argument: fight until the bag holds the stones) |
| `socket` | `SocketStep.cs` | plays (WP-M3-9, ClientCore's `ClientGems`): at a jeweler's bench, the argument's cut gem (or the first in the Normal bag) into the first gear with a free gem slot, worn first; done on the committed answer either way (`socket.socketed`, `socket.failed`: the gem lost, decision 11); `socket.noGem`, `socket.noTarget`, `socket.noJob`, `socket.noStation` (failed in a script), `socket.refused.<reason>` | WP-M3-9 | `socket` |
| `dev` | `DevStep.cs` | plays (WP-M3-13): `dev:<command>` sends the chat line `/<command>` to the dev console (a server with `MMORPG_DEV_TOOLS=1`), waits for its `DevCommandResult` (a rate-limited line is sent again after `ChatSettings.periodMs / messagesPerPeriod`) and logs the answer's lines; done when it ran, failed with `dev.refused.<reason>` | WP-M3-13 | (none; behaviour `dev-console-demo` is a dev session script: `loadtest --behaviour dev-console-demo --bots 1` with `MMORPG_DEV_TOOLS=1` set) |
| `useItem` | `UseItemStep.cs` | plays (WP-M3-3): below `loopDrinkBelowPercent` drinks a held HP potion; `useItem:<item>` uses that item at once; `useItem:<item>@<camp>` (WP-M3-12) fights at the camp until in combat with HP missing, drinks, and is done only when HP and toxicity rose (reads `context.Stats`, `OwnStats`) | WP-M3-3, WP-M3-12 | `useItem:m2.potionHpSmall@m1.spawn.combat.a` |

## The M4 steps
| Step | File | Today | Made real by | Exit run entry |
|---|---|---|---|---|
| `mail` | `MailStep.cs` | plays (WP-M4-3, WP-M4-14, ClientCore's `ClientMail`): `mail:claim` walks to the nearest mailbox (`ApproachNpc` on `NpcInteraction.Mail`: an entity in interest, else the `m1.npc.mailbox` marker; R-4.5-03, attachments are taken at a mailbox), then claims every open mail there (claim all, oldest first, naming the mailbox) while the `MailCount` it holds is above 0, out of combat; each answer after the commit: `mail.claimed` (mails emptied), `mail.gold`, `mail.units`; `mail.partial` and done when the bag left units in a mail, `mail.bagFull` done (the mail waits); `mail.refused.MailboxNotInReach` walks on and asks again; `mail.none` with nothing to claim and `mail.noMailbox` without a mailbox (both failed in a script); any other `mail.refused.<reason>` failed. `mail:read` (WP-M4-14) reads where the bot stands: the list and the oldest mail (`mail.read`), then, away from a mailbox, one claim of it that must be refused `MailboxNotInReach` (`mail.awayRefused`; anything else fails, `mail.awayClaimed`); at a mailbox no claim (`mail.readAtMailbox`) | WP-M4-3, WP-M4-14 | `mail:read`, `mail:claim` (a seeded mail: `BotScenario.seedMail`, `DbMigrate --dev --mail`) |

- A placeholder keeps its argument and shows it in the reason. Each file's summary says what the real step should do;
  the WP's plan decides the details.
- **Job choice without an argument** (`m3-loop`'s `takeJob`): the `JobDef` rows in id order, round robin by bot index,
  the suggested rule for WP-M3-1. Below the job's `minLevel` the step is done with `takeJob.tooLow` (not failed), so the
  M3 loop plays the M2 steps until level 12.
- **The Jeweler's rough gems (WP-M3-9):** monster drops (every node is the Blacksmith's): `fight:until=<item id>x<count>` (`FightStep`, the WP-M3-8 form) fights until
  the bag holds that many (`fight:until=m3.roughGemT1x2`), then two `refine`, `craft:m3.moldRing` (a ring from one cut gem) and `socket` (the other into the ring).
- A step name must not change without the orchestrator: the exit rows and the parallel WPs use these.

## Pairs (WP-M4-12, M4)
Two bots play as a pair when the scenario says so; the M4 steps that need a second player address the partner.

- **Who plays with whom.** `BotScenario.pairs` (`[Optional]` bool): bot 2k plays with bot 2k+1 (`bots` must be even;
  `BotScenario.PartnerOf`). Each side gets its own row through `botBehaviourIds` round robin (`["m4-exit-a", "m4-exit-b"]`:
  bot 0 is A, bot 1 is B). A behaviour that uses a pair step (`BotBehaviour.PairStepIds`: `meet`, `party`, `whisper`,
  `trade`, `contract`) needs a scenario with `pairs` (a set rule). The QA partner bot is the one exception: its partner is
  the smoke's player (below).
- **The pair link.** `context.Pair` is the bot's side of the pair (`PairLink`, engine-free in
  `unity/Assets/Game/Entities/Core/PairLink.cs`, so the Unity harness uses the same code); null when the bot plays alone,
  and then `meet` fails with "no partner" and the party and whisper steps report "not available: partner needed".
  - `Pair.IsFirst`: the even bot (in the QA smoke the player is first, the partner bot second). Use it to choose who
    invites, who offers first.
  - `Pair.PartnerName`: the partner's character name. Every bot posts its own once in the world.
  - `context.TryPartnerEntity(out id, out at)`: that name looked up in `context.Identities` (ClientCore's
    `EntityIdentities`, from the server's `EntityIdentity`, already through `DisplayIdentity`, rule 8), then the entity
    in the newest snapshots. A request that names the partner sends this entity id, or the display name, as the protocol
    of its WP asks; never a character id the client cannot know.
  - `context.ApproachPartner(reachCm, ref command)`: walks to the partner (its entity when in interest, else its last
    posted position; every bot posts its own as it moves) until both the predicted and the server's own position are
    within `reachCm` (`NpcApproach.InReach`); `None` while the partner has not posted where it is.
- **The sync primitive: meetings on the pair board.** Each side posts keys and values on its board and reads the
  partner's (newest value per key wins). A **meeting** is a named point both sides must reach:
  - `int n = Pair.Arrive("<point>")`: this side reached its n-th occurrence of the point (a loop meets again every round).
  - `Pair.Check("<point>", n)`: `Met` once the partner arrived at the same occurrence (or later), `PartnerGaveUp` when the
    partner's step ended without meeting, `PartnerGone` when the partner's script finished or it disconnected, else
    `Waiting`. A step waits at most `BotBehaviour.pairWaitMs` (placeholder 120 s) and then fails "partner did not reach".
  - **The loop gives up every meeting a step arrived at and did not see met when the step ends** (whatever its status), so
    a failed or timed-out side never leaves its partner waiting.
  - `Pair.Post("<step>.<what>", value)` / `Pair.TryRead(...)`: a fact the server does not tell (what was offered).
  - **Game outcomes are read from the server, never from the board.** The board only says "I am here" and "I am ready";
    whether the party formed or the trade committed comes from the server's answers, after the commit. Example (WP-M4-2):
    `party:invite` invites `Pair.PartnerName` and is done on the committed `PartyState` with both; `party:accept` waits for
    the partner's `PartyInvite` message, accepts it and is done on the same `PartyState` (the server's state is the sync: no meeting).
- **Every pair leg starts with its own `meet:<point>`.** A script entry whose step needs the partner in sight or ready (a
  trade, a purchase from the partner's listing or stall, a contract) comes right after a `meet:<point>` of its own on both
  sides (`meet:trade` before `trade:...`). Without it one side can reach the leg while the other still fights or walks, and
  the step gives up on its own timeout (`m4-exit` run 2: the trade gave up after `TradeSettings.inviteTimeoutMs`). Steps
  that sync on the server's state anyway (`party:invite`/`accept`) may share the start meeting.
- **Where the board lives.** In a load test (`loadtest`, `run`) the two sides share one `MemoryPairBoard` (every bot ticks
  on the runner's one thread, in index order: no lock). For the QA partner bot it is a `FilePairBoard`: one append-only log
  per side in `<run>\partner` (`player.log`, `bot.log`, lines `key<TAB>value`), read incrementally; both logs stay as
  evidence.
- **The QA partner bot** (`tools/qa/Run-QaSmoke.ps1 -Partner`, `BotClient run --partner-dir <dir>`): one bot plays the row
  `qa-partner`, whose only entry `serve` stands by. A smoke step asks it to play a partner script with
  `Pair.Request("<script>")` (`script.<n>`); `serve` plays the row `qa-partner-<script>` (a `loopOnce` script) inside its
  own entry and answers `result.<n>`: `done`, or the first entry that was not done as `<outcome>; <entry>: <reason>`, then
  every entry's outcome. The script's steps read the `qa-partner` row's tuning (`context.Behaviour`). Today
  `qa-partner-join` is `meet:join` (the smoke's `partner-join`). A WP whose smoke step needs the partner adds its row
  (`qa-partner-party-up`, `qa-partner-trade-item`, `qa-partner-buy-listing`, `qa-partner-buy-from-stall`,
  `qa-partner-loot-bag-stolen`, `qa-partner-contract-sword`) with the pair steps it plays.

## The M4 steps (WP-M4-12 placeholders)
Every M4 step id is in `BotBehaviour.LoopStepIds` and the registry; a WP replaces the body of its file only (for an argument
branch of an existing step, that branch only). A placeholder is "not yet" naming its WP and keeps its argument in the reason.

| Step | File | Today | Made real by | Exit run entry |
|---|---|---|---|---|
| `meet[:<point>]` | `MeetStep.cs` | plays: waits for the partner's name, walks to within `pairReachCm` (placeholder 400) of it, arrives at the point (`meet` without an argument) and is done on `Met`; failed unpaired, when the partner gave up or left, or after `pairWaitMs`; counters `meet.met`, `.gaveUp`, `.gone`, `.late`, `.noPartner` | WP-M4-12 | `meet:start`, `meet:trade` (both: a leg whose step needs the partner in sight meets it first) |
| `serve` | `ServeStep.cs` | plays: the QA partner bot's standing step (above); `serve.scripts`, `serve.unknown`, `serve.partnerGone` | WP-M4-12 | (the QA smoke) |
| `party:invite`, `party:accept`, `party:leave`, `party:chat` | `PartyStep.cs` | plays (below) | WP-M4-2 | `party:invite` (A), `party:accept` (B) |
| `whisper` | `WhisperStep.cs` | plays (below) | WP-M4-2 | (`m4-loop`, `ci-pair`) |
| `trade:<give>:<for>` | `TradeStep.cs` | plays (WP-M4-4): each side `<ItemDef id>[x<count>]`, `gold=<n>` or `nothing` (`TradeStep.TryParse`; a bad one throws at the run's start); the first of the pair walks to the partner (`context.TryPartnerEntity`) and invites, both offer, lock and confirm; done on the committed result; "not available: partner needed" while the partner is not in sight | WP-M4-4 | `trade:m1.demonAsh:gold=10` (A), `trade:gold=10:m1.demonAsh` (B) |
| `mail:claim` | `MailStep.cs` | plays (WP-M4-3; at the city's mailbox since WP-M4-14): walks to the mailbox, claims every open mail into the bag, answered after the commit; done when none is left or the bag is full; no mail is `mail.none`, failed in a script | WP-M4-3, WP-M4-14 | `mail:claim` (A twice, B once) |
| `pickup:roll` | `PickupStep.cs`, the `roll` branch | plays (WP-M4-6): loots like `pickup`; done once a party roll part (`LootRollNotice`, sent after the winner's save) reached the bot since its last done (the fight before it loots too); outside a party failed "no party" in a script (`pickup.noParty`), no roll by the idle time `pickup.noRoll` (failed "no roll seen" in a script); counters `pickup.rolls`, `pickup.rollUnitsWon`, `pickup.rollUnitsMailed`; scenarios `m4-party-loot` and `m4-party-loot-full` (bags seeded full: winnings by mail) | WP-M4-6 | `pickup:roll` (both) |
| `list:<item>:<price>`, `list:cancel:<item>` | `ListStep.cs` | not yet | WP-M4-7 | `list:m1.demonAsh:15` (B); `list:m1.demonAsh:999`, `list:cancel:m1.demonAsh` (A) |
| `buy:listing:<item>` | `BuyStep.cs`, the `listing:` branch | not yet | WP-M4-7 | `buy:listing:m1.demonAsh` (A) |
| `stall:open:<item>:<price>`, `stall:close` | `StallStep.cs` | not yet | WP-M4-8 | `stall:open:m1.demonAsh:20` (A) |
| `buy:stall` | `BuyStep.cs`, the `stall` branch | not yet | WP-M4-8 | `buy:stall` (B) |
| `die[:<camp>[:dev]]`, `recoverBag[:expire]`, `recoverBag:partner`, `sell:stolen` | `DieStep.cs`, `RecoverBagStep.cs`, `SellStep.cs` | real | WP-M4-9 | A: `die:field1.spawn.spitter.a1` then `recoverBag`; `meet:bag`, `die:...` (B: `meet:bag`, `recoverBag:partner`, `sell:stolen`); a third `die:...` then `recoverBag:expire`. `die` stands at the camp until killed (`:dev` sends `/die` there), done on the new own bag (posted as `bag.at` for the partner); `recoverBag` takes all back; `:partner` waits out the owner window and takes with the stolen mark; `:expire` waits until the bag is gone. A bag dropped before `die` (a death in a fight or at a meeting point) is taken up when it lies at the camp, else taken back first. Scenario `m4-bags` plays the three bags alone (`Run-BotLoad.ps1 -Scenario m4-bags`) |
| `contract:propose:<mold or recipe>`, `contract:craft` | `ContractStep.cs` | not yet | WP-M4-10 | `contract:propose:m3.moldSword` (B), `contract:craft` (A) |

- The rows: `ci-pair-a/-b` (CI: `meet:start`, `party`, `whisper`, `trade`, `fight:1`, `meet:end`), `m4-loop-a/-b` (the loop),
  `m4-exit-a/-b` (the exit scripts), `qa-partner`, `qa-partner-join`. A WP may change its own entries' arguments with the
  orchestrator (the exit run's item, prices, camps), never the step names.
- The exit scripts' expected outcomes per entry are in `tests/GameServer.Tests/Bots/BotPairTests.An_M4_exit_script_...`:
  when a step stops being a placeholder, update that test (and `An_M4_placeholder_is_not_yet_naming_its_WP`).
## The M4 party steps (WP-M4-2)
A pair's steps (spec 6.5 "M4 party", 9.12): they read ClientCore's `ClientParty` (`context.Party`: the committed
`PartyState`, the invite waiting, the last `PartyResult`) and act on the server's messages only. The partner is read in one
place, `PairPartner.Name(context)`: `context.Pair?.PartnerName` (WP-M4-12's pair board; null while the bot plays alone, so
"not available: partner needed").

| Step | File | Plays | Counters |
|---|---|---|---|
| `party:invite` | `PartyStep.cs` | invites the partner by its display name once; done when the committed `PartyState` lists the partner; failed with `party.refused.<reason>` | `party.invited`, `party.joined` |
| `party:accept` | `PartyStep.cs` | waits for a `PartyInvite` whose inviter is the partner (other invites are ignored), accepts it; done on the same `PartyState`; failed with `party.refused.<reason>` | `party.joined` |
| `party:leave` | `PartyStep.cs` | leaves the party; done once the state shows none (at once outside a party) | `party.left` |
| `party:chat` | `PartyStep.cs` | sends a party line; done on its echo; a party line of another member counts while it runs; failed outside a party | `party.chatSent`, `party.chatReceived` |
| `whisper` | `WhisperStep.cs` | whispers one line to the partner by display name; done on the echo naming the partner; failed with `whisper.refused.<reason>` | `whisper.sent`, `whisper.received` |

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
