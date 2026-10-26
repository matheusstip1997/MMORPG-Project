using System;

namespace Shared.Data.Tables
{
    /// <summary>Corner, edge or center an element is placed from (spec 9.7 <c>anchor</c>).</summary>
    public enum UiAnchor
    {
        TopLeft,
        TopCenter,
        TopRight,
        MiddleLeft,
        Center,
        MiddleRight,
        BottomLeft,
        BottomCenter,
        BottomRight,
    }

    /// <summary>
    /// One element of a login screen (spec 9.10: <c>LoginLayout</c> uses the schema of <c>HudLayout</c>, spec 9.7).
    /// Client-only. Coordinates are in the reference resolution of <see cref="LoginSettings"/>. Element ids repeat across
    /// screens (<c>steam_badge</c>), so the row id is <c>&lt;screen&gt;.&lt;element&gt;</c>.
    /// </summary>
    [Table("LoginLayout", 1)]
    public sealed class LoginLayout : IValidatableRow
    {
        /// <summary><c>&lt;screen&gt;.&lt;element&gt;</c>, for example <c>title.press_any_button</c>.</summary>
        [TableId] public string Id { get; set; } = "";

        [Ref(typeof(LoginScreens))] public string Screen { get; set; } = "";

        public UiAnchor Anchor { get; set; }

        /// <summary>[x, y] from the anchor, towards the inside of the screen (spec 9.7); for centered axes, x right and y down.</summary>
        public int[] Offset { get; set; } = Array.Empty<int>();

        /// <summary>[width, height]; 0 means sized by content.</summary>
        public int[] Size { get; set; } = Array.Empty<int>();

        /// <summary>The element id within its screen (the part after the dot). A method: every property is a table field.</summary>
        public string ElementId() => Id.Length > Screen.Length + 1 ? Id.Substring(Screen.Length + 1) : "";

        public static string IdFor(string screen, string element) => screen + "." + element;

        public void Validate(RowErrors errors)
        {
            if (!Id.StartsWith(Screen + ".", StringComparison.Ordinal) || ElementId().Length == 0)
                errors.Add(nameof(Id), $"must be '<screen>.<element>' with screen '{Screen}'");
            UiLayoutRules.Check(Offset, Size, errors);
        }
    }
}
