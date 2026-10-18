using System;
using System.Collections.Generic;
using System.Linq;
using MilestoneAdmin.Application.Session;
using MilestoneAdmin.Domain.Capabilities;
using MilestoneAdmin.Domain.Catalog;

namespace MilestoneAdmin.Application.Navigation
{
    public sealed class WorkflowInfo
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Page { get; set; }
        public string CapabilityId { get; set; }
        public string[] Keywords { get; set; } = Array.Empty<string>();
        public bool Modifies { get; set; }
    }

    /// <summary>Guided operations, as the administrator thinks of them. Used for the dashboard, favorites and search.</summary>
    public static class WorkflowCatalog
    {
        public static readonly IReadOnlyList<WorkflowInfo> All = new List<WorkflowInfo>
        {
            new WorkflowInfo { Id = "connect", Title = "Connect to XProtect", Page = "Connect", CapabilityId = "connect", Keywords = new[] { "login", "sign in", "management server", "profile" } },
            new WorkflowInfo { Id = "recorders.inventory", Title = "Check recording servers", Page = "RecordingServers", CapabilityId = "recorders.query", Keywords = new[] { "recorder", "rs", "servers", "status" } },
            new WorkflowInfo { Id = "hardware.inventory", Title = "Browse hardware", Page = "Hardware", CapabilityId = "hardware.query", Keywords = new[] { "devices", "encoder", "ip address" } },
            new WorkflowInfo { Id = "hardware.add", Title = "Add hardware", Description = "Scan, select and add cameras/encoders to a recording server.", Page = "AddHardware", CapabilityId = "hardware.add", Modifies = true, Keywords = new[] { "add camera", "add hardware", "scan", "discover", "onboard" } },
            new WorkflowInfo { Id = "hardware.csv", Title = "Bulk onboarding from CSV or Excel", Page = "CsvImport", CapabilityId = "hardware.csv", Modifies = true, Keywords = new[] { "csv", "excel", "xlsx", "spreadsheet", "import", "bulk", "onboarding", "template" } },
            new WorkflowInfo { Id = "hardware.edit", Title = "Edit hardware or update device credentials", Page = "Hardware", CapabilityId = "hardware.update", Modifies = true, Keywords = new[] { "rename hardware", "password", "credentials", "address" } },
            new WorkflowInfo { Id = "storage.manage", Title = "Add or remove recording storage", Page = "Storage", CapabilityId = "storage.query", Modifies = true, Keywords = new[] { "add storage", "remove storage", "retention" } },
            new WorkflowInfo { Id = "security.manage", Title = "Manage role members and basic users", Page = "Security", CapabilityId = "roles.query", Modifies = true, Keywords = new[] { "add user to role", "basic user", "lock out", "reset password" } },
            new WorkflowInfo { Id = "rules.manage", Title = "Enable or disable rules", Page = "Rules", CapabilityId = "rules.query", Modifies = true, Keywords = new[] { "disable rule", "enable rule" } },
            new WorkflowInfo { Id = "sites", Title = "Switch site (Federated Architecture)", Page = "System", CapabilityId = null, Keywords = new[] { "site", "child site", "federated", "milestone federated architecture" } },
            new WorkflowInfo { Id = "hardware.remove", Title = "Remove hardware", Page = "Hardware", CapabilityId = "hardware.remove", Modifies = true, Keywords = new[] { "delete", "remove camera", "remove hardware" } },
            new WorkflowInfo { Id = "hardware.enable", Title = "Enable or disable hardware", Page = "Hardware", CapabilityId = "hardware.update", Modifies = true, Keywords = new[] { "disable", "enable" } },
            new WorkflowInfo { Id = "cameras.inventory", Title = "Browse cameras", Page = "Cameras", CapabilityId = "cameras.query", Keywords = new[] { "camera list", "devices" } },
            new WorkflowInfo { Id = "cameras.enable", Title = "Enable or disable cameras", Page = "Cameras", CapabilityId = "cameras.update", Modifies = true, Keywords = new[] { "disable camera", "enable camera" } },
            new WorkflowInfo { Id = "cameras.rename", Title = "Rename cameras", Description = "Rename by pattern or from a CSV file.", Page = "Cameras", CapabilityId = "cameras.update", Modifies = true, Keywords = new[] { "naming", "rename", "names" } },
            new WorkflowInfo { Id = "cameras.recording", Title = "Change recording settings", Page = "Cameras", CapabilityId = "cameras.update", Modifies = true, Keywords = new[] { "frame rate", "fps", "prebuffer", "recording" } },
            new WorkflowInfo { Id = "cameras.storage", Title = "Move cameras to another storage", Page = "Cameras", CapabilityId = "cameras.storage", Modifies = true, Keywords = new[] { "storage", "retention", "move" } },
            new WorkflowInfo { Id = "storage.inventory", Title = "Storage configuration", Page = "Storage", CapabilityId = "storage.query", Keywords = new[] { "retention", "disk", "archive" } },
            new WorkflowInfo { Id = "maintenance.status", Title = "Device status", Page = "Maintenance", CapabilityId = "devices.status", Keywords = new[] { "offline", "health", "not recording", "errors" } },
            new WorkflowInfo { Id = "reports.camera-inventory", Title = "Export camera inventory", Page = "Reports", CapabilityId = "cameras.query", Keywords = new[] { "report", "inventory", "export", "csv" } },
            new WorkflowInfo { Id = "reports", Title = "Reports", Page = "Reports", CapabilityId = "recorders.query", Keywords = new[] { "report", "export" } },
            new WorkflowInfo { Id = "security.roles", Title = "Roles and users", Page = "Security", CapabilityId = "roles.query", Keywords = new[] { "permissions", "roles", "users", "basic user" } },
            new WorkflowInfo { Id = "rules.inventory", Title = "Rules, events and alarms", Page = "Rules", CapabilityId = "rules.query", Keywords = new[] { "rules", "events", "alarms" } },
            new WorkflowInfo { Id = "smartclient.views", Title = "Smart Client views and profiles", Page = "SmartClient", CapabilityId = "views.query", Keywords = new[] { "views", "view groups", "smart client" } },
            new WorkflowInfo { Id = "automation", Title = "Job Builder", Page = "Automation", CapabilityId = "cameras.query", Keywords = new[] { "automation", "job", "workflow", "batch" } },
            new WorkflowInfo { Id = "explorer", Title = "Command Explorer", Page = "CommandExplorer", CapabilityId = null, Keywords = new[] { "powershell", "cmdlet", "command", "expert" } },
            new WorkflowInfo { Id = "audit", Title = "Audit log", Page = "Audit", CapabilityId = null, Keywords = new[] { "audit", "history", "changes" } }
        };

        public static WorkflowInfo Find(string id) => All.FirstOrDefault(w => w.Id == id);

        public static CapabilityStatus StatusFor(WorkflowInfo w, SessionContext session) =>
            w.CapabilityId == null
                ? new CapabilityStatus { State = CapabilityState.Available, Reason = string.Empty }
                : session.Capabilities.FirstOrDefault(c => c.Definition.Id == w.CapabilityId)
                  ?? new CapabilityStatus { State = CapabilityState.Unavailable, Reason = "MilestonePSTools has not been detected yet." };
    }

    public enum SearchResultKind
    {
        Workflow,
        RecordingServer,
        Hardware,
        Camera,
        Command
    }

    public sealed class SearchResult
    {
        public SearchResultKind Kind { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public string Page { get; set; }
        public object Target { get; set; }
        public int Score { get; set; }
    }

    /// <summary>Global search across workflows, the loaded inventory and (in Expert Mode) the command catalog.</summary>
    public sealed class SearchService
    {
        private readonly SessionContext _session;

        public SearchService(SessionContext session)
        {
            _session = session;
        }

        public IReadOnlyList<SearchResult> Search(string query, bool expertMode, int max = 50)
        {
            var q = (query ?? string.Empty).Trim();
            if (q.Length < 2) return Array.Empty<SearchResult>();
            var results = new List<SearchResult>();

            int Score(string text)
            {
                if (string.IsNullOrEmpty(text)) return 0;
                if (string.Equals(text, q, StringComparison.OrdinalIgnoreCase)) return 100;
                if (text.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 80;
                return text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ? 60 : 0;
            }

            foreach (var w in WorkflowCatalog.All)
            {
                var s = Math.Max(Score(w.Title), w.Keywords.Select(k => Score(k) - 10).DefaultIfEmpty(0).Max());
                if (s > 0) results.Add(new SearchResult { Kind = SearchResultKind.Workflow, Title = w.Title, Detail = w.Description ?? "Guided workflow", Page = w.Page, Target = w, Score = s + 5 });
            }

            var inv = _session.Inventory;
            foreach (var r in inv.RecordingServers)
            {
                var s = Math.Max(Score(r.Name), Score(r.HostName));
                if (s > 0) results.Add(new SearchResult { Kind = SearchResultKind.RecordingServer, Title = r.Name, Detail = "Recording server · " + r.HostName, Page = "RecordingServers", Target = r, Score = s });
            }

            foreach (var h in inv.Hardware)
            {
                var s = Math.Max(Score(h.Name), Score(h.Address));
                if (s > 0) results.Add(new SearchResult { Kind = SearchResultKind.Hardware, Title = h.Name, Detail = $"Hardware · {h.Address} · {h.RecordingServerName}", Page = "Hardware", Target = h, Score = s });
            }

            foreach (var c in inv.Cameras)
            {
                var s = Math.Max(Score(c.Name), Score(c.Id.ToString()));
                if (s > 0) results.Add(new SearchResult { Kind = SearchResultKind.Camera, Title = c.Name, Detail = $"Camera · {c.HardwareName} · {c.RecordingServerName}", Page = "Cameras", Target = c, Score = s });
            }

            if (expertMode && _session.Catalog != null)
            {
                foreach (var c in _session.Catalog.Commands)
                {
                    var s = Math.Max(Score(c.Name), c.Aliases.Select(Score).DefaultIfEmpty(0).Max());
                    if (s == 0 && q.Length >= 4 && c.Synopsis.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) s = 30;
                    if (s > 0) results.Add(new SearchResult { Kind = SearchResultKind.Command, Title = c.Name, Detail = c.Synopsis, Page = "CommandExplorer", Target = c, Score = s });
                }
            }

            return results.OrderByDescending(r => r.Score).ThenBy(r => r.Kind).ThenBy(r => r.Title).Take(max).ToList();
        }
    }
}
