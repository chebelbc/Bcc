using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.PSTools.Commands
{
    /// <summary>
    /// Typed builders for every MilestonePSTools command the application invokes directly. Command and parameter
    /// names here are verified against the module catalog by CatalogDriftTests (see <see cref="Samples"/>).
    /// </summary>
    public static class PsCommands
    {
        /// <summary>Refer to an object by worker handle, falling back to type + Id when the handle is unknown.</summary>
        public static ParameterValue Ref(VmsObject o)
        {
            if (o == null) throw new ArgumentNullException(nameof(o));
            if (!string.IsNullOrEmpty(o.Handle)) return ParameterValue.Handle(o.Handle);
            if (o.ObjectType == "Storage" && !string.IsNullOrEmpty(o.Path)) return ParameterValue.Reference("Storage", o.Path);
            if (o.Id != Guid.Empty && !string.IsNullOrEmpty(o.ObjectType)) return ParameterValue.Reference(o.ObjectType, o.Id.ToString("D"));
            throw new InvalidOperationException($"'{o.Name}' cannot be referenced; refresh the data and try again.");
        }

        public static ParameterValue Refs(IEnumerable<VmsObject> objects) => ParameterValue.List(objects.Select(Ref));

        // ---------------------------------------------------------------- connection
        public static PipelineStep ConnectVms(Uri serverAddress, string credentialSecretId, string userName, bool basicUser, bool secureOnly, bool includeChildSites, bool acceptEula)
        {
            var s = new PipelineStep("Connect-Vms").With("ServerAddress", ParameterValue.String(serverAddress.ToString()));
            if (credentialSecretId != null) s.With("Credential", ParameterValue.Credential(userName, credentialSecretId));
            if (basicUser) s.With("BasicUser", ParameterValue.Switch());
            if (secureOnly) s.With("SecureOnly", ParameterValue.Switch());
            if (includeChildSites) s.With("IncludeChildSites", ParameterValue.Switch());
            if (acceptEula) s.With("AcceptEula", ParameterValue.Switch());
            return s;
        }

        public static PipelineStep DisconnectVms() => new PipelineStep("Disconnect-Vms");

        public static PipelineStep GetVmsManagementServer() => new PipelineStep("Get-VmsManagementServer");

        // ---------------------------------------------------------------- system / inventory
        public static PipelineStep GetVmsRecordingServer() => new PipelineStep("Get-VmsRecordingServer");

        /// <summary>All hardware. The module's default returns only enabled hardware, so EnableFilter=All is explicit.</summary>
        public static PipelineStep GetVmsHardware(RecordingServerInfo recorder = null)
        {
            var s = new PipelineStep("Get-VmsHardware");
            if (recorder != null) s.With("RecordingServer", Ref(recorder));
            return s.With("EnableFilter", ParameterValue.String("All"));
        }

        /// <summary>All cameras (enabled and disabled; the module default is enabled only).</summary>
        public static PipelineStep GetVmsCamera(IEnumerable<HardwareInfo> hardware = null)
        {
            var s = new PipelineStep("Get-VmsCamera");
            var list = hardware?.ToList();
            if (list != null && list.Count > 0) s.With("Hardware", Refs(list));
            return s.With("EnableFilter", ParameterValue.String("All"));
        }

        public static PipelineStep GetVmsStorage(RecordingServerInfo recorder = null)
        {
            var s = new PipelineStep("Get-VmsStorage");
            if (recorder != null) s.With("RecordingServer", ParameterValue.List(new[] { Ref(recorder) }));
            return s;
        }

        public static PipelineStep GetVmsDeviceStatus(IEnumerable<Guid> recordingServerIds = null, IEnumerable<string> deviceTypes = null)
        {
            var s = new PipelineStep("Get-VmsDeviceStatus");
            var ids = recordingServerIds?.ToList();
            if (ids != null && ids.Count > 0) s.With("RecordingServerId", ParameterValue.List(ids.Select(ParameterValue.Guid)));
            var types = deviceTypes?.ToList();
            if (types != null && types.Count > 0) s.With("DeviceType", ParameterValue.StringList(types));
            return s;
        }

        public static PipelineStep GetVmsHardwareDriver(RecordingServerInfo recorder) =>
            new PipelineStep("Get-VmsHardwareDriver").With("RecordingServer", ParameterValue.List(new[] { Ref(recorder) }));

        public static PipelineStep GetVmsRole() => new PipelineStep("Get-VmsRole");

        public static PipelineStep GetVmsBasicUser() => new PipelineStep("Get-VmsBasicUser");

        public static PipelineStep GetVmsRule() => new PipelineStep("Get-VmsRule");

        public static PipelineStep GetVmsAlarmDefinition() => new PipelineStep("Get-VmsAlarmDefinition");

        public static PipelineStep GetVmsViewGroup(bool recurse = true)
        {
            var s = new PipelineStep("Get-VmsViewGroup");
            if (recurse) s.With("Recurse", ParameterValue.Switch());
            return s;
        }

        public static PipelineStep GetVmsClientProfile() => new PipelineStep("Get-VmsClientProfile");

        public static PipelineStep GetVmsLicenseOverview() => new PipelineStep("Get-VmsLicenseOverview");

        public static PipelineStep GetVmsCameraReport(bool includeRetentionInfo, bool includeRecordingStats)
        {
            var s = new PipelineStep("Get-VmsCameraReport");
            if (includeRetentionInfo) s.With("IncludeRetentionInfo", ParameterValue.Switch());
            if (includeRecordingStats) s.With("IncludeRecordingStats", ParameterValue.Switch());
            return s.With("EnableFilter", ParameterValue.String("All"));
        }

        // ---------------------------------------------------------------- hardware
        public static PipelineStep StartVmsHardwareScanExpress(IEnumerable<RecordingServerInfo> recorders, IReadOnlyList<(string User, string SecretId)> credentials, bool useDefaultCredentials, bool useHttps)
        {
            var s = new PipelineStep("Start-VmsHardwareScan").With("RecordingServer", Refs(recorders)).With("Express", ParameterValue.Switch());
            return ScanOptions(s, credentials, useDefaultCredentials, useHttps);
        }

        public static PipelineStep StartVmsHardwareScanManual(IEnumerable<RecordingServerInfo> recorders, IEnumerable<Uri> addresses, IEnumerable<int> driverNumbers,
            IEnumerable<string> driverFamilies, IReadOnlyList<(string User, string SecretId)> credentials, bool useDefaultCredentials, bool useHttps)
        {
            var s = new PipelineStep("Start-VmsHardwareScan").With("RecordingServer", Refs(recorders));
            s.With("Address", ParameterValue.StringList(addresses.Select(a => a.ToString())));
            var dn = driverNumbers?.ToList();
            if (dn != null && dn.Count > 0) s.With("DriverNumber", ParameterValue.List(dn.Select(d => ParameterValue.Integer(d))));
            var df = driverFamilies?.ToList();
            if (df != null && df.Count > 0) s.With("DriverFamily", ParameterValue.StringList(df));
            return ScanOptions(s, credentials, useDefaultCredentials, useHttps);
        }

        private static PipelineStep ScanOptions(PipelineStep s, IReadOnlyList<(string User, string SecretId)> credentials, bool useDefaultCredentials, bool useHttps)
        {
            if (credentials != null && credentials.Count > 0)
                s.With("Credential", ParameterValue.List(credentials.Select(c => ParameterValue.Credential(c.User, c.SecretId))));
            if (useDefaultCredentials) s.With("UseDefaultCredentials", ParameterValue.Switch());
            if (useHttps) s.With("UseHttps", ParameterValue.Switch());
            return s;
        }

        public static PipelineStep AddVmsHardwareFromScan(IEnumerable<HardwareScanResultInfo> scanResults, string name, bool skipConfig, bool force)
        {
            var s = new PipelineStep("Add-VmsHardware").With("HardwareScan", Refs(scanResults));
            if (!string.IsNullOrWhiteSpace(name)) s.With("Name", ParameterValue.String(name));
            if (skipConfig) s.With("SkipConfig", ParameterValue.Switch());
            if (force) s.With("Force", ParameterValue.Switch());
            return s;
        }

        public static PipelineStep AddVmsHardwareManual(RecordingServerInfo recorder, Uri address, string name, int driverNumber, string userName, string secretId, bool skipConfig, bool force)
        {
            var s = new PipelineStep("Add-VmsHardware")
                .With("RecordingServer", Ref(recorder))
                .With("HardwareAddress", ParameterValue.String(address.ToString()))
                .With("DriverNumber", ParameterValue.Integer(driverNumber))
                .With("Credential", ParameterValue.Credential(userName, secretId));
            if (!string.IsNullOrWhiteSpace(name)) s.With("Name", ParameterValue.String(name));
            if (skipConfig) s.With("SkipConfig", ParameterValue.Switch());
            if (force) s.With("Force", ParameterValue.Switch());
            return s;
        }

        public static PipelineStep SetVmsHardware(IEnumerable<HardwareInfo> hardware, bool? enabled = null, string name = null, string description = null, bool passThru = true)
        {
            var s = new PipelineStep("Set-VmsHardware").With("Hardware", Refs(hardware));
            if (enabled.HasValue) s.With("Enabled", ParameterValue.Boolean(enabled.Value));
            if (name != null) s.With("Name", ParameterValue.String(name));
            if (description != null) s.With("Description", ParameterValue.String(description));
            if (passThru) s.With("PassThru", ParameterValue.Switch());
            return s;
        }

        public static PipelineStep RemoveVmsHardware(IEnumerable<HardwareInfo> hardware) => new PipelineStep("Remove-VmsHardware").With("Hardware", Refs(hardware));

        public static PipelineStep GetVmsHardwarePassword(HardwareInfo hardware) => new PipelineStep("Get-VmsHardwarePassword").With("Hardware", Ref(hardware));

        /// <summary>
        /// Import-VmsHardware with a MilestonePSTools workbook (.xlsx exported by Export-VmsHardware) or CSV file. The module
        /// itself parses the file (docs/commands/en-US/Import-VmsHardware.md).
        /// </summary>
        public static PipelineStep ImportVmsHardware(string path, RecordingServerInfo recorder, string userName, string credentialSecretId, bool updateExisting)
        {
            var s = new PipelineStep("Import-VmsHardware").With("LiteralPath", ParameterValue.StringList(new[] { path }));
            if (recorder != null) s.With("RecordingServer", Ref(recorder));
            if (credentialSecretId != null) s.With("Credential", ParameterValue.Credential(userName, credentialSecretId));
            if (updateExisting) s.With("UpdateExisting", ParameterValue.Switch());
            return s;
        }

        // ---------------------------------------------------------------- device groups
        /// <summary>Returns the camera group at the path, creating missing levels (New-VmsDeviceGroup -Path is idempotent).</summary>
        public static PipelineStep NewVmsDeviceGroupPath(string path) => new PipelineStep("New-VmsDeviceGroup").With("Path", ParameterValue.StringList(new[] { path }));

        /// <summary>Adds devices to the group received from the pipeline (Add-VmsDeviceGroupMember -Group is ValueFromPipeline).</summary>
        public static PipelineStep AddVmsDeviceGroupMember(IEnumerable<Guid> deviceIds) =>
            new PipelineStep("Add-VmsDeviceGroupMember").With("DeviceId", ParameterValue.List(deviceIds.Select(ParameterValue.Guid)));

        // ---------------------------------------------------------------- sites
        public static PipelineStep GetVmsSite(bool listAvailable)
        {
            var s = new PipelineStep("Get-VmsSite");
            if (listAvailable) s.With("ListAvailable", ParameterValue.Switch());
            return s;
        }

        public static PipelineStep SelectVmsSite(VmsObject site) => new PipelineStep("Select-VmsSite").With("Site", Ref(site));

        /// <summary>
        /// Lists objects of one type for a picker with the type's producer from classification-rules.json (a read-only
        /// Get-* command). Device producers get -EnableFilter All so disabled devices can be picked too.
        /// </summary>
        public static PipelineStep Producer(string command, bool allEnableStates)
        {
            var s = new PipelineStep(command);
            if (allEnableStates) s.With("EnableFilter", ParameterValue.String("All"));
            return s;
        }

        /// <summary>All camera group paths ("/first/second"), for picking a device group.</summary>
        public static IEnumerable<PipelineStep> CameraGroupPaths() => new[]
        {
            new PipelineStep("Get-VmsDeviceGroup").With("Recurse", ParameterValue.Switch()),
            new PipelineStep("Resolve-VmsDeviceGroupPath").With("NoTypePrefix", ParameterValue.Switch())
        };

        // ---------------------------------------------------------------- roles
        public static PipelineStep GetVmsRoleMember(VmsObject role) => new PipelineStep("Get-VmsRoleMember").With("Role", Refs(new[] { role }));

        // ---------------------------------------------------------------- cameras
        /// <summary>Properties of Set-VmsCamera exposed by the camera editor, with their value types.</summary>
        public static readonly IReadOnlyDictionary<string, Type> CameraSettableProperties = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            { "Name", typeof(string) },
            { "ShortName", typeof(string) },
            { "Description", typeof(string) },
            { "Enabled", typeof(bool) },
            { "RecordingEnabled", typeof(bool) },
            { "RecordingFramerate", typeof(double) },
            { "RecordKeyframesOnly", typeof(bool) },
            { "PrebufferEnabled", typeof(bool) },
            { "PrebufferSeconds", typeof(int) }
        };

        public static PipelineStep SetVmsCamera(IEnumerable<CameraInfo> cameras, IReadOnlyDictionary<string, object> properties, bool passThru = true)
        {
            var s = new PipelineStep("Set-VmsCamera").With("InputObject", Refs(cameras));
            foreach (var kv in properties)
            {
                if (!CameraSettableProperties.TryGetValue(kv.Key, out var t)) throw new ArgumentException($"'{kv.Key}' is not a supported camera property.");
                s.With(kv.Key, Literal(kv.Value, t));
            }

            if (passThru) s.With("PassThru", ParameterValue.Switch());
            return s;
        }

        public static PipelineStep SetVmsDeviceStorage(IEnumerable<CameraInfo> devices, string destinationStorageName, bool passThru = false)
        {
            var s = new PipelineStep("Set-VmsDeviceStorage").With("Device", Refs(devices)).With("Destination", ParameterValue.String(destinationStorageName));
            if (passThru) s.With("PassThru", ParameterValue.Switch());
            return s;
        }

        public static ParameterValue Literal(object value, Type type)
        {
            if (value == null) return ParameterValue.FromLiteral(JsonNull.Instance);
            if (type == typeof(bool)) return ParameterValue.Boolean(Convert.ToBoolean(value, CultureInfo.InvariantCulture));
            if (type == typeof(int) || type == typeof(long)) return ParameterValue.Integer(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            if (type == typeof(double)) return ParameterValue.Number(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            return ParameterValue.String(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        /// <summary>One sample invocation of every builder, for the catalog drift test.</summary>
        public static IEnumerable<PipelineStep> Samples()
        {
            var rs = new RecordingServerInfo { Handle = "h1", ObjectType = "RecordingServer", Name = "RS" };
            var hw = new HardwareInfo { Handle = "h2", ObjectType = "Hardware", Name = "HW" };
            var cam = new CameraInfo { Handle = "h3", ObjectType = "Camera", Name = "Cam" };
            var scan = new HardwareScanResultInfo { Handle = "h4", ObjectType = "VmsHardwareScanResult" };
            var creds = new List<(string, string)> { ("root", "s1") };
            yield return ConnectVms(new Uri("http://server"), "s1", "user", true, true, true, true);
            yield return DisconnectVms();
            yield return GetVmsManagementServer();
            yield return GetVmsRecordingServer();
            yield return GetVmsHardware(rs);
            yield return GetVmsCamera(new[] { hw });
            yield return GetVmsStorage(rs);
            yield return GetVmsDeviceStatus(new[] { Guid.NewGuid() }, new[] { "Camera" });
            yield return GetVmsHardwareDriver(rs);
            yield return GetVmsRole();
            yield return GetVmsBasicUser();
            yield return GetVmsRule();
            yield return GetVmsAlarmDefinition();
            yield return GetVmsViewGroup();
            yield return GetVmsClientProfile();
            yield return GetVmsLicenseOverview();
            yield return GetVmsCameraReport(true, true);
            yield return StartVmsHardwareScanExpress(new[] { rs }, creds, true, true);
            yield return StartVmsHardwareScanManual(new[] { rs }, new[] { new Uri("http://10.0.0.1") }, new[] { 806 }, new[] { "AXIS" }, creds, true, true);
            yield return AddVmsHardwareFromScan(new[] { scan }, "n", true, true);
            yield return AddVmsHardwareManual(rs, new Uri("http://10.0.0.1"), "n", 806, "root", "s1", true, true);
            yield return SetVmsHardware(new[] { hw }, true, "n", "d");
            yield return RemoveVmsHardware(new[] { hw });
            yield return GetVmsHardwarePassword(hw);
            yield return SetVmsCamera(new[] { cam }, CameraSettableProperties.ToDictionary(k => k.Key, k => k.Value == typeof(string) ? (object)"x" : k.Value == typeof(bool) ? (object)true : k.Value == typeof(int) ? (object)1 : 1.0));
            yield return SetVmsDeviceStorage(new[] { cam }, "Storage", true);
            yield return ImportVmsHardware("C:\\import.xlsx", rs, "root", "s1", true);
            yield return NewVmsDeviceGroupPath("/Imported cameras");
            yield return AddVmsDeviceGroupMember(new[] { Guid.NewGuid() });
            yield return GetVmsSite(true);
            yield return SelectVmsSite(new GenericItem { Handle = "h5", ObjectType = "Site", Name = "Site" });
            yield return GetVmsRoleMember(new GenericItem { Handle = "h6", ObjectType = "Role", Name = "Role" });
            yield return Producer("Get-VmsMicrophone", true);
            foreach (var step in CameraGroupPaths()) yield return step;
        }
    }
}
