using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.Application.Services;
using MilestoneAdmin.Application.Session;
using MilestoneAdmin.Domain.Csv;
using MilestoneAdmin.Domain.Errors;
using MilestoneAdmin.Protocol.Json;
using MilestoneAdmin.PSTools;
using MilestoneAdmin.PSTools.Commands;
using MilestoneAdmin.PSTools.Mapping;

namespace MilestoneAdmin.Application.Reports
{
    public sealed class ReportDefinition
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string[] Commands { get; set; }
    }

    public sealed class ReportResult
    {
        public string Title { get; set; }
        public DateTimeOffset GeneratedAt { get; set; }
        public string System { get; set; }
        public string Source { get; set; }
        public List<string> Columns { get; } = new List<string>();
        public List<string[]> Rows { get; } = new List<string[]>();
        public List<string> Notes { get; } = new List<string>();
        public AdminError Error { get; set; }

        public string ToCsv() => CsvParser.Write(Columns, Rows.Select(r => (IReadOnlyList<string>)r));

        public string ToJson()
        {
            var rows = new JsonArray();
            foreach (var r in Rows)
            {
                var o = new JsonObject();
                for (var i = 0; i < Columns.Count; i++) o.Set(Columns[i], i < r.Length ? r[i] : null);
                rows.Add(o);
            }

            return new JsonObject
            {
                { "title", Title }, { "generatedAt", GeneratedAt.ToString("o", CultureInfo.InvariantCulture) }, { "system", System },
                { "source", Source }, { "notes", new JsonArray(Notes.Select(n => (JsonNode)n)) }, { "rows", rows }
            }.ToJson(true);
        }
    }

    /// <summary>
    /// Reports built only from data MilestonePSTools returns (read operations). Columns that the module does not provide
    /// are not invented; units are stated only where the property name defines them.
    /// </summary>
    public sealed class ReportService
    {
        public static readonly IReadOnlyList<ReportDefinition> Definitions = new List<ReportDefinition>
        {
            new ReportDefinition { Id = "camera-inventory", Title = "Camera inventory", Description = "All cameras with hardware, recording server and recording settings.", Commands = new[] { "Get-VmsRecordingServer", "Get-VmsHardware", "Get-VmsCamera", "Get-VmsStorage" } },
            new ReportDefinition { Id = "hardware-inventory", Title = "Hardware inventory", Description = "All hardware devices with address, model and recording server.", Commands = new[] { "Get-VmsRecordingServer", "Get-VmsHardware", "Get-VmsCamera" } },
            new ReportDefinition { Id = "recorder-inventory", Title = "Recording server inventory", Description = "Recording servers with hardware and camera counts.", Commands = new[] { "Get-VmsRecordingServer", "Get-VmsHardware", "Get-VmsCamera" } },
            new ReportDefinition { Id = "disabled-cameras", Title = "Disabled cameras", Description = "Cameras that are disabled in the configuration.", Commands = new[] { "Get-VmsCamera", "Get-VmsHardware" } },
            new ReportDefinition { Id = "device-status", Title = "Device status", Description = "Live status reported by the recording servers (Get-VmsDeviceStatus).", Commands = new[] { "Get-VmsDeviceStatus" } },
            new ReportDefinition { Id = "storage", Title = "Storage configuration", Description = "Live recording storages per recording server, with retention.", Commands = new[] { "Get-VmsStorage", "Get-VmsRecordingServer" } },
            new ReportDefinition { Id = "roles", Title = "Roles", Description = "Roles configured in the VMS.", Commands = new[] { "Get-VmsRole" } },
            new ReportDefinition { Id = "summary", Title = "Configuration summary", Description = "Counts of the main configuration objects.", Commands = new[] { "Get-VmsManagementServer", "Get-VmsRecordingServer", "Get-VmsHardware", "Get-VmsCamera" } },
            new ReportDefinition { Id = "camera-report", Title = "Detailed camera report (Get-VmsCameraReport)", Description = "MilestonePSTools' own detailed per-camera report. Can take several minutes on large systems; needs access to recording servers (TCP 7563) for status columns.", Commands = new[] { "Get-VmsCameraReport" } }
        };

        private readonly SessionContext _session;
        private readonly InventoryService _inventory;

        public ReportService(SessionContext session, InventoryService inventory)
        {
            _session = session;
            _inventory = inventory;
        }

        public bool IsAvailable(ReportDefinition d) => _session.Catalog != null && d.Commands.All(_session.Catalog.Contains);

        public string UnavailableReason(ReportDefinition d)
        {
            var missing = d.Commands.Where(c => _session.Catalog == null || !_session.Catalog.Contains(c)).ToList();
            return missing.Count == 0 ? null : $"Not supported by the currently installed MilestonePSTools version: missing {string.Join(", ", missing)}.";
        }

        private static string B(bool? b) => b.HasValue ? (b.Value ? "True" : "False") : string.Empty;

        private static string N(double? d) => d.HasValue ? d.Value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;

        public async Task<ReportResult> RunAsync(ReportDefinition d, CancellationToken ct)
        {
            var r = new ReportResult { Title = d.Title, GeneratedAt = DateTimeOffset.Now, System = _session.SystemName, Source = "MilestonePSTools: " + string.Join(", ", d.Commands) };
            if (!IsAvailable(d))
            {
                r.Error = new AdminError { Kind = ErrorKind.ModuleLoadFailed, Summary = UnavailableReason(d) };
                return r;
            }

            var inv = _session.Inventory;
            var needsInventory = d.Id != "device-status" && d.Id != "roles" && d.Id != "camera-report";
            if (needsInventory && inv.LoadedAt == null)
            {
                var load = await _inventory.RefreshAsync(ct).ConfigureAwait(false);
                if (!load.Success)
                {
                    r.Error = load.Error;
                    return r;
                }

                inv = _session.Inventory;
            }

            switch (d.Id)
            {
                case "camera-inventory":
                case "disabled-cameras":
                    r.Columns.AddRange(new[] { "Name", "Enabled", "Channel", "Hardware", "Address", "Recording server", "Recording enabled", "Recording frame rate", "Pre-buffer (s)", "Storage", "Id" });
                    foreach (var c in inv.Cameras.Where(c => d.Id != "disabled-cameras" || c.Enabled == false).OrderBy(c => c.RecordingServerName).ThenBy(c => c.Name))
                        r.Rows.Add(new[] { c.Name, B(c.Enabled), c.Channel?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, c.HardwareName, c.HardwareAddress, c.RecordingServerName, B(c.RecordingEnabled), N(c.RecordingFramerate), c.PrebufferSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, c.RecordingStorageName ?? c.RecordingStorage, c.Id.ToString() });
                    break;
                case "hardware-inventory":
                    r.Columns.AddRange(new[] { "Name", "Address", "Model", "Enabled", "Recording server", "Cameras", "User name", "Id" });
                    foreach (var h in inv.Hardware.OrderBy(h => h.RecordingServerName).ThenBy(h => h.Name))
                        r.Rows.Add(new[] { h.Name, h.Address, h.Model, B(h.Enabled), h.RecordingServerName, h.CameraCount.ToString(CultureInfo.InvariantCulture), h.UserName, h.Id.ToString() });
                    r.Notes.Add("Hardware passwords are not included in this report.");
                    break;
                case "recorder-inventory":
                    r.Columns.AddRange(new[] { "Name", "Host name", "Web server URI", "Enabled", "Hardware", "Cameras", "Disabled cameras", "Id" });
                    foreach (var x in inv.RecordingServers)
                        r.Rows.Add(new[] { x.Name, x.HostName, x.WebServerUri, B(x.Enabled), x.HardwareCount.ToString(CultureInfo.InvariantCulture), x.CameraCount.ToString(CultureInfo.InvariantCulture), x.DisabledCameraCount.ToString(CultureInfo.InvariantCulture), x.Id.ToString() });
                    break;
                case "storage":
                    r.Columns.AddRange(new[] { "Name", "Recording server", "Disk path", "Retention (days)", "MaxSize (as reported)", "Default", "Id" });
                    foreach (var s in inv.Storage.OrderBy(s => s.RecordingServerName).ThenBy(s => s.Name))
                        r.Rows.Add(new[] { s.Name, s.RecordingServerName, s.DiskPath, s.Retention.HasValue ? s.Retention.Value.TotalDays.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty, s.MaxSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, B(s.IsDefault), s.Id.ToString() });
                    r.Notes.Add("Retention is computed from the RetainMinutes property. MaxSize is shown exactly as MilestonePSTools reports it.");
                    break;
                case "device-status":
                {
                    var st = await _session.Gateway.GetDeviceStatusAsync(null, ct).ConfigureAwait(false);
                    if (!st.Success)
                    {
                        r.Error = ErrorTranslator.Translate(st.Raw, new OperationContext("read device status", null, _session.SystemName));
                        return r;
                    }

                    r.Columns.AddRange(new[] { "Device", "Type", "Recording server", "Enabled", "Started", "Recording", "Status", "Time" });
                    foreach (var s in st.Items.OrderBy(s => s.RecorderName).ThenBy(s => s.DeviceName))
                        r.Rows.Add(new[] { s.DeviceName, s.DeviceType, s.RecorderName, B(s.Enabled), B(s.Started), B(s.Recording), s.Summary, s.Time?.ToString("u", CultureInfo.InvariantCulture) ?? string.Empty });
                    foreach (var e in st.Raw.Errors) r.Notes.Add("Warning: " + e.Message);
                    break;
                }

                case "roles":
                {
                    var roles = await _session.Gateway.GetRolesAsync(ct).ConfigureAwait(false);
                    if (!roles.Success)
                    {
                        r.Error = ErrorTranslator.Translate(roles.Raw, new OperationContext("read roles"));
                        return r;
                    }

                    r.Columns.AddRange(new[] { "Name", "Role type", "Description", "Id" });
                    foreach (var x in roles.Items.OrderBy(x => x.Name)) r.Rows.Add(new[] { x.Name, x.RoleType, x.Description, x.Id.ToString() });
                    break;
                }

                case "summary":
                    r.Columns.AddRange(new[] { "Item", "Value" });
                    r.Rows.Add(new[] { "Management server", _session.ManagementServer?.Name });
                    r.Rows.Add(new[] { "Computer name", _session.ManagementServer?.ComputerName });
                    r.Rows.Add(new[] { "XProtect version", _session.ManagementServer?.Version });
                    r.Rows.Add(new[] { "MilestonePSTools version", _session.ModuleVersion });
                    r.Rows.Add(new[] { "Recording servers", inv.RecordingServers.Count.ToString(CultureInfo.InvariantCulture) });
                    r.Rows.Add(new[] { "Hardware", inv.Hardware.Count.ToString(CultureInfo.InvariantCulture) });
                    r.Rows.Add(new[] { "Disabled hardware", inv.DisabledHardware.ToString(CultureInfo.InvariantCulture) });
                    r.Rows.Add(new[] { "Cameras", inv.Cameras.Count.ToString(CultureInfo.InvariantCulture) });
                    r.Rows.Add(new[] { "Disabled cameras", inv.DisabledCameras.ToString(CultureInfo.InvariantCulture) });
                    r.Rows.Add(new[] { "Storages", inv.Storage.Count.ToString(CultureInfo.InvariantCulture) });
                    break;
                case "camera-report":
                {
                    var op = new PSTools.Execution.VmsOperation { Title = "Get-VmsCameraReport", TimeoutSeconds = 3600, Risk = Domain.Classification.RiskLevel.ReadOnly };
                    op.Pipeline.Add(PsCommands.GetVmsCameraReport(includeRetentionInfo: false, includeRecordingStats: false));
                    op.Output.RegisterHandles = false;
                    var res = await _session.Gateway.ExecuteAsync(op, null, ct).ConfigureAwait(false);
                    if (!res.Success)
                    {
                        r.Error = ErrorTranslator.Translate(res, new OperationContext("run the camera report"));
                        return r;
                    }

                    var objs = res.Objects.ToList();
                    var cols = objs.SelectMany(o => o.Keys).Where(k => !k.StartsWith("$", StringComparison.Ordinal)).Distinct().ToList();
                    r.Columns.AddRange(cols);
                    foreach (var o in objs) r.Rows.Add(cols.Select(c => Convert.ToString(DtoMapper.ToClr(o[c]), CultureInfo.InvariantCulture) ?? string.Empty).ToArray());
                    r.Notes.Add("Columns are exactly those returned by Get-VmsCameraReport. Plain-text passwords are never requested by this application.");
                    foreach (var e in res.Errors) r.Notes.Add("Warning: " + e.Message);
                    break;
                }
            }

            foreach (var w in inv.Warnings) r.Notes.Add("Warning: " + w);
            return r;
        }
    }
}
