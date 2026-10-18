namespace Shared.Data.Tables
{
    /// <summary>
    /// Labels, button texts and other fixed UI text (spec 9.1: no user-facing text in code). Client-only. <see cref="Text"/>
    /// may hold <c>{name}</c> placeholders the client fills. Localization is a later item (spec 10).
    /// </summary>
    [Table("UiStrings", 1)]
    public sealed class UiStrings : IValidatableRow
    {
        [TableId] public string Id { get; set; } = "";

        public string Text { get; set; } = "";

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }
    }
}
