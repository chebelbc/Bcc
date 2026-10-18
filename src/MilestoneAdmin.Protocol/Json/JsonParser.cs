using System;
using System.Globalization;
using System.Text;

namespace MilestoneAdmin.Protocol.Json
{
    public sealed class JsonParseException : FormatException
    {
        public JsonParseException(string message, int position)
            : base($"{message} (at position {position})")
        {
            Position = position;
        }

        public int Position { get; }
    }

    /// <summary>Strict RFC 8259 recursive-descent parser with a nesting limit.</summary>
    public static class JsonParser
    {
        public const int MaxDepth = 256;

        public static JsonNode Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var p = new State(json);
            p.SkipWhitespace();
            var node = p.ParseValue(0);
            p.SkipWhitespace();
            if (!p.End) throw new JsonParseException("Unexpected trailing characters", p.Pos);
            return node;
        }

        private sealed class State
        {
            private readonly string _s;

            public State(string s)
            {
                _s = s;
            }

            public int Pos;

            public bool End => Pos >= _s.Length;

            public void SkipWhitespace()
            {
                while (Pos < _s.Length)
                {
                    var c = _s[Pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') Pos++;
                    else break;
                }
            }

            public JsonNode ParseValue(int depth)
            {
                if (depth > MaxDepth) throw new JsonParseException("Maximum nesting depth exceeded", Pos);
                if (End) throw new JsonParseException("Unexpected end of input", Pos);
                var c = _s[Pos];
                switch (c)
                {
                    case '{': return ParseObject(depth);
                    case '[': return ParseArray(depth);
                    case '"': return new JsonString(ParseString());
                    case 't': Expect("true"); return JsonBool.True;
                    case 'f': Expect("false"); return JsonBool.False;
                    case 'n': Expect("null"); return JsonNull.Instance;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                        throw new JsonParseException($"Unexpected character '{c}'", Pos);
                }
            }

            private void Expect(string literal)
            {
                if (string.CompareOrdinal(_s, Pos, literal, 0, literal.Length) != 0)
                    throw new JsonParseException($"Expected '{literal}'", Pos);
                Pos += literal.Length;
            }

            private JsonObject ParseObject(int depth)
            {
                var obj = new JsonObject();
                Pos++; // {
                SkipWhitespace();
                if (!End && _s[Pos] == '}')
                {
                    Pos++;
                    return obj;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (End || _s[Pos] != '"') throw new JsonParseException("Expected property name", Pos);
                    var key = ParseString();
                    SkipWhitespace();
                    if (End || _s[Pos] != ':') throw new JsonParseException("Expected ':'", Pos);
                    Pos++;
                    SkipWhitespace();
                    obj.Set(key, ParseValue(depth + 1));
                    SkipWhitespace();
                    if (End) throw new JsonParseException("Unterminated object", Pos);
                    if (_s[Pos] == ',')
                    {
                        Pos++;
                        continue;
                    }

                    if (_s[Pos] == '}')
                    {
                        Pos++;
                        return obj;
                    }

                    throw new JsonParseException("Expected ',' or '}'", Pos);
                }
            }

            private JsonArray ParseArray(int depth)
            {
                var arr = new JsonArray();
                Pos++; // [
                SkipWhitespace();
                if (!End && _s[Pos] == ']')
                {
                    Pos++;
                    return arr;
                }

                while (true)
                {
                    SkipWhitespace();
                    arr.Add(ParseValue(depth + 1));
                    SkipWhitespace();
                    if (End) throw new JsonParseException("Unterminated array", Pos);
                    if (_s[Pos] == ',')
                    {
                        Pos++;
                        continue;
                    }

                    if (_s[Pos] == ']')
                    {
                        Pos++;
                        return arr;
                    }

                    throw new JsonParseException("Expected ',' or ']'", Pos);
                }
            }

            private string ParseString()
            {
                Pos++; // opening quote
                StringBuilder sb = null;
                var start = Pos;
                while (true)
                {
                    if (End) throw new JsonParseException("Unterminated string", Pos);
                    var c = _s[Pos];
                    if (c == '"')
                    {
                        string result;
                        if (sb == null) result = _s.Substring(start, Pos - start);
                        else
                        {
                            sb.Append(_s, start, Pos - start);
                            result = sb.ToString();
                        }

                        Pos++;
                        return result;
                    }

                    if (c < 0x20) throw new JsonParseException("Control character in string", Pos);
                    if (c != '\\')
                    {
                        Pos++;
                        continue;
                    }

                    sb ??= new StringBuilder();
                    sb.Append(_s, start, Pos - start);
                    Pos++;
                    if (End) throw new JsonParseException("Unterminated escape", Pos);
                    var e = _s[Pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Pos + 4 > _s.Length) throw new JsonParseException("Truncated unicode escape", Pos);
                            if (!int.TryParse(_s.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                throw new JsonParseException("Invalid unicode escape", Pos);
                            sb.Append((char)code);
                            Pos += 4;
                            break;
                        default:
                            throw new JsonParseException($"Invalid escape '\\{e}'", Pos - 1);
                    }

                    start = Pos;
                }
            }

            private JsonNode ParseNumber()
            {
                var start = Pos;
                if (_s[Pos] == '-') Pos++;
                if (End) throw new JsonParseException("Invalid number", start);
                if (_s[Pos] == '0') Pos++;
                else if (_s[Pos] >= '1' && _s[Pos] <= '9')
                {
                    while (!End && char.IsDigit(_s[Pos]) && _s[Pos] < 128) Pos++;
                }
                else throw new JsonParseException("Invalid number", start);

                if (!End && _s[Pos] == '.')
                {
                    Pos++;
                    var digits = Pos;
                    while (!End && _s[Pos] >= '0' && _s[Pos] <= '9') Pos++;
                    if (Pos == digits) throw new JsonParseException("Invalid fraction", start);
                }

                if (!End && (_s[Pos] == 'e' || _s[Pos] == 'E'))
                {
                    Pos++;
                    if (!End && (_s[Pos] == '+' || _s[Pos] == '-')) Pos++;
                    var digits = Pos;
                    while (!End && _s[Pos] >= '0' && _s[Pos] <= '9') Pos++;
                    if (Pos == digits) throw new JsonParseException("Invalid exponent", start);
                }

                return JsonNumber.FromRaw(_s.Substring(start, Pos - start));
            }
        }
    }

    public static class JsonWriter
    {
        public static void Write(JsonNode node, StringBuilder sb, bool indented, int level)
        {
            switch (node?.Kind ?? JsonKind.Null)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;
                case JsonKind.Bool:
                    sb.Append(((JsonBool)node).Value ? "true" : "false");
                    break;
                case JsonKind.Number:
                    sb.Append(((JsonNumber)node).Raw);
                    break;
                case JsonKind.String:
                    WriteString(((JsonString)node).Value, sb);
                    break;
                case JsonKind.Array:
                {
                    var arr = (JsonArray)node;
                    if (arr.Count == 0)
                    {
                        sb.Append("[]");
                        break;
                    }

                    sb.Append('[');
                    var first = true;
                    foreach (var item in arr)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        NewLine(sb, indented, level + 1);
                        Write(item, sb, indented, level + 1);
                    }

                    NewLine(sb, indented, level);
                    sb.Append(']');
                    break;
                }
                case JsonKind.Object:
                {
                    var obj = (JsonObject)node;
                    if (obj.Count == 0)
                    {
                        sb.Append("{}");
                        break;
                    }

                    sb.Append('{');
                    var first = true;
                    foreach (var kv in obj)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        NewLine(sb, indented, level + 1);
                        WriteString(kv.Key, sb);
                        sb.Append(indented ? ": " : ":");
                        Write(kv.Value, sb, indented, level + 1);
                    }

                    NewLine(sb, indented, level);
                    sb.Append('}');
                    break;
                }
            }
        }

        private static void NewLine(StringBuilder sb, bool indented, int level)
        {
            if (!indented) return;
            sb.Append('\n');
            sb.Append(' ', level * 2);
        }

        public static void WriteString(string s, StringBuilder sb)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        // Control characters and line/paragraph separators are escaped so a document is always one line.
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }

            sb.Append('"');
        }
    }
}
