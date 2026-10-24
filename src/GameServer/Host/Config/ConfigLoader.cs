using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shared.Data;

namespace GameServer.Host.Config;

public sealed record ConfigLoadResult(ConfigSet Set, IReadOnlyList<ValidationError> Errors)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Loads config tables from <c>data/tables/&lt;Table&gt;.json</c> (spec 1.8). File format:
/// <c>{ "table": "Name", "version": 1, "rows": [ { ... } ] }</c>, camelCase field names.
/// Every row must have exactly the fields of its row type, with no nulls.
/// </summary>
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        // Must match DocumentOptions: a row element's raw text still contains its comments.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Enum fields are camelCase strings ("armedOnly"); numbers are refused so a file cannot depend on enum order.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly string[] EnvelopeFields = ["table", "version", "rows"];

    /// <summary>Loads the tables, then runs <see cref="TableValidator"/> on what loaded.</summary>
    public static ConfigLoadResult LoadAndValidate(string tablesDirectory, IReadOnlyList<Type> rowTypes)
    {
        var loaded = Load(tablesDirectory, rowTypes);
        var errors = loaded.Errors.Concat(TableValidator.Validate(loaded.Set)).ToList();
        return new ConfigLoadResult(loaded.Set, errors);
    }

    public static ConfigLoadResult Load(string tablesDirectory, IReadOnlyList<Type> rowTypes)
    {
        var set = new ConfigSet();
        var errors = new List<ValidationError>();
        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rowType in rowTypes)
        {
            var info = TableInfo.For(rowType);
            var path = Path.Combine(tablesDirectory, info.Name + ".json");
            expectedFiles.Add(Path.GetFileName(path));

            if (!File.Exists(path))
            {
                errors.Add(new ValidationError(ValidationCodes.MissingTable, info.Name, null, null, $"file not found: {path}"));
                continue;
            }

            var table = LoadFile(path, info, errors);
            if (table != null)
                set.Add(table);
        }

        if (Directory.Exists(tablesDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(tablesDirectory, "*.json").Order(StringComparer.Ordinal))
            {
                if (!expectedFiles.Contains(Path.GetFileName(file)))
                    errors.Add(new ValidationError(ValidationCodes.UnknownTable, Path.GetFileNameWithoutExtension(file), null, null,
                        "no row type is registered for this file (add it to ConfigTables.All)"));
            }
        }

        return new ConfigLoadResult(set, errors);
    }

    private static ConfigTable? LoadFile(string path, TableInfo info, List<ValidationError> errors)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path), DocumentOptions);
        }
        catch (JsonException e)
        {
            errors.Add(new ValidationError(ValidationCodes.InvalidJson, info.Name, null, null, e.Message));
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!TryReadEnvelope(root, info, errors, out var version, out var rowsElement))
                return null;

            var fieldNames = info.Fields.ToDictionary(f => JsonNamingPolicy.CamelCase.ConvertName(f.Name), f => f);
            var rows = new List<object>();
            var index = 0;
            foreach (var rowElement in rowsElement.EnumerateArray())
            {
                var row = ReadRow(rowElement, index, info, fieldNames, errors);
                if (row != null)
                    rows.Add(row);
                index++;
            }

            return new ConfigTable(info.RowType, version, rows);
        }
    }

    private static bool TryReadEnvelope(JsonElement root, TableInfo info, List<ValidationError> errors,
        out int version, out JsonElement rows)
    {
        version = 0;
        rows = default;

        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ValidationError(ValidationCodes.BadEnvelope, info.Name, null, null, "root must be an object"));
            return false;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!EnvelopeFields.Contains(property.Name))
                errors.Add(new ValidationError(ValidationCodes.BadEnvelope, info.Name, null, property.Name,
                    "unknown root field (allowed: table, version, rows)"));
        }

        var ok = true;
        if (!root.TryGetProperty("table", out var table) || table.ValueKind != JsonValueKind.String || table.GetString() != info.Name)
        {
            errors.Add(new ValidationError(ValidationCodes.BadEnvelope, info.Name, null, "table", $"must be \"{info.Name}\""));
            ok = false;
        }

        if (!root.TryGetProperty("version", out var versionElement) || !versionElement.TryGetInt32(out version))
        {
            errors.Add(new ValidationError(ValidationCodes.BadEnvelope, info.Name, null, "version", "must be an integer"));
            ok = false;
        }

        if (!root.TryGetProperty("rows", out rows) || rows.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new ValidationError(ValidationCodes.BadEnvelope, info.Name, null, "rows", "must be an array"));
            ok = false;
        }

        return ok;
    }

    private static object? ReadRow(JsonElement element, int index, TableInfo info,
        Dictionary<string, PropertyInfo> fieldNames, List<ValidationError> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ValidationError(ValidationCodes.WrongType, info.Name, index, null, "row must be an object"));
            return null;
        }

        var errorCount = errors.Count;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            present.Add(property.Name);
            if (!fieldNames.ContainsKey(property.Name))
                errors.Add(new ValidationError(ValidationCodes.UnknownField, info.Name, index, property.Name, "not a field of this table"));
            else if (property.Value.ValueKind == JsonValueKind.Null)
                errors.Add(new ValidationError(ValidationCodes.NullValue, info.Name, index, property.Name, "null is not allowed"));
        }

        foreach (var name in fieldNames.Keys)
        {
            if (!present.Contains(name) && !fieldNames[name].IsDefined(typeof(OptionalAttribute), false))
                errors.Add(new ValidationError(ValidationCodes.MissingField, info.Name, index, name, "required field is missing"));
        }

        if (errors.Count > errorCount)
            return null;

        try
        {
            return element.Deserialize(info.RowType, JsonOptions);
        }
        catch (JsonException e)
        {
            errors.Add(new ValidationError(ValidationCodes.WrongType, info.Name, index, e.Path, e.Message));
            return null;
        }
    }
}
