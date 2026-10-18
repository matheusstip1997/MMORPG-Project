using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Shared.Data
{
    /// <summary>
    /// Checks loaded tables against their row types: versions, singleton row counts, unique IDs,
    /// integer ranges, references between tables (list fields item by item), <see cref="IValidatableRow"/> rules, and
    /// <see cref="ISetValidatableRow"/> cross-table rules.
    /// Schema checks on the raw files (missing or unknown fields) are done by the loader.
    /// </summary>
    public static class TableValidator
    {
        public static List<ValidationError> Validate(ConfigSet set)
        {
            var errors = new List<ValidationError>();
            foreach (var table in set.Tables)
                ValidateTable(set, table, errors);
            return errors;
        }

        private static void ValidateTable(ConfigSet set, ConfigTable table, List<ValidationError> errors)
        {
            var info = TableInfo.For(table.RowType);

            if (table.Version != info.Version)
                errors.Add(new ValidationError(ValidationCodes.VersionMismatch, info.Name, null, null,
                    $"file version {table.Version}, code expects {info.Version}"));

            if (info.IsSingleton && table.Rows.Count != 1)
                errors.Add(new ValidationError(ValidationCodes.SingletonCount, info.Name, null, null,
                    $"singleton table has {table.Rows.Count} rows, expected 1"));

            var seenIds = new Dictionary<string, int>();
            for (var row = 0; row < table.Rows.Count; row++)
            {
                var item = table.Rows[row];

                if (info.IdField != null)
                {
                    var id = Key(info.IdField.GetValue(item));
                    if (seenIds.TryGetValue(id, out var firstRow))
                        errors.Add(new ValidationError(ValidationCodes.DuplicateId, info.Name, row, info.IdField.Name,
                            $"id '{id}' already used by row {firstRow}"));
                    else
                        seenIds[id] = row;
                }

                foreach (var field in info.Fields)
                {
                    CheckRange(info, row, item, field, errors);
                    CheckRef(set, info, row, item, field, errors);
                }

                if (item is IValidatableRow validatable)
                {
                    var rowErrors = new RowErrors();
                    validatable.Validate(rowErrors);
                    foreach (var (field, message) in rowErrors.Items)
                        errors.Add(new ValidationError(ValidationCodes.RuleBroken, info.Name, row, field, message));
                }

                if (item is ISetValidatableRow setValidatable)
                {
                    var rowErrors = new RowErrors();
                    setValidatable.Validate(set, rowErrors);
                    foreach (var (field, message) in rowErrors.Items)
                        errors.Add(new ValidationError(ValidationCodes.RuleBroken, info.Name, row, field, message));
                }
            }
        }

        private static void CheckRange(TableInfo info, int row, object item, PropertyInfo field, List<ValidationError> errors)
        {
            var range = field.GetCustomAttribute<RangeAttribute>();
            if (range == null)
                return;

            var value = field.GetValue(item);
            if (!(value is int || value is long))
            {
                errors.Add(new ValidationError(ValidationCodes.SchemaError, info.Name, row, field.Name,
                    $"[Range] is only supported on int and long fields, not {field.PropertyType.Name}"));
                return;
            }

            var number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (number < range.Min || number > range.Max)
                errors.Add(new ValidationError(ValidationCodes.OutOfRange, info.Name, row, field.Name,
                    $"{number} is outside [{range.Min}, {range.Max}]"));
        }

        private static void CheckRef(ConfigSet set, TableInfo info, int row, object item, PropertyInfo field, List<ValidationError> errors)
        {
            var reference = field.GetCustomAttribute<RefAttribute>();
            if (reference == null)
                return;

            var value = field.GetValue(item);
            if (value is Array list)
            {
                // A list of references (for example ActionDef.CancelableBy) is checked item by item.
                foreach (var listItem in list)
                    CheckRefKey(set, info, row, field, reference, Key(listItem), errors);
                return;
            }
            CheckRefKey(set, info, row, field, reference, Key(value), errors);
        }

        private static void CheckRefKey(ConfigSet set, TableInfo info, int row, PropertyInfo field, RefAttribute reference,
            string key, List<ValidationError> errors)
        {
            if (reference.Optional && (key.Length == 0 || key == "0"))
                return;

            var target = TableInfo.For(reference.Target);
            if (target.IdField == null)
            {
                errors.Add(new ValidationError(ValidationCodes.SchemaError, info.Name, row, field.Name,
                    $"[Ref] target {target.Name} has no [TableId] field"));
                return;
            }

            if (!set.TryGet(reference.Target, out var targetTable))
            {
                errors.Add(new ValidationError(ValidationCodes.BrokenRef, info.Name, row, field.Name,
                    $"points to table {target.Name}, which is not loaded"));
                return;
            }

            foreach (var targetRow in targetTable.Rows)
            {
                if (Key(target.IdField.GetValue(targetRow)) == key)
                    return;
            }

            errors.Add(new ValidationError(ValidationCodes.BrokenRef, info.Name, row, field.Name,
                $"'{key}' does not exist in {target.Name}"));
        }

        private static string Key(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
