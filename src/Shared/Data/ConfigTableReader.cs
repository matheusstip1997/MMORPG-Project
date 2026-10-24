using System;
using System.Collections.Generic;
using System.Reflection;

namespace Shared.Data
{
    /// <summary>
    /// Reads config table files into their row types without a JSON package, so the Unity client loads the same
    /// <c>data/tables</c> files as the server (spec 1.8: "the client loads the same tables for prediction").
    /// Same format and checks as the server's <c>ConfigLoader</c>: envelope <c>{ table, version, rows }</c>, camelCase
    /// field names, exactly the fields of the row type, no nulls; int, long, bool, string and enum fields, enums as
    /// camelCase names. A test in GameServer.Tests checks that both readers give the same rows for every committed table.
    /// Load time only; it allocates.
    /// </summary>
    public static class ConfigTableReader
    {
        /// <summary>
        /// Reads every table in <paramref name="rowTypes"/> and validates the set (<see cref="TableValidator"/>).
        /// <paramref name="readTableJson"/> returns the file text for a table name, or null when the file is missing.
        /// </summary>
        public static ConfigSet LoadAndValidate(IReadOnlyList<Type> rowTypes, Func<string, string?> readTableJson,
            List<ValidationError> errors)
        {
            var set = new ConfigSet();
            foreach (var rowType in rowTypes)
            {
                var info = TableInfo.For(rowType);
                var json = readTableJson(info.Name);
                if (json == null)
                {
                    errors.Add(new ValidationError(ValidationCodes.MissingTable, info.Name, null, null, "file not found"));
                    continue;
                }
                var table = ReadTable(json, rowType, errors);
                if (table != null)
                    set.Add(table);
            }
            errors.AddRange(TableValidator.Validate(set));
            return set;
        }

        /// <summary>Reads one table file. Returns null and adds errors when the file or any row is unusable.</summary>
        public static ConfigTable? ReadTable(string json, Type rowType, List<ValidationError> errors)
        {
            var info = TableInfo.For(rowType);
            if (!ConfigJson.TryParse(json, out var root, out var parseError))
            {
                errors.Add(new ValidationError(ValidationCodes.InvalidJson, info.Name, null, null, parseError));
                return null;
            }
            if (!TryReadEnvelope(root, info, errors, out int version, out var rowsValue))
                return null;

            var fields = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
            foreach (var field in info.Fields)
                fields[CamelCase(field.Name)] = field;

            var rows = new List<object>();
            for (int index = 0; index < rowsValue.Items.Count; index++)
            {
                var row = ReadRow(rowsValue.Items[index], index, info, fields, errors);
                if (row != null)
                    rows.Add(row);
            }
            return new ConfigTable(rowType, version, rows);
        }

        /// <summary>The camelCase rule of System.Text.Json's <c>JsonNamingPolicy.CamelCase</c>, for field and enum names.</summary>
        public static string CamelCase(string name)
        {
            if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0]))
                return name;
            char[] chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (i == 1 && !char.IsUpper(chars[i]))
                    break;
                bool hasNext = i + 1 < chars.Length;
                if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
                {
                    if (chars[i + 1] == ' ')
                        chars[i] = char.ToLowerInvariant(chars[i]);
                    break;
                }
                chars[i] = char.ToLowerInvariant(chars[i]);
            }
            return new string(chars);
        }

        private static bool TryReadEnvelope(ConfigJsonValue root, TableInfo info, List<ValidationError> errors,
            out int version, out ConfigJsonValue rows)
        {
            version = 0;
            rows = null!;
            if (root.Kind != ConfigJsonKind.Object)
            {
                errors.Add(new ValidationError(ValidationCodes.BadEnvelope, info.Name, null, null, "root must be an object"));
                return false;
            }

            foreach (var member in root.Members)
            {
                if (member.Key != "table" && member.Key != "version" && member.Key != "rows")
                    errors.Add(new ValidationError(ValidationCodes.BadEnvelope, info.Name, null, member.Key,
                        "unknown root field (allowed: table, version, rows)"));
            }

            bool ok = true;
            if (!root.TryGetMember("table", out var table) || table.Kind != ConfigJsonKind.String || table.Text != info.Name)
            {
                errors.Add(new ValidationError(ValidationCodes.BadEnvelope, info.Name, null, "table", $"must be \"{info.Name}\""));
                ok = false;
            }

            if (!root.TryGetMember("version", out var versionValue) || !versionValue.TryGetInt64(out long longVersion)
                || longVersion < int.MinValue || longVersion > int.MaxValue)
            {
                errors.Add(new ValidationError(ValidationCodes.BadEnvelope, info.Name, null, "version", "must be an integer"));
                ok = false;
            }
            else
            {
                version = (int)longVersion;
            }

            if (!root.TryGetMember("rows", out rows) || rows.Kind != ConfigJsonKind.Array)
            {
                errors.Add(new ValidationError(ValidationCodes.BadEnvelope, info.Name, null, "rows", "must be an array"));
                ok = false;
            }
            return ok;
        }

        private static object? ReadRow(ConfigJsonValue element, int index, TableInfo info,
            Dictionary<string, PropertyInfo> fields, List<ValidationError> errors)
        {
            if (element.Kind != ConfigJsonKind.Object)
            {
                errors.Add(new ValidationError(ValidationCodes.WrongType, info.Name, index, null, "row must be an object"));
                return null;
            }

            int errorCount = errors.Count;
            foreach (var member in element.Members)
            {
                if (!fields.ContainsKey(member.Key))
                    errors.Add(new ValidationError(ValidationCodes.UnknownField, info.Name, index, member.Key, "not a field of this table"));
                else if (member.Value.Kind == ConfigJsonKind.Null)
                    errors.Add(new ValidationError(ValidationCodes.NullValue, info.Name, index, member.Key, "null is not allowed"));
            }
            foreach (var name in fields.Keys)
            {
                if (!element.TryGetMember(name, out _) && !fields[name].IsDefined(typeof(OptionalAttribute), false))
                    errors.Add(new ValidationError(ValidationCodes.MissingField, info.Name, index, name, "required field is missing"));
            }
            if (errors.Count > errorCount)
                return null;

            object row;
            try
            {
                row = Activator.CreateInstance(info.RowType)
                      ?? throw new InvalidOperationException($"{info.RowType.Name} could not be created");
            }
            catch (Exception e) when (e is MissingMethodException || e is MemberAccessException || e is InvalidOperationException)
            {
                errors.Add(new ValidationError(ValidationCodes.SchemaError, info.Name, index, null,
                    $"row type needs a public parameterless constructor: {e.Message}"));
                return null;
            }

            foreach (var member in element.Members)
            {
                var field = fields[member.Key];
                if (!TryConvert(member.Value, field.PropertyType, out var value, out var problem))
                {
                    errors.Add(new ValidationError(problem == null ? ValidationCodes.WrongType : ValidationCodes.SchemaError,
                        info.Name, index, member.Key, problem ?? $"expected {Describe(field.PropertyType)}"));
                    continue;
                }
                field.SetValue(row, value);
            }
            return errors.Count > errorCount ? null : row;
        }

        /// <param name="schemaProblem">Set when the field type itself is not supported (a code problem, not a data one).</param>
        private static bool TryConvert(ConfigJsonValue json, Type type, out object? value, out string? schemaProblem)
        {
            value = null;
            schemaProblem = null;
            if (type == typeof(int))
            {
                if (!json.TryGetInt64(out long number) || number < int.MinValue || number > int.MaxValue)
                    return false;
                value = (int)number;
                return true;
            }
            if (type == typeof(long))
            {
                if (!json.TryGetInt64(out long number))
                    return false;
                value = number;
                return true;
            }
            if (type == typeof(bool))
            {
                if (json.Kind != ConfigJsonKind.True && json.Kind != ConfigJsonKind.False)
                    return false;
                value = json.Kind == ConfigJsonKind.True;
                return true;
            }
            if (type == typeof(string))
            {
                if (json.Kind != ConfigJsonKind.String)
                    return false;
                value = json.Text;
                return true;
            }
            if (type.IsEnum)
            {
                if (json.Kind != ConfigJsonKind.String)
                    return false;
                foreach (var name in Enum.GetNames(type))
                {
                    if (CamelCase(name) == json.Text)
                    {
                        value = Enum.Parse(type, name);
                        return true;
                    }
                }
                return false;
            }
            if (type.IsArray && type.GetArrayRank() == 1)
            {
                // Lists of any supported type, for example the string lists of CharacterCreationConfig. No null items.
                if (json.Kind != ConfigJsonKind.Array)
                    return false;
                var itemType = type.GetElementType()!;
                var array = Array.CreateInstance(itemType, json.Items.Count);
                for (int i = 0; i < json.Items.Count; i++)
                {
                    if (!TryConvert(json.Items[i], itemType, out var item, out schemaProblem) || item == null)
                        return false;
                    array.SetValue(item, i);
                }
                value = array;
                return true;
            }
            schemaProblem = $"field type {type.Name} is not supported by config tables";
            return false;
        }

        private static string Describe(Type type)
        {
            if (type == typeof(int)) return "an integer (32-bit)";
            if (type == typeof(long)) return "an integer (64-bit)";
            if (type == typeof(bool)) return "true or false";
            if (type == typeof(string)) return "a string";
            if (type.IsEnum)
            {
                var names = Enum.GetNames(type);
                for (int i = 0; i < names.Length; i++)
                    names[i] = CamelCase(names[i]);
                return $"one of: {string.Join(", ", names)}";
            }
            if (type.IsArray && type.GetArrayRank() == 1)
                return $"a list of {Describe(type.GetElementType()!)}";
            return type.Name;
        }
    }
}
