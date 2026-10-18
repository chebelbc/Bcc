using System;
using System.Collections.Generic;
using System.Linq;
using MilestoneAdmin.Domain.Capabilities;
using MilestoneAdmin.Domain.Catalog;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.Domain.Scripting;
using MilestoneAdmin.PSTools;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.Application.Session
{
    /// <summary>Inventory snapshot of the connected site (joined across recording servers, hardware and cameras).</summary>
    public sealed class InventorySnapshot
    {
        public List<RecordingServerInfo> RecordingServers { get; } = new List<RecordingServerInfo>();
        public List<HardwareInfo> Hardware { get; } = new List<HardwareInfo>();
        public List<CameraInfo> Cameras { get; } = new List<CameraInfo>();
        public List<StorageInfo> Storage { get; } = new List<StorageInfo>();
        public DateTimeOffset? LoadedAt { get; set; }
        public List<string> Warnings { get; } = new List<string>();

        public int DisabledCameras => Cameras.Count(c => c.Enabled == false);

        public int DisabledHardware => Hardware.Count(h => h.Enabled == false);

        public RecordingServerInfo RecorderByPath(string path) => RecordingServers.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

        public HardwareInfo HardwareByPath(string path) => Hardware.FirstOrDefault(h => string.Equals(h.Path, path, StringComparison.OrdinalIgnoreCase));

        /// <summary>Fill derived names and counts.</summary>
        public void Join()
        {
            var storageByPath = Storage.Where(s => s.Path != null).GroupBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var s in Storage) s.RecordingServerName = RecorderByPath(s.ParentItemPath)?.Name;
            foreach (var h in Hardware) h.RecordingServerName = RecorderByPath(h.ParentItemPath)?.Name;
            foreach (var c in Cameras)
            {
                var h = HardwareByPath(c.ParentItemPath);
                c.HardwareName = h?.Name;
                c.HardwareAddress = h?.Address;
                c.RecordingServerName = h?.RecordingServerName;
                c.RecordingStorageName = c.RecordingStorage != null && storageByPath.TryGetValue(c.RecordingStorage, out var st) ? st.Name : null;
            }

            foreach (var h in Hardware) h.CameraCount = Cameras.Count(c => string.Equals(c.ParentItemPath, h.Path, StringComparison.OrdinalIgnoreCase));
            foreach (var r in RecordingServers)
            {
                var hw = Hardware.Where(h => string.Equals(h.ParentItemPath, r.Path, StringComparison.OrdinalIgnoreCase)).ToList();
                r.HardwareCount = hw.Count;
                var cams = Cameras.Where(c => c.RecordingServerName == r.Name).ToList();
                r.CameraCount = cams.Count;
                r.DisabledCameraCount = cams.Count(c => c.Enabled == false);
            }
        }

        public IEnumerable<VmsObject> AllObjects() => RecordingServers.Cast<VmsObject>().Concat(Hardware).Concat(Cameras).Concat(Storage);
    }

    /// <summary>State of the current connection, shared by all pages.</summary>
    public sealed class SessionContext
    {
        private readonly Dictionary<string, VmsObject> _byHandle = new Dictionary<string, VmsObject>(StringComparer.Ordinal);

        public event EventHandler Changed;

        public bool IsConnected { get; private set; }

        public IVmsGateway Gateway { get; private set; }

        public ConnectionRequest Connection { get; private set; }

        public ManagementServerInfo ManagementServer { get; private set; }

        public string ConnectedAccount { get; private set; }

        public CommandCatalog Catalog { get; set; }

        public IReadOnlyList<CapabilityStatus> Capabilities { get; set; } = Array.Empty<CapabilityStatus>();

        public JsonObject EnvironmentReport { get; set; }

        public string ModuleVersion => Gateway?.Executor.Info?.ModuleVersion ?? Catalog?.ModuleVersion;

        public InventorySnapshot Inventory { get; private set; } = new InventorySnapshot();

        public string SystemName => ManagementServer?.Name ?? Connection?.ServerAddress?.Host;

        public void SetConnected(IVmsGateway gateway, ConnectionRequest connection, ManagementServerInfo server, string account)
        {
            Gateway = gateway;
            Connection = connection;
            ManagementServer = server;
            ConnectedAccount = account;
            IsConnected = true;
            Inventory = new InventorySnapshot();
            lock (_byHandle) _byHandle.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetDisconnected()
        {
            IsConnected = false;
            Gateway = null;
            ManagementServer = null;
            Inventory = new InventorySnapshot();
            lock (_byHandle) _byHandle.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetInventory(InventorySnapshot snapshot)
        {
            Inventory = snapshot;
            Register(snapshot.AllObjects());
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Register(IEnumerable<VmsObject> objects)
        {
            lock (_byHandle)
            {
                foreach (var o in objects)
                {
                    if (!string.IsNullOrEmpty(o?.Handle)) _byHandle[o.Handle] = o;
                }
            }
        }

        public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

        /// <summary>Lets the script generator turn worker handles back into "Get-Vms* -Id ..." lines.</summary>
        public ScriptObjectInfo DescribeHandle(string handle)
        {
            lock (_byHandle)
            {
                if (handle == null || !_byHandle.TryGetValue(handle, out var o)) return null;
                return new ScriptObjectInfo { ObjectType = o.ObjectType, Id = o.ObjectType == "Storage" ? o.Path : o.Id == Guid.Empty ? null : o.Id.ToString("D"), Name = o.Name };
            }
        }

        public ScriptConnectionInfo ScriptConnection => Connection == null
            ? null
            : new ScriptConnectionInfo
            {
                ServerAddress = Connection.ServerAddress.ToString(),
                UsesCredential = Connection.AuthMode != ConnectionAuthMode.CurrentWindowsUser,
                BasicUser = Connection.AuthMode == ConnectionAuthMode.BasicUser,
                SecureOnly = Connection.SecureOnly,
                IncludeChildSites = Connection.IncludeChildSites
            };
    }
}
