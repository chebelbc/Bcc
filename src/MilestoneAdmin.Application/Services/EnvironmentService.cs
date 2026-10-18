using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Application.Session;
using MilestoneAdmin.Domain.Capabilities;
using MilestoneAdmin.Domain.Catalog;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.Application.Services
{
    public enum CheckStatus
    {
        Passed,
        Warning,
        Failed,
        NotChecked
    }

    public sealed class PrecheckItem
    {
        public string Title { get; set; }
        public CheckStatus Status { get; set; }
        public string Detail { get; set; }
        public string Remedy { get; set; }

        public string Symbol => Status == CheckStatus.Passed ? "✓" : Status == CheckStatus.Warning ? "!" : Status == CheckStatus.Failed ? "✗" : "–";

        public static PrecheckItem Pass(string title, string detail) => new PrecheckItem { Title = title, Status = CheckStatus.Passed, Detail = detail };

        public static PrecheckItem Warn(string title, string detail, string remedy) => new PrecheckItem { Title = title, Status = CheckStatus.Warning, Detail = detail, Remedy = remedy };

        public static PrecheckItem Fail(string title, string detail, string remedy) => new PrecheckItem { Title = title, Status = CheckStatus.Failed, Detail = detail, Remedy = remedy };
    }

    public sealed class PrecheckReport
    {
        public List<PrecheckItem> Items { get; } = new List<PrecheckItem>();

        public bool CanContinue => Items.All(i => i.Status != CheckStatus.Failed);
    }

    /// <summary>
    /// Environment and operation pre-checks. Requirements come from the MilestonePSTools manifest and README:
    /// Windows PowerShell 5.1 (Desktop), .NET Framework 4.7.2+, 64-bit, FullLanguage mode, execution policy not Restricted.
    /// </summary>
    public sealed class EnvironmentService
    {
        /// <summary>.NET Framework 4.7.2 release key (Microsoft documentation: "Release" DWORD >= 461808).</summary>
        public const int NetFramework472Release = 461808;

        /// <summary>The module version this application release was validated against (catalog/milestonepstools-catalog.json).</summary>
        public const string ValidatedModuleVersion = "25.2.61";

        public PrecheckReport CheckEnvironment(JsonObject env, CommandCatalog catalog)
        {
            var r = new PrecheckReport();
            if (env == null)
            {
                r.Items.Add(PrecheckItem.Fail("Windows PowerShell worker", "The PowerShell worker could not be started.", "Reinstall the application or check the Activity log for details."));
                return r;
            }

            var psv = env.GetString("psVersion");
            var edition = env.GetString("psEdition");
            if (Version.TryParse(psv, out var v) && v.Major == 5 && v.Minor >= 1 && (edition == null || edition == "Desktop"))
                r.Items.Add(PrecheckItem.Pass("Windows PowerShell 5.1", $"PowerShell {psv} ({edition ?? "Desktop"})"));
            else
                r.Items.Add(PrecheckItem.Fail("Windows PowerShell 5.1", $"Found PowerShell {psv} ({edition}).", "MilestonePSTools requires Windows PowerShell 5.1 (Desktop edition); it does not run on PowerShell 7."));

            var rel = env["netFrameworkRelease"]?.AsInt64();
            if (rel.HasValue && rel.Value >= NetFramework472Release) r.Items.Add(PrecheckItem.Pass(".NET Framework 4.7.2 or later", $"Release key {rel.Value}"));
            else if (rel.HasValue) r.Items.Add(PrecheckItem.Fail(".NET Framework 4.7.2 or later", $"Release key {rel.Value}", "Install .NET Framework 4.8 from Microsoft."));
            else r.Items.Add(PrecheckItem.Warn(".NET Framework 4.7.2 or later", "Could not read the .NET Framework version.", "Verify .NET Framework 4.7.2 or later is installed."));

            r.Items.Add(env.GetBool("is64BitProcess")
                ? PrecheckItem.Pass("64-bit process", "The worker runs as a 64-bit process.")
                : PrecheckItem.Fail("64-bit process", "The worker is not 64-bit.", "MilestonePSTools requires 64-bit Windows PowerShell (manifest ProcessorArchitecture = Amd64)."));

            var lm = env.GetString("languageMode");
            r.Items.Add(lm == "FullLanguage"
                ? PrecheckItem.Pass("PowerShell language mode", "FullLanguage")
                : PrecheckItem.Fail("PowerShell language mode", lm, "MilestonePSTools requires FullLanguage mode. Constrained Language Mode is usually enforced by AppLocker/WDAC policy; contact your system administrator."));

            var ep = env.GetString("executionPolicy");
            if (string.Equals(ep, "Restricted", StringComparison.OrdinalIgnoreCase))
                r.Items.Add(PrecheckItem.Fail("Execution policy", ep, "Modules cannot be loaded with the Restricted policy. An administrator must choose a policy (MilestonePSTools documentation uses RemoteSigned). The application never changes it."));
            else if (string.Equals(ep, "AllSigned", StringComparison.OrdinalIgnoreCase))
                r.Items.Add(PrecheckItem.Warn("Execution policy", ep, "AllSigned requires every script in the module to carry a trusted signature."));
            else
                r.Items.Add(PrecheckItem.Pass("Execution policy", ep));

            var versions = (env.GetArray("installedModuleVersions") ?? new JsonArray()).Select(x => x.AsObject()?.GetString("version")).Where(x => x != null).ToList();
            var loaded = env.GetString("loadedModuleVersion");
            if (versions.Count == 0 && loaded == null)
            {
                r.Items.Add(PrecheckItem.Fail("MilestonePSTools installed", "MilestonePSTools was not found in any module path.",
                    "Install it from the PowerShell Gallery: Install-Module MilestonePSTools -Scope CurrentUser (see the setup wizard)."));
            }
            else
            {
                r.Items.Add(PrecheckItem.Pass("MilestonePSTools installed", "Versions: " + string.Join(", ", versions.DefaultIfEmpty(loaded))));
                var current = loaded ?? versions.First();
                var cmp = CompareVersions(current, ValidatedModuleVersion);
                if (cmp == 0) r.Items.Add(PrecheckItem.Pass("Supported MilestonePSTools version", $"{current} (validated)"));
                else if (cmp > 0) r.Items.Add(PrecheckItem.Warn("Supported MilestonePSTools version", $"{current} is newer than the validated {ValidatedModuleVersion}.", "Features are enabled per command availability; new commands appear in Command Explorer."));
                else r.Items.Add(PrecheckItem.Warn("Supported MilestonePSTools version", $"{current} is older than the validated {ValidatedModuleVersion}.", "Consider updating: Update-Module MilestonePSTools. Unavailable features are disabled."));
            }

            if (env.GetBool("mipSdkEulaAccepted"))
                r.Items.Add(PrecheckItem.Pass("MIP SDK license agreement", "Accepted for this Windows user."));
            else
                r.Items.Add(PrecheckItem.Warn("MIP SDK license agreement", "Not yet accepted for this Windows user.", "Review and accept the Milestone MIP SDK EULA in the setup wizard before connecting."));

            if (catalog != null)
                r.Items.Add(PrecheckItem.Pass("Command catalog", $"{catalog.Commands.Count} commands ({(catalog.Source == CatalogSourceKind.Runtime ? "discovered from installed module" : "shipped catalog")})."));
            return r;
        }

        public static int CompareVersions(string a, string b)
        {
            if (!Version.TryParse(a, out var va) || !Version.TryParse(b, out var vb)) return string.CompareOrdinal(a, b);
            return va.CompareTo(vb);
        }

        /// <summary>Pre-checks for a specific operation (commands present, connected, VMS version, recorder reachability).</summary>
        public async Task<PrecheckReport> CheckOperationAsync(SessionContext session, IEnumerable<string> requiredCommands, IEnumerable<RecordingServerInfo> recorders, CancellationToken ct)
        {
            var r = new PrecheckReport();
            r.Items.Add(session.Gateway?.Executor?.IsRunning == true
                ? PrecheckItem.Pass("Windows PowerShell worker", $"Running, MilestonePSTools {session.ModuleVersion}")
                : PrecheckItem.Fail("Windows PowerShell worker", "Not running.", "Connect to a management server."));
            r.Items.Add(session.IsConnected
                ? PrecheckItem.Pass("Authentication", $"Connected to {session.SystemName} as {session.ConnectedAccount}")
                : PrecheckItem.Fail("Authentication", "Not connected.", "Connect to a management server."));
            foreach (var c in requiredCommands ?? Enumerable.Empty<string>())
            {
                var cmd = session.Catalog?.Find(c);
                if (cmd == null)
                {
                    r.Items.Add(PrecheckItem.Fail($"Command {c} available", $"Not supported by the currently installed MilestonePSTools version ({session.ModuleVersion}).", "Update MilestonePSTools."));
                    continue;
                }

                r.Items.Add(PrecheckItem.Pass($"Command {c} available", cmd.Synopsis));
                var min = cmd.Requirements.MinVmsVersion;
                if (min != null && session.ManagementServer?.Version != null)
                {
                    r.Items.Add(VmsVersion.Satisfies(session.ManagementServer.Version, min)
                        ? PrecheckItem.Pass($"XProtect version for {c}", $"{session.ManagementServer.Version} >= {min}")
                        : PrecheckItem.Fail($"XProtect version for {c}", $"{session.ManagementServer.Version} < {min} ({VmsVersion.ToProductName(min)})", "This operation needs a newer XProtect version."));
                }

                if (cmd.Requirements.VmsFeatures.Count > 0)
                    r.Items.Add(PrecheckItem.Warn($"Licensed features for {c}", "Requires: " + string.Join(", ", cmd.Requirements.VmsFeatures), "MilestonePSTools verifies the license when the command runs."));
                if (cmd.Requirements.Elevation)
                    r.Items.Add(PrecheckItem.Warn($"Elevation for {c}", "Requires local Administrator privileges on the machine where it runs.", "The application does not elevate itself."));
            }

            foreach (var rec in recorders ?? Enumerable.Empty<RecordingServerInfo>())
                r.Items.Add(await CheckRecorderAsync(rec, ct).ConfigureAwait(false));
            r.Items.Add(new PrecheckItem
            {
                Title = "User permissions",
                Status = CheckStatus.NotChecked,
                Detail = "XProtect permissions are enforced by the management server when the operation runs; they cannot be fully evaluated in advance."
            });
            return r;
        }

        /// <summary>TCP reachability of the recording server web server from this workstation (default port 7563).</summary>
        public static async Task<PrecheckItem> CheckRecorderAsync(RecordingServerInfo rec, CancellationToken ct)
        {
            var title = $"Recording server {rec.Name} reachable";
            if (rec.Enabled == false) return PrecheckItem.Warn(title, "The recording server is disabled in the configuration.", "Enable it in Management Client.");
            var uriText = rec.ActiveWebServerUri ?? rec.WebServerUri;
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
                return new PrecheckItem { Title = title, Status = CheckStatus.NotChecked, Detail = "No web server address is configured." };
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(uri.Host, uri.Port);
                    var done = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(4), ct)).ConfigureAwait(false);
                    if (done == connect && client.Connected)
                        return PrecheckItem.Pass(title, $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)} accepted a TCP connection.");
                }
            }
            catch (Exception)
            {
                // reported below
            }

            return PrecheckItem.Warn(title, $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)} did not accept a TCP connection from this workstation.",
                "Configuration changes go through the management server and may still succeed, but status, snapshots and some reports need direct access to the recording server.");
        }
    }
}
