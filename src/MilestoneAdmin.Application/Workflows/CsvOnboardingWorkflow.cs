using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Application.Activity;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.Application.Execution;
using MilestoneAdmin.Application.Session;
using MilestoneAdmin.Domain.Csv;
using MilestoneAdmin.Domain.Errors;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.PSTools.Execution;
using MilestoneAdmin.PSTools.Mapping;

namespace MilestoneAdmin.Application.Workflows
{
    public sealed class CsvImportItemResult
    {
        public HardwareImportItem Item { get; set; }
        public string Status { get; set; }
        public AdminError Error { get; set; }
        public HardwareInfo Hardware { get; set; }
        public List<string> Messages { get; } = new List<string>();

        public bool Succeeded => Error == null && Status != "Skipped";
    }

    /// <summary>
    /// Guided CSV hardware onboarding. Validation (offline, then online against the connected site) happens before
    /// anything is applied; the administrator inspects invalid rows first. Each hardware unit is added individually so
    /// failures are reported per row and can be retried.
    /// </summary>
    public sealed class CsvOnboardingWorkflow
    {
        private readonly SessionContext _session;
        private readonly OperationRunner _runner;
        private readonly ActivityLog _activity;

        public CsvOnboardingWorkflow(SessionContext session, OperationRunner runner, ActivityLog activity)
        {
            _session = session;
            _runner = runner;
            _activity = activity;
        }

        public CsvTable Table { get; private set; }
        public HardwareCsvReport Report { get; private set; }
        public string DefaultRecordingServer { get; set; }
        public DeviceCredential CommonCredential { get; set; }
        public bool UseDefaultCredentials { get; set; } = true;

        public HardwareCsvReport Load(CsvTable table, HardwareCsvSiteContext site)
        {
            Table = table;
            Report = HardwareCsvValidator.Validate(table, DefaultRecordingServer, site);
            return Report;
        }

        /// <summary>Collects the site facts needed for online validation (recorders, installed drivers, storage, existing hardware).</summary>
        public async Task<(HardwareCsvSiteContext Context, AdminError Error)> BuildSiteContextAsync(CsvTable table, CancellationToken ct)
        {
            var ctx = new HardwareCsvSiteContext();
            var inv = _session.Inventory;
            foreach (var r in inv.RecordingServers) ctx.RecordingServers.Add(r.Name);
            foreach (var s in inv.Storage.Where(s => s.RecordingServerName != null))
            {
                if (!ctx.StorageNames.TryGetValue(s.RecordingServerName, out var set)) ctx.StorageNames[s.RecordingServerName] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(s.Name);
            }

            foreach (var h in inv.Hardware)
            {
                if (HardwareCsvValidator.TryParseAddress(h.Address, out var u)) ctx.ExistingAddresses.Add(HardwareCsvValidator.NormalizeAddress(u));
            }

            // Drivers only for recorders referenced by rows that specify a driver number.
            var col = table.IndexOf("RecordingServer");
            var drvCol = table.IndexOf("DriverNumber");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (drvCol >= 0)
            {
                foreach (var row in table.Rows)
                {
                    if (row[drvCol].Trim().Length == 0) continue;
                    var n = col >= 0 && row[col].Trim().Length > 0 ? row[col].Trim() : DefaultRecordingServer;
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
            }

            foreach (var n in names)
            {
                var rec = inv.RecordingServers.FirstOrDefault(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase));
                if (rec == null) continue;
                var drivers = await _session.Gateway.GetHardwareDriversAsync(rec, ct).ConfigureAwait(false);
                if (!drivers.Success) return (ctx, ErrorTranslator.Translate(drivers.Raw, new OperationContext("read hardware drivers", null, rec.Name)));
                ctx.DriverNumbers[rec.Name] = new HashSet<int>(drivers.Items.Where(d => d.Number.HasValue).Select(d => d.Number.Value));
            }

            return (ctx, null);
        }

        public async Task<List<CsvImportItemResult>> ApplyAsync(IEnumerable<HardwareImportItem> items, IProgress<BulkProgress> progress, CancellationToken ct)
        {
            var list = items.ToList();
            var results = new List<CsvImportItemResult>();
            var n = 0;
            foreach (var item in list)
            {
                progress?.Report(new BulkProgress { Completed = n, Total = list.Count, Current = item.DisplayAddress, Failed = results.Count(r => r.Error != null) });
                if (ct.IsCancellationRequested)
                {
                    results.Add(new CsvImportItemResult { Item = item, Status = "Cancelled" });
                    continue;
                }

                if (item.AlreadyOnSite)
                {
                    results.Add(new CsvImportItemResult { Item = item, Status = "Skipped", Messages = { "Hardware with this address already exists." } });
                    n++;
                    continue;
                }

                results.Add(await ApplyOneAsync(item, ct).ConfigureAwait(false));
                n++;
            }

            progress?.Report(new BulkProgress { Completed = n, Total = list.Count, Failed = results.Count(r => r.Error != null) });
            var ok = results.Count(r => r.Succeeded);
            _activity.Add(results.Any(r => r.Error != null) ? ActivityLevel.Warning : ActivityLevel.Success,
                $"CSV onboarding: {ok} added, {results.Count(r => r.Error != null)} failed, {results.Count(r => r.Status == "Skipped")} skipped.");
            return results;
        }

        private (string User, SecureString Password) CredentialFor(HardwareImportItem item) =>
            item.HasCsvCredential ? (item.UserName, item.Password) : (CommonCredential?.UserName, CommonCredential?.Password);

        private async Task<CsvImportItemResult> ApplyOneAsync(HardwareImportItem item, CancellationToken ct)
        {
            var result = new CsvImportItemResult { Item = item };
            var gw = _session.Gateway;
            var rec = _session.Inventory.RecordingServers.FirstOrDefault(r => string.Equals(r.Name, item.RecordingServer, StringComparison.OrdinalIgnoreCase));
            var ctx = new OperationContext("add hardware", item.DisplayAddress, item.RecordingServer);
            if (rec == null)
            {
                result.Status = "Failed";
                result.Error = new AdminError { Kind = ErrorKind.ObjectNotFound, Summary = $"Recording server '{item.RecordingServer}' was not found.", Operation = "Add hardware", Target = item.DisplayAddress };
                return result;
            }

            var (user, pwd) = CredentialFor(item);
            VmsOperation add;
            if (item.DriverNumber.HasValue)
            {
                if (pwd == null)
                {
                    result.Status = "Failed";
                    result.Error = new AdminError { Kind = ErrorKind.InvalidInput, Summary = "A device credential is required when a DriverNumber is given (provide UserName/Password in the file or a common credential).", Operation = "Add hardware", Target = item.DisplayAddress };
                    return result;
                }

                add = gw.PlanAddHardwareManual(rec, item.Address, item.HardwareName, item.DriverNumber.Value, user, pwd);
            }
            else
            {
                var creds = pwd != null ? new List<(string, SecureString)> { (user, pwd) } : new List<(string, SecureString)>();
                var scanOp = gw.PlanAddressScan(rec, new[] { item.Address }, null, item.DriverGroups, creds, UseDefaultCredentials, item.Address.Scheme == Uri.UriSchemeHttps);
                var scan = await _runner.RunAsync(scanOp, new OperationContext("scan for hardware", item.DisplayAddress, rec.Name), null, ct, quiet: true).ConfigureAwait(false);
                var found = scan.Result.Objects.Select(DtoMapper.HardwareScanResult).FirstOrDefault(s => s.HardwareScanValidated);
                if (found == null)
                {
                    result.Status = "Failed";
                    result.Error = scan.Error ?? new AdminError
                    {
                        Kind = ErrorKind.ObjectNotFound,
                        Summary = $"No supported device was identified at {item.DisplayAddress}.",
                        Operation = "Add hardware",
                        Target = item.DisplayAddress,
                        Server = rec.Name,
                        PossibleCauses = { "The device is offline or unreachable from the recording server.", "Wrong credentials.", "The driver is not in the installed device pack." },
                        SuggestedChecks = { "Ping/browse the device from the recording server.", "Provide a DriverNumber or DriverGroup in the CSV." }
                    };
                    return result;
                }

                add = gw.PlanAddHardwareFromScan(new[] { found }, item.HardwareName);
            }

            var outcome = await _runner.RunAsync(add, ctx, null, ct, quiet: true).ConfigureAwait(false);
            if (!outcome.Success || outcome.PartialFailure)
            {
                result.Status = "Failed";
                result.Error = outcome.Error ?? ErrorTranslator.Translate(outcome.Result.Errors[0], ctx);
                _activity.Error($"{item.DisplayAddress} ✗ {result.Error.Summary}");
                return result;
            }

            result.Hardware = outcome.Result.Objects.Select(DtoMapper.Hardware).FirstOrDefault();
            _activity.Success($"{item.DisplayAddress} ✓ added as '{result.Hardware?.Name}'");
            if (result.Hardware != null && item.Channels.Count > 0) await ConfigureChannelsAsync(item, result, rec, ct).ConfigureAwait(false);
            result.Status = result.Messages.Any(m => m.StartsWith("Failed", StringComparison.Ordinal)) ? "Added (with errors)" : "Added";
            return result;
        }

        private async Task ConfigureChannelsAsync(HardwareImportItem item, CsvImportItemResult result, RecordingServerInfo rec, CancellationToken ct)
        {
            var cams = await _session.Gateway.GetCamerasAsync(new[] { result.Hardware }, ct).ConfigureAwait(false);
            if (!cams.Success)
            {
                result.Messages.Add("Failed to read the new cameras: " + cams.Raw.PrimaryError?.Message);
                return;
            }

            foreach (var ch in item.Channels)
            {
                var cam = cams.Items.FirstOrDefault(c => c.Channel == ch.Channel);
                if (cam == null)
                {
                    result.Messages.Add($"Failed: line {ch.SourceLine}: channel {ch.Channel} does not exist on this hardware.");
                    continue;
                }

                var props = new Dictionary<string, object>();
                if (ch.Name != null && ch.Name != cam.Name) props["Name"] = ch.Name;
                if (ch.Enabled.HasValue && ch.Enabled != cam.Enabled) props["Enabled"] = ch.Enabled.Value;
                if (props.Count > 0)
                {
                    var o = await _runner.RunAsync(_session.Gateway.PlanSetCamera(cam, props), new OperationContext("configure camera", cam.Name, rec.Name), null, ct, quiet: true).ConfigureAwait(false);
                    result.Messages.Add(o.Success && !o.PartialFailure ? $"Channel {ch.Channel}: configured." : $"Failed: channel {ch.Channel}: {(o.Error ?? ErrorTranslator.Translate(o.Result.Errors[0], new OperationContext("configure camera", cam.Name))).Summary}");
                }

                if (ch.StorageName != null)
                {
                    var s = await _runner.RunAsync(_session.Gateway.PlanSetDeviceStorage(cam, ch.StorageName), new OperationContext("set recording storage", cam.Name, rec.Name), null, ct, quiet: true).ConfigureAwait(false);
                    result.Messages.Add(s.Success && !s.PartialFailure ? $"Channel {ch.Channel}: storage '{ch.StorageName}'." : $"Failed: channel {ch.Channel} storage: {(s.Error ?? ErrorTranslator.Translate(s.Result.Errors[0], new OperationContext("set recording storage", cam.Name))).Summary}");
                }

                if (ch.Coordinates != null) result.Messages.Add($"Channel {ch.Channel}: coordinates not applied by guided onboarding (use Import-VmsHardware in Expert Mode).");
            }
        }

        /// <summary>CSV of rows that were invalid or failed, with the reason, for correction and re-import.</summary>
        public string FailedRowsCsv(IEnumerable<CsvImportItemResult> results)
        {
            var headers = new List<string> { "Line", "Status", "Reason" };
            headers.AddRange(Table.Headers.Where(h => !string.Equals(h, "Password", StringComparison.OrdinalIgnoreCase)));
            var rows = new List<IReadOnlyList<string>>();
            foreach (var row in Report.Rows.Where(r => !r.IsValid))
                rows.Add(new[] { row.LineNumber.ToString(CultureInfo.InvariantCulture), "Invalid", string.Join("; ", row.Errors) }.Concat(headers.Skip(3).Select(row.Get)).ToList());
            foreach (var r in (results ?? Enumerable.Empty<CsvImportItemResult>()).Where(r => r.Error != null))
            foreach (var line in r.Item.SourceLines)
            {
                var row = Report.Rows.First(x => x.LineNumber == line);
                rows.Add(new[] { line.ToString(CultureInfo.InvariantCulture), "Failed", r.Error.Summary }.Concat(headers.Skip(3).Select(row.Get)).ToList());
            }

            return CsvParser.Write(headers, rows);
        }
    }
}
