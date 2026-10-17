#nullable enable
using System.Globalization;
using System.Text;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// A tiny JSON writer with a fixed layout: keys in the order written, two-space indent, LF line endings, invariant
    /// integers, UTF-8 without BOM. The same calls always produce the same bytes (spec 11.6 deterministic output).
    /// </summary>
    public sealed class DeterministicJson
    {
        private readonly StringBuilder _text = new StringBuilder();
        private int _depth;
        private bool _first = true;

        public DeterministicJson BeginObject(string? key = null) => Open(key, '{');
        public DeterministicJson EndObject() => Close('}');
        public DeterministicJson BeginArray(string? key = null) => Open(key, '[');
        public DeterministicJson EndArray() => Close(']');

        public DeterministicJson Field(string key, string value)
        {
            Prefix(key);
            WriteString(value);
            return this;
        }

        public DeterministicJson Field(string key, long value)
        {
            Prefix(key);
            _text.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public DeterministicJson Field(string key, bool value)
        {
            Prefix(key);
            _text.Append(value ? "true" : "false");
            return this;
        }

        public byte[] ToBytes() => new UTF8Encoding(false).GetBytes(ToString());

        public override string ToString() => _text.ToString() + "\n";

        private DeterministicJson Open(string? key, char bracket)
        {
            Prefix(key);
            _text.Append(bracket);
            _depth++;
            _first = true;
            return this;
        }

        private DeterministicJson Close(char bracket)
        {
            _depth--;
            if (!_first)
                NewLine();
            _text.Append(bracket);
            _first = false;
            return this;
        }

        private void Prefix(string? key)
        {
            if (_depth > 0)
            {
                if (!_first)
                    _text.Append(',');
                NewLine();
            }
            _first = false;
            if (key != null)
            {
                WriteString(key);
                _text.Append(": ");
            }
        }

        private void NewLine()
        {
            _text.Append('\n');
            _text.Append(' ', _depth * 2);
        }

        private void WriteString(string value)
        {
            _text.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': _text.Append("\\\""); break;
                    case '\\': _text.Append("\\\\"); break;
                    case '\n': _text.Append("\\n"); break;
                    case '\r': _text.Append("\\r"); break;
                    case '\t': _text.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            _text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            _text.Append(c);
                        break;
                }
            }
            _text.Append('"');
        }
    }
}
