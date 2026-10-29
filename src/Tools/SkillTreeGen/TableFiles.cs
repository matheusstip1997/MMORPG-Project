using System.Reflection;
using System.Text;
using Shared.Data;

namespace SkillTreeGen;

/// <summary>The table files of one data directory: read as text, parsed rows, appended rows and whole writes.</summary>
public sealed class TableFiles
{
    private readonly string _directory;

    public TableFiles(string directory)
    {
        _directory = directory;
    }

    public string PathOf(string table) => Path.Combine(_directory, table + ".json");

    public string? ReadText(string table) => File.Exists(PathOf(table)) ? File.ReadAllText(PathOf(table)) : null;

    public void WriteText(string table, string text) => File.WriteAllText(PathOf(table), text);

    /// <summary>The rows of a table, or an empty list when the file is missing or unreadable (the problem is added).</summary>
    public List<T> Rows<T>(List<string> problems) where T : class
    {
        var info = TableInfo.For(typeof(T));
        var text = ReadText(info.Name);
        if (text == null)
        {
            problems.Add($"{info.Name}.json not found");
            return new List<T>();
        }
        var errors = new List<ValidationError>();
        var table = ConfigTableReader.ReadTable(text, typeof(T), errors);
        foreach (var error in errors)
            problems.Add(error.ToString());
        return table == null ? new List<T>() : table.Rows.Cast<T>().ToList();
    }
}

/// <summary>Writes table rows as one-line JSON objects in the table files' own style (camelCase fields and enums).</summary>
public static class RowWriter
{
    public static string Write(object row)
    {
        var info = TableInfo.For(row.GetType());
        var builder = new StringBuilder("{ ");
        bool first = true;
        foreach (var field in info.Fields)
        {
            if (!first)
                builder.Append(", ");
            first = false;
            builder.Append('"').Append(ConfigTableReader.CamelCase(field.Name)).Append("\": ");
            AppendValue(builder, field.GetValue(row));
        }
        return builder.Append(" }").ToString();
    }

    private static void AppendValue(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case string text:
                AppendString(builder, text);
                break;
            case bool flag:
                builder.Append(flag ? "true" : "false");
                break;
            case Enum member:
                AppendString(builder, ConfigTableReader.CamelCase(member.ToString()));
                break;
            case int or long or short or byte:
                builder.Append(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case Array items:
                builder.Append('[');
                for (int i = 0; i < items.Length; i++)
                {
                    if (i > 0)
                        builder.Append(", ");
                    AppendValue(builder, items.GetValue(i));
                }
                builder.Append(']');
                break;
            default:
                throw new InvalidOperationException($"RowWriter cannot write a {value.GetType().Name}");
        }
    }

    private static void AppendString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                default:
                    if (c < 0x20)
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }

    /// <summary>A whole generated table file: header comments, one row per line.</summary>
    public static string Table(string name, int version, IEnumerable<string> header, IEnumerable<object> rows)
    {
        var builder = new StringBuilder();
        builder.Append("{\n  \"table\": \"").Append(name).Append("\",\n  \"version\": ").Append(version).Append(",\n  \"rows\": [\n");
        foreach (var line in header)
            builder.Append("    // ").Append(line).Append('\n');
        var written = rows.Select(Write).ToList();
        for (int i = 0; i < written.Count; i++)
            builder.Append("    ").Append(written[i]).Append(i + 1 < written.Count ? ",\n" : "\n");
        return builder.Append("  ]\n}\n").ToString();
    }
}

/// <summary>Appends rows at the end of a table file's <c>rows</c> array without touching what is there (JSON with comments).</summary>
public static class JsoncAppender
{
    public static string Append(string text, string comment, IReadOnlyList<string> rows)
    {
        if (rows.Count == 0)
            return text;
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        int close = LastArrayClose(text);
        int lastRow = LastRowEnd(text, close);
        var block = new StringBuilder();
        if (lastRow >= 0)
            block.Append(',').Append(newline);
        block.Append(newline).Append("    // ").Append(comment);
        for (int i = 0; i < rows.Count; i++)
            block.Append(newline).Append("    ").Append(rows[i]).Append(i + 1 < rows.Count ? "," : string.Empty);
        // The rows go right after the last row (or the opening bracket); what followed it (the newline and "]") stays.
        int insertAt = lastRow >= 0 ? lastRow + 1 : text.LastIndexOf('[', close) + 1;
        return text.Substring(0, insertAt) + block + text.Substring(insertAt);
    }

    /// <summary>The closing bracket of the rows array: the last ']' of the file.</summary>
    private static int LastArrayClose(string text)
    {
        int close = text.LastIndexOf(']');
        if (close < 0)
            throw new InvalidOperationException("no rows array");
        return close;
    }

    /// <summary>The last '}' before <paramref name="close"/> outside a // comment line, or -1 for an empty array.</summary>
    private static int LastRowEnd(string text, int close)
    {
        int open = text.LastIndexOf('[', close);
        for (int i = close - 1; i > open; i--)
        {
            if (text[i] != '}')
                continue;
            int lineStart = text.LastIndexOf('\n', i) + 1;
            int comment = text.IndexOf("//", lineStart, StringComparison.Ordinal);
            if (comment >= 0 && comment < i)
                continue;
            return i;
        }
        return -1;
    }
}
