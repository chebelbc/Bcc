using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.Protocol.Json;
using Xunit;

namespace MilestoneAdmin.Tests.Engine
{
    [Collection("PowerShell")]
    public class WorkerEngineTests
    {
        [Fact]
        public void Reads_objects_as_structured_data_not_console_text()
        {
            using var h = new EngineHarness();
            var r = h.Invoke("Get-VmsRecordingServer");
            Assert.True(r.Success, r.AllText);
            Assert.Equal(2, r.Items.Count);
            var rs = r.Items[0];
            Assert.Equal("VideoOS.Platform.ConfigurationItems.RecordingServer", rs.GetString("$type"));
            Assert.Equal("RS01", rs.GetString("Name"));
            Assert.Equal("11111111-1111-1111-1111-000000000001", rs.GetString("Id"));
            Assert.Equal(true, rs["Enabled"].AsBool());
            Assert.StartsWith("h", rs.GetString("$handle"));
        }

        [Fact]
        public void Explicit_property_list_limits_output()
        {
            using var h = new EngineHarness();
            var r = h.Invoke("Get-VmsCamera", s => s.With("EnableFilter", ParameterValue.String("All")), tweak: q =>
            {
                q.Output.Properties.AddRange(new[] { "Name", "Enabled" });
            });
            Assert.True(r.Success, r.AllText);
            Assert.Equal(6, r.Items.Count);
            Assert.All(r.Items, i => Assert.Equal(new[] { "$type", "$handle", "Name", "Enabled" }, i.Keys.ToArray()));
        }

        [Fact]
        public void Default_query_returns_only_enabled_devices_like_the_real_module()
        {
            using var h = new EngineHarness();
            var enabled = h.Invoke("Get-VmsCamera");
            var all = h.Invoke("Get-VmsCamera", s => s.With("EnableFilter", ParameterValue.String("All")));
            Assert.True(enabled.Items.Count < all.Items.Count);
        }

        [Fact]
        public void Commands_outside_the_module_are_rejected()
        {
            using var h = new EngineHarness();
            var r = h.Invoke("Remove-Item", s => s.With("Path", ParameterValue.String("/")), allowStateChange: true);
            Assert.True(r.IsFailed);
            Assert.Contains("not a command exported by MilestonePSTools", r.FailureMessage);
        }

        [Theory]
        [InlineData("Get-VmsCamera; Remove-Item /")]
        [InlineData("Get-VmsCamera | Remove-Item")]
        [InlineData("Microsoft.PowerShell.Management\\Remove-Item")]
        [InlineData("$(Remove-Item /)")]
        public void Command_names_with_script_syntax_are_rejected(string name)
        {
            using var h = new EngineHarness();
            var r = h.Invoke(name);
            Assert.True(r.IsFailed);
        }

        [Fact]
        public void Injection_strings_are_bound_as_literal_values()
        {
            using var h = new EngineHarness();
            const string evil = "x'; Remove-VmsHardware -Hardware (Get-VmsHardware) -Confirm:$false; '$(Get-Date)`\"";
            var cam = h.Invoke("Get-VmsCamera").Items.First();
            var r = h.Invoke("Set-VmsCamera", s => s
                .With("InputObject", ParameterValue.Handle(cam.GetString("$handle")))
                .With("Name", ParameterValue.String(evil))
                .With("PassThru", ParameterValue.Switch()), allowStateChange: true);
            Assert.True(r.Success, r.AllText);
            Assert.Equal(evil, r.Items.Single().GetString("Name"));
            Assert.Equal(3, h.Invoke("Get-VmsHardware", s => s.With("EnableFilter", ParameterValue.String("All"))).Items.Count);
        }

        [Fact]
        public void Script_block_parameters_are_refused()
        {
            using var h = new EngineHarness();
            var r = h.Invoke("Invoke-MockScriptBlockProbe", s => s.With("Filter", ParameterValue.String("Remove-Item /")), allowStateChange: true);
            Assert.True(r.IsFailed || !r.Success);
            Assert.Contains("script block", r.FailureMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Unknown_parameters_and_abbreviations_are_refused()
        {
            using var h = new EngineHarness();
            var r1 = h.Invoke("Get-VmsCamera", s => s.With("Nam", ParameterValue.String("x")));
            Assert.Contains("no parameter named 'Nam'", r1.FailureMessage);
            var r2 = h.Invoke("Get-VmsCamera", s => s.With("ErrorAction", ParameterValue.String("Ignore")));
            Assert.Contains("controlled by the application", r2.FailureMessage);
        }

        [Fact]
        public void State_changing_commands_require_explicit_approval()
        {
            using var h = new EngineHarness();
            var hw = h.Invoke("Get-VmsHardware").Items.First();
            var r = h.Invoke("Remove-VmsHardware", s => s.With("Hardware", ParameterValue.Handle(hw.GetString("$handle"))), allowStateChange: false);
            Assert.Contains("without state-change approval", r.FailureMessage);
            Assert.Equal(3, h.Invoke("Get-VmsHardware", s => s.With("EnableFilter", ParameterValue.String("All"))).Items.Count);
        }

        [Fact]
        public void ConfirmImpact_high_command_runs_after_approval_without_prompting()
        {
            using var h = new EngineHarness();
            var hw = h.Invoke("Get-VmsHardware", s => s.With("Name", ParameterValue.String("Parking")).With("EnableFilter", ParameterValue.String("All"))).Items.Single();
            var r = h.Invoke("Remove-VmsHardware", s => s.With("Hardware", ParameterValue.Handle(hw.GetString("$handle"))), allowStateChange: true);
            Assert.True(r.Success, r.AllText);
            Assert.Equal(2, h.Invoke("Get-VmsHardware", s => s.With("EnableFilter", ParameterValue.String("All"))).Items.Count);
        }

        [Fact]
        public void WhatIf_output_is_captured_as_its_own_stream_and_changes_nothing()
        {
            using var h = new EngineHarness();
            var cam = h.Invoke("Get-VmsCamera").Items.First();
            var r = h.Invoke("Set-VmsCamera", s => s
                .With("InputObject", ParameterValue.Handle(cam.GetString("$handle")))
                .With("Name", ParameterValue.String("Entrance")), allowStateChange: true, whatIf: true);
            Assert.True(r.Success, r.AllText);
            var whatIf = r.Stream("whatif").ToList();
            Assert.Single(whatIf);
            Assert.Contains("Update camera settings", whatIf[0]);
            Assert.True(r.Result.GetBool("whatIf"));
            Assert.Equal(cam.GetString("Name"), h.Invoke("Get-VmsCamera", s => s.With("Id", ParameterValue.String(cam.GetString("Id")))).Items.Single().GetString("Name"));
        }

        [Fact]
        public void WhatIf_is_refused_for_commands_without_ShouldProcess()
        {
            using var h = new EngineHarness();
            var rs = h.Invoke("Get-VmsRecordingServer").Items.First();
            var r = h.Invoke("Add-VmsHardware", s => s
                .With("RecordingServer", ParameterValue.Handle(rs.GetString("$handle")))
                .With("HardwareAddress", ParameterValue.String("http://10.9.9.9"))
                .With("Credential", ParameterValue.Credential("root", "p")), allowStateChange: true, whatIf: true, tweak: q => q.Secrets["p"] = "secret1");
            Assert.Contains("does not support -WhatIf", r.FailureMessage);
        }

        [Fact]
        public void Not_connected_commands_are_blocked_instead_of_auto_connecting()
        {
            using var h = new EngineHarness(connect: false);
            var r = h.Invoke("Get-VmsRecordingServer");
            Assert.Contains("Not connected", r.FailureMessage);
            var t = h.Invoke("Test-VmsConnection");
            Assert.True(t.Success, t.AllText);
            Assert.False(t.Result.GetArray("items")[0].AsBool());
        }

        [Fact]
        public void Interactive_commands_are_refused()
        {
            using var h = new EngineHarness();
            var r = h.Invoke("Select-Camera");
            Assert.Contains("interactive", r.FailureMessage, StringComparison.OrdinalIgnoreCase);
            var r2 = h.Invoke("Connect-Vms", s => s.With("ShowDialog", ParameterValue.Switch()));
            Assert.Contains("interactive", r2.FailureMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Secrets_are_scrubbed_from_errors_and_never_echoed()
        {
            using var h = new EngineHarness(connect: false);
            var req = new InvokeRequest();
            req.Pipeline.Add(new PipelineStep("Connect-Vms")
                .With("ServerAddress", ParameterValue.String("http://mgmt.example.local"))
                .With("Credential", ParameterValue.Credential("admin", "s1"))
                .With("AcceptEula", ParameterValue.Switch()));
            req.Secrets["s1"] = "wrong";
            var r = h.Invoke(req);
            Assert.False(r.Success);
            // The mock deliberately puts the password in its exception message.
            Assert.DoesNotContain("'wrong'", r.AllText);
            Assert.Contains("********", r.AllText);
            Assert.Equal("System.UnauthorizedAccessException", r.Result.GetObject("terminatingError").GetString("exceptionType"));
        }

        [Fact]
        public void Secret_properties_in_output_are_redacted()
        {
            using var h = new EngineHarness();
            var rs = h.Invoke("Get-VmsRecordingServer").Items.First();
            var req = new InvokeRequest { AllowStateChange = true };
            req.Pipeline.Add(new PipelineStep("Start-VmsHardwareScan")
                .With("RecordingServer", ParameterValue.Handle(rs.GetString("$handle")))
                .With("Address", ParameterValue.StringList(new[] { "http://10.1.20.55" }))
                .With("Credential", ParameterValue.Credential("root", "p1")));
            req.Secrets["p1"] = "Sc4nP4ss";
            var r = h.Invoke(req);
            Assert.True(r.Success, r.AllText);
            var item = r.Items.Single();
            Assert.Equal("********", item.GetString("Password"));
            Assert.DoesNotContain("Sc4nP4ss", r.AllText);
            Assert.Equal("root", item.GetString("UserName"));
        }

        [Fact]
        public void Sensitive_commands_require_explicit_reveal_approval()
        {
            using var h = new EngineHarness();
            var hw = h.Invoke("Get-VmsHardware").Items.First();
            var denied = h.Invoke("Get-VmsHardwarePassword", s => s.With("Hardware", ParameterValue.Handle(hw.GetString("$handle"))));
            Assert.Contains("returns secrets", denied.FailureMessage);
            var allowed = h.Invoke("Get-VmsHardwarePassword", s => s.With("Hardware", ParameterValue.Handle(hw.GetString("$handle"))), tweak: q => q.Output.AllowSensitive = true);
            Assert.True(allowed.Success, allowed.AllText);
            Assert.Equal("enc0der!", allowed.Result.GetArray("items")[0].AsString());
        }

        [Fact]
        public void Handles_round_trip_and_scan_results_feed_add_hardware()
        {
            using var h = new EngineHarness();
            var rs = h.Invoke("Get-VmsRecordingServer", s => s.With("Name", ParameterValue.String("RS02"))).Items.Single();
            var scan = h.Invoke("Start-VmsHardwareScan", s => s
                .With("RecordingServer", ParameterValue.Handle(rs.GetString("$handle")))
                .With("Express", ParameterValue.Switch()), allowStateChange: true);
            Assert.True(scan.Success, scan.AllText);
            Assert.Equal(2, scan.Items.Count);
            var add = h.Invoke("Add-VmsHardware", s => s
                .With("HardwareScan", ParameterValue.List(scan.Items.Select(i => ParameterValue.Handle(i.GetString("$handle")))))
                .With("Name", ParameterValue.String("New camera")), allowStateChange: true);
            Assert.True(add.Success, add.AllText);
            Assert.Equal(2, add.Items.Count);
            Assert.All(add.Items, i => Assert.Equal("New camera", i.GetString("Name")));
        }

        [Fact]
        public void References_are_resolved_by_id_after_handles_are_gone()
        {
            using var h = new EngineHarness();
            var cam = h.Invoke("Get-VmsCamera").Items.First();
            h.Send(new JsonObject { { "type", MessageTypes.ReleaseHandles }, { "id", "rel" }, { "handles", new JsonArray { cam.GetString("$handle") } } });
            var stale = h.Invoke("Set-VmsCamera", s => s.With("InputObject", ParameterValue.Handle(cam.GetString("$handle"))).With("Enabled", ParameterValue.Boolean(false)), allowStateChange: true);
            Assert.Contains("no longer available", stale.FailureMessage);
            var byRef = h.Invoke("Set-VmsCamera", s => s
                .With("InputObject", ParameterValue.Reference("Camera", cam.GetString("Id")))
                .With("Enabled", ParameterValue.Boolean(false))
                .With("PassThru", ParameterValue.Switch()), allowStateChange: true);
            Assert.True(byRef.Success, byRef.AllText);
            Assert.False(byRef.Items.Single()["Enabled"].AsBool());
        }

        [Fact]
        public void Non_terminating_errors_are_reported_per_object_with_partial_results()
        {
            using var h = new EngineHarness();
            var cams = h.Invoke("Get-VmsCamera", s => s.With("EnableFilter", ParameterValue.String("All"))).Items.Take(2).ToList();
            // Name containing FAIL makes the mock report a save failure for that camera only.
            var r = h.Invoke("Set-VmsCamera", s => s
                .With("InputObject", ParameterValue.Handle(cams[0].GetString("$handle")))
                .With("Name", ParameterValue.String("Camera FAIL"))
                .With("PassThru", ParameterValue.Switch()), allowStateChange: true);
            Assert.True(r.Success);
            Assert.True(r.Result.GetBool("hadErrors"));
            var err = r.Result.GetArray("errors")[0].AsObject();
            Assert.Equal("SaveFailed,Set-VmsCamera", err.GetString("fullyQualifiedErrorId"));
            Assert.Equal("WriteError", err.GetString("category"));
            Assert.Contains(r.Events, e => e.GetString("type") == MessageTypes.ErrorRecord);
        }

        [Fact]
        public void Progress_warnings_verbose_and_host_output_are_streamed()
        {
            using var h = new EngineHarness(connect: false);
            var req = new InvokeRequest();
            req.Pipeline.Add(new PipelineStep("Connect-Vms")
                .With("ServerAddress", ParameterValue.String("http://mgmt.example.local"))
                .With("AcceptEula", ParameterValue.Switch()));
            var c = h.Invoke(req);
            Assert.Contains(c.Stream("host"), m => m.Contains("Connected to"));

            var rs = h.Invoke("Get-VmsRecordingServer").Items.First();
            var scan = h.Invoke("Start-VmsHardwareScan", s => s
                .With("RecordingServer", ParameterValue.Handle(rs.GetString("$handle")))
                .With("Address", ParameterValue.StringList(new[] { "http://10.1.20.99" })), allowStateChange: true, tweak: q => q.Verbose = true);
            Assert.Contains(scan.Events, e => e.GetString("type") == MessageTypes.Progress);
            Assert.Contains(scan.Stream("warning"), m => m.Contains("No supported device"));
        }

        [Fact]
        public async Task Long_running_command_can_be_cancelled()
        {
            using var h = new EngineHarness();
            var req = new InvokeRequest { Id = "long" };
            req.Pipeline.Add(new PipelineStep("Wait-VmsTask").With("Path", ParameterValue.StringList(new[] { "Task[1]" })));
            var task = Task.Run(() => h.Invoke(req));
            await Task.Delay(1500);
            h.Engine.Cancel("long");
            var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.True(finished == task, "Cancellation did not stop the pipeline");
            var r = await task;
            Assert.False(r.Success);
            Assert.True(r.Result.GetBool("cancelled"));
            Assert.False(r.Result.GetBool("timedOut"));
        }

        [Fact]
        public void Timeouts_stop_the_pipeline_and_are_reported_as_timeouts()
        {
            using var h = new EngineHarness();
            var r = h.Invoke("Wait-VmsTask", s => s.With("Path", ParameterValue.StringList(new[] { "Task[1]" })), tweak: q => q.TimeoutSeconds = 2);
            Assert.False(r.Success);
            Assert.True(r.Result.GetBool("timedOut"));
            Assert.Equal("System.TimeoutException", r.Result.GetObject("terminatingError").GetString("exceptionType"));
        }

        [Fact]
        public void Runtime_discovery_reads_metadata_without_running_commands()
        {
            using var h = new EngineHarness(connect: false);
            var r = h.Send(new JsonObject { { "type", MessageTypes.Discover }, { "id", "d" } }).Last();
            Assert.True(r.GetBool("success"), r.ToJson());
            var catalog = r.GetArray("items")[0].AsObject();
            Assert.Equal("runtime", catalog.GetObject("source").GetString("kind"));
            Assert.Equal("25.2.61", catalog.GetString("moduleVersion"));
            var cmds = catalog.GetArray("commands").Select(c => c.AsObject()).ToList();
            var set = cmds.Single(c => c.GetString("name") == "Set-VmsCamera");
            Assert.True(set.GetBool("supportsShouldProcess"));
            Assert.True(set.GetObject("requirements")["vmsConnection"].AsBool());
            var remove = cmds.Single(c => c.GetString("name") == "Remove-VmsHardware");
            Assert.Equal("High", remove.GetString("confirmImpact"));
            var select = cmds.Single(c => c.GetString("name") == "Select-Camera");
            Assert.True(select.GetObject("requirements").GetBool("interactiveSession"));
            var get = cmds.Single(c => c.GetString("name") == "Get-VmsCamera");
            Assert.Contains("Get-Camera", get.GetArray("aliases").Strings());
            var ef = get.GetArray("parameters").Select(p => p.AsObject()).Single(p => p.GetString("name") == "EnableFilter");
            Assert.Equal(new[] { "All", "Enabled", "Disabled" }, ef.GetArray("acceptedValues").Strings().ToArray());
            // Still not connected: discovery executed nothing.
            Assert.False(h.Invoke("Test-VmsConnection").Result.GetArray("items")[0].AsBool());
        }

        [Fact]
        public void Environment_probe_reports_module_and_powershell()
        {
            using var h = new EngineHarness(connect: false);
            var r = h.Send(new JsonObject { { "type", MessageTypes.Environment }, { "id", "e" } }).Last();
            Assert.True(r.GetBool("success"), r.ToJson());
            var env = r.GetArray("items")[0].AsObject();
            Assert.False(string.IsNullOrEmpty(env.GetString("psVersion")));
            Assert.Equal("25.2.61", env.GetString("loadedModuleVersion"));
            Assert.Equal("FullLanguage", env.GetString("languageMode"));
        }
    }
}
