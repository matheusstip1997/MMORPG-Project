# QA smoke run (backlog J)

`Run-QaSmoke.ps1` opens the game the way a player does and checks it with screenshots, so an agent (or anyone) can
validate the M1 slice without pressing Play. It starts the local stack, runs the live end-to-end test and the scripted
scenario in Unity, stops what it started, and leaves everything under `artifacts/qa/<run-id>/` (git-ignored).

## Run it

Prerequisites: Docker Desktop running, the .NET SDK of `global.json`, Unity `6000.3.26f1` at its default path, and
**no Unity editor open on `unity/`** (batch runs need the project to themselves; the script stops with a message if
the editor has it open). The first run in a fresh worktree imports the whole Unity project and takes several minutes.
In a fresh worktree run `git lfs pull` first: the art and the LiteNetLib DLL are in Git LFS, and with pointer files
Unity stops with `CS0246 ... 'LiteNetLib'`.

In PowerShell at the repository root (Windows PowerShell 5.1 or PowerShell 7):

```powershell
powershell -ExecutionPolicy Bypass -File tools/qa/Run-QaSmoke.ps1
```

Options: `-RunId <name>` (folder name, default `<yyyy-MM-dd-HHmmss>-smoke`), `-SkipEndToEnd` (only the scenario),
`-DevLogin <n>` (sign in as `dev:<n>`; default a new id per run, so a new character starts at the city spawn),
`-Class <id>` (the class of the character the scenario creates, a `ClassDef` id such as `archer`; default the first allowed class, the warrior),
`-DbSuffix <suffix>` (own dev databases `mmorpg_global_<suffix>` and `mmorpg_realm_1_<suffix>`, migrated with `DbMigrate -- --dev --db-suffix <suffix>`, and Redis keys under `mmorpg:<suffix>:`; use it on any branch that adds a migration, so the shared `mmorpg_global` and `mmorpg_realm_1` stay on main's schema for the other sessions on this machine),
`-UnityPath <Unity.exe>`, `-UnityTimeoutMinutes <n>` (default 20), `-StopLeftovers` (see below). Exit code 0 only when
every phase passed and every step passed or was `n/a`.

If a run is interrupted (the script itself killed), its cleanup never runs and the GameServer, the Auth API or a Unity
batch run of this checkout can stay alive. The next run finds them in its preflight and stops with their process ids;
pass `-StopLeftovers` to stop them and go on. Only processes of this checkout are touched (servers started from its
`artifacts/bin`, Unity test runs on its `unity/`). One run takes about 2 minutes on a warm Library (98 s measured; the
scenario itself about 70 s, of which about 30 s are the monster respawn wait); see the timings in `summary.json`.

What it does, in order (each line is a phase in `summary.json`):

1. **preflight**: Unity found, no leftovers of an earlier run, editor closed, ports 5080 (Auth API) and 7777
   (GameServer) free. The composed dev stack (`docker compose up -d --wait`, spec 11.3) holds both: stop its servers
   first with `docker compose stop authapi gameserver adminpanel`.
2. **docker**: `docker compose up -d --wait postgres redis` (the databases only, never the composed servers); if they
   were not running before, they are stopped again at the end.
3. **build**: `dotnet build` of GameServer, AuthApi and DbMigrate. **migrate**: `DbMigrate -- --dev` (with `--db-suffix` when `-DbSuffix` is given), then the demand loop's
   persisted spawn percents (R-4.2-03, `demand_state` of world 1, read only) into `demand-start.json` for `wait-respawn`.
4. **servers**: GameServer and Auth API (`MMORPG_AUTH_IDENTITY=dev`) from `artifacts/bin`, with a ticket secret and a
   token secret generated for this run (process environment only, never written), telemetry in the run folder, the
   GameServer's dev tools on (`MMORPG_DEV_TOOLS=1`, spec 11.7), and the servers and Unity reading a copy of
   `data/tables` in the run folder (`MMORPG_DATA_DIR`), which the `dev-tools` step edits to test the hot reload.
5. **end-to-end**: Unity EditMode `LoginEndToEndTests` (`MMORPG_E2E_AUTH_URL`), see `unity/Assets/Game/UI/README.md`.
6. **smoke**: Unity PlayMode `QaSmokeScenarioTests` in batch mode with graphics (`-devLogin <n>`, `-qaClass <id>` with `-Class`, `MMORPG_QA_RUN_DIR`).
7. **saved-bag**: when the `move-stack` step got that far, reads `item_stacks` and `item_instances` of the container it moved in (`bagContainer` in `result.json`) from the run's realm database (`docker compose exec postgres psql`, the character of the newest `move:` operation) and fails unless the moved stack is in its new slot and gone from the old one (the bag as saved, not as the client was told).
8. Always: both servers stopped, environment restored, `summary.json` written, the step table printed.

## The scenario

`unity/Assets/Game/Dev/Qa/QaScenario.cs`, one screenshot and one JSON file per step:

| # | Step | Checks |
|---|------|--------|
| 1 | `title` | login scene loaded, title screen shown |
| 2 | `dev-login` | sign-in done (the "press any button" prompt, no error), account badge; then a key press |
| 3 | `server-select` | one row per `ServerDirectory` row, named; Enter server opens the character list |
| 4 | `character` | an existing character is selected, or one is created (`Qa` + letters, the `-Class` class or the first allowed one); extra shot `-create` |
| 5 | `enter-world` | world scene, own HP/MP/stamina in snapshots, own server position, the class from the own spawn (as `-Class` asked); its `ActionBindings` row is the kit, of which the learned skills are played below (WP-M2-5); the HUD resource panel shows the own HP (`HUD shows the own HP`) |
| 6 | `learn-skill` | first, a character that logged in away from the city spawn (`ServerLoop.playerSpawnMarkerId`; a second run starts where the first logged out, beside the boss) walks back to it, waits for its respawn if it dies on the way, and waits out the combat state (`StartingValuesM1.combatStateMs` after its last hit dealt or taken), all logged (QA wave 1 F1: Advance and Evolve are refused in combat, and a hit flinches the player while it clicks; the window's requests are never retried); a new character is already there and out of combat. Then the skill tree (WP-M2-5, spec 3.5): a new character has its normal attack and 1 point (R-3.5-04). A character at a tier-1 unlock level (the second run of "Level-30 run" below) first advances through the window's Advance button into the first child class with a skill to learn (`advanced to the tier-1 class`, `the new tree is shown`, shot `-advanced`), learns that tree's first skill, and after closing the window presses it from its hotbar key (`tier-1 skill used from the hotbar`). The `Hotkeys` key `toggleSkillTree` (K) opens the window with the committed tree (`window opens with its key`); a click selects the first learnable active of the root tree (`click selects the skill`), a click on Learn sends it; once its save committed the skill is on its default hotbar slot in the predicted loadout (`learned skill on the hotbar`); the clicks start nothing (`clicks on the window start nothing`); extra shot `-learned`. The fight steps below play the normal attack and the learned skills only |
| 7 | `chat-echo` | Enter opens the chat input (`Enter opens the chat input`); W held while it is open does not move the player (`gameplay keys suspended while typing`); Enter sends a line and the server echo shows it with the own entity id and display name (`echo of the own line`, `sender name from DisplayIdentity`); extra shot `-typing` |
| 8 | `footwork` | directional locomotion (R-2.2-01): the mouse held over a point screen-up of the player, D strafes right and S backpedals (`QaTimeouts.FootworkLegSeconds` each), then A and W walk back; for the strafe and the backpedal: clear ground, the player moved at least `FootworkPercentOfDistance` of move speed x leg time along the key's direction and kept facing the aim (within `FootworkToleranceDeg`); the local visual turns its legs more than half `AnimationSet.legTurnMaxDeg` while strafing (`strafe: the legs turn toward the movement`) and blends mostly its backward loops while backpedalling (`backpedal: the backward loops play`), logged as `locomotion: ...`; extra shots `-strafe`, `-backpedal` (key held) |
| 9 | `walk-to-demon` | walks a grid path to the monster of the nearest `SpawnPoints` marker, sees it, stops within reach; on the way, outside the demon's aggro range, casts the opening buffs and presses the kit's moves and utilities once, then the dodge (`<id> moves the player`, `dodge moves the player`: its Dash of `StartingValuesM1.dodgeDistanceCm`, pressed with no movement key held; `<id> fires`, see "How it fights") |
| 10 | `body-block` | body collision (R-2.2-07): steering at the demon's centre for `QaTimeouts.BodyBlockSeconds` (3 s) brings the player to its body and no further, measured on the server positions (the own snapshot and the demon's newest one): never closer than contact (`ClassDef` + `MonsterDef` `bodyRadiusCm` less `BodyCollisionSettings.overlapToleranceCm`) beyond the whole-cm rounding (`never inside the demon's body`), and within `BodyTouchCm` of it at least once (`reached the demon's body`); the screenshot shows the stop |
| 11 | `lock-demon` | the target lock (R-2.2-06) with the mouse put 90 degrees around the player from the demon: the `Hotkeys` key `targetLock` (Tab) locks the demon (`<Keyboard>/tab locks the demon`); within 3 s the facing the client sends and the facing the server applied (own snapshot) are within 20 degrees of the direction to the demon (`sent facing follows the lock`, `server facing follows the lock`); its name plate shows the lock marker and its HP bar (`HUD lock marker on the demon`, `HUD HP bar of the locked demon`); a double press (started `doublePressWindowMs` + 0.2 s after the lock press, so its first tap does not pair with that press) clears the lock and the marker, and the facing follows the mouse again (`double press clears the lock`, `HUD lock marker gone`, `facing follows the mouse again`). The fight below runs without the lock |
| 12 | `kill-demon` | the class's kit until it dies (see "How it fights" below): hit messages from us, HP bucket goes down, a `Killed` hit, damage >= `MonsterDef.maxHp`; the player's own HP, damage taken and deaths (`player deaths`, `player alive`); the HUD shows damage numbers for our hits, the demon's name plate and its HP bar once damaged (`HUD damage numbers`, `HUD name plate`, `HUD monster HP bar`), watched every frame with the casts included (a skill's strikes can take the demon from full HP to dead within one cast); when the demon dies in the tick of our first hit, no snapshot shows it damaged and alive (a level-30 character, QA wave 1 F2), so `HP bucket went down` and `HUD monster HP bar` are `n/a` checks (`notApplicable`, the reason in `actual` and the log); every kit action fired once in the run and the cast bar shown during a channel (`every kit action fired`, `HUD cast bar during a channel`); when the snapshots show the demon `Staggered` (a poise break, R-3.3-01; the warrior's chain breaks it), its plate shows the stagger marker and loses it again while the demon lives (`HUD stagger marker on the staggered demon`, `HUD stagger marker gone after the stagger`, shot `-stagger`; the second is `n/a` when the demon dies still staggered), otherwise the marker never shows (`HUD stagger marker only on a staggered demon`); the normal attack chain reaches its last hit (`attack chain reached its last hit`, R-3.1-10); extra shots `-fight`, `-cast`, `-stagger`, `-chain` (the last hit of the chain in its Active phase) |
| 13 | `pick-up-drop` | the demon's loot appears as a `LootDrop` spawn naming an `ItemDef`; walks next to it; before the press the HUD's pickup prompt stands at that drop with the `UiStrings` text naming the pickup key (`pickup prompt at the drop`, shot `-drop`); presses the pickup key (G): the answer is `None`, the container that receives the item's category (`ContainerDef.receivesNewItems`, as the server's `ItemRules.ReceiverOf`: the Normal bag for loot, the Safe bag's equipment area for equipment) holds, in the inventory the server sends after the save, the taken count more of the item than before the press (an earlier run's pickup may be in it); the log names the container and slot it landed in; the drop despawns and the prompt with it (`pickup prompt gone with the drop`); extra shot `-drop` |
| 14 | `move-stack` | the inventory window (WP16, spec 9.13), all through the pointer (see "How it plays"): opens with its `Hotkeys` key (I), clicks the tab of the container the pickup landed in when it is not the Normal bag (`tab of <container> selected`), shows the picked-up stack in its saved slot; hovering it shows the tooltip and sets the pointer mask (`UI Toolkit sees the pointer (hover)`, `pointer on the window masks the mouse buttons`); attack and parry held over the window start nothing (predicted machine idle, stamina not spent); click on the stack, then on the last empty slot (click-to-move); then a drag from there onto the next empty slot (the drag ghost shows, shot `-drag` with the attack button held); each move: the preview moves it at once, the server answers `Moved`, the bag it sends after the save has the stack in its new slot and the window shows it confirmed; a drag out of the window is refused with the `ItemDropUnavailable` toast and sends nothing (shot `-refused`); closes with the key and the mask is released. A merge is not part of the M1 harness (see "Known limits"). The run records the drag's slots for the database check below; extra shots `-open`, `-drag`, `-moved`, `-refused` |
| 15 | `arrow-wall` | the kit's first projectile action (the normal attack, else the lowest hotbar slot, from `ActionBindings`) at the nearest wall in range: its projectile spawns and despawns with reason `World`; extra shot `-flight`. `n/a` for a kit that shoots nothing (the warrior's): run `-Class archer` for this step |
| 16 | `wait-respawn` | the monster reappears at its marker after `StartingValuesM1.monsterRespawnMs` scaled by the demand loop's spawn percent of its type in the marker's region (R-4.2-03, `SpawnController`: the last `DemandUpdated` of the run's telemetry before its death, else `demand-start.json`, else 100; never below `DemandSettings.minRespawnMs` when shortened; the step log names the source), no earlier, at most 15 s later, with full HP. An earlier run on the same databases leaves its percent in `demand_state` |
| 17 | `dev-tools` | the dev tools (spec 11.7): F4, F5 and F6 (their `Hotkeys` rows) show the hitbox gizmos, the action timeline and the tick panel; one normal attack: the gizmos draw its predicted and its server-tested shapes (a striking kit; logged for a projectile kit), the timeline has samples from prediction and the server, the tick panel has server tick times. Then the table hot reload: the normal attack's `recoveryMs` + 50 ms in the run's tables copy, the server accepts it and the client applies the same tables (same hash, the client's catalog has the new value), and back. Extra shot `-hitboxes` (IMGUI panels are not in shots) |
| 18 | `parry-boss` | the parry against the boss (fix/parry-feel; R-3.3-03, R-3.3-04): walks to the Cyclops, and for each melee attack the class can parry (not red; yellow heavy, blue medium or heavy) presses the parry `QaTimeouts.ParryLeadTicks` (4) ticks before the drawn hit; a `ParrySuccess` within 6 parries, which flashes the P square (`HudSettings.parryFlashMs`) and the boss (`hitFlashMs`). Every parry result (early, late) is logged. Extra shot `-flash` |
| 19 | `tutorial-gate` | the tutorial gate (WP-M2-10; R-12.3-01, R-2.3-06): walks to the nearest pass into a level-gated region (`QaNavigation.GateCrossings` over the grid and `RegionDef.minLevel`) on a path around the other camps' aggro, and pushes into it for `QaTimeouts.GatePushSeconds`. Below the region's level (a new character): `the server keeps the player out`, `the prediction stops at the gate`, `the gate prompt shows` ("Reach level 10 to leave the tutorial.", extra shot `-gate`). At or above it (the level-30 second run after `DbMigrate -- --dev --set-level <name> 30`): `the player walks through the gate`, `no gate prompt` (shot `-through`), then it walks back. Not applicable in a world without a gated region |

How it plays (no server shortcut): gameplay input goes through a virtual **QA Keyboard** and **QA Mouse** added to the
Input System, so `GameInput` reads them like a player's devices (keys looked up from the live action bindings; the
mouse is put over the aim point). Login buttons get a UI Toolkit submit event (what Enter on a focused button does) and
the name field gets its value set: a marked test input source, after which the screens' own handlers run. In the world,
UI Toolkit gets the pointer from `QaUiPointer` (QA wave 6 F1): UI Toolkit's runtime event system drops pointer input
while the application has no focus, and a batch run never has focus, so the QA Mouse alone never reached a panel. The
pointer events go to the panel's root at panel positions and UI Toolkit's own dispatch does the rest (element under the
pointer, enter and leave, pointer capture, the windows' handlers); the QA Mouse moves and presses along with it, so the
game sees the same pointer and button. It observes
through a read-only tap on the world connection (`QaWorldTap`): its own snapshot registry, projectile tracker and hit
decoder, fed with the same messages the game gets. Numbers come from the config tables; the run's own waits and
thresholds are in `QaTimeouts.cs`.

### How it fights (kill-demon)
The same rule for every class, from its `ActionBindings` row: no per-class code (`QaFightPlan.cs`, user decision
2026-10-25).
- **The kit:** the normal attack and each bound skill. From the tables come:
  - its damage, the multipliers of its strike, projectile and damaging ground-area effects added up;
  - its reach, from `HitShapeDef` shapes or the `ProjectileDef` range, plus a forward `Dash` that fires before the damage
    (Jump Strike: 400 cm, then a 150 cm circle, so 250 to 550 cm; closer than that the dash carries the player past);
  - whether it heals the caster: a `Heal` effect with target `self`, or an `ApplyStatus` on `self` of a status with
    `tickHealPercent` (the Assist's Life Regeneration);
  - whether it is an opening buff: an `ApplyStatus` on `self` of a `buff` status that does not heal (Stamina Buff, Magic
    Barrier).
  - its attack chain (R-3.1-10): the normal attack's later hits, from `chainNext`. A press of the normal attack runs
    the whole chain: the step presses attack again once per hit when the predicted machine reaches that hit's
    `chainFromMs` (`QaFightPlan.ChainPressDue`), and shoots `-chain` the first time the last hit is Active.
- **Usable:** an action is usable when the snapshot's stamina and MP pay its `staminaCost` (the normal attack: its whole
  chain's, so a later hit is not refused with `NoStamina`) and `mpCost`, and the client's
  predicted action machine (`PredictionDevClient.Predictor`) is idle with the action off cooldown.
  - The server's word wins: an action it refuses with `OnCooldown` is held off for its `ActionDef.cooldownMs`.
  - After a press the step waits only while the predicted machine is busy, so a refused or interrupted action does not
    leave the player standing idle.
- **Each turn**, facing the demon (walking after it while nothing usable reaches it):
  1. below `QaTimeouts.HealBelowPercent` HP, a usable heal on itself, an instant one before a heal over time; a heal that
     is off cooldown and paid in MP but short of stamina makes it **spend no stamina** until the heal is paid (every
     attack would restart `staminaRegenDelayMs`): it stands and waits for the time `StartingValuesM1.staminaRegenDelayMs`
     and `staminaRegenPerSec` give, logged as "no attacks for about N ms" (QA wave 6 F2);
  2. below `QaTimeouts.BackOffBelowPercent` HP with no heal usable, a back-off: legs of `BackOffCm` away from the demon,
     along the most direct direction that stays out of no-entry regions (the city: the demon would drop the player and
     walk home to full HP) and within the demon's `leashRangeCm` minus `BackOffLeashMarginCm` of its home, until HP is
     back at `ReengageAbovePercent` or a heal is usable (at most `BackOffRecoverSeconds`);
  3. the usable opening buffs not cast yet in this life (walk-to-demon already casts them `BuffUpMarginCm` outside the
     demon's `aggroRangeCm`, so its first pounce cannot flinch them; a cast the demon interrupts is cast again when ready);
  4. the usable damaging skill with the biggest multiplier that this run has not fired yet (QA wave 6 F3): pressed in
     reach, after a step back when its dash needs room (Jump Strike), or after walking in when it reaches less far than
     the demon is (the Mage's 350 cm Flamethrower);
  5. otherwise, the usable damaging action in reach with the biggest multiplier. The kit's skills go when ready, and the
     normal attack fills in between. When the best one needs room for its dash, it steps back (at most
     `StepBackSeconds`) and casts it from the upper half of its reach.
- **Every skill fires once (QA wave 6 F3):** walk-to-demon presses the skills the fight never needs at the buff-up spot:
  each move (a `Dash` effect and nothing else: the Archer's Dash and Back Step) along a clear direction away from the
  demon, checked by the server position (`<id> moves the player`: at least `DashPercentOfDistance` of its distance), and
  each utility (the Assist's Simple Cleanse: nothing in the M1 fight puts a status or build-up on the player, so only that
  it fires and spends its MP is checked); then it waits for full stamina. After the kill, any kit action still not fired
  (a heal the fight did not need) is pressed once. An action fired when the client's predicted machine reached Active
  without a flinch, a stagger or a server refusal.
- **Checks:** besides the kill, `buff row shows <status>` for every status the kit cast on the player (the HUD chip with
  the status name; extra shot `-buff`), `every kit action fired` (each action with its count), `HUD cast bar during a
  channel` for a kit with a channeled action (the first channel whose name the `cast_bar` shows gets the extra shot
  `-cast`, in the step it happens in; the Warrior's kit has none), and the step fails at once with "the demon reset to
  full HP" when the demon's HP bucket returns to full while the player lives.
- **The step log** lists the kit as the harness read it and every action used (`used: ... xN`), with the back-offs.
A class that still cannot kill the demon this way is a tuning finding for backlog K, not a harness change. Balance
numbers stay in the config tables.

## Level-30 run: the class advance (WP-M2-5)

The tier-1 path of `learn-skill` needs a character at level 30. Three commands, on your own databases (`-DbSuffix`) and
with one `-DevLogin` for both runs; `<name>` is the `Qa...` character the first run created (`steps/04-character.json`):

```
powershell -ExecutionPolicy Bypass -File tools/qa/Run-QaSmoke.ps1 -DbSuffix <s> -DevLogin <n>
dotnet run --project src/Tools/DbMigrate -- --dev --db-suffix <s> --set-level <name> 30
powershell -ExecutionPolicy Bypass -File tools/qa/Run-QaSmoke.ps1 -DbSuffix <s> -DevLogin <n>
```

The second run plays every step on the level-30 character (QA wave 1). It logs in where the first run ended, beside the
boss, so `learn-skill` first walks back to the city spawn and waits out the combat state (the walk and the wait are in its
log). At that level the demon may die in the tick of the first hit, so two `kill-demon` checks can be `n/a`. The bag
already holds the first run's pickup, and the demon's respawn delay follows the demand state the first run left.

## Test-only: reaching a Bystial book milestone fast (WP-M2-8)

The book earns a card from the kill counters (`monster_kill_counts`, spec 14.2 R-14.2-01/02): 100 demons for the normal
demon card. To see it without 100 kills, set the count of a character that is **offline** (a world holding it saves its
own count over yours), then log in and kill one demon:

```
powershell -ExecutionPolicy Bypass -File tools/qa/Set-KillCount.ps1 -Character hero1s0 -Count 99 [-Monster m1.demon] [-DbSuffix <name>]
```

It upserts one row through `docker compose exec postgres psql` on `mmorpg_realm_1` (or `mmorpg_realm_1_<DbSuffix>`): dev
databases only. After the kill the card appears in the book (key B, placeholder) within a second (the class 2 save);
select it and click the slot to place it, and Max HP rises by 10 (out of combat: R-14.2-06). The live end-to-end test
`BystialEndToEndTests` seeds the same row.

## Output: `artifacts/qa/<run-id>/`

| File | What it is |
|------|------------|
| `summary.json` | The script's verdict: `passed`, total `seconds`, `devLogin`, `class`, each phase (`pass`/`fail`/`skipped`, seconds, detail) and each step with its screenshots. Start here. |
| `result.json` | The scenario's verdict: `passed`, `failedStep`, and every step in full (as below). |
| `steps/NN-<step>.json` | One step: `status` (`pass`, `fail`, `skipped`, `n/a`: does not apply to this class), `message` (why it failed), `durationMs`, `checks` (name, pass, expected, actual; `notApplicable`: the check does not apply to this run and passes, `actual` says why), `screenshots`, `log` (game log lines, refused actions, errors). |
| `steps/NN-<step>.png` | Screenshot at the end of the step (1280 x 720). `-create`, `-fight`, `-flight`: mid-step shots; `-failure`: the frame when the step failed. |
| `smoke-results.xml`, `unity-smoke.log` | NUnit results and the Unity log of the scenario run. |
| `e2e-results.xml`, `unity-e2e.log` | The same for `LoginEndToEndTests`. |
| `gameserver.log`, `authapi.log` (+ `.err`) | Server output. |
| `telemetry/` | The GameServer's telemetry JSON lines for this run. |
| `demand-start.json` | The demand loop's spawn percents the GameServer loaded at start (`wait-respawn`). |
| `build-*.log`, `dbmigrate.log`, `docker*.log` | Output of the setup commands. |

World screenshots show the 3D view and the HUD (UI Toolkit panels are captured); the dev status line is IMGUI and is
not captured in batch mode. The
same numbers are in the step JSON.

## Known limits and flakiness

- **No live stack merge in M1** (user decision 2026-10-26): an M1 bag never holds two stacks of the same item below
  max (a pickup fills the existing stack, every M1 drop is `common`, split is "Not yet" in spec 9.13), so `move-stack`
  cannot merge in play. A live merge check would need a dev give-item command or a seeded bag; it is deferred. Merging
  is covered by the EditMode `InventoryWindowTests` and the GameServer `InventoryMoveWorldTests` and
  `InventoryMoveEndToEndTests`. No step or check waits on it; the step log says so.
- Batch mode has no Game view: cameras and UI Toolkit panels are rendered into textures and blended by the harness.
  The run is capped at 60 frames per second: uncapped batch frames made later screenshots lose their material colors.
- `enter-world` fails "local player drawn" when the character's prefab has no renderer. Seen on a worktree whose
  Library imported the VRoid avatars badly (the log also says "the body has no humanoid RightHand bone"). A fresh
  Library now imports them again by itself (`FailedModelReimporter`, see `unity/Assets/Game/Dev/README.md`); a Library
  that kept the failure from before is repaired by one Unity EditMode run (`EntityVisualTests`) or by
  `-executeMethod Game.Gameplay.Entities.Visuals.Editor.EntityVisualPrefabBuilder.Build`. The builder also re-saves the
  visual prefabs and materials (mostly line endings): check `git status` and restore the ones you did not mean to change.
- The demon fights back (monster AI): it aggroes when the player comes within its `aggroRangeCm`, claws and pounces, and
  can kill the character. The kill loop re-aims and follows it every attack and logs the player's HP. When the player
  dies, the step waits for the respawn at the city spawn (`playerRespawnDelayMs`), walks back and fights again: by then
  the demon has lost its target, walked home and healed to full (leash), so each life is a fresh fight. The step fails
  with "the player died N times" after `QaTimeouts.KillLives` deaths, and with "the demon still lives" (plus the player's
  HP, damage taken and deaths) after `QaTimeouts.KillSeconds`. Dodging is not used (it needs reading the demon's
  wind-ups); a back-off stays well inside the leash (`QaTimeouts.BackOffCm`).
- Each run makes a new dev account and character in the dev database (`mmorpg_realm_1`); nothing deletes them.
- Times are wall-clock: a heavily loaded machine can hit the waits in `QaTimeouts.cs`.
