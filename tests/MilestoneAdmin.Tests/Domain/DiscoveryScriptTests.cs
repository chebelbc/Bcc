using System;
using System.IO;
using System.Linq;
using System.Management.Automation;
using MilestoneAdmin.Domain.Catalog;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Protocol.Json;
using Xunit;

namespace MilestoneAdmin.Tests.Domain
{
    /// <summary>Runs tools/discovery/Export-PSToolsCatalog.ps1 (in-process PowerShell 7) against the mock module.</summary>
    public class DiscoveryScriptTests
    {
        [Fact]
        public void Produces_a_catalog_the_application_can_load()
        {
            var output = Path.Combine(Path.GetTempPath(), "xpa-discovery-" + Guid.NewGuid().ToString("N") + ".json");
            var script = Path.Combine(TestPaths.RepoRoot, "tools", "discovery", "Export-PSToolsCatalog.ps1");
            var mockRoot = Path.GetDirectoryName(Path.GetDirectoryName(TestPaths.MockModule));
            var originalModulePath = Environment.GetEnvironmentVariable("PSModulePath");
            try
            {
                using (var ps = PowerShell.Create())
                {
                    // The script finds the module by name, as on a real workstation. PSModulePath is process-wide and is
                    // restored below; the other tests load the mock by explicit path and do not depend on it.
                    Environment.SetEnvironmentVariable("PSModulePath", mockRoot + Path.PathSeparator + originalModulePath);
                    ps.AddCommand(script).AddParameter("OutputPath", output).AddParameter("DisableTelemetry", true);
                    ps.Invoke();
                    Assert.True(ps.Streams.Error.Count == 0, string.Join(Environment.NewLine, ps.Streams.Error.Select(e => e.ToString())));
                }

                var json = JsonNode.Parse(File.ReadAllText(output)).AsObject();
                Assert.Equal("runtime", json.GetObject("source").GetString("kind"));
                Assert.Equal("25.2.61", json.GetString("moduleVersion"));
                Assert.True(json.GetObject("environment").GetBool("telemetryDisabledHere"));

                var catalog = CommandCatalog.FromJson(json, ClassificationRules.Embedded);
                Assert.Equal(json.GetArray("commands").Count, catalog.Commands.Count);

                var set = catalog.Find("Set-VmsHardware");
                Assert.True(set.SupportsShouldProcess);
                Assert.True(set.Requirements.VmsConnection);
                Assert.Equal(RiskLevel.Modify, set.Risk.Level);
                Assert.True(set.FindParameter("Password").IsSecret);

                Assert.Equal(RiskLevel.Sensitive, catalog.Find("Get-VmsHardwarePassword").Risk.Level);
                Assert.Equal(ControlKind.Unsupported, catalog.Find("Invoke-MockScriptBlockProbe").FindParameter("Filter").Control);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PSModulePath", originalModulePath);
                if (File.Exists(output)) File.Delete(output);
            }
        }
    }
}
