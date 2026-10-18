using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.Application.Execution;
using MilestoneAdmin.Application.Session;
using MilestoneAdmin.Domain.Changes;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Domain.Csv;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.PSTools.Execution;
using MilestoneAdmin.PSTools.Mapping;

namespace MilestoneAdmin.Application.Workflows
{
    public sealed class WorkbookHardwareRow
    {
        public int Row { get; set; }
        public string Name { get; set; }
        public string Address { get; set; }
        public string RecordingServer { get; set; }
        public string Enabled { get; set; }
        public bool ExistsOnSite { get; set; }
        public string Status => ExistsOnSite ? "Already on the site" : "New";
    }

    public sealed class WorkbookPreview
    {
        public string Path { get; set; }
        public List<string> Worksheets { get; } = new List<string>();
        public List<WorkbookHardwareRow> Hardware { get; } = new List<WorkbookHardwareRow>();
        public int CameraRows { get; set; }
        public bool ContainsPasswords { get; set; }
        public List<string> Problems { get; } = new List<string>();

        public int NewHardware => Hardware.Count(h => !h.ExistsOnSite);
        public int ExistingHardware => Hardware.Count(h => h.ExistsOnSite);

        public string SummaryText =>
            $"MilestonePSTools workbook · {Hardware.Count} hardware row(s) ({NewHardware} new, {ExistingHardware} already on the site) · {CameraRows} camera row(s) · {Worksheets.Count} worksheet(s)";
    }

    /// <summary>
    /// Imports a workbook in MilestonePSTools' own Excel format (produced by Export-VmsHardware) with Import-VmsHardware.
    /// MilestonePSTools parses and applies the whole workbook; the application previews the Hardware worksheet, adds the
    /// review/confirmation and audit, and never claims a dry run (Import-VmsHardware does not support -WhatIf).
    /// </summary>
    public sealed class WorkbookImportWorkflow
    {
        private readonly SessionContext _session;
        private readonly OperationRunner _runner;
        private readonly ScriptService _scripts;

        public WorkbookImportWorkflow(SessionContext session, OperationRunner runner, ScriptService scripts)
        {
            _session = session;
            _runner = runner;
            _scripts = scripts;
        }

        public static bool IsMilestonePSToolsWorkbook(string path) =>
            HardwareImportFile.IsExcel(path) && XlsxReader.ReadWorkbookInfo(path).IsMilestonePSToolsExport;

        public WorkbookPreview Preview(string path)
        {
            var preview = new WorkbookPreview { Path = path };
            var info = XlsxReader.ReadWorkbookInfo(path);
            preview.Worksheets.AddRange(info.Sheets.Select(s => s.Name));
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in _session.Inventory.Hardware)
                if (HardwareCsvValidator.TryParseAddress(h.Address, out var u)) existing.Add(HardwareCsvValidator.NormalizeAddress(u));

            try
            {
                var hw = XlsxReader.ReadTable(path, "Hardware");
                string Get(string[] row, string col) => hw.IndexOf(col) >= 0 ? row[hw.IndexOf(col)].Trim() : string.Empty;
                if (hw.IndexOf("Address") < 0) preview.Problems.Add("The Hardware worksheet has no Address column.");
                var passIdx = hw.IndexOf("Password");
                for (var i = 0; i < hw.Rows.Count; i++)
                {
                    var row = hw.Rows[i];
                    var address = Get(row, "Address");
                    var r = new WorkbookHardwareRow
                    {
                        Row = hw.RowLineNumbers[i], Name = Get(row, "Name"), Address = address, RecordingServer = Get(row, "RecordingServer"), Enabled = Get(row, "Enabled"),
                        ExistsOnSite = HardwareCsvValidator.TryParseAddress(address, out var uri) && existing.Contains(HardwareCsvValidator.NormalizeAddress(uri))
                    };
                    preview.Hardware.Add(r);
                    // Import-VmsHardwareExcel skips rows whose Address is not an absolute URI (e.g. http://192.168.1.101).
                    if (address.Length > 0 && !(Uri.TryCreate(address, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps)))
                        preview.Problems.Add($"Hardware worksheet row {r.Row}: '{address}' is not an absolute address such as http://{address}. MilestonePSTools will skip this row.");
                    if (passIdx >= 0 && row[passIdx].Length > 0) preview.ContainsPasswords = true;
                }

                if (preview.Hardware.Count == 0) preview.Problems.Add("The Hardware worksheet has no rows. MilestonePSTools will not import anything.");
            }
            catch (CsvFormatException ex)
            {
                preview.Problems.Add("The Hardware worksheet could not be previewed: " + ex.Message + " MilestonePSTools may still be able to read it.");
            }

            try
            {
                preview.CameraRows = XlsxReader.ReadTable(path, "Cameras").Rows.Count;
            }
            catch (CsvFormatException)
            {
                // Preview only; MilestonePSTools reports its own errors for the Cameras worksheet.
            }

            return preview;
        }

        public ChangePlan BuildPlan(WorkbookPreview preview, RecordingServerInfo recorder, string userName, SecureString password, bool updateExisting)
        {
            var plan = new ChangePlan
            {
                Title = updateExisting ? "Import and update hardware from a MilestonePSTools workbook" : "Import hardware from a MilestonePSTools workbook",
                OperationId = "hardware.import-workbook",
                Risk = updateExisting ? RiskLevel.Modify : RiskLevel.Create,
                WhatIfAvailable = false
            };
            foreach (var h in preview.Hardware)
            {
                var op = h.ExistsOnSite ? (updateExisting ? "Update" : "Existing") : "Add";
                var item = new PlannedChange { TargetType = "Hardware", TargetName = string.IsNullOrEmpty(h.Name) ? h.Address : h.Name, Operation = op };
                item.Properties.Add(new PropertyChange { Property = "Address", DisplayName = "Address", Current = h.ExistsOnSite ? h.Address : null, New = h.Address });
                item.Properties.Add(new PropertyChange
                {
                    Property = "RecordingServer", DisplayName = "Recording server", Current = null,
                    New = recorder != null ? recorder.Name + " (override)" : string.IsNullOrEmpty(h.RecordingServer) ? "(from the workbook)" : h.RecordingServer
                });
                plan.Items.Add(item);
            }

            plan.Summary.Add(new KeyValuePair<string, string>("File", System.IO.Path.GetFileName(preview.Path)));
            plan.Summary.Add(new KeyValuePair<string, string>("Hardware rows", preview.Hardware.Count.ToString(CultureInfo.InvariantCulture)));
            plan.Summary.Add(new KeyValuePair<string, string>("Camera rows", preview.CameraRows.ToString(CultureInfo.InvariantCulture)));
            plan.Summary.Add(new KeyValuePair<string, string>("Update existing hardware", updateExisting ? "Yes" : "No"));
            plan.Warnings.Add("MilestonePSTools applies the whole workbook (hardware, cameras and other devices, settings, streams, events, PTZ, groups). This preview lists the Hardware worksheet only.");
            plan.Warnings.Add("Import-VmsHardware does not support PowerShell -WhatIf; no dry run is available. This is the application's preview.");
            if (preview.ExistingHardware > 0)
                plan.Warnings.Add(updateExisting
                    ? $"{preview.ExistingHardware} hardware unit(s) already exist and will be updated with the settings in the workbook."
                    : $"{preview.ExistingHardware} hardware unit(s) already exist and will be skipped by MilestonePSTools (enable 'update existing' to update them).");
            if (preview.ContainsPasswords) plan.Warnings.Add("The workbook contains plain-text device passwords. Protect or delete the file after the import.");
            if (password == null && !preview.ContainsPasswords) plan.Warnings.Add("No device credential was given and the workbook has no passwords; MilestonePSTools will try the drivers' default credentials.");
            foreach (var p in preview.Problems) plan.Warnings.Add(p);
            plan.RequiresStrongConfirmation = updateExisting || preview.Hardware.Count >= 10;
            plan.ConfirmationPhrase = plan.RequiresStrongConfirmation ? preview.Hardware.Count.ToString(CultureInfo.InvariantCulture) : null;
            plan.ScriptPreview = _scripts.Script(new[] { _session.Gateway.PlanImportHardwareFile(preview.Path, recorder, userName, password, updateExisting) });
            return plan;
        }

        public async Task<(OperationOutcome Outcome, List<HardwareInfo> Imported)> ApplyAsync(WorkbookPreview preview, RecordingServerInfo recorder, string userName,
            SecureString password, bool updateExisting, IProgress<OperationEvent> progress, CancellationToken ct)
        {
            var op = _session.Gateway.PlanImportHardwareFile(preview.Path, recorder, userName, password, updateExisting);
            var outcome = await _runner.RunAsync(op, new OperationContext("import hardware", System.IO.Path.GetFileName(preview.Path), recorder?.Name), progress, ct).ConfigureAwait(false);
            var imported = outcome.Result?.Objects.Select(DtoMapper.Hardware).Where(h => h != null).ToList() ?? new List<HardwareInfo>();
            return (outcome, imported);
        }
    }
}
