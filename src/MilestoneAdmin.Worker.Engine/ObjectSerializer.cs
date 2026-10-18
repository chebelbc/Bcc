using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Management.Automation;
using System.Net;
using System.Security;
using System.Text.RegularExpressions;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.Worker.Engine
{
    /// <summary>Objects returned to the GUI, addressable by handle in later requests.</summary>
    public sealed class HandleTable
    {
        private readonly int _capacity;
        private readonly Dictionary<string, object> _items = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Queue<string> _order = new Queue<string>();
        private long _next;

        public HandleTable(int capacity = 100000)
        {
            _capacity = capacity;
        }

        public int Count
        {
            get
            {
                lock (_items) return _items.Count;
            }
        }

        public string Register(object value)
        {
            lock (_items)
            {
                var id = "h" + (++_next).ToString(CultureInfo.InvariantCulture);
                _items[id] = value;
                _order.Enqueue(id);
                while (_items.Count > _capacity && _order.Count > 0)
                {
                    _items.Remove(_order.Dequeue());
                }

                return id;
            }
        }

        public bool TryGet(string id, out object value)
        {
            lock (_items) return _items.TryGetValue(id, out value);
        }

        public void Release(IEnumerable<string> ids)
        {
            lock (_items)
            {
                foreach (var id in ids) _items.Remove(id);
            }
        }

        public void Clear()
        {
            lock (_items)
            {
                _items.Clear();
                _order.Clear();
            }
        }
    }

    /// <summary>
    /// Converts PowerShell output objects to JSON by reading named properties. Formatted console output is never used.
    ///
    /// Without an explicit property list only properties whose declared type is simple are read: properties of complex
    /// type on MIP SDK configuration items (e.g. child folders) are lazily loaded and would trigger extra server round
    /// trips for every object.
    /// </summary>
    public sealed class ObjectSerializer
    {
        public const int MaxNestedItems = 1000;
        public const int MaxStringLength = 1024 * 1024;
        private const string Redacted = "********";

        // Kept in sync with catalog/classification-rules.json secretParameter.namePattern (+ token/connection strings).
        private static readonly Regex SecretName = new Regex("(password|secret|credential|apikey|privatekey|token|connectionstring)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly HashSet<string> SimpleTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.String", "System.Char", "System.Boolean", "System.Byte", "System.SByte", "System.Int16", "System.UInt16",
            "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.Single", "System.Double", "System.Decimal",
            "System.Guid", "System.DateTime", "System.DateTimeOffset", "System.TimeSpan", "System.Uri", "System.Version",
            "string", "char", "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal",
            "guid", "datetime", "timespan", "uri", "version", "System.Net.IPAddress", "System.String[]", "string[]",
            "System.Int32[]", "int[]", "System.Guid[]", "guid[]"
        };

        private readonly OutputOptions _options;
        private readonly HandleTable _handles;
        private readonly SecretScrubber _scrubber;

        public ObjectSerializer(OutputOptions options, HandleTable handles, SecretScrubber scrubber)
        {
            _options = options ?? new OutputOptions();
            _handles = handles;
            _scrubber = scrubber ?? SecretScrubber.None;
        }

        public JsonNode SerializeTopLevel(object value) => Serialize(value, _options.MaxDepth, topLevel: true);

        private JsonNode Serialize(object value, int depth, bool topLevel)
        {
            if (value == null) return JsonNull.Instance;
            var pso = value as PSObject;
            var baseObject = pso != null ? pso.BaseObject : value;
            if (baseObject == null) return JsonNull.Instance;

            var simple = SerializeSimple(baseObject);
            if (simple != null)
            {
                if (simple is JsonString s) return new JsonString(_scrubber.Scrub(s.Value));
                return simple;
            }

            if (baseObject is SecureString) return new JsonString(Redacted);
            if (baseObject is PSCredential cred)
            {
                return new JsonObject
                {
                    { "$type", typeof(PSCredential).FullName },
                    { "UserName", cred.UserName }
                };
            }

            if (baseObject is ScriptBlock) return new JsonObject { { "$type", "System.Management.Automation.ScriptBlock" } };

            if (baseObject is IDictionary dict)
            {
                var o = new JsonObject();
                o.Set("$type", baseObject.GetType().FullName);
                var n = 0;
                foreach (DictionaryEntry e in dict)
                {
                    if (n++ >= MaxNestedItems) break;
                    var key = Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? string.Empty;
                    if (key.StartsWith("$", StringComparison.Ordinal)) key = "_" + key;
                    o.Set(key, IsSecretName(key) && !_options.AllowSensitive ? new JsonString(Redacted) : depth > 0 ? Serialize(e.Value, depth - 1, false) : Summary(e.Value));
                }

                return o;
            }

            if (baseObject is IEnumerable seq && !(baseObject is string))
            {
                var a = new JsonArray();
                var n = 0;
                foreach (var item in seq)
                {
                    if (n++ >= MaxNestedItems) break;
                    a.Add(depth > 0 || topLevel ? Serialize(item, Math.Max(depth - 1, 0), false) : Summary(item));
                }

                return a;
            }

            var obj = new JsonObject();
            obj.Set("$type", TypeName(pso, baseObject));
            if (topLevel && _options.RegisterHandles && _handles != null) obj.Set("$handle", _handles.Register(pso ?? PSObject.AsPSObject(baseObject)));
            if (!topLevel && depth <= 0)
            {
                obj.Set("$string", SafeToString(baseObject));
                return obj;
            }

            var psobj = pso ?? PSObject.AsPSObject(baseObject);
            if (_options.Properties.Count > 0 && topLevel)
            {
                foreach (var name in _options.Properties)
                {
                    var prop = psobj.Properties[name];
                    if (prop == null) continue;
                    WriteProperty(obj, prop, depth);
                }
            }
            else
            {
                foreach (var prop in psobj.Properties)
                {
                    if (!(prop is PSNoteProperty) && !IsSimpleDeclaredType(prop.TypeNameOfValue)) continue;
                    WriteProperty(obj, prop, depth);
                }
            }

            return obj;
        }

        private void WriteProperty(JsonObject obj, PSPropertyInfo prop, int depth)
        {
            if (!prop.IsGettable) return;
            var key = prop.Name.StartsWith("$", StringComparison.Ordinal) ? "_" + prop.Name : prop.Name;
            if (IsSecretName(prop.Name) && !_options.AllowSensitive)
            {
                obj.Set(key, Redacted);
                return;
            }

            object v;
            try
            {
                v = prop.Value;
            }
            catch (Exception)
            {
                // Some adapted properties throw when read (e.g. not supported on this VMS version). Skip them.
                return;
            }

            obj.Set(key, Serialize(v, depth - 1, false));
        }

        private JsonNode Summary(object value)
        {
            var simple = SerializeSimple(value is PSObject p ? p.BaseObject : value);
            if (simple != null) return simple is JsonString s ? new JsonString(_scrubber.Scrub(s.Value)) : simple;
            if (value == null) return JsonNull.Instance;
            var b = value is PSObject pso ? pso.BaseObject : value;
            return new JsonObject { { "$type", TypeName(value as PSObject, b) }, { "$string", SafeToString(b) } };
        }

        private string SafeToString(object o)
        {
            try
            {
                var s = Convert.ToString(o, CultureInfo.InvariantCulture);
                return _scrubber.Scrub(s);
            }
            catch (Exception)
            {
                return o.GetType().FullName;
            }
        }

        private static string TypeName(PSObject pso, object baseObject)
        {
            // PSCustomObjects carry their logical type in TypeNames (e.g. "VideoOS.Platform.ConfigurationItems.Camera" in tests).
            if (pso != null && baseObject is PSCustomObject && pso.TypeNames.Count > 0) return pso.TypeNames[0];
            return baseObject.GetType().FullName;
        }

        public static bool IsSecretName(string name) => !string.IsNullOrEmpty(name) && SecretName.IsMatch(name);

        private static bool IsSimpleDeclaredType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            if (SimpleTypeNames.Contains(typeName)) return true;
            // Nullable<T> of a simple type
            if (typeName.StartsWith("System.Nullable`1[[", StringComparison.Ordinal))
            {
                var inner = typeName.Substring("System.Nullable`1[[".Length);
                var comma = inner.IndexOf(',');
                if (comma > 0) inner = inner.Substring(0, comma);
                return SimpleTypeNames.Contains(inner);
            }

            return false;
        }

        internal static JsonNode SerializeSimple(object v)
        {
            switch (v)
            {
                case null:
                    return JsonNull.Instance;
                case string s:
                    return new JsonString(s.Length > MaxStringLength ? s.Substring(0, MaxStringLength) : s);
                case bool _:
                case char _:
                case byte _:
                case sbyte _:
                case short _:
                case ushort _:
                case int _:
                case uint _:
                case long _:
                case ulong _:
                case float _:
                case double _:
                case decimal _:
                case Guid _:
                case DateTime _:
                case DateTimeOffset _:
                case TimeSpan _:
                case Uri _:
                case Enum _:
                    return JsonNode.From(v);
                case Version ver:
                    return new JsonString(ver.ToString());
                case IPAddress ip:
                    return new JsonString(ip.ToString());
                case SwitchParameter sw:
                    return JsonBool.Of(sw.IsPresent);
                default:
                    return null;
            }
        }
    }

    public static class ErrorSerializer
    {
        public static ErrorInfo FromErrorRecord(ErrorRecord record, SecretScrubber scrubber, bool terminating) =>
            ScrubAll(FromErrorRecordCore(record, scrubber, terminating), scrubber);

        public static ErrorInfo FromException(Exception ex, SecretScrubber scrubber, bool terminating) =>
            ScrubAll(FromExceptionCore(ex, scrubber, terminating), scrubber);

        /// <summary>
        /// Scrubs every string field. Note that for errors created with `throw`, PowerShell uses the exception message as
        /// the FullyQualifiedErrorId, so no field can be assumed free of user data.
        /// </summary>
        private static ErrorInfo ScrubAll(ErrorInfo e, SecretScrubber s)
        {
            if (e == null || s == null) return e;
            e.Message = s.Scrub(e.Message);
            e.FullyQualifiedErrorId = s.Scrub(e.FullyQualifiedErrorId);
            e.Category = s.Scrub(e.Category);
            e.Reason = s.Scrub(e.Reason);
            e.TargetName = s.Scrub(e.TargetName);
            e.TargetType = s.Scrub(e.TargetType);
            e.ExceptionMessage = s.Scrub(e.ExceptionMessage);
            e.ScriptStackTrace = s.Scrub(e.ScriptStackTrace);
            e.PositionMessage = s.Scrub(e.PositionMessage);
            e.CommandName = s.Scrub(e.CommandName);
            e.RecommendedAction = s.Scrub(e.RecommendedAction);
            e.StackTrace = s.Scrub(e.StackTrace);
            for (var i = 0; i < e.InnerMessages.Count; i++) e.InnerMessages[i] = s.Scrub(e.InnerMessages[i]);
            return e;
        }

        private static ErrorInfo FromErrorRecordCore(ErrorRecord record, SecretScrubber scrubber, bool terminating)
        {
            scrubber ??= SecretScrubber.None;
            var info = FromExceptionCore(record.Exception, scrubber, terminating);
            info.Message = scrubber.Scrub(record.ErrorDetails?.Message ?? record.Exception?.Message ?? record.ToString());
            info.FullyQualifiedErrorId = record.FullyQualifiedErrorId;
            info.Category = record.CategoryInfo?.Category.ToString();
            info.Reason = record.CategoryInfo?.Reason;
            info.TargetName = scrubber.Scrub(record.CategoryInfo?.TargetName);
            info.TargetType = record.CategoryInfo?.TargetType;
            info.RecommendedAction = scrubber.Scrub(record.ErrorDetails?.RecommendedAction);
            info.ScriptStackTrace = scrubber.Scrub(record.ScriptStackTrace);
            info.PositionMessage = scrubber.Scrub(record.InvocationInfo?.PositionMessage);
            info.CommandName = record.InvocationInfo?.MyCommand?.Name;
            return info;
        }

        private static ErrorInfo FromExceptionCore(Exception ex, SecretScrubber scrubber, bool terminating)
        {
            scrubber ??= SecretScrubber.None;
            if (ex is RuntimeException rte && rte.ErrorRecord != null && rte.ErrorRecord.Exception != ex)
            {
                // ActionPreferenceStopException etc. wrap the original error record.
                var inner = FromErrorRecordCore(rte.ErrorRecord, scrubber, terminating);
                inner.ExceptionTypeChain.Insert(0, ex.GetType().FullName);
                return inner;
            }

            var info = new ErrorInfo
            {
                IsTerminating = terminating,
                Message = scrubber.Scrub(ex?.Message),
                ExceptionType = ex?.GetType().FullName,
                ExceptionMessage = scrubber.Scrub(ex?.Message),
                StackTrace = scrubber.Scrub(ex?.StackTrace)
            };
            var e = ex;
            var guard = 0;
            while (e != null && guard++ < 20)
            {
                info.ExceptionTypeChain.Add(e.GetType().FullName);
                if (e != ex && !string.IsNullOrEmpty(e.Message)) info.InnerMessages.Add(scrubber.Scrub(e.Message));
                e = e.InnerException;
            }

            if (ex is RuntimeException r && r.ErrorRecord != null)
            {
                info.FullyQualifiedErrorId = r.ErrorRecord.FullyQualifiedErrorId;
                info.Category = r.ErrorRecord.CategoryInfo?.Category.ToString();
                info.Reason = r.ErrorRecord.CategoryInfo?.Reason;
                info.CommandName = r.ErrorRecord.InvocationInfo?.MyCommand?.Name;
                info.PositionMessage = scrubber.Scrub(r.ErrorRecord.InvocationInfo?.PositionMessage);
                info.ScriptStackTrace = scrubber.Scrub(r.ErrorRecord.ScriptStackTrace);
            }

            return info;
        }
    }
}
