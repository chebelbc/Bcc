using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.Protocol.Json;
using MilestoneAdmin.PSTools.Commands;
using MilestoneAdmin.PSTools.Execution;
using MilestoneAdmin.PSTools.Mapping;

namespace MilestoneAdmin.PSTools
{
    public sealed class QueryResult<T>
    {
        public QueryResult(VmsOperation operation, OperationResult raw, List<T> items)
        {
            Operation = operation;
            Raw = raw;
            Items = items;
        }

        public VmsOperation Operation { get; }

        public OperationResult Raw { get; }

        public List<T> Items { get; }

        public bool Success => Raw.Success;
    }

    public enum ConnectionAuthMode
    {
        /// <summary>The Windows account running the application (no password is handled by the application).</summary>
        CurrentWindowsUser,

        /// <summary>A different Windows/AD account.</summary>
        WindowsUser,

        /// <summary>An XProtect basic user.</summary>
        BasicUser
    }

    public sealed class ConnectionRequest
    {
        public Uri ServerAddress { get; set; }
        public ConnectionAuthMode AuthMode { get; set; }
        public string UserName { get; set; }
        public SecureString Password { get; set; }
        public bool SecureOnly { get; set; }
        public bool IncludeChildSites { get; set; }
        public bool AcceptEula { get; set; }
    }

    /// <summary>Read-only inventory query definition for generic list pages.</summary>
    public sealed class GenericQuery
    {
        public string Title { get; set; }
        public string ObjectType { get; set; }
        public Func<PipelineStep> Step { get; set; }
        public string[] Properties { get; set; } = Array.Empty<string>();
        public string RequiredCommand { get; set; }
    }

    public interface IVmsGateway
    {
        IPowerShellExecutor Executor { get; }

        Task<OperationResult> ExecuteAsync(VmsOperation operation, IProgress<OperationEvent> progress, CancellationToken ct);

        VmsOperation PlanConnect(ConnectionRequest request);

        Task<ManagementServerInfo> GetManagementServerAsync(CancellationToken ct);

        Task<QueryResult<RecordingServerInfo>> GetRecordingServersAsync(CancellationToken ct);

        Task<QueryResult<HardwareInfo>> GetHardwareAsync(RecordingServerInfo recorder, CancellationToken ct);

        Task<QueryResult<CameraInfo>> GetCamerasAsync(IEnumerable<HardwareInfo> hardware, CancellationToken ct);

        Task<QueryResult<StorageInfo>> GetStorageAsync(RecordingServerInfo recorder, CancellationToken ct);

        Task<QueryResult<DeviceStatusInfo>> GetDeviceStatusAsync(IProgress<OperationEvent> progress, CancellationToken ct);

        Task<QueryResult<HardwareDriverInfo>> GetHardwareDriversAsync(RecordingServerInfo recorder, CancellationToken ct);

        Task<QueryResult<RoleInfo>> GetRolesAsync(CancellationToken ct);

        Task<QueryResult<GenericItem>> QueryAsync(GenericQuery query, CancellationToken ct);

        VmsOperation PlanExpressScan(RecordingServerInfo recorder, IReadOnlyList<(string User, SecureString Password)> credentials, bool useDefaultCredentials, bool useHttps);

        VmsOperation PlanAddressScan(RecordingServerInfo recorder, IEnumerable<Uri> addresses, IEnumerable<int> driverNumbers, IEnumerable<string> driverFamilies,
            IReadOnlyList<(string User, SecureString Password)> credentials, bool useDefaultCredentials, bool useHttps);

        Task<QueryResult<HardwareScanResultInfo>> RunScanAsync(VmsOperation scan, IProgress<OperationEvent> progress, CancellationToken ct);

        VmsOperation PlanAddHardwareFromScan(IReadOnlyList<HardwareScanResultInfo> results, string name);

        VmsOperation PlanAddHardwareManual(RecordingServerInfo recorder, Uri address, string name, int driverNumber, string userName, SecureString password);

        VmsOperation PlanSetCamera(CameraInfo camera, IReadOnlyDictionary<string, object> properties);

        VmsOperation PlanSetHardware(HardwareInfo hardware, bool? enabled, string name);

        VmsOperation PlanRemoveHardware(HardwareInfo hardware);

        VmsOperation PlanSetDeviceStorage(CameraInfo camera, string storageName);

        VmsOperation PlanRevealHardwarePassword(HardwareInfo hardware);
    }

    /// <summary>XProtect access through MilestonePSTools. Contains all knowledge of which command does what.</summary>
    public sealed class VmsGateway : IVmsGateway
    {
        public static readonly string[] RecordingServerProperties = { "Name", "Id", "Path", "HostName", "WebServerUri", "ActiveWebServerUri", "Enabled", "Description", "PublicAccessEnabled", "LastModified" };
        public static readonly string[] HardwareProperties = { "Name", "Id", "Path", "Address", "Model", "Enabled", "UserName", "Description", "HardwareDriverPath", "ParentItemPath", "LastModified" };
        public static readonly string[] CameraProperties =
        {
            "Name", "Id", "Path", "ShortName", "Description", "Enabled", "Channel", "ParentItemPath", "RecordingEnabled", "RecordingFramerate",
            "RecordKeyframesOnly", "PrebufferEnabled", "PrebufferSeconds", "RecordingStorage", "GisPoint", "LastModified"
        };
        public static readonly string[] StorageProperties = { "Name", "Id", "Path", "DiskPath", "RetainMinutes", "MaxSize", "IsDefault", "ParentItemPath", "Description" };
        public static readonly string[] DriverProperties = { "Name", "Id", "Path", "Number", "GroupName", "DriverType" };

        public VmsGateway(IPowerShellExecutor executor)
        {
            Executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        public IPowerShellExecutor Executor { get; }

        public Task<OperationResult> ExecuteAsync(VmsOperation operation, IProgress<OperationEvent> progress, CancellationToken ct) => Executor.InvokeAsync(operation, progress, ct);

        private static VmsOperation Read(string title, PipelineStep step, string[] properties, string targetType = null, int timeoutSeconds = 300)
        {
            var op = new VmsOperation { Title = title, Risk = RiskLevel.ReadOnly, TargetType = targetType, TimeoutSeconds = timeoutSeconds };
            op.Pipeline.Add(step);
            if (properties != null) op.Output.Properties.AddRange(properties);
            return op;
        }

        private async Task<QueryResult<T>> QueryAsync<T>(VmsOperation op, Func<JsonObject, T> map, IProgress<OperationEvent> progress, CancellationToken ct)
        {
            var r = await Executor.InvokeAsync(op, progress, ct).ConfigureAwait(false);
            var items = r.Objects.Select(map).ToList();
            return new QueryResult<T>(op, r, items);
        }

        public VmsOperation PlanConnect(ConnectionRequest request)
        {
            var op = new VmsOperation
            {
                Title = "Connect to management server",
                Target = request.ServerAddress.ToString(),
                TargetType = "ManagementServer",
                Risk = RiskLevel.Session,
                TimeoutSeconds = 180
            };
            string secretId = null;
            string user = null;
            if (request.AuthMode != ConnectionAuthMode.CurrentWindowsUser)
            {
                user = request.UserName;
                secretId = op.AddSecret(request.Password, request.AuthMode == ConnectionAuthMode.BasicUser ? "XProtect basic user password" : "Windows account password");
            }

            op.Pipeline.Add(PsCommands.ConnectVms(request.ServerAddress, secretId, user, request.AuthMode == ConnectionAuthMode.BasicUser, request.SecureOnly, request.IncludeChildSites, request.AcceptEula));
            op.Output.Properties.AddRange(new[] { "Name", "ComputerName", "Version", "Id" });
            return op;
        }

        public async Task<ManagementServerInfo> GetManagementServerAsync(CancellationToken ct)
        {
            var r = await QueryAsync(Read("Read management server", PsCommands.GetVmsManagementServer(), new[] { "Name", "ComputerName", "Version", "Id", "Description" }),
                DtoMapper.ManagementServer, null, ct).ConfigureAwait(false);
            if (!r.Success) throw new VmsOperationException(r.Raw);
            return r.Items.FirstOrDefault();
        }

        public Task<QueryResult<RecordingServerInfo>> GetRecordingServersAsync(CancellationToken ct) =>
            QueryAsync(Read("Retrieve recording servers", PsCommands.GetVmsRecordingServer(), RecordingServerProperties, "RecordingServer"), DtoMapper.RecordingServer, null, ct);

        public Task<QueryResult<HardwareInfo>> GetHardwareAsync(RecordingServerInfo recorder, CancellationToken ct) =>
            QueryAsync(Read(recorder == null ? "Retrieve hardware" : $"Retrieve hardware from {recorder.Name}", PsCommands.GetVmsHardware(recorder), HardwareProperties, "Hardware", 900),
                DtoMapper.Hardware, null, ct);

        public Task<QueryResult<CameraInfo>> GetCamerasAsync(IEnumerable<HardwareInfo> hardware, CancellationToken ct) =>
            QueryAsync(Read("Retrieve cameras", PsCommands.GetVmsCamera(hardware), CameraProperties, "Camera", 900), DtoMapper.Camera, null, ct);

        public Task<QueryResult<StorageInfo>> GetStorageAsync(RecordingServerInfo recorder, CancellationToken ct) =>
            QueryAsync(Read("Retrieve storage", PsCommands.GetVmsStorage(recorder), StorageProperties, "Storage"), DtoMapper.Storage, null, ct);

        public Task<QueryResult<DeviceStatusInfo>> GetDeviceStatusAsync(IProgress<OperationEvent> progress, CancellationToken ct)
        {
            var op = Read("Retrieve live device status", PsCommands.GetVmsDeviceStatus(), null, "Camera", 600);
            op.Output.RegisterHandles = false;
            return QueryAsync(op, DtoMapper.DeviceStatus, progress, ct);
        }

        public Task<QueryResult<HardwareDriverInfo>> GetHardwareDriversAsync(RecordingServerInfo recorder, CancellationToken ct) =>
            QueryAsync(Read($"Retrieve hardware drivers on {recorder.Name}", PsCommands.GetVmsHardwareDriver(recorder), DriverProperties, "HardwareDriver"), DtoMapper.HardwareDriver, null, ct);

        public Task<QueryResult<RoleInfo>> GetRolesAsync(CancellationToken ct) =>
            QueryAsync(Read("Retrieve roles", PsCommands.GetVmsRole(), new[] { "Name", "Id", "Path", "RoleType", "Description" }, "Role"), DtoMapper.Role, null, ct);

        public Task<QueryResult<GenericItem>> QueryAsync(GenericQuery query, CancellationToken ct) =>
            QueryAsync(Read(query.Title, query.Step(), query.Properties.Length > 0 ? query.Properties : null, query.ObjectType), o => DtoMapper.Generic(o, query.ObjectType), null, ct);

        private static List<(string, string)> AddCredentials(VmsOperation op, IReadOnlyList<(string User, SecureString Password)> credentials)
        {
            var list = new List<(string, string)>();
            if (credentials == null) return list;
            foreach (var c in credentials) list.Add((c.User, op.AddSecret(c.Password, $"Device password for '{c.User}'")));
            return list;
        }

        public VmsOperation PlanExpressScan(RecordingServerInfo recorder, IReadOnlyList<(string User, SecureString Password)> credentials, bool useDefaultCredentials, bool useHttps)
        {
            var op = new VmsOperation { Title = $"Express hardware scan on {recorder.Name}", Target = recorder.Name, TargetType = "RecordingServer", Risk = RiskLevel.Operational, AllowStateChange = true, TimeoutSeconds = 1800 };
            op.Pipeline.Add(PsCommands.StartVmsHardwareScanExpress(new[] { recorder }, AddCredentials(op, credentials), useDefaultCredentials, useHttps));
            return op;
        }

        public VmsOperation PlanAddressScan(RecordingServerInfo recorder, IEnumerable<Uri> addresses, IEnumerable<int> driverNumbers, IEnumerable<string> driverFamilies,
            IReadOnlyList<(string User, SecureString Password)> credentials, bool useDefaultCredentials, bool useHttps)
        {
            var list = addresses.ToList();
            var op = new VmsOperation
            {
                Title = $"Hardware scan of {list.Count} address(es) on {recorder.Name}", Target = string.Join(", ", list.Take(5)) + (list.Count > 5 ? ", ..." : string.Empty),
                TargetType = "RecordingServer", Risk = RiskLevel.Operational, AllowStateChange = true, TimeoutSeconds = 1800
            };
            op.Pipeline.Add(PsCommands.StartVmsHardwareScanManual(new[] { recorder }, list, driverNumbers, driverFamilies, AddCredentials(op, credentials), useDefaultCredentials, useHttps));
            return op;
        }

        public Task<QueryResult<HardwareScanResultInfo>> RunScanAsync(VmsOperation scan, IProgress<OperationEvent> progress, CancellationToken ct) =>
            QueryAsync(scan, DtoMapper.HardwareScanResult, progress, ct);

        public VmsOperation PlanAddHardwareFromScan(IReadOnlyList<HardwareScanResultInfo> results, string name)
        {
            var op = new VmsOperation
            {
                Title = "Add hardware",
                Target = results.Count == 1 ? results[0].HardwareAddress : $"{results.Count} devices",
                TargetType = "Hardware", Risk = RiskLevel.Create, AllowStateChange = true, TimeoutSeconds = 1800
            };
            op.Pipeline.Add(PsCommands.AddVmsHardwareFromScan(results, name, skipConfig: false, force: false));
            op.Output.Properties.AddRange(HardwareProperties);
            return op;
        }

        public VmsOperation PlanAddHardwareManual(RecordingServerInfo recorder, Uri address, string name, int driverNumber, string userName, SecureString password)
        {
            var op = new VmsOperation { Title = "Add hardware", Target = address.ToString(), TargetType = "Hardware", Risk = RiskLevel.Create, AllowStateChange = true, TimeoutSeconds = 900 };
            var secret = op.AddSecret(password, $"Device password for {address.Host}");
            op.Pipeline.Add(PsCommands.AddVmsHardwareManual(recorder, address, name, driverNumber, userName, secret, skipConfig: false, force: false));
            op.Output.Properties.AddRange(HardwareProperties);
            return op;
        }

        public VmsOperation PlanSetCamera(CameraInfo camera, IReadOnlyDictionary<string, object> properties)
        {
            var op = new VmsOperation { Title = "Update camera", Target = camera.Name, TargetType = "Camera", Risk = RiskLevel.Modify, AllowStateChange = true };
            op.Pipeline.Add(PsCommands.SetVmsCamera(new[] { camera }, properties));
            op.Output.Properties.AddRange(CameraProperties);
            return op;
        }

        public VmsOperation PlanSetHardware(HardwareInfo hardware, bool? enabled, string name)
        {
            var op = new VmsOperation { Title = "Update hardware", Target = hardware.Name, TargetType = "Hardware", Risk = RiskLevel.Modify, AllowStateChange = true };
            op.Pipeline.Add(PsCommands.SetVmsHardware(new[] { hardware }, enabled, name));
            op.Output.Properties.AddRange(HardwareProperties);
            return op;
        }

        public VmsOperation PlanRemoveHardware(HardwareInfo hardware)
        {
            var op = new VmsOperation
            {
                Title = "Remove hardware and all child devices", Target = $"{hardware.Name} ({hardware.Address})", TargetType = "Hardware",
                Risk = RiskLevel.Delete, AllowStateChange = true, TimeoutSeconds = 900
            };
            op.Pipeline.Add(PsCommands.RemoveVmsHardware(new[] { hardware }));
            return op;
        }

        public VmsOperation PlanSetDeviceStorage(CameraInfo camera, string storageName)
        {
            var op = new VmsOperation { Title = $"Move recording storage to '{storageName}'", Target = camera.Name, TargetType = "Camera", Risk = RiskLevel.Modify, AllowStateChange = true, TimeoutSeconds = 1800 };
            op.Pipeline.Add(PsCommands.SetVmsDeviceStorage(new[] { camera }, storageName));
            return op;
        }

        public VmsOperation PlanRevealHardwarePassword(HardwareInfo hardware)
        {
            var op = new VmsOperation { Title = "Reveal hardware password", Target = hardware.Name, TargetType = "Hardware", Risk = RiskLevel.Sensitive };
            op.Pipeline.Add(PsCommands.GetVmsHardwarePassword(hardware));
            op.Output.AllowSensitive = true;
            op.Output.RegisterHandles = false;
            return op;
        }
    }

    public sealed class VmsOperationException : Exception
    {
        public VmsOperationException(OperationResult result)
            : base(result.PrimaryError?.Message ?? "The operation failed.")
        {
            Result = result;
        }

        public OperationResult Result { get; }
    }
}
