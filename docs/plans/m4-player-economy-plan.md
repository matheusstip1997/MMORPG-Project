# Plan: Milestone 4, player economy (handover to the M4 orchestrator)

Prepared 2026-11-09 by the M3 orchestrator at the user's request ("Go on to M4 handover"), when M3 was functionally
complete (main at the merge of PR #162, 7ed0ecfe; the final `m3-exit` run on that commit is recorded in
`docs/plans/m3-jobs-crafting-plan.md` section 3a). It is written for a fresh orchestrator session. Read it together
with:
- `CLAUDE.md`;
- `docs/spec/00-index.md`;
- `docs/spec/04-items-inventory-economy.md` (4.2 fees, 4.3 loot bags and party loot, 4.5 trade, auction and shops);
- `docs/spec/05-jobs-crafting-contracts-quests.md` (5.4 contracts and portfolio);
- `docs/spec/06-pk-law-guilds-arena-raids-social.md` (6.5 party and chat);
- `docs/spec/07-operations-platform.md` (7.1 save classes, 7.2 economy telemetry, 7.4 the admin panel);
- `docs/spec/12-delivery-plan-milestones.md`;
- the memory files.

Then start with the handover checklist at the end. Everything here follows the spec. Where the spec says **Open**,
this plan lists the question for the user with a recommendation and never answers it. The plan decides nothing.

## 1. Goal and exit criteria (spec 12.2)
**M4: player economy.** Party, trade, auction, shops, loot bags and party loot, contracts.

**Exit:** no duplication in concurrency tests; the economy dashboard shows faucet and sink totals.

Proposed acceptance checks, to confirm with the user in the first session:
- **The exit run.** Two fresh characters go through the loop on main, as scripted bots (`m4-exit`) and as a human
  with a partner bot (the QA smoke):
  1. A invites B; they form a party. A kill in the field drops loot; the pickup rolls among the two and the winner's
     bag (or mailbox, when full) holds it; the chat line names the winner.
  2. A trades the item to B against gold in the trade window: one transaction, both bags and both gold balances
     change together, the ledger shows a move and no faucet.
  3. B lists the item at the auction house; A searches, finds and buys it. The listing fee and the sale tax are sinks
     through `FeeService`; B receives the proceeds minus the tax by mail and claims them.
  4. A opens a shop stall in the city with the item; B buys it from the stall.
  5. A dies in the field zone: the Normal bag becomes a persisted loot bag that survives a server restart; A recovers
     it within the owner window. A second bag left alone expires and its items are destroyed (a sink).
  6. B contracts A to forge a sword from B's ore and gold. A runs the forge session owned by the contract at the city
     forge; the sword goes to B, the gold minus the contract fee to A; A's portfolio counts the contract.
  7. A mail with an item attached (a returned, unsold listing) is claimed into the bag.
- **Transaction tests.** Every value-moving operation added in M4 has the four integration tests of rule 5: double
  submit, retry, crash in the middle and concurrent access. The operations are:
  - the trade (two characters in one transaction);
  - listing create, buy, cancel and expiry (auction and stall);
  - mail claim and mail expiry;
  - the death bag (create, pickup by the owner, pickup by another player, expiry);
  - the party loot roll (a pickup that hands the drop to another member);
  - contract propose, accept, complete, cancel and expiry;
  - the fee (quote and apply inside the caller's transaction).

  Each is answered only after its save committed.
- **No duplication** (the spec's exit): a concurrency suite, run in CI, where two buyers take one listing, a listing is
  bought while cancelled, a trade is confirmed while one item is moved or sold, a mail is claimed twice, two players
  take one loot bag, a contract is completed while cancelled, and a crash falls between escrow and settlement; after
  each, the sum of every item and gold over both characters, the escrow and the mailboxes is unchanged, and the
  `gold_ledger` flows sum to zero apart from faucet and sink rows (gold conservation, R-4.5-02).
- **Unit tests.** Every pure function has them: the fee split, the party roll weights and the loot debt decay, the
  listing fee and tax, the contract cancel cost, the bag owner window and expiry, the mail expiry. The seed of every
  party roll is logged (rule 6).
- **Economy.**
  - `FeeService` is the only fee computer; every fee is a `gold_ledger` sink row with its reason, and the destroyed
    share counts in `EconomyLedger` and the telemetry.
  - M4 adds **no gold faucet**: every channel moves gold (R-4.5-02); the outpost sale stays the only source.
  - The economy simulator gains fees as a sink (a trade and listing volume per player-hour, placeholders) and
    `--check` still passes in CI; the sink cover line counts fees.
  - **The economy dashboard** (the spec's exit): a page of the admin panel shows faucet and sink totals per hour and by
    reason, and the gold in circulation, from a bot run's data.
- **Smoke and load.**
  - The QA smoke harness grows a step per feature (party up, trade, list and buy, stall, die and recover, contract,
    claim mail), with a partner bot as the second player.
  - The bots play the M4 loop in pairs.
  - The 50-bot zone run with stalls and loot bags loaded still meets the tick budget, and an auction search over a
    seeded table of listings stays within a query budget set in the plan of its WP.

## 2. Roles and process (carried over from M3; see `docs/plans/m3-jobs-crafting-plan.md` section 2)
- **Orchestrator.** It splits the work into packages (WPs), briefs sessions, reviews PRs and relays interface changes
  between parallel packages. It dispatches QA findings to fixers without waiting and keeps this plan's section 3a
  current. **The orchestrator merges every PR** after its review and green CI (user decision 2026-11-02, memory
  `auto-merge-all-prs`); the user is told in the report. Review at high effort with the code-review skill and the
  architecture rules of `CLAUDE.md`.
- **WP sessions.**
  - One session per WP, each with its own worktree and branch.
  - **Plan mode first:** the session writes a file-level plan and waits for the user's approval before coding.
  - Model Opus 5.5 at high effort for every session the orchestrator starts.
  - A WP that would touch more than one session's worth of work ships in two PRs, as WP-M2-5 and WP-M3-15 did.
- **Fixer and QA.** Their role files are `.claude/agents/fixer.md` and `.claude/agents/qa.md`. They run as visible
  sessions, never as background subagents. QA findings go to a fixer automatically; design calls go to the user.
- **Temporary design sessions** may edit only `docs/design/**`. They report decisions to the orchestrator, who writes
  them into the spec and the decision log.
- **Balance is deferred to the end of the project** (user decision 2026-10-30). Every number in M4 is a placeholder
  row in a config table, and **no WP tunes**. A WP that needs a number to make a test pass sets a plausible
  placeholder and says so.
- **Standing rules** (memory files, M2 and M3):
  - Everything is written in English.
  - Branches with a new migration use their own databases (`DbMigrate --dev --db-suffix <x>`,
    `Run-QaSmoke.ps1 -DbSuffix <x>`), never the shared `mmorpg_realm_1`.
  - Sessions may stop the user's composed play stack for a smoke run with exactly
    `docker compose stop authapi gameserver adminpanel` in `C:\Users\Matheus\MMORPG-Project`, telling the
    orchestrator first, and must restore it with exactly `docker compose up -d --wait`. After a merge that changes
    server code or a table schema or rows, the orchestrator rebuilds the stack without asking: port precheck (a
    non-Docker holder of udp 7777 or tcp 5080 or 5090 aborts with "PORTS HELD BY"), `git pull --ff-only origin main`,
    `docker compose up -d --build --wait`. Docs-only and Unity-only merges need no rebuild.
  - Run the Unity EditMode suite before a PR that changes Unity code or table rows Unity reads, from a short worktree
    path, never in the play checkout; `git lfs pull` in a fresh worktree.
  - Never touch `infra/`, secrets, AWS or Steam; the dev login `dev:<n>` only.
  - Commits end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` and PR bodies with
    `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- **Working agreement.**
  - Whoever merges later takes protocol main+1 and the next migration number, and rebases (section 7).
  - A PR that changes a table schema also updates the validator and the spec.
  - Placeholders (art, numbers) are data, so a swap never touches code.
  - Every refusal has a reason code and a `ReasonCodeStrings` row.
  - New events go into spec 8.1, transactions into 8.2, tables into 8.5.
  - The user's play sessions drive a findings loop (finding, then a fixer or a small WP, then a PR).
  - A known CI flake is rerun once (`gh run rerun <id> --failed`) and handed to the next fixer round.

## 3a. Where we are (kept by the M4 orchestrator)
_One dated entry per milestone event: decisions answered, waves briefed, PRs merged in order with their protocol and
migration numbers, QA results, carry-overs._

- **2026-11-09:** M4 started. Main at the merge of PR #163 (this plan), 61326747: protocol 40, highest reliable id 55,
  realm schema V0015, telemetry kind 103, 141 tables, no open PRs; CI green on the #163 run (build-and-test, bot-smoke,
  compose-stack) and on every merge back to #160 (the #159 run failed on the chain-hit flake fixer round 3 took).
  Verified in the orchestrator's worktree on origin/main: `dotnet build` 0 warnings, `dotnet test Mmorpg.sln` 4,279
  tests green (Shared 2,336, GameServer 1,829, AuthApi 84, AdminPanel 30; the integration tests against the
  docker-compose PostgreSQL and Redis). The play stack in `C:\Users\Matheus\MMORPG-Project` is at 61326747 with all
  five containers healthy; it runs the 8ac7c567 image, and #162 and #163 are docs only, so no rebuild. The QA smoke
  of checklist step 2 runs in the session `qa-m4-start` (report `qa-m4-start.md` and its evidence folder in the M3
  orchestrator's notes folder `local_f4cc6e62-1584-4003-928c-e3e8e9fb3f51`, section 9 step 10): DataValidator OK (141
  tables, 2,789 rows; the world 880 x 800 cells, 6 regions, 114 markers); the smoke's result goes into the next entry.
  Balance stays deferred to the end of the project (user decision 2026-10-30), to be confirmed with the section 1
  checks. The orchestrator merges every PR after review and green CI (memory `auto-merge-all-prs`). Next: the section 1
  acceptance checks and decisions 1 to 5 of section 5 to the user in one message, their spec and decision-log PR, then
  the wave 0 briefs (WP-M4-1, -2, -3, -4, -5 and the WP-M4-12 skeleton) in plan mode.
- **2026-11-09 (acceptance checks confirmed, decisions 1 to 5):** docs #164 merged. The user confirmed the section 1
  acceptance checks and that balance stays deferred (every M4 number a placeholder, no WP tunes), and approved
  decisions 1 to 5 as recommended, recorded in this docs PR: spec 6.5 (the party rules, exp share, runs, immunity
  and the party and whisper channels), 3.6 and 5.5 (the exp and run pointers), 4.2 (the three fee types and the
  Merchant hook), 4.3 (the party roll and the debt), 4.5 (the mail model and the trade window) and the decision log.
  The QA smoke of main runs on in `qa-m4-start`; its result goes into the next entry. Next: the wave 0 briefs
  (WP-M4-1 fees, -2 party, -3 mail, -4 escrow and trade, -5 economy snapshots and the dashboard data, and the
  WP-M4-12 skeleton) as six plan-mode sessions, Opus 5.5 high, own worktrees, proposed protocol 41 to 43 and realm
  V0016 to V0018 in that order, the real numbers at merge.
- **2026-11-09 (wave 0 briefed, the start smoke passed, WP-M4-1 merged):** docs #165 merged (a153baa3). Its push run
  failed on two flakes, `DevCommandParserTests.A_failed_lookup_is_tried_again` and
  `TelemetryBridgeTests.Bus_publish_dispatch_and_telemetry_enqueue_allocate_nothing_in_steady_state` (5,840 bytes
  allocated), while the pull request run of the same commit passed; green on one rerun, both go to the first M4 fixer
  round.
  - **QA smoke of main 61326747: pass, 2 of 2 runs, no findings** (`qa-m4-start.md` and its evidence folder in the
    notes folder): the level 1 warrior 21 pass, 12 n/a, 0 fail; the level 12 warrior with `-SeedStack` 32 pass, 1 n/a,
    0 fail, every M3 crafting step; 0 error lines in the server logs, 0 security or save telemetry records.
  - **Environment:** Docker Desktop died on its own twice today (12:57 local during the QA run, and again around
    14:00), with no error in its logs; restarted both times and the play stack restored with
    `docker compose up -d --wait`. The cause is unknown; it also happened twice in M3.
  - **Wave 0 briefed:** six plan-mode sessions on Opus 5.5 high, own worktrees: `wp-m4-1` fees, `wp-m4-2` party and
    chat channels, `wp-m4-3` mail, `wp-m4-4` escrow and trade, `wp-m4-5` economy snapshots and the dashboard data,
    `wp-m4-12` the bot pair skeleton. Proposed numbers, final at merge: -2 protocol 41, reliable ids 56 to 59,
    telemetry 105 (`PartyChanged`); -3 protocol 42, ids 60 to 65, realm V0016, `InventoryReason` 21 and 22; -4
    protocol 43, ids 66 to 68, V0017, reasons 23 to 25; -5 V0018. WP-M4-2 also brings the table `ChatChannelDef`
    (`ChatSettings` v2 loses its rate fields).
  - **The pair contract (WP-M4-12 owns it; -2, -3 and -4 write their steps against it):** `BotScenario.pairs` (bot 2k
    with 2k+1), `context.Pair` (`Arrive`/`Check` meeting points, `Post`/`TryRead`), `context.TryPartnerEntity`,
    `context.ApproachPartner`; steps `party:invite|accept|leave|chat`, `whisper`, `trade:<give>:<for>`, `mail:claim`,
    real now `meet` and `serve`, the rest "not yet, WP-M4-n"; game outcomes only from server messages after the
    commit; smoke steps `partner-join`, `party-up`, `party-chat`, `trade-item`, `claim-mail` with `qa-partner-<step>`
    rows; the `m4-exit` seed level 25 is a placeholder (`TODO(open)`, decision 14).
  - **Merged: #166 WP-M4-1** (69e0348f): `FeeService` (the only fee computer; quote, then the fee as one more gold
    entry of the payer's batch, key `{opKey}:fee`, `FeeCharged` after the commit), `FeeRates`, shared `FeeMath`, the
    simulator's fee sinks (rows `m4-15`, `m4-300`). **`InventoryReason.Fee = 20`, telemetry 104 `FeeCharged`**, no
    protocol, no migration, 142 tables, 4,351 tests. `InventoryService.OpCommitted` is multicast from now on (`+=`),
    and `EconomyLedger.ReasonCount` comes from the enum (GemSocket to GemRemove were not counted since WP-M3-9).
    Reviewed at high effort, no blocker. Notes for the WP-M4-7 brief and the first fixer round: an applied fee has no
    undo (callers apply after every check); `_awaitingCommit` keeps the quote of a faulted or dropped character;
    `FeeCharged` is published before `CommitGold`; an owner share throws (M8); a world without a complete `FeeRates`
    has `Fees` null; the simulator takes the sale tax from the seller while the hand-over note proposes the buyer's
    batch with `price - tax` mailed (WP-M4-7 settles it with WP-M4-3 and -4). Open for the user: a minimum fee and
    the Merchant job (`TODO(open)`). The play stack was rebuilt on 69e0348f.
  - Next: review and merge WP-M4-2, -3, -4, -5 and -12 as they arrive, the QA of main after wave 0 and its fixer
    round, the user's play test, then decisions 6 to 9 and the wave 1 briefs.
- **2026-11-09 (WP-M4-5 merged, wave 0 numbers final, #169 to #171 in review):** docs #167 merged (152e4b6a).
  - **Merged: #168 WP-M4-5** (869b23d5): `Economy/EconomySnapshots` (after a demand control step, at most every
    `EconomySettings.snapshotIntervalMs`, queued only) and `Persistence/EconomySnapshotWorker` (the
    `faucet_sink_snapshots` rows off the tick thread, a last row at stop), `EconomyRepository.HourlyTotalsAsync`
    (faucet and sink per UTC hour and reason from `gold_ledger`; moves never count), the admin panel's
    `GET /api/economy?hours=<n>` and its plain Economy tab (every read an `admin_actions` row `view_economy`),
    `GoldChanged` telemetry with `flow` (`EconomyFlows.Of`; the ledger counts created gold only for faucet flows).
    **Realm V0018** `faucet_sink_snapshots`; its `admin_actions` action check lists `view_economy` and WP-M4-3's
    `send_mail` (V0016 lists both too, so either apply order ends the same). No protocol, reason, telemetry kind or
    table; 4,370 tests, 142 tables. Reviewed at high effort: the check, the final snapshot's time, the worker's
    logging and the gold sum only when due were fixed; left for the first fixer round: a permanently rejected row
    blocks the snapshot queue, the unbounded channel and the shutdown wait, `EconomyView`'s silent 500-row cut, the
    snapshot list allocated on the tick thread. The page's final shape waits for decision 13. The play stack was
    rebuilt on 869b23d5, five containers healthy.
  - **Wave 0 numbers, final (fixed per PR whatever the merge order; the gaps until all are in are fine, the migration
    runner applies unapplied versions in ascending order):** WP-M4-2 (#169) protocol 41, reliable ids 56 to 59,
    telemetry 105 `PartyChanged`, no migration; WP-M4-3 (#171) protocol 42, ids 60 to 65 (`MailOpenRequest`,
    `MailList`, `MailContent`, `MailClaimRequest`, `MailResult`, `MailCount`), realm V0016, `InventoryReason`
    `MailClaim = 21` and `MailExpired = 22`, telemetry 106 `MailSent`, 107 `MailClaimed`, 108 `MailExpired`; WP-M4-4
    (#170) protocol 43, ids 66 to 68, realm V0017, reasons `Trade = 23`, `EscrowHold = 24`, `EscrowRelease = 25`,
    telemetry 109 `TradeCompleted`, 110 `TradeCancelled`.
  - **In review (each at high effort, findings sent to its session):**
    - **#169 WP-M4-2 part A** (party core and chat channels): 9 findings. Fixed and verified on 4367de15: an invite
      sent before the party formed is accepted, entity ids instead of character ids on the wire (rule 8), the name
      lookup over the peer map. Left with a `TODO` or for part B and the fixer round: the visibility check on the
      name lookup (`TODO(M5, open)`), the invite slot a stranger can hold, the leader-left cause, the silent refused
      accept, the kill ledger surviving a leash reset or an id reuse, small allocations. Waits for its merge of main
      (two spec files) and a green run.
    - **#170 WP-M4-4 part 1** (the two-character transaction, escrow, trade): 10 findings. Required: a settling
      trade dropped when a side detaches, the invite by entity id skipping the interest check, `Settle` throwing
      after the in-memory swap. The fixes and the merge of main are committed in its worktree, not pushed yet.
    - **#171 WP-M4-3 part A** (system mail, admin compensation, the bot step): 9 findings. Required: a refused or
      failed mailbox reload is never retried, an expired admin compensation counts as a sink with no faucet, the
      mail worker's failures are counted and never logged. Asked: a bounded reload queue with the stop token, an
      upper bound on compensation gold, the admin write's row count and its cap race. Needs a merge of main (five
      files).
    - **WP-M4-12 skeleton:** committed in its worktree (5f330b9b and a merge of main), no PR yet; asked to merge
      main again, run the suites and open it.
  - **For the wave 1 briefs:** the mail send contract (a carrier send's op key is never sent again once committed:
    a repeated key faults the carrier's save; the attachments leave their owner in the same operation). The second
    of #169 and #170 to merge unifies the player slash-command path; the second of #170 and #171 wires
    `EscrowService.MailReleaser` to the mail send.
  - **Open for the user, not urgent:** a minimum fee, the Merchant job discount, and whether a trade invite and the
    cancel cooldown may keep the target from trading (one player can lock another out).
  - Next: verify the three pushes and merge #169, #170, #171 and the skeleton in the order they go green (a stack
    rebuild after each), then the QA of main after wave 0, its fixer round, the user's play test, decisions 6 to 9
    and the wave 1 briefs.
- **2026-11-09 (WP-M4-2 part A merged, #170 and #171 verified and merging main in turn):** docs #172 merged
  (45ffa62d).
  - **WP-M4-2 part A merged:** PR #169 (ae6e0acd): `PartyService`, the party and whisper chat channels, protocol 41,
    reliable ids 56 to 59, telemetry 105 `PartyChanged`, no migration; 4,436 tests, 144 tables, EditMode 784. Review
    findings 1, 2 and 4 fixed; left for the first fixer round: the `KillContributions` ledger across a leash reset
    or despawn, the `PartyChanged` and detach allocations, a party request rate limit. Part B (started from main):
    the leader-left cause, a notice to the inviter when an accept fails, the pending invite sent again on reconnect,
    the Unity party UI. The play stack was rebuilt on ae6e0acd, five containers healthy.
  - **Main now:** protocol 41, highest reliable id 59, realm V0015 and V0018, telemetry up to 105, `InventoryReason`
    up to `Fee = 20`, `NpcInteraction` up to 7, 144 tables.
  - **#170 (WP-M4-4 part 1), verified at dde7c9e2, CI green:** fixed: a side dropped while the pair settles, the
    aborted save link (`SaveScheduler.AbortTransfer`), invites only to players in the inviter's interest, the
    forgotten transfer keys and cooldowns, escrow releases to mail one at a time and stopped at shutdown, an
    integration test of the pair. Left: the invite and cancel cooldown lock-out (`TODO(open)`), `/trade` still
    hooked in `GameWorld.DevConsole.HandleDevLines`, small allocations. It conflicts with main after #169 and is
    merging main; it merges next (protocol stays 43).
  - **#171 (WP-M4-3 part A), verified at d0cd8b46:** fixed: refused or failed mailbox reads are asked again
    (`MailService.Settled`, a stale mark), an expired admin compensation is no sink, the worker logs when it starts
    failing and when it recovers, queued reads are answered as failed at shutdown, a mail's gold is capped at
    `EconomySettings.maxGold`, the admin send checks its update's row count and takes an advisory lock for the cap.
    Deferred with a TODO: `MailArrived` across several servers (M9), the dedupe caps to `MailSettings` (fixer
    round). It merges after #170: one merge of main, `ProtocolVersion` left at 43, and it wires
    `EscrowService.MailReleaser` to the mail send (or hands that to the wave 1 brief if it is more than a day).
  - **WP-M4-12 skeleton:** still no PR; `m4-loop` passed locally, its `m4-exit` run had one entry of bot B stuck
    until the step limit and is being looked at. It needs main merged again once #170 and #171 are in (its
    `M4ReasonNames` check depends on the merged reasons).
  - **For the wave 1 briefs, added:** `EscrowService.IsReleased` is true for an id this world never knew; escrow
    ids are unique for ever (built with the world's run id); the dashboard leaves out expired admin compensations
    (`mail_ops` kind 3 rows of mails with `kind` 5); the player slash-command path is unified by WP-M4-2 part B if
    #170 is on main when it gets there, otherwise by WP-M4-4 part 2.
  - **Fixer round backlog, added:** an AdminPanel kick test that failed once under load
    (`Reusing_a_kick_request_id_for_another_character...`).
  - **Open for the user, not urgent, added:** whether one stranger's party invite may hold the invitee's only
    invite slot (WP-M4-2 suggests several pending invites, one per inviter, capped: a new rule, `TODO(open)`).
  - Next: merge #170 then #171 as each goes green after its merge of main (a stack rebuild after each), the
    skeleton PR, then the QA of main after wave 0, its fixer round, the user's play test, decisions 6 to 9 and the
    wave 1 briefs.
- **2026-11-09, sixth entry: WP-M4-4 part 1 merged (#170, e7d480bf), the skeleton PR #174 reviewed.**
  - **Merged:** #173 (this file, f074f0b0) and #170 (WP-M4-4 part 1, direct trade and the escrow, e7d480bf). The
    play stack was rebuilt on e7d480bf (five containers healthy).
  - **Main now:** protocol 43 (42 kept for mail in the history comment), highest reliable id 68 (56 to 59 party,
    60 to 65 kept for mail, 66 to 68 trade), realm V0015, V0017, V0018 (V0016 is mail's), telemetry 104
    `FeeCharged`, 105 `PartyChanged`, 109 `TradeCompleted`, 110 `TradeCancelled` (106 to 108 kept for mail),
    `InventoryReason` `Fee = 20`, `Trade = 23`, `EscrowHold = 24`, `EscrowRelease = 25` (21 and 22 are mail's),
    `NpcInteraction` up to 7.
  - **#171 (WP-M4-3 part A):** conflicts with main after #170; its session merges e7d480bf (keeps
    `ProtocolVersion.Current` 43 with its "42" entry, ids 60 to 65, V0016, reasons `MailClaim = 21`,
    `MailExpired = 22` in place of the "21"/"22" name placeholders, telemetry 106 to 108, and wires
    `EscrowService.MailReleaser` or hands it to wave 1). It merges next, once green.
  - **#174 (WP-M4-12 skeleton), reviewed at 7b3fd55c, CI green, conflicts with main:** the pair contract
    (`BotScenario.pairs`, `requireNoFailedSteps`, `BotBehaviour.pairWaitMs`/`pairReachCm`, `PairLink` and its
    boards, steps `meet`, `serve` and the M4 placeholders, scenarios `ci-pair`, `m4-loop`, `m4-exit`,
    `Run-M4Exit.ps1`, `Run-QaSmoke.ps1 -Partner`, `EconomyReportM4`, the CI step "No duplication (concurrency
    suite)"). Eight findings sent, six to fix before the merge: the meeting counters fall out of step for ever
    when one side's step ends before it arrives; a reused `-RunId` replays the old pair board; a timed-out meet
    gives up a meeting its partner saw met; a given-up meeting reads as met later; pairs with an odd behaviour
    list are not validated; the walk to the partner has no bound of its own. Its call: the file board's file open
    per read, eight identical placeholder steps. It merges after #171, after its merges of main (main's real
    `trade`, `party`, `whisper` steps kept), a full rerun on the merged head and its `-Partner` smoke result.
  - **In flight:** WP-M4-2 part B (from main; moves `/trade` onto the player slash-command path), WP-M4-4 part 2
    (the Unity trade window, plan first).
  - Next: merge #171 then #174 (a stack rebuild after each), parts B and 2, then the QA of main after wave 0, its
    fixer round, the user's play test, decisions 6 to 9 and the wave 1 briefs.
- **2026-11-09, seventh entry: WP-M4-3 part A merged (#171, 18ceca36); #176 and #177 reviewed; #174 fixes verified.**
  - **Merged:** #175 (this file, 2af32f74) and #171 (WP-M4-3 part A: system mail on the server, admin compensation,
    the bot step, the escrow's release to mail through `EscrowMailSink`; 18ceca36). Unity EditMode on its head: 784
    passed, 2 skipped, 0 failed. The play stack is rebuilt on 18ceca36 (it applies realm V0016).
  - **Main now:** protocol 43, reliable ids up to 68 with no gap (56 to 59 party, 60 to 65 mail, 66 to 68 trade),
    realm V0015 to V0018, telemetry up to 110 with no gap (106 `MailSent`, 107 `MailClaimed`, 108 `MailExpired`),
    `InventoryReason` up to `EscrowRelease = 25` with no gap (`MailClaim = 21`, `MailExpired = 22`),
    `NpcInteraction` up to 7. `ClientReasonCode` 13 and 14 are #176's.
  - **#174 (WP-M4-12 skeleton), 7f4dadf6:** review fixes 1 to 7 verified (a meeting is reserved when its step
    begins, give-ups are per occurrence, a given-up meeting is its own state, the partner folder is cleared, pairs
    need an even behaviour list, the walk is bounded, the file board reads at most every 50 ms); 8 skipped by
    agreement. `ci-pair` plays the real party, whisper and trade. Open: its merge of 18ceca36, one `m4-exit` run
    and the `-Partner` smoke on the merged head.
  - **#176 (WP-M4-2 part B: chat tabs, party frame, invite toast, one player command path), reviewed at f70ff37b,
    8 findings:** a plain line on the Whisper tab goes to whoever whispered last; one history list for all tabs;
    the usage line leaves `/trade` out; an own echo marks a tab unread; the frame's pointer counter can stick;
    `FindInView` sits in a class #174 moves. The server's `/trade` hook is gone: the client's `ChatCommandParser`
    reads player commands and sends requests, every `/` line that reaches the server is the dev console's.
  - **#177 (WP-M4-4 part 2: the trade window and its invite prompt), reviewed at 69b9eeff, 9 findings:** digits
    typed into the gold field are also gameplay keys (a skill starts combat and the trade is cancelled); the offer
    rows hide rarity, quality and gems; Lock ignores gold typed but not committed; `ITradeRequests` and a second
    `ClientTrade` clash with #176 (the window's shape is the keeper); `trade_invite` sits on `party_invite`'s
    place; a drop always offers the whole stack.
  - **For the wave 1 briefs, added:** an escrow release to mail must fit one mail, checked at the hold: at most
    `attachmentsPerMail` (8) item rows, gold up to `EconomySettings.maxGold`, a known `MailKind`, an op key of at
    most 200 characters. A death loot bag (WP-M4-9, up to 30 slots) needs one escrow per 8 rows or another split.
  - **Fixer round backlog, added:** `SessionsEndToEndTests.A_lock_left_by_a_crashed_server_refuses_the_login_
    until_its_ttl_runs_out` failed once in CI (a 15 s wait); four analyzer warnings in #170's tests
    (`TradeServiceTests.cs:130`, `TransferPersistenceTests.cs:213-215`); `TradeService.InviteByName` is unused
    after #176.
  - **In flight:** WP-M4-3 part B (the Unity mail window, plan first). Next: merge #174, #176 and #177 as each is
    ready (one merge of main each, a stack rebuild after #174), then the QA of main after wave 0, its fixer round,
    the user's play test, decisions 6 to 9 and the wave 1 briefs.
- **2026-11-10, eighth entry: #177, #176 and #174 merged; wave 0 is on main except the mail window.**
  - **Merged, in order:** #178 (this file), #177 (WP-M4-4 part 2, the trade window and its invite prompt, a40d398f),
    #176 (WP-M4-2 part B: chat tabs, party frame, invite toast, the client's `ChatCommandParser` as the one player
    command path, c9442416), #174 (WP-M4-12 skeleton, 31371232). Also the designer's docs-only PRs #179 to #181
    (`docs/design/{art-direction,biome-sheets,city-layouts,open-world-content}.md`); their spec update is its own
    session and PR. The play stack is rebuilt on 31371232.
  - **Numbers:** unchanged by these three (protocol 43, ids up to 68, realm V0018, telemetry up to 110,
    `InventoryReason` up to 25, `NpcInteraction` up to 7); `ClientReasonCode` 13 `ChatCommandUsage` and 14
    `ChatNoWhisperTarget` are taken.
  - **Reviews closed:** #176: 7 of 8 fixed, the name lookup settled at #174's merge (`EntityIdentities.FindInView`
    and `TryFind` in `Entities/Core/EntityIdentities.cs`, the lowest entity id wins). #177: 8 of 9 fixed (gameplay
    keys off while the gold field has focus through `WorldClientFeed.WindowTextInput`, offer rows show rarity,
    quality and gems, Lock waits for the typed gold); left: offering part of a stack (new UI). The server's
    `/trade` hook and `TradeService.InviteByName` are gone.
  - **#174 evidence:** the `-Partner` QA smoke passed (173 s, `partner-join` passed, the party, trade and mail smoke
    steps still "not yet"); `ci-pair` passes with the real party, whisper and trade (p99 8.0 ms). `m4-exit` run 2
    (2 bots, 60 min): 13 entries done, 15 not yet, 3 failed (`mail:claim`, no mail until WP-M4-7 and WP-M4-8 mail
    the bots), 1 timed out, 6 not played; tick p99 0.36 ms. Every pair leg now starts with its own
    `meet:<point>`.
  - **In flight:** WP-M4-3 part B (the Unity mail window, the M badge, the `claim-mail` smoke step; client half
    built, merging 31371232); the spec update from the design docs.
  - **Fixer round backlog, added:** allocation tests that fail under parallel load, a different one in about every
    second CI run (`EventBusTests.Publish_and_dispatch_allocate_nothing_in_steady_state`,
    `HotMessageAllocationTests`, `ProjectileWorldTests.Ticks_that_fire_fly_and_send_arrows_do_not_allocate`,
    `SweptCircleTests.Hits_does_not_allocate`, with the older `TelemetryBridgeTests` one) and
    `TableReloaderTests.The_watcher_reloads_once_after_a_burst_of_writes`; the archer bot's `fight:6@<camp>` that
    never counts six kills at a single-demon camp (notes `wp-m4-12-skeleton.md`; WP-M4-6's roll leg needs it);
    `TryFind` compares Ordinal while `FindInView` ignores case.
  - **For the wave 2 decisions:** the design docs ask whether mail and the auction need a place in the city.
  - Next: the mail window PR, then the QA of main after wave 0, its fixer round, the user's play test, decisions 6
    to 9 and the wave 1 briefs.
- **2026-11-10, ninth entry: the mail window merged (#184, 67f896d0); wave 0 is complete on main.**
  - **Merged:** #183 (this file), #182 (the spec update from the designer's art and world decisions of 2026-11-09:
    new 9.14, the world authoring rules in 11.6, 12.9, R-2.3-07 to R-2.3-13 and others, four decision-log rows),
    #184 (WP-M4-3 part B: the mail window on M, the count badge on its shortcut button, the `claim-mail` smoke
    step with its seed `tools/qa/Send-DevMail.ps1` and `DbMigrate --mail-tag`). The play stack is rebuilt on
    67f896d0.
  - **#184 review, 6 findings, all fixed:** the detail is drawn from the mail's content (enchant, quality, gems,
    rarity, the stolen mark); one list reload per claim (the server allows 4 requests per 2 s, burst 8); a refused
    list is shown and asked once more; the window stays closed while a trade is open (a placeholder, no rule in
    the spec); the claim toast names `MailSettings.toastItemsShown` items; an open window redraws its expiry
    every `windowRefreshMs`. Numbers unchanged; no `ClientReasonCode` taken.
  - **Wave 0 on main:** WP-M4-1 fees (#166), WP-M4-2 party and chat (#169, #176), WP-M4-3 mail (#171, #184),
    WP-M4-4 trade and escrow (#170, #177), WP-M4-5 economy snapshots and the admin view (#168), the WP-M4-12
    skeleton (#174).
  - **Running:** the fixer for the flaky tests (allocation tests, the watcher burst, the session lock wait); the QA
    of main after wave 0 (report `qa-m4-wave0.md` in the notes folder) once the user starts it.
  - **Open for the user, from the design docs (not blocking M4):** the rest spot rules; the lawless city's
    services; the arena queue in medium cities; guards and the safe zone edge in small towns; `world-map.md`
    D1-D15 (2026-10-30) never carried into the spec, D5 said to conflict with 3.6 and 12.8.
  - Next: the QA findings to a fixer round with the backlog above, a stack rebuild, the user's play test, then
    decisions 6 to 9 and the wave 1 briefs (WP-M4-6 to -9).
- **2026-11-10, tenth entry: the QA of main after wave 0 passed (187a9150).**
  - **QA report** `qa-m4-wave0.md` (notes folder, evidence beside it): build 0 errors; `dotnet test` 4,666 passed,
    0 failed; `NoDuplication` 63 passed; DataValidator and EconomySimulator `--check` OK; migrations V0001 to
    V0018 on fresh databases; Unity EditMode 852 passed, 2 skipped; smoke A (level 1) 22 pass, 16 n/a; smoke B
    (level 12, `-SeedStack`, `-Partner`) 34 pass, 4 n/a, `partner-join` and `claim-mail` played; `ci-pair` passed
    3 of 3 and `m4-trade` passed; 0 server error lines; the gold ledger's moves sum to 0, every character's gold
    equals its ledger sum, item stacks rebuild from the audit log; the admin economy view matches the ledger.
  - **Findings:** F1, the smoke steps `party-up`, `party-chat` and `trade-item` are still placeholders, so the
    party frame, both invite prompts and the trade window were never on screen in a validation; F2, four xUnit
    analyzer warnings in the trade tests (fixed by #186).
  - **Fixer round, three pieces:** #186 (the flaky allocation, watcher, session-lock and dev-lookup tests; reviewed,
    four findings on the helper's JIT guard sent back, not merged yet); a session keeping the test suite under
    PostgreSQL's 100 connections (the likely cause of the AdminPanel 503); two sessions proposed to the user: the
    review backlog (`fixer-m4-wave0-backlog.md`: snapshots, party, trade, mail, client identities) and the real
    party and trade smoke steps with the archer bot's fight stall and the `bot-smoke` "19 of 20" failures.
  - **Design questions from QA, for the user:** D1 the mail list row does not show that items are attached; D2
    other players' name plates are hard to read; D3 the bot economy report prints `trade 0/0` for a run that
    traded (moves are neither in nor out); D4 the M badge is about 14 px.
  - **Environment:** the play stack was found stopped (PostgreSQL and Redis exited at 03:52Z, the three services
    stopped at 13:53Z, cause unknown); it is restored and healthy on 67f896d0. The WP-M4-12 session is archived.
  - Next: merge #186 after its fixes; the two fixer PRs; a stack rebuild; the user's play test of wave 0; then
    decisions 6 to 9 and the wave 1 briefs (WP-M4-6 to -9).
- **2026-11-10, eleventh entry: the test fixes and the art reference boards merged; main at 73a7fe89.**
  - **Merged, tests only (no stack rebuild):** #186 (cc89d32d: `tests/Common/AllocationMeter` decides an
    allocation test by the bytes of one prepared window; the watcher, session-lock and dev-lookup tests fixed; the
    four review findings fixed in d73e2a97) and #187 (a573ab1b: `tests/Common/Postgres/TestPostgres`, a pool of 10
    per test data source; one full run peaks at about 34 of PostgreSQL's 100 connections, was 59 to 75). Docs #188
    merged (6943db04). Main was green on all three jobs at cc89d32d. Operations follow-up, not M4: the services'
    own pool size is Npgsql's default of 100 per data source (spec 07).
  - **Art reference (a session the user asked for, on Fable 5.2 by the user's request):** five docs-only PRs
    merged in order, #189 (91a4e3e3, W1: five biomes), #190 (6123d0e0, C1 the water city), #191 (08a63912, W2:
    five biomes), #192 (9fe6e3ca, C2 the caravan bazaar city) and #193 (73a7fe89, W3: nine biomes): the files
    `docs/design/reference-board{s-w1,s-w2,s-w3,-c1,-c2}.{md,svg}`. Every colour, size and photo is a proposal;
    the files decide no Open item and change no rule, table or asset. Review: every cited rule exists and says
    what the boards claim; all 90 warning rows and 189 palette rows recomputed from the hex values with none off;
    all 187 photo links exist on Wikimedia Commons with the stated author and licence (links only, so
    `THIRD_PARTY_LICENSES.md` is unchanged); the drawings parse, hold no script or outside reference and were
    rendered and looked at. Three notes for the session's next PR, none blocking: the view size is cited as
    R-2.2-05 although the camera distance is a placeholder (the numbers are `art-direction.md`'s); the script
    behind the tables is not in the repository (the colour-vision figures were not recomputed in review); the 39
    questions for the designer have no pointer from spec 10.4.
  - **What the boards found, for the designer:** on eight of the 19 biomes' main ground the red and yellow
    warnings keep under half of the colour difference they have on today's M1 ground (sand, dry grass, mineral
    terraces and snow; Red sands is a start biome); roads in sand, gravel, ash and snow are close to the ground's
    colour (R-9.14-07); the water kit's slate roof is close to the water, and C2's clay walls, flat roofs, streets
    and sand are nearly one colour. Not covered yet: C3, the Ember caldera and M6, characters, monsters and the UI.
  - **Waiting on the user:** two proposed fixer sessions (the wave 0 review backlog; the real party and trade
    smoke steps with the archer fight stall and the `bot-smoke` "19 of 20" failures); a test mail on the play
    stack for the play test; the play test of wave 0 (party and trade need two clients); QA's D1 to D4; the
    boards' questions.
  - Next: the two fixer PRs and a stack rebuild after them; the play test; then decisions 6 to 9 and the wave 1
    briefs (WP-M4-6 to -9).
- **2026-11-10, twelfth entry: the art reference round; the designer's 34 answers; main at 6de793db.**
  - **Merged, all docs or tools, no game code or tables (no stack rebuild):** docs #194 (726aa522); #195 (f71f3551:
    the view size cited from `art-direction.md`, the exact method written into the W1 board); #196 (1859d05d: the
    C3 medieval city board); #197 (286438be: `docs/design/reference-boards-questions.md`, the boards' 50 questions
    as 34, each with the designer's answer of 2026-11-10); #198 (aba6ebbf: `tools/art-boards`, PowerShell 5.1
    scripts and inputs that rebuild every board; `Build-ArtBoards.ps1 -Check` compares the committed boards with
    what the inputs give); #199 (133a8f16: the answers applied to `city-layouts.md` as decisions E8 to E15 and to
    the boards); #200 (6de793db: the W4 board, the Ember caldera and M6). Open, reviewed, waiting for CI: #201
    (two W4 corrections) and #202 (the seven W4 questions as Q35 to Q41, open; an encoding fix in the photo search).
  - **Review:** the 34 recorded answers match the user's replies in the art session; `-Check` gives "same" for
    every board at each script commit; the warning, palette and colour-vision rows recomputed independently with
    none off; every photo link checked on Wikimedia Commons (links only, `THIRD_PARTY_LICENSES.md` unchanged).
  - **Not done, and it matters: the answers are not in the spec.** `city-layouts.md` and the boards now hold
    decisions the spec still lists as open (10.4: M2's kit, the look of fast travel, the review of the city plans
    and the biome sheets; R-11.6-05: the inn). The art reference session has a plan for one spec PR (the rules in
    2.3, 9.14 and 11.6, the open list of 10.4, one decision log row, `biome-sheets.md`) that waits for the user's
    approval. Until it merges, the design files are ahead of the spec on those points.
  - **For M4:** the designer chose **places in a main city for mail and the auction** (a mailbox and an auction
    house, E14). R-4.5-03 and 10.4 say the M4 plan decides, and the mail window on main is not tied to a place.
    This goes to the user with decisions 6 to 9, before the wave 1 briefs: it changes the auction package and
    may change how mail is opened.
  - **Client work the answers ask for, not planned yet:** warnings with a dark edge or a stronger fill on light
    ground (Q1), warnings drawn above air effects (Q8), a strength limit for flashes (Q7), a prototype city street
    to choose the fade's look (Q9). None is M4 scope; where each lands is a question for the user.
  - **For the user to decide:** whether `-Check` runs in CI and whether `CLAUDE.md` gets a line for
    `tools/art-boards`; the W4 questions Q35 to Q41; and still: two proposed fixer sessions, the wave 0 play test,
    QA's D1 to D4.
- **2026-11-10, thirteenth entry: the art answers are in the spec; the W4 questions answered; main at b7f7d9e8.**
  - **Merged, all docs or tools, no game code or tables (no stack rebuild):** #202 (d5252ee5: Q35 to Q41 in the
    question list, the contact sheet HTML-encodes its texts); docs #203 (4e468f75); #204 (88aa54d3: the answers of
    2026-11-10 in the spec, new R-2.3-14 and R-2.3-15, changes to R-2.3-07, R-2.3-09, R-2.3-11, R-9.14-03,
    R-9.14-04, R-9.14-07, R-11.6-04, R-11.6-05, 10.4 and one decision log row); #201 (99722b56: a W4 photo's
    author and the "no guards" wording); #205 (fb68ab6c: status texts in two design files); #207 (89e27fba:
    R-3.4-09 and R-14.2-08 follow the accepted sheets, approved by the user in the art session); and the stack
    #206, #208, #209 and #210, merged as one through #210 (50ced7f8). GitHub reported conflicts for that stack
    while the merges were clean locally; the orchestrator merged main into #210's branch (same tree, plain push)
    and merged after its CI was green again.
  - **The twelfth entry's "not in the spec yet" no longer holds:** the 34 answers are in the spec since #204.
  - **The W4 answers (Q35 to Q41, the user in the art session, 2026-11-10):** lava keeps its strength and stays
    thin lines and small pools; the lava's cooling crust moves away from the red warning (darker, browner); the
    Ember caldera's light is ambient plus the lava's glow; the crater is a rim nobody goes down; scrap walls get a
    dark band at the foot; the Ember caldera's sheet and the lawless city's layout are accepted, so all 20 biome
    sheets are (C15 to C18 in `biome-sheets.md`, E16 and E17 in `city-layouts.md`; R-2.3-14, R-2.3-15, R-9.14-03,
    R-9.14-04, R-3.4-09, R-14.2-08, a second decision log row). The lawless city's services stay Open.
  - **Still outside the spec, on purpose:** places in a main city for mail and the auction (E14), which goes to
    the user with decisions 6 to 9; and five answers that are client drawing work or wait for a prototype (Q1, Q7,
    Q8, Q9, and Q38: cover in the lawless city joins the fade prototype). None is M4 scope and none is planned.
  - **Stated on the W4 board, not decided:** the new crust colour is 4 (Delta E) from the walkable rock the lava
    lights, so the glow has to show where walking ends; the dark band is 4 from a bare rock street.
  - **The characters and monsters board, #211 (b7f7d9e8):** `docs/design/reference-board-figures.md` and `.svg`,
    built by `tools/art-boards/FigureBoard.ps1`; the user approved its plan in the art session. Sizes on screen
    from the placeholder `ClientCamera` values (a character is 104 px tall and 67 px wide at 1080p), the armour
    tier in the black shape, one outline against the 20 main grounds (no single outline colour is clear on all of
    them), figure tones and monster families against those grounds. Every colour, shape and size is a proposal,
    and it ends with **twelve questions for the designer**; the user is answering them in the art session (Q1 to
    Q8 answered when this was written), and the answers will come as PRs of their own. Review notes sent there,
    none blocking: the twelve questions are not in the question list or in 10.4; the "far edge" size row measures
    a figure that is mostly off screen; the camera values and monster radii are copied into the board's input and
    not read from the tables.
  - **Review:** every art PR was checked the same way (rule ids, the numbers recomputed, the Commons links,
    `Build-ArtBoards.ps1 -Check` on the PR commit). Open from the last review, sent to the art session: three
    status notes in the design files still say the W4 answers are not in the spec yet.
  - **CI:** `AttributePointsEndToEndTests.A_level_3_character_places_a_point_and_sees_its_stats_rise_after_the_commit`
    failed once on the docs-only #201 (expected 27, actual 37), green on one rerun; it is item 12 of the fixer
    backlog note.
  - **For the user to decide:** the rest of the twelve questions of the characters and monsters board (in the art
    session); and still: whether `-Check` runs in CI and a `CLAUDE.md` line for `tools/art-boards`, two proposed
    fixer sessions, the wave 0 play test, QA's D1 to D4.
  - Next: unchanged. The two fixer PRs and a stack rebuild after them; the play test; then decisions 6 to 9 with
    the mail and auction places, and the wave 1 briefs (WP-M4-6 to -9).
- **2026-11-10, fourteenth entry: the answers on characters and monsters; main at 39515d8e.**
  - **Merged, all docs, no stack rebuild:** docs #212 (0c2ba479); #213 (335d7ace: the three status notes the last
    review asked for); #214 and #215, merged as one through #215 (39515d8e).
  - **The user answered the twelve questions of the characters and monsters board** in the art session (Q42 to
    Q53 in `reference-boards-questions.md`, decisions A9 to A19 in `art-direction.md`): a two-tone outline (a dark
    line with a thin light rim) whose colour stays neutral; the armour tier reads from shoulder width plus
    headgear; a Mage and an Assist with a staff differ by staff shape and clothing; weapons about 1.5 times real
    size, heads and hands about 1.25 times; a height range per monster size class; a local variant with a family
    marking for a family in several biomes; a contrasting core for elementals; element effects keep their natural
    hues and warnings stay apart by form; the disguise robe hides the class and the armour tier; the art is tuned
    for 1080p. In the spec: R-9.14-03, R-9.14-05, R-9.14-06, R-2.2-08, R-14.2-08, R-6.2-01, 10.4 and a third
    decision log row of 2026-11-10. **Style references, not a rule:** Grand Chase Mobile, Tree of Savior, Ragnarok
    and Final Fantasy.
  - **Open after these answers (10.4):** the outline's two tones and widths; the height numbers; the colour of
    each element; what is seen of a robed figure by an observer who passes the Detect check or is in the wearer's
    party or guild, and whether the server keeps the class and the gear's look from the others; whether a local
    family variant is a monster type with its own Bystial card; which Final Fantasy, and how the references go
    with 6 heads.
  - **Client work the answers ask for, not planned, not M4 scope:** the two-tone outline (with Q1, Q7, Q8, Q9 and
    Q38 of the earlier entries).
  - **Review:** each recorded answer was compared with the user's choice in the art session's transcript. Open
    notes with the art session, all low: a guideline in `art-direction.md` that still calls the outline's colour a
    later decision, and four notes on the board itself from #211.
  - Next: unchanged.

## 3. Where M3 left things (main at PR #162, 7ed0ecfe, 2026-11-09)
**Done in M3:**
- jobs (Blacksmith and Jeweler, permanent picks at the job master, job XP with diminishing returns, job trees with a
  free respec);
- the item model v3: materials with tiers and tags, tools, molds, versioned instance properties, in-memory
  reservations;
- consumables (potions and food as a `useItem` action; toxicity and satiety);
- resource nodes, harvesting as a channelled action, node demand;
- the forge engine (refine, convert, mold crafts with quality, rarity and crafted stats; forge v2 free slots and
  material stat ranges), the Blacksmith's timing bar;
- enchanting, gems and the Jeweler's bench;
- HUD v3 (jobs, forge, enchant and gem windows, the quick item, node and station prompts), the dev console;
- the controls after play (the mouse aims, the lock aims at the enemy, ground skills keep the mouse; decisions 14-17);
- the economy simulator with the M3 sinks and the enchant and arbitrage Monte Carlo, the M3 economy report, the
  crafting bots and the `m3-loop` and `m3-exit` scenarios.

Protocol **40**, highest reliable message id **55** (`TargetLockChanged`), realm schema **V0015**, telemetry kinds up
to **103** (`LockFacingClamp`, retired and kept), **141 tables**.

**What M4 builds on (checked on 7ed0ecfe):**
- **Protocol.**
  - `src/Shared/Protocol/ProtocolVersion.cs` holds `Current = 40`.
  - Reliable ids are the `ReliableMessageId` enum in `src/Shared/Protocol/ActionMessages.cs`; the last are 54
    `DevCommandResult` and 55 `TargetLockChanged`.
  - A new message needs a layout in `ReliableMessageReader.Build` (R-2.5-07, `MemoryPackGuard`), and a client message
    also needs a `ReliableMessageCaps` row and a route in `src/GameServer/Net/InputRouter.cs`.
  - The snapshot entity kinds (`src/Shared/Protocol/Snapshot.cs`) already reserve **`LootBag = 5`** and
    **`ShopStall = 8`** (ground drops are `LootDrop = 9`); the client has no view for either.
  - **M4 starts at protocol 41 and reliable id 56.**
- **Persistence.**
  - Realm migrations V0001 to V0015 are in `src/GameServer/Persistence/Migrations/Realm`; **M4 starts at V0016**.
  - `CharacterRepository.SaveAsync` writes **one character** per transaction with its version check and its pending
    batches (`InventoryBatch`, `ProgressionBatch`, `BystialBatch`, `QuestBatch`, the `craft_ops`, `skill_ops`,
    `job_ops` and `attribute_ops` rows). `SaveScheduler` keeps one save in flight per character. **There is no
    transaction over two characters**: a trade, a sale to another player or a contract settlement needs one (section
    6, WP-M4-4 owns it).
  - Class 1 operations (outpost sale, vendor purchase) apply in memory at once, ask `RequestClass1Save`, and the save
    carries the pending class 2 batch (the ordering barrier by inclusion, spec 7.1). The player is answered after the
    commit (`InventoryService.IsCommitted`).
  - Value-moving single-player operations (skill tree, attribute points, job pick, forge, enchant, gems) save at once
    and answer after the commit.
  - Spec 7.1 names the tables M4 adds and nothing of them exists: `mail`, `escrow`, `loot_bags`, `listings`, `shops`,
    `contracts`, `portfolio_counters`, `portfolio_showcase`, `faucet_sink_snapshots`, `party_history`.
- **Items** (`src/GameServer/Items`):
  - `InventoryService` is the only writer of items and gold. It offers `TryAdd`, `TryRemove`, `TryMove` (with a split
    count), `TryEquip`/`TryUnequip`, `GrantStarterGear`, `TryAddGold`/`TryRemoveGold` (`GoldFits`, `Gold`,
    `CommittedGold`), `SpaceFor`, `Accepts`, `IsNonTradable`, `WasApplied`, `IsCommitted`, `RequestClass1Save`, and for
    saving `TakeBatch`/`Committed`.
  - **Reservations (WP-M3-2, `InventoryService.Reservations.cs`):** `Reserve(character, key, items)`, `Release`,
    `ReleaseAll`, `HasReservation`, `IsReserved`, `Unreserved`, `TryRemoveReserved(character, key, opKey, reason,
    source)` and `TaintsOutput`. They hold units **in memory within one character's bag**; nothing leaves the bag until
    the consuming operation. **There is no escrow**: no asset ever sits outside a character.
  - **Properties (WP-M3-8):** `TryChangeProperties(character, at, change, opKey, ...)` edits an instance's versioned
    `ItemProperties` as a pending change shown once committed; `TryGetLatestProperties` reads it.
  - `item_instances` rows carry `stolen`, `non_tradable` and `originalOwner` fields (V0004); the **stolen mark and
    the tradable flags are inert**: nothing sets `stolen`, and `IsNonTradable` is read only by the outpost sale and
    the tooltip ("Bound: cannot be traded"). `ItemDef` has a tradable flag for molds (spec 4.1 "molds are tradable,
    inert until trade lands").
  - `InventoryReason` (`src/GameServer/Events/ItemEvents.cs`) runs from None 0 to **GemRemove 19**; **M4 continues at
    20** (trade, listing, fee, mail, bag...). `EconomyLedger.ReasonCount` is `Enchant + 1` and must be widened to the
    enum's last value by the first WP that adds a reason.
  - Ground drops (`LootDrop`, `LootService` on `Killed`) are volatile and locked to the killer for
    `StartingValuesM1.partyLootLockMs` (30 s; "with no parties the party is the killer"), expiring after
    `bagExpiryMs` (15 min). A pickup that does not fit takes what fits; **mail overflow is M4**. Spec 8.5 names
    `PartyLootParams`, `OwnerWindows` and `BagExpiry` as the M4 tables of these numbers.
  - Dropping an item to the ground is refused on the client (`ItemDropUnavailable`, spec 9.13: "loot bags are M4").
- **Economy** (`src/GameServer/Economy`): `DemandService`, `PriceService`, `EconomyLedger` (gold created and destroyed
  and items destroyed by reason, from `GoldChanged`, `GoldDestroyed`, `ItemDestroyed`), `EconomyFlows`,
  `EconomyRules`. **There is no `FeeService` and no `SinkLedger`** (spec 4.2: "Fees are M4"). After each demand
  control step the world records `EconomyTotals` and `EconomyByReason` telemetry; `BotClient report --kind economy`
  prints faucet and sink totals per UTC hour and by reason, and `--json` writes them "for a later dashboard page".
- **Admin panel v0** (`src/AdminPanel`: `AdminEndpoints.cs`, `AdminPanelApp.cs`, `AdminPanelSettings.cs`,
  `Identity`, `Logs`, `Queries`, `wwwroot/{index.html,app.js,app.css}`): sign-in, character search and detail,
  kick, the action log, server logs and a telemetry view. **There is no economy page and no mail or compensation
  action** (spec 7.4 lists "economy and telemetry dashboards" and "send compensation by mail").
- **Outposts and NPCs** (`src/GameServer/Outposts`): every interaction goes through `OutpostInteractions` and an
  `IOutpostInteractionHandler` per `NpcInteraction` (`OutpostReason? Interact(Player, Npc, uint requestId)`, null
  means the handler answers later). `NpcInteraction` has Sell 1, Buy 2, RankUp 3, Quests 4, Jobs 5, Forge 6 and
  JewelerBench 7; **M4 continues at 8** (auction, contracts, stall). `NpcDef.json` has the outpost, the vendor, the
  job master, the forge and the bench; NPCs and stations are placed by `npc` markers (spec 11.6). `MarkerKind` has
  PlayerSpawn, MonsterSpawn, Npc and ResourceNode; a stall place is a new kind.
- **Crafting** (`src/GameServer/Crafting`): `ForgeService` keeps one `ForgeSession` per character at a public station,
  with **`Owner` and `OutputRecipient` kept apart and the inputs read only from its reservation**, both marked
  `TODO(M4)` for contracts (spec 5.3 "Contracts (5.4, interfaces only)"). `Crafted` carries `recipientCharacterId`.
  `ForgeService`, `EnchantService` and `GemService` write their `craft_ops` rows through `CraftBatchSources` on the
  character save.
- **Jobs** (`src/GameServer/Jobs`): `JobService` with `IJobQuery` and `IJobBonusQuery`. The job trees defer "contract
  and minigame bonuses to M4" (M3 decision 9); there is no Merchant job (R-4.2-05's fee discount has no holder).
- **Social** (`src/GameServer/Social`): `ChatService` (local channel only, a token bucket from `ChatSettings`) and
  `DisplayIdentity`. **There is no `Party`**: `PvpRules.CanDamage` returns false for every pair (no class change
  before level 30), `IVisibilityQuery` is `EveryoneVisible`, `ExpService` pays the killer only (`TODO(M4): per party
  member in range`), `RunStore` keeps `owner_party_id` null (`TODO(M4): party runs`), `LootDrop` locks to the killer.
  The HUD registry has the `party_frame` row (9.3, "in a party"), the chat its channel tabs (Local, Party, Guild...)
  in the mockup only, and the minimap a `partyMember` marker source "a list M4 parties fill". Friends, mute, block,
  whisper and global chat are not built either (spec 6.5 says "M2" for them; carried).
- **Progression** (`src/GameServer/Progression`): `ProgressionStore` is the only writer of level, exp, rank, points
  and counters; `AchievementCategory` has MonsterKills, MonsterTypesKilled, BossKills, RunsClaimed and OutpostsVisited
  plus the M3 gathering and crafting categories; none for trades or contracts.
- **World.** `RegionDef` has `areaType`, `minLevel` and the flags of spec 2.3 through `RegionDefaults`; the
  `deathDrop` flag (on, off, confiscate) is in the spec's table and must be checked for a row field by WP-M4-9. The
  field zone is the greybox of `WorldAuthoring/Editor/FieldZoneGreybox`; the city has the outposts, the job master,
  the forge and the bench.
- **Tools.**
  - Bot loop steps, one file each, in `src/Tools/BotClient/Loop/Steps`: Allocate, Buy, Convert, Craft, Dev, Enchant,
    Equip, Fight, Gather, LearnJob, Learn, Level, Pickup, RankUp, Refine, Run, Sell, Socket, TakeJob, UseItem, with
    `ForgeRun`. Their contract is in that README. Scenarios: `ci-smoke`, `ci-merge`, `m1-exit`, `m2-zone`, `m2-loop`,
    `m2-exit`, `m3-loop`, `m3-exit` (two bots with scripts per bot, a level seed, done / not yet / failed per entry;
    `tools/load/Run-M3Exit.ps1`). **Bots act alone**: no step addresses another bot.
  - The QA smoke steps are `StepNames` in `unity/Assets/Game/Dev/Qa/QaScenario.cs`, from `title` to
    `tutorial-gate` (33 steps; `QaStepFlow` keeps the run going after a failure). Test-only seeds live in `tools/qa`
    (`Set-BagItem.ps1`, `Set-BagInstance.ps1`, `Set-KillCount.ps1`, `Set-LearnedSkill.ps1`; `Run-QaSmoke.ps1
    -SeedStack`). **The harness drives one client**; a second player needs a partner bot.
  - `src/Tools/EconomySimulator` models sales as the only faucet and vendors, consumable use, failed conversions,
    enchant stones and failed sockets as sinks; `EconomySimScenario` rows; `--check` in CI; the enchant ladder and
    the conversion arbitrage Monte Carlo. No trade or listing volume.
- **UI.**
  - `unity/Assets/Game/UI/Menus` has Book, Character, Forge, Inventory, Jobs, Outpost, Quests and Skills; every window
    follows the 9.13 pattern (`HudLayout` row, `Hotkeys` row, `UiStrings`, reason-code toasts).
  - The shortcut row's buttons are `character`, `inventory`, `skills`, `jobs`, `book`, `journal`. **There is no M
    (mail), C (contracts) or SO (social) button**, and no badge counts.
  - `UnlockSchedule` hides prompts and HUD elements below a level, with `hideWhen` conditions (WP-M3-11); spec 12.3
    puts party at 12-18 and trade, auction and shops at 20-25, with no exact level per feature.

**Tables on main (141):** AchievementDef ActionBindings ActionDef ActionEffectDef AdminSettings AttributeFormula
AuthApiLimits BodyCollisionSettings BotBehaviour BotLinkProfile BotScenario BuffIconRegistry BuildUpDef BystialCardArt
BystialCardDef BystialEffectCap BystialFamilyBonus BystialSlots BystialStatCap CharacterCreationConfig
CharacterSheetStats CharacterSlots ChatSettings ClassDef ClassGearDef ClientCamera ClientPrediction ConsumableDef
ConsumptionMeterSettings ContainerDef ConversionOdds DamageConstants DemandCurve DemandSettings DevTools
EconomySettings EconomySimScenario EffectBuildUpDef ElementCaps EnchantBonus EnchantMaterial EnchantOdds
EntityVisualDef EquipSlotDef EventBusLimits ExpBandCurves ExpCurve ForfeitPenalty ForgeActionDef ForgeChallengeDef
ForgeSettings GatherNodes GatherSettings GemDef GemSocketOdds GroundAreaSettings HarvestSettings HitShapeDef Hotkeys
HudLayout HudSettings HudThemeTokens InventorySettings InventoryWindowSettings ItemDef ItemIconRegistry
ItemStatBonusDef JobBonusCap JobDef JobLevelCurve JobSettings JobTree JobTreeSettings JobXpWeights LoginLayout
LoginScreens LoginSettings LootSettings LootTableDef LootTableEntry MaterialStatDef MaterialTagDef MaterialTierDef
MinimapIconRegistry MinimapMarkerDef MinimapRegionTint MinimapSettings MoldDef MoldSlotDef MonsterAiSettings
MonsterDef NetSecurity NodeDef NodeYieldEntry NpcDef NpcVendorStock OutpostQuestDef PriceBase PriceFloorRecovery
ProgressionSettings ProjectileDef ProjectileSettings QualityCaps QualityThresholds QuestBoardSettings QuestCaps
QuestItemDrop RankFactor RankLevelRanges RankModifier RankRequirements RarityDef RarityMult ReasonCodeStrings
RecipeDef RecipeInputDef RegionDef RegionDefaults ReliableMessageCaps ResourceRegen RunBonus ServerDirectory
ServerLoop SessionSettings SkillDef SkillEvolution SkillModifierDef SkillTree SkillTreeSettings SpawnPoints
StartingValuesM1 StatDef StatusEffectDef StatusModifierDef StatusSettings TargetLockSettings TelemetrySettings ToolDef
TutorialSettings UiStrings UnlockSchedule.

**Carried over from M3** (not blockers for M4; list them in 3a when they move):
- **The balance phase and the M2 exit run** ("5 bots reach level 30 within 4 h"): deferred to the end of the project
  (user decision 2026-10-30), with the Warrior K pass, the mage and assist bot deaths, the bosses' stun resistance and
  the Blood loss fill rate. Every M3 number is a placeholder too.
- **Pending `TODO(open)` calls from M2 PRs:** WP-M2-5 (points back when a table change drops a learned node; respec and
  a weapon a cleared unlock allowed), WP-M2-6 (the bot's deliver offers; a board that always carries a kill offer),
  WP-M2-8 (the family threshold and the build-up resistance readings).
- **The second-client WP and the 3-4 player playtests at 100 and 200 ms** (from M1). M4's trade, party and contract
  work needs two clients in play anyway: the partner bot of WP-M4-12 covers the harness, and the first team playtest
  is the natural place for the protocol of 12.4 K.
- **The LAN corrections** (wave 2 QA observation, fixer round 3): about 125 prediction corrections over a 50-bot LAN
  run, 111 on the bots whose sessions entered the world within 5 ms; press volume, flinches and hits taken ruled out;
  the cause not shown. Next step: a per-bot correction counter with the mismatch kind (a small WP or the first fixer
  round).
- **The review notes of PR #161** (six low): `tools/qa/Set-LearnedSkill.ps1` writes `character_skills` and the hotbar
  JSON around `SkillTreeService` (a `DbMigrate --dev --learn` through the service would follow the writer, as `--give`
  does); `BotCounters.NoteInCombat` widened the load criterion instead of fixing the boss-homed assist bot's idle; the
  `m3-exit` 55-minute duration does not cover the 45-minute step limit plus the rest of the script; the seed's
  single-line regex over `SkillTree.json`; `WatchStagger` scanning the scene per frame until the feed exists;
  `RecentMonsters` evicting by first sight.
- **Left by fixer round 3 with reasons:** the tooltip clamp, the reconnect `NoSession`, `_sessionInputs`, the reader
  allocation test flake.
- **WP-M3-13 part 2** (the admin panel's Dev section for the console commands): only if the user asks.
- **Zone art** (CC0 models for the six field types and the Warlord): the archived art session.
- **Known placeholders:** interact key T; NPCs wear class avatars; the faint uncolored telegraph; melee chain hits
  without a forward step; the forge's timing bar art; every table number.

## 4. Spec sections to read per area (before briefing a WP)
| Area | Spec | Notes |
|---|---|---|
| Fees and sinks | 4.2 (R-4.2-04..06, `FeeService.Quote`/`Apply`, `SinkLedger`), 12.5 | owner share is 0 until guilds (M8): the whole fee is destroyed; the Merchant discount has no job yet (decision 3) |
| Party | 6.5 (R-6.5-01 "party (loot, quests, immunity)"), 2.4 (R-2.4-03 "party members always see each other"), 3.1 and 3.2 (party immunity in `HitResolver` and projectiles), 3.6 (R-3.6-03, exp per member), 5.5 (party runs) | party finder and matchmaking are M7 (R-6.5-02); chat channels in 6.5 and 9.12 |
| Party loot | 4.3 (R-4.3-02, `PartyRoll`, `LootDebt`), 8.1 (`LootRolled`) | the 30 s lock exists for the killer; rolls unit by unit; full bag → mail |
| Death loot bags | 4.3 (R-4.3-03, the death transaction), 4.4 (R-4.4-01 the stolen mark), 2.3 (`deathDrop` flag), 9.5 and 9.12 (the minimap death marker) | honor changes are M5 (R-6.1-03): set the mark and the owner now, leave `TODO(M5)` for honor |
| Mail | 4.3 ("full bag → mail"), 4.4 (returns by mail), 4.5 (unsold listings return by mail), 7.4 ("send compensation by mail"), 7.5 (rate limits on mail), 9.3 (`shortcut_row` M badge, `MailCount`) | the model is Open (decision 4) |
| Trade | 4.5 (R-4.5-01, -02, the trade window implementation), 4.1 (R-4.1-04 the trade checks of the stolen and non-tradable flags), 7.1 (R-7.1-01 class 1, R-7.1-02 the barrier) | one transaction over two characters (WP-M4-4) |
| Auction and shops | 4.5 (listings share one model, `ShopStall` entities, paginated search), 7.1 (`listings`, `shops`, Redis "listings, cache"), 7.5 (rate limits on listings) | buyout only or bids is Open (decision 6) |
| Contracts and portfolio | 5.4 (R-5.4-01..04, the state machine, `EscrowService`), 5.3 ("Contracts (5.4, interfaces only)"), 4.4 (R-4.4-03 no stolen inputs), 8.2 ("Contract accept / complete") | the forge session owned by the contract, the output to the customer |
| Save classes and transactions | 7.1 (R-7.1-01, -02, -05, -06), 8.2 | every M4 operation is class 1; confirm after the commit; the audit ledger must still rebuild a character |
| Economy telemetry and the dashboard | 7.2 (R-7.2-02 "periodic economy snapshots... faucet and sink totals by reason"), 7.4 (admin panel features), 12.5 (the M2 and M3 instruments) | the exit's dashboard |
| Events, transactions and tables | 8.1, 8.2, 8.5 ("Items / loot" and "Economy" rows) | 8.1 already lists `TradeCompleted`, `ListingCreated`, `ListingSold`, `LootRolled`, `LootBag*`, `FeeCharged`, `GoldDestroyed`, `PartyChanged`, `GroupFormed`, `ContractCompleted`, `ContractExpired` |
| HUD and menus | 9.3 (`party_frame`, `chat` channel tabs, `shortcut_row` M and C badges), 9.12 (chat channels), 9.13 (window pattern, "Dropping to the ground is not in M1") | spec 10 lists the screens not mocked: auction and shops, contracts, journal, social |
| Unlock schedule | 12.3 (12-18 party; 20-25 trade, auction, shops) | exact levels per feature are Open (decision 14) |
| Anti-abuse | 7.3, 7.5 (rate limits on trade, listings and mail; large transfers flagged), 5.4 (minimum contract value, per-pair caps) | flags are telemetry and an admin list in M4, never an automatic action |
| Law (interfaces only) | 4.4, 6.1 | M5: the stolen mark's honor effects, the black market, laundering, return by guild |

## 5. Decisions the user must make (Open in the spec; ask before the wave that needs them)
Each decision gives its rule ids, the question and the orchestrator's recommended answer with the reason. The plan
decides none of them. Record every answer in the spec section named and in `13-decision-log.md` in the PR that
implements it. **Every number is a placeholder** (section 2).

### Before wave 0
1. **Party rules** (R-6.5-01 "standard MMO behavior", spec 2.4, 3.6, 5.5; nothing is written down).
   - Size, roles and lifetime: **recommendation: up to 6 members** (the raid size of spec 9 and R-6.5-01), one leader
     who invites, kicks and passes the lead; a member leaves at will; the party dissolves below two members; a
     disconnected member stays for the linger time, then leaves. No roles in M4 (roles are the M7 matchmaking's).
   - Exp (R-3.6-03, `ExpService` `TODO(M4)`): **recommendation: every member within the interest radius of the kill
     shares the kill, each paid by their own rank and level, split by damage contribution with a floor per member**
     (spec 5.5 says "exp per member by contribution" for runs; one rule for both). The floor and the range are
     `PartySettings` rows.
   - Runs (spec 5.5 `TODO(M4)`): **recommendation: the run stays the character's; the kills of every member count for
     every member's active run** (no shared run object, no `owner_party_id` yet: `RunStore` stays one writer per
     character; shared party runs can come with the party finder).
   - Immunity (R-2.4-03, 3.1): party members never damage each other and always see each other. In M4 no player
     damages a player anyway (`PvpRules`), so the rule lands as the party check inside `PvpRules` and
     `IVisibilityQuery` with tests, inert in play until M5.
2. **Party loot parameters** (R-4.3-02, `PartyLootParams`): the eligible range, the loot debt decay and value weight,
   and whether every pickup rolls or the leader may pick a mode.
   - **Recommendation: one mode in M4, the weighted roll of R-4.3-02** (no master looter, no round robin): eligible
     members are online, within the interest radius of the drop and with a hit on the monster or within the range at
     the kill; the debt is the item's `PriceBase` value (0 for an item without a price) decaying by half every 10
     minutes; stackables roll unit by unit; the drop lock of 30 s applies to the whole party. Placeholders.
3. **Fee rates and the Merchant** (R-4.2-05, `FeeRates`): the listing fee, the sale tax, the contract fee, their bounds,
   and what to do about the Merchant discount with no Merchant job.
   - **Recommendation: three fee types with placeholders** (listing fee 2 % of the asking price, paid at listing and
     not returned; sale tax 5 % of the sale price; contract fee 5 % of the contract gold), bounds for owners to set
     later (M8), and **the whole fee destroyed** until territories exist (`ITerritoryOwnerQuery` returns none). The
     Merchant discount is a hook in `FeeService` on `IJobQuery` with no job row: `TODO(open)` for the job list beyond
     the two of M3.
4. **The mail model** (spec 4.3, 4.4, 4.5, 7.4 use mail; nothing defines it).
   - **Recommendation: system mail only in M4**: the server writes mails for returned and unsold listings, listing
     proceeds, party roll winnings that do not fit, contract returns and admin compensation; each mail carries items
     and gold as attachments the player claims into the bag at any place (a class 1 operation); mails expire after 30
     days and expired attachments are destroyed (a sink, `MailExpired`); a mailbox cap per character. **Player-to-player
     mail with attachments is deferred**: it is a third transfer channel with its own fee and rate limits, and nothing
     in the M4 exit needs it (`TODO(open)`).
   - Proceeds always go to the seller by mail, even when the seller is online: a settlement then touches the buyer's
     character and the `mail` table only, which keeps one writer per character (spec 7.1) and makes the offline seller
     the same case as the online one.
5. **Trade rules** (R-4.5-01 implementation: "proximity required", "cancel on disconnect").
   - **Recommendation** (`TradeSettings`): the two players within 500 cm, both alive and out of combat; up to 12 item
     slots and a gold amount per side; a change by either side clears both locks; both lock, then both confirm within
     60 s; the server re-validates every unit and the gold at confirm; cancelled when one moves out of range,
     disconnects, dies or enters combat; one open trade per player; a cooldown of 5 s after a cancel.
   - Which flags block a trade (R-4.1-04, R-4.5-01): sell-only, quest, stolen or tainted, non-tradable and reserved
     units, and gear that is equipped. **Recommendation: refuse them all with their own reason codes**; molds and
     tools trade (spec 4.1 decision), gems and materials trade.

### Before wave 1
6. **Auction listings** (R-4.5-01, 4.5 implementation): durations, caps, buyout or bids, the search fields.
   - **Recommendation: buyout only in M4** (bids need a second escrow of gold per bidder and sniping rules; it can be
     added on the same model later, `TODO(open)`); durations 12, 24 or 48 hours; 10 active listings per character; the
     item in escrow from the listing; search by name, category, rarity, level range and job, sorted by unit price, 20
     rows per page from indexed SQL (no Redis listing cache in M4); the auctioneer is an NPC in the city
     (`NpcInteraction.Auction`), browsing and buying need the NPC in reach (the outpost pattern), so the auction is not
     a remote market.
7. **Shop stalls** (R-4.5-01 "small shops in cities", 4.5 "`ShopStall` entities, cap per city, a cap on visible
   stalls"): where, how many, how long, and whether a stall sells while its owner is offline.
   - **Recommendation**: stall places are `stall` markers in the city (the cap per city is the marker count, 12
     placeholders); a stall is opened on a free place for 24 hours against the listing fee, holds up to 8 listings on
     the shared listing model, **stays open while the owner is offline** (the items are in escrow, the proceeds go by
     mail, so nothing needs the owner), and returns its unsold items by mail when it closes; one stall per character;
     visible stalls are capped by `InterestRadius` like every entity; the stall shows its owner through
     `DisplayIdentity`.
8. **Death loot bags** (R-4.3-03; spec 10 "[M4] loot window lengths after testing"; `OwnerWindows`, `BagExpiry`).
   - The owner window and the expiry: **recommendation: 60 s owner window, 15 minutes expiry** (the placeholders spec 12.7
     already lists next to the ground drop's `bagExpiryMs`).
   - What drops: **recommendation: the whole Normal bag as the rule says**, nothing of the Safe and Quest bags, and
     the equipped gear stays (R-4.1-02); gold does not drop (the spec names the bag only).
   - Regions: the `deathDrop` flag of 2.3: on in the field zone's rings, off in the city, confiscate is M5 (guards).
   - Party members "collect for the owner": **recommendation: a member's pickup inside the owner window sends the
     items to the owner by mail**, with no stolen mark; outside the window every non-owner pickup sets `stolen`,
     `originalOwnerId` and `stolenAt` on the instances and stacks (R-4.4-01) with no honor change in M4 (`TODO(M5)`).
   - A bag the owner picks up returns the items to the Normal bag; what does not fit goes by mail.
9. **Pets** (R-4.3-04, listed under the M4 loot `TODO(M4)`): in M4 or later?
   - **Recommendation: later**, with mounts (2.2) and their unlock source; M4 keeps the pickup range a server rule the
     pet can extend (`IPickupRangeSource`), nothing more.

### Before wave 2
10. **Contracts** (R-5.4-01..03; the numbers and the board are Open).
    - **Recommendation** (`ContractSettings`): the customer proposes a job action (refine, convert, craft with a named
      mold and recipe, enchant) with the materials either side supplies and a gold payment; the deadline is chosen by
      the customer between 1 and 72 hours and runs in real time; a crafter accepts at the **contract board**, an NPC in
      the city (`NpcInteraction.Contracts`), where open proposals are listed; proposing and following a contract is
      possible anywhere through the C window; the crafter's cancel after work started costs 10 % of the gold (a sink)
      and returns everything else; the customer cannot cancel after work started; a timeout returns everything and
      counts as expired; minimum contract value 10 gold and 5 contracts per pair and day (R-5.4-04 anti-farming).
    - Supplying materials: the customer's materials and gold go to escrow at acceptance; the crafter's own materials
      are reserved in the crafter's bag at the forge, as now.
11. **Portfolio** (R-5.4-04): **recommendation**: counters per crafter from `Crafted`, `Enchanted`, `Converted`,
    `ContractCompleted`, `ContractExpired` and the cancel (by job, mold category, rarity, outcome), a showcase of up
    to 6 pinned snapshots, a hide flag; shown in the Jobs window's portfolio tab and, for another player, from the
    contract board.
12. **Which screens to build** (spec 10 "[M4] screens not mocked yet: ... auction and shops, ..., journal, contracts,
    ..."): **recommendation**: the party frame and the party invite toast, the trade window, the mail window, the
    auction window (search, my listings, sell), the stall windows (owner and buyer), the loot bag window, the contract
    board and the C window, the chat channel tabs; the journal's contracts tab and the social window (friends list)
    minimal; each in the 9.13 pattern with placeholder art.
13. **The economy dashboard** (the exit; 7.2, 7.4, 12.5): **recommendation**: an admin panel page reading
    `gold_ledger` (faucet and sink totals per hour and by reason, SQL, the authoritative figures), the
    `faucet_sink_snapshots` rows the world writes after each control step (gold in circulation, active players) and
    the newest `EconomyTotals` telemetry for the live line; the same figures as `BotClient report --kind economy`,
    with a test comparing the two over a recorded run.
14. **Unlock levels** (12.3): **recommendation**: `UnlockSchedule` rows party 12, trade 20, auction 22, stalls 25,
    contracts 20 (placeholders inside the spec's bands); prompts for each.
15. **Character creation screen** (spec 10 "[M4]... character creation screen"): **recommendation: leave the
    placeholder** (M2 decision 11) until the avatar art pipeline; not in the M4 exit.
16. **Friends, whisper, mute and block, global chat** (R-6.5-01; spec 6.5 said "M2", not built): **recommendation**:
    party chat and whisper in M4 (the party WP routes channels), friends and mute/block as the small social WP of
    wave 2 if time allows, global chat with the party finder (M7).
17. **Large transfers flagged** (R-4.5-02, 7.5): **recommendation**: `TransferFlagSettings` thresholds (gold and
    value per pair and day), a `TransferFlagged` telemetry record and an admin panel list; no automatic action.

### After wave 2
18. **M4 close**: when the exit run passes with QA's evidence, the user closes M4 (12.2 row) and the M5 handover plan
    is written in the same shape.

## 6. Work packages
Each WP lists what it adds under **Data**, **Server**, **Events**, **Migration**, **Protocol**, **Client**, **Bot/smoke
step**, **Tests**, **Shares** and **Done**. The protocol, reliable id, migration and telemetry numbers are proposals in
wave order from **protocol 41, reliable id 56, realm V0016, telemetry kind 104, `InventoryReason` 20,
`NpcInteraction` 8**; the merge order assigns the real ones (section 7). Every number in a table is a placeholder.

### Wave 0: foundations (parallel; nothing in it depends on another M4 WP)
**WP-M4-1 FeeService and the fee sinks (spec 4.2; decision 3).**
- **Data.** `FeeRates` (one row per fee type: `listing`, `saleTax`, `contract`; default percent, min and max for
  owners later, the destroyed share percent when owned), `EconomySettings` fields for the ledger.
- **Server.** `src/GameServer/Economy/FeeService`: `Quote(regionId, feeType, amount)` returns `(total, ownerShare,
  adventurerShare)`, `Apply(characterId, quote, opKey, source)` destroys the adventurer share through
  `InventoryService.TryRemoveGold` with reason `Fee` **inside the caller's operation** (the caller's save carries it)
  and credits the owner share through an `ITerritoryOwnerQuery` that returns none in M4 (`TODO(M8)`). The Merchant
  hook on `IJobQuery` with no job. `EconomyLedger.ReasonCount` widened to the enum's last value.
- **Events.** `FeeCharged {characterId, feeType, regionId, total, ownerShare, destroyed, opKey}` after the commit;
  `GoldDestroyed` as today.
- **Telemetry.** `FeeCharged` kind 104; `EconomyByReason` gains the `fee` reason.
- **Simulator.** `EconomySimScenario` gains `tradesPerPlayerHour`, `listingsPerPlayerHour` and a traded value
  distribution (placeholders); fees are modelled as a sink of that volume; the sink cover line counts them.
- **Tests.** The split as a pure function; a test operation charges a fee and the four tests on it; `--check` green.
- **Shares** `FeeService` with WP-M4-7, -8 and -10; the `Fee` reason with WP-M4-5's dashboard.
- **Done:** a test sale charges a fee, the destroyed gold shows in `gold_ledger`, `EconomyLedger` and the report, and
  the simulator's sink cover line counts fees.

**WP-M4-2 Party core and chat channels (spec 6.5, 2.4, 3.6; decisions 1 and 16).**
- **Data.** `PartySettings` (max members 6, invite timeout, exp share range and floor, the linger rule),
  `ChatSettings` per channel (append `channel` rows: local, party, whisper), `UnlockSchedule` row for the party.
- **Server.** `src/GameServer/Social/PartyService`, **the only writer of party state** (in memory on the world; one
  world in M4; `party_history` rows class 2 for telemetry only): invite, accept, decline, leave, kick, pass lead,
  disband; `IPartyQuery` (members of, same party, leader) read by loot, exp, visibility and PvP rules. Exp: `ExpService`
  pays every eligible member by their own rank and level, split by contribution (decision 1); runs: every member's
  kill counts for each member's active run (`QuestService` reads `IPartyQuery`). `PvpRules` and `IVisibilityQuery`
  gain the same-party check (inert in play, tested). `ChatService` routes by channel: party to the members, whisper to
  one recipient by display name through `DisplayIdentity`; rate limits per channel.
- **Events.** `PartyChanged {partyId, characterId, change (formed, joined, left, kicked, leaderChanged, disbanded),
  members}`; `GroupFormed` when the second member joins.
- **Protocol 41** (reliable ids from 56): `PartyRequest` (the action and the target by display name or entity),
  `PartyResult`, `PartyState` (members with character id, display name per recipient, class, level, online, and HP
  percent for members out of interest, sent on change and at a `PartySettings` interval), `PartyInvite` (to the
  invitee, with a timeout); `ChatSendRequest` gains the channel and the whisper target (appended fields).
- **Client.** The party frame (`HudLayout` `party_frame`), the invite toast, the chat channel tabs, right-click or
  `/invite <name>` through the chat parser (`/invite`, `/leave`, `/kick`, `/w <name>`); `Entities/Core/ClientParty`
  for the bots. Minimap `partyMember` markers from `PartyState`.
- **Bot steps** `party:<role>` (one bot invites its pair, the other accepts; the pair from the scenario) and
  `whisper`; **smoke steps** `party-up` (with the partner bot) and `party-chat`.
- **Tests.** The contribution split and the floor as pure functions; invite races (two invites to one player, an
  invite to a full party, a leave and a kick in one tick); exp to two members with different ranks; a kill counting
  for both runs; the visibility and PvP checks.
- **Shares** `IPartyQuery` with WP-M4-6 and -9, the channel field with WP-M4-6's roll line.
- **Done:** two bots form a party, both see the frame with the other's HP, a kill pays both exp, a party message
  reaches only the party, and a whisper reaches one player.

**WP-M4-3 Mail (spec 4.3, 4.4, 4.5, 7.4; decision 4).**
- **Data.** `MailSettings` (mailbox cap, expiry days, attachments per mail, claim range none), `UiStrings` for the
  system mail subjects, `HudLayout` `mail_window`, `Hotkeys` `toggleMail`, the M shortcut button with its badge.
- **Server.** `src/GameServer/Items/MailService`, **the only writer of mailboxes**: `Send(toCharacterId, kind, items,
  gold, opKey)` writes a `mail` row with its attachments **in the caller's transaction** (the attachments are created
  from the operation's own items: an escrow release, a roll winning, an admin grant), readable whether the recipient
  is online or not; `Claim(characterId, mailId, opKey)` moves the attachments into the Normal bag and the gold to the
  character as one class 1 operation through `InventoryService` (reason `MailClaim`), refused `BagFull` when nothing
  fits (a partial claim takes the gold and the items that fit); expiry destroys the attachments (reason `MailExpired`, a
  sink) in a world job outside the tick thread. `MailCount` for the badge on login and on change.
- **Events.** `MailSent {toCharacterId, mailId, kind, items, gold}`, `MailClaimed`, `MailExpired`.
- **Migration V0016** `mail` (recipient, kind, subject id, body args, gold, created, expires, claimed at, the op key) and
  `mail_items` (mail, def id, rarity, count, instance snapshot JSON, stolen and owner fields carried through).
- **Protocol 42**: `MailList` (headers), `MailOpenRequest`/`MailContent`, `MailClaimRequest`/`MailResult`,
  `MailCount`.
- **Client.** The mail window (list, open, claim, claim all) in the 9.13 pattern; the badge on the M button;
  `ClientMail` for the bots.
- **Admin panel.** "Send compensation by mail" (7.4): an admin action writing a system mail through the same path (an
  `admin_actions` row, the grant reason `Admin`).
- **Bot step** `mail:claim`; **smoke step** `claim-mail` (a seeded system mail through `DbMigrate --dev --mail`, the
  writer's path).
- **Tests.** The four tests on the claim (double claim, retry, a crash between the row update and the bag write,
  two claims at once from two sessions of one character are impossible by the ownership lock: tested as a double
  submit), expiry idempotent, a claim into a full bag.
- **Shares** `MailService.Send` with WP-M4-6, -7, -8, -9 and -10.
- **Done:** a system mail with an item and gold is claimed into the bag after a relogin; an expired mail's items are
  destroyed and counted; the admin panel sends a compensation.

**WP-M4-4 Escrow, the two-character transaction and the trade window (spec 4.5, 4.1 R-4.1-04, 7.1; decision 5).**
- **Data.** `TradeSettings` (range, slots, gold cap per trade, lock and confirm timeouts, cancel cooldown),
  `UnlockSchedule` row, `HudLayout` `trade_window`, reason codes `TradeTooFar`, `TradeBusy`, `TradeNotTradable`,
  `TradeEquipped`, `TradeReserved`, `TradeChanged`, `TradeCancelled`, `TradeTimeout`, `TradeInCombat`, `TradeDead`,
  `TradeSelf`, `TradeDeclined`.
- **Server.**
  - **Persistence:** `CharacterRepository.SaveManyAsync` (or a `TransferTransaction`): the pending batches of two
    characters, both version-checked, written in **one database transaction** with one `transfer_ops` row (the op
    key, both characters, the items and gold moved, the save ids); `SaveScheduler` issues it as one job holding both
    characters' save slots, after both pending class 2 batches are included (the barrier by inclusion over two
    characters); a `VersionConflict` or `DuplicateOperation` on either side rolls back both and faults both. This is
    the **shared transfer path** of M4.
  - `src/GameServer/Items/EscrowService`, **the only holder of assets outside a character**: `Hold(characterId,
    items, gold, holderKind, holderId, opKey)` removes them from the character (reason `EscrowHold`) and writes
    `escrow` rows in the same transaction; `Release(escrowId, toCharacterId | mail, opKey)` gives them to a character
    (reason `EscrowRelease`) or to `MailService.Send`, in one transaction. Listings and contracts use it; the trade
    does not need escrow (both sides are online and the swap is one transaction).
  - `src/GameServer/Items/TradeService`: the window state per pair (offer, lock, confirm), re-validation of every unit
    and the gold at confirm (ownership, flags, reservations, equipped, the bag space of the other side including
    stack merges, the gold cap), then one two-character transaction swapping items and gold with reason `Trade`;
    answered to both after the commit; cancel on range, death, combat, disconnect, timeout or a change.
- **Events.** `TradeCompleted {characterA, characterB, itemsAtoB, itemsBtoA, goldAtoB, goldBtoA, opKey}` after the
  commit; `TradeCancelled` (reason); `ItemTransferred` per item (8.1), `GoldChanged` moves (flow `move`).
- **Migration V0017** `escrow` (holder kind and id, owner character, gold, state, created) and `escrow_items`;
  `transfer_ops` (the op key as primary key, both characters, the save ids, the moved summary).
- **Protocol 43**: `TradeRequest` (invite, accept, decline, add, remove, setGold, lock, confirm, cancel),
  `TradeState` (both offers, locks, the countdown), `TradeResult`.
- **Client.** The trade window (both sides, lock and confirm buttons, the gold field, the drag from the inventory
  window), the invite toast, `/trade <name>`; `ClientTrade` for the bots.
- **Bot steps** `trade:<give>:<for>` (the pair: one offers an item, the other gold, both confirm); **smoke step**
  `trade-item` with the partner bot.
- **Tests.** The four tests on the trade (double confirm, retry after an unknown outcome, a crash between the two
  rows, a confirm while one unit is sold or moved: `TradeChanged`), the two-character save path alone (version
  conflict on the second character rolls back the first), the escrow hold and release with their four tests, the
  conservation check of section 1.
- **Shares** the two-character transaction and `EscrowService` with WP-M4-7, -8 and -10; `TradeSettings` with
  WP-M4-11.
- **Done:** two bots trade an item for gold; both bags and balances change in one transaction visible after a
  relogin; a confirm after a change is refused; the ledger shows two moves and no faucet.

**WP-M4-5 Economy snapshots and the dashboard data (spec 7.2, 7.4, 12.5; decision 13).**
- **Data.** `EconomySettings.snapshotIntervalMs`, `AdminSettings` for the dashboard's hour window.
- **Server.** After each demand control step the world writes a `faucet_sink_snapshots` row (gold created and
  destroyed since the last row by reason, gold held online, active players, the UTC time) through the persistence
  worker (never on the tick thread); the `gold_ledger` gains an index on `(created_at, reason)`.
- **Migration V0018** `faucet_sink_snapshots`, the ledger index.
- **Admin panel.** `GET /economy?hours=<n>` returns faucet and sink totals per hour and by reason from `gold_ledger`,
  the snapshots and the newest `EconomyTotals` telemetry line; the `wwwroot` page "Economy" draws them as a table
  and a simple bar chart (no new dependency; ask before adding a chart library).
- **Tests.** The SQL totals against a seeded ledger; the page's data equals `BotClient report --kind economy --json`
  over a recorded run.
- **Shares** the `Fee` and M4 reasons with every WP (the report lists them; the existing name test).
- **Done:** after a 20-minute bot run, the admin panel shows faucet and sink totals per hour and by reason that match
  the report.

### Wave 1: party loot, listings and loot bags (after wave 0 merged)
**WP-M4-6 Party loot rolls and loot debt (spec 4.3 R-4.3-02; decision 2).**
- **Data.** `PartyLootParams` (eligible range, debt half-life, value source, the lock window moved from
  `StartingValuesM1.partyLootLockMs` with a `TableReloadPolicy` class), `UiStrings` for the roll line.
- **Server.** `LootService`: a drop is locked to the killer's party (`IPartyQuery`); a pickup by a member rolls among
  the eligible members with `Shared/Simulation/Items/PartyLoot` (`RollWeights(debts, values)`, `DecayDebt`), seeded
  from the world's loot stream and logged; stackables unit by unit; the winner's bag through `InventoryService.TryAdd`
  (reason `LootPickup`, the picker's op key), what does not fit by `MailService.Send`; the debt map per party in
  `PartyService` (memory). The roll line in the party chat channel.
- **Events.** `LootRolled {partyId, dropKey, winnerCharacterId, weights, seed}`.
- **Telemetry.** `LootRolled` kind.
- **Protocol 44**: nothing new on the wire beyond the chat line and a `LootRolled` toast message if the user wants one.
- **Client.** The roll line, a toast for the winner.
- **Bot steps**: the pair's `fight` with `pickup` shows the roll in the report; **smoke step** `party-loot`.
- **Tests.** Weights and decay as pure functions (a member with nothing gets more; a zero-value item is uniform); a
  roll whose winner's bag is full mails the item; two members picking one drop in one tick; a member leaving between
  the kill and the pickup.
- **Shares** `IPartyQuery` (WP-M4-2), `MailService.Send` (WP-M4-3).
- **Done:** two bots in a party; the drops of ten kills are spread between them, every roll logged with its seed, and
  the full-bag case arrives by mail.

**WP-M4-7 Listings and the auction house (spec 4.5; decision 6).**
- **Data.** `ListingSettings` (durations, caps per character, page size, search fields, the stall listing cap),
  `NpcDef` `m1.auctioneer` with `NpcInteraction.Auction = 8` and an npc marker in the city, `UnlockSchedule` row,
  reason codes `ListingNotFound`, `ListingExpired`, `ListingOwn`, `ListingCap`, `ListingNotTradable`,
  `ListingPriceInvalid`, `ListingSold`, `NotEnoughGold`, `BagFull`.
- **Server.** `src/GameServer/Market/ListingService`, **the only writer of listings** (auction and stalls share it):
  `Create` holds the item in escrow and charges the listing fee through `FeeService` in one transaction (the seller's
  save with the `listings` row), `Buy` is a two-party settlement in one transaction: the buyer's gold out (reason
  `ListingPurchase`), the sale tax destroyed through `FeeService`, the proceeds to the seller by `MailService.Send`,
  the escrow released to the buyer's bag (or by mail when full: decision), the listing closed; `Cancel` returns the
  item from escrow to the seller by mail (the fee is kept); expiry does the same in a world job. Search is SQL with
  indexes on `(state, category, rarity, level, unit_price)` and keyset pagination, run on the persistence worker and
  answered to the player asynchronously.
- **Events.** `ListingCreated`, `ListingSold`, `ListingCancelled`, `ListingExpired`.
- **Migration V0019** `listings` (seller, kind auction or stall, stall id, escrow id, def id, rarity, count, unit
  price, fee paid, created, expires, state, buyer, sold at, op keys).
- **Protocol 45**: `ListingSearchRequest`/`ListingSearchResponse` (a page), `ListingCreateRequest`,
  `ListingBuyRequest`, `ListingCancelRequest`, `ListingResult`, `MyListings`.
- **Client.** The auction window (search with filters, buy; my listings with cancel; sell from the inventory window's
  drag or a picker) at the auctioneer; `ClientListings` for the bots.
- **Bot steps** `list:<item>:<price>`, `buy:listing:<item>`; **smoke steps** `list-item`, `buy-listing` (the partner
  bot lists).
- **Tests.** The four tests on create, buy, cancel and expiry; two buyers on one listing (one `ListingSold`); a buy
  racing a cancel; a crash between the buyer's debit and the mail row (both or neither); the search over 10,000
  seeded rows within the plan's budget; conservation.
- **Shares** `ListingService` with WP-M4-8; `FeeService`, `EscrowService`, `MailService`.
- **Done:** a bot lists an item, its pair finds it by search and buys it; the seller claims the proceeds minus the tax
  from the mail; the fee and the tax show in the ledger; an unsold listing returns by mail at expiry.

**WP-M4-8 Shop stalls (spec 4.5; decision 7).**
- **Data.** `StallSettings` (duration, listings per stall, one per character, the fee type), `MarkerKind.Stall`
  with `stall` markers in the city greybox (exported by `UnityExporter`; the committed `data/world/m1`), `HudLayout`
  `stall_window`, `stall_prompt`, reason codes `StallPlaceTaken`, `StallOwnAlready`, `StallNotYours`, `StallClosed`.
- **Server.** `ShopStall` entity (`EntityKind.ShopStall = 8`) on a stall marker, persisted in `shops` (owner, place,
  opened, closes, name); open and close through an interaction on the place (the pickup prompt's range rule, no NPC);
  its listings through `ListingService` with `kind = stall`; buying through the same `Buy`; the stall closes at its
  time or by its owner and returns the unsold items by mail. Stalls are entities in the snapshots (interest, caps).
- **Events.** `StallOpened`, `StallClosed`.
- **Migration V0020** `shops`.
- **Protocol 46**: `StallRequest` (open, close, browse), `StallState` (the stall's listings for a browser), the
  stall's entity in the snapshot with its owner identity per observer.
- **Client.** The stall as a placeholder prop with the owner's name, the browse window (buyer) and the manage window
  (owner); `ClientStalls`.
- **Bot steps** `stall:open:<item>:<price>`, `buy:stall`; **smoke steps** `open-stall`, `buy-from-stall`.
- **Tests.** Two players opening one place in one tick; a stall surviving a restart; a purchase after the stall closed;
  the owner offline during a sale; the entity count in the 50-bot run.
- **Shares** `ListingService` (WP-M4-7), `MailService`, the greybox with WP-M4-11.
- **Done:** a bot opens a stall with an item, logs out, its pair buys from the stall, and the proceeds wait in the
  seller's mail.

**WP-M4-9 Death loot bags and the stolen mark (spec 4.3 R-4.3-03, 4.4 R-4.4-01, 2.3; decision 8).**
- **Data.** `OwnerWindows` (owner window per region type or one row), `BagExpiry`, `RegionDef`/`RegionDefaults`
  `deathDrop` field if missing (on in the rings, off in the city), `HudLayout` `loot_bag_window`, `MinimapMarkerDef`
  source `ownLootBag`, `UnlockSchedule` prompt "your bag dropped", reason codes `BagNotFound`, `BagLocked`,
  `BagExpired`, `BagEmpty`.
- **Server.** `src/GameServer/Items/DeathLootService` consumes `Died` (idempotent per event): when the region's flag
  is on, the whole Normal bag moves into a persisted `LootBag` entity (`EntityKind.LootBag = 5`) at the death spot
  **in one class 1 transaction** (the character's save with the `loot_bags` and `loot_bag_items` rows, reason
  `DeathDrop`); the bag is reloaded at start. Pickup (`LootBagRequest`, phase 1, entity id order): the owner inside
  the window takes all (what fits; the rest by mail); a party member inside the window sends to the owner by mail
  (decision 8); after the window anyone takes items one by one or all, and a non-owner's take **sets `stolen`,
  `originalOwnerId` and `stolenAt`** on the instances and stacks (R-4.4-01; stolen stacks never merge with normal
  ones; `TODO(M5)` honor). Expiry destroys the content (reason `BagExpired`, a sink) in a world job. The outpost sale
  refuses stolen items (R-4.4-02; the check exists for non-tradable and gains stolen).
- **Events.** `LootBagCreated`, `LootBagPickedUp {bagId, characterId, owner, stolen}`, `LootBagExpired`,
  `ItemStolen` (8.1).
- **Migration V0021** `loot_bags` (owner, region, position, created, owner window until, expires, state) and
  `loot_bag_items` (instance snapshot or stack).
- **Protocol 47**: `LootBagRequest` (open, take one, take all), `LootBagContent`, `LootBagResult`; the bag entity in
  the snapshot.
- **Client.** The bag as a placeholder prop, the bag window (take, take all), the minimap's own-bag marker replacing
  the death spot (9.12 `TODO(M4)`), the inventory's stolen icon (9.13).
- **Bot steps** `die` (the bot lets a monster kill it) and `recoverBag`; **smoke step** `die-and-recover` and
  `loot-bag-stolen` (the partner bot takes from the player's bag after the window).
- **Tests.** The four tests on the death transaction (a double `Died`, a crash between the bag row and the bag
  write: both or neither; a restart reloads the bag), the owner and the stranger picking in one tick, the window and
  expiry as pure functions, the stolen mark set and the stolen stack refusing to merge, the sale refusal.
- **Shares** `MailService`, `IPartyQuery`; the stolen fields with M5.
- **Done:** a bot dies in ring 2, its Normal bag lies as a bag that survives a server restart; the bot recovers it
  inside the window; a second bag taken by its pair after the window carries the stolen mark and cannot be sold at the
  outpost; a third bag expires and its items are counted as destroyed.

### Wave 2: contracts, the dashboard page, HUD v4 and the loop end to end
**WP-M4-10 Contracts and portfolio (spec 5.4; decisions 10 and 11).**
- **Data.** `ContractSettings` (deadline bounds, cancel cost percent, minimum value, per-pair cap), `NpcDef`
  `m1.contractBoard` with `NpcInteraction.Contracts = 9` and a marker in the city, `HudLayout` `contract_window`,
  `contract_board`, the C shortcut button with its badge, `UnlockSchedule` row, reason codes `ContractNotFound`,
  `ContractStateInvalid`, `ContractValueTooLow`, `ContractPairCap`, `ContractNotTradable`, `ContractJobRequired`,
  `ContractDeadlineInvalid`, `ContractNotAtStation`, `ContractOwn`.
- **Server.** `src/GameServer/Contracts/ContractService`, **the only writer of contracts**: the state machine
  `Proposed → Accepted → InProgress → Completed | Expired | Cancelled | Failed`; `Propose` holds the customer's gold
  and materials in escrow (one transaction with the `contracts` row); `Accept` at the board by a crafter holding the
  job and level; the forge session at a station with **`ForgeSession.Owner = contract` and `OutputRecipient =
  customer`**, its inputs from an `IForgeInputSource` over the escrow (the customer's) and the crafter's reservation
  (the crafter's), `ForgeService` taking the source instead of the bag; `Complete` at the finish: the output to the
  customer (bag or mail), the gold to the crafter minus the fee through `FeeService`, the counters, one transaction
  (the crafter's save with the escrow release and the mail row); `Cancel` and expiry return everything to the customer
  by mail, the crafter's cancel after start pays the cost (a sink). `Portfolio` counters from the events; showcase
  rows through `PortfolioService` (pin, unpin, hide).
- **Events.** `ContractProposed`, `ContractAccepted`, `ContractCompleted`, `ContractExpired`, `ContractCancelled`
  (8.1), `PortfolioChanged`.
- **Migration V0022** `contracts` (customer, crafter, action kind, recipe or mold, materials from escrow, gold,
  deadline, state, timestamps, op keys), `portfolio_counters`, `portfolio_showcase`.
- **Protocol 48**: `ContractBoard` (a page of proposals), `ContractRequest` (propose, accept, cancel, start),
  `ContractState` (the player's contracts), `ContractResult`, `PortfolioRequest`/`PortfolioState`; `ForgeStart` gains
  the contract id (appended).
- **Client.** The contract board window at the NPC, the C window (mine as customer and crafter, badge), the propose
  form (job action, materials from the inventory window, gold, deadline), the forge window's contract banner, the
  portfolio tab in the Jobs window; `ClientContracts`.
- **Bot steps** `contract:propose:<recipe>` and `contract:craft` (the pair: one customer, one crafter); **smoke
  step** `contract-sword` (the partner bot proposes; the player crafts).
- **Tests.** The four tests on propose, accept, complete, cancel and expiry; a complete racing a cancel; a crash
  between the output and the payment (both or neither); a forge session that loses its contract (disconnect, kick)
  leaving everything in escrow; the cancel cost and the per-pair cap as pure functions; conservation.
- **Shares** `EscrowService`, `FeeService`, `MailService`, `ForgeService` (WP-M3-6's owner and recipient fields).
- **Done:** a bot proposes a sword contract with its ore and gold; its pair accepts at the board, forges at the city
  forge, and the sword lands in the customer's bag while the crafter's gold rose by the payment minus the fee; an
  unaccepted proposal expires and returns.

**WP-M4-11 HUD and menus v4 (spec 9.3, 9.12, 9.13, 12.3; decisions 12, 14 and 16).**
- Everything of M4 presented and unlocked: the party frame and the chat tabs polished, the trade, mail, auction, stall,
  bag and contract windows in one pass of the 9.13 pattern, the M, C and SO buttons with badges, the social window
  (friends list, mute and block if decision 16 takes them), the `UnlockSchedule` rows and the tutorial prompts of
  12.3 for party, trade, auction, stalls and contracts, the inventory's stolen icon and the trade and listing refusals
  as toasts; `ReasonCodeStrings` for every new code; the EditMode suite over the new rows.
- No server change except friends and mute/block if taken (`friends`, `blocks` tables, V0023, a small writer in
  `Social`).
- **Smoke steps** `window-places` extended to the new windows; `shortcut-row` to the new buttons.
- **Done:** every M4 window opens from its button or hotkey at its unlock level, the badges count, and the smoke
  harness walks them.

**WP-M4-12 Bots in pairs, the QA partner bot, the `m4-exit` run and the report (spec 11.5, 12.5).**
- **Bots.** Pair scenarios: `BotScenario` gains `pairs` (bot n with bot n+1) and the steps that address the pair
  (`party`, `trade`, `list`/`buy`, `stall`, `contract`); `m4-loop` (pairs play the loop: party up, hunt, roll, trade
  the surplus, list, buy, stall, a contract per round) and `m4-exit` (two bots through section 1's run, done / not
  yet / failed per entry, `tools/load/Run-M4Exit.ps1`). The 50-bot `m2-zone` run gains stalls and bags.
- **QA.** `Run-QaSmoke.ps1 -Partner`: a BotClient relay plays the second player for the pair steps (`party-up`,
  `trade-item`, `buy-listing`, `buy-from-stall`, `loot-bag-stolen`, `contract-sword`); seeds through `DbMigrate --dev`
  (`--give`, `--mail`), never raw SQL.
- **Report.** `report --kind economy` gains the M4 section: fees by type, trades and their value, listings created,
  sold, cancelled and expired with the taxes, stalls, bags created, recovered, stolen from and expired with the
  destroyed value, contracts by outcome, party rolls per party and member, mail sent, claimed and expired; the
  reasons `Trade`, `ListingPurchase`, `ListingProceeds`, `Fee`, `MailClaim`, `MailExpired`, `EscrowHold`,
  `EscrowRelease`, `DeathDrop`, `BagExpired`, `ContractCancel` listed with the existing name test.
- **CI.** The concurrency suite of section 1 as its own test category run on every push (`Category=NoDuplication`);
  `bot-smoke` plays one pair.
- Grows with each wave from a skeleton in wave 0 (the pair plumbing and the report's empty section).
- **Done:** `Run-M4Exit.ps1` passes on main; the dashboard shows the run's totals; the smoke with the partner bot
  passes for all four classes.

### Continuous
- Section 3a of this plan after every merge, QA round and play session.
- Spec 8.1, 8.2 and 8.5 in the PR that adds the event, transaction or table; the decision log with every user answer.
- The play stack rebuilt after every server or table merge; the user asked to play after every wave.
- The `TODO(M5)` list for the honor effects of the stolen mark, the black market and the fence, kept in the spec at the
  place of each.

## 7. Dependencies and merge order
- **Wave 0 WPs are independent.** Their shared surfaces:
  - WP-M4-1 owns `FeeService` and the `Fee` reason;
  - WP-M4-2 owns `IPartyQuery` and the chat channels;
  - WP-M4-3 owns `MailService.Send` and `Claim`;
  - WP-M4-4 owns the two-character transaction, `EscrowService` and `transfer_ops`;
  - WP-M4-5 owns the snapshots table and the dashboard endpoint.

  Until WP-M4-3 merges, WP-M4-4's "what does not fit goes by mail" uses a test double; whichever merges second wires
  them. WP-M4-1's `Apply` runs inside a caller's operation: its test caller is the outpost sale on a branch flag,
  removed when WP-M4-7 merges.
- **Wave 1 needs wave 0 merged:**
  - WP-M4-6 needs the party (2) and mail (3);
  - WP-M4-7 needs fees (1), escrow and the transfer path (4), mail (3);
  - WP-M4-8 needs WP-M4-7's `ListingService` and may start when its PR is open, against its branch, rebasing at merge;
  - WP-M4-9 needs mail (3) and the party (2).
- **Wave 2 needs wave 1:**
  - WP-M4-10 needs escrow (4), fees (1), mail (3) and the forge's owner and recipient (M3);
  - WP-M4-11 comes last, since it presents everything;
  - WP-M4-12 grows with each wave from a skeleton started with wave 0.
- **Protocol.** Every WP that adds a message or a snapshot field takes **main+1 at merge time**; the numbers in
  section 6 are proposals in wave order from protocol 41 and reliable id 56. When two PRs take the same number, the
  orchestrator fixes the order and the later PR renumbers and rebases. Reliable messages stay append-only MemoryPack
  with a `ReliableMessageCaps` row and an `InputRouter` route; the stall and bag entities ride the bit-packed snapshot.
- **Migrations.** Append only, one per WP that needs a table, from V0016, on the branch's own databases
  (`--db-suffix`). `MigrationRunner` applies gaps and late lower versions, but a WP that merges after another took its
  number renames to main+1 before merge. Never edit an applied migration.
- **Telemetry kinds** from 104 in merge order; **`InventoryReason`** from 20 and **`NpcInteraction`** from 8, appended
  in merge order (the report's name test fails a reason the report does not list).
- **Tables.** New fields on shared tables (`ItemDef`, `NpcDef`, `RegionDef`, `ChatSettings`, `HudLayout`,
  `UnlockSchedule`, `BotScenario`, `BotBehaviour`, `EconomySimScenario`) are `[Optional]`, since other branches add
  rows to them.

## 8. Risks to watch
- **Duplication through a transfer.** Every M4 operation moves value between two owners; the exit forbids any
  duplication. The two-character transaction (WP-M4-4) is the one path, escrow the one place outside a character, and
  every settlement writes the debit, the credit (or its mail) and the state change in one transaction, keyed by one
  operation key. The concurrency suite runs in CI from wave 0; the orchestrator merges nothing value-moving without
  its four tests.
- **One writer per character across the network.** A sale to an offline seller must not write the seller's row: the
  proceeds go by mail (decision 4). A WP that wants to credit a character it does not own is wrong.
- **The barrier by inclusion over two characters.** Both pending class 2 batches ride the transfer; a picked-up item
  can never commit after it was traded.
- **Mail as a loophole.** Mail holds value outside bags: caps, expiry with destruction, no player-to-player mail in
  M4, and every mail row keyed by its operation.
- **Fee bypass.** Every listing, sale and contract goes through `FeeService.Apply` inside the operation; a test asserts
  a fee row per settlement.
- **Party loot griefing.** The lock to the party, eligibility by participation, the debt, and no master looter in
  M4; the leader cannot take from a member's roll.
- **Death bag abuse** (alts "stealing" to launder later, M5): the mark records the owner and time; honor and the
  black market come with M5; the field zone's flag is on only in the rings.
- **Auction search load.** Indexed SQL on the persistence worker, keyset pagination, a per-player request rate limit,
  a budget measured in the WP against 10,000 rows; never a query on the tick thread.
- **Entity caps.** Stalls and bags are entities: caps per city and per world, interest radius, the 50-bot run with
  them loaded.
- **Lost contracts.** A disconnect, kick or shutdown during a contract's forge session leaves everything in escrow,
  the contract back to `Accepted`; the deadline runs on.
- **Scope creep from M5, M7 and M8.** Honor, the black market, laundering, party finder, matchmaking, guild
  treasuries and owner fee shares, player mail, bids, pets, the Merchant job: each gets an interface and a
  `TODO(M5)`/`TODO(M7)`/`TODO(M8)`/`TODO(open)`, not the system.
- **Two clients in play.** The harness drives one client; the partner bot must land in wave 0 (the WP-M4-12 skeleton)
  or the pair steps cannot be smoked. The team playtest protocol of 12.4 K is the real check.
- **Souls-like feel.** A trade or a mail claim never interrupts combat (refused in combat); the bag recovery is a run
  back under danger; check in the 100 and 200 ms play.

## 9. Handover checklist for the M4 orchestrator
1. Read:
   - `CLAUDE.md`, this plan, and `docs/plans/m3-jobs-crafting-plan.md` (process history, section 3a, the M3 decisions
     14-17 on the controls);
   - the memory files (`english-only`, `orchestrator-roles`, `placeholder-animations`, `own-dev-databases`,
     `play-stack-smoke-permission`, `unity-editmode-for-data-rows`, `unity-batchmode-short-path`,
     `auto-merge-all-prs`);
   - the role files in `.claude/agents/`;
   - spec 4.2-4.5, 5.4, 6.5, 7.1, 7.2, 7.4, 12 and 13.
2. Verify main: `git log`, open PRs, CI on main, `docker compose up -d --build --wait`, `dotnet test Mmorpg.sln`, one
   smoke run through QA. Write the first dated entry of section 3a.
3. Confirm with the user the acceptance checks of section 1 and that balance stays deferred.
4. Put decisions 1 to 5 of section 5 to the user in one message, with the recommendations. Record the answers in the
   spec and the decision log before briefing wave 0.
5. Brief wave 0 as five sessions in plan mode (Opus 5.5 high, own worktrees): WP-M4-1, -2, -3, -4, -5. Each brief
   carries: the spec sections of section 4, the WP text of section 6, the interfaces it shares, the standing rules of
   section 2, the proposed protocol and migration numbers, and "done" as written. Start WP-M4-12's skeleton (the pair
   plumbing, the partner bot) with them.
6. Before wave 1, put decisions 6 to 9 to the user. Brief WP-M4-6, -7, -8 and -9 (WP-M4-8 may start against
   WP-M4-7's open PR).
7. Before wave 2, put decisions 10 to 17. Brief WP-M4-10, -11 and the rest of -12.
8. After each wave: QA of main, fixers for findings, rebuild the play stack, ask the user to play (with a partner
   bot or a second client), update section 3a.
9. When the `m4-exit` run passes on main with QA's evidence, ask the user to close M4 and write the M5 handover plan
   in the same shape.
10. Shared notes folders (hand-over notes and QA evidence per session): the M2 orchestrator's
    `C:\Users\Matheus\AppData\Local\Claude-Data\claude_pzs8sxrjxfjjc\side-session-notes\local_e83ffa86-5a4b-4fcf-bdb1-e3f42cd0c7bd\`
    and the M3 orchestrator's
    `C:\Users\Matheus\AppData\Local\Claude-Data\claude_pzs8sxrjxfjjc\side-session-notes\local_f4cc6e62-1584-4003-928c-e3e8e9fb3f51\`
    (`qa-m3-wave0.md`, `qa-m3-wave1.md`, `qa-m3-wave2.md` with `qa-m3-wave2-evidence/`, `m3-wave2-fixer.md`,
    `m3-wave2-fixer-3.md`).
