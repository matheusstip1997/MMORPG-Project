using System;

namespace Shared.Data
{
    /// <summary>Marks a row type as a config table stored in <c>data/tables/&lt;Name&gt;.json</c> (spec 1.8, 8.5).</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class TableAttribute : Attribute
    {
        public TableAttribute(string name, int version)
        {
            Name = name;
            Version = version;
        }

        public string Name { get; }

        /// <summary>Expected <c>version</c> in the file. Bump it when the row shape changes.</summary>
        public int Version { get; }
    }

    /// <summary>The table holds exactly one row (settings tables such as <c>StartingValuesM1</c>).</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class SingletonAttribute : Attribute
    {
    }

    /// <summary>Unique key of a row; other tables point to it with <see cref="RefAttribute"/>.</summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public sealed class TableIdAttribute : Attribute
    {
    }

    /// <summary>Inclusive bounds for an integer field.</summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public sealed class RangeAttribute : Attribute
    {
        public RangeAttribute(long min, long max)
        {
            Min = min;
            Max = max;
        }

        public long Min { get; }

        public long Max { get; }
    }

    /// <summary>The field holds the <see cref="TableIdAttribute"/> value of a row in another table.</summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public sealed class RefAttribute : Attribute
    {
        public RefAttribute(Type target)
        {
            Target = target;
        }

        public Type Target { get; }

        /// <summary>An empty string or 0 means "no reference".</summary>
        public bool Optional { get; set; }
    }

    /// <summary>
    /// The field may be left out of a row; the row then keeps the property's initial value. Only for fields added to a
    /// table that other branches also add rows to, so their rows stay valid (for example <c>ActionEffectDef.element</c>,
    /// default <c>none</c>). Both readers honour it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public sealed class OptionalAttribute : Attribute
    {
    }

    /// <summary>Cross-field rules that attributes cannot express.</summary>
    public interface IValidatableRow
    {
        /// <summary>Adds one message per broken rule.</summary>
        void Validate(RowErrors errors);
    }

    /// <summary>
    /// Rules that need other rows or other tables (for example "the dodge action lasts <c>DodgeDurationMs</c>").
    /// <see cref="TableValidator"/> calls it with every loaded table, after the field checks. A table that is not loaded
    /// is already reported (<c>MissingTable</c>), so a rule skips quietly when a table it reads is absent.
    /// </summary>
    public interface ISetValidatableRow
    {
        /// <summary>Adds one message per broken rule.</summary>
        void Validate(ConfigSet set, RowErrors errors);
    }
}
