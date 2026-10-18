using System;
using System.Collections.Generic;

namespace MilestoneAdmin.Domain.Inventory
{
    /// <summary>
    /// Base for XProtect objects shown in the UI. <see cref="Handle"/> addresses the live object in the current worker
    /// session; <see cref="ObjectType"/> + <see cref="Id"/> allow re-resolution after a worker restart.
    /// </summary>
    public abstract class VmsObject
    {
        public string Handle { get; set; }
        public string ObjectType { get; set; }
        public Guid Id { get; set; }
        public string Path { get; set; }
        public string Name { get; set; }

        public override string ToString() => Name;
    }

    public sealed class ManagementServerInfo : VmsObject
    {
        public string ComputerName { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
    }

    public sealed class RecordingServerInfo : VmsObject
    {
        public string HostName { get; set; }
        public string WebServerUri { get; set; }
        public string ActiveWebServerUri { get; set; }
        public bool? Enabled { get; set; }
        public string Description { get; set; }
        public bool? PublicAccessEnabled { get; set; }
        public DateTime? LastModified { get; set; }

        // Derived
        public int HardwareCount { get; set; }
        public int CameraCount { get; set; }
        public int DisabledCameraCount { get; set; }
    }

    public sealed class HardwareInfo : VmsObject
    {
        public string Address { get; set; }
        public string Model { get; set; }
        public bool? Enabled { get; set; }
        public string UserName { get; set; }
        public string Description { get; set; }
        public string HardwareDriverPath { get; set; }
        public string ParentItemPath { get; set; }
        public DateTime? LastModified { get; set; }

        // Derived
        public string RecordingServerName { get; set; }
        public int CameraCount { get; set; }
    }

    public sealed class CameraInfo : VmsObject
    {
        public string ShortName { get; set; }
        public string Description { get; set; }
        public bool? Enabled { get; set; }
        public int? Channel { get; set; }
        public string ParentItemPath { get; set; }
        public bool? RecordingEnabled { get; set; }
        public double? RecordingFramerate { get; set; }
        public bool? RecordKeyframesOnly { get; set; }
        public bool? PrebufferEnabled { get; set; }
        public int? PrebufferSeconds { get; set; }
        public string RecordingStorage { get; set; }
        public string GisPoint { get; set; }
        public DateTime? LastModified { get; set; }

        // Derived
        public string HardwareName { get; set; }
        public string HardwareAddress { get; set; }
        public string RecordingServerName { get; set; }
        public string RecordingStorageName { get; set; }
    }

    public sealed class StorageInfo : VmsObject
    {
        public string DiskPath { get; set; }
        public long? RetainMinutes { get; set; }
        public long? MaxSize { get; set; }
        public bool? IsDefault { get; set; }
        public string ParentItemPath { get; set; }
        public string Description { get; set; }
        public string RecordingServerName { get; set; }

        public TimeSpan? Retention => RetainMinutes.HasValue ? TimeSpan.FromMinutes(RetainMinutes.Value) : (TimeSpan?)null;
    }

    public sealed class HardwareDriverInfo : VmsObject
    {
        public int? Number { get; set; }
        public string GroupName { get; set; }
        public string DriverType { get; set; }

        public string DisplayName => Number.HasValue ? $"{Number} - {Name} ({GroupName})" : Name;
    }

    /// <summary>A hardware scan result. The password captured by the scan never leaves the worker (it is redacted).</summary>
    public sealed class HardwareScanResultInfo : VmsObject
    {
        public string HardwareAddress { get; set; }
        public string UserName { get; set; }
        public string MacAddress { get; set; }
        public string HardwareDriverPath { get; set; }
        public bool HardwareScanValidated { get; set; }
        public bool MacAddressExistsLocal { get; set; }
        public bool MacAddressExistsGlobal { get; set; }
        public string State { get; set; }
        public string ErrorText { get; set; }
        public string RecordingServerName { get; set; }
        public string DriverName { get; set; }
    }

    public sealed class DeviceStatusInfo
    {
        public string DeviceName { get; set; }
        public string DeviceType { get; set; }
        public string RecorderName { get; set; }
        public Guid RecorderId { get; set; }
        public Guid DeviceId { get; set; }
        public bool? Enabled { get; set; }
        public bool? Started { get; set; }
        public bool? Recording { get; set; }
        public bool? Motion { get; set; }
        public bool? Error { get; set; }
        public bool? ErrorNoConnection { get; set; }
        public bool? ErrorNotLicensed { get; set; }
        public bool? ErrorOverflow { get; set; }
        public bool? ErrorWritingGop { get; set; }
        public bool? DbMoveInProgress { get; set; }
        public bool? DbRepairInProgress { get; set; }
        public DateTime? Time { get; set; }

        public string Summary
        {
            get
            {
                if (Enabled == false) return "Disabled";
                var problems = new List<string>();
                if (ErrorNoConnection == true) problems.Add("No connection to device");
                if (ErrorNotLicensed == true) problems.Add("Not licensed");
                if (ErrorOverflow == true) problems.Add("Overflow");
                if (ErrorWritingGop == true) problems.Add("Error writing to database");
                if (DbRepairInProgress == true) problems.Add("Database repair in progress");
                if (DbMoveInProgress == true) problems.Add("Database move in progress");
                if (Error == true && problems.Count == 0) problems.Add("Error");
                if (problems.Count > 0) return string.Join(", ", problems);
                if (Started == false) return "Not started";
                return Recording == true ? "OK (recording)" : "OK";
            }
        }

        public bool HasProblem => Enabled != false && (Error == true || ErrorNoConnection == true || ErrorNotLicensed == true || ErrorOverflow == true || ErrorWritingGop == true || Started == false);
    }

    public sealed class RoleInfo : VmsObject
    {
        public string RoleType { get; set; }
        public string Description { get; set; }
    }

    /// <summary>Generic object for read-only inventory pages driven by definitions (views, rules, profiles, ...).</summary>
    public sealed class GenericItem : VmsObject
    {
        public Dictionary<string, object> Properties { get; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public object this[string key] => Properties.TryGetValue(key, out var v) ? v : null;
    }
}
