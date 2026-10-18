using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MilestoneAdmin.Domain.Changes;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.Protocol.Json;
using MilestoneAdmin.PSTools.Commands;

namespace MilestoneAdmin.Application.Automation
{
    public enum CameraSource
    {
        AllCameras,
        RecordingServer,
        Hardware
    }

    public enum FilterOperator
    {
        Equals,
        NotEquals,
        Contains,
        StartsWith,
        IsTrue,
        IsFalse
    }

    public sealed class JobFilter
    {
        public string Property { get; set; }
        public FilterOperator Operator { get; set; }
        public string Value { get; set; }

        public override string ToString() => Operator == FilterOperator.IsTrue || Operator == FilterOperator.IsFalse ? $"{Property} {Operator}" : $"{Property} {Operator} '{Value}'";
    }

    public enum JobActionKind
    {
        /// <summary>Set one camera property through Set-VmsCamera (review + optional WhatIf before apply).</summary>
        SetCameraProperty,

        /// <summary>Export the filtered cameras as an inventory report (read-only).</summary>
        ExportInventory
    }

    /// <summary>
    /// A deterministic, data-only job: source -> filters -> one action. Jobs never contain PowerShell code; they are
    /// translated into the same typed operations and review screens as the guided workflows.
    /// </summary>
    public sealed class JobDefinition
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "New job";
        public CameraSource Source { get; set; } = CameraSource.AllCameras;
        public string SourceName { get; set; }
        public List<JobFilter> Filters { get; } = new List<JobFilter>();
        public JobActionKind Action { get; set; } = JobActionKind.ExportInventory;
        public string ActionProperty { get; set; }
        public string ActionValue { get; set; }

        public static readonly string[] FilterableProperties = { "Name", "Enabled", "RecordingEnabled", "RecordingFramerate", "HardwareName", "HardwareAddress", "RecordingServerName", "RecordingStorageName", "Channel" };

        public IEnumerable<string> Describe()
        {
            yield return "Connect to XProtect (current session)";
            yield return Source == CameraSource.AllCameras ? "Get all cameras" : $"Get cameras of {Source} '{SourceName}'";
            foreach (var f in Filters) yield return "Filter: " + f;
            yield return Action == JobActionKind.SetCameraProperty ? $"Set {ActionProperty} = {ActionValue} (review before apply)" : "Generate camera inventory report";
        }

        public List<CameraInfo> Select(IEnumerable<CameraInfo> cameras)
        {
            var list = cameras;
            if (Source == CameraSource.RecordingServer) list = list.Where(c => string.Equals(c.RecordingServerName, SourceName, StringComparison.OrdinalIgnoreCase));
            if (Source == CameraSource.Hardware) list = list.Where(c => string.Equals(c.HardwareName, SourceName, StringComparison.OrdinalIgnoreCase));
            foreach (var f in Filters) list = list.Where(c => Matches(c, f)).ToList();
            return list.ToList();
        }

        public static object Get(CameraInfo c, string property)
        {
            switch (property)
            {
                case "Name": return c.Name;
                case "Enabled": return c.Enabled;
                case "RecordingEnabled": return c.RecordingEnabled;
                case "RecordingFramerate": return c.RecordingFramerate;
                case "HardwareName": return c.HardwareName;
                case "HardwareAddress": return c.HardwareAddress;
                case "RecordingServerName": return c.RecordingServerName;
                case "RecordingStorageName": return c.RecordingStorageName;
                case "Channel": return c.Channel;
                default: throw new ArgumentException($"Unknown property '{property}'.");
            }
        }

        public static bool Matches(CameraInfo c, JobFilter f)
        {
            var v = Get(c, f.Property);
            var s = PropertyChange.Format(v);
            switch (f.Operator)
            {
                case FilterOperator.IsTrue: return v is bool b && b;
                case FilterOperator.IsFalse: return v is bool b2 && !b2;
                case FilterOperator.Equals: return string.Equals(s, f.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                case FilterOperator.NotEquals: return !string.Equals(s, f.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                case FilterOperator.Contains: return v != null && s.IndexOf(f.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
                case FilterOperator.StartsWith: return v != null && s.StartsWith(f.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                default: return false;
            }
        }

        /// <summary>Validates and converts the action value to the Set-VmsCamera parameter type.</summary>
        public object ConvertActionValue()
        {
            if (!PsCommands.CameraSettableProperties.TryGetValue(ActionProperty ?? string.Empty, out var t)) throw new FormatException($"'{ActionProperty}' is not a supported camera property.");
            var text = (ActionValue ?? string.Empty).Trim();
            if (t == typeof(bool))
            {
                if (bool.TryParse(text, out var b)) return b;
                throw new FormatException("Enter True or False.");
            }

            if (t == typeof(int))
            {
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
                throw new FormatException("Enter a whole number.");
            }

            if (t == typeof(double))
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0) return d;
                throw new FormatException("Enter a positive number.");
            }

            if (text.Length == 0) throw new FormatException("Enter a value.");
            return text;
        }

        public JsonObject ToJson()
        {
            var filters = new JsonArray();
            foreach (var f in Filters) filters.Add(new JsonObject { { "property", f.Property }, { "operator", f.Operator.ToString() }, { "value", f.Value } });
            return new JsonObject
            {
                { "id", Id }, { "name", Name }, { "source", Source.ToString() }, { "sourceName", SourceName }, { "filters", filters },
                { "action", Action.ToString() }, { "actionProperty", ActionProperty }, { "actionValue", ActionValue }
            };
        }

        public static JobDefinition FromJson(JsonObject o)
        {
            var j = new JobDefinition
            {
                Id = o.GetString("id") ?? Guid.NewGuid().ToString("N"),
                Name = o.GetString("name"),
                Source = Enum.TryParse<CameraSource>(o.GetString("source"), out var s) ? s : CameraSource.AllCameras,
                SourceName = o.GetString("sourceName"),
                Action = Enum.TryParse<JobActionKind>(o.GetString("action"), out var a) ? a : JobActionKind.ExportInventory,
                ActionProperty = o.GetString("actionProperty"),
                ActionValue = o.GetString("actionValue")
            };
            foreach (var f in o.GetArray("filters") ?? new JsonArray())
            {
                var fo = f.AsObject();
                if (fo == null || !FilterableProperties.Contains(fo.GetString("property"))) continue;
                j.Filters.Add(new JobFilter
                {
                    Property = fo.GetString("property"),
                    Operator = Enum.TryParse<FilterOperator>(fo.GetString("operator"), out var op) ? op : FilterOperator.Equals,
                    Value = fo.GetString("value")
                });
            }

            return j;
        }
    }
}
