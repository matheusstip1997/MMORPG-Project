using System.Text;

namespace AdminPanel.Logs;

/// <summary>The end of one server log file.</summary>
public sealed record LogTailResult(IReadOnlyList<string> Files, string? File, IReadOnlyList<string> Lines);

/// <summary>
/// Reads the end of the server log files in one folder (spec 7.4 "view logs"). In docker compose the GameServer and the
/// Auth API write their output to <c>&lt;service&gt;.log</c> there (docker/dev-entrypoint.sh). The client names a file only by
/// one of the names this class lists, never by a path, and at most <c>maxBytes</c> are read from the file's end.
/// </summary>
public static class LogTail
{
    public const string Pattern = "*.log";

    /// <summary>The <c>*.log</c> files in <paramref name="directory"/>, newest first; empty when the folder does not exist.</summary>
    public static IReadOnlyList<string> ListFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return [];
        return new DirectoryInfo(directory).GetFiles(Pattern)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => f.Name)
            .ToList();
    }

    /// <summary>
    /// The last <paramref name="maxLines"/> lines of <paramref name="file"/> (default the newest file) that contain
    /// <paramref name="contains"/> (ignoring case; all lines when empty), reading at most <paramref name="maxBytes"/>.
    /// A name that is not one of the listed files reads nothing.
    /// </summary>
    public static LogTailResult Read(string directory, string? file, string? contains, int maxLines, int maxBytes)
    {
        var files = ListFiles(directory);
        var chosen = string.IsNullOrEmpty(file) ? files.FirstOrDefault() : files.FirstOrDefault(f => f == file);
        if (chosen == null)
            return new LogTailResult(files, null, []);

        var lines = ReadTailLines(Path.Combine(directory, chosen), maxBytes);
        var matching = string.IsNullOrEmpty(contains)
            ? lines
            : lines.Where(l => l.Contains(contains, StringComparison.OrdinalIgnoreCase)).ToList();
        return new LogTailResult(files, chosen, matching.Skip(Math.Max(0, matching.Count - maxLines)).ToList());
    }

    /// <summary>The complete lines in the last <paramref name="maxBytes"/> of a file the server may still be writing.</summary>
    internal static List<string> ReadTailLines(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - start];
        stream.ReadExactly(buffer);

        var text = Encoding.UTF8.GetString(buffer);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (start > 0 && lines.Count > 0)
            lines.RemoveAt(0); // cut in the middle of a line
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1); // the text ended with a newline
        return lines;
    }
}
