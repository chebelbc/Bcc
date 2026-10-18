using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Application.CommandExplorer;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.Application.Execution;
using MilestoneAdmin.Application.Session;
using MilestoneAdmin.Domain.Catalog;
using MilestoneAdmin.Domain.Changes;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.PSTools.Commands;
using MilestoneAdmin.PSTools.Execution;

namespace MilestoneAdmin.Application.Actions
{
    public enum ActionSelection
    {
        /// <summary>Creates something; no object is selected (e.g. New-VmsBasicUser).</summary>
        None,
        Single,
        Multiple
    }

    /// <summary>A visible input of a quick action: one parameter of the action's MilestonePSTools command.</summary>
    public sealed class QuickActionField
    {
        public QuickActionField(string parameter, string label = null, bool? required = null, string currentFrom = null)
        {
            Parameter = parameter;
            Label = label;
            Required = required;
            CurrentFrom = currentFrom;
        }

        public string Parameter { get; }
        public string Label { get; }
        public bool? Required { get; }

        /// <summary>Property of the selected object that holds the current value (prefilled; unchanged values are not sent).</summary>
        public string CurrentFrom { get; }
    }

    /// <summary>
    /// A guided change that runs one documented MilestonePSTools command: the selected objects are bound to
    /// <see cref="SelectionParameter"/>, a few of the command's own parameters are shown as typed inputs, and fixed values
    /// are added. Inputs come from the command catalog, so names and types cannot drift from the installed module, and
    /// everything goes through the normal review → (WhatIf) → apply flow.
    /// </summary>
    public sealed class QuickAction
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }

        /// <summary>The object type the action works on (the inventory tab it belongs to).</summary>
        public string ObjectType { get; set; }

        public string Command { get; set; }
        public string ParameterSet { get; set; }
        public string SelectionParameter { get; set; }

        /// <summary>
        /// Bind this property of the selected objects (as strings) instead of the objects themselves, e.g. the SIDs of role
        /// members. Keeps generated scripts reproducible for objects that have no Id-based producer.
        /// </summary>
        public string SelectionValueProperty { get; set; }

        public ActionSelection Selection { get; set; }

        /// <summary>False: one invocation receives all selected objects (e.g. Remove-VmsRoleMember -User).</summary>
        public bool OneOperationPerObject { get; set; } = true;

        public List<QuickActionField> Fields { get; } = new List<QuickActionField>();

        /// <summary>Parameter values that are part of the action itself (e.g. -Enabled $false for "Disable").</summary>
        public Dictionary<string, object> Fixed { get; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Parameter bound to the context object (e.g. the role whose members are listed).</summary>
        public string ParentParameter { get; set; }

        /// <summary>For actions without selection: the field whose value names the new object.</summary>
        public string TargetNameField { get; set; }

        public bool StrongConfirmation { get; set; }
        public List<string> Warnings { get; } = new List<string>();

        public IEnumerable<string> AllParameters =>
            Fields.Select(f => f.Parameter).Concat(Fixed.Keys).Concat(new[] { SelectionParameter, ParentParameter }).Where(p => !string.IsNullOrEmpty(p));
    }

    public sealed class QuickActionForm
    {
        public QuickAction Action { get; set; }
        public CatalogCommand Command { get; set; }
        public CommandForm Form { get; set; }
        public IReadOnlyList<VmsObject> Selection { get; set; }
        public VmsObject Parent { get; set; }
        public Dictionary<string, string> Prefilled { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class QuickActionTarget
    {
        public string Name { get; set; }
        public VmsObject Object { get; set; }
        public IReadOnlyList<VmsObject> Objects { get; set; }

        /// <summary>The reviewed change for this target; targets without changes are skipped when applying.</summary>
        public PlannedChange Change { get; set; }

        public override string ToString() => Name;
    }

    public sealed class QuickActionPlan
    {
        public ChangePlan Plan { get; set; }
        public List<QuickActionTarget> Targets { get; } = new List<QuickActionTarget>();
        public Func<QuickActionTarget, VmsOperation> Build { get; set; }
    }

    public sealed class QuickActionService
    {
        private readonly SessionContext _session;
        private readonly OperationRunner _runner;
        private readonly BulkOperationRunner _bulk;
        private readonly ScriptService _scripts;

        public QuickActionService(SessionContext session, OperationRunner runner, BulkOperationRunner bulk, ScriptService scripts)
        {
            _session = session;
            _runner = runner;
            _bulk = bulk;
            _scripts = scripts;
        }

        public static object CurrentValue(VmsObject o, string property)
        {
            if (o == null || property == null) return null;
            if (o is GenericItem g) return g[property];
            return o.GetType().GetProperty(property)?.GetValue(o);
        }

        /// <summary>Why the action cannot be used with the installed module (null = available).</summary>
        public static string UnavailableReason(QuickAction a, CommandCatalog catalog)
        {
            if (catalog == null) return "Connect first; the command catalog has not been loaded.";
            var cmd = catalog.Find(a.Command);
            if (cmd == null) return $"{a.Command} is not available in the installed MilestonePSTools version.";
            var missing = a.AllParameters.Where(p => cmd.FindParameter(p) == null).ToList();
            if (missing.Count > 0) return $"The installed MilestonePSTools version of {a.Command} has no -{string.Join(", -", missing)} parameter.";
            if (FindSet(a, cmd) == null) return $"No parameter set of {a.Command} accepts this combination of parameters.";
            return null;
        }

        public static string SelectionProblem(QuickAction a, int count)
        {
            switch (a.Selection)
            {
                case ActionSelection.Single when count != 1: return "Select exactly one item.";
                case ActionSelection.Multiple when count == 0: return "Select one or more items.";
                default: return null;
            }
        }

        private static CatalogParameterSet FindSet(QuickAction a, CatalogCommand cmd)
        {
            var needed = a.AllParameters.Select(p => cmd.FindParameter(p)?.Name).ToList();
            if (needed.Any(n => n == null)) return null;
            bool Fits(CatalogParameterSet s) => s.IsAllSets || needed.All(n => s.Parameters.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)));
            if (a.ParameterSet != null) return cmd.ParameterSets.FirstOrDefault(s => s.Name == a.ParameterSet && Fits(s));
            return cmd.ParameterSets.Where(Fits).OrderByDescending(s => s.IsDefault).FirstOrDefault()
                   ?? (cmd.ParameterSets.Count == 0 ? new CatalogParameterSet { Name = "__AllParameterSets" } : null);
        }

        public QuickActionForm CreateForm(QuickAction a, IReadOnlyList<VmsObject> selection, VmsObject parent = null)
        {
            var reason = UnavailableReason(a, _session.Catalog);
            if (reason != null) throw new InvalidOperationException(reason);
            var cmd = _session.Catalog.Find(a.Command);
            var set = FindSet(a, cmd);
            var full = CommandFormBuilder.Build(cmd, set, expertMode: true);
            var form = new QuickActionForm
            {
                Action = a, Command = cmd, Selection = selection ?? Array.Empty<VmsObject>(), Parent = parent,
                Form = new CommandForm { Command = cmd, ParameterSet = set }
            };
            foreach (var f in a.Fields)
            {
                var field = full.Fields.FirstOrDefault(x => string.Equals(x.Name, f.Parameter, StringComparison.OrdinalIgnoreCase))
                            ?? new FormField { Parameter = cmd.FindParameter(f.Parameter), Label = CommandFormBuilder.Humanize(f.Parameter) };
                if (f.Label != null) field.Label = f.Label;
                if (f.Required.HasValue) field.Mandatory = f.Required.Value;
                if (form.Selection.Count == 1 && f.CurrentFrom != null)
                {
                    var current = CurrentValue(form.Selection[0], f.CurrentFrom);
                    if (current is bool b) field.Flag = b;
                    else if (current != null) field.Text = Convert.ToString(current, CultureInfo.InvariantCulture);
                    form.Prefilled[field.Name] = current is bool bb ? (bb ? "True" : "False") : current == null ? null : Convert.ToString(current, CultureInfo.InvariantCulture);
                }

                form.Form.Fields.Add(field);
            }

            return form;
        }

        public List<string> Validate(QuickActionForm f)
        {
            var errors = new List<string>();
            var sel = SelectionProblem(f.Action, f.Selection.Count);
            if (sel != null) errors.Add(sel);
            errors.AddRange(CommandFormBuilder.Validate(f.Form));
            return errors;
        }

        private static string FieldValueText(FormField f)
        {
            if (f.Control == ControlKind.Checkbox || f.Control == ControlKind.Toggle) return f.Flag.HasValue ? (f.Flag.Value ? "True" : "False") : null;
            return f.Text;
        }

        private static bool Unchanged(QuickActionForm form, FormField f) =>
            form.Prefilled.TryGetValue(f.Name, out var before) && string.Equals((before ?? string.Empty).Trim(), (FieldValueText(f) ?? string.Empty).Trim(), StringComparison.Ordinal);

        private static string Display(FormField f)
        {
            if (f.IsSecret || f.Control == ControlKind.SecureText || f.Control == ControlKind.Credential) return "(new value, hidden)";
            if (f.Objects.Count > 0) return string.Join(", ", f.Objects.Select(o => o.Name));
            if (f.SelectedValues.Count > 0) return string.Join(", ", f.SelectedValues);
            if (f.Date.HasValue) return f.Date.Value.ToString("u", CultureInfo.InvariantCulture);
            return FieldValueText(f);
        }

        private static string OperationVerb(RiskLevel risk) => risk == RiskLevel.Delete ? "Remove" : risk == RiskLevel.Create ? "Add" : "Update";

        public QuickActionPlan BuildPlan(QuickActionForm form)
        {
            var a = form.Action;
            var cmd = form.Command;
            var changed = form.Form.Fields.Where(f => f.HasValue && !Unchanged(form, f)).ToList();
            var result = new QuickActionPlan();

            if (a.Selection == ActionSelection.None)
            {
                var nameField = form.Form.Fields.FirstOrDefault(f => string.Equals(f.Name, a.TargetNameField, StringComparison.OrdinalIgnoreCase));
                result.Targets.Add(new QuickActionTarget { Name = nameField?.Text?.Trim() ?? a.Title, Objects = Array.Empty<VmsObject>() });
            }
            else if (a.OneOperationPerObject)
            {
                foreach (var o in form.Selection) result.Targets.Add(new QuickActionTarget { Name = o.Name, Object = o, Objects = new[] { o } });
            }
            else
            {
                result.Targets.Add(new QuickActionTarget { Name = string.Join(", ", form.Selection.Select(o => o.Name).Take(5)) + (form.Selection.Count > 5 ? ", ..." : string.Empty), Objects = form.Selection });
            }

            result.Build = t =>
            {
                // Build from a form that holds only the changed inputs, then add selection, parent and fixed values.
                var sub = new CommandForm { Command = cmd, ParameterSet = form.Form.ParameterSet };
                sub.Fields.AddRange(changed);
                var op = CommandFormBuilder.BuildOperation(sub, whatIf: false);
                var step = op.Pipeline[0];
                if (a.SelectionParameter != null && t.Objects.Count > 0)
                {
                    var p = cmd.FindParameter(a.SelectionParameter);
                    var isArray = p.Type != null && p.Type.EndsWith("[]", StringComparison.Ordinal);
                    var value = a.SelectionValueProperty != null
                        ? ParameterValue.StringList(t.Objects.Select(o => Convert.ToString(CurrentValue(o, a.SelectionValueProperty), CultureInfo.InvariantCulture)))
                        : isArray || t.Objects.Count > 1 ? PsCommands.Refs(t.Objects) : PsCommands.Ref(t.Objects[0]);
                    step.Parameters.Insert(0, new KeyValuePair<string, ParameterValue>(p.Name, value));
                }

                if (a.ParentParameter != null && form.Parent != null)
                    step.Parameters.Insert(0, new KeyValuePair<string, ParameterValue>(cmd.FindParameter(a.ParentParameter).Name, PsCommands.Ref(form.Parent)));

                foreach (var kv in a.Fixed)
                {
                    var p = cmd.FindParameter(kv.Key);
                    step.Parameters.Add(new KeyValuePair<string, ParameterValue>(p.Name,
                        kv.Value is bool sw && p.Type == "SwitchParameter" ? ParameterValue.Switch(sw) : PsCommands.Literal(kv.Value, kv.Value?.GetType() ?? typeof(string))));
                }

                op.Title = a.Title;
                op.Target = t.Name;
                op.TargetType = a.ObjectType;
                op.Output.RegisterHandles = cmd.Risk.Level != RiskLevel.Delete;
                return op;
            };

            var plan = new ChangePlan
            {
                Title = a.Title,
                OperationId = "action." + a.Id,
                Risk = cmd.Risk.Level,
                WhatIfAvailable = cmd.SupportsShouldProcess
            };
            foreach (var t in result.Targets)
            {
                var item = new PlannedChange { TargetType = a.ObjectType, TargetName = t.Name, TargetHandle = t.Object?.Handle, TargetId = t.Object?.Id ?? Guid.Empty, Operation = OperationVerb(cmd.Risk.Level) };
                foreach (var f in changed)
                {
                    var spec = a.Fields.First(x => string.Equals(x.Parameter, f.Name, StringComparison.OrdinalIgnoreCase));
                    var current = t.Object != null && spec.CurrentFrom != null ? CurrentValue(t.Object, spec.CurrentFrom) : null;
                    item.Properties.Add(new PropertyChange { Property = f.Name, DisplayName = f.Label, Current = f.IsSecret ? null : current, New = Display(f) });
                }

                foreach (var kv in a.Fixed)
                    item.Properties.Add(new PropertyChange { Property = kv.Key, DisplayName = CommandFormBuilder.Humanize(kv.Key), Current = CurrentValue(t.Object, kv.Key), New = kv.Value });
                if (form.Parent != null && a.ParentParameter != null) item.Notes.Add($"{CommandFormBuilder.Humanize(a.ParentParameter)}: {form.Parent.Name}");
                t.Change = item;
                plan.Items.Add(item);
            }

            plan.Summary.Add(new KeyValuePair<string, string>("Command", cmd.Name));
            plan.Summary.Add(new KeyValuePair<string, string>(a.Selection == ActionSelection.None ? "New object" : "Objects", result.Targets.Count == 1 ? result.Targets[0].Name : form.Selection.Count.ToString(CultureInfo.InvariantCulture)));
            if (form.Parent != null) plan.Summary.Add(new KeyValuePair<string, string>(CommandFormBuilder.Humanize(a.ParentParameter), form.Parent.Name));
            plan.Warnings.AddRange(a.Warnings);
            if (!cmd.SupportsShouldProcess) plan.Warnings.Add($"{cmd.Name} does not support PowerShell -WhatIf; this is the application's preview.");
            if (a.Selection != ActionSelection.None && changed.Count == 0 && a.Fixed.Count == 0 && cmd.Risk.Level != RiskLevel.Delete && a.Fields.Count > 0)
                plan.Warnings.Add("Nothing was changed.");
            plan.RequiresStrongConfirmation = a.StrongConfirmation || cmd.Risk.Level == RiskLevel.Delete || cmd.Risk.Level == RiskLevel.HighImpact;
            plan.ConfirmationPhrase = plan.RequiresStrongConfirmation
                ? (form.Selection.Count == 1 ? form.Selection[0].Name : result.Targets.Count == 1 && a.Selection == ActionSelection.None ? result.Targets[0].Name : form.Selection.Count.ToString(CultureInfo.InvariantCulture))
                : null;
            plan.ScriptPreview = _scripts.Script(result.Targets.Select(result.Build));
            result.Plan = plan;
            return result;
        }

        public async Task<List<string>> RunWhatIfAsync(QuickActionPlan plan, CancellationToken ct)
        {
            var lines = new List<string>();
            foreach (var t in plan.Targets.Where(t => t.Change == null || t.Change.HasChanges))
            {
                var o = await _runner.RunAsync(plan.Build(t).AsWhatIf(), new OperationContext("preview " + plan.Plan.Title.ToLowerInvariant(), t.Name), null, ct, quiet: true).ConfigureAwait(false);
                lines.AddRange(o.Success ? o.Result.WhatIfLines : new List<string> { $"{t.Name}: preview failed - {o.Error?.Summary}" });
            }

            plan.Plan.PreviewKind = PreviewKind.PowerShellWhatIf;
            plan.Plan.WhatIfOutput.Clear();
            plan.Plan.WhatIfOutput.AddRange(lines);
            return lines;
        }

        public Task<BulkResult<QuickActionTarget>> ApplyAsync(QuickActionPlan plan, IProgress<BulkProgress> progress, CancellationToken ct) =>
            _bulk.RunAsync(plan.Plan.Title, plan.Targets, t => t.Change != null && !t.Change.HasChanges ? null : plan.Build(t), t => t.Name, t => new OperationContext(plan.Plan.Title.ToLowerInvariant(), t.Name), progress, ct);
    }
}
