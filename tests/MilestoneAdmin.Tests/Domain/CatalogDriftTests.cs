using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using MilestoneAdmin.Domain.Catalog;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Domain.Scripting;
using MilestoneAdmin.Protocol.Json;
using MilestoneAdmin.PSTools.Commands;
using MilestoneAdmin.Worker.Engine;
using Xunit;

namespace MilestoneAdmin.Tests.Domain
{
    /// <summary>
    /// Guards against invented commands or parameters: everything the application builds, and everything the mock
    /// module imitates, must exist in the catalog extracted from the official MilestonePSTools sources.
    /// </summary>
    public class CatalogDriftTests
    {
        private static readonly CommandCatalog Catalog = CommandCatalog.Load(TestPaths.StaticCatalog, ClassificationRules.Embedded);

        private static readonly JsonObject Rules = JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "catalog", "classification-rules.json"))).AsObject();

        // Parameters every advanced function/cmdlet has; they are not listed per command in the catalog.
        private static readonly HashSet<string> Common = new HashSet<string>(
            Cmdlet.CommonParameters.Concat(Cmdlet.OptionalCommonParameters), StringComparer.OrdinalIgnoreCase);

        private static IEnumerable<string> Missing(string command, IEnumerable<string> parameters)
        {
            var c = Catalog.Find(command);
            if (c == null)
            {
                yield return $"{command}: command not in the MilestonePSTools catalog";
                yield break;
            }

            foreach (var p in parameters.Where(p => !Common.Contains(p) && c.FindParameter(p) == null))
                yield return $"{command} -{p}: parameter not in the MilestonePSTools catalog";
        }

        [Fact]
        public void Application_command_builders_use_only_documented_commands_and_parameters()
        {
            var steps = PsCommands.Samples().ToList();
            Assert.True(steps.Count > 30, "Samples() should cover every builder");
            var problems = steps.SelectMany(s => Missing(s.Command, s.Parameters.Select(p => p.Key))).Distinct().ToList();
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Object_producers_exist_and_accept_their_lookup_parameter()
        {
            var problems = ReferenceResolver.Producers.Select(kv => (kv.Key, kv.Value.Command, kv.Value.Parameter))
                .Concat(ScriptGenerator.Producers.Select(kv => (kv.Key, kv.Value.Command, kv.Value.Parameter)))
                .SelectMany(p => Missing(p.Command, new[] { p.Parameter }).Select(m => $"{p.Key}: {m}"))
                .ToList();
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Worker_and_script_producers_agree()
        {
            Assert.Equal(
                ReferenceResolver.Producers.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value.Command} -{k.Value.Parameter}"),
                ScriptGenerator.Producers.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value.Command} -{k.Value.Parameter}"));
        }

        [Fact]
        public void Mock_module_imitates_only_real_commands_and_parameters()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddCommand("Import-Module").AddParameter("Name", TestPaths.MockModule).AddParameter("PassThru", true);
                var module = ps.Invoke<PSModuleInfo>().Single(m => m.Name == "MilestonePSTools");
                Assert.Empty(ps.Streams.Error);

                // Invoke-MockScriptBlockProbe exists only to prove that ScriptBlock parameters are refused.
                var problems = module.ExportedFunctions.Values
                    .Where(f => f.Name != "Invoke-MockScriptBlockProbe")
                    .SelectMany(f => Missing(f.Name, f.Parameters.Keys))
                    .ToList();
                Assert.True(module.ExportedFunctions.Count > 15);
                Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
            }
        }

        [Fact]
        public void Worker_policy_lists_mirror_classification_rules()
        {
            var verbRisk = Rules.GetObject("verbRisk");
            var nonMutating = verbRisk.Where(kv => kv.Value.AsString() == "ReadOnly" || kv.Value.AsString() == "Session").Select(kv => kv.Key)
                // Export-* writes local files (Export-VmsHardware even writes passwords), so the worker treats it as a state change.
                .Where(v => v != "Export")
                .OrderBy(v => v, StringComparer.Ordinal);
            Assert.Equal(nonMutating, CommandPolicy.NonMutatingVerbs.OrderBy(v => v, StringComparer.Ordinal));

            var sensitive = Rules.GetObject("commandRiskOverrides").Where(kv => kv.Value.AsObject().GetString("risk") == "Sensitive").Select(kv => kv.Key).OrderBy(x => x);
            Assert.Equal(sensitive, CommandPolicy.SensitiveCommands.OrderBy(x => x));

            var sensParams = Rules.GetObject("sensitiveParameters").Where(kv => !kv.Key.StartsWith("$", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value.AsArray().Strings().OrderBy(x => x).ToArray());
            Assert.Equal(sensParams.Keys.OrderBy(x => x), CommandPolicy.SensitiveParameters.Keys.OrderBy(x => x));
            foreach (var kv in sensParams) Assert.Equal(kv.Value, CommandPolicy.SensitiveParameters[kv.Key].OrderBy(x => x));
        }

        [Fact]
        public void Policy_lists_name_real_commands()
        {
            var names = CommandPolicy.SensitiveCommands.Concat(CommandPolicy.SensitiveParameters.Keys).Concat(CommandPolicy.NoConnectionCommands);
            var problems = names.Where(n => Catalog.Find(n) == null).ToList();
            Assert.True(problems.Count == 0, "Not in catalog: " + string.Join(", ", problems));
            foreach (var kv in CommandPolicy.SensitiveParameters)
                Assert.Empty(Missing(kv.Key, kv.Value));
        }
    }
}
