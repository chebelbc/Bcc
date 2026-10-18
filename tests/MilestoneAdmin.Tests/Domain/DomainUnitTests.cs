using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation.Language;
using MilestoneAdmin.Domain.Capabilities;
using MilestoneAdmin.Domain.Changes;
using MilestoneAdmin.Domain.Csv;
using MilestoneAdmin.Domain.Scripting;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.Protocol.Json;
using Xunit;

namespace MilestoneAdmin.Tests.Domain
{
    public class JsonTests
    {
        [Fact]
        public void Round_trips_nested_values_and_escapes()
        {
            var text = "{\"s\":\"a\\\"b\\\\c\\n\\u00e9\\ud83d\\ude00\",\"n\":-12.5e2,\"i\":9007199254740993,\"b\":true,\"z\":null,\"a\":[1,[2,{}]]}";
            var node = JsonNode.Parse(text).AsObject();
            Assert.Equal("a\"b\\c\né\U0001F600", node.GetString("s"));
            Assert.Equal(-1250d, node.GetDouble("n"));
            Assert.Equal(9007199254740993L, node.GetInt64("i"));
            Assert.True(node.GetBool("b"));
            Assert.Equal(JsonKind.Null, node["z"].Kind);
            var again = JsonNode.Parse(node.ToJson()).AsObject();
            Assert.Equal(node.ToJson(), again.ToJson());
            Assert.Equal(node.ToJson(), JsonNode.Parse(node.ToJson(true)).ToJson());
        }

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("{\"a\":}")]
        [InlineData("[1,]")]
        [InlineData("{\"a\":1} x")]
        [InlineData("\"unterminated")]
        [InlineData("tru")]
        [InlineData("{'a':1}")]
        public void Rejects_malformed_input(string text)
        {
            Assert.ThrowsAny<FormatException>(() => JsonNode.Parse(text));
        }

        [Fact]
        public void Rejects_excessive_nesting()
        {
            var deep = new string('[', JsonParser.MaxDepth + 2) + new string(']', JsonParser.MaxDepth + 2);
            Assert.Throws<JsonParseException>(() => JsonNode.Parse(deep));
        }

        [Fact]
        public void Writer_escapes_control_characters_so_one_message_stays_one_line()
        {
            var json = new JsonObject { { "m", "line1\nline2\r\u0001\u2028" } }.ToJson();
            Assert.DoesNotContain("\n", json);
            Assert.DoesNotContain("\r", json);
            Assert.Equal("line1\nline2\r\u0001\u2028", JsonNode.Parse(json).AsObject().GetString("m"));
        }
    }

    public class CsvTests
    {
        [Fact]
        public void Parses_quoted_fields_with_delimiters_quotes_and_line_breaks()
        {
            var t = CsvParser.Parse("﻿Name,Address,Description\r\n\"Cam, 1\",10.0.0.1,\"He said \"\"hi\"\"\nsecond line\"\r\n\r\nCam2,10.0.0.2\r\n");
            Assert.Equal(new[] { "Name", "Address", "Description" }, t.Headers);
            Assert.Equal(2, t.Rows.Count);
            Assert.Equal("Cam, 1", t.Rows[0][0]);
            Assert.Equal("He said \"hi\"\nsecond line", t.Rows[0][2]);
            Assert.Equal(string.Empty, t.Rows[1][2]);
            Assert.Equal(new[] { 2, 5 }, t.RowLineNumbers);
        }

        [Fact]
        public void Detects_semicolon_files()
        {
            Assert.Equal(';', CsvParser.DetectDelimiter("Name;Address;Channel"));
            Assert.Equal(',', CsvParser.DetectDelimiter("Name,Address,Channel"));
        }

        [Fact]
        public void Rejects_duplicate_headers_and_overlong_rows()
        {
            Assert.Throws<CsvFormatException>(() => CsvParser.Parse("Name,name\n1,2"));
            var ex = Assert.Throws<CsvFormatException>(() => CsvParser.Parse("A,B\n1,2,3"));
            Assert.Equal(2, ex.Line);
        }

        [Theory]
        [InlineData("=HYPERLINK(\"http://x\")")]
        [InlineData("+cmd|' /C calc'!A0")]
        [InlineData("-2+3")]
        [InlineData("@SUM(1)")]
        public void Escapes_formula_injection(string value)
        {
            var escaped = CsvParser.Escape(value);
            var parsed = CsvParser.Parse("V\n" + escaped).Rows[0][0];
            Assert.StartsWith("'", parsed);
            Assert.Equal(value, parsed.Substring(1));
        }

        [Fact]
        public void Leaves_negative_numbers_alone()
        {
            Assert.Equal("-12.5", CsvParser.Escape("-12.5"));
        }

        [Fact]
        public void Write_and_parse_round_trip()
        {
            var rows = new List<IReadOnlyList<string>> { new[] { "a,b", "x\"y" }, new[] { "line\nbreak", "" } };
            var parsed = CsvParser.Parse(CsvParser.Write(new[] { "H1", "H2" }, rows));
            Assert.Equal("a,b", parsed.Rows[0][0]);
            Assert.Equal("x\"y", parsed.Rows[0][1]);
            Assert.Equal("line\nbreak", parsed.Rows[1][0]);
        }
    }

    public class HardwareCsvValidatorTests
    {
        private const string Header = "DeviceType,Name,Address,Channel,UserName,Password,DriverNumber,RecordingServer,Enabled,StorageName,Coordinates";

        private static HardwareCsvReport Validate(string body, string defaultRs = "RS1", HardwareCsvSiteContext site = null) =>
            HardwareCsvValidator.Validate(CsvParser.Parse(Header + "\n" + body), defaultRs, site);

        [Fact]
        public void Reports_counts_in_the_documented_format()
        {
            var r = Validate("Camera,Cam 1,10.0.0.1,0,root,pw,806,,true,,\n" +
                             "Camera,Cam 2,10.0.0.1,1,root,pw,806,,yes,,\n" +
                             "Camera,Bad,10.0.0,0,,,,,,,\n" +
                             "Toaster,X,10.0.0.9,0,,,,,,,\n" +
                             "Camera,Dup,10.0.0.1,1,,,,,,,");
            Assert.Equal("5 rows detected, 2 valid, 3 invalid", r.SummaryText);
            Assert.Contains(r.Rows[2].Errors, e => e.Contains("Invalid Address"));
            Assert.Contains(r.Rows[3].Errors, e => e.Contains("Invalid DeviceType"));
            Assert.Contains(r.Rows[4].Errors, e => e.Contains("Duplicate of line 3"));
            Assert.Single(r.Hardware);
            Assert.Equal(2, r.Hardware[0].Channels.Count);
            Assert.True(r.Hardware[0].HasCsvCredential);
        }

        [Fact]
        public void Passwords_are_masked_in_row_values_and_flagged()
        {
            var r = Validate("Camera,Cam,10.0.0.1,0,root,Secr3t!,,,,,");
            Assert.Equal("********", r.Rows[0].Get("Password"));
            Assert.DoesNotContain(r.Rows, row => row.Values.Values.Any(v => v.Contains("Secr3t!")));
            Assert.Contains(r.FileWarnings, w => w.Contains("plain-text passwords"));
            Assert.Equal(7, r.Hardware[0].Password.Length);
        }

        [Fact]
        public void Validates_values()
        {
            var r = Validate("Camera,A,10.0.0.1,-1,,,0,,maybe,,\"91,0\"");
            var errors = string.Join("|", r.Rows[0].Errors);
            Assert.Contains("Invalid Coordinates", errors);
            Assert.Contains("Invalid Channel", errors);
            Assert.Contains("Invalid DriverNumber", errors);
            Assert.Contains("Invalid Enabled", errors);
        }

        [Fact]
        public void Requires_address_column_and_recording_server()
        {
            var noAddress = HardwareCsvValidator.Validate(CsvParser.Parse("Name\nx"));
            Assert.False(noAddress.CanApply);
            Assert.Contains(noAddress.FileErrors, e => e.Contains("'Address'"));
            var noRs = Validate("Camera,A,10.0.0.1,0,,,,,,,", defaultRs: null);
            Assert.Contains(noRs.Rows[0].Errors, e => e.Contains("RecordingServer is required"));
        }

        [Fact]
        public void Online_validation_checks_site_objects()
        {
            var site = new HardwareCsvSiteContext();
            site.RecordingServers.Add("RS1");
            site.DriverNumbers["RS1"] = new HashSet<int> { 806 };
            site.StorageNames["RS1"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Local default" };
            site.ExistingAddresses.Add("10.0.0.5");
            var r = Validate("Camera,A,10.0.0.1,0,,,999,,,,\nCamera,B,10.0.0.2,0,,,,RS9,,,\nCamera,C,10.0.0.3,0,,,,,,Archive,\nCamera,D,10.0.0.5,0,,,806,,,Local default,", site: site);
            Assert.Contains(r.Rows[0].Errors, e => e.Contains("Driver 999 is not installed"));
            Assert.Contains(r.Rows[1].Errors, e => e.Contains("'RS9' does not exist"));
            Assert.Contains(r.Rows[2].Errors, e => e.Contains("Storage 'Archive'"));
            Assert.True(r.Rows[3].IsValid);
            Assert.Contains(r.Rows[3].Warnings, w => w.Contains("already exists"));
            Assert.True(r.Hardware.Single().AlreadyOnSite);
        }

        [Theory]
        [InlineData("10.0.0.1", "10.0.0.1")]
        [InlineData("http://10.0.0.1:8080/path?x", "10.0.0.1:8080")]
        [InlineData("https://Cam.Example.local", "cam.example.local")]
        [InlineData("camera-01", "camera-01")]
        public void Parses_addresses(string text, string normalized)
        {
            Assert.True(HardwareCsvValidator.TryParseAddress(text, out var uri));
            Assert.Equal(normalized, HardwareCsvValidator.NormalizeAddress(uri));
        }

        [Theory]
        [InlineData("10.0.0")]
        [InlineData("167772161")]
        [InlineData("http://10.1/")]
        [InlineData("999.1.1.1")]
        [InlineData("ftp://10.0.0.1")]
        [InlineData("")]
        [InlineData("http://")]
        public void Rejects_bad_addresses(string text)
        {
            Assert.False(HardwareCsvValidator.TryParseAddress(text, out _));
        }

        [Fact]
        public void Coordinates_must_be_in_range()
        {
            Assert.True(HardwareCsvValidator.TryParseCoordinates("55.67, 12.56", out var lat, out var lon));
            Assert.Equal(55.67, lat);
            Assert.Equal(12.56, lon);
            Assert.False(HardwareCsvValidator.TryParseCoordinates("91,0", out _, out _));
            Assert.False(HardwareCsvValidator.TryParseCoordinates("1;2", out _, out _));
        }
    }

    public class ScriptGeneratorTests
    {
        private static string ParseSingleString(string literal)
        {
            var ast = Parser.ParseInput(literal, out _, out var errors);
            Assert.Empty(errors);
            var s = ast.FindAll(a => a is StringConstantExpressionAst, true).Cast<StringConstantExpressionAst>().ToList();
            Assert.Single(s);
            Assert.Equal(StringConstantType.SingleQuoted, s[0].StringConstantType);
            // The whole input must be just that one string: no commands, pipelines or sub-expressions.
            Assert.Single(ast.EndBlock.Statements);
            return s[0].Value;
        }

        [Theory]
        [InlineData("plain")]
        [InlineData("O'Brien")]
        [InlineData("x'; Remove-VmsHardware -Hardware (Get-VmsHardware); '")]
        [InlineData("smart ‘quotes’ ‚ ‛")]
        [InlineData("$(Get-Process) `n $env:USERNAME")]
        [InlineData("")]
        public void Quoted_literals_parse_back_to_the_same_value(string value)
        {
            var literal = PowerShellLiteral.Quote(value);
            Assert.Equal(value, ParseSingleString(literal));
        }

        [Fact]
        public void Variable_names_are_sanitized()
        {
            Assert.Equal("$camera1", PowerShellLiteral.VariableName("Camera", 1));
            Assert.Equal("$evil2", PowerShellLiteral.VariableName("$(evil)", 2));
            Assert.Equal("$object4", PowerShellLiteral.VariableName("$( )", 4));
            Assert.Equal("$object1233", PowerShellLiteral.VariableName("123", 3));
        }

        [Fact]
        public void Secrets_become_prompts_and_handles_are_reacquired()
        {
            var gen = new ScriptGenerator(h => h == "h1" ? new ScriptObjectInfo { ObjectType = "Hardware", Id = "8a6c1b7e-0000-4000-8000-000000000001", Name = "Cam'1" } : null);
            var inv = new ScriptInvocation { Title = "Change\nhardware", WhatIf = true };
            inv.SecretDescriptions["s1"] = "Password for hardware 10.0.0.1";
            inv.Pipeline.Add(new PipelineStep("Set-VmsHardware")
                .With("Hardware", ParameterValue.Handle("h1"))
                .With("Password", ParameterValue.Secret("s1"))
                .With("Name", ParameterValue.String("New 'name'"))
                .With("Enabled", ParameterValue.Boolean(false))
                .With("PassThru", ParameterValue.Switch()));
            var script = gen.Generate(new[] { inv }, new ScriptConnectionInfo { ServerAddress = "http://mgmt", UsesCredential = true }, "25.2.61", new DateTime(2026, 1, 1));

            Assert.Contains("Connect-Vms -ServerAddress 'http://mgmt' -Credential (Get-Credential", script);
            Assert.Contains("Read-Host -AsSecureString -Prompt 'Password for hardware 10.0.0.1'", script);
            Assert.Contains("Get-VmsHardware -Id '8a6c1b7e-0000-4000-8000-000000000001'", script);
            Assert.Contains("-Name 'New ''name''' -Enabled $false -PassThru -WhatIf", script);
            Assert.DoesNotContain("\nhardware", script.Replace("\r", string.Empty).Split('\n').First(l => l.StartsWith("# Change", StringComparison.Ordinal)));

            Parser.ParseInput(script, out _, out var errors);
            Assert.Empty(errors);
        }
    }

    public class VersionAndDiffTests
    {
        [Theory]
        [InlineData("25.2.4815.1", "21.1", true)]
        [InlineData("21.1", "21.1", true)]
        [InlineData("20.3.1", "21.1", false)]
        [InlineData("23.2", "23.10", false)]
        [InlineData(null, "21.1", false)]
        [InlineData("21.1", null, true)]
        public void Version_requirements(string actual, string required, bool expected)
        {
            Assert.Equal(expected, VmsVersion.Satisfies(actual, required));
        }

        [Fact]
        public void Product_names()
        {
            Assert.Equal("XProtect 2025 R2", VmsVersion.ToProductName("25.2.4815.1"));
            Assert.Equal("13.3", VmsVersion.ToProductName("13.3"));
        }

        [Fact]
        public void Diff_only_compares_requested_properties()
        {
            var current = new Dictionary<string, object> { { "Name", "Cam 1" }, { "Enabled", true }, { "Fps", 15.0 } };
            var desired = new Dictionary<string, object> { { "Name", "Cam 1" }, { "Enabled", false } };
            var diff = DiffBuilder.Compute(current, desired, new Dictionary<string, string> { { "Enabled", "Camera enabled" } });
            Assert.Equal(2, diff.Count);
            Assert.False(diff.Single(d => d.Property == "Name").IsChanged);
            var enabled = diff.Single(d => d.Property == "Enabled");
            Assert.True(enabled.IsChanged);
            Assert.Equal("Camera enabled", enabled.DisplayName);
        }

        [Fact]
        public void Plan_counts_only_items_with_changes()
        {
            var plan = new ChangePlan();
            var a = new PlannedChange { TargetName = "A", Operation = "Update" };
            a.Properties.Add(new PropertyChange { Property = "Name", Current = "x", New = "x" });
            var b = new PlannedChange { TargetName = "B", Operation = "Update" };
            b.Properties.Add(new PropertyChange { Property = "Name", Current = "x", New = "y" });
            plan.Items.Add(a);
            plan.Items.Add(b);
            Assert.Equal(1, plan.ChangedItemCount);
            Assert.Equal(1, plan.UnchangedItemCount);
        }
    }
}
