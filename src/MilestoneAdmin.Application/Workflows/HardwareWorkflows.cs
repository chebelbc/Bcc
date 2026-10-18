using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.Application.Execution;
using MilestoneAdmin.Application.Session;
using MilestoneAdmin.Domain.Changes;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Domain.Csv;
using MilestoneAdmin.Domain.Errors;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.PSTools.Execution;
using MilestoneAdmin.PSTools.Mapping;

namespace MilestoneAdmin.Application.Workflows
{
    public enum DiscoveryMode
    {
        /// <summary>Start-VmsHardwareScan -Express on the recording server's network.</summary>
        Express,

        /// <summary>Start-VmsHardwareScan -Address for the entered addresses/ranges (driver auto-detection, optionally limited).</summary>
        Addresses,

        /// <summary>Add-VmsHardware with a known driver number, no scan.</summary>
        KnownDriver
    }

    public sealed class DeviceCredential
    {
        public string UserName { get; set; }
        public SecureString Password { get; set; }
    }

    public sealed class AddressParseResult
    {
        public List<Uri> Addresses { get; } = new List<Uri>();
        public List<string> Errors { get; } = new List<string>();
    }

    /// <summary>State and steps of the Add Hardware wizard.</summary>
    public sealed class AddHardwareWorkflow
    {
        public const int MaxAddresses = 1024;

        private readonly SessionContext _session;
        private readonly OperationRunner _runner;
        private readonly BulkOperationRunner _bulk;
        private readonly ScriptService _scripts;

        public AddHardwareWorkflow(SessionContext session, OperationRunner runner, BulkOperationRunner bulk, ScriptService scripts)
        {
            _session = session;
            _runner = runner;
            _bulk = bulk;
            _scripts = scripts;
        }

        public RecordingServerInfo Recorder { get; set; }
        public DiscoveryMode Mode { get; set; } = DiscoveryMode.Addresses;
        public List<Uri> Addresses { get; } = new List<Uri>();
        public List<DeviceCredential> Credentials { get; } = new List<DeviceCredential>();
        public bool UseDefaultCredentials { get; set; } = true;
        public bool UseHttps { get; set; }
        public HardwareDriverInfo SelectedDriver { get; set; }
        public List<int> LimitToDrivers { get; } = new List<int>();
        public List<HardwareDriverInfo> AvailableDrivers { get; } = new List<HardwareDriverInfo>();
        public List<HardwareScanResultInfo> ScanResults { get; } = new List<HardwareScanResultInfo>();
        public List<AddCandidate> Candidates { get; } = new List<AddCandidate>();
        public List<HardwareInfo> AddedHardware { get; } = new List<HardwareInfo>();
        public List<ChannelSetting> Channels { get; } = new List<ChannelSetting>();

        public sealed class AddCandidate
        {
            public bool Selected { get; set; } = true;
            public string Address { get; set; }
            public string DriverName { get; set; }
            public string MacAddress { get; set; }
            public string HardwareName { get; set; }
            public string Status { get; set; }
            public bool AlreadyExists { get; set; }
            public HardwareScanResultInfo ScanResult { get; set; }
            public Uri ManualAddress { get; set; }
        }

        public sealed class ChannelSetting
        {
            public CameraInfo Camera { get; set; }
            public string HardwareName { get; set; }
            public int Channel { get; set; }
            public bool Enabled { get; set; }
            public string Name { get; set; }
            public string Storage { get; set; }
        }

        /// <summary>Parses addresses: one per line or comma-separated; IPv4 ranges "a.b.c.d-e" or "a.b.c.d-a.b.c.e"; host names; URLs.</summary>
        private static bool IsDottedQuad(string s)
        {
            var parts = s.Split('.');
            return parts.Length == 4 && parts.All(p => p.Length > 0 && p.Length <= 3 && p.All(char.IsDigit));
        }

        public static AddressParseResult ParseAddresses(string text, bool https)
        {
            var r = new AddressParseResult();
            var tokens = (text ?? string.Empty).Split(new[] { '\n', '\r', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in tokens)
            {
                var t = raw.Trim();
                var dash = t.IndexOf('-');
                if (dash > 0 && !t.Contains("://") && IPAddress.TryParse(t.Substring(0, dash), out var start) && start.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    var endText = t.Substring(dash + 1);
                    // IPAddress.TryParse accepts shorthand such as "10.0.0" (= 10.0.0.0); only dotted quads are ranges.
                    if (!IsDottedQuad(t.Substring(0, dash)) || (endText.Contains(".") && !IsDottedQuad(endText)))
                    {
                        r.Errors.Add($"'{t}' is not a valid IPv4 range.");
                        continue;
                    }

                    var sb = start.GetAddressBytes();
                    IPAddress end;
                    if (int.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var last) && last >= 0 && last <= 255)
                        end = new IPAddress(new[] { sb[0], sb[1], sb[2], (byte)last });
                    else if (!IPAddress.TryParse(endText, out end) || end.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        r.Errors.Add($"'{t}' is not a valid IPv4 range.");
                        continue;
                    }

                    var a = ToUInt(start);
                    var b = ToUInt(end);
                    if (b < a)
                    {
                        r.Errors.Add($"'{t}': the range end is before the start.");
                        continue;
                    }

                    if (b - a + 1 + (uint)r.Addresses.Count > MaxAddresses)
                    {
                        r.Errors.Add($"'{t}' expands to more than {MaxAddresses} addresses.");
                        continue;
                    }

                    for (var x = a; x <= b; x++) r.Addresses.Add(new Uri((https ? "https://" : "http://") + FromUInt(x) + "/"));
                    continue;
                }

                if (HardwareCsvValidator.TryParseAddress(t.Contains("://") ? t : (https ? "https://" : "http://") + t, out var uri)) r.Addresses.Add(uri);
                else r.Errors.Add($"'{t}' is not a valid address.");
            }

            var distinct = r.Addresses.GroupBy(HardwareCsvValidator.NormalizeAddress).Select(g => g.First()).ToList();
            r.Addresses.Clear();
            r.Addresses.AddRange(distinct);
            if (r.Addresses.Count > MaxAddresses) r.Errors.Add($"More than {MaxAddresses} addresses.");
            return r;
        }

        private static uint ToUInt(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static string FromUInt(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";

        public async Task<AdminError> LoadDriversAsync(CancellationToken ct)
        {
            AvailableDrivers.Clear();
            var r = await _session.Gateway.GetHardwareDriversAsync(Recorder, ct).ConfigureAwait(false);
            if (!r.Success) return ErrorTranslator.Translate(r.Raw, new OperationContext("read hardware drivers", null, Recorder.Name));
            AvailableDrivers.AddRange(r.Items.OrderBy(d => d.GroupName).ThenBy(d => d.Name));
            return null;
        }

        private IReadOnlyList<(string, SecureString)> CredentialList() => Credentials.Where(c => !string.IsNullOrEmpty(c.UserName) && c.Password != null).Select(c => (c.UserName, c.Password)).ToList();

        public VmsOperation PlanScan()
        {
            if (Mode == DiscoveryMode.Express) return _session.Gateway.PlanExpressScan(Recorder, CredentialList(), UseDefaultCredentials, UseHttps);
            return _session.Gateway.PlanAddressScan(Recorder, Addresses, LimitToDrivers, null, CredentialList(), UseDefaultCredentials, UseHttps);
        }

        /// <summary>Step: discover devices.</summary>
        public async Task<AdminError> ScanAsync(IProgress<OperationEvent> progress, CancellationToken ct)
        {
            ScanResults.Clear();
            Candidates.Clear();
            if (Mode == DiscoveryMode.KnownDriver)
            {
                var existing = new HashSet<string>(_session.Inventory.Hardware.Select(h => HardwareCsvValidator.TryParseAddress(h.Address, out var u) ? HardwareCsvValidator.NormalizeAddress(u) : h.Address), StringComparer.OrdinalIgnoreCase);
                foreach (var a in Addresses)
                {
                    var exists = existing.Contains(HardwareCsvValidator.NormalizeAddress(a));
                    Candidates.Add(new AddCandidate
                    {
                        Address = a.ToString(), ManualAddress = a, DriverName = SelectedDriver?.DisplayName, Selected = !exists, AlreadyExists = exists,
                        Status = exists ? "Already on the site" : "Will be added with the selected driver (no scan)"
                    });
                }

                return null;
            }

            var op = PlanScan();
            var outcome = await _runner.RunAsync(op, new OperationContext("scan for hardware", op.Target, Recorder.Name), progress, ct).ConfigureAwait(false);
            if (!outcome.Success && outcome.Result.Items.Count == 0) return outcome.Error;
            var results = outcome.Result.Objects.Select(DtoMapper.HardwareScanResult).ToList();
            var drivers = AvailableDrivers.ToDictionary(d => d.Path ?? string.Empty, d => d, StringComparer.OrdinalIgnoreCase);
            foreach (var s in results)
            {
                s.RecordingServerName = Recorder.Name;
                s.DriverName = s.HardwareDriverPath != null && drivers.TryGetValue(s.HardwareDriverPath, out var d) ? d.DisplayName : s.HardwareDriverPath;
                ScanResults.Add(s);
                var exists = s.MacAddressExistsLocal || s.MacAddressExistsGlobal;
                Candidates.Add(new AddCandidate
                {
                    ScanResult = s,
                    Address = s.HardwareAddress,
                    DriverName = s.DriverName,
                    MacAddress = s.MacAddress,
                    AlreadyExists = exists,
                    Selected = s.HardwareScanValidated && !exists,
                    Status = !s.HardwareScanValidated ? "Not identified: " + (string.IsNullOrEmpty(s.ErrorText) ? "no supported device responded" : s.ErrorText)
                        : exists ? "Already added (MAC address exists " + (s.MacAddressExistsLocal ? "on this recording server" : "on the site") + ")" : "Ready to add"
                });
            }

            return outcome.Error != null && results.Count == 0 ? outcome.Error : null;
        }

        /// <summary>Step: review planned additions.</summary>
        public ChangePlan BuildAddPlan()
        {
            var selected = Candidates.Where(c => c.Selected).ToList();
            var plan = new ChangePlan { Title = "Add hardware", OperationId = "hardware.add", Risk = RiskLevel.Create, WhatIfAvailable = false };
            plan.Summary.Add(new KeyValuePair<string, string>("Recording server", Recorder?.Name));
            plan.Summary.Add(new KeyValuePair<string, string>("Discovery", Mode == DiscoveryMode.Express ? "Express scan" : Mode == DiscoveryMode.Addresses ? "Address scan" : "Known driver (no scan)"));
            if (Mode == DiscoveryMode.KnownDriver) plan.Summary.Add(new KeyValuePair<string, string>("Driver", SelectedDriver?.DisplayName));
            plan.Summary.Add(new KeyValuePair<string, string>("Devices discovered", Mode == DiscoveryMode.KnownDriver ? "-" : ScanResults.Count.ToString(CultureInfo.InvariantCulture)));
            plan.Summary.Add(new KeyValuePair<string, string>("Hardware to add", selected.Count.ToString(CultureInfo.InvariantCulture)));
            foreach (var c in selected)
            {
                var item = new PlannedChange { TargetType = "Hardware", TargetName = c.Address, Operation = "Add" };
                item.Properties.Add(new PropertyChange { Property = "Address", DisplayName = "Address", New = c.Address });
                item.Properties.Add(new PropertyChange { Property = "Driver", DisplayName = "Driver", New = c.DriverName });
                item.Properties.Add(new PropertyChange { Property = "Name", DisplayName = "Hardware name", New = string.IsNullOrWhiteSpace(c.HardwareName) ? "(default: model and address)" : c.HardwareName });
                plan.Items.Add(item);
            }

            // Behaviour verified in MilestonePSTools/Public/Add-VmsHardware.ps1 (Set-NewHardwareConfig).
            plan.Warnings.Add("MilestonePSTools enables the new hardware and only camera channel 1 (channel 0). When a hardware name is given, child devices are renamed \"<name> - Camera N\". You can change channels in the next step.");
            plan.Warnings.Add("Devices are not added to device groups (MilestonePSTools does not do this automatically).");
            plan.Warnings.Add("PowerShell -WhatIf is not available for Add-VmsHardware; this is the application's validated preview.");
            plan.ScriptPreview = _scripts.Script(selected.Select(PlanAdd));
            return plan;
        }

        private SecureString FirstPassword(out string user)
        {
            var c = Credentials.FirstOrDefault(x => !string.IsNullOrEmpty(x.UserName) && x.Password != null);
            user = c?.UserName;
            return c?.Password;
        }

        public VmsOperation PlanAdd(AddCandidate c)
        {
            if (c.ScanResult != null) return _session.Gateway.PlanAddHardwareFromScan(new[] { c.ScanResult }, c.HardwareName);
            var pwd = FirstPassword(out var user);
            if (pwd == null) throw new InvalidOperationException("A device credential is required when adding with a known driver.");
            if (SelectedDriver?.Number == null) throw new InvalidOperationException("Select a driver.");
            return _session.Gateway.PlanAddHardwareManual(Recorder, c.ManualAddress, c.HardwareName, SelectedDriver.Number.Value, user, pwd);
        }

        /// <summary>Step: apply (add hardware), per device.</summary>
        public async Task<BulkResult<AddCandidate>> AddAsync(IProgress<BulkProgress> progress, CancellationToken ct)
        {
            AddedHardware.Clear();
            var selected = Candidates.Where(c => c.Selected).ToList();
            return await _bulk.RunAsync("Add hardware", selected, PlanAdd, c => c.Address, c => new OperationContext("add hardware", c.Address, Recorder.Name), progress, ct,
                (c, outcome) =>
                {
                    if (outcome.Success) AddedHardware.AddRange(outcome.Result.Objects.Select(DtoMapper.Hardware));
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
        }

        /// <summary>Step: load channels of the newly added hardware so the administrator can choose names/enabled/storage.</summary>
        public async Task<AdminError> LoadChannelsAsync(CancellationToken ct)
        {
            Channels.Clear();
            if (AddedHardware.Count == 0) return null;
            var r = await _session.Gateway.GetCamerasAsync(AddedHardware, ct).ConfigureAwait(false);
            if (!r.Success) return ErrorTranslator.Translate(r.Raw, new OperationContext("read the new cameras", null, Recorder.Name));
            foreach (var cam in r.Items.OrderBy(c => c.ParentItemPath).ThenBy(c => c.Channel))
            {
                var hw = AddedHardware.FirstOrDefault(h => h.Path == cam.ParentItemPath);
                cam.HardwareName = hw?.Name;
                cam.HardwareAddress = hw?.Address;
                cam.RecordingServerName = Recorder.Name;
                Channels.Add(new ChannelSetting { Camera = cam, HardwareName = hw?.Name, Channel = cam.Channel ?? 0, Enabled = cam.Enabled ?? false, Name = cam.Name });
            }

            _session.Register(r.Items);
            _session.Register(AddedHardware);
            return null;
        }

        /// <summary>Step: review channel configuration changes (names, enabled, storage).</summary>
        public (ChangePlan Plan, List<VmsOperation> Operations) BuildChannelPlan()
        {
            var plan = new ChangePlan { Title = "Configure new cameras", OperationId = "cameras.update", Risk = RiskLevel.Modify, WhatIfAvailable = true };
            var ops = new List<VmsOperation>();
            foreach (var ch in Channels)
            {
                var item = new PlannedChange { TargetType = "Camera", TargetId = ch.Camera.Id, TargetName = ch.Camera.Name, TargetHandle = ch.Camera.Handle, Operation = "Update" };
                item.Properties.Add(new PropertyChange { Property = "Name", DisplayName = "Camera name", Current = ch.Camera.Name, New = ch.Name });
                item.Properties.Add(new PropertyChange { Property = "Enabled", DisplayName = "Enabled", Current = ch.Camera.Enabled, New = ch.Enabled });
                var changes = item.EffectiveChanges.ToDictionary(p => p.Property, p => p.New);
                if (changes.Count > 0) ops.Add(_session.Gateway.PlanSetCamera(ch.Camera, changes));
                if (!string.IsNullOrEmpty(ch.Storage))
                {
                    item.Properties.Add(new PropertyChange { Property = "Storage", DisplayName = "Recording storage", Current = "(default)", New = ch.Storage });
                    ops.Add(_session.Gateway.PlanSetDeviceStorage(ch.Camera, ch.Storage));
                }

                plan.Items.Add(item);
            }

            plan.Summary.Add(new KeyValuePair<string, string>("New cameras", Channels.Count.ToString(CultureInfo.InvariantCulture)));
            plan.Summary.Add(new KeyValuePair<string, string>("Cameras to enable", Channels.Count(c => c.Enabled).ToString(CultureInfo.InvariantCulture)));
            plan.Summary.Add(new KeyValuePair<string, string>("Changes", ops.Count.ToString(CultureInfo.InvariantCulture)));
            plan.ScriptPreview = _scripts.Script(ops);
            return (plan, ops);
        }

        public Task<BulkResult<VmsOperation>> ApplyChannelsAsync(List<VmsOperation> operations, IProgress<BulkProgress> progress, CancellationToken ct) =>
            _bulk.RunAsync("Configure new cameras", operations, o => o, o => $"{o.Target}: {o.Title}", o => new OperationContext(o.Title, o.Target, Recorder.Name), progress, ct);
    }

    /// <summary>Remove hardware (destructive: removes all child devices; recordings may be lost).</summary>
    public sealed class RemoveHardwareWorkflow
    {
        private readonly SessionContext _session;
        private readonly OperationRunner _runner;
        private readonly BulkOperationRunner _bulk;
        private readonly ScriptService _scripts;

        public RemoveHardwareWorkflow(SessionContext session, OperationRunner runner, BulkOperationRunner bulk, ScriptService scripts)
        {
            _session = session;
            _runner = runner;
            _bulk = bulk;
            _scripts = scripts;
        }

        public ChangePlan BuildPlan(IReadOnlyList<HardwareInfo> hardware)
        {
            var plan = new ChangePlan
            {
                Title = "Remove hardware",
                OperationId = "hardware.remove",
                Risk = RiskLevel.Delete,
                RequiresStrongConfirmation = true,
                ConfirmationPhrase = hardware.Count == 1 ? hardware[0].Name : $"DELETE {hardware.Count}",
                WhatIfAvailable = _session.Catalog?.Find("Remove-VmsHardware")?.SupportsShouldProcess == true
            };
            var cams = 0;
            foreach (var h in hardware)
            {
                var item = new PlannedChange { TargetType = "Hardware", TargetId = h.Id, TargetName = h.Name, TargetHandle = h.Handle, Operation = "Remove" };
                item.Properties.Add(new PropertyChange { Property = "Address", DisplayName = "Address", Current = h.Address, New = "(removed)" });
                item.Properties.Add(new PropertyChange { Property = "Cameras", DisplayName = "Cameras removed", Current = h.CameraCount, New = 0 });
                item.Properties.Add(new PropertyChange { Property = "RecordingServer", DisplayName = "Recording server", Current = h.RecordingServerName, New = h.RecordingServerName });
                cams += h.CameraCount;
                plan.Items.Add(item);
            }

            plan.Summary.Add(new KeyValuePair<string, string>("Operation", "Remove hardware and all child devices"));
            plan.Summary.Add(new KeyValuePair<string, string>("Hardware", hardware.Count.ToString(CultureInfo.InvariantCulture)));
            plan.Summary.Add(new KeyValuePair<string, string>("Cameras affected", cams.ToString(CultureInfo.InvariantCulture)));
            plan.Warnings.Add("Removing hardware removes all its cameras, microphones, speakers, metadata, inputs and outputs. Recordings of these devices may be deleted and cannot be restored by this application.");
            plan.Warnings.Add("Rules, views and permissions that reference these devices will lose them.");
            plan.ScriptPreview = _scripts.Script(hardware.Select(h => _session.Gateway.PlanRemoveHardware(h)));
            return plan;
        }

        public async Task<List<string>> RunWhatIfAsync(ChangePlan plan, IReadOnlyList<HardwareInfo> hardware, CancellationToken ct)
        {
            var lines = new List<string>();
            foreach (var h in hardware)
            {
                var outcome = await _runner.RunAsync(_session.Gateway.PlanRemoveHardware(h).AsWhatIf(), new OperationContext("preview hardware removal", h.Name, h.RecordingServerName), null, ct, quiet: true).ConfigureAwait(false);
                if (outcome.Success) lines.AddRange(outcome.Result.WhatIfLines);
                else lines.Add($"{h.Name}: preview failed - {outcome.Error?.Summary}");
            }

            plan.PreviewKind = PreviewKind.PowerShellWhatIf;
            plan.WhatIfOutput.Clear();
            plan.WhatIfOutput.AddRange(lines);
            return lines;
        }

        public Task<BulkResult<HardwareInfo>> ApplyAsync(IReadOnlyList<HardwareInfo> hardware, IProgress<BulkProgress> progress, CancellationToken ct) =>
            _bulk.RunAsync("Remove hardware", hardware, h => _session.Gateway.PlanRemoveHardware(h), h => $"{h.Name} ({h.Address})",
                h => new OperationContext("remove hardware", h.Name, h.RecordingServerName), progress, ct);
    }

    /// <summary>Enable or disable hardware (Set-VmsHardware, supports -WhatIf).</summary>
    public sealed class HardwareEditWorkflow
    {
        private readonly SessionContext _session;
        private readonly OperationRunner _runner;
        private readonly BulkOperationRunner _bulk;
        private readonly ScriptService _scripts;

        public HardwareEditWorkflow(SessionContext session, OperationRunner runner, BulkOperationRunner bulk, ScriptService scripts)
        {
            _session = session;
            _runner = runner;
            _bulk = bulk;
            _scripts = scripts;
        }

        public ChangePlan BuildEnablePlan(IReadOnlyList<HardwareInfo> hardware, bool enable)
        {
            var plan = new ChangePlan { Title = enable ? "Enable hardware" : "Disable hardware", OperationId = "hardware.update", Risk = RiskLevel.Modify, WhatIfAvailable = _session.Catalog?.Find("Set-VmsHardware")?.SupportsShouldProcess == true };
            foreach (var h in hardware)
            {
                var item = new PlannedChange { TargetType = "Hardware", TargetId = h.Id, TargetName = h.Name, TargetHandle = h.Handle, Operation = "Update" };
                item.Properties.Add(new PropertyChange { Property = "Enabled", DisplayName = "Enabled", Current = h.Enabled, New = enable });
                plan.Items.Add(item);
            }

            if (!enable) plan.Warnings.Add("Disabling hardware stops all of its devices from streaming and recording.");
            plan.RequiresStrongConfirmation = !enable && plan.ChangedItemCount >= 10;
            plan.ConfirmationPhrase = plan.RequiresStrongConfirmation ? plan.ChangedItemCount.ToString(CultureInfo.InvariantCulture) : null;
            plan.Summary.Add(new KeyValuePair<string, string>("Hardware to change", plan.ChangedItemCount.ToString(CultureInfo.InvariantCulture)));
            plan.ScriptPreview = _scripts.Script(hardware.Where(h => h.Enabled != enable).Select(h => _session.Gateway.PlanSetHardware(h, enable, null)));
            return plan;
        }

        public async Task<List<string>> RunEnableWhatIfAsync(ChangePlan plan, IReadOnlyList<HardwareInfo> hardware, bool enable, CancellationToken ct)
        {
            var lines = new List<string>();
            foreach (var h in hardware.Where(x => x.Enabled != enable))
            {
                var outcome = await _runner.RunAsync(_session.Gateway.PlanSetHardware(h, enable, null).AsWhatIf(), new OperationContext("preview hardware change", h.Name, h.RecordingServerName), null, ct, quiet: true).ConfigureAwait(false);
                if (outcome.Success) lines.AddRange(outcome.Result.WhatIfLines);
                else lines.Add($"{h.Name}: preview failed - {outcome.Error?.Summary}");
            }

            plan.PreviewKind = PreviewKind.PowerShellWhatIf;
            plan.WhatIfOutput.Clear();
            plan.WhatIfOutput.AddRange(lines);
            return lines;
        }

        public Task<BulkResult<HardwareInfo>> ApplyEnableAsync(IReadOnlyList<HardwareInfo> hardware, bool enable, IProgress<BulkProgress> progress, CancellationToken ct) =>
            _bulk.RunAsync(enable ? "Enable hardware" : "Disable hardware", hardware,
                h => h.Enabled == enable ? null : _session.Gateway.PlanSetHardware(h, enable, null), h => h.Name,
                h => new OperationContext(enable ? "enable hardware" : "disable hardware", h.Name, h.RecordingServerName), progress, ct,
                (h, o) =>
                {
                    if (o.Success) h.Enabled = enable;
                    return Task.CompletedTask;
                });
    }
}
