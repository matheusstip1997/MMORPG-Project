#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using Shared.Data.Tables;

namespace Game.UI.Config
{
    /// <summary>A <c>{name}</c> placeholder and its value.</summary>
    public readonly struct TextArg
    {
        public TextArg(string name, string value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }

        public string Value { get; }
    }

    /// <summary>Fills <c>{name}</c> placeholders. Unknown placeholders stay as written, so a missing value is visible.</summary>
    public static class TextTemplate
    {
        public static string Fill(string template, params TextArg[] args)
        {
            if (args.Length == 0 || template.IndexOf('{') < 0)
                return template;
            var text = new StringBuilder(template);
            foreach (var arg in args)
                text.Replace("{" + arg.Name + "}", arg.Value);
            return text.ToString();
        }
    }

    /// <summary>Fixed UI text from <c>UiStrings</c> (spec 9.1). A missing id shows the id itself so it is easy to spot.</summary>
    public sealed class UiText
    {
        private readonly Dictionary<string, string> _texts = new Dictionary<string, string>(StringComparer.Ordinal);

        public UiText(IEnumerable<UiStrings> rows)
        {
            foreach (var row in rows)
                _texts[row.Id] = row.Text;
        }

        public bool Has(string id) => _texts.ContainsKey(id);

        public string Get(string id, params TextArg[] args) =>
            _texts.TryGetValue(id, out var text) ? TextTemplate.Fill(text, args) : id;

        /// <summary>Display name of a class id (<c>class.&lt;id&gt;</c>), or the id when there is no row.</summary>
        public string ClassName(string classId) =>
            _texts.TryGetValue(UiTextIds.ClassPrefix + classId, out var text) ? text : classId;
    }

    /// <summary>
    /// Reason code to text and icon from <c>ReasonCodeStrings</c> (spec 9.1, 9.7). A code without a row shows the
    /// <c>UnknownReason</c> row with the code in <c>{code}</c>; without that row too, the code itself.
    /// </summary>
    public sealed class ReasonText
    {
        private readonly Dictionary<string, ReasonCodeStrings> _rows = new Dictionary<string, ReasonCodeStrings>(StringComparer.Ordinal);

        public ReasonText(IEnumerable<ReasonCodeStrings> rows)
        {
            foreach (var row in rows)
                _rows[row.Code] = row;
        }

        public bool Has(string code) => _rows.ContainsKey(code);

        public string Text(string code, params TextArg[] args)
        {
            if (_rows.TryGetValue(code, out var row))
                return TextTemplate.Fill(row.Text, args);
            if (_rows.TryGetValue(ReasonCodeStrings.UnknownCode, out var unknown))
                return TextTemplate.Fill(unknown.Text, new TextArg("code", code));
            return code;
        }

        /// <summary>Icon resource path, or empty.</summary>
        public string Icon(string code) =>
            _rows.TryGetValue(code, out var row) ? row.Icon
            : _rows.TryGetValue(ReasonCodeStrings.UnknownCode, out var unknown) ? unknown.Icon : "";
    }
}
