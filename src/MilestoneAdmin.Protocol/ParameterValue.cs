using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.Protocol
{
    public enum ParameterValueKind
    {
        /// <summary>A JSON string, number or boolean. The worker converts it to the parameter's declared .NET type.</summary>
        Literal,

        /// <summary>A switch parameter (present / explicitly false).</summary>
        Switch,

        /// <summary>A list of values (each may itself be a handle, reference or literal).</summary>
        List,

        /// <summary>A secret delivered in the request's secrets section; becomes a SecureString in the worker.</summary>
        Secret,

        /// <summary>User name + secret; becomes a PSCredential in the worker.</summary>
        Credential,

        /// <summary>An object previously returned by the worker, identified by its handle.</summary>
        Handle,

        /// <summary>An XProtect object identified by type and Id; the worker re-resolves it with the type's Get- command.</summary>
        Reference,

        /// <summary>A string-keyed table of literal values (for Hashtable/IDictionary parameters).</summary>
        Table
    }

    /// <summary>
    /// A typed parameter value as sent from the GUI to the worker. Values are data, never script: the worker binds them
    /// with PowerShell.AddParameter after converting to the parameter's declared type.
    /// </summary>
    public sealed class ParameterValue
    {
        private ParameterValue(ParameterValueKind kind)
        {
            Kind = kind;
        }

        public ParameterValueKind Kind { get; }

        public JsonNode Literal { get; private set; }

        public bool SwitchValue { get; private set; }

        public IReadOnlyList<ParameterValue> Items { get; private set; }

        public string SecretId { get; private set; }

        public string UserName { get; private set; }

        public string HandleId { get; private set; }

        public string ObjectType { get; private set; }

        public string ObjectId { get; private set; }

        public IReadOnlyDictionary<string, JsonNode> Table { get; private set; }

        public static ParameterValue FromLiteral(JsonNode value)
        {
            if (value == null || value.Kind == JsonKind.Object || value.Kind == JsonKind.Array)
                throw new ArgumentException("Literal values must be strings, numbers, booleans or null.", nameof(value));
            return new ParameterValue(ParameterValueKind.Literal) { Literal = value };
        }

        public static ParameterValue String(string value) => FromLiteral(value == null ? (JsonNode)JsonNull.Instance : new JsonString(value));

        public static ParameterValue Integer(long value) => FromLiteral(new JsonNumber(value));

        public static ParameterValue Number(double value) => FromLiteral(new JsonNumber(value));

        public static ParameterValue Boolean(bool value) => FromLiteral(JsonBool.Of(value));

        public static ParameterValue Guid(Guid value) => String(value.ToString("D"));

        public static ParameterValue DateTime(DateTime value) => String(value.ToString("o", CultureInfo.InvariantCulture));

        public static ParameterValue Switch(bool present = true) => new ParameterValue(ParameterValueKind.Switch) { SwitchValue = present };

        public static ParameterValue List(IEnumerable<ParameterValue> items) =>
            new ParameterValue(ParameterValueKind.List) { Items = items.ToList() };

        public static ParameterValue StringList(IEnumerable<string> items) => List(items.Select(String));

        public static ParameterValue Secret(string secretId) =>
            new ParameterValue(ParameterValueKind.Secret) { SecretId = Require(secretId, nameof(secretId)) };

        public static ParameterValue Credential(string userName, string secretId) =>
            new ParameterValue(ParameterValueKind.Credential) { UserName = Require(userName, nameof(userName)), SecretId = Require(secretId, nameof(secretId)) };

        public static ParameterValue Handle(string handleId) =>
            new ParameterValue(ParameterValueKind.Handle) { HandleId = Require(handleId, nameof(handleId)) };

        public static ParameterValue Reference(string objectType, string objectId) =>
            new ParameterValue(ParameterValueKind.Reference) { ObjectType = Require(objectType, nameof(objectType)), ObjectId = Require(objectId, nameof(objectId)) };

        public static ParameterValue FromTable(IDictionary<string, JsonNode> table)
        {
            foreach (var kv in table)
            {
                if (kv.Value != null && (kv.Value.Kind == JsonKind.Object || kv.Value.Kind == JsonKind.Array))
                    throw new ArgumentException($"Table value '{kv.Key}' must be a literal.");
            }

            return new ParameterValue(ParameterValueKind.Table) { Table = new Dictionary<string, JsonNode>(table, StringComparer.OrdinalIgnoreCase) };
        }

        private static string Require(string value, string name)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("Value is required.", name);
            return value;
        }

        public JsonNode ToJson()
        {
            switch (Kind)
            {
                case ParameterValueKind.Literal:
                    return Literal;
                case ParameterValueKind.Switch:
                    return new JsonObject { { "$switch", SwitchValue } };
                case ParameterValueKind.List:
                    return new JsonArray(Items.Select(i => i.ToJson()));
                case ParameterValueKind.Secret:
                    return new JsonObject { { "$secret", SecretId } };
                case ParameterValueKind.Credential:
                    return new JsonObject { { "$credential", new JsonObject { { "userName", UserName }, { "secret", SecretId } } } };
                case ParameterValueKind.Handle:
                    return new JsonObject { { "$handle", HandleId } };
                case ParameterValueKind.Reference:
                    return new JsonObject { { "$ref", new JsonObject { { "type", ObjectType }, { "id", ObjectId } } } };
                case ParameterValueKind.Table:
                {
                    var t = new JsonObject();
                    foreach (var kv in Table) t.Set(kv.Key, kv.Value);
                    return new JsonObject { { "$table", t } };
                }
                default:
                    throw new InvalidOperationException();
            }
        }

        public static ParameterValue FromJson(JsonNode node)
        {
            if (node == null) throw new ProtocolException("Missing parameter value.");
            switch (node.Kind)
            {
                case JsonKind.Null:
                case JsonKind.Bool:
                case JsonKind.Number:
                case JsonKind.String:
                    return FromLiteral(node);
                case JsonKind.Array:
                    return List(node.AsArray().Select(FromJson));
                case JsonKind.Object:
                {
                    var o = node.AsObject();
                    if (o.Count != 1) throw new ProtocolException("A structured parameter value must have exactly one '$' key.");
                    var key = o.Keys.First();
                    var v = o[key];
                    switch (key)
                    {
                        case "$switch":
                            return Switch(v.AsBool() ?? throw new ProtocolException("$switch must be a boolean."));
                        case "$secret":
                            return Secret(v.AsString() ?? throw new ProtocolException("$secret must be a string id."));
                        case "$credential":
                        {
                            var c = v.AsObject() ?? throw new ProtocolException("$credential must be an object.");
                            return Credential(c.GetString("userName") ?? throw new ProtocolException("$credential.userName is required."),
                                c.GetString("secret") ?? throw new ProtocolException("$credential.secret is required."));
                        }
                        case "$handle":
                            return Handle(v.AsString() ?? throw new ProtocolException("$handle must be a string."));
                        case "$ref":
                        {
                            var r = v.AsObject() ?? throw new ProtocolException("$ref must be an object.");
                            return Reference(r.GetString("type") ?? throw new ProtocolException("$ref.type is required."),
                                r.GetString("id") ?? throw new ProtocolException("$ref.id is required."));
                        }
                        case "$table":
                        {
                            var t = v.AsObject() ?? throw new ProtocolException("$table must be an object.");
                            var d = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
                            foreach (var kv in t) d[kv.Key] = kv.Value;
                            try
                            {
                                return FromTable(d);
                            }
                            catch (ArgumentException ex)
                            {
                                throw new ProtocolException(ex.Message);
                            }
                        }
                        default:
                            throw new ProtocolException($"Unknown structured parameter value '{key}'.");
                    }
                }
                default:
                    throw new ProtocolException("Unsupported parameter value.");
            }
        }

        /// <summary>Secret ids referenced by this value (recursively).</summary>
        public IEnumerable<string> ReferencedSecrets()
        {
            if (SecretId != null) yield return SecretId;
            if (Items != null)
            {
                foreach (var i in Items)
                foreach (var s in i.ReferencedSecrets())
                    yield return s;
            }
        }
    }

    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message)
            : base(message)
        {
        }
    }
}
