namespace Shared.Data.Tables
{
    /// <summary>Server status on the selection screen (R-9.10-02).</summary>
    public enum ServerStatus
    {
        Online,
        /// <summary>At the cap: the player waits in the login queue (R-9.10-03).</summary>
        Full,
        /// <summary>Not selectable (R-9.10-02).</summary>
        Maintenance,
    }

    /// <summary>
    /// The servers on the selection screen (spec 9.10 <c>ServerDirectory</c>). Client-only for M1: one dev server.
    /// TODO(B5-serverlist): status, population and the login queue come from <c>ServerListResponse</c> once the Auth API
    /// has a realm directory; the population thresholds of R-9.10-02 belong here then.
    /// </summary>
    [Table("ServerDirectory", 1)]
    public sealed class ServerDirectory : IValidatableRow
    {
        [TableId] public string Id { get; set; } = "";

        /// <summary>Shown in the server table.</summary>
        public string Name { get; set; } = "";

        /// <summary>Shown in the server table (R-2.1-03).</summary>
        public string Region { get; set; } = "";

        /// <summary>The realm this server runs (R-2.1-03); the Auth API character calls use it.</summary>
        [Range(1, 65_535)] public int RealmId { get; set; }

        /// <summary>Game server address for the connection after enter world. TODO(realm-directory): from the Auth API.</summary>
        public string GameHost { get; set; } = "";

        [Range(1, 65_535)] public int GamePort { get; set; }

        public ServerStatus Status { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (string.IsNullOrWhiteSpace(Name))
                errors.Add(nameof(Name), "must not be empty");
            if (string.IsNullOrWhiteSpace(GameHost))
                errors.Add(nameof(GameHost), "must not be empty");
        }
    }
}
