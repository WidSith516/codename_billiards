using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Rewind.Sim;

public enum JsonKind
{
    Null,
    Bool,
    Number,
    String,
    Array,
    Object,
}

/// <summary>
/// Minimal JSON document node. Deliberately hand-written instead of System.Text.Json:
/// it keeps the kernel free of engine and reflection dependencies (so the M4 solvability
/// solver can link the same files), it is deterministic in member order, and it cannot be
/// surprised by trimming/AOT settings in an export.
/// </summary>
public sealed class JsonValue
{
    public JsonKind Kind;

    public bool Bool;
    public double Number;
    public string Str;
    public List<JsonValue> Items;
    public List<KeyValuePair<string, JsonValue>> Members;

    public static JsonValue Null() => new JsonValue { Kind = JsonKind.Null };

    public static JsonValue Of(bool v) => new JsonValue { Kind = JsonKind.Bool, Bool = v };

    public static JsonValue Of(double v) => new JsonValue { Kind = JsonKind.Number, Number = v };

    public static JsonValue Of(string v) => new JsonValue { Kind = JsonKind.String, Str = v ?? string.Empty };

    public static JsonValue Array()
    {
        return new JsonValue { Kind = JsonKind.Array, Items = new List<JsonValue>() };
    }

    public static JsonValue Object()
    {
        return new JsonValue
        {
            Kind = JsonKind.Object,
            Members = new List<KeyValuePair<string, JsonValue>>(),
        };
    }

    public JsonValue Add(JsonValue item)
    {
        Items.Add(item);
        return this;
    }

    public JsonValue Set(string key, JsonValue value)
    {
        for (var i = 0; i < Members.Count; i++)
        {
            if (Members[i].Key == key)
            {
                Members[i] = new KeyValuePair<string, JsonValue>(key, value);
                return this;
            }
        }

        Members.Add(new KeyValuePair<string, JsonValue>(key, value));
        return this;
    }

    public bool Has(string key)
    {
        if (Kind != JsonKind.Object) return false;
        foreach (var kv in Members)
        {
            if (kv.Key == key) return true;
        }

        return false;
    }

    public JsonValue Get(string key)
    {
        if (Kind == JsonKind.Object)
        {
            foreach (var kv in Members)
            {
                if (kv.Key == key) return kv.Value;
            }
        }

        return null;
    }

    public JsonValue Require(string key)
    {
        var v = Get(key);
        if (v == null) throw new JsonException("missing required member '" + key + "'");
        return v;
    }

    public int Count => Kind == JsonKind.Array ? Items.Count : 0;

    public JsonValue At(int index) => Items[index];

    public double AsDouble(double fallback = 0.0)
    {
        switch (Kind)
        {
            case JsonKind.Number:
                return Number;
            case JsonKind.Bool:
                return Bool ? 1.0 : 0.0;
            case JsonKind.String:
                return double.TryParse(Str, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
            default:
                return fallback;
        }
    }

    public double Num(string key, double fallback)
    {
        var v = Get(key);
        return v == null ? fallback : v.AsDouble(fallback);
    }

    public int Int(string key, int fallback)
    {
        var v = Get(key);
        return v == null ? fallback : (int)Math.Round(v.AsDouble(fallback));
    }

    public bool Flag(string key, bool fallback)
    {
        var v = Get(key);
        if (v == null) return fallback;
        return v.Kind == JsonKind.Bool ? v.Bool : v.AsDouble() != 0.0;
    }

    public string Text(string key, string fallback)
    {
        var v = Get(key);
        return v == null || v.Kind != JsonKind.String ? fallback : v.Str;
    }

    public JsonValue Arr(string key)
    {
        var v = Get(key);
        if (v == null || v.Kind != JsonKind.Array) throw new JsonException("member '" + key + "' must be an array");
        return v;
    }

    public JsonValue Obj(string key)
    {
        var v = Get(key);
        if (v == null || v.Kind != JsonKind.Object) throw new JsonException("member '" + key + "' must be an object");
        return v;
    }
}

public sealed class JsonException : Exception
{
    public JsonException(string message) : base(message)
    {
    }
}

public static class Json
{
    public static JsonValue Parse(string text)
    {
        var p = new Parser(text);
        var v = p.ParseValue();
        p.SkipWhitespace();
        if (!p.AtEnd) throw new JsonException("trailing content at offset " + p.Position);
        return v;
    }

    /// <summary>
    /// Compact writer. Doubles use "R" (round-trippable) with InvariantCulture, so
    /// encode -> decode is bit-exact — required for the state hash round-trip test.
    /// </summary>
    public static string Write(JsonValue value, bool indented = false)
    {
        var sb = new StringBuilder(1024);
        WriteValue(sb, value, indented, 0);
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, JsonValue v, bool indented, int depth)
    {
        if (v == null)
        {
            sb.Append("null");
            return;
        }

        switch (v.Kind)
        {
            case JsonKind.Null:
                sb.Append("null");
                break;
            case JsonKind.Bool:
                sb.Append(v.Bool ? "true" : "false");
                break;
            case JsonKind.Number:
                sb.Append(FormatNumber(v.Number));
                break;
            case JsonKind.String:
                WriteString(sb, v.Str);
                break;
            case JsonKind.Array:
                WriteArray(sb, v, indented, depth);
                break;
            case JsonKind.Object:
                WriteObject(sb, v, indented, depth);
                break;
            default:
                throw new JsonException("unknown json kind");
        }
    }

    private static void WriteArray(StringBuilder sb, JsonValue v, bool indented, int depth)
    {
        if (v.Items.Count == 0)
        {
            sb.Append("[]");
            return;
        }

        sb.Append('[');
        for (var i = 0; i < v.Items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            if (indented) NewLine(sb, depth + 1);
            WriteValue(sb, v.Items[i], indented, depth + 1);
        }

        if (indented) NewLine(sb, depth);
        sb.Append(']');
    }

    private static void WriteObject(StringBuilder sb, JsonValue v, bool indented, int depth)
    {
        if (v.Members.Count == 0)
        {
            sb.Append("{}");
            return;
        }

        sb.Append('{');
        for (var i = 0; i < v.Members.Count; i++)
        {
            if (i > 0) sb.Append(',');
            if (indented) NewLine(sb, depth + 1);
            WriteString(sb, v.Members[i].Key);
            sb.Append(':');
            if (indented) sb.Append(' ');
            WriteValue(sb, v.Members[i].Value, indented, depth + 1);
        }

        if (indented) NewLine(sb, depth);
        sb.Append('}');
    }

    private static void NewLine(StringBuilder sb, int depth)
    {
        sb.Append('\n');
        sb.Append(' ', depth * 2);
    }

    public static string FormatNumber(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
            throw new JsonException("non-finite number cannot be serialized: " + d);
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
        {
            // Integers stay integers: keeps level files and state dumps readable.
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        }

        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    private static void WriteString(StringBuilder sb, string s)
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
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s)
        {
            _s = s ?? string.Empty;
        }

        public int Position => _i;

        public bool AtEnd => _i >= _s.Length;

        public void SkipWhitespace()
        {
            while (_i < _s.Length)
            {
                var c = _s[_i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _i++;
                else break;
            }
        }

        public JsonValue ParseValue()
        {
            SkipWhitespace();
            if (AtEnd) throw new JsonException("unexpected end of input");
            var c = _s[_i];
            switch (c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return JsonValue.Of(ParseString());
                case 't':
                    Expect("true");
                    return JsonValue.Of(true);
                case 'f':
                    Expect("false");
                    return JsonValue.Of(false);
                case 'n':
                    Expect("null");
                    return JsonValue.Null();
                default:
                    return JsonValue.Of(ParseNumber());
            }
        }

        private JsonValue ParseObject()
        {
            var obj = JsonValue.Object();
            _i++; // '{'
            SkipWhitespace();
            if (!AtEnd && _s[_i] == '}')
            {
                _i++;
                return obj;
            }

            while (true)
            {
                SkipWhitespace();
                if (AtEnd || _s[_i] != '"') throw new JsonException("expected object key at offset " + _i);
                var key = ParseString();
                SkipWhitespace();
                if (AtEnd || _s[_i] != ':') throw new JsonException("expected ':' at offset " + _i);
                _i++;
                obj.Set(key, ParseValue());
                SkipWhitespace();
                if (AtEnd) throw new JsonException("unterminated object");
                if (_s[_i] == ',')
                {
                    _i++;
                    continue;
                }

                if (_s[_i] == '}')
                {
                    _i++;
                    return obj;
                }

                throw new JsonException("expected ',' or '}' at offset " + _i);
            }
        }

        private JsonValue ParseArray()
        {
            var arr = JsonValue.Array();
            _i++; // '['
            SkipWhitespace();
            if (!AtEnd && _s[_i] == ']')
            {
                _i++;
                return arr;
            }

            while (true)
            {
                arr.Add(ParseValue());
                SkipWhitespace();
                if (AtEnd) throw new JsonException("unterminated array");
                if (_s[_i] == ',')
                {
                    _i++;
                    continue;
                }

                if (_s[_i] == ']')
                {
                    _i++;
                    return arr;
                }

                throw new JsonException("expected ',' or ']' at offset " + _i);
            }
        }

        private string ParseString()
        {
            _i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd) throw new JsonException("unterminated string");
                var c = _s[_i++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (AtEnd) throw new JsonException("unterminated escape");
                var e = _s[_i++];
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
                        if (_i + 4 > _s.Length) throw new JsonException("bad \\u escape");
                        var hex = _s.Substring(_i, 4);
                        _i += 4;
                        sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        break;
                    default:
                        throw new JsonException("bad escape '\\" + e + "'");
                }
            }
        }

        private double ParseNumber()
        {
            var start = _i;
            if (!AtEnd && (_s[_i] == '-' || _s[_i] == '+')) _i++;
            while (!AtEnd)
            {
                var c = _s[_i];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') _i++;
                else break;
            }

            var text = _s.Substring(start, _i - start);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                throw new JsonException("bad number '" + text + "' at offset " + start);
            return d;
        }

        private void Expect(string literal)
        {
            if (_i + literal.Length > _s.Length || _s.Substring(_i, literal.Length) != literal)
                throw new JsonException("expected '" + literal + "' at offset " + _i);
            _i += literal.Length;
        }
    }
}
