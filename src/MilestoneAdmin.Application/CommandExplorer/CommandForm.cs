using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using MilestoneAdmin.Domain.Catalog;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.Protocol.Json;
using MilestoneAdmin.PSTools.Commands;
using MilestoneAdmin.PSTools.Execution;

namespace MilestoneAdmin.Application.CommandExplorer
{
    /// <summary>One generated input control for a command parameter.</summary>
    public sealed class FormField
    {
        public CatalogParameter Parameter { get; set; }

        /// <summary>
        /// Object types for a GUID parameter that identifies objects (e.g. -CameraId). The field is then shown as an
        /// object picker and the selected objects' Ids are sent, so nobody has to type a GUID.
        /// </summary>
        public List<string> IdObjectTypes { get; } = new List<string>();

        public bool PicksIds => IdObjectTypes.Count > 0;

        public ControlKind Control => !PicksIds ? Parameter.Control : Parameter.Control == ControlKind.GuidList ? ControlKind.ObjectMultiSelector : ControlKind.ObjectSelector;
        public string Name => Parameter.Name;
        public string Label { get; set; }
        public string Description => Parameter.Description;
        public bool Mandatory { get; set; }
        public string TypeName => Parameter.Type;
        public IReadOnlyList<string> AcceptedValues => Parameter.AcceptedValues;
        public IReadOnlyList<string> ObjectTypes => PicksIds ? (IReadOnlyList<string>)IdObjectTypes : Parameter.ObjectTypes;
        public bool IsSecret => Parameter.IsSecret;

        // Values (set by the UI)
        public string Text { get; set; }
        public bool? Flag { get; set; }
        public DateTime? Date { get; set; }
        public string UserName { get; set; }
        public SecureString Secret { get; set; }
        public List<VmsObject> Objects { get; } = new List<VmsObject>();
        public List<string> SelectedValues { get; } = new List<string>();

        public bool HasValue
        {
            get
            {
                switch (Control)
                {
                    case ControlKind.Checkbox:
                    case ControlKind.Toggle:
                        return Flag.HasValue;
                    case ControlKind.DateTime:
                        return Date.HasValue;
                    case ControlKind.Credential:
                        return !string.IsNullOrEmpty(UserName) && Secret != null && Secret.Length > 0;
                    case ControlKind.SecureText:
                        return Secret != null && Secret.Length > 0;
                    case ControlKind.ObjectSelector:
                    case ControlKind.ObjectMultiSelector:
                        return Objects.Count > 0;
                    case ControlKind.MultiSelect:
                        return SelectedValues.Count > 0;
                    default:
                        return !string.IsNullOrWhiteSpace(Text);
                }
            }
        }

        public bool IsSupported => Control != ControlKind.Unsupported;
    }

    public sealed class CommandForm
    {
        public CatalogCommand Command { get; set; }
        public CatalogParameterSet ParameterSet { get; set; }
        public List<FormField> Fields { get; } = new List<FormField>();

        /// <summary>Why the command cannot be executed from the generated form (null = executable).</summary>
        public string ExecutionBlockedReason { get; set; }
    }

    /// <summary>
    /// Generates forms from command metadata so that commands added in newer MilestonePSTools versions are usable
    /// without redesigning the application, and turns filled forms into typed operations (never script text).
    /// </summary>
    public static class CommandFormBuilder
    {
        public static CommandForm Build(CatalogCommand command, CatalogParameterSet set, bool expertMode)
        {
            var form = new CommandForm { Command = command, ParameterSet = set };
            var setName = set?.Name ?? "__AllParameterSets";
            var parameters = set == null ? command.Parameters : command.Parameters.Where(p => p.InSet(setName));
            foreach (var p in parameters.OrderByDescending(p => p.IsMandatoryIn(setName)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var field = new FormField { Parameter = p, Label = Humanize(p.Name), Mandatory = p.IsMandatoryIn(setName) };
                field.IdObjectTypes.AddRange(IdObjectTypesFor(command, p));
                form.Fields.Add(field);
            }
            form.ExecutionBlockedReason = BlockedReason(command, form, expertMode);
            return form;
        }

        private static string BlockedReason(CatalogCommand c, CommandForm form, bool expertMode)
        {
            switch (c.GuiSuitability)
            {
                case GuiSuitability.ExpertOnly:
                    return "This command is documented here but cannot be run from the application: " + string.Join("; ", c.GuiSuitabilityReasons) + ". Copy the generated PowerShell to use it.";
                case GuiSuitability.AppManaged:
                    return "This functionality is provided by the application itself: " + string.Join("; ", c.GuiSuitabilityReasons);
                case GuiSuitability.NotSuitable:
                    return string.Join("; ", c.GuiSuitabilityReasons);
                case GuiSuitability.CustomWorkflow when !expertMode:
                    return "Use the guided workflow for this operation, or enable Expert Mode.";
            }

            var unsupported = form.Fields.Where(f => f.Mandatory && (!f.IsSupported || f.Control == ControlKind.KeyValue)).Select(f => f.Name).ToList();
            if (unsupported.Count > 0) return "Mandatory parameters cannot be entered in a form: " + string.Join(", ", unsupported) + ". Choose another parameter set.";
            if (c.Requirements.InteractiveSession) return "This command requires an interactive session.";
            return null;
        }

        /// <summary>Id parameter name -> object types the Id refers to. "Id" alone refers to the command's own noun.</summary>
        private static readonly Dictionary<string, string[]> IdParameterTypes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "CameraId", new[] { "Camera" } }, { "CameraIds", new[] { "Camera" } },
            { "MicrophoneIds", new[] { "Microphone" } }, { "SpeakerIds", new[] { "Speaker" } },
            { "RecordingServerId", new[] { "RecordingServer" } }, { "RecorderId", new[] { "RecordingServer" } },
            { "HardwareId", new[] { "Hardware" } }, { "RoleId", new[] { "Role" } },
            { "DeviceId", new[] { "Camera", "Microphone", "Speaker", "Metadata", "InputEvent", "Output" } },
            { "DeviceIds", new[] { "Camera", "Microphone", "Speaker", "Metadata", "InputEvent", "Output" } },
            { "IncludeDeviceId", new[] { "Camera" } }, { "ExcludeDeviceId", new[] { "Camera" } }
        };

        /// <summary>Command noun -> object type, for "-Id" parameters of commands that work on one kind of object.</summary>
        private static readonly Dictionary<string, string> NounTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "VmsCamera", "Camera" }, { "VmsHardware", "Hardware" }, { "VmsRecordingServer", "RecordingServer" }, { "VmsMicrophone", "Microphone" },
            { "VmsSpeaker", "Speaker" }, { "VmsMetadata", "Metadata" }, { "VmsInput", "InputEvent" }, { "VmsOutput", "Output" }, { "VmsRole", "Role" },
            { "VmsView", "View" }, { "VmsViewGroup", "ViewGroup" }, { "VmsClientProfile", "ClientProfile" }, { "VmsFailoverGroup", "FailoverGroup" },
            { "VmsFailoverRecorder", "FailoverRecorder" }, { "VmsRule", "Rule" }, { "UserDefinedEvent", "UserDefinedEvent" }
        };

        public static IEnumerable<string> IdObjectTypesFor(CatalogCommand command, CatalogParameter p)
        {
            if (p.Control != ControlKind.Guid && p.Control != ControlKind.GuidList) return Array.Empty<string>();
            if (IdParameterTypes.TryGetValue(p.Name, out var types)) return types;
            if (string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase) && NounTypes.TryGetValue(command.Noun ?? string.Empty, out var t)) return new[] { t };
            return Array.Empty<string>();
        }

        public static string Humanize(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                if (i > 0 && char.IsUpper(ch) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])))) sb.Append(' ');
                sb.Append(ch);
            }

            return sb.ToString();
        }

        public static List<string> Validate(CommandForm form)
        {
            var errors = new List<string>();
            foreach (var f in form.Fields)
            {
                if (f.Mandatory && !f.HasValue) errors.Add($"{f.Label} is required.");
                if (!f.HasValue) continue;
                if (!f.IsSupported)
                {
                    errors.Add($"{f.Label}: values of type {f.TypeName} cannot be entered in a form.");
                    continue;
                }

                try
                {
                    ConvertField(f, new VmsOperation());
                }
                catch (FormatException ex)
                {
                    errors.Add($"{f.Label}: {ex.Message}");
                }
            }

            return errors;
        }

        public static VmsOperation BuildOperation(CommandForm form, bool whatIf)
        {
            var c = form.Command;
            var op = new VmsOperation
            {
                Title = c.Name,
                Target = string.Join(", ", form.Fields.Where(f => f.Objects.Count > 0).SelectMany(f => f.Objects).Select(o => o.Name).Take(5)),
                TargetType = form.Fields.SelectMany(f => f.ObjectTypes).FirstOrDefault(),
                Risk = c.Risk.Level,
                AllowStateChange = !c.Risk.IsReadOnly || c.Risk.Level == RiskLevel.Session,
                WhatIf = whatIf
            };
            if (c.Risk.Level == RiskLevel.Sensitive) op.Output.AllowSensitive = true;
            var step = new PipelineStep(c.Name);
            foreach (var f in form.Fields.Where(f => f.HasValue))
            {
                var v = ConvertField(f, op);
                if (v != null) step.With(f.Name, v);
            }

            op.Pipeline.Add(step);
            return op;
        }

        private static ParameterValue ConvertField(FormField f, VmsOperation op)
        {
            switch (f.Control)
            {
                case ControlKind.Checkbox:
                    return f.Flag == true ? ParameterValue.Switch() : null;
                case ControlKind.Toggle:
                    return ParameterValue.Boolean(f.Flag == true);
                case ControlKind.Integer:
                    if (!long.TryParse(f.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) throw new FormatException("Enter a whole number.");
                    return ParameterValue.Integer(l);
                case ControlKind.Decimal:
                    if (!double.TryParse(f.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) throw new FormatException("Enter a number (use '.' as decimal separator).");
                    return ParameterValue.Number(d);
                case ControlKind.Guid:
                    if (!Guid.TryParse(f.Text.Trim(), out var g)) throw new FormatException("Enter a GUID.");
                    return ParameterValue.Guid(g);
                case ControlKind.Uri:
                    if (!Uri.TryCreate(f.Text.Trim(), UriKind.Absolute, out var u)) throw new FormatException("Enter an absolute address such as http://server.");
                    return ParameterValue.String(u.ToString());
                case ControlKind.DateTime:
                    return ParameterValue.DateTime(f.Date.Value);
                case ControlKind.TimeSpan:
                    if (!TimeSpan.TryParse(f.Text.Trim(), CultureInfo.InvariantCulture, out var ts)) throw new FormatException("Enter a duration such as 1.00:00:00 (d.hh:mm:ss).");
                    return ParameterValue.String(ts.ToString("c", CultureInfo.InvariantCulture));
                case ControlKind.Dropdown:
                    if (f.AcceptedValues.Count > 0 && !f.AcceptedValues.Contains(f.Text, StringComparer.OrdinalIgnoreCase)) throw new FormatException("Choose one of the listed values.");
                    return ParameterValue.String(f.Text);
                case ControlKind.MultiSelect:
                    return ParameterValue.StringList(f.SelectedValues);
                case ControlKind.TextList:
                    return ParameterValue.StringList(SplitList(f.Text));
                case ControlKind.IntegerList:
                    return ParameterValue.List(SplitList(f.Text).Select(x => long.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? ParameterValue.Integer(v) : throw new FormatException($"'{x}' is not a whole number.")));
                case ControlKind.GuidList:
                    return ParameterValue.List(SplitList(f.Text).Select(x => Guid.TryParse(x, out var v) ? ParameterValue.Guid(v) : throw new FormatException($"'{x}' is not a GUID.")));
                case ControlKind.SecureText:
                    return ParameterValue.Secret(op.AddSecret(f.Secret, f.Label));
                case ControlKind.Credential:
                    return ParameterValue.Credential(f.UserName, op.AddSecret(f.Secret, $"{f.Label} for {f.UserName}"));
                case ControlKind.ObjectSelector when f.PicksIds:
                    return ParameterValue.Guid(f.Objects[0].Id);
                case ControlKind.ObjectMultiSelector when f.PicksIds:
                    return ParameterValue.List(f.Objects.Select(o => ParameterValue.Guid(o.Id)));
                case ControlKind.ObjectSelector:
                    return PsCommands.Ref(f.Objects[0]);
                case ControlKind.ObjectMultiSelector:
                    return PsCommands.Refs(f.Objects);
                case ControlKind.FilePath:
                case ControlKind.Text:
                    return ParameterValue.String(f.Text);
                default:
                    throw new FormatException($"Values of type {f.TypeName} cannot be entered in a form.");
            }
        }

        private static IEnumerable<string> SplitList(string text) =>
            (text ?? string.Empty).Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0);
    }
}
