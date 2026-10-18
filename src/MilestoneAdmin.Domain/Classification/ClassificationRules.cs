using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.Domain.Classification
{
    public enum RiskLevel
    {
        ReadOnly,
        Sensitive,
        Session,
        LocalState,
        Operational,
        Create,
        Modify,
        HighImpact,
        Delete
    }

    public enum ControlKind
    {
        Text,
        Uri,
        Integer,
        Decimal,
        Toggle,
        Checkbox,
        DateTime,
        TimeSpan,
        Guid,
        Credential,
        SecureText,
        KeyValue,
        Dropdown,
        MultiSelect,
        ObjectSelector,
        ObjectMultiSelector,
        FilePath,
        TextList,
        IntegerList,
        GuidList,
        Unsupported
    }

    public enum GuiSuitability
    {
        AutoForm,
        CustomWorkflow,
        ExpertOnly,
        AppManaged,
        NotSuitable
    }

    public sealed class RiskLevelDefinition
    {
        public RiskLevel Level { get; set; }
        public string Matrix { get; set; }
        public bool RequiresReview { get; set; }
        public bool RequiresStrongConfirmation { get; set; }
        public string Description { get; set; }
    }

    public sealed class RiskInfo
    {
        public RiskLevel Level { get; set; }
        public string Source { get; set; }
        public string Reason { get; set; }
        public RiskLevelDefinition Definition { get; set; }

        public string Matrix => Definition?.Matrix;

        public bool IsReadOnly => Matrix == "Read" && Level != RiskLevel.Sensitive;

        public bool RequiresStrongConfirmation => Definition?.RequiresStrongConfirmation ?? true;
    }

    public sealed class ObjectTypeDefinition
    {
        public string Type { get; set; }
        public string Label { get; set; }
        public string Producer { get; set; }
    }

    public sealed class CategoryDefinition
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public Regex NounPattern { get; set; }
    }

    /// <summary>
    /// The shared rule set in catalog/classification-rules.json (embedded resource). The same file drives
    /// tools/catalog/classification.py, so static and runtime catalogs are classified identically.
    /// </summary>
    public sealed class ClassificationRules
    {
        public string RulesVersion { get; private set; }
        public IReadOnlyList<RiskLevelDefinition> RiskLevels { get; private set; }
        public IReadOnlyDictionary<string, RiskLevel> VerbRisk { get; private set; }
        public RiskLevel ConfirmImpactHighRaisesTo { get; private set; }
        public IReadOnlyDictionary<string, (RiskLevel Risk, string Reason)> CommandRiskOverrides { get; private set; }
        public IReadOnlyDictionary<string, string[]> SensitiveParameters { get; private set; }
        public HashSet<string> SecretTypes { get; private set; }
        public Regex SecretNamePattern { get; private set; }
        public IReadOnlyList<CategoryDefinition> Categories { get; private set; }
        public string FallbackCategory { get; private set; }
        public IReadOnlyDictionary<string, string> ExpertOnlyCommands { get; private set; }
        public IReadOnlyDictionary<string, string> AppManagedCommands { get; private set; }
        public HashSet<string> UnsupportedParameterTypes { get; private set; }
        public IReadOnlyDictionary<string, ObjectTypeDefinition> ObjectTypes { get; private set; }
        public Regex PathParameterNamePattern { get; private set; }
        public IReadOnlyDictionary<string, string> SideEffects { get; private set; }

        public RiskLevelDefinition Definition(RiskLevel level) => RiskLevels.First(r => r.Level == level);

        public string CategoryLabel(string id) => Categories.FirstOrDefault(c => c.Id == id)?.Label ?? id;

        private static ClassificationRules _embedded;

        public static ClassificationRules Embedded
        {
            get
            {
                if (_embedded != null) return _embedded;
                var asm = typeof(ClassificationRules).GetTypeInfo().Assembly;
                using (var s = asm.GetManifestResourceStream("MilestoneAdmin.Domain.classification-rules.json")
                               ?? throw new InvalidOperationException("Embedded classification rules not found."))
                using (var r = new StreamReader(s))
                {
                    _embedded = Parse(r.ReadToEnd());
                }

                return _embedded;
            }
        }

        public static ClassificationRules Parse(string json)
        {
            var o = JsonNode.Parse(json).AsObject();
            var rules = new ClassificationRules { RulesVersion = o.GetString("rulesVersion") };
            rules.RiskLevels = o.GetArray("riskLevels").Select(x => x.AsObject()).Select(x => new RiskLevelDefinition
            {
                Level = ParseRisk(x.GetString("id")),
                Matrix = x.GetString("matrix"),
                RequiresReview = x.GetBool("requiresReview"),
                RequiresStrongConfirmation = x.GetBool("requiresStrongConfirmation"),
                Description = x.GetString("description")
            }).ToList();
            rules.VerbRisk = o.GetObject("verbRisk").ToDictionary(kv => kv.Key, kv => ParseRisk(kv.Value.AsString()), StringComparer.Ordinal);
            rules.ConfirmImpactHighRaisesTo = ParseRisk(o.GetString("confirmImpactHighRaisesTo"));
            var overrides = new Dictionary<string, (RiskLevel, string)>(StringComparer.Ordinal);
            foreach (var kv in o.GetObject("commandRiskOverrides"))
            {
                if (!(kv.Value is JsonObject ov)) continue;
                overrides[kv.Key] = (ParseRisk(ov.GetString("risk")), ov.GetString("reason"));
            }

            rules.CommandRiskOverrides = overrides;
            var sens = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in o.GetObject("sensitiveParameters"))
            {
                if (kv.Key.StartsWith("$", StringComparison.Ordinal)) continue;
                sens[kv.Key] = kv.Value.AsArray().Strings().ToArray();
            }

            rules.SensitiveParameters = sens;
            var sp = o.GetObject("secretParameter");
            rules.SecretTypes = new HashSet<string>(sp.GetArray("types").Strings().Select(t => t.ToLowerInvariant()));
            rules.SecretNamePattern = PyRegex(sp.GetString("namePattern"));
            rules.Categories = o.GetArray("categories").Select(x => x.AsObject()).Select(x => new CategoryDefinition
            {
                Id = x.GetString("id"),
                Label = x.GetString("label"),
                NounPattern = PyRegex(x.GetString("nounPattern"))
            }).ToList();
            rules.FallbackCategory = o.GetString("fallbackCategory");
            rules.ExpertOnlyCommands = o.GetObject("expertOnlyCommands").ToDictionary(kv => kv.Key, kv => kv.Value.AsString(), StringComparer.Ordinal);
            rules.AppManagedCommands = o.GetObject("appManagedCommands").ToDictionary(kv => kv.Key, kv => kv.Value.AsString(), StringComparer.Ordinal);
            rules.UnsupportedParameterTypes = new HashSet<string>(o.GetArray("unsupportedParameterTypes").Strings().Select(t => t.ToLowerInvariant()));
            rules.ObjectTypes = o.GetObject("objectTypes").ToDictionary(kv => kv.Key, kv => new ObjectTypeDefinition
            {
                Type = kv.Key,
                Label = kv.Value.AsObject().GetString("label"),
                Producer = kv.Value.AsObject().GetString("producer")
            }, StringComparer.Ordinal);
            rules.PathParameterNamePattern = PyRegex(o.GetString("pathParameterNamePattern"));
            rules.SideEffects = o.GetObject("sideEffectRules").ToDictionary(kv => kv.Key, kv => kv.Value.AsString(), StringComparer.Ordinal);
            return rules;
        }

        private static RiskLevel ParseRisk(string s) => (RiskLevel)Enum.Parse(typeof(RiskLevel), s);

        /// <summary>Python and .NET regex syntax agree for the patterns used; "(?i)" inline flags are supported by both.</summary>
        private static Regex PyRegex(string pattern) => new Regex(pattern, RegexOptions.CultureInvariant);
    }
}
