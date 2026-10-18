using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;

namespace MilestoneAdmin.Worker.Engine
{
    public sealed class PolicyViolationException : Exception
    {
        public PolicyViolationException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Worker-side guard rails. The GUI is responsible for review/confirmation; these checks make sure a GUI defect
    /// cannot turn a read path into a write, run something outside the module, or leak secrets.
    /// </summary>
    public sealed class CommandPolicy
    {
        /// <summary>Verbs that do not change VMS configuration. Mirrors verbRisk ReadOnly/Session in catalog/classification-rules.json.</summary>
        public static readonly HashSet<string> NonMutatingVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Get", "Find", "Test", "Assert", "Select", "Resolve", "Split", "Join", "Trace", "Wait",
            "ConvertFrom", "ConvertTo", "Resize", "Connect", "Disconnect"
        };

        /// <summary>Commands whose output is a secret. Mirrors commandRiskOverrides with risk Sensitive.</summary>
        public static readonly HashSet<string> SensitiveCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Get-VmsHardwarePassword", "Get-VmsToken", "Get-VmsConnectionString", "Export-VmsHardware"
        };

        /// <summary>Parameters that make a command return secrets. Mirrors sensitiveParameters.</summary>
        public static readonly Dictionary<string, string[]> SensitiveParameters = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Get-VmsCameraReport", new[] { "IncludePlainTextPasswords" } }
        };

        /// <summary>Parameters that open dialogs or prompts and therefore cannot be used in the worker.</summary>
        public static readonly HashSet<string> InteractiveParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ShowDialog"
        };

        /// <summary>Common parameters the GUI may set. ErrorAction etc. are controlled by the engine itself.</summary>
        public static readonly HashSet<string> AllowedCommonParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Verbose"
        };

        public static readonly HashSet<string> EngineControlledParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WhatIf", "Confirm", "Debug", "ErrorAction", "WarningAction", "InformationAction", "ErrorVariable", "WarningVariable",
            "InformationVariable", "OutVariable", "OutBuffer", "PipelineVariable"
        };

        private readonly Dictionary<string, CommandInfo> _allowed;

        public CommandPolicy(string moduleName, IEnumerable<CommandInfo> moduleCommands)
        {
            ModuleName = moduleName;
            _allowed = new Dictionary<string, CommandInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in moduleCommands)
            {
                var target = c is AliasInfo a ? a.ResolvedCommand : c;
                if (target == null) continue;
                _allowed[c.Name] = target;
            }
        }

        public string ModuleName { get; }

        public IEnumerable<CommandInfo> Commands => _allowed.Values.Distinct();

        public int Count => _allowed.Count;

        public CommandInfo Resolve(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { ' ', '\t', ';', '|', '&', '$', '(', ')', '{', '}', '`', '\'', '"', '\\', '/' }) >= 0)
                throw new PolicyViolationException($"'{name}' is not a valid command name.");
            if (!_allowed.TryGetValue(name, out var cmd))
                throw new PolicyViolationException($"'{name}' is not a command exported by {ModuleName} and cannot be executed by the application.");
            return cmd;
        }

        public static bool IsMutating(CommandInfo cmd)
        {
            var verb = cmd is CmdletInfo ci ? ci.Verb : cmd is FunctionInfo fi ? fi.Verb : null;
            return verb == null || !NonMutatingVerbs.Contains(verb);
        }

        public static bool RequiresInteractiveSession(CommandInfo cmd) =>
            CommandAttributes(cmd).Any(a => a.GetType().Name == "RequiresInteractiveSessionAttribute");

        /// <summary>
        /// Commands known not to need a VMS connection. Used only when a command carries no
        /// [RequiresVmsConnection] attribute (e.g. functions in other module versions or test doubles).
        /// </summary>
        public static readonly HashSet<string> NoConnectionCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Connect-Vms", "Connect-ManagementServer", "Disconnect-Vms", "Disconnect-ManagementServer", "Test-VmsConnection",
            "Get-VmsModuleConfig", "Set-VmsModuleConfig", "Get-VmsConnectionProfile", "Remove-VmsConnectionProfile",
            "ConvertFrom-GisPoint", "ConvertTo-GisPoint", "Split-VmsDeviceGroupPath", "Join-VmsDeviceGroupPath", "Resize-Image"
        };

        /// <summary>
        /// Whether the command needs an established VMS connection. MilestonePSTools commands marked
        /// [RequiresVmsConnection()] try to auto-connect when not connected, using the saved "default" profile or an
        /// interactive login dialog (src/MilestonePSTools/Attributes.cs, Connect-Vms.ps1). The engine must prevent that,
        /// so it asks this before running anything.
        /// </summary>
        public static bool RequiresConnection(CommandInfo cmd)
        {
            if (cmd.Name.StartsWith("Connect-", StringComparison.OrdinalIgnoreCase) || cmd.Name.StartsWith("Disconnect-", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var a in CommandAttributes(cmd))
            {
                if (a.GetType().Name != "RequiresVmsConnectionAttribute") continue;
                var p = a.GetType().GetProperty("ConnectionRequired");
                if (p != null && p.GetValue(a) is bool b) return b;
                return true;
            }

            return !NoConnectionCommands.Contains(cmd.Name);
        }

        public static bool SupportsWhatIf(CommandInfo cmd) => cmd.Parameters != null && cmd.Parameters.ContainsKey("WhatIf");

        public static IEnumerable<object> CommandAttributes(CommandInfo cmd)
        {
            try
            {
                if (cmd is CmdletInfo c) return c.ImplementingType.GetCustomAttributes(true);
                if (cmd is FunctionInfo f) return f.ScriptBlock.Attributes;
            }
            catch (Exception)
            {
                // Attribute inspection is best effort.
            }

            return Enumerable.Empty<object>();
        }
    }
}
