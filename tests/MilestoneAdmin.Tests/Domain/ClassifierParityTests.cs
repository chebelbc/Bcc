using System;
using System.IO;
using System.Linq;
using MilestoneAdmin.Domain.Catalog;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Protocol.Json;
using Xunit;

namespace MilestoneAdmin.Tests.Domain
{
    /// <summary>
    /// The committed static catalog was classified by tools/catalog/classification.py. The C# CommandClassifier must
    /// produce identical results from the same rules, otherwise runtime catalogs (classified in C#) and the generated
    /// documentation (classified in Python) would disagree.
    /// </summary>
    public class ClassifierParityTests
    {
        private static readonly JsonObject Raw = JsonNode.Parse(File.ReadAllText(TestPaths.StaticCatalog)).AsObject();
        private static readonly CommandCatalog Catalog = CommandCatalog.FromJson(JsonNode.Parse(File.ReadAllText(TestPaths.StaticCatalog)).AsObject(), ClassificationRules.Embedded);

        [Fact]
        public void Catalog_contains_all_commands()
        {
            Assert.Equal(Raw.GetArray("commands").Count, Catalog.Commands.Count);
            Assert.True(Catalog.Commands.Count > 250, "Static catalog unexpectedly small");
        }

        [Fact]
        public void Risk_category_and_suitability_match_python()
        {
            var mismatches = (from c in Raw.GetArray("commands").Select(x => x.AsObject())
                let cs = Catalog.Find(c.GetString("name"))
                let py = c.GetObject("risk")
                where cs.Risk.Level.ToString() != py.GetString("level")
                      || cs.Risk.Source != py.GetString("source")
                      || cs.Category != c.GetString("category")
                      || cs.GuiSuitability.ToString() != c.GetString("guiSuitability")
                      || !cs.GuiSuitabilityReasons.SequenceEqual(c.GetArray("guiSuitabilityReasons").Strings())
                select $"{cs.Name}: C# {cs.Risk.Level}/{cs.Risk.Source}/{cs.Category}/{cs.GuiSuitability} [{string.Join("|", cs.GuiSuitabilityReasons)}] vs " +
                       $"py {py.GetString("level")}/{py.GetString("source")}/{c.GetString("category")}/{c.GetString("guiSuitability")} [{string.Join("|", c.GetArray("guiSuitabilityReasons").Strings())}]").ToList();
            Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches.Take(20)));
        }

        [Fact]
        public void Parameter_controls_and_secret_flags_match_python()
        {
            var mismatches = (from c in Raw.GetArray("commands").Select(x => x.AsObject())
                let cs = Catalog.Find(c.GetString("name"))
                from p in c.GetArray("parameters").Select(x => x.AsObject())
                let name = p.GetString("name")
                where name != "WhatIf" && name != "Confirm"
                let cp = cs.FindParameter(name)
                where cp == null || cp.Control.ToString() != p.GetString("control") || cp.IsSecret != p.GetBool("isSecret")
                select $"{cs.Name} -{name}: C# {cp?.Control}/{cp?.IsSecret} vs py {p.GetString("control")}/{p.GetBool("isSecret")}").ToList();
            Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches.Take(20)));
        }

        [Fact]
        public void Dependencies_match_python()
        {
            foreach (var c in Raw.GetArray("commands").Select(x => x.AsObject()))
            {
                var cs = Catalog.Find(c.GetString("name"));
                var deps = c.GetObject("dependencies");
                var py = deps.GetArray("byParameterType").Select(d => d.AsObject()).Select(d => $"{d.GetString("parameter")}:{d.GetString("type")}:{d.GetString("preferredProducer")}").ToList();
                var cs1 = cs.TypeDependencies.Select(d => $"{d.Parameter}:{d.ObjectType}:{d.PreferredProducer}").ToList();
                Assert.Equal(py, cs1);
                Assert.Equal(deps.GetArray("usedTogetherInExamples").Strings().ToList(), cs.UsedTogetherInExamples);
            }
        }

        [Theory]
        [InlineData("Get-VmsCamera", RiskLevel.ReadOnly)]
        [InlineData("Remove-VmsHardware", RiskLevel.Delete)]
        [InlineData("Move-VmsHardware", RiskLevel.HighImpact)]
        [InlineData("Export-VmsHardware", RiskLevel.Sensitive)]
        [InlineData("Get-VmsHardwarePassword", RiskLevel.Sensitive)]
        [InlineData("Start-VmsHardwareScan", RiskLevel.Operational)]
        [InlineData("Add-VmsHardware", RiskLevel.Create)]
        [InlineData("Set-VmsCamera", RiskLevel.Modify)]
        [InlineData("Connect-Vms", RiskLevel.Session)]
        public void Known_commands_have_expected_risk(string command, RiskLevel expected)
        {
            Assert.Equal(expected, Catalog.Find(command).Risk.Level);
        }

        [Fact]
        public void Every_object_type_producer_exists_in_catalog()
        {
            foreach (var t in ClassificationRules.Embedded.ObjectTypes.Values)
                Assert.True(Catalog.Contains(t.Producer), $"Producer {t.Producer} for {t.Type} is not in the catalog");
        }

        [Fact]
        public void Aliases_resolve_to_their_command()
        {
            Assert.Equal("Get-VmsCamera", Catalog.Find("Get-Camera").Name);
        }

        [Fact]
        public void Set_VmsCamera_InputObject_is_a_camera_selector()
        {
            var p = Catalog.Find("Set-VmsCamera").FindParameter("InputObject");
            Assert.Equal(ControlKind.ObjectMultiSelector, p.Control);
            Assert.Equal(new[] { "Camera" }, p.ObjectTypes);
        }

        [Fact]
        public void Credential_parameters_are_secret()
        {
            Assert.True(Catalog.Find("Add-VmsHardware").FindParameter("Credential").IsSecret);
            Assert.Equal(ControlKind.Credential, Catalog.Find("Add-VmsHardware").FindParameter("Credential").Control);
        }
    }
}
