using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Shared.Data
{
    public enum ConfigJsonKind : byte
    {
        Null,
        False,
        True,
        Number,
        String,
        Array,
        Object,
    }

    /// <summary>One parsed JSON value of a config table file. Numbers keep their source text.</summary>
    public sealed class ConfigJsonValue
    {
        private static readonly List<ConfigJsonValue> NoItems = new List<ConfigJsonValue>();
        private static readonly List<KeyValuePair<string, ConfigJsonValue>> NoMembers = new List<KeyValuePair<string, ConfigJsonValue>>();

        internal ConfigJsonValue(ConfigJsonKind kind, string text = "",
            List<ConfigJsonValue>? items = null, List<KeyValuePair<string, ConfigJsonValue>>? members = null)
        {
            Kind = kind;
            Text = text;
            Items = items ?? NoItems;
            Members = members ?? NoMembers;
        }

        public ConfigJsonKind Kind { get; }

        /// <summary>The string value, or the number as written.</summary>
        public string Text { get; }

        public IReadOnlyList<ConfigJsonValue> Items { get; }

        /// <summary>Object members in file order.</summary>
        public IReadOnlyList<KeyValuePair<string, ConfigJsonValue>> Members { get; }

        public bool TryGetMember(string name, out ConfigJsonValue value)
        {
            foreach (var member in Members)
            {
                if (member.Key == name)
                {
                    value = member.Value;
                    return true;
                }
            }
            value = null!;
            return false;
        }

        /// <summary>True for an integer literal (no fraction or exponent) that fits in a long.</summary>
        public bool TryGetInt64(out long value)
        {
            value = 0;
            return Kind == ConfigJsonKind.Number
                   && Text.IndexOfAny(new[] { '.', 'e', 'E' }) < 0
                   && long.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>
    /// A small JSON parser for the config table files (spec 1.8), portable so the Unity client reads the same files
    /// as the server without a JSON package. It accepts what the server's loader accepts: standard JSON plus
    /// <c>//</c> and <c>/* */</c> comments and trailing commas. Duplicate member names are an error.
    /// Load time only; it allocates freely.
    /// </summary>
    public static class ConfigJson
    {
        public static bool TryParse(string text, out ConfigJsonValue value, out string error)
        {
            var parser = new Parser(text);
            try
            {
                value = parser.ParseDocument();
                error = string.Empty;
                return true;
            }
            catch (FormatException e)
            {
                value = null!;
                error = e.Message;
                return false;
            }
        }

        private struct Parser
        {
            private const int MaxDepth = 64;
            private readonly string _text;
            private int _position;
            private int _depth;

            public Parser(string text)
            {
                _text = text ?? throw new ArgumentNullException(nameof(text));
                _position = 0;
                _depth = 0;
            }

            public ConfigJsonValue ParseDocument()
            {
                SkipTrivia();
                var value = ParseValue();
                SkipTrivia();
                if (_position != _text.Length)
                    throw Error("unexpected text after the root value");
                return value;
            }

            private ConfigJsonValue ParseValue()
            {
                if (_position >= _text.Length)
                    throw Error("unexpected end of file");
                char c = _text[_position];
                switch (c)
                {
                    case '{':
                        return ParseObject();
                    case '[':
                        return ParseArray();
                    case '"':
                        return new ConfigJsonValue(ConfigJsonKind.String, ParseString());
                    case 't':
                        ExpectWord("true");
                        return new ConfigJsonValue(ConfigJsonKind.True);
                    case 'f':
                        ExpectWord("false");
                        return new ConfigJsonValue(ConfigJsonKind.False);
                    case 'n':
                        ExpectWord("null");
                        return new ConfigJsonValue(ConfigJsonKind.Null);
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                            return new ConfigJsonValue(ConfigJsonKind.Number, ParseNumber());
                        throw Error($"unexpected character '{c}'");
                }
            }

            private ConfigJsonValue ParseObject()
            {
                Enter();
                _position++; // {
                var members = new List<KeyValuePair<string, ConfigJsonValue>>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                SkipTrivia();
                while (Peek() != '}')
                {
                    if (Peek() != '"')
                        throw Error("expected a member name");
                    string name = ParseString();
                    if (!names.Add(name))
                        throw Error($"duplicate member \"{name}\"");
                    SkipTrivia();
                    Expect(':');
                    SkipTrivia();
                    members.Add(new KeyValuePair<string, ConfigJsonValue>(name, ParseValue()));
                    if (!SeparatorOrEnd('}'))
                        break;
                }
                Expect('}');
                _depth--;
                return new ConfigJsonValue(ConfigJsonKind.Object, members: members);
            }

            private ConfigJsonValue ParseArray()
            {
                Enter();
                _position++; // [
                var items = new List<ConfigJsonValue>();
                SkipTrivia();
                while (Peek() != ']')
                {
                    items.Add(ParseValue());
                    if (!SeparatorOrEnd(']'))
                        break;
                }
                Expect(']');
                _depth--;
                return new ConfigJsonValue(ConfigJsonKind.Array, items: items);
            }

            /// <summary>After a member or item: true when a comma follows (a trailing comma is allowed).</summary>
            private bool SeparatorOrEnd(char close)
            {
                SkipTrivia();
                if (Peek() == ',')
                {
                    _position++;
                    SkipTrivia();
                    return true;
                }
                if (Peek() != close)
                    throw Error($"expected ',' or '{close}'");
                return false;
            }

            private string ParseString()
            {
                _position++; // opening quote
                var builder = new StringBuilder();
                while (true)
                {
                    if (_position >= _text.Length)
                        throw Error("unterminated string");
                    char c = _text[_position++];
                    if (c == '"')
                        return builder.ToString();
                    if (c < ' ')
                        throw Error("control character in a string");
                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }
                    if (_position >= _text.Length)
                        throw Error("unterminated string");
                    char escape = _text[_position++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (_position + 4 > _text.Length
                                || !int.TryParse(_text.Substring(_position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                                throw Error("bad \\u escape");
                            builder.Append((char)code);
                            _position += 4;
                            break;
                        default:
                            throw Error($"bad escape '\\{escape}'");
                    }
                }
            }

            /// <summary>Standard JSON number grammar: -?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?</summary>
            private string ParseNumber()
            {
                int start = _position;
                if (Peek() == '-')
                    _position++;
                if (Peek() == '0')
                    _position++;
                else if (!Digits())
                    throw Error("bad number");
                if (Peek() == '.')
                {
                    _position++;
                    if (!Digits())
                        throw Error("bad number");
                }
                if (Peek() == 'e' || Peek() == 'E')
                {
                    _position++;
                    if (Peek() == '+' || Peek() == '-')
                        _position++;
                    if (!Digits())
                        throw Error("bad number");
                }
                return _text.Substring(start, _position - start);
            }

            private bool Digits()
            {
                int start = _position;
                while (_position < _text.Length && _text[_position] >= '0' && _text[_position] <= '9')
                    _position++;
                return _position > start;
            }

            private void SkipTrivia()
            {
                while (_position < _text.Length)
                {
                    char c = _text[_position];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '﻿')
                    {
                        _position++;
                    }
                    else if (c == '/' && _position + 1 < _text.Length && _text[_position + 1] == '/')
                    {
                        while (_position < _text.Length && _text[_position] != '\n')
                            _position++;
                    }
                    else if (c == '/' && _position + 1 < _text.Length && _text[_position + 1] == '*')
                    {
                        int end = _text.IndexOf("*/", _position + 2, StringComparison.Ordinal);
                        if (end < 0)
                            throw Error("unterminated comment");
                        _position = end + 2;
                    }
                    else
                    {
                        return;
                    }
                }
            }

            private void ExpectWord(string word)
            {
                if (string.CompareOrdinal(_text, _position, word, 0, word.Length) != 0)
                    throw Error($"expected '{word}'");
                _position += word.Length;
            }

            private void Expect(char c)
            {
                if (Peek() != c)
                    throw Error($"expected '{c}'");
                _position++;
            }

            private void Enter()
            {
                if (++_depth > MaxDepth)
                    throw Error("nesting is too deep");
            }

            private char Peek() => _position < _text.Length ? _text[_position] : '\0';

            private FormatException Error(string message)
            {
                int line = 1;
                int column = 1;
                for (int i = 0; i < _position && i < _text.Length; i++)
                {
                    if (_text[i] == '\n')
                    {
                        line++;
                        column = 1;
                    }
                    else
                    {
                        column++;
                    }
                }
                return new FormatException($"{message} at line {line}, column {column}");
            }
        }
    }
}
