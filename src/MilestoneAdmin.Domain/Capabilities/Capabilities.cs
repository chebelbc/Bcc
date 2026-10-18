using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MilestoneAdmin.Domain.Catalog;

namespace MilestoneAdmin.Domain.Capabilities
{
    public sealed class CapabilityDefinition
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Area { get; set; }

        /// <summary>Every one of these commands must exist in the installed module.</summary>
        public string[] RequiredCommands { get; set; } = Array.Empty<string>();

        /// <summary>When set, the capability is known to be unavailable in MilestonePSTools regardless of version.</summary>
        public string NotSupportedReason { get; set; }
    }

    public enum CapabilityState
    {
        Available,
        Unavailable,
        NotSupportedByModule,
        RequiresConnection
    }

    public sealed class CapabilityStatus
    {
        public CapabilityDefinition Definition { get; set; }
        public CapabilityState State { get; set; }
        public string Reason { get; set; }

        public bool IsAvailable => State == CapabilityState.Available;

        public string Symbol => IsAvailable ? "✓" : "✗";
    }

    /// <summary>Features of the application mapped to the MilestonePSTools commands they depend on.</summary>
    public static class CapabilityCatalog
    {
        public static readonly IReadOnlyList<CapabilityDefinition> All = new List<CapabilityDefinition>
        {
            new CapabilityDefinition { Id = "connect", Area = "Connection", Title = "Connect to XProtect", RequiredCommands = new[] { "Connect-Vms", "Disconnect-Vms", "Test-VmsConnection", "Get-VmsManagementServer" } },
            new CapabilityDefinition { Id = "recorders.query", Area = "System", Title = "Recording server inventory", RequiredCommands = new[] { "Get-VmsRecordingServer" } },
            new CapabilityDefinition { Id = "hardware.query", Area = "Hardware", Title = "Hardware inventory", RequiredCommands = new[] { "Get-VmsHardware" } },
            new CapabilityDefinition { Id = "hardware.add", Area = "Hardware", Title = "Add hardware (scan + add)", RequiredCommands = new[] { "Start-VmsHardwareScan", "Add-VmsHardware", "Get-VmsHardwareDriver" } },
            new CapabilityDefinition { Id = "hardware.update", Area = "Hardware", Title = "Enable/disable hardware", RequiredCommands = new[] { "Set-VmsHardware" } },
            new CapabilityDefinition { Id = "hardware.remove", Area = "Hardware", Title = "Remove hardware", RequiredCommands = new[] { "Remove-VmsHardware" } },
            new CapabilityDefinition { Id = "hardware.csv", Area = "Hardware", Title = "CSV hardware onboarding", RequiredCommands = new[] { "Add-VmsHardware", "Start-VmsHardwareScan", "Get-VmsRecordingServer", "Get-VmsHardwareDriver" } },
            new CapabilityDefinition { Id = "cameras.query", Area = "Devices", Title = "Camera inventory", RequiredCommands = new[] { "Get-VmsCamera" } },
            new CapabilityDefinition { Id = "cameras.update", Area = "Devices", Title = "Camera properties (enable, rename, recording)", RequiredCommands = new[] { "Set-VmsCamera" } },
            new CapabilityDefinition { Id = "cameras.storage", Area = "Storage", Title = "Change camera recording storage", RequiredCommands = new[] { "Set-VmsDeviceStorage", "Get-VmsStorage" } },
            new CapabilityDefinition { Id = "devices.status", Area = "Maintenance", Title = "Live device status", RequiredCommands = new[] { "Get-VmsDeviceStatus" } },
            new CapabilityDefinition { Id = "storage.query", Area = "Storage", Title = "Storage information", RequiredCommands = new[] { "Get-VmsStorage" } },
            new CapabilityDefinition { Id = "roles.query", Area = "Security", Title = "Role inventory", RequiredCommands = new[] { "Get-VmsRole" } },
            new CapabilityDefinition { Id = "users.query", Area = "Security", Title = "Basic user inventory", RequiredCommands = new[] { "Get-VmsBasicUser" } },
            new CapabilityDefinition { Id = "rules.query", Area = "RulesEvents", Title = "Rule inventory", RequiredCommands = new[] { "Get-VmsRule" } },
            new CapabilityDefinition { Id = "alarms.query", Area = "RulesEvents", Title = "Alarm definition inventory", RequiredCommands = new[] { "Get-VmsAlarmDefinition" } },
            new CapabilityDefinition { Id = "views.query", Area = "SmartClient", Title = "Smart Client views", RequiredCommands = new[] { "Get-VmsViewGroup" } },
            new CapabilityDefinition { Id = "clientprofiles.query", Area = "SmartClient", Title = "Smart Client profiles", RequiredCommands = new[] { "Get-VmsClientProfile" } },
            new CapabilityDefinition { Id = "license.query", Area = "Licensing", Title = "License overview", RequiredCommands = new[] { "Get-VmsLicenseOverview" } },
            new CapabilityDefinition { Id = "reports.camera", Area = "Reports", Title = "Detailed camera report (Get-VmsCameraReport)", RequiredCommands = new[] { "Get-VmsCameraReport" } },
            new CapabilityDefinition
            {
                Id = "backup", Area = "Maintenance", Title = "Configuration backup / restore",
                NotSupportedReason = "MilestonePSTools has no backup/restore command (feature request milestonesys/MilestonePSTools#121 is open). " +
                                     "Object-level exports exist (Export-VmsHardware, Export-VmsRole, Export-VmsRule, Export-VmsViewGroup, Export-VmsClientProfile) but are not a system backup."
            }
        };

        public static CapabilityDefinition Find(string id) => All.FirstOrDefault(c => c.Id == id);
    }

    public sealed class CapabilityEvaluator
    {
        public IReadOnlyList<CapabilityStatus> Evaluate(CommandCatalog catalog) => CapabilityCatalog.All.Select(d => Evaluate(d, catalog)).ToList();

        public CapabilityStatus Evaluate(CapabilityDefinition d, CommandCatalog catalog)
        {
            if (d.NotSupportedReason != null) return new CapabilityStatus { Definition = d, State = CapabilityState.NotSupportedByModule, Reason = d.NotSupportedReason };
            if (catalog == null) return new CapabilityStatus { Definition = d, State = CapabilityState.Unavailable, Reason = "MilestonePSTools has not been detected yet." };
            var missing = d.RequiredCommands.Where(c => !catalog.Contains(c)).ToList();
            if (missing.Count > 0)
            {
                return new CapabilityStatus
                {
                    Definition = d,
                    State = CapabilityState.Unavailable,
                    Reason = $"Not supported by the currently installed MilestonePSTools version ({catalog.ModuleVersion}): missing {string.Join(", ", missing)}."
                };
            }

            return new CapabilityStatus { Definition = d, State = CapabilityState.Available, Reason = "All required commands are available." };
        }
    }

    /// <summary>
    /// XProtect version comparison. MilestonePSTools expresses requirements such as [RequiresVmsVersion('21.1')] in
    /// product version numbers (21.1 = XProtect 2021 R1) and the management server reports e.g. "25.2.4815.1".
    /// </summary>
    public static class VmsVersion
    {
        public static Version Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var parts = s.Trim().Split('.', ' ', '-').Where(p => p.Length > 0).Take(4).ToList();
            var nums = new List<int>();
            foreach (var p in parts)
            {
                if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) break;
                nums.Add(n);
            }

            if (nums.Count == 0) return null;
            while (nums.Count < 2) nums.Add(0);
            return nums.Count == 2 ? new Version(nums[0], nums[1]) : nums.Count == 3 ? new Version(nums[0], nums[1], nums[2]) : new Version(nums[0], nums[1], nums[2], nums[3]);
        }

        /// <summary>Compares major.minor only, which is the granularity of RequiresVmsVersion.</summary>
        public static bool Satisfies(string actual, string required)
        {
            var a = Parse(actual);
            var r = Parse(required);
            if (r == null) return true;
            if (a == null) return false;
            return a.Major > r.Major || (a.Major == r.Major && a.Minor >= r.Minor);
        }

        /// <summary>
        /// "25.2" -> "XProtect 2025 R2". The year-based scheme is documented in the MilestonePSTools repository
        /// ("XProtect 2023 R2 is version 23.2"); older numbering is not mapped because it was not verified.
        /// </summary>
        public static string ToProductName(string version)
        {
            var v = Parse(version);
            if (v == null) return version;
            return v.Major >= 20 && v.Major < 100 ? $"XProtect 20{v.Major:00} R{v.Minor}" : version;
        }
    }
}
