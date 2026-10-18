using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.Domain.Scripting
{
    /// <summary>Safe rendering of values as PowerShell literals for *display and export only*. Never executed by the app.</summary>
    public static class PowerShellLiteral
    {
        // PowerShell treats all of these as single-quote characters (see System.Management.Automation.Language.SpecialChars).
        private static readonly char[] SingleQuotes = { '\'', (char)0x2018, (char)0x2019, (char)0x201A, (char)0x201B };

        public static string Quote(string value)
        {
            if (value == null) return "$null";
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('\'');
            foreach (var c in value)
            {
                if (Array.IndexOf(SingleQuotes, c) >= 0) sb.Append(c);
                sb.Append(c);
            }

            sb.Append('\'');
            return sb.ToString();
        }

        public static string FromJson(JsonNode node)
        {
            switch (node?.Kind ?? JsonKind.Null)
            {
                case JsonKind.Null: return "$null";
                case JsonKind.Bool: return node.AsBool() == true ? "$true" : "$false";
                case JsonKind.Number: return ((JsonNumber)node).Raw;
                case JsonKind.String: return Quote(node.AsString());
                default: return Quote(node.ToJson());
            }
        }

        /// <summary>A variable name derived from a label, restricted to [A-Za-z0-9].</summary>
        public static string VariableName(string label, int index)
        {
            var sb = new StringBuilder();
            foreach (var c in label ?? "object")
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) sb.Append(c);
            }

            if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, "object");
            sb[0] = char.ToLowerInvariant(sb[0]);
            return "$" + sb + index.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Describes an object handle so the generated script can re-acquire it.</summary>
    public sealed class ScriptObjectInfo
    {
        public string ObjectType { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public sealed class ScriptInvocation
    {
        public string Title { get; set; }
        public List<PipelineStep> Pipeline { get; } = new List<PipelineStep>();
        public bool WhatIf { get; set; }

        /// <summary>Description per secret id, e.g. "Password for hardware 10.1.20.55". The value is never available here.</summary>
        public Dictionary<string, string> SecretDescriptions { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class ScriptConnectionInfo
    {
        public string ServerAddress { get; set; }
        public bool UsesCredential { get; set; }
        public bool BasicUser { get; set; }
        public bool SecureOnly { get; set; }
        public bool IncludeChildSites { get; set; }
    }

    /// <summary>
    /// Generates the PowerShell equivalent of what the GUI executes (Expert Mode). Secrets are never written: passwords
    /// become Read-Host -AsSecureString / Get-Credential prompts.
    /// </summary>
    public sealed class ScriptGenerator
    {
        /// <summary>Producer used to re-acquire an object by Id in a script. Mirrors the worker's ReferenceResolver.</summary>
        public static readonly Dictionary<string, (string Command, string Parameter)> Producers =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                { "RecordingServer", ("Get-VmsRecordingServer", "Id") },
                { "Hardware", ("Get-VmsHardware", "Id") },
                { "Camera", ("Get-VmsCamera", "Id") },
                { "Microphone", ("Get-VmsMicrophone", "Id") },
                { "Speaker", ("Get-VmsSpeaker", "Id") },
                { "Metadata", ("Get-VmsMetadata", "Id") },
                { "InputEvent", ("Get-VmsInput", "Id") },
                { "Output", ("Get-VmsOutput", "Id") },
                { "Role", ("Get-VmsRole", "Id") },
                { "View", ("Get-VmsView", "Id") },
                { "ViewGroup", ("Get-VmsViewGroup", "Id") },
                { "ClientProfile", ("Get-VmsClientProfile", "Id") },
                { "FailoverGroup", ("Get-VmsFailoverGroup", "Id") },
                { "Rule", ("Get-VmsRule", "Id") },
                { "Storage", ("Get-VmsStorage", "ItemPath") }
            };

        private readonly Func<string, ScriptObjectInfo> _resolveHandle;

        public ScriptGenerator(Func<string, ScriptObjectInfo> resolveHandle)
        {
            _resolveHandle = resolveHandle ?? (_ => null);
        }

        public string Generate(IEnumerable<ScriptInvocation> invocations, ScriptConnectionInfo connection, string moduleVersion, DateTime generatedAt)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#Requires -Version 5.1");
            sb.AppendLine("#Requires -Modules MilestonePSTools");
            sb.AppendLine("<#");
            sb.AppendLine("    Generated by XProtect Admin Assistant on " + generatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(moduleVersion)) sb.AppendLine("    Validated against MilestonePSTools " + moduleVersion);
            sb.AppendLine("    Review before running. No passwords are stored in this script; you will be prompted for them.");
            sb.AppendLine("    Commands that support it will ask for confirmation; add -WhatIf to preview first.");
            sb.AppendLine("#>");
            sb.AppendLine();
            if (connection != null && !string.IsNullOrEmpty(connection.ServerAddress))
            {
                sb.Append("Connect-Vms -ServerAddress ").Append(PowerShellLiteral.Quote(connection.ServerAddress));
                if (connection.UsesCredential) sb.Append(" -Credential (Get-Credential -Message 'XProtect login')");
                if (connection.BasicUser) sb.Append(" -BasicUser");
                if (connection.SecureOnly) sb.Append(" -SecureOnly");
                if (connection.IncludeChildSites) sb.Append(" -IncludeChildSites");
                sb.AppendLine();
                sb.AppendLine();
            }

            var state = new State();
            foreach (var inv in invocations)
            {
                if (!string.IsNullOrEmpty(inv.Title)) sb.Append("# ").AppendLine(SafeComment(inv.Title));
                var preamble = new StringBuilder();
                var line = RenderPipeline(inv, state, preamble);
                sb.Append(preamble);
                sb.AppendLine(line);
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        public string GenerateFragment(ScriptInvocation invocation)
        {
            var state = new State();
            var preamble = new StringBuilder();
            var line = RenderPipeline(invocation, state, preamble);
            return preamble + line;
        }

        private sealed class State
        {
            public int Counter;
            public readonly Dictionary<string, string> HandleVariables = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> SecretVariables = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private string RenderPipeline(ScriptInvocation inv, State state, StringBuilder preamble)
        {
            var parts = new List<string>();
            for (var i = 0; i < inv.Pipeline.Count; i++)
            {
                var step = inv.Pipeline[i];
                var sb = new StringBuilder(step.Command);
                foreach (var kv in step.Parameters)
                {
                    var v = kv.Value;
                    if (v.Kind == ParameterValueKind.Switch)
                    {
                        sb.Append(" -").Append(kv.Key);
                        if (!v.SwitchValue) sb.Append(":$false");
                        continue;
                    }

                    sb.Append(" -").Append(kv.Key).Append(' ').Append(Render(v, inv, state, preamble, kv.Key));
                }

                if (i == inv.Pipeline.Count - 1 && inv.WhatIf) sb.Append(" -WhatIf");
                parts.Add(sb.ToString());
            }

            return string.Join(" |" + Environment.NewLine + "    ", parts);
        }

        private string Render(ParameterValue v, ScriptInvocation inv, State state, StringBuilder preamble, string parameterName)
        {
            switch (v.Kind)
            {
                case ParameterValueKind.Literal:
                    return PowerShellLiteral.FromJson(v.Literal);
                case ParameterValueKind.List:
                {
                    var handles = v.Items.Where(x => x.Kind == ParameterValueKind.Handle || x.Kind == ParameterValueKind.Reference).ToList();
                    if (handles.Count == v.Items.Count && handles.Count > 1) return ObjectListVariable(v.Items, state, preamble, parameterName);
                    var items = v.Items.Select(x => Render(x, inv, state, preamble, parameterName)).ToList();
                    return items.Count == 0 ? "@()" : string.Join(", ", items);
                }

                case ParameterValueKind.Secret:
                    return SecretVariable(v.SecretId, inv, state, preamble, parameterName);
                case ParameterValueKind.Credential:
                {
                    var key = "cred:" + v.UserName + ":" + v.SecretId;
                    if (!state.SecretVariables.TryGetValue(key, out var name))
                    {
                        name = "$credential" + (++state.Counter).ToString(CultureInfo.InvariantCulture);
                        inv.SecretDescriptions.TryGetValue(v.SecretId, out var desc);
                        preamble.Append(name).Append(" = Get-Credential -UserName ").Append(PowerShellLiteral.Quote(v.UserName))
                            .Append(" -Message ").AppendLine(PowerShellLiteral.Quote(desc ?? "Credential for -" + parameterName));
                        state.SecretVariables[key] = name;
                    }

                    return name;
                }

                case ParameterValueKind.Handle:
                case ParameterValueKind.Reference:
                    return ObjectVariable(v, state, preamble);
                case ParameterValueKind.Table:
                {
                    var entries = v.Table.Select(kv => PowerShellLiteral.Quote(kv.Key) + " = " + PowerShellLiteral.FromJson(kv.Value));
                    return "@{ " + string.Join("; ", entries) + " }";
                }

                default:
                    return "$null";
            }
        }

        private string SecretVariable(string secretId, ScriptInvocation inv, State state, StringBuilder preamble, string parameterName)
        {
            if (state.SecretVariables.TryGetValue(secretId, out var name)) return name;
            name = "$secret" + (++state.Counter).ToString(CultureInfo.InvariantCulture);
            inv.SecretDescriptions.TryGetValue(secretId, out var desc);
            preamble.Append(name).Append(" = Read-Host -AsSecureString -Prompt ").AppendLine(PowerShellLiteral.Quote(desc ?? "Value for -" + parameterName));
            state.SecretVariables[secretId] = name;
            return name;
        }

        private ScriptObjectInfo Describe(ParameterValue v) =>
            v.Kind == ParameterValueKind.Reference
                ? new ScriptObjectInfo { ObjectType = v.ObjectType, Id = v.ObjectId }
                : _resolveHandle(v.HandleId);

        private string ObjectVariable(ParameterValue v, State state, StringBuilder preamble)
        {
            var key = v.Kind == ParameterValueKind.Handle ? "h:" + v.HandleId : "r:" + v.ObjectType + ":" + v.ObjectId;
            if (state.HandleVariables.TryGetValue(key, out var existing)) return existing;
            var info = Describe(v);
            var name = PowerShellLiteral.VariableName(info?.ObjectType ?? "object", ++state.Counter);
            if (info != null && info.Id != null && Producers.TryGetValue(info.ObjectType ?? string.Empty, out var p))
            {
                preamble.Append(name).Append(" = ").Append(p.Command).Append(" -").Append(p.Parameter).Append(' ').Append(PowerShellLiteral.Quote(info.Id));
                if (!string.IsNullOrEmpty(info.Name)) preamble.Append("  # ").Append(SafeComment(info.Name));
                preamble.AppendLine();
            }
            else
            {
                preamble.Append(name).Append(" = $null  # TODO: obtain ").Append(SafeComment(info?.ObjectType ?? "object"));
                if (!string.IsNullOrEmpty(info?.Name)) preamble.Append(" '").Append(SafeComment(info.Name)).Append('\'');
                preamble.AppendLine(" - it cannot be re-acquired by Id in a script");
            }

            state.HandleVariables[key] = name;
            return name;
        }

        private string ObjectListVariable(IReadOnlyList<ParameterValue> items, State state, StringBuilder preamble, string parameterName)
        {
            var infos = items.Select(Describe).ToList();
            var type = infos.Select(i => i?.ObjectType).Distinct().Count() == 1 ? infos[0]?.ObjectType : null;
            if (type == null || infos.Any(i => i?.Id == null) || !Producers.TryGetValue(type, out var p))
                return string.Join(", ", items.Select(i => ObjectVariable(i, state, preamble)));

            var name = PowerShellLiteral.VariableName(type + "List", ++state.Counter);
            preamble.Append(name).AppendLine(" = @(");
            foreach (var info in infos)
            {
                preamble.Append("    ").Append(PowerShellLiteral.Quote(info.Id));
                if (!string.IsNullOrEmpty(info.Name)) preamble.Append("  # ").Append(SafeComment(info.Name));
                preamble.AppendLine();
            }

            preamble.Append(") | ForEach-Object { ").Append(p.Command).Append(" -").Append(p.Parameter).AppendLine(" $_ }");
            return name;
        }

        /// <summary>Object names go into comments; strip characters that could end the comment line or open a block comment.</summary>
        private static string SafeComment(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                if (c == '\r' || c == '\n' || c == (char)0x2028 || c == (char)0x2029 || c == (char)0x85) sb.Append(' ');
                else sb.Append(c);
            }

            return sb.ToString().Replace("#>", "# >").Replace("<#", "< #");
        }
    }
}
