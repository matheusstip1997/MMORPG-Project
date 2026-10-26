using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.Data
{
    /// <summary>
    /// A 64-bit FNV-1a hash of table files as read from disk (dev hot reload, spec 11.7): the server writes the hash of
    /// the Hot tables it swapped in its <c>ConfigReload</c> telemetry record, and the dev client applies a reload only when
    /// the files it reads hash the same, so client prediction runs exactly the tables the server runs. Not a security hash.
    /// </summary>
    public static class TableHash
    {
        private const ulong OffsetBasis = 14_695_981_039_346_656_037UL;
        private const ulong Prime = 1_099_511_628_211UL;

        /// <summary>
        /// Hashes <c>name, 0, bytes, 0</c> for each table in <paramref name="rowTypes"/> order. <paramref name="readTableBytes"/>
        /// returns a table file's bytes by table name, or null when it is missing (hashed as no bytes).
        /// </summary>
        public static ulong Compute(IReadOnlyList<Type> rowTypes, Func<string, byte[]?> readTableBytes)
        {
            ulong hash = OffsetBasis;
            foreach (var rowType in rowTypes)
            {
                string name = TableInfo.For(rowType).Name;
                hash = Add(hash, Encoding.UTF8.GetBytes(name));
                hash = Add(hash, 0);
                var bytes = readTableBytes(name);
                if (bytes != null)
                    hash = Add(hash, bytes);
                hash = Add(hash, 0);
            }
            return hash;
        }

        private static ulong Add(ulong hash, byte[] bytes)
        {
            foreach (byte b in bytes)
                hash = Add(hash, b);
            return hash;
        }

        private static ulong Add(ulong hash, byte b) => unchecked((hash ^ b) * Prime);
    }
}
