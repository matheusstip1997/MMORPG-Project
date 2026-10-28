#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the character sheet (spec 9.1, 9.13 M2; WP-M2-11). An EditMode test checks that each has a row;
    /// the stat names (<c>stat.&lt;id&gt;</c>) are checked by <c>UiStringCoverage</c> in the DataValidator.
    /// </summary>
    public static class CharacterSheetTextIds
    {
        public const string Title = "charSheet.title";
        public const string Close = "charSheet.close";
        public const string CloseLabel = "charSheet.closeLabel";
        /// <summary>The class line: <c>{class}</c> (the <c>class.&lt;id&gt;</c> text).</summary>
        public const string Class = "charSheet.class";
        /// <summary><c>{level}</c>, <c>{rank}</c> (the <c>rank.*</c> text).</summary>
        public const string Level = "charSheet.level";
        /// <summary><c>{exp}</c> of <c>{need}</c>; <see cref="ExpMax"/> at the level cap.</summary>
        public const string Exp = "charSheet.exp";
        public const string ExpMax = "charSheet.expMax";
        public const string Points = "charSheet.points";
        /// <summary>Before the first <c>OwnStats</c> arrived.</summary>
        public const string Waiting = "charSheet.waiting";

        public static readonly string[] All = { Title, Close, CloseLabel, Class, Level, Exp, ExpMax, Points, Waiting };

        /// <summary>A section's heading: <c>charSheet.section.&lt;section&gt;</c> (camelCase).</summary>
        public static string Section(CharacterSheetSection section) => "charSheet.section." + ConfigTableReader.CamelCase(section.ToString());

        /// <summary>A value in its unit: <c>charSheet.value.&lt;unit&gt;</c> with <c>{value}</c>.</summary>
        public static string Value(StatUnit unit) => "charSheet.value." + ConfigTableReader.CamelCase(unit.ToString());
    }

    /// <summary>One stat line of the sheet: its <c>StatDef</c> id, section and unit, in sheet order.</summary>
    public readonly struct CharacterSheetLine
    {
        public CharacterSheetLine(string statId, CharacterSheetSection section, StatUnit unit)
        {
            StatId = statId;
            Section = section;
            Unit = unit;
        }

        public string StatId { get; }
        public CharacterSheetSection Section { get; }
        public StatUnit Unit { get; }
    }

    /// <summary>
    /// What the character sheet reads from the config tables (WP-M2-11): the <c>CharacterSheetStats</c> rows in section and
    /// order, and the <c>Hotkeys</c> row <c>toggleCharacter</c>.
    /// </summary>
    public sealed class CharacterSheetConfig
    {
        public CharacterSheetConfig(IReadOnlyList<CharacterSheetLine> lines, string toggleBinding)
        {
            Lines = lines;
            ToggleBinding = toggleBinding;
        }

        /// <summary>Every stat line, by section then <c>order</c> (ties in table order).</summary>
        public IReadOnlyList<CharacterSheetLine> Lines { get; }

        public string ToggleBinding { get; }

        public static CharacterSheetConfig From(ConfigSet set)
        {
            var rows = new List<(CharacterSheetStats Row, int Index)>();
            if (set.TryGet(typeof(CharacterSheetStats), out var table))
            {
                for (int i = 0; i < table.Rows.Count; i++)
                    rows.Add(((CharacterSheetStats)table.Rows[i], i));
            }
            rows.Sort((a, b) => a.Row.Section != b.Row.Section ? a.Row.Section.CompareTo(b.Row.Section)
                : a.Row.Order != b.Row.Order ? a.Row.Order.CompareTo(b.Row.Order) : a.Index.CompareTo(b.Index));
            var lines = new List<CharacterSheetLine>(rows.Count);
            foreach (var (row, _) in rows)
                lines.Add(new CharacterSheetLine(row.Id, row.Section, row.Unit));
            return new CharacterSheetConfig(lines, Hotkeys.Find(set, Hotkeys.ToggleCharacter)?.Binding ?? "");
        }
    }
}
