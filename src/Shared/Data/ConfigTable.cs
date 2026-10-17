using System;
using System.Collections.Generic;
using System.Reflection;

namespace Shared.Data
{
    /// <summary>One loaded table: its rows and the version read from the file.</summary>
    public sealed class ConfigTable
    {
        public ConfigTable(Type rowType, int version, IReadOnlyList<object> rows)
        {
            RowType = rowType;
            Version = version;
            Rows = rows;
        }

        public Type RowType { get; }

        public int Version { get; }

        public IReadOnlyList<object> Rows { get; }

        public string Name => TableInfo.For(RowType).Name;
    }

    /// <summary>All loaded tables, by row type.</summary>
    public sealed class ConfigSet
    {
        private readonly Dictionary<Type, ConfigTable> _tables = new Dictionary<Type, ConfigTable>();

        public IEnumerable<ConfigTable> Tables => _tables.Values;

        public void Add(ConfigTable table) => _tables[table.RowType] = table;

        public bool TryGet(Type rowType, out ConfigTable table) => _tables.TryGetValue(rowType, out table!);

        public T Single<T>() where T : class
        {
            if (!_tables.TryGetValue(typeof(T), out var table) || table.Rows.Count != 1)
                throw new InvalidOperationException($"Table {typeof(T).Name} is not loaded as a single row.");
            return (T)table.Rows[0];
        }
    }

    /// <summary>The attributes of a row type, read once.</summary>
    public sealed class TableInfo
    {
        private static readonly Dictionary<Type, TableInfo> Cache = new Dictionary<Type, TableInfo>();

        private TableInfo(Type rowType)
        {
            var table = rowType.GetCustomAttribute<TableAttribute>()
                ?? throw new InvalidOperationException($"{rowType.Name} has no [Table] attribute.");
            RowType = rowType;
            Name = table.Name;
            Version = table.Version;
            IsSingleton = rowType.GetCustomAttribute<SingletonAttribute>() != null;
            Fields = rowType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in Fields)
            {
                if (field.GetCustomAttribute<TableIdAttribute>() != null)
                    IdField = field;
            }
        }

        public Type RowType { get; }

        public string Name { get; }

        public int Version { get; }

        public bool IsSingleton { get; }

        public PropertyInfo? IdField { get; }

        public IReadOnlyList<PropertyInfo> Fields { get; }

        public static TableInfo For(Type rowType)
        {
            lock (Cache)
            {
                if (!Cache.TryGetValue(rowType, out var info))
                {
                    info = new TableInfo(rowType);
                    Cache[rowType] = info;
                }
                return info;
            }
        }
    }
}
