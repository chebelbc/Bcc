using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management.Automation;
using System.Security;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.Worker.Engine
{
    /// <summary>
    /// Turns protocol parameter values into .NET objects of the parameter's declared type. This is the
    /// PowerShell-injection boundary: values are converted with LanguagePrimitives and bound with AddParameter, so a
    /// string such as "'; Remove-VmsHardware ..." is only ever a string. Script blocks are rejected outright.
    /// </summary>
    public sealed class ParameterBinder
    {
        public const int MaxStringLength = 32 * 1024;
        public const int MaxListItems = 100000;

        private readonly IReadOnlyDictionary<string, string> _secrets;
        private readonly HandleTable _handles;
        private readonly Func<string, string, object> _resolveReference;

        public ParameterBinder(IReadOnlyDictionary<string, string> secrets, HandleTable handles, Func<string, string, object> resolveReference)
        {
            _secrets = secrets ?? new Dictionary<string, string>();
            _handles = handles;
            _resolveReference = resolveReference;
        }

        public List<KeyValuePair<string, object>> Bind(CommandInfo command, PipelineStep step)
        {
            var bound = new List<KeyValuePair<string, object>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in step.Parameters)
            {
                var meta = FindParameter(command, kv.Key);
                if (!seen.Add(meta.Name)) throw new PolicyViolationException($"Parameter '{meta.Name}' was specified more than once for {command.Name}.");
                if (CommandPolicy.EngineControlledParameters.Contains(meta.Name))
                    throw new PolicyViolationException($"Parameter '-{meta.Name}' is controlled by the application and cannot be set directly.");
                if (CommandPolicy.InteractiveParameters.Contains(meta.Name))
                    throw new PolicyViolationException($"Parameter '-{meta.Name}' opens an interactive dialog and is not supported.");
                RejectScriptBlock(command, meta);
                bound.Add(new KeyValuePair<string, object>(meta.Name, Convert(command, meta, kv.Value)));
            }

            return bound;
        }

        private static ParameterMetadata FindParameter(CommandInfo command, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new PolicyViolationException("Parameter name is required.");
            var parameters = command.Parameters ?? new Dictionary<string, ParameterMetadata>();
            foreach (var p in parameters.Values)
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            }

            // Aliases are accepted because the catalog lists them, but abbreviations ("-Na" for "-Name") are not.
            foreach (var p in parameters.Values)
            {
                if (p.Aliases != null && p.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase))) return p;
            }

            throw new PolicyViolationException($"{command.Name} has no parameter named '{name}'.");
        }

        private static void RejectScriptBlock(CommandInfo command, ParameterMetadata meta)
        {
            var t = meta.ParameterType;
            var element = t.IsArray ? t.GetElementType() : t;
            if (typeof(ScriptBlock).IsAssignableFrom(element))
                throw new PolicyViolationException($"Parameter '-{meta.Name}' of {command.Name} accepts a script block. Script blocks are never accepted from the user interface.");
        }

        private object Convert(CommandInfo command, ParameterMetadata meta, ParameterValue value)
        {
            var target = meta.ParameterType;
            try
            {
                return ConvertTo(value, target, meta.Name);
            }
            catch (PolicyViolationException)
            {
                throw;
            }
            catch (Exception ex) when (ex is PSInvalidCastException || ex is InvalidCastException || ex is FormatException || ex is ArgumentException)
            {
                throw new PolicyViolationException($"Value for '-{meta.Name}' of {command.Name} cannot be converted to {Describe(target)}: {ex.Message}");
            }
        }

        private object ConvertTo(ParameterValue value, Type target, string parameterName)
        {
            var isArray = target.IsArray;
            var element = isArray ? target.GetElementType() : target;
            switch (value.Kind)
            {
                case ParameterValueKind.Switch:
                    if (target == typeof(SwitchParameter)) return new SwitchParameter(value.SwitchValue);
                    if (target == typeof(bool)) return value.SwitchValue;
                    throw new PolicyViolationException($"'-{parameterName}' is not a switch parameter.");

                case ParameterValueKind.Literal:
                {
                    var raw = LiteralToClr(value.Literal);
                    if (raw is string s && s.Length > MaxStringLength) throw new PolicyViolationException($"Value for '-{parameterName}' is too long.");
                    if (raw == null) return null;
                    if (target == typeof(SwitchParameter)) return new SwitchParameter(LanguagePrimitives.ConvertTo<bool>(raw));
                    if (target == typeof(SecureString)) throw new PolicyViolationException($"'-{parameterName}' requires a secret; plain values are not accepted.");
                    if (target == typeof(PSCredential)) throw new PolicyViolationException($"'-{parameterName}' requires a credential; plain values are not accepted.");
                    return LanguagePrimitives.ConvertTo(raw, target, CultureInfo.InvariantCulture);
                }

                case ParameterValueKind.List:
                {
                    if (value.Items.Count > MaxListItems) throw new PolicyViolationException($"Too many values for '-{parameterName}'.");
                    var items = value.Items.Select(i => ConvertTo(i, element, parameterName)).ToArray();
                    if (!isArray)
                    {
                        if (target == typeof(object) || target == typeof(PSObject)) return items;
                        if (items.Length == 1) return items[0];
                        throw new PolicyViolationException($"'-{parameterName}' accepts a single value.");
                    }

                    var arr = Array.CreateInstance(element, items.Length);
                    for (var i = 0; i < items.Length; i++)
                        arr.SetValue(items[i] == null ? null : LanguagePrimitives.ConvertTo(items[i], element, CultureInfo.InvariantCulture), i);
                    return arr;
                }

                case ParameterValueKind.Secret:
                {
                    var secret = GetSecret(value.SecretId);
                    if (element == typeof(string)) return isArray ? (object)new[] { secret } : secret;
                    var ss = ToSecureString(secret);
                    return isArray ? (object)new[] { ss } : ss;
                }

                case ParameterValueKind.Credential:
                {
                    var cred = new PSCredential(value.UserName, ToSecureString(GetSecret(value.SecretId)));
                    if (element != typeof(PSCredential) && element != typeof(object) && element != typeof(PSObject))
                        throw new PolicyViolationException($"'-{parameterName}' does not accept a credential.");
                    return isArray && element == typeof(PSCredential) ? (object)new[] { cred } : cred;
                }

                case ParameterValueKind.Handle:
                {
                    if (_handles == null || !_handles.TryGet(value.HandleId, out var obj))
                        throw new PolicyViolationException($"The object referenced by handle '{value.HandleId}' is no longer available in this session. Refresh the data and try again.");
                    return isArray ? WrapArray(obj, element) : obj;
                }

                case ParameterValueKind.Reference:
                {
                    if (_resolveReference == null) throw new PolicyViolationException("Object references are not available.");
                    var obj = _resolveReference(value.ObjectType, value.ObjectId);
                    return isArray ? WrapArray(obj, element) : obj;
                }

                case ParameterValueKind.Table:
                {
                    if (!typeof(IDictionary).IsAssignableFrom(target) && target != typeof(object))
                        throw new PolicyViolationException($"'-{parameterName}' does not accept a table of values.");
                    var h = new Hashtable(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in value.Table) h[kv.Key] = LiteralToClr(kv.Value);
                    return h;
                }

                default:
                    throw new PolicyViolationException("Unsupported parameter value.");
            }
        }

        private static object WrapArray(object obj, Type element)
        {
            // Let PowerShell's binder perform the element conversion (e.g. PSObject wrapping a Camera -> IConfigurationItem[]).
            return new[] { obj };
        }

        private string GetSecret(string id)
        {
            if (!_secrets.TryGetValue(id, out var s)) throw new PolicyViolationException($"Secret '{id}' was referenced but not supplied.");
            return s;
        }

        private static SecureString ToSecureString(string s)
        {
            var ss = new SecureString();
            foreach (var c in s) ss.AppendChar(c);
            ss.MakeReadOnly();
            return ss;
        }

        internal static object LiteralToClr(JsonNode node)
        {
            switch (node?.Kind ?? JsonKind.Null)
            {
                case JsonKind.Null: return null;
                case JsonKind.Bool: return node.AsBool();
                case JsonKind.String: return node.AsString();
                case JsonKind.Number:
                {
                    var raw = ((JsonNumber)node).Raw;
                    if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                        return l >= int.MinValue && l <= int.MaxValue ? (object)(int)l : l;
                    return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
                }

                default:
                    throw new PolicyViolationException("Only literal values are accepted here.");
            }
        }

        private static string Describe(Type t) => t.IsArray ? t.GetElementType().Name + "[]" : t.Name;
    }
}
