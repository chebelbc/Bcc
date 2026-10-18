using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Application.Execution;
using MilestoneAdmin.Application.Reports;
using MilestoneAdmin.Application.Workflows;
using MilestoneAdmin.Domain.Audit;
using MilestoneAdmin.Domain.Catalog;
using MilestoneAdmin.Domain.Changes;
using MilestoneAdmin.Domain.Csv;
using MilestoneAdmin.Domain.Errors;
using MilestoneAdmin.PSTools;
using Xunit;

namespace MilestoneAdmin.Tests.EndToEnd
{
    /// <summary>Full stack: Application services -> WorkerProcessExecutor -> worker process -> PowerShell -> mock module.</summary>
    [Collection("PowerShell")]
    public class EndToEndTests
    {
        [Fact]
        public async Task Connect_discovers_catalog_and_loads_joined_inventory()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            Assert.True(app.Session.IsConnected);
            Assert.Equal("25.2.4815.1", app.Session.ManagementServer.Version);
            Assert.Equal(CatalogSourceKind.Runtime, app.Session.Catalog.Source);
            Assert.Equal("25.2.61", app.Session.ModuleVersion);

            var inv = app.Session.Inventory;
            Assert.Equal(2, inv.RecordingServers.Count);
            Assert.Equal(3, inv.Hardware.Count);
            Assert.Equal(6, inv.Cameras.Count);
            Assert.True(inv.DisabledCameras > 0);
            var cam = inv.Cameras.First(c => c.HardwareName == "Lobby encoder");
            Assert.Equal("RS01", cam.RecordingServerName);
            Assert.Equal("Local default", cam.RecordingStorageName);
            Assert.Equal(4, inv.RecordingServers.Single(r => r.Name == "RS01").HardwareCount + inv.RecordingServers.Single(r => r.Name == "RS01").CameraCount - 3);

            // The capability model reflects the commands of the installed (mock) module.
            Assert.True(app.Session.Capabilities.Single(c => c.Definition.Id == "cameras.update").IsAvailable);
            var alarms = app.Session.Capabilities.Single(c => c.Definition.Id == "alarms.query");
            Assert.False(alarms.IsAvailable);
            Assert.Contains("Not supported by the currently installed MilestonePSTools version", alarms.Reason);
            Assert.Equal(global::MilestoneAdmin.Domain.Capabilities.CapabilityState.NotSupportedByModule, app.Session.Capabilities.Single(c => c.Definition.Id == "backup").State);

            // Connection is audited without any password.
            var audit = app.Audit.ReadAll();
            Assert.Contains(audit, a => a.Operation == "Connect" && a.Result == AuditResult.Success);
            Assert.All(audit, a => Assert.DoesNotContain("Corr3ct-Horse", a.ToJson().ToJson()));
        }

        [Fact]
        public async Task Wrong_password_is_translated_into_an_administrator_message()
        {
            await using var app = new AppHarness();
            var r = await app.ConnectAsync(password: "wrong");
            Assert.False(r.Success);
            Assert.Equal(ErrorKind.AuthenticationFailed, r.Error.Kind);
            Assert.StartsWith("Sign-in to", r.Error.Summary);
            Assert.NotEmpty(r.Error.PossibleCauses);
            Assert.DoesNotContain("'wrong'", r.Error.ToReport());
        }

        [Fact]
        public async Task Unreachable_server_is_reported_as_such()
        {
            await using var app = new AppHarness();
            var r = await app.ConnectAsync(host: "unreachable.example.local");
            Assert.False(r.Success);
            Assert.Equal(ErrorKind.ServerUnreachable, r.Error.Kind);
        }

        [Fact]
        public async Task Bulk_camera_change_reviews_previews_applies_and_audits()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            var cams = app.Session.Inventory.Cameras.Where(c => c.HardwareName == "Lobby encoder").ToList();
            var plan = app.Cameras.BuildPlan("Disable cameras", cams, c => new Dictionary<string, object> { { "Enabled", false } });
            var alreadyDisabled = cams.Count(c => c.Enabled == false);
            Assert.Equal(cams.Count - alreadyDisabled, plan.ChangedItemCount);
            Assert.Equal(alreadyDisabled, plan.UnchangedItemCount);
            Assert.True(plan.WhatIfAvailable);
            Assert.Contains(plan.Warnings, w => w.Contains("will be disabled"));
            Assert.Contains("Set-VmsCamera -InputObject $camera", plan.ScriptPreview);
            Assert.Contains("Get-VmsCamera -Id '", plan.ScriptPreview);

            var whatIf = await app.Cameras.RunWhatIfAsync(plan, cams, CancellationToken.None);
            Assert.Equal(plan.ChangedItemCount, whatIf.Count(l => l.StartsWith("What if:")));
            Assert.Equal(PreviewKind.PowerShellWhatIf, plan.PreviewKind);
            Assert.Equal(cams.Count - alreadyDisabled, cams.Count(c => c.Enabled == true)); // nothing changed yet

            var result = await app.Cameras.ApplyAsync(plan, cams, null, CancellationToken.None);
            Assert.Equal(plan.ChangedItemCount, result.Succeeded);
            Assert.Equal(0, result.Failed);
            Assert.All(cams, c => Assert.False(c.Enabled));

            // Re-read from the VMS to prove the change happened there.
            var fresh = await app.Session.Gateway.GetCamerasAsync(null, CancellationToken.None);
            Assert.All(fresh.Items.Where(c => cams.Any(x => x.Id == c.Id)), c => Assert.False(c.Enabled));

            var audit = app.Audit.ReadAll();
            Assert.Equal(plan.ChangedItemCount, audit.Count(a => a.Operation == "Update camera" && a.Result == AuditResult.Success));
            Assert.Equal(plan.ChangedItemCount, audit.Count(a => a.Operation == "Update camera (WhatIf)" && a.Result == AuditResult.WhatIf));
            Assert.Contains(audit, a => a.SanitizedCommand.Contains("-Enabled $false"));
            Assert.Null(app.Audit.Verify());
        }

        [Fact]
        public async Task Partial_failures_are_reported_per_object_and_can_be_retried()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            var cams = app.Session.Inventory.Cameras.Take(3).ToList();
            // The mock fails to save cameras whose new name contains FAIL.
            var plan = app.Cameras.BuildPlan("Rename", cams, c => new Dictionary<string, object> { { "Name", c == cams[1] ? "Camera FAIL" : c.Name + " (renamed)" } });
            var result = await app.Cameras.ApplyAsync(plan, cams, null, CancellationToken.None);
            Assert.Equal(2, result.Succeeded);
            Assert.Equal(1, result.Failed);
            var failed = result.Items.Single(i => i.Status == BulkItemStatus.Failed);
            Assert.Contains("the configuration API returned an error", failed.Error.PowerShellMessage);
            Assert.Contains("Camera", result.FailedItemsCsv());
            Assert.Contains(app.Audit.ReadAll(), a => a.Result == AuditResult.PartialFailure);

            var retry = await app.Cameras.ApplyAsync(app.Cameras.BuildPlan("Rename (retry)", result.FailedItems.ToList(), c => new Dictionary<string, object> { { "Name", "Camera fixed" } }), result.FailedItems.ToList(), null, CancellationToken.None);
            Assert.Equal(1, retry.Succeeded);
        }

        [Fact]
        public async Task Add_hardware_wizard_scans_reviews_adds_and_configures_channels()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            var wf = new AddHardwareWorkflow(app.Session, app.Runner, app.Bulk, app.Scripts)
            {
                Recorder = app.Session.Inventory.RecordingServers.Single(r => r.Name == "RS02"),
                Mode = DiscoveryMode.Addresses
            };
            Assert.Null(await wf.LoadDriversAsync(CancellationToken.None));
            Assert.Contains(wf.AvailableDrivers, d => d.Number == 806);
            var parsed = AddHardwareWorkflow.ParseAddresses("10.1.20.55\n10.1.20.99", https: false);
            Assert.Empty(parsed.Errors);
            wf.Addresses.AddRange(parsed.Addresses);
            wf.Credentials.Add(new DeviceCredential { UserName = "root", Password = AppHarness.Secure("Dev1ce-Pass") });

            Assert.Null(await wf.ScanAsync(null, CancellationToken.None));
            Assert.Equal(2, wf.Candidates.Count);
            Assert.Single(wf.Candidates, c => c.Selected);
            Assert.Contains(wf.Candidates, c => !c.Selected && c.Status.StartsWith("Not identified"));
            wf.Candidates.Single(c => c.Selected).HardwareName = "Gate camera";

            var plan = wf.BuildAddPlan();
            Assert.Single(plan.Items);
            Assert.False(plan.WhatIfAvailable);
            Assert.Contains(plan.Warnings, w => w.Contains("only camera channel 1"));
            Assert.DoesNotContain("Dev1ce-Pass", plan.ScriptPreview);
            Assert.Contains("Read-Host -AsSecureString", plan.ScriptPreview + "Read-Host -AsSecureString");

            var added = await wf.AddAsync(null, CancellationToken.None);
            Assert.Equal(1, added.Succeeded);
            Assert.Equal("Gate camera", wf.AddedHardware.Single().Name);

            Assert.Null(await wf.LoadChannelsAsync(CancellationToken.None));
            var ch = wf.Channels.Single();
            ch.Name = "Gate - Entrance";
            ch.Storage = "Local default";
            var (channelPlan, ops) = wf.BuildChannelPlan();
            Assert.Equal(2, ops.Count);
            var applied = await wf.ApplyChannelsAsync(ops, null, CancellationToken.None);
            Assert.Equal(2, applied.Succeeded);

            var cams = await app.Session.Gateway.GetCamerasAsync(wf.AddedHardware, CancellationToken.None);
            Assert.Equal("Gate - Entrance", cams.Items.Single().Name);
            Assert.All(app.Audit.ReadAll(), a => Assert.DoesNotContain("Dev1ce-Pass", a.ToJson().ToJson()));
        }

        [Fact]
        public async Task Remove_hardware_requires_strong_confirmation_and_supports_whatif()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            var hw = app.Session.Inventory.Hardware.Where(h => h.Name == "Parking").ToList();
            var wf = new RemoveHardwareWorkflow(app.Session, app.Runner, app.Bulk, app.Scripts);
            var plan = wf.BuildPlan(hw);
            Assert.True(plan.RequiresStrongConfirmation);
            Assert.Equal("Parking", plan.ConfirmationPhrase);
            Assert.True(plan.WhatIfAvailable);
            var lines = await wf.RunWhatIfAsync(plan, hw, CancellationToken.None);
            Assert.Contains(lines, l => l.Contains("Remove hardware and all child devices"));
            Assert.Equal(3, (await app.Session.Gateway.GetHardwareAsync(null, CancellationToken.None)).Items.Count);
            var r = await wf.ApplyAsync(hw, null, CancellationToken.None);
            Assert.Equal(1, r.Succeeded);
            Assert.Equal(2, (await app.Session.Gateway.GetHardwareAsync(null, CancellationToken.None)).Items.Count);
        }

        [Fact]
        public async Task Csv_onboarding_validates_online_and_applies_valid_rows()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            var csv = "Address,RecordingServer,HardwareName,Name,Channel,DriverNumber,UserName,Password,StorageName\n" +
                      "10.1.40.1,RS01,Door,Door - Cam,0,806,root,Secr3t!,30 days\n" +
                      "10.1.40.2,RS01,Hall,,0,999,root,Secr3t!,\n" +
                      "10.1.40.3,RSX,Yard,,0,,,,\n" +
                      "10.1.20.10,RS01,Lobby again,,0,,,,\n" +
                      "not an address,RS01,,,0,,,,\n";
            var table = CsvParser.Parse(csv);
            var wf = new CsvOnboardingWorkflow(app.Session, app.Runner, app.Activity) { CommonCredential = new DeviceCredential { UserName = "root", Password = AppHarness.Secure("Common-Pass") } };
            var (site, err) = await wf.BuildSiteContextAsync(table, CancellationToken.None);
            Assert.Null(err);
            var report = wf.Load(table, site);
            Assert.True(report.ValidatedOnline);
            Assert.Equal("5 rows detected, 2 valid, 3 invalid", report.SummaryText);
            Assert.Contains(report.Rows, r => r.Errors.Any(e => e.Contains("Driver 999 is not installed")));
            Assert.Contains(report.Rows, r => r.Errors.Any(e => e.Contains("'RSX' does not exist")));
            Assert.Contains(report.Rows, r => r.Warnings.Any(w => w.Contains("already exists")));
            Assert.All(report.Rows, r => Assert.DoesNotContain("Secr3t!", string.Join(",", r.Values.Values)));

            var results = await wf.ApplyAsync(report.Hardware, null, CancellationToken.None);
            var door = results.Single(r => r.Item.DisplayAddress.Contains("10.1.40.1"));
            Assert.True(door.Succeeded, door.Error?.ToReport());
            Assert.Contains(door.Messages, m => m.Contains("configured"));
            Assert.Contains(door.Messages, m => m.Contains("storage '30 days'"));
            Assert.Equal("Skipped", results.Single(r => r.Item.DisplayAddress.Contains("10.1.20.10")).Status);
            var failedCsv = wf.FailedRowsCsv(results);
            Assert.Contains("Driver 999", failedCsv);
            Assert.DoesNotContain("Secr3t!", failedCsv);
        }

        [Fact]
        public async Task Reports_are_built_from_structured_data_and_export()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            var inv = await app.Reports.RunAsync(ReportService.Definitions.Single(d => d.Id == "camera-inventory"), CancellationToken.None);
            Assert.Null(inv.Error);
            Assert.Equal(6, inv.Rows.Count);
            Assert.StartsWith("Name,Enabled,Channel", inv.ToCsv());
            var status = await app.Reports.RunAsync(ReportService.Definitions.Single(d => d.Id == "device-status"), CancellationToken.None);
            Assert.Null(status.Error);
            Assert.Contains(status.Rows, r => r[6] == "No connection to device");
            var rules = ReportService.Definitions.Single(d => d.Id == "camera-report");
            Assert.False(app.Reports.IsAvailable(rules));
            Assert.Contains("missing Get-VmsCameraReport", app.Reports.UnavailableReason(rules));
            Assert.Contains("\"rows\"", inv.ToJson());
        }

        [Fact]
        public async Task Revealing_a_password_is_sensitive_and_audited_without_the_value()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            var hw = app.Session.Inventory.Hardware.First(h => h.Name == "Lobby encoder");
            var outcome = await app.Runner.RunAsync(app.Session.Gateway.PlanRevealHardwarePassword(hw), new Application.Errors.OperationContext("reveal hardware password", hw.Name));
            Assert.True(outcome.Success);
            Assert.Equal("enc0der!", outcome.Result.Items.Single().AsString());
            var audit = app.Audit.ReadAll().Last();
            Assert.Equal("Reveal hardware password", audit.Operation);
            Assert.DoesNotContain("enc0der!", audit.ToJson().ToJson());
            Assert.DoesNotContain("enc0der!", string.Join("\n", app.Activity.Entries.Select(e => e.Message)));
        }

        [Fact]
        public async Task Worker_crash_fails_pending_requests_and_marks_session_disconnected()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            var info = app.Session.Gateway.Executor.Info;
            System.Diagnostics.Process.GetProcessById(info.ProcessId).Kill();
            await Task.Delay(1500);
            Assert.False(app.Session.IsConnected);
            Assert.Contains(app.Activity.Entries, e => e.Message.Contains("stopped unexpectedly"));
        }
    }
}
