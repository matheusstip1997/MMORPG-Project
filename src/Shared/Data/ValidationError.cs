using System.Collections.Generic;

namespace Shared.Data
{
    /// <summary>One problem found while loading or validating config tables.</summary>
    public sealed class ValidationError
    {
        public ValidationError(string code, string table, int? row, string? field, string message)
        {
            Code = code;
            Table = table;
            Row = row;
            Field = field;
            Message = message;
        }

        /// <summary>Stable kind of problem, for example <c>OutOfRange</c> (see <see cref="ValidationCodes"/>).</summary>
        public string Code { get; }

        public string Table { get; }

        /// <summary>Zero-based row index, or null for table-level problems.</summary>
        public int? Row { get; }

        public string? Field { get; }

        public string Message { get; }

        public override string ToString()
        {
            var where = Table;
            if (Row.HasValue) where += $" row {Row.Value}";
            if (Field != null) where += $" field {Field}";
            return $"{Code}: {where}: {Message}";
        }
    }

    public static class ValidationCodes
    {
        public const string MissingTable = "MissingTable";
        public const string UnknownTable = "UnknownTable";
        public const string InvalidJson = "InvalidJson";
        public const string BadEnvelope = "BadEnvelope";
        public const string VersionMismatch = "VersionMismatch";
        public const string MissingField = "MissingField";
        public const string UnknownField = "UnknownField";
        public const string NullValue = "NullValue";
        public const string WrongType = "WrongType";
        public const string SingletonCount = "SingletonCount";
        public const string DuplicateId = "DuplicateId";
        public const string OutOfRange = "OutOfRange";
        public const string BrokenRef = "BrokenRef";
        public const string RuleBroken = "RuleBroken";
        public const string SchemaError = "SchemaError";
    }

    /// <summary>Collects cross-field rule messages for one row (<see cref="IValidatableRow"/>).</summary>
    public sealed class RowErrors
    {
        private readonly List<(string? Field, string Message)> _items = new List<(string? Field, string Message)>();

        public IReadOnlyList<(string? Field, string Message)> Items => _items;

        public void Add(string? field, string message) => _items.Add((field, message));
    }
}
