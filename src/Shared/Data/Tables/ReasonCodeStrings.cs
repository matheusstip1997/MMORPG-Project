namespace Shared.Data.Tables
{
    /// <summary>
    /// Reason code to message and icon (spec 9.1, 9.7 <c>ReasonCodeStrings</c>). Every refusal reaches the player as one
    /// of these rows; <see cref="ReasonCodeCoverage"/> checks that every reason code enum has one. <see cref="Text"/> may
    /// hold <c>{name}</c> placeholders the client fills (for example the versions of <c>VersionMismatch</c>).
    /// </summary>
    [Table("ReasonCodeStrings", 1)]
    public sealed class ReasonCodeStrings : IValidatableRow
    {
        /// <summary>Row used for a code that has no row of its own; the client puts the code into <c>{code}</c>.</summary>
        public const string UnknownCode = "UnknownReason";

        /// <summary>The enum member name, for example <c>NameTaken</c>.</summary>
        [TableId] public string Code { get; set; } = "";

        public string Text { get; set; } = "";

        /// <summary>Icon (a Resources path); empty for none until the UI art exists.</summary>
        public string Icon { get; set; } = "";

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Code))
                errors.Add(nameof(Code), "must not be empty");
            if (string.IsNullOrWhiteSpace(Text))
                errors.Add(nameof(Text), "must not be empty");
        }
    }
}
