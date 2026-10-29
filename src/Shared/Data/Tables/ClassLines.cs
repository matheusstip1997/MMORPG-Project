using System;
using System.Collections.Generic;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Class lines over the <see cref="ClassDef"/> rows (R-3.5-01, R-3.5-02; WP-M2-5): the chain from a root class through
    /// its <see cref="ClassDef.Parent"/> links. For validators and tools; the runtime keeps its own copy (<c>SkillCatalog</c>).
    /// </summary>
    public static class ClassLines
    {
        /// <summary>The line of a class, root first; empty when the class is unknown or its chain loops.</summary>
        public static List<ClassDef> LineOf(ConfigSet set, string classId)
        {
            var line = new List<ClassDef>();
            var current = ClassDef.Find(set, classId);
            while (current != null)
            {
                if (line.Count > ClassDef.MaxTier)
                    return new List<ClassDef>();
                line.Insert(0, current);
                current = current.Parent.Length != 0 ? ClassDef.Find(set, current.Parent) : null;
            }
            return line;
        }

        /// <summary>True when <paramref name="ancestorId"/> is on the line of <paramref name="classId"/> before it.</summary>
        public static bool IsStrictAncestor(ConfigSet set, string ancestorId, string classId)
        {
            var line = LineOf(set, classId);
            for (int i = 0; i < line.Count - 1; i++)
            {
                if (string.Equals(line[i].Id, ancestorId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>The <see cref="ActionBindings"/> row a class uses: its own, else the nearest earlier class's on its line.</summary>
        public static ActionBindings? BindingsFor(ConfigSet set, string classId)
        {
            var line = LineOf(set, classId);
            for (int i = line.Count - 1; i >= 0; i--)
            {
                var row = NetIds.Find<ActionBindings>(set, line[i].Id, b => b.ClassId);
                if (row != null)
                    return row;
            }
            return null;
        }
    }
}
