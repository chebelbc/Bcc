using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MilestoneAdmin.Domain.Classification;

namespace MilestoneAdmin.Domain.Changes
{
    public enum PreviewKind
    {
        /// <summary>Computed by the application from current values read from the VMS plus validation rules.</summary>
        ValidatedPreview,

        /// <summary>Produced by running the MilestonePSTools command with -WhatIf (ShouldProcess).</summary>
        PowerShellWhatIf
    }

    public sealed class PropertyChange
    {
        public string Property { get; set; }
        public string DisplayName { get; set; }
        public object Current { get; set; }
        public object New { get; set; }

        public bool IsChanged => !ValuesEqual(Current, New);

        public string CurrentText => Format(Current);

        public string NewText => Format(New);

        public static string Format(object v)
        {
            switch (v)
            {
                case null: return "(not set)";
                case bool b: return b ? "True" : "False";
                case double d: return d.ToString("0.###", CultureInfo.InvariantCulture);
                case DateTime dt: return dt.ToString("u", CultureInfo.InvariantCulture);
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                default: return v.ToString();
            }
        }

        public static bool ValuesEqual(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (IsNumeric(a) && IsNumeric(b))
                return Math.Abs(Convert.ToDouble(a, CultureInfo.InvariantCulture) - Convert.ToDouble(b, CultureInfo.InvariantCulture)) < 1e-9;
            if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
            return a.Equals(b);
        }

        private static bool IsNumeric(object o) => o is int || o is long || o is double || o is float || o is decimal || o is short;
    }

    public sealed class PlannedChange
    {
        public string TargetType { get; set; }
        public Guid TargetId { get; set; }
        public string TargetName { get; set; }
        public string TargetHandle { get; set; }
        public string Operation { get; set; }
        public List<PropertyChange> Properties { get; } = new List<PropertyChange>();
        public List<string> Notes { get; } = new List<string>();

        public IEnumerable<PropertyChange> EffectiveChanges => Properties.Where(p => p.IsChanged);

        public bool HasChanges => Operation != "Update" || EffectiveChanges.Any();
    }

    /// <summary>What the application intends to do, shown on the Review screen before anything is applied.</summary>
    public sealed class ChangePlan
    {
        public string Title { get; set; }
        public string OperationId { get; set; }
        public RiskLevel Risk { get; set; }
        public bool RequiresStrongConfirmation { get; set; }

        /// <summary>The text the user must type to confirm destructive operations (e.g. the number of affected items).</summary>
        public string ConfirmationPhrase { get; set; }

        public List<KeyValuePair<string, string>> Summary { get; } = new List<KeyValuePair<string, string>>();
        public List<PlannedChange> Items { get; } = new List<PlannedChange>();
        public List<string> Warnings { get; } = new List<string>();
        public PreviewKind PreviewKind { get; set; } = PreviewKind.ValidatedPreview;
        public bool WhatIfAvailable { get; set; }
        public List<string> WhatIfOutput { get; } = new List<string>();
        public string ScriptPreview { get; set; }

        public int ChangedItemCount => Items.Count(i => i.HasChanges);

        public int UnchangedItemCount => Items.Count(i => !i.HasChanges);
    }

    public static class DiffBuilder
    {
        /// <summary>Compare current values to desired values; only keys present in <paramref name="desired"/> are compared.</summary>
        public static List<PropertyChange> Compute(IReadOnlyDictionary<string, object> current, IReadOnlyDictionary<string, object> desired, IReadOnlyDictionary<string, string> displayNames = null)
        {
            var result = new List<PropertyChange>();
            foreach (var kv in desired)
            {
                current.TryGetValue(kv.Key, out var cur);
                string dn = null;
                displayNames?.TryGetValue(kv.Key, out dn);
                result.Add(new PropertyChange { Property = kv.Key, DisplayName = dn ?? kv.Key, Current = cur, New = kv.Value });
            }

            return result;
        }
    }
}
