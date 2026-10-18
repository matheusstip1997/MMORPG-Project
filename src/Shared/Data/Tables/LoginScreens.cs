using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// The login flow screens (spec 9.10 <c>LoginScreens</c>: art, music, order). Client-only. The ids are fixed because
    /// the client has one presenter per screen; art and music are placeholders (empty: a plain panel, no music).
    /// </summary>
    [Table("LoginScreens", 1)]
    public sealed class LoginScreens : IValidatableRow
    {
        public const string Title = "title";
        public const string ServerSelect = "serverSelect";
        public const string CharacterSelect = "characterSelect";
        public const string CharacterCreate = "characterCreate";

        public static readonly string[] Known = { Title, ServerSelect, CharacterSelect, CharacterCreate };

        [TableId] public string Id { get; set; } = "";

        /// <summary>Position in the flow (the order of the spec 9.10 screen table).</summary>
        [Range(0, 100)] public int Order { get; set; }

        /// <summary>Background art (a Resources path); empty for a plain panel.</summary>
        public string Art { get; set; } = "";

        /// <summary>Music (a Resources path); empty for none.</summary>
        public string Music { get; set; } = "";

        public void Validate(RowErrors errors)
        {
            if (Array.IndexOf(Known, Id) < 0)
                errors.Add(nameof(Id), $"'{Id}' is not a login screen (known: {string.Join(", ", Known)})");
        }
    }
}
