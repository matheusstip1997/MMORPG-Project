namespace Game.Dev.Qa
{
    /// <summary>
    /// Limits of the QA smoke run itself (how long to wait, how close to stand). Gameplay numbers (HP, timings, ranges,
    /// respawn delay) are never here: the scenario reads them from the config tables.
    /// </summary>
    public static class QaTimeouts
    {
        /// <summary>Screenshot size; the login screens' reference resolution is 1280 x 720 (LoginSettings).</summary>
        public const int ShotWidth = 1280;
        public const int ShotHeight = 720;

        /// <summary>A scene load, a login screen change or an Auth API answer.</summary>
        public const float UiSeconds = 20f;

        /// <summary>From Enter world to the first own snapshot in the world scene.</summary>
        public const float EnterWorldSeconds = 30f;

        /// <summary>For the local player's visual to load (Addressables, asynchronous).</summary>
        public const float VisualSeconds = 10f;

        /// <summary>The whole walk from the spawn to the demon.</summary>
        public const float WalkSeconds = 60f;

        /// <summary>
        /// Parry-boss step (fix/parry-feel, fixer round 3): the press goes from this many ticks before the drawn hit, on the
        /// first frame that gets there. The window is the parry's Active phase, 1 to 5 ticks after the press on the view clock
        /// (spec 12.7: 50 ms startup, 200 ms window); with the server judging the press up to 3 ticks late (the clamped view lag
        /// measured on main) a lead of 4 to 5 ticks stays in it. The press lands about a frame after this point.
        /// </summary>
        public const double ParryLeadTicks = 5.0;

        /// <summary>
        /// Parry-boss step: an attack first seen with less than this left before its hit is skipped (no fair press). With
        /// <see cref="ParryLeadTicks"/> a band of 1.5 ticks, 75 ms: the old band of 0.5 ticks was shorter than a loaded batch-mode
        /// frame, so a frame could step over it and lose a parryable attack (runs a3, a5, w2-warrior-b, w2-warrior-c).
        /// </summary>
        public const double ParryLatestLeadTicks = 3.5;

        /// <summary>
        /// Parry-boss step: below this level only heavy armor attempts the boss. By R-3.3-04 light armor parries only its green
        /// swipe and medium also its blue sweep, and its openers take a level-1 mage's 65 HP or assist's 80 HP before those come
        /// (QA wave 2 F3). A harness limit, no game rule: the level-12 runs with <c>-SeedStack</c> attempt it, and level 12 is what
        /// the job steps need anyway.
        /// </summary>
        public const int ParryBossMinLevel = 12;

        /// <summary>
        /// Lock-demon (QA wave 2 F1): a press waits until a strike the target has drawn lands at least this many ticks after the
        /// wind-up's end (the server may judge it later on the view clock), so the wind-up is not flinched.
        /// </summary>
        public const double WindUpMarginTicks = 3.0;

        /// <summary>Lock-demon: presses until the locked attack lands (a wind-up can still be flinched), and its walk legs' tries.</summary>
        public const int LockAttackTries = 4;
        public const int LockedLegTries = 3;

        /// <summary>
        /// Parry-boss step: the walk goes along a lane this far north of the boss marker, then this far west of it, so it
        /// passes outside the other spawns' aggro (smoke pf1: the straight path pulled demon b to the boss).
        /// </summary>
        public const int ParryBossLaneOffsetCm = 500;

        /// <summary>A walk to a ground point is done this close to it.</summary>
        public const int WaypointReachedCm = 100;

        /// <summary>
        /// Learn-skill step (QA wave 1 F1): the character counts as at the city spawn this close to its marker; farther (an
        /// earlier run logged out beside the boss) it walks back first.
        /// </summary>
        public const int CitySpawnRadiusCm = 300;

        /// <summary>Where pick-job stands: this far south of the job master's marker, inside its 3 m range (WP-M3-1).</summary>
        public const int NpcStandOffCm = 150;

        /// <summary>Learn-skill step: waited past <c>StartingValuesM1.combatStateMs</c> after the last hit (a tick and the link).</summary>
        public const int CombatStateMarginMs = 500;

        /// <summary>Parry-boss step: stand this close to the boss (inside every melee attack's shape).</summary>
        public const int ParryBossApproachCm = 250;

        /// <summary>Parry-boss step: parries before the step fails without a success, and the time it may take.</summary>
        public const int ParryBossAttempts = 6;
        public const float ParryBossSeconds = 60f;

        /// <summary>
        /// Fail the walk when the server position moved less than <see cref="StuckCm"/> in this time (the kill step); the
        /// walk to a point paths again instead.
        /// </summary>
        public const float StuckSeconds = 3f;
        public const int StuckCm = 20;

        /// <summary>The probe step that tells a key direction pressed dead into a wall face from one that moves (QA walk).</summary>
        public const int WalkProbeCm = 10;

        /// <summary>Stand this share of the basic attack's reach away from the target's center.</summary>
        public const int ApproachPercentOfReach = 55;

        /// <summary>Killing the demon with the class's kit, re-engaging after the player's deaths included.</summary>
        public const float KillSeconds = 150f;

        /// <summary>
        /// Lives the kill step may use: after a death it waits for the respawn and walks back (the demon has leashed home
        /// to full HP by then, so each life is a fresh fight). This many deaths fail the step with "the player died".
        /// </summary>
        public const int KillLives = 3;

        /// <summary>The step log calls the player's HP low below this share of max HP.</summary>
        public const int LowHpPercent = 25;

        /// <summary>The kill step uses a ready heal of the kit on itself below this share of max HP (QaFightPlan).</summary>
        public const int HealBelowPercent = 50;

        /// <summary>Below this share of max HP with no heal ready, the kill step backs off from the demon (QaFightPlan).</summary>
        public const int BackOffBelowPercent = 20;

        /// <summary>
        /// One leg of a back-off walks this far away from the demon, for at most <see cref="BackOffSeconds"/>, along a
        /// direction that stays out of no-entry regions and inside the demon's leash (QaNavigation.TryBackOffTarget).
        /// </summary>
        public const int BackOffCm = 800;
        public const float BackOffSeconds = 3f;

        /// <summary>A back-off ends this far inside the demon's <c>leashRangeCm</c> from its home, so it does not reset.</summary>
        public const int BackOffLeashMarginCm = 400;

        /// <summary>
        /// After backing off, the kill step walks back once HP is at this share of max HP or a heal is usable (QA wave 5
        /// H2); until then it keeps backing off, for at most <see cref="BackOffRecoverSeconds"/>.
        /// </summary>
        public const int ReengageAbovePercent = 40;
        public const float BackOffRecoverSeconds = 30f;

        /// <summary>Back-offs and step-backs end this far outside other spawn points' monster aggro ranges.</summary>
        public const int AvoidAggroMarginCm = 300;

        /// <summary>Body-block (R-2.2-07) holds the move toward the demon's centre this long.</summary>
        public const float BodyBlockSeconds = 3f;

        /// <summary>Body-block: both positions are whole cm in the snapshots, so a distance can read this much short of contact.</summary>
        public const int BodyRoundingCm = 2;

        /// <summary>Body-block: the closest distance must come this near contact (the player reached the body).</summary>
        public const int BodyTouchCm = 10;

        /// <summary>Walk-to-demon casts the kit's opening buffs this far outside the demon's <c>aggroRangeCm</c> (H1).</summary>
        public const int BuffUpMarginCm = 200;

        /// <summary>After the kill, how long a buff cast just before it gets to show in the buff row.</summary>
        public const float BuffRowSeconds = 2f;

        /// <summary>Stepping back to make room for a forward-dash skill (Jump Strike) gives up after this long.</summary>
        public const float StepBackSeconds = 3f;

        /// <summary>
        /// A demon that leaves the snapshots while the player is alive and this close to it has died (it is far inside the
        /// interest area); farther away it only went out of sight.
        /// </summary>
        public const int GoneNearCm = 500;

        /// <summary>From the key press to the projectile's spawn, and from the spawn to its despawn.</summary>
        public const float ProjectileSeconds = 5f;

        /// <summary>Use walls up to this share of the projectile's range, so the shot surely reaches one.</summary>
        public const int WallPercentOfRange = 80;

        /// <summary>Directions tried when looking for a wall to shoot at.</summary>
        public const int WallRays = 72;

        /// <summary>Skip a shot direction that passes this close to a monster.</summary>
        public const int ShotClearanceCm = 200;

        /// <summary>Waiting for the demon's drop, walking to it, the pickup answer and the saved bag (class 2: about 1 s).</summary>
        public const float LootSeconds = 15f;

        /// <summary>Walk this share of <c>LootSettings.pickupRangeCm</c> close to a drop before pressing pickup.</summary>
        public const int LootApproachPercentOfRange = 40;

        /// <summary>A respawn may come this much later than <c>monsterRespawnMs</c> before the step fails.</summary>
        public const float RespawnMarginSeconds = 15f;

        /// <summary>A respawned monster counts when it appears this close to its spawn marker.</summary>
        public const int RespawnMarkerRadiusCm = 300;

        /// <summary>How long a key or button is held for one press (two ticks at 20 Hz).</summary>
        public const float PressSeconds = 0.1f;

        /// <summary>Frame rate of the run (batch mode has no vsync).</summary>
        public const int FrameRate = 60;

        /// <summary>Screenshot of the empty spawn point this long into the respawn wait.</summary>
        public const float MidWaitShotSeconds = 10f;

        /// <summary>Frames to let the UI and views settle before a screenshot.</summary>
        public const int SettleFrames = 3;

        /// <summary>The chat step holds W this long while the chat input is open; the player must not move.</summary>
        public const float ChatTypingHoldSeconds = 0.5f;

        /// <summary>From Enter to the echo of the own chat line (one round trip and a tick).</summary>
        public const float ChatEchoSeconds = 5f;

        /// <summary>
        /// Target lock step (R-2.2-06, M3 decision 16): the locked attack's facing and the body after it within this of the
        /// direction to the demon (it is drawn an interpolation delay behind and may move), and a locked ground skill within this
        /// of the mouse.
        /// </summary>
        public const double LockFacingToleranceDeg = 20.0;

        /// <summary>
        /// Target lock step: the mouse is put this far around the player from the demon (M3 decision 16 QA: 60 degrees), so only
        /// the lock aims the attack at the demon and the locked walk is not turned by it.
        /// </summary>
        public const double LockMouseOffsetDeg = 60.0;

        /// <summary>Target lock step: the facings settle (past a hit's flinch or a running action) within this.</summary>
        public const float LockSettleSeconds = 3f;

        /// <summary>Target lock step: the double press starts this much later than the double-press window after the lock press.</summary>
        public const float LockDoublePressMarginSeconds = 0.2f;

        /// <summary>
        /// Target lock step: how long to wait, <paramref name="sinceLockPressSeconds"/> after the press that locked, before the
        /// double press that clears. R-2.2-06: a press within <c>doublePressWindowMs</c> of the one before is the second of a
        /// double press, so a first tap sooner pairs with the lock press, clears, and the second tap locks again.
        /// </summary>
        public static float SecondsBeforeDoublePress(int doublePressWindowMs, float sinceLockPressSeconds) =>
            System.Math.Max(0f, doublePressWindowMs / 1000f + LockDoublePressMarginSeconds - sinceLockPressSeconds);

        /// <summary>Dev tools step: shapes, timeline samples and tick times come within this (telemetry is polled).</summary>
        public const float DevToolsSeconds = 10f;

        /// <summary>From a table edit to the client applying the server's accepted reload (debounce, checks, telemetry poll).</summary>
        public const float ReloadSeconds = 30f;

        /// <summary>The dev tools step lengthens the normal attack's recovery by this much in the run's tables copy.</summary>
        public const int ReloadRecoveryDeltaMs = 50;

        /// <summary>Frames a pointer move to a slot takes (a drag passes <c>dragThresholdPx</c> on the way, F1).</summary>
        public const int PointerSteps = 8;

        /// <summary>The refused-move drag ends this far left of the inventory window, over the world (F1).</summary>
        public const float DropOutsidePx = 120f;

        /// <summary>After a refused drag out, how long nothing may be sent for it (a move answer is one round trip, F1).</summary>
        public const float RefusedMoveSeconds = 1f;

        /// <summary>A movement skill of the kit tour needs this much clear ground beyond its dash distance (F3).</summary>
        public const int DashClearanceCm = 100;

        /// <summary>The server position must move at least this share of a movement skill's dash distance (F3).</summary>
        public const int DashPercentOfDistance = 70;

        /// <summary>To face a direction, the QA mouse is put over the ground this far that way (F3).</summary>
        public const int AimAheadCm = 300;

        /// <summary>
        /// Lock-demon: the QA mouse beside the demon this far from the player, close enough that the pointer stays off the HUD
        /// (M3 decision 15: the client keeps the last aim while the pointer is over UI, and 300 cm toward the bottom of the
        /// screen reaches the hotbar).
        /// </summary>
        public const int LockAimCm = 150;

        /// <summary>Footwork: how long each leg holds its key (a shot leg's shot is taken at its end, keys held).</summary>
        public const float FootworkLegSeconds = 1.2f;

        /// <summary>Footwork: a leg must move the player at least this share of move speed x leg time.</summary>
        public const int FootworkPercentOfDistance = 50;

        /// <summary>Footwork: the movement may be this far off the leg's direction, and the body this far off the walk.</summary>
        public const float FootworkToleranceDeg = 30f;

        /// <summary>Footwork, the aim check (M3 decision 15): the mouse this far clockwise of screen-up when the attack is pressed.</summary>
        public const float FootworkAimDeg = 60f;

        /// <summary>The aim checks: the attack's facing and the body after it within this of the mouse aim.</summary>
        public const float AimToleranceDeg = 5f;

        /// <summary>
        /// Lock-demon, the locked walk (R-2.2-06, M3 decision 16): each key held this long, short enough that the leg stays near
        /// the demon for the fight after it (its direction checked within <see cref="FootworkToleranceDeg"/>).
        /// </summary>
        public const float LockedLegSeconds = 0.3f;

        /// <summary>Tutorial gate (R-12.3-01): the push starts this many cells before the boundary and aims as many past it.</summary>
        public const int GateDepthCells = 4;

        /// <summary>Tutorial gate: how long the player pushes into the gate (the prompt must show within it).</summary>
        public const float GatePushSeconds = 2f;
    }
}
