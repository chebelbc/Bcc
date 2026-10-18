using System;
using System.IO;
using System.Linq;
using System.Security;
using MilestoneAdmin.Application.CommandExplorer;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.Application.Workflows;
using MilestoneAdmin.Domain.Audit;
using MilestoneAdmin.Domain.Catalog;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Domain.Errors;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.Infrastructure;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.Protocol.Json;
using Xunit;

namespace MilestoneAdmin.Tests.ApplicationLayer
{
    public class ErrorTranslatorTests
    {
        private static ErrorInfo Error(string message, params string[] types)
        {
            var e = ErrorInfo.FromMessage(message, types.LastOrDefault());
            e.ExceptionTypeChain.AddRange(types);
            return e;
        }

        [Theory]
        [InlineData("connect to the management server", "No connection could be made because the target machine actively refused it", "System.Net.Sockets.SocketException", ErrorKind.ServerUnreachable)]
        [InlineData("connect to the management server", "Login failed", "VideoOS.Platform.SDK.Platform.InvalidCredentialsMIPException", ErrorKind.AuthenticationFailed)]
        [InlineData("add hardware", "Access is denied.", "System.UnauthorizedAccessException", ErrorKind.PermissionDenied)]
        [InlineData("set camera", "Requires XProtect 2021 R1", "MilestonePSTools.VmsVersionValidationException", ErrorKind.VersionNotSupported)]
        [InlineData("set camera", "Feature not available", "MilestonePSTools.VmsFeatureNotAvailableException", ErrorKind.FeatureNotLicensed)]
        [InlineData("install", "Must be elevated", "MilestonePSTools.AdminElevationRequiredException", ErrorKind.ElevationRequired)]
        [InlineData("remove hardware", "Item not found", "VideoOS.Platform.PathNotFoundMIPException", ErrorKind.ObjectNotFound)]
        [InlineData("set camera", "Cannot convert value", "System.Management.Automation.ParameterBindingException", ErrorKind.InvalidInput)]
        [InlineData("anything", "Something odd", "System.Exception", ErrorKind.OperationFailed)]
        public void Classifies_errors(string operation, string message, string type, ErrorKind expected)
        {
            var err = ErrorTranslator.Translate(Error(message, type), new OperationContext(operation, "Cam 1", "RS1"));
            Assert.Equal(expected, err.Kind);
            Assert.False(string.IsNullOrWhiteSpace(err.Summary));
            Assert.NotEmpty(err.PossibleCauses);
            Assert.Same(message, err.PowerShellError.Message);
        }

        [Fact]
        public void Timeouts_on_connect_mean_unreachable_server()
        {
            var err = ErrorTranslator.Translate(Error("The operation has timed out", "System.TimeoutException"), new OperationContext("connect to the management server", "http://mgmt"));
            Assert.Equal(ErrorKind.ServerUnreachable, err.Kind);
            Assert.Contains("http://mgmt", err.Summary);
            var other = ErrorTranslator.Translate(Error("x"), new OperationContext("add hardware"), timedOut: true);
            Assert.Equal(ErrorKind.Timeout, other.Kind);
        }

        [Fact]
        public void Eula_and_interactive_prompts_are_explained()
        {
            Assert.Equal(ErrorKind.EulaNotAccepted, ErrorTranslator.Translate(Error("You must accept the license agreement (-AcceptEula)"), new OperationContext("connect")).Kind);
            Assert.Equal(ErrorKind.InteractiveInputRequired,
                ErrorTranslator.Translate(Error("x", "MilestonePSTools.InteractiveSessionRequiredException"), new OperationContext("export")).Kind);
        }
    }

    public class AddressAndPatternTests
    {
        [Fact]
        public void Expands_ranges_lists_and_names()
        {
            var r = AddHardwareWorkflow.ParseAddresses("10.0.0.1-3, cam-a\n10.0.0.2\nhttps://10.0.1.5:8443", https: false);
            Assert.Empty(r.Errors);
            Assert.Equal(new[] { "http://10.0.0.1/", "http://10.0.0.2/", "http://10.0.0.3/", "http://cam-a/", "https://10.0.1.5:8443/" }, r.Addresses.Select(u => u.ToString()));
            var full = AddHardwareWorkflow.ParseAddresses("192.168.1.250-192.168.2.1", https: true);
            Assert.Equal(8, full.Addresses.Count);
            Assert.All(full.Addresses, u => Assert.Equal("https", u.Scheme));
        }

        [Theory]
        [InlineData("10.0.0.9-3", "before the start")]
        [InlineData("10.0.0.1-x", "not a valid IPv4 range")]
        [InlineData("10.0.0-5", "not a valid")]
        [InlineData("10.0.0.0-10.0.255.255", "more than 1024")]
        [InlineData("bad_host!", "not a valid address")]
        public void Reports_bad_input(string text, string message)
        {
            var r = AddHardwareWorkflow.ParseAddresses(text, https: false);
            Assert.Contains(r.Errors, e => e.Contains(message));
            Assert.Empty(r.Addresses);
        }

        [Fact]
        public void Expands_naming_patterns()
        {
            var cam = new CameraInfo { Name = "AXIS M1065 (10.0.0.1) - Camera 1", HardwareName = "Lobby", HardwareAddress = "http://10.0.0.1/", RecordingServerName = "RS1", Channel = 0 };
            Assert.Equal("RS1-Lobby-Cam01-007", CameraEditWorkflow.ExpandPattern("{RecordingServer}-{Hardware}-Cam{Channel1:00}-{Index:000}", cam, 7));
            Assert.Equal("10.0.0.1 ch0", CameraEditWorkflow.ExpandPattern("{Address} ch{Channel}", cam, 1));
            Assert.Equal(cam.Name, CameraEditWorkflow.ExpandPattern("", cam, 1));
            Assert.Equal("{Unknown}", CameraEditWorkflow.ExpandPattern("{Unknown}", cam, 1));
        }
    }

    public class CommandFormTests
    {
        private static readonly CommandCatalog Catalog = CommandCatalog.Load(TestPaths.StaticCatalog, ClassificationRules.Embedded);

        private static SecureString Secure(string s)
        {
            var ss = new SecureString();
            foreach (var c in s) ss.AppendChar(c);
            return ss;
        }

        [Fact]
        public void Builds_typed_parameters_from_the_form_never_script_text()
        {
            var cmd = Catalog.Find("Set-VmsHardware");
            var form = CommandFormBuilder.Build(cmd, cmd.DefaultParameterSet, expertMode: true);
            Assert.Null(form.ExecutionBlockedReason);
            var hardware = form.Fields.Single(f => f.Name == "Hardware");
            Assert.True(hardware.Mandatory);
            Assert.Equal("Hardware", form.Fields.First().Name);

            Assert.Contains("Hardware is required.", CommandFormBuilder.Validate(form));

            hardware.Objects.Add(new HardwareInfo { Handle = "h1", ObjectType = "Hardware", Name = "Lobby", Id = Guid.NewGuid() });
            form.Fields.Single(f => f.Name == "Name").Text = "x'; Remove-VmsHardware -Hardware (Get-VmsHardware) #";
            form.Fields.Single(f => f.Name == "Password").Secret = Secure("hunter2");
            form.Fields.Single(f => f.Name == "PassThru").Flag = true;
            Assert.Empty(CommandFormBuilder.Validate(form));

            var op = CommandFormBuilder.BuildOperation(form, whatIf: true);
            Assert.True(op.WhatIf);
            Assert.True(op.AllowStateChange);
            var step = op.Pipeline.Single();
            Assert.Equal("Set-VmsHardware", step.Command);
            var p = step.Parameters.ToDictionary(k => k.Key, k => k.Value);
            Assert.Equal(ParameterValueKind.Literal, p["Name"].Kind);
            Assert.Equal("x'; Remove-VmsHardware -Hardware (Get-VmsHardware) #", p["Name"].Literal.AsString());
            Assert.Equal(ParameterValueKind.Secret, p["Password"].Kind);
            Assert.Equal(ParameterValueKind.Switch, p["PassThru"].Kind);
            Assert.False(p.ContainsKey("Enabled"));

            // The secret travels only in the request's separate "secrets" map (written to the worker's stdin pipe);
            // the pipeline, the generated script and the redacted diagnostic form never contain it.
            var request = op.ToRequest("r1");
            Assert.Equal("hunter2", request.Secrets[p["Password"].SecretId]);
            Assert.DoesNotContain("hunter2", step.ToJson().ToJson());
            Assert.DoesNotContain("hunter2", request.ToRedactedJson().ToJson());
        }

        [Fact]
        public void Rejects_values_that_do_not_match_the_parameter_type()
        {
            var cmd = Catalog.Find("Set-VmsHardware");
            var form = CommandFormBuilder.Build(cmd, cmd.DefaultParameterSet, expertMode: true);
            form.Fields.Single(f => f.Name == "Hardware").Objects.Add(new HardwareInfo { Handle = "h1", ObjectType = "Hardware", Name = "Lobby" });
            form.Fields.Single(f => f.Name == "Address").Text = "not a uri";
            Assert.Contains(CommandFormBuilder.Validate(form), e => e.StartsWith("Address:", StringComparison.Ordinal));
        }

        [Fact]
        public void Commands_that_need_a_workflow_or_the_app_are_blocked()
        {
            var blocked = Catalog.Commands.Where(c => c.GuiSuitability == GuiSuitability.ExpertOnly || c.GuiSuitability == GuiSuitability.AppManaged || c.GuiSuitability == GuiSuitability.NotSuitable).ToList();
            Assert.NotEmpty(blocked);
            foreach (var c in blocked)
                Assert.NotNull(CommandFormBuilder.Build(c, c.DefaultParameterSet, expertMode: true).ExecutionBlockedReason);
            var connect = Catalog.Find("Connect-Vms");
            Assert.NotNull(CommandFormBuilder.Build(connect, connect.DefaultParameterSet, expertMode: true).ExecutionBlockedReason);
        }

        [Fact]
        public void Read_only_commands_never_allow_state_change()
        {
            var cmd = Catalog.Find("Get-VmsRecordingServer");
            var form = CommandFormBuilder.Build(cmd, cmd.DefaultParameterSet, expertMode: false);
            var op = CommandFormBuilder.BuildOperation(form, whatIf: false);
            Assert.False(op.AllowStateChange);
            Assert.False(op.Output.AllowSensitive);
        }

        [Theory]
        [InlineData("RecordingServer", "Recording Server")]
        [InlineData("IncludePlainTextPasswords", "Include Plain Text Passwords")]
        [InlineData("SSLEnabled", "SSL Enabled")]
        public void Humanizes_parameter_names(string name, string label)
        {
            Assert.Equal(label, CommandFormBuilder.Humanize(name));
        }
    }

    public class AuditStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "xpa-audit-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }

        private static AuditRecord Record(string op) => new AuditRecord
        {
            Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), WindowsUser = "CONTOSO\\admin", Operation = op, Target = "Cam 1",
            SanitizedCommand = "Set-VmsCamera -Camera $camera1 -Name 'Lobby'", Result = AuditResult.Success, DurationMs = 12
        };

        [Fact]
        public void Hash_chain_detects_edits_and_deletions()
        {
            var path = Path.Combine(_dir, "audit.jsonl");
            var store = new JsonLinesAuditStore(path);
            store.Append(Record("a"));
            store.Append(Record("b"));
            store.Append(Record("c"));
            Assert.Null(store.Verify());
            Assert.Equal(new long[] { 1, 2, 3 }, store.ReadAll().Select(r => r.Sequence));

            var lines = File.ReadAllLines(path);
            File.WriteAllLines(path, new[] { lines[0], lines[1].Replace("Cam 1", "Cam 2"), lines[2] });
            Assert.Equal(2, store.Verify());

            File.WriteAllLines(path, new[] { lines[0], lines[2] });
            Assert.Equal(3, store.Verify());

            File.WriteAllLines(path, new[] { lines[0], "{not json" });
            Assert.Equal(0, store.Verify());
        }

        [Fact]
        public void Records_round_trip()
        {
            var r = Record("x");
            r.Error = "boom";
            var back = AuditRecord.FromJson(JsonNode.Parse(r.ToJson().ToJson()).AsObject());
            Assert.Equal(r.ComputeHash(), back.ComputeHash());
            Assert.Equal("CONTOSO\\admin", back.WindowsUser);
            Assert.Equal(AuditResult.Success, back.Result);
        }
    }
}
