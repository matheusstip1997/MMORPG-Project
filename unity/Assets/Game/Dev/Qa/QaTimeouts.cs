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

        /// <summary>Fail the walk when the server position moved less than <see cref="StuckCm"/> in this time.</summary>
        public const float StuckSeconds = 3f;
        public const int StuckCm = 20;

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

        /// <summary>A back-off walks this far away from the demon, for at most <see cref="BackOffSeconds"/>, then fights on.</summary>
        public const int BackOffCm = 800;
        public const float BackOffSeconds = 3f;

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
    }
}
