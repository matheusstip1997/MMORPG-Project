namespace Shared.Data.Tables
{
    /// <summary>
    /// Combat debugging tools and config hot reload in dev (backlog J and A, spec 11.7). The server reads the reload and
    /// strike trace values only in a build with <c>DevTools</c> and with <c>MMORPG_DEV_TOOLS</c> on; the client reads the
    /// rest only in the editor or a <c>MMORPG_DEV_TOOLS</c> build. The keys are <see cref="Hotkeys"/> rows.
    /// Values in data/tables/DevTools.json are placeholders.
    /// </summary>
    [Table("DevTools", 1)]
    [Singleton]
    public sealed class DevTools : ISetValidatableRow
    {
        /// <summary>The server reloads the tables once no table file has changed for this long (editors save in bursts).</summary>
        [Range(50, 10_000)] public int HotReloadDebounceMs { get; set; }

        /// <summary>The server writes every strike it tests and every target it tests it against to telemetry (spec 7.2).</summary>
        public bool StrikeTrace { get; set; }

        /// <summary>How often the dev client reads what the local server appended to its telemetry file.</summary>
        [Range(20, 5_000)] public int TelemetryPollMs { get; set; }

        /// <summary>The tick timing panel summarises the ticks of this last stretch of server time.</summary>
        [Range(1_000, 600_000)] public int TickWindowMs { get; set; }

        /// <summary>Ticks of history the action timeline shows.</summary>
        [Range(20, 400)] public int TimelineTicks { get; set; }

        /// <summary>How long a hit shape stays drawn after the tick it was tested on.</summary>
        [Range(50, 10_000)] public int GizmoHoldMs { get; set; }

        /// <summary>Shapes the client placed from its own prediction or from snapshots. Colors are <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
        public string PredictedColor { get; set; } = "";

        /// <summary>Shapes and target circles from the server's strike trace: exactly what <c>HitResolver</c> tested.</summary>
        public string ServerColor { get; set; } = "";

        /// <summary>A target circle the server's strike touched.</summary>
        public string ServerHitColor { get; set; } = "";

        public string AreaColor { get; set; } = "";

        public string ProjectileColor { get; set; } = "";

        /// <summary>Body circles: the target radius every shape is tested against.</summary>
        public string BodyColor { get; set; } = "";

        /// <summary>The <see cref="Hotkeys"/> rows the dev views toggle with.</summary>
        public static readonly string[] Keys =
            { Hotkeys.DevWorldGeometry, Hotkeys.DevHitboxes, Hotkeys.DevTimeline, Hotkeys.DevTickTiming };

        public void Validate(ConfigSet set, RowErrors errors)
        {
            CheckColor(nameof(PredictedColor), PredictedColor, errors);
            CheckColor(nameof(ServerColor), ServerColor, errors);
            CheckColor(nameof(ServerHitColor), ServerHitColor, errors);
            CheckColor(nameof(AreaColor), AreaColor, errors);
            CheckColor(nameof(ProjectileColor), ProjectileColor, errors);
            CheckColor(nameof(BodyColor), BodyColor, errors);
            if (!set.TryGet(typeof(Hotkeys), out _))
                return;
            foreach (var key in Keys)
            {
                if (Hotkeys.Find(set, key) == null)
                    errors.Add(null, $"the dev views need a Hotkeys row '{key}'");
            }
        }

        private static void CheckColor(string field, string value, RowErrors errors)
        {
            if (!HudColor.TryParse(value, out _))
                errors.Add(field, $"'{value}' is not #RRGGBB or #RRGGBBAA");
        }
    }
}
