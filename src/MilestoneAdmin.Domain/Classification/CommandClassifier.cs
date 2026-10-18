using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MilestoneAdmin.Domain.Catalog;

namespace MilestoneAdmin.Domain.Classification
{
    /// <summary>
    /// Port of tools/catalog/classification.py. Both evaluate catalog/classification-rules.json; ClassifierParityTests
    /// re-classifies the committed static catalog and fails on any difference.
    /// </summary>
    public sealed class CommandClassifier
    {
        private static readonly Dictionary<string, ControlKind> Primitive = new Dictionary<string, ControlKind>(StringComparer.Ordinal)
        {
            { "string", ControlKind.Text }, { "char", ControlKind.Text }, { "uri", ControlKind.Uri }, { "ipaddress", ControlKind.Text },
            { "int16", ControlKind.Integer }, { "int32", ControlKind.Integer }, { "int64", ControlKind.Integer }, { "uint32", ControlKind.Integer },
            { "uint64", ControlKind.Integer }, { "byte", ControlKind.Integer },
            { "double", ControlKind.Decimal }, { "single", ControlKind.Decimal }, { "decimal", ControlKind.Decimal },
            { "boolean", ControlKind.Toggle }, { "switchparameter", ControlKind.Checkbox },
            { "datetime", ControlKind.DateTime }, { "timespan", ControlKind.TimeSpan }, { "guid", ControlKind.Guid },
            { "pscredential", ControlKind.Credential }, { "securestring", ControlKind.SecureText },
            { "hashtable", ControlKind.KeyValue }, { "idictionary", ControlKind.KeyValue }
        };

        private static readonly Regex ExampleCommand = new Regex(@"\b([A-Z][a-z]+-[A-Za-z0-9]+)\b", RegexOptions.CultureInvariant);

        private readonly ClassificationRules _rules;

        public CommandClassifier(ClassificationRules rules)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        }

        public void Apply(CommandCatalog catalog)
        {
            foreach (var c in catalog.Commands)
            foreach (var p in c.Parameters)
            {
                p.Control = ControlFor(p);
                p.IsSecret = IsSecret(p);
                p.ObjectTypes.Clear();
                p.ObjectTypes.AddRange(ObjectTypesFor(p));
            }

            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in catalog.Commands) names[c.Name] = c.Name;
            foreach (var c in catalog.Commands)
            foreach (var a in c.Aliases)
            {
                if (!names.ContainsKey(a)) names[a] = c.Name;
            }

            foreach (var c in catalog.Commands)
            {
                c.Risk = RiskFor(c);
                c.Category = CategoryFor(c);
                c.CategoryLabel = _rules.CategoryLabel(c.Category);
                var (level, reasons) = Suitability(c);
                c.GuiSuitability = level;
                c.GuiSuitabilityReasons.Clear();
                c.GuiSuitabilityReasons.AddRange(reasons);
                c.TypeDependencies.Clear();
                foreach (var p in c.Parameters)
                foreach (var t in p.ObjectTypes)
                {
                    c.TypeDependencies.Add(new TypeDependency { Parameter = p.Name, ObjectType = t, PreferredProducer = _rules.ObjectTypes[t].Producer, Mandatory = p.MandatoryInAnySet });
                }

                var refs = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var ex in c.Examples)
                foreach (Match m in ExampleCommand.Matches(ex.Code ?? string.Empty))
                {
                    if (names.TryGetValue(m.Groups[1].Value, out var n) && n != c.Name) refs.Add(n);
                }

                c.UsedTogetherInExamples.Clear();
                c.UsedTogetherInExamples.AddRange(refs);
            }
        }

        private static (string Base, bool IsArray) BaseType(string t)
        {
            t = string.IsNullOrWhiteSpace(t) ? "Object" : t.Trim();
            return t.EndsWith("[]", StringComparison.Ordinal) ? (t.Substring(0, t.Length - 2), true) : (t, false);
        }

        public bool IsSecret(CatalogParameter p) =>
            _rules.SecretTypes.Contains((p.Type ?? string.Empty).ToLowerInvariant()) || _rules.SecretNamePattern.IsMatch(p.Name ?? string.Empty);

        private bool AllKnown(IReadOnlyCollection<string> types) => types != null && types.Count > 0 && types.All(t => _rules.ObjectTypes.ContainsKey(t));

        public IEnumerable<string> ObjectTypesFor(CatalogParameter p)
        {
            var (b, _) = BaseType(p.Type);
            if (_rules.ObjectTypes.ContainsKey(b)) return new[] { b };
            if (p.MipItemType != null && _rules.ObjectTypes.ContainsKey(p.MipItemType)) return new[] { p.MipItemType };
            if (AllKnown(p.ValidItemTypes)) return p.ValidItemTypes.ToList();
            return Array.Empty<string>();
        }

        public ControlKind ControlFor(CatalogParameter p)
        {
            var (b, isArray) = BaseType(p.Type);
            var lb = b.ToLowerInvariant();
            if (_rules.UnsupportedParameterTypes.Contains((p.Type ?? string.Empty).ToLowerInvariant()) || _rules.UnsupportedParameterTypes.Contains(lb)) return ControlKind.Unsupported;
            if (p.AcceptedValues.Count > 0) return isArray ? ControlKind.MultiSelect : ControlKind.Dropdown;
            if (_rules.ObjectTypes.ContainsKey(b) || (p.MipItemType != null && _rules.ObjectTypes.ContainsKey(p.MipItemType)) || AllKnown(p.ValidItemTypes))
                return isArray ? ControlKind.ObjectMultiSelector : ControlKind.ObjectSelector;
            if (lb == "string" && _rules.PathParameterNamePattern.IsMatch(p.Name ?? string.Empty)) return ControlKind.FilePath;
            if (Primitive.TryGetValue(lb, out var ctl))
            {
                if (!isArray) return ctl;
                switch (ctl)
                {
                    case ControlKind.Text:
                    case ControlKind.Uri:
                        return ControlKind.TextList;
                    case ControlKind.Integer:
                        return ControlKind.IntegerList;
                    case ControlKind.Guid:
                        return ControlKind.GuidList;
                    default:
                        return ControlKind.Unsupported;
                }
            }

            return ControlKind.Unsupported;
        }

        private RiskLevel Raise(RiskLevel a, RiskLevel b) => (int)a >= (int)b ? a : b;

        public RiskInfo RiskFor(CatalogCommand c)
        {
            RiskInfo info;
            if (_rules.CommandRiskOverrides.TryGetValue(c.Name, out var ov))
            {
                info = new RiskInfo { Level = ov.Risk, Source = "override", Reason = ov.Reason };
            }
            else if (c.Verb != null && _rules.VerbRisk.TryGetValue(c.Verb, out var level))
            {
                info = new RiskInfo { Level = level, Source = "verb", Reason = $"Verb '{c.Verb}'" };
                if (c.ConfirmImpact == "High")
                {
                    var raised = Raise(level, _rules.ConfirmImpactHighRaisesTo);
                    if (raised != level) info = new RiskInfo { Level = raised, Source = "confirmImpact", Reason = "Command declares ConfirmImpact = High" };
                }
            }
            else
            {
                info = new RiskInfo { Level = RiskLevel.Modify, Source = "default", Reason = "Unknown verb; treated as a configuration change" };
                if (c.ConfirmImpact == "High")
                {
                    var raised = Raise(info.Level, _rules.ConfirmImpactHighRaisesTo);
                    if (raised != info.Level) info = new RiskInfo { Level = raised, Source = "confirmImpact", Reason = "Command declares ConfirmImpact = High" };
                }
            }

            info.Definition = _rules.Definition(info.Level);
            return info;
        }

        public string CategoryFor(CatalogCommand c)
        {
            foreach (var cat in _rules.Categories)
            {
                if (cat.NounPattern.IsMatch(c.Noun ?? string.Empty)) return cat.Id;
            }

            return _rules.FallbackCategory;
        }

        private (GuiSuitability, List<string>) Suitability(CatalogCommand c)
        {
            if (_rules.AppManagedCommands.TryGetValue(c.Name, out var am)) return (GuiSuitability.AppManaged, new List<string> { am });
            if (c.InternalHelper) return (GuiSuitability.NotSuitable, new List<string> { "Internal helper function exported by the module's wildcard export; undocumented" });
            if (c.Deprecated) return (GuiSuitability.NotSuitable, new List<string> { $"Deprecated; replaced by {c.ReplacedBy}" });
            if (_rules.ExpertOnlyCommands.TryGetValue(c.Name, out var eo)) return (GuiSuitability.ExpertOnly, new List<string> { eo });
            if (c.Category == "Advanced") return (GuiSuitability.ExpertOnly, new List<string> { "Low-level SDK / Configuration API command" });
            if (c.Requirements.InteractiveSession) return (GuiSuitability.ExpertOnly, new List<string> { "Requires an interactive session (shows its own dialog)" });
            if (c.ParameterSets.Count == 0 && c.Parameters.Count == 0 && c.Implementation == null) return (GuiSuitability.NotSuitable, new List<string> { "No metadata available" });

            var reasons = new List<string>();
            var usable = new List<string>();
            IEnumerable<(string Name, List<(string Name, bool Mandatory)> Params)> sets = c.ParameterSets.Count > 0
                ? c.ParameterSets.Select(s => (s.Name, s.Parameters.Select(p => (p.Name, p.Mandatory)).ToList()))
                : new[] { ("__AllParameterSets", c.Parameters.Select(p => (p.Name, p.MandatoryInAnySet)).ToList()) };
            foreach (var (setName, ps) in sets)
            {
                var bad = new List<string>();
                foreach (var (pname, mandatory) in ps)
                {
                    if (!mandatory) continue;
                    var p = c.Parameters.FirstOrDefault(x => x.Name == pname);
                    if (p == null) continue;
                    if (p.Control == ControlKind.Unsupported || p.Control == ControlKind.KeyValue) bad.Add($"{p.Name} <{p.Type}>");
                    else if ((p.Control == ControlKind.ObjectSelector || p.Control == ControlKind.ObjectMultiSelector) && p.ObjectTypes.Count == 0)
                        bad.Add($"{p.Name} <{p.Type}> has no known producer");
                }

                if (bad.Count == 0) usable.Add(setName);
                else reasons.Add($"Parameter set '{setName}' needs complex input: " + string.Join(", ", bad));
            }

            var risk = c.Risk.Level;
            if (risk == RiskLevel.Delete || risk == RiskLevel.HighImpact || risk == RiskLevel.Sensitive)
            {
                var r = new List<string> { $"Risk level {risk} requires a guided workflow with review and strong confirmation" };
                if (usable.Count == 0) r.AddRange(reasons);
                return (GuiSuitability.CustomWorkflow, r);
            }

            if (usable.Count == 0) return (GuiSuitability.CustomWorkflow, reasons);
            return (GuiSuitability.AutoForm, new List<string> { "Usable parameter sets: " + string.Join(", ", usable) });
        }
    }
}
