using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MilestoneAdmin.Protocol.Json
{
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// Minimal JSON document model. It exists so that the PowerShell worker process carries no third-party
    /// assemblies that could conflict with the dependencies MilestonePSTools loads into the same process
    /// (see docs/adr/0001-process-architecture.md).
    /// </summary>
    public abstract class JsonNode
    {
        public abstract JsonKind Kind { get; }

        public bool IsNull => Kind == JsonKind.Null;

        public static JsonNode Parse(string json) => JsonParser.Parse(json);

        public string ToJson(bool indented = false)
        {
            var sb = new StringBuilder();
            JsonWriter.Write(this, sb, indented, 0);
            return sb.ToString();
        }

        public override string ToString() => ToJson();

        public virtual JsonNode this[string key]
        {
            get => null;
            set => throw new InvalidOperationException("Only JSON objects can be indexed by key.");
        }

        public virtual JsonNode this[int index]
        {
            get => null;
            set => throw new InvalidOperationException("Only JSON arrays can be indexed by position.");
        }

        public virtual string AsString() => null;

        public virtual bool? AsBool() => null;

        public virtual long? AsInt64() => null;

        public virtual double? AsDouble() => null;

        public JsonArray AsArray() => this as JsonArray;

        public JsonObject AsObject() => this as JsonObject;

        public static JsonNode From(object value)
        {
            switch (value)
            {
                case null:
                    return JsonNull.Instance;
                case JsonNode n:
                    return n;
                case string s:
                    return new JsonString(s);
                case bool b:
                    return JsonBool.Of(b);
                case char c:
                    return new JsonString(c.ToString());
                case byte or sbyte or short or ushort or int or uint or long:
                    return new JsonNumber(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                case ulong ul:
                    return JsonNumber.FromRaw(ul.ToString(CultureInfo.InvariantCulture));
                case float f:
                    return new JsonNumber(f);
                case double d:
                    return new JsonNumber(d);
                case decimal m:
                    return JsonNumber.FromRaw(m.ToString(CultureInfo.InvariantCulture));
                case Guid g:
                    return new JsonString(g.ToString("D"));
                case DateTime dt:
                    return new JsonString(dt.ToString("o", CultureInfo.InvariantCulture));
                case DateTimeOffset dto:
                    return new JsonString(dto.ToString("o", CultureInfo.InvariantCulture));
                case TimeSpan ts:
                    return new JsonString(ts.ToString("c", CultureInfo.InvariantCulture));
                case Uri u:
                    return new JsonString(u.OriginalString);
                case Enum e:
                    return new JsonString(e.ToString());
                case IDictionary<string, object> dict:
                {
                    var o = new JsonObject();
                    foreach (var kv in dict) o[kv.Key] = From(kv.Value);
                    return o;
                }
                case IEnumerable seq:
                {
                    var a = new JsonArray();
                    foreach (var item in seq) a.Add(From(item));
                    return a;
                }
                default:
                    return new JsonString(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
        }

        public static implicit operator JsonNode(string value) => value == null ? (JsonNode)JsonNull.Instance : new JsonString(value);

        public static implicit operator JsonNode(bool value) => JsonBool.Of(value);

        public static implicit operator JsonNode(long value) => new JsonNumber(value);

        public static implicit operator JsonNode(int value) => new JsonNumber(value);

        public static implicit operator JsonNode(double value) => new JsonNumber(value);
    }

    public sealed class JsonNull : JsonNode
    {
        public static readonly JsonNull Instance = new JsonNull();

        private JsonNull()
        {
        }

        public override JsonKind Kind => JsonKind.Null;
    }

    public sealed class JsonBool : JsonNode
    {
        public static readonly JsonBool True = new JsonBool(true);
        public static readonly JsonBool False = new JsonBool(false);

        private JsonBool(bool value)
        {
            Value = value;
        }

        public bool Value { get; }

        public override JsonKind Kind => JsonKind.Bool;

        public static JsonBool Of(bool value) => value ? True : False;

        public override bool? AsBool() => Value;
    }

    public sealed class JsonNumber : JsonNode
    {
        private JsonNumber(string raw, bool _)
        {
            Raw = raw;
        }

        public JsonNumber(long value)
        {
            Raw = value.ToString(CultureInfo.InvariantCulture);
        }

        public JsonNumber(double value)
        {
            Raw = double.IsNaN(value) || double.IsInfinity(value) ? null : value.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>The literal as it appears in JSON. Null for NaN/Infinity, which are written as null.</summary>
        public string Raw { get; }

        public override JsonKind Kind => Raw == null ? JsonKind.Null : JsonKind.Number;

        internal static JsonNumber FromRaw(string raw) => new JsonNumber(raw, true);

        public override long? AsInt64()
        {
            if (Raw == null) return null;
            if (long.TryParse(Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            if (double.TryParse(Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d % 1) < double.Epsilon && d >= long.MinValue && d <= long.MaxValue)
                return (long)d;
            return null;
        }

        public override double? AsDouble()
        {
            if (Raw == null) return null;
            return double.TryParse(Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
        }

        public override string AsString() => Raw;
    }

    public sealed class JsonString : JsonNode
    {
        public JsonString(string value)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
        }

        public string Value { get; }

        public override JsonKind Kind => JsonKind.String;

        public override string AsString() => Value;

        public override bool? AsBool()
        {
            if (bool.TryParse(Value, out var b)) return b;
            return null;
        }

        public override long? AsInt64() => long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : (long?)null;

        public override double? AsDouble() => double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
    }

    public sealed class JsonArray : JsonNode, IList<JsonNode>
    {
        private readonly List<JsonNode> _items = new List<JsonNode>();

        public JsonArray()
        {
        }

        public JsonArray(IEnumerable<JsonNode> items)
        {
            foreach (var i in items) Add(i);
        }

        public override JsonKind Kind => JsonKind.Array;

        public int Count => _items.Count;

        public bool IsReadOnly => false;

        public override JsonNode this[int index]
        {
            get => index >= 0 && index < _items.Count ? _items[index] : null;
            set => _items[index] = value ?? JsonNull.Instance;
        }

        public void Add(JsonNode item) => _items.Add(item ?? JsonNull.Instance);

        public void Clear() => _items.Clear();

        public bool Contains(JsonNode item) => _items.Contains(item);

        public void CopyTo(JsonNode[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

        public IEnumerator<JsonNode> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

        public int IndexOf(JsonNode item) => _items.IndexOf(item);

        public void Insert(int index, JsonNode item) => _items.Insert(index, item ?? JsonNull.Instance);

        public bool Remove(JsonNode item) => _items.Remove(item);

        public void RemoveAt(int index) => _items.RemoveAt(index);

        public IEnumerable<string> Strings()
        {
            foreach (var i in _items)
            {
                var s = i.AsString();
                if (s != null) yield return s;
            }
        }
    }

    /// <summary>Ordered JSON object with case-sensitive keys (last write wins on duplicate keys while parsing).</summary>
    public sealed class JsonObject : JsonNode, IEnumerable<KeyValuePair<string, JsonNode>>
    {
        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, JsonNode> _map = new Dictionary<string, JsonNode>(StringComparer.Ordinal);

        public override JsonKind Kind => JsonKind.Object;

        public int Count => _order.Count;

        public IEnumerable<string> Keys => _order;

        public override JsonNode this[string key]
        {
            get => key != null && _map.TryGetValue(key, out var v) ? v : null;
            set => Set(key, value);
        }

        public JsonObject Set(string key, JsonNode value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (!_map.ContainsKey(key)) _order.Add(key);
            _map[key] = value ?? JsonNull.Instance;
            return this;
        }

        public JsonObject Set(string key, object value) => Set(key, From(value));

        public bool ContainsKey(string key) => _map.ContainsKey(key);

        public bool Remove(string key)
        {
            if (!_map.Remove(key)) return false;
            _order.Remove(key);
            return true;
        }

        public bool TryGet(string key, out JsonNode value) => _map.TryGetValue(key, out value);

        public string GetString(string key, string fallback = null) => this[key]?.AsString() ?? fallback;

        public bool GetBool(string key, bool fallback = false) => this[key]?.AsBool() ?? fallback;

        public long GetInt64(string key, long fallback = 0) => this[key]?.AsInt64() ?? fallback;

        public double GetDouble(string key, double fallback = 0) => this[key]?.AsDouble() ?? fallback;

        public JsonObject GetObject(string key) => this[key] as JsonObject;

        public JsonArray GetArray(string key) => this[key] as JsonArray;

        public IEnumerator<KeyValuePair<string, JsonNode>> GetEnumerator()
        {
            foreach (var k in _order) yield return new KeyValuePair<string, JsonNode>(k, _map[k]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // Collection-initializer support: new JsonObject { { "a", 1 } }
        public void Add(string key, JsonNode value) => Set(key, value);
    }
}
