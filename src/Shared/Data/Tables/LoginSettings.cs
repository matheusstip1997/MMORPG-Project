namespace Shared.Data.Tables
{
    /// <summary>How UI panels scale to the screen (spec 9.7 <c>scaling</c>).</summary>
    public enum UiScaleMode
    {
        /// <summary>Scales with screen height; anchors stay on their corner (spec 9.7 "fit-height").</summary>
        FitHeight,
    }

    /// <summary>
    /// Login client settings (spec 9.10). Client-only. Holds the root fields of the layout schema (spec 9.7
    /// <c>referenceResolution</c>, <c>scaling</c>), which the table format cannot nest inside <see cref="LoginLayout"/>,
    /// plus where the client signs in and which scene the world loads into.
    /// </summary>
    [Table("LoginSettings", 1)]
    [Singleton]
    public sealed class LoginSettings : IValidatableRow
    {
        /// <summary>Spec 9.10: 1280 x 720, the same as the HUD.</summary>
        [Range(320, 7_680)] public int ReferenceWidth { get; set; }
        [Range(240, 4_320)] public int ReferenceHeight { get; set; }

        public UiScaleMode Scaling { get; set; }

        /// <summary>
        /// Base URL of the Auth API (spec 9.10 "Auth API v0"). v0 runs one Auth API per realm, so M1 signs in and lists
        /// characters at this one address. TODO(realm-directory): per-realm addresses from <c>ServerListResponse</c>.
        /// HTTPS, or plain HTTP on a loopback address for local dev only (<see cref="AuthEndpoint"/>, R-2.5-02).
        /// </summary>
        public string AuthApiUrl { get; set; } = "";

        /// <summary>Time limit for one Auth API call before the client reports it unreachable.</summary>
        [Range(1_000, 120_000)] public int AuthRequestTimeoutMs { get; set; }

        /// <summary>Unity scene path loaded after the game connection is accepted (R-9.10-05).</summary>
        public string WorldScene { get; set; } = "";

        public void Validate(RowErrors errors)
        {
            if (!AuthEndpoint.IsAllowed(AuthApiUrl, out var problem))
                errors.Add(nameof(AuthApiUrl), problem);
            if (string.IsNullOrWhiteSpace(WorldScene) || !WorldScene.EndsWith(".unity", System.StringComparison.Ordinal))
                errors.Add(nameof(WorldScene), "must be a scene path ending in .unity");
        }
    }
}
