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
`-UnityPath <Unity.exe>`, `-UnityTimeoutMinutes <n>` (default 20), `-StopLeftovers` (see below). Exit code 0 only when
every phase passed and every step passed or was `n/a`.

If a run is interrupted (the script itself killed), its cleanup never runs and the GameServer, the Auth API or a Unity
batch run of this checkout can stay alive. The next run finds them in its preflight and stops with their process ids;
pass `-StopLeftovers` to stop them and go on. Only processes of this checkout are touched (servers started from its
`artifacts/bin`, Unity test runs on its `unity/`). One run takes about 2 minutes on a warm Library (98 s measured; the
scenario itself about 70 s, of which about 30 s are the monster respawn wait); see the timings in `summary.json`.

What it does, in order (each line is a phase in `summary.json`):

1. **preflight**: Unity found, no leftovers of an earlier run, editor closed, ports 5080 (Auth API) and 7777
   (GameServer) free.
2. **docker**: `docker compose up -d --wait`; if compose was not running before, it is stopped again at the end.
3. **build**: `dotnet build` of GameServer, AuthApi and DbMigrate. **migrate**: `DbMigrate -- --dev`.
4. **servers**: GameServer and Auth API (`MMORPG_AUTH_IDENTITY=dev`) from `artifacts/bin`, with a ticket secret and a
   token secret generated for this run (process environment only, never written), telemetry in the run folder.
5. **end-to-end**: Unity EditMode `LoginEndToEndTests` (`MMORPG_E2E_AUTH_URL`), see `unity/Assets/Game/UI/README.md`.
6. **smoke**: Unity PlayMode `QaSmokeScenarioTests` in batch mode with graphics (`-devLogin <n>`, `-qaClass <id>` with `-Class`, `MMORPG_QA_RUN_DIR`).
7. Always: both servers stopped, environment restored, `summary.json` written, the step table printed.

## The scenario

`unity/Assets/Game/Dev/Qa/QaScenario.cs`, one screenshot and one JSON file per step:

| # | Step | Checks |
|---|------|--------|
| 1 | `title` | login scene loaded, title screen shown |
| 2 | `dev-login` | sign-in done (the "press any button" prompt, no error), account badge; then a key press |
| 3 | `server-select` | one row per `ServerDirectory` row, named; Enter server opens the character list |
| 4 | `character` | an existing character is selected, or one is created (`Qa` + letters, the `-Class` class or the first allowed one); extra shot `-create` |
| 5 | `enter-world` | world scene, own HP/MP/stamina in snapshots, own server position, the class from the own spawn (as `-Class` asked); its `ActionBindings` row is the kit played below |
| 6 | `walk-to-demon` | walks a grid path to the monster of the nearest `SpawnPoints` marker, sees it, stops within reach |
| 7 | `kill-demon` | the class's normal attacks until it dies: hit messages from us, HP bucket goes down, a `Killed` hit, damage >= `MonsterDef.maxHp`; the player's own HP, damage taken and deaths (`player deaths`, `player alive`); extra shot `-fight` |
| 8 | `pick-up-drop` | the demon's loot appears as a `LootDrop` spawn naming an `ItemDef`; walks next to it, presses the pickup key (G): the answer is `None`, the bag the server sends after the save holds the item, the drop despawns; extra shot `-drop` |
| 9 | `arrow-wall` | the kit's first projectile action (the normal attack, else the lowest hotbar slot, from `ActionBindings`) at the nearest wall in range: its projectile spawns and despawns with reason `World`; extra shot `-flight`. `n/a` for a kit that shoots nothing (the warrior's): run `-Class archer` for this step |
| 10 | `wait-respawn` | the monster reappears at its marker after `StartingValuesM1.monsterRespawnMs` (no earlier, at most 15 s later) with full HP |

How it plays (no server shortcut): gameplay input goes through a virtual **QA Keyboard** and **QA Mouse** added to the
Input System, so `GameInput` reads them like a player's devices (keys looked up from the live action bindings; the
mouse is put over the aim point). Login buttons get a UI Toolkit submit event (what Enter on a focused button does) and
the name field gets its value set: a marked test input source, after which the screens' own handlers run. It observes
through a read-only tap on the world connection (`QaWorldTap`): its own snapshot registry, projectile tracker and hit
decoder, fed with the same messages the game gets. Numbers come from the config tables; the run's own waits are in
`QaTimeouts.cs`.

## Output: `artifacts/qa/<run-id>/`

| File | What it is |
|------|------------|
| `summary.json` | The script's verdict: `passed`, total `seconds`, `devLogin`, `class`, each phase (`pass`/`fail`/`skipped`, seconds, detail) and each step with its screenshots. Start here. |
| `result.json` | The scenario's verdict: `passed`, `failedStep`, and every step in full (as below). |
| `steps/NN-<step>.json` | One step: `status` (`pass`, `fail`, `skipped`, `n/a`: does not apply to this class), `message` (why it failed), `durationMs`, `checks` (name, pass, expected, actual), `screenshots`, `log` (game log lines, refused actions, errors). |
| `steps/NN-<step>.png` | Screenshot at the end of the step (1280 x 720). `-create`, `-fight`, `-flight`: mid-step shots; `-failure`: the frame when the step failed. |
| `smoke-results.xml`, `unity-smoke.log` | NUnit results and the Unity log of the scenario run. |
| `e2e-results.xml`, `unity-e2e.log` | The same for `LoginEndToEndTests`. |
| `gameserver.log`, `authapi.log` (+ `.err`) | Server output. |
| `telemetry/` | The GameServer's telemetry JSON lines for this run. |
| `build-*.log`, `dbmigrate.log`, `docker*.log` | Output of the setup commands. |

World screenshots show the 3D view only: the dev scene's status line is IMGUI and is not captured in batch mode. The
same numbers are in the step JSON.

## Known limits and flakiness

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
  HP, damage taken and deaths) after `QaTimeouts.KillSeconds`. Dodging or backing off to regenerate is not used: it
  needs reading the demon's wind-ups, and backing off past the leash resets the demon to full HP anyway.
- Each run makes a new dev account and character in the dev database (`mmorpg_realm_1`); nothing deletes them.
- Times are wall-clock: a heavily loaded machine can hit the waits in `QaTimeouts.cs`.
