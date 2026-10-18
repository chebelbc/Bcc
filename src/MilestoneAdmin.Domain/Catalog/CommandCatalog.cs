using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.Domain.Catalog
{
    public enum CatalogSourceKind
    {
        /// <summary>Generated at runtime from the installed module (authoritative for this workstation).</summary>
        Runtime,

        /// <summary>Generated from the official repository and shipped with the application (fallback / documentation).</summary>
        StaticRepository
    }

    /// <summary>The application's understanding of the MilestonePSTools commands available on this workstation.</summary>
    public sealed class CommandCatalog
    {
        private readonly Dictionary<string, CatalogCommand> _byName = new Dictionary<string, CatalogCommand>(StringComparer.OrdinalIgnoreCase);

        public CommandCatalog(string moduleName, string moduleVersion, CatalogSourceKind source, IEnumerable<CatalogCommand> commands)
        {
            ModuleName = moduleName;
            ModuleVersion = moduleVersion;
            Source = source;
            Commands = commands.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var c in Commands)
            {
                _byName[c.Name] = c;
            }

            foreach (var c in Commands)
            foreach (var a in c.Aliases)
            {
                if (!_byName.ContainsKey(a)) _byName[a] = c;
            }
        }

        public string ModuleName { get; }

        public string ModuleVersion { get; }

        public CatalogSourceKind Source { get; }

        public IReadOnlyList<CatalogCommand> Commands { get; }

        public string GeneratedAtUtc { get; set; }

        public IReadOnlyList<string> Discrepancies { get; set; } = Array.Empty<string>();

        public bool Contains(string name) => name != null && _byName.ContainsKey(name);

        public CatalogCommand Find(string name) => name != null && _byName.TryGetValue(name, out var c) ? c : null;

        public static CommandCatalog FromJson(JsonObject json, ClassificationRules rules)
        {
            var kind = json.GetObject("source")?.GetString("kind") == "runtime" ? CatalogSourceKind.Runtime : CatalogSourceKind.StaticRepository;
            var commands = (json.GetArray("commands") ?? new JsonArray()).Select(c => CatalogCommand.FromJson(c.AsObject())).ToList();
            var catalog = new CommandCatalog(json.GetString("moduleName", "MilestonePSTools"), json.GetString("moduleVersion"), kind, commands)
            {
                GeneratedAtUtc = json.GetString("generatedAtUtc"),
                Discrepancies = (json.GetArray("discrepancies") ?? new JsonArray())
                    .Select(d => d.AsObject()).Where(d => d != null)
                    .Select(d => d.GetString("command") + ": " + d.GetString("issue")).ToList()
            };
            new CommandClassifier(rules).Apply(catalog);
            return catalog;
        }

        public static CommandCatalog Load(string path, ClassificationRules rules) =>
            FromJson(JsonNode.Parse(File.ReadAllText(path)).AsObject() ?? throw new InvalidDataException("Catalog must be a JSON object."), rules);
    }

    public sealed class CatalogCommand
    {
        public string Name { get; set; }
        public string Verb { get; set; }
        public string Noun { get; set; }
        public string CommandType { get; set; }
        public string Implementation { get; set; }
        public string SourceFile { get; set; }
        public string HelpUri { get; set; }
        public string Synopsis { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> Aliases { get; } = new List<string>();
        public bool Deprecated { get; set; }
        public string ReplacedBy { get; set; }
        public bool InternalHelper { get; set; }
        public bool SupportsShouldProcess { get; set; }
        public string ConfirmImpact { get; set; }
        public CommandRequirements Requirements { get; set; } = new CommandRequirements();
        public List<CatalogParameterSet> ParameterSets { get; } = new List<CatalogParameterSet>();
        public List<CatalogParameter> Parameters { get; } = new List<CatalogParameter>();
        public List<string> OutputTypes { get; } = new List<string>();
        public List<CommandExample> Examples { get; } = new List<CommandExample>();

        // Classification (computed by CommandClassifier from the shared rules)
        public RiskInfo Risk { get; set; }
        public string Category { get; set; }
        public string CategoryLabel { get; set; }
        public GuiSuitability GuiSuitability { get; set; }
        public List<string> GuiSuitabilityReasons { get; } = new List<string>();
        public List<TypeDependency> TypeDependencies { get; } = new List<TypeDependency>();
        public List<string> UsedTogetherInExamples { get; } = new List<string>();

        public CatalogParameter FindParameter(string name) =>
            Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? Parameters.FirstOrDefault(p => p.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)));

        public CatalogParameterSet DefaultParameterSet => ParameterSets.FirstOrDefault(s => s.IsDefault) ?? ParameterSets.FirstOrDefault();

        public static CatalogCommand FromJson(JsonObject o)
        {
            var c = new CatalogCommand
            {
                Name = o.GetString("name"),
                Verb = o.GetString("verb"),
                Noun = o.GetString("noun"),
                CommandType = o.GetString("commandType"),
                Implementation = o.GetString("implementation"),
                SourceFile = o.GetString("sourceFile"),
                HelpUri = o.GetString("helpUri"),
                Synopsis = o.GetString("synopsis", string.Empty),
                Description = o.GetString("description", string.Empty),
                Deprecated = o.GetBool("deprecated"),
                ReplacedBy = o.GetString("replacedBy"),
                InternalHelper = o.GetBool("internalHelper"),
                SupportsShouldProcess = o.GetBool("supportsShouldProcess"),
                ConfirmImpact = o.GetString("confirmImpact"),
                Requirements = CommandRequirements.FromJson(o.GetObject("requirements"))
            };
            if (string.IsNullOrEmpty(c.Verb) && c.Name != null && c.Name.Contains("-"))
            {
                c.Verb = c.Name.Substring(0, c.Name.IndexOf('-'));
                c.Noun = c.Name.Substring(c.Name.IndexOf('-') + 1);
            }

            c.Aliases.AddRange((o.GetArray("aliases") ?? new JsonArray()).Strings());
            c.OutputTypes.AddRange((o.GetArray("outputTypes") ?? new JsonArray()).Strings());
            foreach (var p in o.GetArray("parameters") ?? new JsonArray()) c.Parameters.Add(CatalogParameter.FromJson(p.AsObject()));
            foreach (var s in o.GetArray("parameterSets") ?? new JsonArray()) c.ParameterSets.Add(CatalogParameterSet.FromJson(s.AsObject()));
            foreach (var e in o.GetArray("examples") ?? new JsonArray())
            {
                var eo = e.AsObject();
                c.Examples.Add(new CommandExample { Title = eo.GetString("title"), Code = eo.GetString("code", string.Empty), Remarks = eo.GetString("remarks", string.Empty) });
            }

            // Common parameters appear in the docs-derived static catalog; they are not user inputs.
            c.Parameters.RemoveAll(p => p.Name == "WhatIf" || p.Name == "Confirm");
            return c;
        }

        public override string ToString() => Name;
    }

    public sealed class CommandRequirements
    {
        public bool? VmsConnection { get; set; }
        public string MinVmsVersion { get; set; }
        public List<string> VmsFeatures { get; } = new List<string>();
        public bool Elevation { get; set; }
        public bool InteractiveSession { get; set; }
        public bool WindowsUser { get; set; }
        public List<string> Documented { get; } = new List<string>();

        public static CommandRequirements FromJson(JsonObject o)
        {
            var r = new CommandRequirements();
            if (o == null) return r;
            var vc = o["vmsConnection"];
            if (vc != null && !vc.IsNull) r.VmsConnection = vc.AsBool();
            r.MinVmsVersion = o.GetString("minVmsVersion");
            r.VmsFeatures.AddRange((o.GetArray("vmsFeatures") ?? new JsonArray()).Strings());
            r.Elevation = o.GetBool("elevation");
            r.InteractiveSession = o.GetBool("interactiveSession");
            r.WindowsUser = o.GetBool("windowsUser");
            r.Documented.AddRange((o.GetArray("documented") ?? new JsonArray()).Strings());
            return r;
        }
    }

    public sealed class CatalogParameterSet
    {
        public string Name { get; set; }
        public bool IsDefault { get; set; }
        public string Syntax { get; set; }
        public List<(string Name, bool Mandatory, int? Position)> Parameters { get; } = new List<(string, bool, int?)>();

        public bool IsAllSets => Name == "__AllParameterSets";

        public string DisplayName => IsAllSets ? "(default)" : Name;

        public static CatalogParameterSet FromJson(JsonObject o)
        {
            var s = new CatalogParameterSet { Name = o.GetString("name"), IsDefault = o.GetBool("isDefault"), Syntax = o.GetString("syntax") };
            foreach (var p in o.GetArray("parameters") ?? new JsonArray())
            {
                var po = p.AsObject();
                var pos = po["position"]?.AsInt64();
                s.Parameters.Add((po.GetString("name"), po.GetBool("mandatory"), pos.HasValue ? (int?)pos.Value : null));
            }

            return s;
        }
    }

    public sealed class CatalogParameter
    {
        public string Name { get; set; }
        public string Type { get; set; } = "Object";
        public string TypeFullName { get; set; }
        public string Description { get; set; } = string.Empty;
        public List<string> Aliases { get; } = new List<string>();
        public List<string> AcceptedValues { get; } = new List<string>();
        public string DefaultValue { get; set; }
        public bool AcceptsWildcards { get; set; }
        public List<(string Set, bool Mandatory, int? Position)> Sets { get; } = new List<(string, bool, int?)>();
        public bool ValueFromPipeline { get; set; }
        public bool ValueFromPipelineByPropertyName { get; set; }
        public bool MandatoryInAnySet { get; set; }
        public string MipItemType { get; set; }
        public List<string> ValidItemTypes { get; } = new List<string>();
        public string MinVmsVersion { get; set; }
        public string VmsFeature { get; set; }
        public List<string> Validators { get; } = new List<string>();

        // Classification
        public ControlKind Control { get; set; }
        public bool IsSecret { get; set; }
        public List<string> ObjectTypes { get; } = new List<string>();

        public bool IsArray => Type != null && Type.EndsWith("[]", StringComparison.Ordinal);

        public string ElementType => IsArray ? Type.Substring(0, Type.Length - 2) : Type;

        public bool InSet(string set) => Sets.Any(s => s.Set == set || s.Set == "__AllParameterSets");

        public bool IsMandatoryIn(string set) => Sets.Any(s => (s.Set == set || s.Set == "__AllParameterSets") && s.Mandatory);

        public static CatalogParameter FromJson(JsonObject o)
        {
            var p = new CatalogParameter
            {
                Name = o.GetString("name"),
                Type = o.GetString("type", "Object"),
                TypeFullName = o.GetString("typeFullName"),
                Description = o.GetString("description", string.Empty),
                DefaultValue = o.GetString("defaultValue"),
                AcceptsWildcards = o.GetBool("acceptsWildcards"),
                ValueFromPipeline = o.GetBool("valueFromPipeline"),
                ValueFromPipelineByPropertyName = o.GetBool("valueFromPipelineByPropertyName"),
                MipItemType = o.GetString("mipItemType"),
                MinVmsVersion = o.GetString("minVmsVersion"),
                VmsFeature = o.GetString("vmsFeature")
            };
            p.Aliases.AddRange((o.GetArray("aliases") ?? new JsonArray()).Strings());
            p.AcceptedValues.AddRange((o.GetArray("acceptedValues") ?? new JsonArray()).Strings());
            p.ValidItemTypes.AddRange((o.GetArray("validItemTypes") ?? new JsonArray()).Strings());
            p.Validators.AddRange((o.GetArray("validators") ?? new JsonArray()).Strings());
            foreach (var s in o.GetArray("sets") ?? new JsonArray())
            {
                var so = s.AsObject();
                var pos = so["position"]?.AsInt64();
                p.Sets.Add((so.GetString("set"), so.GetBool("mandatory"), pos.HasValue ? (int?)pos.Value : null));
            }

            p.MandatoryInAnySet = o.ContainsKey("mandatoryInAnySet") ? o.GetBool("mandatoryInAnySet") : p.Sets.Any(s => s.Mandatory);
            return p;
        }

        public override string ToString() => $"-{Name} <{Type}>";
    }

    public sealed class CommandExample
    {
        public string Title { get; set; }
        public string Code { get; set; }
        public string Remarks { get; set; }
    }

    public sealed class TypeDependency
    {
        public string Parameter { get; set; }
        public string ObjectType { get; set; }
        public string PreferredProducer { get; set; }
        public bool Mandatory { get; set; }
    }
}
