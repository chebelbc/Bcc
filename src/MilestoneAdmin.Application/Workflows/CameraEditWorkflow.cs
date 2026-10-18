using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.Application.Execution;
using MilestoneAdmin.Application.Session;
using MilestoneAdmin.Domain.Changes;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Domain.Csv;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.PSTools.Commands;
using MilestoneAdmin.PSTools.Execution;

namespace MilestoneAdmin.Application.Workflows
{
    /// <summary>
    /// Bulk camera configuration through Set-VmsCamera (supports -WhatIf). Review -> optional PowerShell WhatIf ->
    /// apply per camera with per-object results.
    /// </summary>
    public sealed class CameraEditWorkflow
    {
        public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
        {
            { "Name", "Camera name" }, { "ShortName", "Short name" }, { "Description", "Description" }, { "Enabled", "Enabled" },
            { "RecordingEnabled", "Recording enabled" }, { "RecordingFramerate", "Recording frame rate (fps)" }, { "RecordKeyframesOnly", "Record keyframes only" },
            { "PrebufferEnabled", "Pre-buffer enabled" }, { "PrebufferSeconds", "Pre-buffer (seconds)" }
        };

        private readonly SessionContext _session;
        private readonly BulkOperationRunner _bulk;
        private readonly OperationRunner _runner;
        private readonly ScriptService _scripts;

        public CameraEditWorkflow(SessionContext session, OperationRunner runner, BulkOperationRunner bulk, ScriptService scripts)
        {
            _session = session;
            _runner = runner;
            _bulk = bulk;
            _scripts = scripts;
        }

        public static IReadOnlyDictionary<string, object> CurrentValues(CameraInfo c) => new Dictionary<string, object>
        {
            { "Name", c.Name }, { "ShortName", c.ShortName }, { "Description", c.Description }, { "Enabled", c.Enabled },
            { "RecordingEnabled", c.RecordingEnabled }, { "RecordingFramerate", c.RecordingFramerate }, { "RecordKeyframesOnly", c.RecordKeyframesOnly },
            { "PrebufferEnabled", c.PrebufferEnabled }, { "PrebufferSeconds", c.PrebufferSeconds }
        };

        /// <summary>Builds the review plan. <paramref name="desired"/> returns the requested values per camera.</summary>
        public ChangePlan BuildPlan(string title, IReadOnlyList<CameraInfo> cameras, Func<CameraInfo, IReadOnlyDictionary<string, object>> desired)
        {
            var plan = new ChangePlan
            {
                Title = title,
                OperationId = "cameras.update",
                Risk = RiskLevel.Modify,
                WhatIfAvailable = _session.Catalog?.Find("Set-VmsCamera")?.SupportsShouldProcess == true
            };
            var ops = new List<VmsOperation>();
            foreach (var cam in cameras)
            {
                var want = desired(cam);
                var item = new PlannedChange { TargetType = "Camera", TargetId = cam.Id, TargetName = cam.Name, TargetHandle = cam.Handle, Operation = "Update" };
                item.Properties.AddRange(DiffBuilder.Compute(CurrentValues(cam), want, DisplayNames));
                plan.Items.Add(item);
                var op = BuildOperation(cam, item);
                if (op != null) ops.Add(op);
            }

            plan.Summary.Add(new KeyValuePair<string, string>("Operation", title));
            plan.Summary.Add(new KeyValuePair<string, string>("Cameras selected", cameras.Count.ToString(CultureInfo.InvariantCulture)));
            plan.Summary.Add(new KeyValuePair<string, string>("Cameras to change", plan.ChangedItemCount.ToString(CultureInfo.InvariantCulture)));
            plan.Summary.Add(new KeyValuePair<string, string>("Unchanged (skipped)", plan.UnchangedItemCount.ToString(CultureInfo.InvariantCulture)));
            plan.Summary.Add(new KeyValuePair<string, string>("Command", "Set-VmsCamera"));
            var disabling = plan.Items.Count(i => i.EffectiveChanges.Any(p => p.Property == "Enabled" && Equals(p.New, false)));
            if (disabling > 0) plan.Warnings.Add($"{disabling} camera(s) will be disabled: they stop streaming and recording until enabled again.");
            var stopRec = plan.Items.Count(i => i.EffectiveChanges.Any(p => p.Property == "RecordingEnabled" && Equals(p.New, false)));
            if (stopRec > 0) plan.Warnings.Add($"Recording will be turned off for {stopRec} camera(s).");
            if (plan.ChangedItemCount >= 25) plan.Warnings.Add("This is a bulk change. Consider running the PowerShell -WhatIf preview first.");
            plan.RequiresStrongConfirmation = disabling > 0 && plan.ChangedItemCount >= 10;
            plan.ConfirmationPhrase = plan.RequiresStrongConfirmation ? plan.ChangedItemCount.ToString(CultureInfo.InvariantCulture) : null;
            plan.ScriptPreview = _scripts.Script(ops);
            return plan;
        }

        public VmsOperation BuildOperation(CameraInfo camera, PlannedChange item)
        {
            var changes = item.EffectiveChanges.ToDictionary(p => p.Property, p => p.New);
            if (changes.Count == 0) return null;
            var op = _session.Gateway?.PlanSetCamera(camera, changes) ?? new PSTools.VmsGateway(new NullExecutor()).PlanSetCamera(camera, changes);
            op.Title = "Update camera";
            return op;
        }

        /// <summary>Runs Set-VmsCamera -WhatIf for every changed camera and collects PowerShell's own "What if:" output.</summary>
        public async Task<List<string>> RunWhatIfAsync(ChangePlan plan, IReadOnlyList<CameraInfo> cameras, CancellationToken ct)
        {
            var lines = new List<string>();
            foreach (var item in plan.Items.Where(i => i.HasChanges))
            {
                ct.ThrowIfCancellationRequested();
                var cam = cameras.First(c => c.Id == item.TargetId);
                var op = BuildOperation(cam, item).AsWhatIf();
                var outcome = await _runner.RunAsync(op, new OperationContext("preview camera changes", cam.Name), null, ct, quiet: true).ConfigureAwait(false);
                if (outcome.Success) lines.AddRange(outcome.Result.WhatIfLines);
                else lines.Add($"{cam.Name}: preview failed - {outcome.Error?.Summary}");
            }

            plan.PreviewKind = PreviewKind.PowerShellWhatIf;
            plan.WhatIfOutput.Clear();
            plan.WhatIfOutput.AddRange(lines);
            return lines;
        }

        public Task<BulkResult<CameraInfo>> ApplyAsync(ChangePlan plan, IReadOnlyList<CameraInfo> cameras, IProgress<BulkProgress> progress, CancellationToken ct)
        {
            var targets = cameras.Where(c => plan.Items.Any(i => i.TargetId == c.Id && i.HasChanges)).ToList();
            return _bulk.RunAsync(plan.Title, targets, c => BuildOperation(c, plan.Items.First(i => i.TargetId == c.Id)), c => c.Name,
                c => new OperationContext("update camera", c.Name, c.RecordingServerName), progress, ct,
                (c, outcome) =>
                {
                    // Refresh the cached DTO from the returned object (Set-VmsCamera -PassThru) so the UI reflects reality.
                    var updated = outcome.Result?.Objects.Select(PSTools.Mapping.DtoMapper.Camera).FirstOrDefault();
                    if (updated != null) CopyFrom(c, updated);
                    return Task.CompletedTask;
                });
        }

        private static void CopyFrom(CameraInfo target, CameraInfo s)
        {
            target.Name = s.Name;
            target.ShortName = s.ShortName;
            target.Description = s.Description;
            target.Enabled = s.Enabled;
            target.RecordingEnabled = s.RecordingEnabled;
            target.RecordingFramerate = s.RecordingFramerate;
            target.RecordKeyframesOnly = s.RecordKeyframesOnly;
            target.PrebufferEnabled = s.PrebufferEnabled;
            target.PrebufferSeconds = s.PrebufferSeconds;
            if (!string.IsNullOrEmpty(s.Handle)) target.Handle = s.Handle;
        }

        // ------------------------------------------------------------ naming helpers

        /// <summary>
        /// Expands a naming pattern. Tokens: {Name} {Hardware} {Address} {RecordingServer} {Channel} (0-based)
        /// {Channel1} (1-based) {Index} {Index:000}.
        /// </summary>
        public static string ExpandPattern(string pattern, CameraInfo c, int index)
        {
            if (string.IsNullOrEmpty(pattern)) return c.Name;
            var host = Uri.TryCreate(c.HardwareAddress ?? string.Empty, UriKind.Absolute, out var u) ? u.Host : c.HardwareAddress;
            return Regex.Replace(pattern, @"\{(Name|Hardware|Address|RecordingServer|Channel1|Channel|Index)(?::(0+))?\}", m =>
            {
                switch (m.Groups[1].Value)
                {
                    case "Name": return c.Name ?? string.Empty;
                    case "Hardware": return c.HardwareName ?? string.Empty;
                    case "Address": return host ?? string.Empty;
                    case "RecordingServer": return c.RecordingServerName ?? string.Empty;
                    case "Channel": return (c.Channel ?? 0).ToString(m.Groups[2].Success ? m.Groups[2].Value : "0", CultureInfo.InvariantCulture);
                    case "Channel1": return ((c.Channel ?? 0) + 1).ToString(m.Groups[2].Success ? m.Groups[2].Value : "0", CultureInfo.InvariantCulture);
                    default: return index.ToString(m.Groups[2].Success ? m.Groups[2].Value : "0", CultureInfo.InvariantCulture);
                }
            });
        }

        public sealed class RenameMapping
        {
            public Dictionary<Guid, string> ById { get; } = new Dictionary<Guid, string>();
            public Dictionary<string, string> ByName { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<CsvRowResult> Rows { get; } = new List<CsvRowResult>();
            public int Valid => Rows.Count(r => r.IsValid);
            public int Invalid => Rows.Count(r => !r.IsValid);
        }

        /// <summary>Reads a rename CSV with columns NewName and either Id or Name (current name).</summary>
        public static RenameMapping ParseRenameCsv(CsvTable table)
        {
            var map = new RenameMapping();
            var idCol = table.IndexOf("Id");
            var nameCol = table.IndexOf("Name");
            var newCol = table.IndexOf("NewName");
            for (var i = 0; i < table.Rows.Count; i++)
            {
                var row = new CsvRowResult { LineNumber = table.RowLineNumbers[i] };
                for (var c = 0; c < table.Headers.Count; c++) row.Values[table.Headers[c]] = table.Rows[i][c];
                if (newCol < 0) row.Errors.Add("Column 'NewName' is required.");
                if (idCol < 0 && nameCol < 0) row.Errors.Add("Column 'Id' or 'Name' is required to identify the camera.");
                var newName = newCol >= 0 ? table.Rows[i][newCol].Trim() : string.Empty;
                if (newCol >= 0 && newName.Length == 0) row.Errors.Add("NewName is empty.");
                if (newName.Length > 255) row.Errors.Add("NewName is longer than 255 characters.");
                if (row.IsValid)
                {
                    if (idCol >= 0 && Guid.TryParse(table.Rows[i][idCol], out var id))
                    {
                        if (map.ById.ContainsKey(id)) row.Errors.Add("Duplicate camera Id.");
                        else map.ById[id] = newName;
                    }
                    else if (nameCol >= 0 && table.Rows[i][nameCol].Trim().Length > 0)
                    {
                        var key = table.Rows[i][nameCol].Trim();
                        if (map.ByName.ContainsKey(key)) row.Errors.Add("Duplicate camera name.");
                        else map.ByName[key] = newName;
                    }
                    else row.Errors.Add("Id is not a valid GUID and Name is empty.");
                }

                map.Rows.Add(row);
            }

            return map;
        }

        private sealed class NullExecutor : IPowerShellExecutor
        {
            public bool IsRunning => false;
            public WorkerInfo Info => null;
            public event EventHandler<WorkerExitedEventArgs> Exited { add { } remove { } }
            public event EventHandler<string> Diagnostic { add { } remove { } }
            public Task<WorkerInfo> StartAsync(WorkerStartOptions options, CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task<Protocol.Json.JsonObject> GetEnvironmentAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task<Protocol.Json.JsonObject> DiscoverCatalogAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task<OperationResult> InvokeAsync(VmsOperation operation, IProgress<OperationEvent> progress, CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task ReleaseHandlesAsync(IEnumerable<string> handles) => Task.CompletedTask;
            public Task StopAsync() => Task.CompletedTask;
            public void Dispose() { }
        }
    }
}
