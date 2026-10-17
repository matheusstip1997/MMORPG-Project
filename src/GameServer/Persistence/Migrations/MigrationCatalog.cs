using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GameServer.Persistence.Migrations;

/// <summary>Which database a migration set belongs to (R-2.1-03, R-7.1-03).</summary>
public enum DatabaseKind
{
    Global,
    Realm,
}

/// <summary>One versioned SQL script. The checksum is over the text with line endings normalized to LF.</summary>
public sealed record Migration(int Version, string Name, string Sql, string Checksum)
{
    public static Migration Create(int version, string name, string sql)
    {
        var normalized = sql.TrimStart('﻿').Replace("\r\n", "\n");
        var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return new Migration(version, name, normalized, checksum);
    }
}

/// <summary>
/// The migrations embedded in this assembly (R-7.1-04). Files live in <c>Persistence/Migrations/{Global,Realm}</c>
/// and use Flyway naming, <c>V0001__snake_case_name.sql</c>, so a move to Flyway stays possible.
/// </summary>
public static partial class MigrationCatalog
{
    [GeneratedRegex(@"^V(?<version>\d{4})__(?<name>[a-z0-9_]+)\.sql$")]
    private static partial Regex FileNamePattern();

    public static IReadOnlyList<Migration> For(DatabaseKind kind)
    {
        var prefix = $"Migrations.{kind}.";
        var assembly = typeof(MigrationCatalog).Assembly;
        var migrations = new List<Migration>();

        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var fileName = resource[prefix.Length..];
            var match = FileNamePattern().Match(fileName);
            if (!match.Success)
                throw new MigrationException($"migration file name must look like V0001__name.sql: {fileName}");

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            migrations.Add(Migration.Create(int.Parse(match.Groups["version"].Value), match.Groups["name"].Value, reader.ReadToEnd()));
        }

        return Validate(migrations);
    }

    /// <summary>Sorts by version and refuses duplicates or versions below 1.</summary>
    public static IReadOnlyList<Migration> Validate(IEnumerable<Migration> migrations)
    {
        var sorted = migrations.OrderBy(m => m.Version).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (sorted[i].Version < 1)
                throw new MigrationException($"migration versions start at 1: V{sorted[i].Version}");
            if (i > 0 && sorted[i].Version == sorted[i - 1].Version)
                throw new MigrationException($"duplicate migration version V{sorted[i].Version}");
        }

        return sorted;
    }
}

public sealed class MigrationException(string message) : Exception(message);
