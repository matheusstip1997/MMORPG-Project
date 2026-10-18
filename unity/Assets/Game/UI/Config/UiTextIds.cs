namespace Game.UI.Config
{
    /// <summary>
    /// Ids of the <c>UiStrings</c> rows the login screens show (spec 9.1: no user-facing text in code). An EditMode test
    /// checks that every constant here has a row in <c>data/tables/UiStrings.json</c>.
    /// </summary>
    public static class UiTextIds
    {
        public const string TitleLogo = "title.logo";
        public const string PressAnyButton = "title.press_any_button";
        public const string SigningIn = "title.signing_in";
        public const string Retry = "title.retry";
        public const string BadgeDev = "badge.dev";
        public const string BadgeSteam = "badge.steam";
        public const string BadgeSignedOut = "badge.signed_out";
        public const string VersionLabel = "version.label";

        public const string ServerHeader = "server.header";
        public const string ColumnName = "server.column.name";
        public const string ColumnRegion = "server.column.region";
        public const string ColumnPopulation = "server.column.population";
        public const string ColumnPing = "server.column.ping";
        public const string ColumnYours = "server.column.yours";
        public const string ColumnStatus = "server.column.status";
        public const string StatusOnline = "server.status.online";
        public const string StatusFull = "server.status.full";
        public const string StatusMaintenance = "server.status.maintenance";
        public const string ValueUnknown = "server.value.unknown";
        public const string ServerDetail = "server.detail";
        public const string ServerDetailNone = "server.detail.none";
        public const string EnterServer = "server.enter";
        public const string ServerLoading = "server.loading";

        public const string ServerLabel = "char.server_label";
        public const string SlotCounter = "char.slot_counter";
        public const string EmptySlot = "char.empty_slot";
        public const string Card = "char.card";
        public const string PendingDeletion = "char.pending_deletion";
        public const string Preview = "char.preview";
        public const string Detail = "char.detail";
        public const string DetailNone = "char.detail.none";
        public const string EnterWorld = "char.enter";
        public const string CreateCharacter = "char.create";
        public const string Delete = "char.delete";
        public const string BackToServers = "char.back";
        public const string ExitGame = "char.exit";
        public const string DeleteConfirm = "char.delete_confirm";
        public const string DeleteYes = "char.delete_yes";
        public const string DeleteNo = "char.delete_no";
        public const string CharLoading = "char.loading";
        public const string Entering = "char.entering";

        public const string CreateHeader = "create.header";
        public const string CreateNote = "create.note";
        public const string CreateNameLabel = "create.name_label";
        public const string CreateConfirm = "create.confirm";
        public const string CreateCancel = "create.cancel";
        public const string Creating = "create.creating";

        /// <summary>Prefix of a class display name: <c>class.&lt;classId&gt;</c>; a missing row shows the id.</summary>
        public const string ClassPrefix = "class.";
    }
}
