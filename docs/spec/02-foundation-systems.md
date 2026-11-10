# 2. Foundation Systems

## 2.1 Accounts, login, characters, sessions
**Rules**
- **R-2.1-01** Login is **Steam only**. The account key is the SteamID; there are no passwords or other login providers. The Auth API verifies the Steam session ticket (single use, cancelled when the session ends) and issues its own short-lived session ticket for the game server.
- **R-2.1-02** Family Sharing: bans apply to the owner's account as well as the borrowing account.
- **R-2.1-03** **Realms:** the game launches with **one realm**, but the architecture is realm-aware: a **global database** (accounts by SteamID, bans, realm directory, character counts per realm) and a **database per realm** (characters, items, economy, guilds, territories, quests, arena). Tickets and character data carry a `realmId`. Players come from several regions: host the first realm near most early players; regional realms can be added later.
- **R-2.1-04** One active session per account; a new login replaces the old one.
- **R-2.1-05** Characters: **4 slots per realm** (config), unique names, **soft delete with a grace period**.
- **R-2.1-06** A character is loaded in exactly one place at a time (ownership lock).
- **R-2.1-07** **Logout is blocked** while the character is in combat (`lastCombatAt` + window) and while a bad-honor character is dead (arrest window).
- **R-2.1-08** A **disconnect keeps the character in the world** for a linger period (config per region: longer in combat, none in safe zones). The character is unresponsive but attackable, killable, arrestable. Reconnecting within the linger reattaches to the same entity. For a dead bad-honor player the linger is at least the arrest window.
- **R-2.1-09** Login **queue** when the world reaches its cap (lingering characters count); reconnecting players skip the queue. Graceful shutdown: announce, block logins, save all, disconnect, stop. Client/server protocol versions must match.
- **R-2.1-10** Interactive operations (trade, forge session, contract step) roll back safely if the session is lost.

**Implementation**
- `AuthApi`: verifies tickets with the Steam Web API, creates or loads the account row, returns the character list, issues a signed session ticket (account ID, expiry). A dev login exists behind a build flag: the MSBuild property `DevLogin` (default true only in Debug) defines `DEV_LOGIN` and compiles `Identity/Dev`; Release builds leave it out, and it is used only with `MMORPG_AUTH_IDENTITY=dev`. v0 (endpoints in 9.10): the Steam provider is a stub that refuses every login until the Web API call exists; one realm per Auth API process; plain HTTP is refused except on loopback. Family Sharing bans (R-2.1-02) wait for the bans table; the owner SteamID is stored.
- `SessionManager` validates the ticket, takes a Redis ownership lock `char:{id}` (TTL refreshed by heartbeat), loads the character (`CharacterRepository`, optimistic concurrency by version column), places it in its saved world and position (or the main map if the instance is gone).
- `LingerManager` keeps a map of disconnected entities with expiry timestamps; on expiry the character is saved and removed.
- `LoginQueue` is a Redis sorted set; the Workers service admits players when capacity frees up.
- Saves: periodic, on key events (trade, craft, level-up, death), on logout, via `SaveScheduler` with dirty flags.
- **Implementation (M1, backlog B4):** `src/GameServer/Sessions`. `SessionManager` runs on the world's simulation thread; Redis and PostgreSQL calls run as tasks whose results return through a completion queue read in tick phase 1, so the tick never waits on I/O and reattach, replace and expiry are decided on one thread in command order.
  - Login: the character is the one in the ticket's slot; the lock key is `{prefix}realm:{realmId}:char:{characterId}` (character ids are per realm database), value = a token per hold, TTL `SessionSettings.OwnershipLockTtlMs`, refreshed every `HeartbeatIntervalMs` by compare-and-expire; the row is read again after the lock. The saved position is used when the saved `world_id` is `main`, otherwise the main map spawn.
  - A lock held by another server refuses the login (`CharacterUnavailable`); a failed load releases the lock and refuses (`ServerUnavailable`). A login for an account already online here kicks the old client (`SessionReplaced`) and treats it as a disconnect, so its character lingers. A login for a character still in this world (online, lingering, or closing) reattaches to the same entity without a database read.
  - A lock that is lost, or that cannot be refreshed for a whole TTL, and a save that finds another writer (version conflict) remove the character **without saving** and kick the client (`CharacterUnavailable`, session end reason `OwnershipLost`).
  - `LingerManager`: window `LingerInCombatMs` while `lastCombatAt + CombatStateMs` is in the future, otherwise `LingerOutOfCombatMs` (`StartingValuesM1`). Expiry: logout save, then removal once the save is in, then the lock is released (compare-and-delete). The logout save is the last one: volatile changes after it is issued (regeneration while it is in flight) are not marked again, so they never hold the removal for the next volatile save. Safe-zone linger 0 waits for region flags.
  - Graceful shutdown: block logins (`ServerMaintenance`), stop the simulation, end every session, save every character (at most `ShutdownSaveTimeoutMs`), release the locks, then stop the transport (which sends the kicks). A crashed world is not saved; its locks expire with their TTL.
  - Admin kick (spec 7.4, admin panel v0): a phase 1 command like a login. The client is kicked with `Kicked` and the session ends with reason `Kicked` like a disconnect, so the character lingers (R-2.1-08; user decision 2026-10-26) and is then saved and released as usual. A character that is already lingering is left as it is (`NotConnected`). Packets the transport still receives from a kicked peer are dropped, never opened with its closed keys.
  - Not yet: the login queue (TODO(B4-queue)), the logout request blocked in combat (no combat state yet), the shutdown announcement, replacing a session on another server (needs pub/sub, with the WorldManager).

**Talks to** emits `SessionStarted`, `SessionEnded`, `CharacterCreated`, `CharacterDeleted`; calls `Persistence`, `WorldManager`, `SentencingService` (pending jail on login).

**Flow: Login and session start**

```mermaid
flowchart TB
    n0["Client asks Steam for ticket"]
    n1["Client sends ticket to AuthApi"]
    n2["AuthApi verifies with Steam"]
    n3{"Valid?"}
    n4["Reject login"]
    n5["Issue session ticket"]
    n6["Client connects to game server"]
    n7{"Version OK?"}
    n8["Ask client to update"]
    n9{"Server at cap?"}
    n10["Wait in queue"]
    n11["Admitted"]
    n12["Take character ownership lock"]
    n13["Load character"]
    n14{"Pending jail or jailedUntil?"}
    n15["Place in jail instance"]
    n16["Place in saved world"]
    n17["Character online"]
    n0 --> n1
    n1 --> n2
    n2 --> n3
    n3 -->|"no"| n4
    n3 -->|"yes"| n5
    n5 --> n6
    n6 --> n7
    n7 -->|"no"| n8
    n7 -->|"yes"| n9
    n9 -->|"yes"| n10
    n10 --> n11
    n9 -->|"no"| n11
    n11 --> n12
    n12 --> n13
    n13 --> n14
    n14 -->|"yes"| n15
    n14 -->|"no"| n16
    n16 --> n17
    classDef neg fill:#f6dde0,stroke:#a6414b;
    classDef pos fill:#ddebd5,stroke:#4c7a34;
    class n4,n8 neg;
    class n17 pos;
```

**Flow: Disconnect and linger**

```mermaid
flowchart TB
    n0["Connection lost"]
    n1["Mark session disconnected"]
    n2["Keep entity in world, no inputs"]
    n3{"Reconnect within linger?"}
    n4["Reattach same entity"]
    n5["Save character"]
    n6["Release ownership lock"]
    n7["Remove entity"]
    n0 --> n1
    n1 --> n2
    n2 --> n3
    n3 -->|"yes"| n4
    n3 -->|"no"| n5
    n5 --> n6
    n6 --> n7
    classDef neg fill:#f6dde0,stroke:#a6414b;
    classDef pos fill:#ddebd5,stroke:#4c7a34;
    class n4 pos;
```

## 2.2 Networking and movement
**Rules**
- **R-2.2-01** **WASD** moves; the **mouse aims** but does not turn the body (M3 decisions 15 and 16). The keys move along the **screen axes** (W toward the top of the screen, R-2.2-05) and the character **faces the way it walks**; standing still it keeps its last facing. **A target lock never changes the walk** (M3 decision 16). Actions that do not move **fire at the mouse** without a lock; with a lock (R-2.2-06) every attack and skill that is not a move skill or a ground skill **aims at the locked enemy** instead. Either way the body turns to the action's aim and keeps that facing afterwards, until it walks again. **Ground skills** (an area placed at the pointer, Ice Pike) aim at the **mouse**, lock or not; an area cast from the character (Flamethrower's cone) aims like a strike. **Move skills** (the dodge, the Archer's Dash and Back Step) **follow the keys**, lock or not (R-3.3-02). **Left click** normal attack, **Ctrl** dodge, **right click** defend/parry (aimed at the mouse without a lock, at the locked enemy with one). Hotbar: left side 1 2 3 / 4 5 6, right side Q E R / F Z plus one unassigned slot, two rows per side around the central gauge (section 9.6; configurable).
  - **M3 decisions 14, 15 and 16** (user, 2026-11-02 and 2026-11-03). Until then WASD moved along the screen axes and the character only turned its face toward the mouse. **Decision 14, first reading** (WP-M3-15, protocol 37): *"The keys keep their screen meaning for picking a direction, but the key that points nearest to the mouse moves exactly toward the mouse (mouse at the top: W runs at it; mouse at the left: A runs at it), the opposite key moves exactly away from it, and the other keys turn with it; while the character moves, moving the mouse re-aims the movement at once."* Built as the eight screen key directions turned toward the mouse from its nearest compass sector, with a hysteresis. **Decision 14 revised after play** (WP-M3-15b, protocol 38): *"W keeps moving towards the mouse always, so the other keys follow the same idea."* Every key became an offset from the mouse facing. **Decision 15 after play** (WP-M3-15c, protocol 39), replacing decision 14 for the facing: *"remove the fact that the mouse defines the player direction. Leaves the player to move with the mouse direction. When the target is locked with TAB, then the player will always face the enemy and move based on it. The mouse is going to be used to direct the skills that don't move (keep the move skills to follow the player input of WASD)."* As the user confirmed it: without a lock the mouse does not set the facing and WASD is screen-relative again; a non-moving skill fires at the mouse and the body stays turned that way until it walks or a lock sets it; with a lock the body faces the target and the keys are relative to it (decision 14's offset rule with the lock's facing), the dodge included; non-moving skills aim at the mouse even when locked (the lock's facing resumes after); move skills follow the keys (with no key, backward from the facing); parry and block aim at the mouse without a lock and at the target with one. **Decision 16 after play** (2026-11-03, WP-M3-15d, protocol 40), replacing the locked half of decision 15: *"When locked, the player can move the character the same way as without lock. Lock on the enemy will now direct the skills towards the locked enemy. Every time the player performs an attack or skill the character will direct the face towards the locked enemy. Ground skills like Ice Spike still use the mouse to select the area to launch the skill."* The user's follow-up answers: under a lock the parry and block face the locked enemy; the dodge, Dash and Back Step go along the held keys exactly as without a lock (none held: backward from the current facing); a ground skill turns the body toward the mouse area (the lock does not change ground skills at all); a locked enemy that is dead or out of view at the press leaves the skill at the mouse, as without a lock.
- **R-2.2-02** Skills and attacks cannot happen while moving: movement stops before the action lands. **Facing is locked** from the moment an action starts and only changes after it completes.
- **R-2.2-03** Movement speed comes from the movement-speed stat (grows slightly with level). Mounts add a speed modifier outside combat; being hit dismounts; disabled by region flags.
- **R-2.2-04** Click-to-move is not used.
- **R-2.2-05** The camera stays centred on the character at a fixed **45-degree pitch**; the player can turn it **horizontally** around the character, never vertically (placeholder keys **C** and **V** until the hotkey config decides). Without a lock WASD is screen-relative (W moves toward the top of the screen). Because input frames carry only the four key bits (8 world directions), the yaw settles on the nearest 45-degree step when the turn keys are released, so screen-relative movement is exact at rest. A lock changes nothing here (M3 decision 16: the keys stay screen-relative). **No player zoom** (designer decision A5, 2026-11-09, `docs/design/art-direction.md`): the camera keeps a **fixed distance**, set by config (`ClientCamera`) and the same for every player, so everyone sees the same area (fair in PvP) and the interest area of R-2.3-03 keeps one size. The distance and field-of-view values themselves stay placeholders (10.4).
- **R-2.2-06** **Target lock** (user decision 2026-10-26; M3 decision 16): **Tab** locks the nearest valid target within the acquire range; **Tab again** moves the lock to the nearest valid target not yet locked in this round (when every one was, a new round starts at the nearest); **a second press within the double-press window** clears the lock instead. The lock **aims, it does not steer**: the walk and the facing while walking or standing are as without a lock (R-2.2-01); every press of an attack or skill that is not a move skill or a ground skill (the parry and block included) **aims at the locked enemy**, and the body turns to it at the press (section 3.1 Targeting); ground skills (areas placed at the pointer) aim at the mouse and move skills follow the keys, lock or not. The aim is the `ActionDef.aim` column; DataValidator enforces it (`ActionDef.ValidateAim`: a `moveInput` dash is `body` and only it; `ground` needs a `groundArea` effect; a ground area whose every hit shape lies forward of the caster is `ground`; every action with a ground area writes its aim). The **server** aims the press itself, from where this player saw the enemy (its position history on the press frame's view clock, R-3.1-03); if the enemy is unknown to the server, dead, gone, out of this player's view (not in its interest set, hidden included, rule 9) or farther than the break range the press aims at the mouse. R-2.2-02 still holds (the facing stays locked while an action runs). Valid targets in M1: living monsters and bosses (players wait for the PvP targeting rules). The lock breaks when the target dies, despawns or leaves the interest area, when it is farther than the break range, and on the player's own death or a disconnect; no line-of-sight check in M1. The camera does nothing while locked (R-2.2-05 unchanged).
- **R-2.2-07** **Body collision** (user decision 2026-10-27): living monsters and bosses **block movement**: a player cannot walk, dash or dodge through a monster's body, and a monster cannot walk or dash through a player's or another monster's body. **Players pass through each other** (TODO(open): the push rule between players, decided with PvP). A body is the circle of `bodyRadiusCm` that hits use (`ClassDef`, `MonsterDef`). A walk **slides** along a body, so a player can walk round a monster; a dash or a dodge **stops** at contact (the dash ends early; the action's timing and its i-frames are unchanged), so dodging through the boss is impossible. Nobody pushes: the mover yields. A monster's chase still stops at its attack reach; collision only prevents overlap. Dead bodies do not block. Placement (spawn, login, respawn, a leash reset to the spawn point) is never blocked and pushes nobody; bodies that overlap may only move apart or sideways, never deeper.
- **R-2.2-08** **Monster size classes** (designer decision B5, 2026-11-09, `docs/design/art-direction.md`): by body radius (`MonsterDef.bodyRadiusCm`, the body of R-2.2-07), a monster is **small** (up to 60 cm), **large** (up to 100 cm) or **boss** (over 100 cm). **Large monsters and bosses are placed only on open ground**, with no gap narrower than about 4 m near their camp or arena; this is a placement rule for world builders (R-3.1-11), not a server check. How the server stops a large body squeezing through a gap that the 40 cm agent grid leaves open is **Open** (10.4): the "Not in M1" note under body collision below still holds.

**Implementation**
- `Shared/Simulation/MovementSim` advances position from (WASD bits, facing, speed, dt). The client predicts with it; the server runs the same code from received inputs. The server applies one input frame per player per tick (none when nothing arrived, so frames are never invented) and sends its authoritative state with the newest applied input sequence in every snapshot; the client corrects only when its error exceeds a tolerance (client config) and reconciles by replaying unacknowledged inputs.
  - **Input catch-up** (fix/parry-feel; bug fix approved by the orchestrator 2026-10-26, confirmed by the user 2026-10-27 after play): one frame in and one out per tick never drains a burst, so a jitter or a client hitch left frames queued for good, which added their ticks to the player's input delay and to the view lag its parries and dodges are judged with (R-3.1-03; measured on main: +1 frame on LAN with a 60 fps client, +2 to +4 at rtt100). When the queue still holds frames after the tick's dequeue for `ServerLoop.inputBacklogTicks` ticks in a row (placeholder 10), the tick applies up to `ServerLoop.maxInputFramesPerTick` frames (placeholder 2, at most `maxQueuedInputFrames`) until it is drained: phase 2 steps `MovementSim` once per frame, as the client predicted them; the action machine steps once on their buttons OR-ed together (every press is kept; a release and a new press inside the same catch-up tick read as one hold), facing and keys of the newest frame, the hotbar slot of the newest frame that holds one; the acknowledged sequence is the newest; the view clock (R-3.1-03) is the one of the frame with the first new press, else the newest. A full queue still drops its oldest frame (`maxQueuedInputFrames`), but the dropped frame's held buttons and hotbar slot carry over to the frame after it, so only its movement is lost. Catching up over dropping keeps movement continuous (no lost steps, no correction for position); the client only sees an action-timeline correction when a catch-up tick ran two of its predicted machine steps as one.
- **Controls (R-2.2-01, R-2.2-06, M3 decision 16; WP-M3-15d, protocol 40)**: `Shared/Simulation/MoveSteering`, inside `MovementSim.Step`, so server phase 2, client prediction and the bots compute the same integers frame by frame. Binary angles (65536 a turn, 0 = +Y, clockwise).
  - **The frame** carries the world key bits (the client maps the screen keys by the camera step, R-2.2-05), the mouse **aim** (10 bits) and a **locked** flag (1 bit); no facing (protocol 40: with the walk independent of the lock it had no authoritative use). Spec 1.7.
  - **Facing** (`MoveSteering.FacingFor`): while an action runs, the action's locked facing; with a key direction held, the keys' world heading; else the facing kept (after an action, that action's aim). The lock plays no part.
  - **Move heading** (`TryMoveHeading`): the keys' world direction, lock or not: the axis step (the diagonal scaled by `InvSqrt2Q16`), bit for bit as before decision 14.
  - **Action aim** (spec 3.1; the `ActionDef.aim` column, read as `ActionTiming.Aim`; `ActionMachine.AimOf`): `body` for a move skill (the dodge, the Archer's Dash and Back Step): it starts from the body's facing and dashes along the world keys held at the press, lock or not (none held: the facing plus `directionOffsetDeg`, backward); `ground` for an area placed at the pointer (Ice Pike): the mouse aim, lock or not; `target` (the default) for every other press (attacks, projectiles, Jump Strike, the parry, buffs and self skills, an area cast from the character like Flamethrower's cone): the **lock aim** when the server resolved one for the frame, else the mouse aim. The user's answer (2026-11-03): only areas placed at the pointer keep the mouse under a lock. Requests (an item use, a harvest) and the monster AI keep the facing.
  - **The lock on the server:** the frames' locked flag switches the aim on the exact frame the client predicted. The reliable `TargetLockChanged` (id 55, sent on every change, 0 when cleared) names the target; the router keeps it on the session's input queue (`InputQueue.LockTargetId`), read before that tick's frames. Phase 3, once per player per tick before its action step (`GameWorld.ResolveLockAim`): when the press frame is locked, the target's position history (R-3.1-03) on the frame's clamped view clock (`tick * 64 - DefenceTimeline.Lag64`, between two recorded ticks by the sub-tick, `PositionHistory.TryGetAtView64`) gives the lock aim (`Shared/Simulation/LockAim.Toward`) from the player's position after this tick's walk. A target the server does not know (no message yet, gone, not a combatant), dead, or not in this player's interest set (out of view or hidden from it, rule 9) leaves the press at the mouse. Counters for tests: `LockAimsResolved`, `LockAimNoTarget`, `LockAimDeadTarget`, `LockAimNotInView`. No server-side lock range: the lock's range and break rules stay the client's (`TargetLock`). Decision 15's facing clamp (`lockFacingToleranceDeg`, telemetry `LockFacingClamp` 103, now retired) is gone.
  - **Client:** `PredictionDevClient` sends the camera-mapped keys, the mouse aim from `GroundAim` (the last valid aim repeated while the pointer is over UI or off the ground), the locked flag and the lock message on every change; while locked it gives the predictor the locked monster's drawn position (`TargetLock.TryTargetPosition`), kept per pending frame so replays aim the same way, and the predictor aims presses from the walked position toward it exactly as server phase 3 (a small difference from the server's history comes back with the snapshot's action facing, `ActionMachine.Rebase`). The bots walk by world keys, aim through the frame's aim, and with `BotBehaviour.lockTarget` (on for the CI bot-smoke and the 50-bot runs) lock the monster they fight, so the load test runs the lock aim path.
- Client side (backlog D1, `unity/Assets/Game/Prediction`): the client predicts one `MovementSim` step per input frame it sends and keeps the frames until a snapshot acknowledges them. It compares the server state with the state it predicted after the acknowledged frame; only an error above `ClientPrediction.CorrectionToleranceCm` (at least 2 cm, because snapshots round each axis to whole cm) rebases on the server state and replays the later frames. The visual correction fades over `CorrectionSmoothingMs`, or snaps above `CorrectionSnapCm`. Other entities are drawn `StartingValuesM1.InterpolationDelayMs` behind, blended between snapshots. The client reads the same `data/tables` files as the server with the portable `Shared/Data/ConfigTableReader`.
- The server validates distance per tick against `maxSpeed(stat + mount)` with a tolerance, and counts violations for telemetry and bot scoring. M1: `Shared/Simulation/SpeedCheck` at the end of phase 2, measured from where the previous phase 2 left the entity (so moves made outside `MovementSim` are caught too; a dash the server moves in phase 3 moves this reference with it, D2), tolerance `ServerLoop.SpeedTolerancePercent`, times the frames applied that tick on a catch-up tick (one step each); a violation is counted and recorded as telemetry `SpeedViolation`, not corrected.
- Collision (M1): `MovementSim.Step` with the exported `WorldGrid` resolves the move one axis at a time (X, then Y) against the walkability grid, stopping each axis before the first non-walkable cell, so walls are slid along and corners cannot be cut; the mover is a point because the exporter grows blockers by the agent radius. Server phase 2 and client prediction run the same code (`Shared/World/GridCollision`). The client loads the same exported world, verified with the shared `Shared/World/WorldPackage` checks the server's loader uses, and draws the local player at the grid height under its predicted position. A stop just before a wall's +X or +Y edge rounds onto the wall cell in a whole-cm snapshot, so a rebase from a non-walkable position steps back one raw unit, which is exactly where the server stopped. Bodies: next bullet.
- **Body collision (M1, R-2.2-07)**: `Shared/Simulation/BodyCollision`, run by the server and by client prediction on the same bodies, so a blocked move ends on the same raw position.
  - **Who blocks whom:** `BodyCollision.Blocks` (player by monster, monster by player and monster; never player by player). Only living combatants block; they are gathered through `IBodyBlockers` into a `BodyList` in entity-id order.
  - **Contact:** a move enters a body when the centre distance drops below the two radii added up less `BodyCollisionSettings.overlapToleranceCm`. The move stops at its first contact (the nearest along the move; equal contacts go to the lower id), integer math with `SweptCircle`. With the `slide` response the rest of the move then goes on along the body's tangent, swept again, at most `maxSlideIterations` times (the last pass stops). A body the mover already touches or overlaps blocks only the part of the move towards its centre.
  - **Order of one move:** the body sweep, then the world grid (`GridCollision`), then for monsters the no-entry clip (`KeepOut`), then a guard: an end pushed deeper into a body by those clips does not move (a body against a wall). `MovementSim.Step` and `StepAlong` (walks, the `walkResponse`) and `MovementSim.Dash` (Dash effects, the `dashResponse`) take the bodies; with none they are the grid-only steps.
  - **Effect on other checks:** a blocked move only shortens, never longer than the unblocked one and never a jump. The speed check (below) and the lag-compensation history (recorded after the move) are unchanged.
  - **Server:** phase 2 (players' walks, every catch-up frame, and the monster AI's walk) and phase 3 (dashes) gather from the `SpatialGrid` (`World/BodyBlockers`) where the bodies stand at that moment: entities move in id order, so the bodies before the mover have already moved this tick. The mover yields; nothing pushes in M1.
  - **Client prediction:** `LocalPlayerPredictor.UseBodies` gathers the living monsters of the `EntityRegistry` at their **newest snapshot** position (`RegistryBodyBlockers`; radii by kind and def id, `BodyRadii`), once per predicted frame and once per replay (the same list for every replayed frame). That is the nearest the client knows to where the server collides. With a still monster (idle, attacking, waiting at reach: nearly every contact) both sides stop at the same place within the snapshot rounding (under 1 cm, inside `CorrectionToleranceCm`), so there is no correction. A monster walking into contact gives a correction of about what it walks over the round trip, faded by `CorrectionSmoothingMs`. The bots (`ClientCore`) predict the same way. The F4 hitbox view draws a cross where the own body touches a monster's.
  - **Config:** `BodyCollisionSettings` (singleton): `walkResponse` and `dashResponse` (`stop`, `slide`; placeholders `slide` and `stop`), `overlapToleranceCm` (0..50, placeholder 2), `maxSlideIterations` (1..4, placeholder 2). Without the table bodies block nothing. Reload class `Restart`: the client reads its tables only at start.
  - **Cross-table rules** (`CombatRules.TryBuild`, so `DataValidator`, the server start and the hot reload all check them):
    - the tolerance is below the smallest `bodyRadiusCm`;
    - two of the largest bodies fit the sweep (`SweptCircle.MaxRaw`, 4096 cm);
    - every monster's shortest attack hits the smallest class body standing at contact in front of it. Otherwise it would stand at contact and never attack (spec 3.1, monster AI).
  - **Not in M1:** the world grid still treats every mover as the 40 cm agent, so a large body (the boss) can overlap walls visually.
- **Input buffering:** a press during an action's last frames is buffered for a configurable window (`ActionDef.bufferWindowMs`) and fires when the action ends; the newest press wins (section 3.1, D2).
- While an action is active the sim ignores move bits and the stored facing is the one captured at action start. Facing changes between actions are rate-limited (sanity check).
- Target lock (R-2.2-06): `unity/Assets/Game/Input/Core/TargetLock` picks, cycles and breaks the lock; the candidates are the monsters as drawn at the render time (`TargetCandidates`); while locked, `PredictionDevClient` gives the predictor the locked target's drawn position (the view the server rewinds this player's hits to and aims its locked presses from, R-3.1-03), sends the frames' locked flag and the target in `TargetLockChanged` (the server's lock aim: the controls bullet above), and raises `WorldClientFeed.LockedTargetChanged` for the HUD (spec 9.12). Config: `TargetLockSettings` (client-only, `doublePressWindowMs`, `acquireRangeCm`, `breakRangeCm` between the acquire range and `StartingValuesM1.interestRadiusCm`, `cycleOrder` `distance` or `clockwise`; placeholders 300 ms, 1500 cm, 2000 cm, `distance`), the key in the `Hotkeys` row `targetLock` (Tab), checked by the table's set rule. A press acts at once (lock or cycle) and a second press within the window clears it, so a double press always ends unlocked; a dead target clears the lock without switching to the next.
- Hotbar layouts are stored server-side per character; the server validates that the used skill is learned and on the bar.

**Talks to** calls `ActionMachine` (start/cancel actions), `StatBlock` (speed), `Regions` (mount flags); emits `Moved` (sampled, telemetry only).

**Flow: Input to simulation**

```mermaid
flowchart LR
    n0["Client reads keys and mouse"]
    n1["Predict locally with MovementSim"]
    n2["Send input frame (with redundancy)"]
    n3["Server queues input"]
    n4{"In action?"}
    n5["Ignore move bits, keep locked facing"]
    n6["Run MovementSim"]
    n7["Validate speed"]
    n8["Snapshot to observers"]
    n9["Client reconciles"]
    n0 --> n1
    n1 --> n2
    n2 --> n3
    n3 --> n4
    n4 -->|"yes"| n5
    n4 -->|"no"| n6
    n6 --> n7
    n7 --> n8
    n8 --> n9
    classDef neg fill:#f6dde0,stroke:#a6414b;
    classDef pos fill:#ddebd5,stroke:#4c7a34;
```

## 2.3 World, spatial grid, interest management, instances, regions
**Rules**
- **R-2.3-01** One **continuous main map** with no loading screens, cities, biomes, monsters; small towns around it; 3 main cities with fast travel between them only. The map size is not used for sizing right now.
- **R-2.3-02** Instances (dungeons, raids, jail, GvG sieges) are separate worlds around the map, entered through portals.
- **R-2.3-03** Players only see what the fixed camera shows, so the **interest area is small**: the camera's ground footprint (rectangular/elliptical, aligned to the camera) plus a margin for fast movement and entities arriving. One update rate for everything inside.
- **R-2.3-04** Terrain streams in chunks on the client; entities never depend on chunk loading (the server sends height data or the client holds entities until ground exists).
- **R-2.3-05** Region flags decide PvP, honor policy, visibility checks, death drops, mounts, fees (section 2.3 table).
- **R-2.3-06** A region may need a **minimum level** (`RegionDef.minLevel`, 0 = none): a player below it cannot **enter** the region from a region that needs less; moving inside it or leaving it is never refused (one way, so a character already inside is never stuck). The shared grid collision applies it per cell step (`Shared/World/RegionGates`, `MoverGate` in `GridCollision` and `MovementSim`) to the walk (server phase 2) and to dashes and dodges (phase 3), and client prediction applies the same rule with the own level of `ProgressionState`, so both stop on the same position; monsters are never gated. The world check `GateLeak` (DataValidator, server start) refuses a player spawn inside a gated region and a gated region a lower-level walker reaches from the spawn. M2: the tutorial gate of spec 12.3 (R-12.3-01).

**Main map rules (designer decisions 2026-11-09).** R-2.3-07 to R-2.3-13 carry the decisions of the design talks recorded in `docs/design/city-layouts.md` (E1-E7), `docs/design/biome-sheets.md` (C1-C5) and `docs/design/open-world-content.md` (F1-F5). They are rules for the **main map** and its builders; the dev world `m1` (the implementation notes below) is unchanged. The settlement ids (C1-C3 main cities, M1-M6 medium cities, T1-T11 small towns) and the biome names are the placeholders of `docs/design/world-map.md`, whose own decisions (D1-D15, 2026-10-30) are not carried into the spec yet. Everything those files mark as a proposal, a guideline or Open is **not** a rule: it stays in the design files and is listed in 10.4. **The designer's answers of 2026-11-10** to the art reference questions (`docs/design/reference-boards-questions.md`, Q1-Q41; recorded as E8-E17 in `city-layouts.md` and C6-C18 in `biome-sheets.md`) are in R-2.3-07, R-2.3-11, R-2.3-14 and R-2.3-15 here, and in R-9.14-03, R-9.14-04, R-9.14-07 and R-11.6-05. Five answers change how the client draws or wait for its prototype (Q1, Q7, Q8, Q9, Q38) and one meets the M4 plan (mail and the auction, E14); they are **not** in the spec yet (10.4).
- **R-2.3-07** **Three settlement tiers and their services** (E1, `city-layouts.md`): **main cities** (the 3 of R-2.3-01) have everything; **medium cities** have the outpost, vendors, both crafting stations and a few player stalls; **small towns** have an outpost, one vendor and the respawn point. Travel to a bigger city stays meaningful, which supports the Merchant job (R-5.1-02). The table below is the rule; a cell marked Open is undecided (10.4).

  | Service | Main city | Medium city | Small town |
  |---|---|---|---|
  | Respawn point | yes | yes | yes |
  | Adventurer Guild outpost (sell, rank up, quests; R-4.2-08) | yes | yes | yes |
  | NPC vendors (R-4.2-08) | several | several | one |
  | Forge and jeweler's bench (R-5.3-07) | yes | yes | no |
  | Player shop stalls (R-4.5-03) | yes (the largest cap) | a few | no |
  | Job master (R-4.2-08) | yes | no | no |
  | Fast travel (R-2.3-01): **a travel stone on the plaza**, the same object in all three main cities (E14) | yes | no | no |
  | Inn (R-11.6-05, E14) | yes | **Open** | **Open** |
  | Raid gate (flag `raidStart`, R-6.7-02) | yes | no | no |
  | Arena queue (flag `arenaQueue`, R-6.6-09) | yes | **Open** | no |
  | Guards (R-6.3-01) | yes | yes | **Open** |
  | Walls (R-2.3-08) | full wall | palisade | none |

  **The lawless city** is a medium city by size, but **its services are Open** (10.4): the black market is there (R-4.4-02) and it has no guards (R-6.3-01); whether it has an outpost, crafting stations or stalls is not decided. **Stall places** (E14): how many a market has is left to economy tuning (the caps of R-4.5-03 are not set), and a market is built so that rows of stall places can be added or removed. Which respawn point a dead player goes to is not decided by E1: spec 3.1 still sends a player to the city's spawn marker, and "the nearest city or town" (`world-map.md` D7) is not in the spec yet.
- **R-2.3-08** **Walls by tier** (E2, `city-layouts.md`): main cities have **full walls**, medium cities a **palisade**, small towns are **open**. **Every walled settlement has one gate per road** that reaches it, and **the gate is where the safe zone starts** (R-6.3-07).
- **R-2.3-09** **Building kits and the character of each main city** (E3-E7, `city-layouts.md`): **three separate building kits, one per main city**, each with its own culture (E3). **C1 is a water city**: canals with flowing water run through it, and it has a port where ships can dock (E4). **C2 is a caravan bazaar city** (E5). **C3 is a classic medieval city** with walls and buildings (E6). **Medium cities and small towns borrow the nearest main city's kit**, reduced, with their own biome's materials and props on top; **the lawless city gets its own scrap-built look** (E7). What the designer has since decided about the kits and the plans is R-2.3-14; the rest (the kits' materials, which settlement borrows which kit, and the medium-city and small-town templates) are proposals to review (10.4).
- **R-2.3-10** **Biomes are visual only for now** (C3, `biome-sheets.md`): there are no cold, heat or hazard rules. Gameplay effects of a biome are **Open** (10.4).
- **R-2.3-11** **Biome borders** (C4, C5, `biome-sheets.md`): a border **follows a natural edge** (a river, ridge, road or cliff) where possible, with a **20-30 m blend** of ground, props and light (about one screen); the light presets of R-9.14-04 blend over the same distance. **One exception (C5): Palm coast meets Snow taiga at a cliff wall with ramps and no blend**; the taiga sits on a plateau above the coast, and the cliff follows R-11.6-04 (at least 3 m tall, marked with blockers). **At that cliff the light blends over the length of each ramp** (C14, Q30), because there is no blend of the ground to follow. Which natural edge each other border uses is a proposal (10.4).
- **R-2.3-12** **Safe roads and dangerous roads** (F1, `open-world-content.md`): **roads are safe from monsters near the start cities only.** In the three start biomes and the biomes next to them (11 biomes) no monster camp sits on or next to a road; in the other 9, camps may sit on or next to the road. "Safe" means safe from monsters: open-world PvP (the flags table below) still applies on every road. The distance that keeps a camp off a road and a world check for it are Open (10.4).

  | | Biomes |
  |---|---|
  | **Safe roads (11)** | Green vale, Heather moor, Amber woods (around C1); Red sands, Dry steppe, Bone canyons (around C2); Reed marsh, Palm coast, Fern jungle, Ash woods, Steam springs (around C3) |
  | **Dangerous roads (9)** | Rime coast, Lake country, Old forest, Fungal hollow, Dune isle, Snow taiga, Frost peaks, Storm cliffs, Ember caldera |
- **R-2.3-13** **Places in every biome** (F3, `open-world-content.md`): every biome has a **field boss arena**, **guarded resource spots** (R-5.2-05) and **lore spots**; that is 20 field bosses in total, built over time. How many of each a biome has and where they may stand are guidelines to review, and the field boss rules (respawn time, who shares a kill) are Open (10.4). Rest spots (F4) are R-6.3-08.
- **R-2.3-14** **Kits, city details and the accepted plans** (designer's answers of 2026-11-10, E8-E17 in `city-layouts.md`; content rules for city builders and artists, not server checks):
  - **M2 uses the water kit** (E8).
  - **Kit details** (E9). Water kit: the upper floors are **timber boards without pale panels**, and **every building keeps a quay or a pale stone edge between its roof and the water**. Bazaar kit: the dome is **whiter, a lime-washed white**, and the kit has **a muted teal glazed tile** as its one colour accent. Bazaar and medieval kits: **every wall has a darker band at its foot**.
  - **Roofs** (E10). Roof direction is mixed by place: **gables on squares and wide streets** where there is room, **eaves to the street in lanes and 6 m streets**. **Snow lies on roofs in patches**, so a kit's roof colour still shows on every building.
  - **The bazaar's cloth roof** (E11) is made of **panels**: all panels over the street hide while a player is under it (R-9.14-02), and the shade stays on the ground.
  - **C3's walls** (E12). The moat is **about 8 m wide**; **each of the five gates**, the tutorial gate included, has a bridge with a timber deck; the battlements are scenery (R-2.3-15); the keep stays **within about 15 m**.
  - **The three sea bridges** between the continents **share a look of their own** (E13): one neutral style of stone and timber, with a deck dark enough for attack warnings. They take no city's kit.
  - **The three main city plans are accepted** (E15), with the answers above. The plans themselves stay in `city-layouts.md`.
  - **The lawless city's layout is accepted** (E16): medium-sized, scrap walls with a gate per road, the black market in the middle around a forge-lit yard, no safe square, cover and more than one way out of every yard. Its services stay Open (R-2.3-07). How a wall hides one player from another there is tried in the fade prototype (Q38) and is not a rule yet (10.4).
  - **Scrap walls get a darker band at the foot** (E17), like the walls of the bazaar and medieval kits.
  - The colours on the reference boards are proposals; which buildings carry a dome is not decided (10.4). The inn and fast travel (E14) are in the table of R-2.3-07 and in R-11.6-05; mail and the auction (E14) are not in the spec yet (10.4).
- **R-2.3-15** **Biome sheets, terrain and landmarks** (designer's answers of 2026-11-10, C6-C18 in `biome-sheets.md`; content rules for world builders, not server checks):
  - **The sheets of all 20 biomes are accepted** (C6 for 19 of them, C15 for the Ember caldera), with the answers below. The accepted sheets stay in `biome-sheets.md`; the spec does not copy them.
  - **The Ember caldera** (C16, C18): lava keeps its strength (R-9.14-03) and **stays thin lines and small pools**, and a large lava area such as the crater's lake is kept to a small part of the screen; its cooling crust is a dark red-brown, away from the red warning. **The crater is a rim**: players walk the rim, which is a cliff (R-11.6-04), the lava lake below shows only at its edge, and nobody goes down.
  - **Canyon floors and the mountain pass are wide** (C7): the floors of the Bone canyons and the pass of the Frost peaks are wider than their walls are tall, so the walls stay plain terrain and never have to fade.
  - **Two landmarks are walk-in places** (C8): the hollow tree of the Old forest and the stepped temple of the Fern jungle. Their top hides like a building's roof (R-9.14-02); R-11.6-05 names them as the exception to "only service buildings".
  - **Nothing is walkable on top** (C9) of the fallen statue, the giant ribs over the canyon path, the giant mushroom or C3's wall walk; the path at the mushroom stays on the ground, and battlements and anyone on them are scenery (the one walkable layer of R-11.6-04).
  - **Stepped landmarks are low steps** (C10): the terraces of the Steam springs, the basalt columns of the Storm cliffs and the outside of the stepped temple are walkable as stairs or as ramps of up to 30 degrees (R-11.6-04).
  - **The Reed marsh** (C11): walkable reed beds are low (knee to waist) and never fade; shallow marsh water is unwalkable.
  - The road marks (C12) are in R-9.14-07, the light of the Fungal hollow (C13) and of the Ember caldera (C17) in R-9.14-04, and the light at the Palm coast cliff (C14) in R-2.3-11.

**Implementation**
- `SpatialGrid`: uniform cells (size about half the interest radius). Each entity is registered in a cell. `InterestManager` computes per-player visible sets from nearby cells and emits enter/leave (spawn/despawn) deltas when a player crosses a cell or an entity moves.
- `RegionDef` volumes are authored in Unity and exported with collision/navigation data by `Tools/UnityExporter` into server-readable files (heightmap, simplified blockers, navmesh, region volumes). A per-cell region lookup array makes flag checks one array read. M1: the walkability grid carries the region index and ground height per cell (`grid.bin`, `Shared/World/WorldGridFormat`); every walkable cell belongs to exactly one region (overlapping volumes and walkable ground outside every volume are export errors); a region's flags are the `RegionDefaults` row of its `RegionDef.areaType` (M1 carries `pvpMode`, honor effects and `deathDrop`; the other flags come with their systems). Overlapping walkable surfaces fit as extra grid layers; TODO(open): how an entity picks or changes layer, so v1 grids hold one layer.
- **The first field zone (M2, WP-M2-7, spec 12.8):** the M1 sample world grew into one world of the city and the zone (world id `m1`, `data/world/m1`, scene `M1Sample.unity`; the server still loads one `MMORPG_WORLD_DIR`): X -50..390 m, Z -200..200 m, 50 cm cells (880 x 800, `grid.bin` v2 run-length, about 80 KB). Regions: `m1.city` (normalCity), `m1.combat` (the M1 combat area by the gate) and four open-world rings `field1.ring1` to `field1.ring4` east of the gate (each several volumes; ring 1, wrapping `m1.combat`, is the tutorial area of 12.3), so the demand loop counts per type and ring (R-4.2-03). Low ridges with passes separate the rings; a road runs from the gate to the field boss's plateau (1 m up) at the far end. The client loads the authored scene for its look (`WorldSceneLoader`); it is one scene, so **no terrain streaming yet** (R-2.3-04 applies once the main map outgrows a scene). The zone has no bridge or other overlapping surface, so the layer question (10.4 [M1]) stays open. Interest and grid settings are unchanged (30 m interest radius, 15 m cells; checked by the 50-bot run in the zone, 11.5 `m2-zone`).
- `World` is the common abstraction (entity registry, grid, tick, interest query, collision). `WorldManager` creates instances from templates on demand, enforces player caps, tracks and destroys them. The main map and instances share the interface, so instances can later move to separate processes.
- **Instance entry** is an atomic handoff: remove from the source world, validate cap, create/join, transfer state, persist `worldId + position`. Reverse on exit. A crashed instance resolves to a defined outcome (return players to the main map).
- Large coordinates: positions are sent relative to a region origin or quantized; Unity uses a floating origin.

| Region flag | Values | Used by |
|---|---|---|
| pvpMode | off / armed-only / always-on | PvpRules |
| honorPolicy | allowed / unwelcome | GuardNpcAi, Disguise |
| visibilityChecks | off / forced-only / full | VisibilityService |
| deathDrop | on / off / confiscate | Death handling |
| mountsAllowed | true / false | Mounts |
| feeProfile | default / owner rates | FeeService |
| ownerGuildId | none / guild | Territory, FeeService |
| arenaQueue | true / false | Arena queue (cities only) |
| raidStart | true / false | Raid window start (cities only) |
| arenaRuleset | off / normalized | Stat normalization, neutral rank factor, no drops/honor |

**Default flags by area type**
| Area | PvP | Honor effects | Drops | Notes |
|---|---|---|---|---|
| Normal city | off | on | off | NPC guards attack bad-honor players; robe needed for them |
| Lawless city | always on | **off** (kills do not change honor) | **on** | taking someone's loot still adds the stolen mark, with no honor loss |
| Open world | armed only | on | on | loot bags, stolen marks |
| Regular dungeon | off | off | **off** | party instance |
| Raid | off | off | off | personal loot |
| Arena | always on (teams) | off | off | normalized stats |
| Siege | always on (sides) | off | off | |

**Rest spots have no row yet.** The designer decided (F4, 2026-11-09, R-6.3-08) that rest spots are small places outside cities that are safe from monsters and from PvP. Which area type and flags a rest spot has is **Open** (10.4): no row is added to this table, and no `RegionDefaults` row is written, until the questions listed there are answered.

**Talks to** emits `EnteredRegion`, `LeftRegion`, `InstanceCreated`, `InstanceClosed`; consumed by Law, Guilds, Economy.

**Flow: Enter an instance through a portal**

```mermaid
flowchart TB
    n0["Player uses portal"]
    n1["Validate level, rank, lockout, party"]
    n2{"Allowed?"}
    n3["Show reason"]
    n4["WorldManager finds or creates instance"]
    n5{"Cap reached?"}
    n6["Deny or queue"]
    n7["Atomic transfer: remove from source world"]
    n8["Add to instance with state"]
    n9["Persist worldId and position"]
    n10["Player in instance"]
    n0 --> n1
    n1 --> n2
    n2 -->|"no"| n3
    n2 -->|"yes"| n4
    n4 --> n5
    n5 -->|"yes"| n6
    n5 -->|"no"| n7
    n7 --> n8
    n8 --> n9
    n9 --> n10
    classDef neg fill:#f6dde0,stroke:#a6414b;
    classDef pos fill:#ddebd5,stroke:#4c7a34;
    class n3,n6 neg;
    class n10 pos;
```

## 2.4 Visibility: Sense, Detect, line of sight
**Rules**
- **R-2.4-01** By default a player sees what is in **eye vision** (inside the camera shape and not blocked) and not hidden. Entities that are **hidden by a skill**, or **outside eye vision** (beyond the camera range or blocked by walls/terrain), go through the check; if it passes, the observer **senses** them.
- **R-2.4-02** Every player has **Sense** (hides) and **Detect** (reveals). With distance: `effectiveDetect = Detect x distanceFactor(distance)`. If the hider's Sense > effectiveDetect the hider stays hidden; if equal both see each other; if Sense < effectiveDetect the observer sees the hider. Closer distance makes detection easier (distance bands).
- **R-2.4-03** Area effects hit hidden players. **Monsters** use the same rule and must pass it to aggro. Attacks and skills **break hiding or raise Sense temporarily**. **Party members always see each other.** M4 (WP-M4-2): `World/PartyVisibility` wraps the world's `IVisibilityQuery` (today `EveryoneVisible`): two players of one party are visible to each other whatever the inner query says; every other pair is the inner query's (inert in play until hiding exists).
- **R-2.4-04** Checks are **disabled in cities** except for flagged entities (bad-honor, disguised, hidden by skill).
- **R-2.4-05** Hidden entities are **never sent** to clients.
- **R-2.4-06** The **disguise robe** (6.2) gives any player Sense, and **any observer (guards or ordinary players)** is tested against it with the same Sense vs Detect check.

**Implementation**
- `IVisibilityQuery.CanSee(observer, target)` is called by the interest layer. Order, cheap to expensive: region mode, same party, camera shape, distance, Sense vs Detect with distance factor, line of sight.
- Line of sight: grid/heightmap ray stepping (DDA) against server collision data; results cached per pair and reused until either entity moves to a new sub-cell; checks run at 2-5 Hz in a per-tick time budget.
- **Hysteresis:** the reveal threshold is slightly easier than the hide threshold, so boundary cases do not flicker or spam spawn/despawn.
- Temporary Sense changes are timed `StatModifier`s (source: skill), so recomputation happens through the stat system.
- A per-region set of **flagged entities** limits city checks to a few players; guards evaluate only that set at low rate.

**Talks to** reads `StatBlock`, `Regions`, `CollisionData`, `Party`; emits `Detected`, `Revealed`; consumed by GuardNpcAi, monster aggro.

**Flow: Visibility decision for one pair**

```mermaid
flowchart TB
    n0["Interest layer asks CanSee"]
    n1{"Region checks off and not flagged?"}
    n2["Visible"]
    n3{"Same party?"}
    n4{"Inside camera shape and hidden?"}
    n5{"Line of sight clear?"}
    n6["Run Sense vs Detect with distance factor"]
    n7{"Detect high enough?"}
    n8["Sensed and sent"]
    n9["Hidden, not sent"]
    n0 --> n1
    n1 -->|"yes"| n2
    n1 -->|"no"| n3
    n3 -->|"yes"| n2
    n3 -->|"no"| n4
    n4 -->|"no, in view and not hidden"| n5
    n5 -->|"yes"| n2
    n5 -->|"no"| n6
    n4 -->|"hidden or out of range"| n6
    n6 --> n7
    n7 -->|"yes"| n8
    n7 -->|"no"| n9
    classDef neg fill:#f6dde0,stroke:#a6414b;
    classDef pos fill:#ddebd5,stroke:#4c7a34;
    class n9 neg;
    class n2,n8 pos;
```

## 2.5 Network security
**Rules**
- **R-2.5-01** The Auth API issues a **signed, single-use session ticket** (account, realm, slot, about 60 s expiry, nonce, key ID). The client sends it in the LiteNetLib connection request, and the server **validates it before accepting the peer**.
- **R-2.5-02** The Auth API also gives the client a **session key** over HTTPS; the same key travels inside the ticket encrypted with the server secret. The **reliable channel is encrypted** with authenticated encryption (ChaCha20-Poly1305 or AES-GCM, sequence-based nonce, replay window). The unreliable channel is not encrypted, but **client inputs carry a short integrity tag** (truncated MAC with the session key).
- **R-2.5-03** Unused LiteNetLib features are disabled (unconnected messages, NAT punch-through, discovery). Limits: connection attempts per IP, pending and total peers, packet size, packets per second per peer, reliable backlog, and actions per second per character.
- **R-2.5-04** Decoders bound-check every length and count, cap MemoryPack message sizes, never deserialize client-chosen types, and are fuzz-tested.
- **R-2.5-05** Infrastructure: only the UDP game port and the Auth API (HTTPS) are public; database and Redis are private; the **admin panel is reachable only through a VPN or IP allowlist**. **AWS Shield Standard** at launch; Global Accelerator may be added later for latency. Signing keys live in the parameter store and rotate by key ID.
- **R-2.5-06** Security events (failed connects, MAC or decryption failures, rate-limit hits) go to telemetry and abuse scoring.
- **R-2.5-07** **Every MemoryPack payload is guarded before deserialization.** One entry point reads every reliable message: the id must be registered, the message must fit the id's size cap (from config for client messages), and the whole body must match the id's declared layout (member counts, bounded strings in strict UTF-8, bounded array counts, nothing left over) before MemoryPack is called. The guard is allocation free and linear in the message size, and the cap is checked first, so it cannot be used to amplify CPU or memory. A new message id cannot be read without a layout and, when clients send it, a cap. Reason: MemoryPack trusts its length headers; a crafted string header makes it read past the buffer and kills the process (2026-10-25).

**Implementation**
- `TicketValidator` (signature, expiry, nonce cache in Redis: key `{prefix}nonce:{hex}` set if absent with the ticket's remaining lifetime as TTL, checked synchronously during connection approval; if Redis cannot be reached the ticket is refused with `ServerUnavailable`), `SessionCrypto` (per-direction ChaCha20-Poly1305 on reliable messages, truncated HMAC-SHA256 tag on client input packets) wrapping whole messages inside the transport rather than as a LiteNetLib packet layer (a packet layer would have to parse LiteNetLib's internal datagram format; the cost is that LiteNetLib headers and acks are not authenticated), `RateLimiter` per IP (IPv6 per /64), server-wide and per peer, limits in the `NetSecurity` table, fuzz tests for every decoder in CI. Reliable messages (R-2.5-07): `Shared/Protocol/ReliableMessageReader` holds one spec per `ReliableMessageId` (direction, body type, `MemoryPackLayout`, protocol bound) and is the only code that calls MemoryPack's deserializer, after `MemoryPackGuard` accepted the body; `InputRouter` runs `ReliableMessageReader.IsWellFormed` with the id's `ReliableMessageCaps` cap on every reliable message before it routes anything (a client id without a row is refused, and the DataValidator reports the missing row). Tests fail for an id without a spec or a fuzz sample, and for a client id without a route. `SessionCrypto` is in `Shared/Security` so client and server share one framing; the server plugs in the platform ChaCha20-Poly1305, the Unity client a managed RFC 8439 implementation (interop tests prove they match byte for byte). Client side: `ClientConnection` in `unity/Assets/Game/Net/Client` surfaces `ConnectRejectReason` for the login UI and drops the connection when a server message fails to open.
- Plain HTTP (R-2.5-02, R-2.5-05): the Auth API and the admin panel listen on plain HTTP only on loopback (`GameServer/Host/HttpListenPolicy`). Dev containers (docker compose, spec 11.3; user decision 2026-10-26): inside a container a service must listen on the wildcard address for its published port to reach it, so a wildcard bind is allowed only when the build has the dev login (`DevLogin`, Debug), `MMORPG_DEV_CONTAINER_HTTP=true` is set and the official .NET image's `DOTNET_RUNNING_IN_CONTAINER=true` holds; `docker-compose.yml` publishes those ports on the host's 127.0.0.1 only. A Release build never allows it. TODO(infra): TLS in front of both for staging and production.

**Flow: Secure connection**

```mermaid
flowchart LR
    n0["Client gets ticket and session key from Auth API over HTTPS"]
    n1["Connect request carries ticket"]
    n2{"Signature, expiry, nonce, realm valid?"}
    n3["Reject before allocating a peer"]
    n4["Accept peer, derive keys"]
    n5["Reliable channel encrypted, inputs tagged"]
    n6["Session running"]
    n0 --> n1
    n1 --> n2
    n2 -->|"no"| n3
    n2 -->|"yes"| n4
    n4 --> n5
    n5 --> n6
    classDef neg fill:#f6dde0,stroke:#a6414b;
    classDef pos fill:#ddebd5,stroke:#4c7a34;
    class n3 neg;
    class n6 pos;
```
