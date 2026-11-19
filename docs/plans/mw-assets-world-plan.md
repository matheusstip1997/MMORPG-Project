# Plan: Milestone MW, assets and world (handover to the MW orchestrator)

Prepared 2026-11-19 by the M4 orchestrator at the user's request ("Close M4 now"), when Milestone 4 was closed (main
code at the merge of PR #274, 55f9a59e; the QA of main after wave 2 is recorded in
`docs/plans/m4-player-economy-plan.md` section 3a, thirty-third entry). It is written for a fresh orchestrator
session. Read it together with:
- `CLAUDE.md`;
- `docs/spec/00-index.md`;
- `docs/spec/12-delivery-plan-milestones.md` (12.2 the milestone rows, 12.9 the main map content rules);
- `docs/spec/09-client-ui-hud-login.md` section 9.14 (the art rules R-9.14-01 to R-9.14-08) and 9.10 (the character
  creation contract);
- `docs/spec/11-*.md` section 11.6 (sizes and world building, R-11.6-01 to R-11.6-06);
- the design files in `docs/design/`: `world-map.md` (+ `world-map.svg`), `open-world-content.md`, `city-layouts.md`,
  `biome-sheets.md`, `art-direction.md`, `class-art-style.md`, `class-sheets/*.png`, the reference boards;
- `docs/plans/m4-player-economy-plan.md` (process history, section 3a).

**This milestone is different from M1 to M4.** Those built systems; this one builds the look and the place. Most of
its volume is art that people and tools make, not code. Sessions build the pipeline, the checks, the shaders, the
world layout and the two screens; the team makes or directs the art. **Everything in this plan is a proposal until
the user confirms it in the first session** (section 5): the user closed M4 and named the milestone, and answered
four questions; the exit criteria, the split of the work and the build order are not decided yet.

## 1. Goal and exit criteria (spec 12.2)

**Goal (user direction 2026-11-14, answers 2026-11-19).** System work stops. The milestone adds the assets and builds
the world itself, before M5 (law and PK):
- **the full main map** (user answer: "Full main map"), built to the rules of spec 12.9;
- **the assets**, in the user's words: "We may need to use draw-to-3d tools and other ai tools for VFX. Icons can be
  handled by artist". The source of animations and of audio, which tools, and who draws the inputs are **Open**;
- **visible equipment** (R-9.14-08) and **the character creation screen** (9.10; M4 decision 15 left it for this
  milestone, to be built with the character models).

The deferred work of M3 and M4 stays at the end of the project (user answer), and the numbers M5 to M9 do not move
(user answer): MW sits between M4 and M5.

**Exit criteria (the 12.2 row, proposed, to confirm).**
1. A new character can cross the whole main map, with every region, settlement, road, camp and node of 12.9 in
   place and no M1 placeholder art in view.
2. The four root classes, their gear and the monsters in play wear their own models and animations.
3. The 50-bot zone run stays within the tick budget on the full map, and the client holds a frame budget set in
   this plan (section 5, decision 9: the number and the reference machine are the user's).
4. Every third-party asset and tool output is listed in `THIRD_PARTY_LICENSES.md`.

**Acceptance checks (proposed; the MW orchestrator confirms them with the user, as M4's section 1 was).**
- A scripted walk (bot or smoke harness) from each start city to the two others by road and bridge, with no stuck
  walker, and a screenshot set per region.
- `DataValidator` passes on the main world package, with the world checks of 12.9 that can be checked by a program
  (road and bridge widths, cliff heights, markers per settlement tier, node and camp counts per region, no gate
  leak); what cannot be checked by a program is on a per-region review sheet the user signs off.
- A placeholder audit: an editor check lists every scene object and every `ClassDef`, `MonsterDef`, `ItemDef` in
  play whose look still comes from an M1 placeholder pack; the list is empty for the main map, or each line is an
  exception the user accepted.
- Equipment shows piece by piece on the own character and on other players' characters (R-9.14-08), smoke steps
  with screenshots.
- A character can be made on the creation screen and enters the world in a start city.
- The 50-bot zone run on the main world, tick p99 within budget; a client frame time capture on the reference
  machine in the heaviest city and the heaviest field region.
- The asset budget check (section 6, WP-MW-2) passes in CI.
- Every asset folder under `unity/Assets/ThirdParty/` and every tool has a `THIRD_PARTY_LICENSES.md` row; a test
  fails when one is missing.
- The M4 exit still holds: `m4-exit`, `NoDuplication`, the full suite, EditMode (the dev world `m1` stays, see
  section 3).

## 2. Roles and process (carried over from M4; see `docs/plans/m4-player-economy-plan.md` section 2)
- **Orchestrator.** Splits the work into packages, briefs sessions, reviews every PR at high effort with the
  code-review skill and the architecture rules of `CLAUDE.md`, **merges every PR** after its review and green CI
  on the reviewed head (memory `auto-merge-all-prs`, `verify-pr-after-push`), assigns protocol, reliable id,
  migration and telemetry numbers in merge order, rebuilds the play stack after a merge that changes server code
  or a table row the server reads, runs QA after each wave, and keeps this plan's section 3a current through docs
  PRs.
- **WP sessions.** One per package, own worktree and branch, **plan mode first** (a file-level plan the user
  approves), Opus 5.5 at high effort. A package too large for one PR ships in parts.
- **Fixer and QA.** Role files `.claude/agents/fixer.md` and `.claude/agents/qa.md`; visible sessions. QA findings go
  to a fixer; design calls go to the user.
- **Design sessions** may edit only `docs/design/**`; the orchestrator writes their decisions into the spec and the
  decision log.
- **New in MW: the art comes from outside the sessions.**
  - A session never invents a look. It builds to `art-direction.md`, the biome sheets, the city layouts and the
    class sheets; a point those leave Open goes to the user.
  - A session never adds a tool, a package, a model file or an asset pack on its own: "ask before adding a
    dependency" (`CLAUDE.md`), and every one gets its `THIRD_PARTY_LICENSES.md` row in the same change.
  - A session never sends the team's drawings, names or project details to an outside service. If a tool is a
    hosted service, the person who owns the account uses it; the session works with the files it is given.
  - Asset hand-over: the team drops source files in an agreed inbox folder (section 5, decision 5); a session
    imports, checks them against the budgets and wires them by data. **A swap of a look is a data change** (memory
    `placeholder-animations`): registries and definition rows, never code.
  - **Visuals follow the action timings in config, never the other way round** (`CLAUDE.md`): an animation is cut or
    retimed to its `ActionDef`, and a mismatch is a finding against the asset.
  - **Art never changes collision silently.** Collision comes from the exported world package; a PR that changes a
    scene shows the package diff, and cities stay on the grid (R-11.6-06).
- **Balance stays deferred to the end of the project.** Monster stats, node yields and camp sizes on the new map are
  placeholder rows; no package tunes.
- **Standing rules** (memory files):
  - Everything is written in English.
  - Branches with a new migration use their own databases (`DbMigrate --dev --db-suffix <x>`,
    `Run-QaSmoke.ps1 -DbSuffix <x>`), never the shared `mmorpg_realm_1`.
  - Sessions may stop the user's composed play stack for a smoke run with exactly
    `docker compose stop authapi gameserver adminpanel` in `C:\Users\Matheus\MMORPG-Project`, telling the
    orchestrator first, and must restore it with exactly `docker compose up -d --wait`; the orchestrator reports
    each stop and each return to the user and checks the return with `docker compose ps`.
  - Run the Unity EditMode suite before a PR that changes Unity code or table rows Unity reads, from a short
    worktree path, never in the play checkout; `git lfs pull` in a fresh worktree. **In MW nearly every PR touches
    Unity**, so this applies to nearly every PR.
  - Never touch `infra/`, secrets, AWS or Steam; the dev login `dev:<n>` only.
  - No Python or Node on this machine: scripts in PowerShell 5.1, pictures viewed through headless Edge (memory
    `no-python-node-render-with-edge`).
  - Commits and PR bodies end with the attribution lines the session's own instructions give.
  - A known CI flake is rerun once and handed to the next fixer round. Never force-push. Deleting remote branches
    and archiving sessions are the user's calls.
- **Working agreement.** Whoever merges later takes protocol main+1 and the next migration number and rebases. A PR
  that changes a table schema also updates the validator and the spec. Large binaries go through Git LFS. The
  user's play sessions drive a findings loop.

## 3a. Where we are (kept by the MW orchestrator)
_One dated entry per milestone event: decisions answered, waves briefed, PRs merged in order with their protocol and
migration numbers, QA results, carry-overs._

- **2026-11-19, first entry: MW started; main verified at f507db15.**
  - **Main:** the merge of docs #277 (this plan, the M4 close in spec 12.2 and the decision log), f507db15. The code
    is that of 55f9a59e (#274): #275 to #277 are docs only. Numbers as the M4 close left them (section 3): protocol
    55, reliable ids up to 94, realm V0027, telemetry event kinds up to 130. No open PRs.
  - **CI:** green on f507db15 and on the three merges before it (build-and-test, bot-smoke, compose-stack), each on
    the first attempt.
  - **Checked in the orchestrator's worktree on f507db15:** `dotnet test Mmorpg.sln` 5,307 of 5,307 (Shared 2,862,
    GameServer 2,323, AuthApi 84, AdminPanel 38; the integration tests against the docker-compose PostgreSQL and
    Redis); `DataValidator` OK (157 tables; the world `m1` 880 x 800 cells, 6 regions, 129 markers).
  - **Play stack** in `C:\Users\Matheus\MMORPG-Project`: all five containers healthy, the checkout at 55f9a59e. Not
    rebuilt and not pulled: nothing but docs merged since.
  - **Not run again:** the QA smoke of checklist step 2. The QA of main ran today on 55f9a59e, the same code (the M4
    plan's thirty-third entry, `qa-m4-wave2.md`); Unity EditMode was 957 of 959 there.
  - **The M4 fixer** (QA findings F1 to F5, the harness backlog, the live gaps; section 3 "Carried from M4") is
    still working and has no PR yet. Its PR is reviewed and merged here when it opens.
  - **Read:** the checklist's step 1 (the memory files, the role files, spec 2.3, 9.10, 9.14, 10.4, 11.6, 12.2,
    12.9, the design files, the M4 plan's section 3a).
  - Next: decision 1 (the exit criteria and the acceptance checks of section 1) and decisions 2 to 10 of section 5
    to the user in one message; their answers into the spec and the decision log through a docs PR; only then the
    wave 0 briefs (WP-MW-1 to WP-MW-4, and WP-MW-5 when decisions 8 and 18 are in), each in plan mode on Opus 5.5 at
    high effort in its own worktree.

## 3. Where M4 left things (main code at PR #274, 55f9a59e, 2026-11-19)

**Numbers on main:** protocol 55, reliable ids up to 94, realm V0027 (global V0001), telemetry event kinds up to 130,
host counter kinds up to 216, `InventoryReason` up to 31, `NpcInteraction` up to 10, `ClientReasonCode` up to 15,
`MailKind` up to 9, `EscrowTargetKind` up to 4.

**Evidence at the close** (notes folder of section 9, `qa-m4-wave2.md` and `qa-m4-wave2-evidence/`): `NoDuplication`
190/190, the full suite 5,307/5,307, EditMode 957/959, migrations V0001 to V0027, `m4-exit` 68/68 (tick p99
0.34 ms), `m4-contract` 14/14, `m4-zone` tick p99 2.06 ms, the dashboard equal to the bot report.

**What exists that MW builds on.**
- **The world pipeline.** The exporter is editor code in `unity/Assets/Game/WorldAuthoring` (batch
  `Game.WorldAuthoring.Editor.WorldExporterBatch.Export`); formats and collision in `src/Shared/World`; server
  loading in `src/GameServer/World/Map` (`MMORPG_WORLD_DIR`); the client reads regions and markers through
  `WorldPackage.TryLoadIndex`. Greybox is built by editor code (`WorldAuthoring/Editor/FieldZoneGreybox`,
  `CityStallsGreybox`), which is the pattern for building the map from the design files.
- **The only world today is the dev world `data/world/m1`** (format version 3, 50 cm cells, agent radius 40 cm,
  880 x 800 cells, one source scene `Assets/Scenes/M1Sample.unity`): the city, the M1 combat area and `field1`. By
  designer decision it **stays the test and dev zone and is not part of the main map** (`world-map.md`). Every
  test, bot scenario and smoke step runs on it.
- **The main map is a drawing only** (`world-map.svg`, 1 unit = 2.5 m): a frame of 3.6 x 2.5 km, about 4.25 km2 of
  land in two continents and an island, 20 biomes, 3 main cities (C1 Green vale, C2 Red sands, C3 Reed marsh),
  6 medium cities (M6 lawless, in the Ember caldera), 11 small towns, three bridges. At 50 cm cells the frame is
  7,200 x 5,000 cells, about 50 times the dev world's cell count. Nothing of it is built.
- **No terrain streaming.** `unity/Assets/Game/Streaming/WorldSceneLoader` loads one scene and says so: "streaming
  in chunks comes with the main map" (R-2.3-04). A player build does not copy `data/world` into `StreamingAssets`
  yet (`ClientWorld`, a `TODO(build)`).
- **Level-gated regions** (`RegionDef.minLevel`, `RegionGates`, world check `GateLeak`) and the tutorial schedule
  exist (WP-M2-10); region flags (`RegionDefaults`: `normalCity`, `lawlessCity`, `deathDrop` and the rest) exist.
- **Markers the map must carry** exist as kinds: `monsterSpawn`, `npc` (outposts, vendors, job master, forge and
  bench, auctioneer, mailbox, contract board), `stall`, resource nodes. Camp names follow
  `field1.spawn.<type>.<camp><n>`.
- **Placeholder art** under `unity/Assets/ThirdParty/` (Git LFS, CC0): `KayKitAdventurers`, `QuaterniusCuteMonsters`,
  `QuaterniusUAL`, `QuaterniusUAL2` (the animation libraries), `VRoidSamples` (VRM avatars through UniVRM, URP
  MToon10), `Fonts`. Looks are bound by data: `ItemIconRegistry`, `BuffIconRegistry`, `MinimapIconRegistry`,
  `BystialCardArt`, `EntityVisualDef` (the look of classes and monsters).
- **`unity/Assets/Game/VFX` and `unity/Assets/Game/Audio` are empty** (an assembly reference each). There is no
  outline, no see-through fade of what hides the character and no roof hiding in the game yet (R-9.14-02,
  R-9.14-05 are rules without code).
- **Character creation** is a placeholder screen (name and root class); the contract is fixed (9.10:
  `CreateCharacterRequest` with an opaque versioned `appearance`, version 0, zero bytes). **Nothing of visible
  equipment is built**, and what the server sends other clients about a character's gear is not specified (10.4).
- **The art direction is written:** `art-direction.md` (decisions A1 to A36, B1 to B7), 20 biome sheets, the city
  layouts and kits, the open world content rules, five class sheets (`class-sheets/*.png`, made on the designer's
  computer with ComfyUI and FLUX.2 klein 4B, Apache 2.0, nothing sent to a service; `class-art-style.md`). The
  sheets are reference pictures for artists, not game assets.

**Carried from M4** (none blocks MW; the list is also in the M4 plan's thirty-fourth entry).
- **A fixer session was running at the close** (started by the user on 2026-11-19): the QA's harness findings F1
  (walks stuck at the city gateway corners), F2 (`contract-propose` spends the seeded ore), F3 (the bench left open
  after an n/a `socket-gem`), F4 (`wait-respawn` times the wrong death), F5 (the mail double-claim tests outside
  the `NoDuplication` category); the harness backlog points 8 and 9 (`fixer-m4-harness-backlog.md`); the
  `dev-tools` flake; the `ContractStateInvalid` double submit; smoke coverage for the SO badge, the portfolio tab
  and the stolen mark; a bot leg for the expiry charge. If its PR is still open, the MW orchestrator reviews and
  merges it.
- **Not seen live at the close:** the SO badge, the portfolio tab's content with pin and hide, the stolen mark on
  screen, the expiry charge.
- **Open with the user from M4:** the gateway between the city and the first camps is about one body wide (QA D1;
  for MW this is decision 14); the play test of waves 1 and 2; minimum fee; Merchant discount; trade lock-out;
  party invite slot; part of a stack in a trade; rest spots; lawless city services; pool size against
  `max_connections`; `-Check` in CI; a `CLAUDE.md` line for `tools/art-boards`; the leftover branch
  `claude/wp-m4-6-party-loot-6cc445`; gloves for an Assist glove weapon with a chest item with gloves.
- **`CLAUDE.md` still names M1 as the current milestone.** Changing it is the user's call (decision 19).
- **Everything M3 and M4 deferred** (balance, tuning, the M3 carry-over list) stays at the end of the project.

## 4. Spec sections to read per area (before briefing a WP)

| Area | Read |
|---|---|
| The map as a whole | 2.3 (R-2.3-01 to R-2.3-13), 12.9, `world-map.md`, `world-map.svg` |
| Settlements | R-2.3-07 to R-2.3-09, R-4.2-08, R-4.5-03, R-5.3-07, R-6.3-07, R-6.6-09, `city-layouts.md` |
| Biomes | R-2.3-10, R-2.3-11, R-14.2-08, R-3.4-09, `biome-sheets.md` |
| Roads, camps, places, field bosses | R-2.3-12, R-2.3-13, R-3.1-11, R-2.2-08, `open-world-content.md` |
| Resource nodes | 5.2, R-5.2-05 |
| Rest spots | R-6.3-08 (**Open**, none built) |
| Sizes and world building | 11.6 (R-11.6-01 to R-11.6-06) |
| Style, occlusion, colour, light, outlines, navigation, camera | 9.14 (R-9.14-01 to R-9.14-07), R-2.2-05 |
| Characters and gear | R-9.14-06, R-9.14-08, 4.1 (slots, R-4.1-05 armor per tier), `class-art-style.md` |
| Character creation | 9.10 (screens table, the creation contract), decision log 2026-10-28 (M2 decision 11) |
| Streaming and world loading | R-2.3-03, R-2.3-04, 1.7 (snapshot priority), 11.5 (load tests) |
| Monsters | 3.1, 14.2 (families), `MonsterDef`, `MonsterAiSettings` |
| World export and formats | `src/Shared/World`, `unity/Assets/Game/WorldAuthoring`, the validator's world checks |
| Licences | `THIRD_PARTY_LICENSES.md`, `CLAUDE.md` "Placeholder art" |

## 5. Decisions the user must make (Open; ask before the wave that needs them)

Each is put to the user with the options and, where there is one, a recommendation. **None is decided here.**

### Before wave 0
1. **The exit criteria and acceptance checks of section 1.** Confirm or change.
2. **Where animations and audio come from** (Open in the user's answer). Animations: keep the CC0 libraries
   retargeted onto the new rigs, an animation tool, or hand animation. Audio: CC0 packs, tools, bought packs or
   commissioned work. The answer sets the rig standard of WP-MW-2.
3. **Which draw-to-3D and VFX tools**, and for each: the licence of its output (commercial use), whether it runs on
   a team computer or is a hosted service that receives the team's drawings, and its cost. The class sheets set a
   precedent (local, Apache 2.0, "nothing is sent to a service"); whether MW keeps to that is the user's call.
4. **Who does what in the team:** who draws the inputs, who cleans up tool output (topology, UVs, rig, skin
   weights), who makes icons (the artist, by the user's answer), who signs off a region.
5. **The split between sessions and the team, and the hand-over place.** Recommended: sessions build the pipeline,
   the checks, the shaders, the map layout, the streaming, the two screens and the wiring by data; the team makes
   or directs every asset and drops source files in one inbox folder per kind.
6. **How the main world lives next to the dev world.** The dev world `m1` stays for tests (designer decision). Which
   world the play stack and the client's play scene load; whether the server runs the main map as one world or
   split (`world-map.md` O8); how a dev character reaches either.
7. **The build order inside MW.** `world-map.md` D15 proposes W1 (C1 and its ring), W2 (C2 and the rest of the west
   continent), W3 (C3, the island, the east continent, the bridges), W4 (the Ember caldera and M6), W5
   (territories). Recommended: W1 first as a full vertical slice with final art, then W2 and W3 in parallel.
   **To confirm:** "the full main map" includes the caldera and M6 as places now, with their law rules left to M5
   (decision 15); W5 stays with M8.
8. **The Open list of `art-direction.md` that blocks assets:** the character's edge in the game (A23), the anime
   line-art look in the engine (A24), the outlines of loot and nodes, base clothes (A31), the parts below the waist
   (A32), how many looks per slot and tier (A27), monster heights (A14), element colours (A17), the disguise robe
   (A18, A27), skins (A35), enchant glow (A36), final camera values (A5), large bodies at narrow gaps (B5), the
   slope check (B4), the fade, the style references.
9. **The client frame budget:** the target frame rate and the reference machine (the minimum spec). The exit
   needs a number; there is none in the spec.
10. **Asset budgets** (triangles and texture size per character, monster, prop, building; draw calls per view).
    WP-MW-2 proposes them from measurements; the user confirms.

### Before the world waves
11. **Monsters on the map:** how monster rank maps to regions (`world-map.md` O1, with the exp band rule R-3.6-03);
    which monsters each biome holds (families exist, R-14.2-08) and **how many new monster kinds MW makes**: each
    is a model, a rig, an animation set and data rows, so the count sets the milestone's size.
12. **The Open points of the world design:** small town services (O5), respawn details (O2), rest spots
    (R-6.3-08), final names (O9), wood (O10), dungeons and portal spots (O6), the Open lists of
    `open-world-content.md`, `city-layouts.md` and `biome-sheets.md`.
13. **Start cities and fast travel.** How a new character gets one of the three start cities (a choice on the
    creation screen, or another rule), and whether fast travel between the three (R-2.3-01), which is a system,
    is built in MW or waits.
14. **Gateway and gate widths** (QA D1): the dev city's gateway is about one body wide; the main map's gates follow
    R-11.6-03 or another width.
15. **Places whose systems come later:** the raid gate hall and the arena queue (M7), the guild hall (M8), the
    black market and M6's always-on PvP (M5). Recommended: the buildings and markers now, the services off, each
    with a `TODO(M5)`/`TODO(M7)`/`TODO(M8)`; no system is built.
16. **Day, night and weather** (A4): the rule today is one fixed light per biome (R-9.14-04). Confirm it holds for
    the whole milestone.

### Before the character wave
17. **The creation screen's design:** what a player picks (9.10 leaves `appearance` an opaque blob; the options
    become schema version 1), and how the screen looks.
18. **What other clients learn about a character's gear** (10.4, Open): a look id per shown slot is the smallest
    answer, and the disguise system must still decide what an observer sees (`CLAUDE.md` rule 8,
    `DisplayIdentity`). This adds a protocol number.

### Any time
19. **`CLAUDE.md`'s "Current milestone" line** (it names M1) and a line for the MW pipeline once it exists.
20. **Steam and tool-made content.** The store asks publishers to declare content made with generative tools; the
    current rules are worth reading before the tools are chosen. The sessions never touch Steam; this is a note
    for the user.

## 6. Work packages

A proposal for the MW orchestrator to refine with the user. Each WP lists **Does**, **Needs** (decisions of section
5) and **Done**. Protocol and migration numbers are assigned at merge (main+1).

### Wave 0: foundations (parallel; they make the rest possible and measure the risk first)

- **WP-MW-1, the world at scale.**
  - **Does:** a main world package next to `m1` (its own id and folder under `data/world/`); the exporter over
    several source scenes; the server's load time and memory at 7,200 x 5,000 cells measured and, if needed, the
    grid format extended (a new format version, append only); the validator's world checks at that size; the
    package copied into a player build (`ClientWorld`'s `TODO(build)`); the minimap texture and the region index at
    that size; a bot scenario that walks the main world.
  - **Needs:** decision 6.
  - **Done:** an empty main world of the full frame (land, water, the three bridges as blockers and walkable
    cells, from the drawing) loads on the server and the client within budgets the WP states and measures; the
    50-bot zone run passes on it; every existing test still runs on `m1`.
- **WP-MW-2, the asset pipeline and budgets.**
  - **Does:** folder layout and naming under `unity/Assets/`; import presets per asset kind; the rig standard and
    the retarget path (decision 2); the scale check against the reference character (R-11.6-01: 1.8 m, radius
    40 cm, about 6 heads); a budgets table and an editor check with an EditMode test in CI; the inbox folders and
    an import command; the placeholder audit of section 1; a test that every third-party folder and tool has a
    licence row; Git LFS rules for the new file kinds.
  - **Needs:** decisions 2, 3, 4, 5, 10.
  - **Done:** one character, one monster, one prop and one building go from the inbox to the game through the
    pipeline, pass the checks and replace their placeholders by data only.
- **WP-MW-3, the look in the engine.**
  - **Does:** the character and world shading of R-9.14-01 (toon, the character's edge), outlines on characters and
    interactables only (R-9.14-05), the see-through fade of what hides the own character and roofs off inside
    buildings (R-9.14-02), the saturation limit and the one light and fog preset per biome as data (R-9.14-03,
    R-9.14-04).
  - **Needs:** decisions 8, 16.
  - **Done:** a test scene with the reference character, a monster, a building with an interior and two biome
    presets, with screenshots the user accepts; frame cost measured.
- **WP-MW-4, terrain streaming.**
  - **Does:** the main map as chunks the client loads and unloads around the camera (R-2.3-04), entities never
    waiting for ground; the play scene's loader replaced for the main world, the dev world unchanged.
  - **Needs:** WP-MW-1's package; decision 6.
  - **Done:** a walk across the empty main map with no loading screen and no hitch above the frame budget; memory
    flat over a long walk.
- **WP-MW-5, modular characters and visible equipment** (may start in wave 0, lands with the character wave).
  - **Does:** the base body with base clothes; the shown slots (head, chest, legs, feet, both weapons; R-9.14-08);
    a look id per item through a registry; armor per tier; the own character and other players' characters; what
    the server sends others (decision 18), through `DisplayIdentity`.
  - **Needs:** decisions 8, 18; WP-MW-2's rig standard.
  - **Done:** changing one equipped piece changes what every client shows; smoke steps with screenshots;
    disguise still hides what it must.

### Wave 1: the first slice (W1 of the build order, end to end with final art)

Proves the whole chain on one start city and its ring before the volume starts.
- **WP-MW-6, the settlement and biome kits for W1.** The kit pieces of `city-layouts.md` (E3, E7) for a main city,
  a medium city and a small town, and the biome kits of the W1 biomes (Green vale, Heather moor, Amber woods, Lake
  country, Rime coast), from the team's assets through WP-MW-2, snapped to the grid in cities (R-11.6-06).
- **WP-MW-7, the W1 layout.** Editor builders (the `FieldZoneGreybox` pattern) that place C1 (plan C1, the water
  city), its tutorial area, M1, M3, T1, T2, the roads (6 m), paths (3 m) and the west end of the north bridge
  (5 m), the regions with their flags and level gates, the service NPC markers per tier, the stall places, the
  camps and nodes by the rules of `open-world-content.md`; then the dressing with WP-MW-6's kits. Monster and node
  rows are placeholders.
- **WP-MW-8, the W1 monsters and the root classes.** The models, rigs and animation sets of the four root classes
  and of the monster kinds W1 needs (decision 11), bound by data; animations cut to the action timings.
- **Done for the wave:** the acceptance checks of section 1, on W1 only; a play test by the user; the measured
  cost per region and per asset kind, which sizes waves 2 and 3.

### Wave 2: the rest of the map (W2 and W3 in parallel, then the caldera)
- **WP-MW-9 (W2):** C2 (plan C2, the caravan bazaar), its tutorial area, M2, T3 to T5, Old forest, Fungal hollow,
  Dry steppe, Red sands, Bone canyons.
- **WP-MW-10 (W3):** C3 (plan C3, the classic medieval city), its tutorial area, the island with M4, the east
  continent with M5 and T6 to T11, the three bridges.
- **WP-MW-11 (the caldera):** the Ember caldera and M6 as places (decisions 7 and 15); no law system.
- Each brings its kits, its monsters and its nodes, as wave 1 did. Two layout packages never edit the same scene
  or the same chunk; shared kits merge first.

### Wave 3: the rest of the assets, the screens and the exit
- **WP-MW-12, the character creation screen** (decision 17), with the appearance schema version 1 and the start
  city rule (decision 13).
- **WP-MW-13, VFX and audio.** The effect and sound hooks by data (`unity/Assets/Game/VFX`, `Audio`): skill and hit
  effects, attack warnings (R-3.3-04), loot glow, ambience per biome, UI sounds; assets from the tools and sources
  of decisions 2 and 3.
- **WP-MW-14, icons.** The artist's icons through the registries (`ItemIconRegistry`, `BuffIconRegistry`,
  `MinimapIconRegistry`, `BystialCardArt`), with a check that no row still points at a placeholder.
- **WP-MW-15, the performance pass and the exit run.** The frame capture on the reference machine, the 50-bot
  run on the full map, the placeholder audit at zero, the licence check, the walk between the three cities.

### Continuous
- QA of main after each wave; fixers for findings; the play stack rebuilt after server merges; the per-region
  review sheet; section 3a.

## 7. Dependencies and merge order
- **WP-MW-1 before everything that touches the main world** (WP-MW-4, every layout package). **WP-MW-2 before every
  asset.** WP-MW-3 before the kits are judged, since the shading changes how an asset reads.
- **Wave 1 before waves 2 and 3.** Its measured cost per region decides how the rest is split and whether the scope
  of "the full main map" in one milestone needs a second look with the user.
- **Scenes and chunks are binary-ish files that do not merge.** One writer per scene or chunk at a time; layout by
  editor builders (code that merges) wherever possible, with the built scene regenerated, not hand-merged.
- **Protocol:** WP-MW-5 (the gear looks) and perhaps WP-MW-12 (the appearance schema) take main+1 at merge. No
  realm migration is expected except for the appearance blob if decision 17 needs one.
- **The world package is committed data.** A PR that changes it shows the validator's report and the package diff;
  two PRs that change the same package rebase one after the other.
- **Git LFS.** The repository grows by the assets; the LFS quota and clone time are checked at the end of wave 0
  and of wave 1.

## 8. Risks to watch
- **Volume against a team of five.** Twenty biomes, twenty settlements, the gear of four classes across tiers,
  the monsters. Wave 1 exists to measure the real cost before the rest is promised.
- **Tool output is not a game asset.** Draw-to-3D output is usually heavy, with poor topology, no rig and uneven
  style; someone cleans it up (decision 4), and the budget check of WP-MW-2 refuses what is not ready.
- **Licences and provenance.** Every tool and model has terms for commercial use of its output; some hosted tools
  claim rights or train on uploads. Nothing enters the repository without its row; models marked non-commercial
  are named in `class-art-style.md` as ones to avoid.
- **One style from many sources.** The class sheets, the artist's icons and tool-made models must read as one game:
  the shading of WP-MW-3 and the user's sign-off per region are the guard.
- **The world at 50 times the size.** Server load time and memory, the grid format, monster AI over long leashes,
  spawn counts, the minimap texture, the client's memory. WP-MW-1 measures before anything is built on it.
- **Frame rate was never an exit before.** The server's tick has budgets and telemetry; the client has none. The
  budget and the capture method are set in wave 0.
- **Collision drift.** Dressing a city moves blockers; a package diff on every scene PR and the grid rule
  (R-11.6-06) keep prediction and server in step.
- **Animation against timings.** A nicer animation that is longer than its action is wrong: the config wins.
- **Tests bound to the dev world.** Every scenario names `m1` places; the dev world stays so nothing breaks, and
  the main world gets its own scenarios.
- **Scope creep into systems.** Fast travel, the black market, the arena, raids, guild halls, rest spots,
  mounts: places and markers only, unless the user decides otherwise (decisions 13 and 15).
- **Hidden entities and names.** New character and gear rendering still goes through `IVisibilityQuery` and
  `DisplayIdentity`; a look id must not leak who a disguised character is.
- **The walk harness.** It already sticks at the dev city's gateway corners (QA F1); a map of this size needs a
  walker that follows roads.

## 9. Handover checklist for the MW orchestrator
1. Read:
   - `CLAUDE.md`, this plan, and `docs/plans/m4-player-economy-plan.md` (process history, section 3a);
   - the memory files (`english-only`, `orchestrator-roles`, `placeholder-animations`, `own-dev-databases`,
     `play-stack-smoke-permission`, `unity-editmode-for-data-rows`, `unity-batchmode-short-path`,
     `auto-merge-all-prs`, `no-python-node-render-with-edge`, `verify-pr-after-push`);
   - the role files in `.claude/agents/`;
   - the spec sections and design files listed at the top and in section 4.
2. Verify main: `git log`, open PRs (the M4 fixer's, if still open), CI on main, `docker compose up -d --build
   --wait`, `dotnet test Mmorpg.sln`, one smoke run through QA. Write the first dated entry of section 3a.
3. Put decision 1 (the exit criteria) and decisions 2 to 10 to the user, with the recommendations. Record the
   answers in the spec (12.2, 12.9, 9.14, 10.4) and the decision log before briefing wave 0. Replace "proposed" in
   the 12.2 row with what the user confirms.
4. Brief wave 0 in plan mode (Opus 5.5 high, own worktrees): WP-MW-1 to WP-MW-4, and WP-MW-5 when its decisions
   are in. Each brief carries the spec sections of section 4, the WP text of section 6, the standing rules of
   section 2 and "done" as written.
5. Before wave 1, put decisions 11 to 16; agree the hand-over of assets with the team (decision 5). Brief WP-MW-6
   to WP-MW-8.
6. After wave 1: QA, the user's play test, the measured cost per region. **Go back to the user with the size of
   waves 2 and 3** before briefing them.
7. Before the character wave, put decisions 17 and 18.
8. After each wave: QA of main, fixers for findings, rebuild the play stack, ask the user to play, update
   section 3a.
9. When the exit checks pass on main with QA's evidence, ask the user to close MW and write the M5 handover plan in
   the same shape (law and PK, spec 12.2).
10. Shared notes folders (hand-over notes and QA evidence per session): the M2 orchestrator's
    `C:\Users\Matheus\AppData\Local\Claude-Data\claude_pzs8sxrjxfjjc\side-session-notes\local_e83ffa86-5a4b-4fcf-bdb1-e3f42cd0c7bd\`
    and the M3 orchestrator's
    `C:\Users\Matheus\AppData\Local\Claude-Data\claude_pzs8sxrjxfjjc\side-session-notes\local_f4cc6e62-1584-4003-928c-e3e8e9fb3f51\`
    (M4's notes are there too: `qa-m4-wave1.md`, `qa-m4-wave2.md` with `qa-m4-wave2-evidence/`,
    `fixer-m4-harness-backlog.md`).
