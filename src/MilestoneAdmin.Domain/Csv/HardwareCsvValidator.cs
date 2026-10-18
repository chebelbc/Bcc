using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security;

namespace MilestoneAdmin.Domain.Csv
{
    /// <summary>Known values on the connected site, used for online validation. Leave null to validate offline only.</summary>
    public sealed class HardwareCsvSiteContext
    {
        public HashSet<string> RecordingServers { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>recording server name -> driver numbers installed on it.</summary>
        public Dictionary<string, HashSet<int>> DriverNumbers { get; } = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>recording server name -> storage names.</summary>
        public Dictionary<string, HashSet<string>> StorageNames { get; } = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Hardware addresses already on the site (normalized host[:port]).</summary>
        public HashSet<string> ExistingAddresses { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class CsvRowResult
    {
        public int LineNumber { get; set; }
        public Dictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> Errors { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();

        public bool IsValid => Errors.Count == 0;

        public string Status => IsValid ? (Warnings.Count > 0 ? "Valid (warnings)" : "Valid") : "Invalid";

        public string Messages => string.Join("; ", Errors.Concat(Warnings));

        public string Get(string column) => Values.TryGetValue(column, out var v) ? v : string.Empty;
    }

    public sealed class ChannelImportItem
    {
        public int Channel { get; set; }
        public string Name { get; set; }
        public bool? Enabled { get; set; }
        public string StorageName { get; set; }
        public string Coordinates { get; set; }
        public int SourceLine { get; set; }
    }

    /// <summary>One hardware unit to add, aggregated from all camera rows with the same recording server + address.</summary>
    public sealed class HardwareImportItem
    {
        public string RecordingServer { get; set; }
        public Uri Address { get; set; }
        public string HardwareName { get; set; }
        public int? DriverNumber { get; set; }
        public List<string> DriverGroups { get; } = new List<string>();
        public string UserName { get; set; }

        /// <summary>Password from the CSV, if any. Held as SecureString in memory only; never logged or displayed.</summary>
        public SecureString Password { get; set; }

        public bool HasCsvCredential => !string.IsNullOrEmpty(UserName) && Password != null;
        public List<ChannelImportItem> Channels { get; } = new List<ChannelImportItem>();
        public List<int> SourceLines { get; } = new List<int>();
        public bool AlreadyOnSite { get; set; }

        public string DisplayAddress => Address?.ToString();
    }

    public sealed class HardwareCsvReport
    {
        public List<CsvRowResult> Rows { get; } = new List<CsvRowResult>();
        public List<string> FileWarnings { get; } = new List<string>();
        public List<string> FileErrors { get; } = new List<string>();
        public List<HardwareImportItem> Hardware { get; } = new List<HardwareImportItem>();
        public bool ValidatedOnline { get; set; }

        public int TotalRows => Rows.Count;
        public int ValidRows => Rows.Count(r => r.IsValid);
        public int InvalidRows => Rows.Count(r => !r.IsValid);
        public bool CanApply => FileErrors.Count == 0 && ValidRows > 0;

        public string SummaryText => $"{TotalRows} rows detected, {ValidRows} valid, {InvalidRows} invalid";
    }

    /// <summary>
    /// Validates hardware onboarding CSV files. Columns follow the format documented for MilestonePSTools'
    /// Import-VmsHardware (docs/commands/en-US/Import-VmsHardware.md): DeviceType, Name, Address, Channel, UserName,
    /// Password, DriverNumber, DriverGroup, RecordingServer, Enabled, HardwareName, StorageName, Coordinates, DeviceGroups.
    /// </summary>
    public static class HardwareCsvValidator
    {
        public static readonly string[] KnownColumns =
        {
            "DeviceType", "Name", "Address", "Channel", "UserName", "Password", "DriverNumber", "DriverGroup", "RecordingServer",
            "Enabled", "HardwareName", "StorageName", "Coordinates", "DeviceGroups"
        };

        /// <summary>DeviceType values accepted by Import-VmsHardware.</summary>
        public static readonly string[] DeviceTypes = { "Camera", "Microphone", "Speaker", "Metadata", "Input", "Output" };

        public static HardwareCsvReport Validate(CsvTable table, string defaultRecordingServer = null, HardwareCsvSiteContext site = null)
        {
            var report = new HardwareCsvReport { ValidatedOnline = site != null };
            if (table.IndexOf("Address") < 0) report.FileErrors.Add("The required column 'Address' is missing.");
            foreach (var h in table.Headers.Where(h => h.Length > 0 && !KnownColumns.Contains(h, StringComparer.OrdinalIgnoreCase)))
                report.FileWarnings.Add($"Column '{h}' is not recognized and will be ignored.");
            if (table.IndexOf("Password") >= 0)
                report.FileWarnings.Add("This file contains plain-text passwords. Protect or delete it after use. Passwords are never displayed or logged by this application.");
            if (table.IndexOf("DeviceGroups") >= 0)
                report.FileWarnings.Add("The DeviceGroups column is not applied by guided onboarding. Use Import-VmsHardware (Expert Mode) to assign device groups.");
            if (table.Rows.Count == 0) report.FileErrors.Add("The file contains no data rows.");

            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < table.Rows.Count; i++)
            {
                var row = new CsvRowResult { LineNumber = table.RowLineNumbers[i] };
                for (var c = 0; c < table.Headers.Count; c++)
                {
                    var key = table.Headers[c];
                    if (key.Length == 0) continue;
                    // Passwords are kept out of the row display model.
                    row.Values[key] = string.Equals(key, "Password", StringComparison.OrdinalIgnoreCase) ? (table.Rows[i][c].Length > 0 ? "********" : string.Empty) : table.Rows[i][c].Trim();
                }

                ValidateRow(row, table, i, defaultRecordingServer, site, seen);
                report.Rows.Add(row);
            }

            if (report.FileErrors.Count == 0) BuildHardware(report, table, defaultRecordingServer, site);
            return report;
        }

        private static void ValidateRow(CsvRowResult row, CsvTable table, int index, string defaultRecorder, HardwareCsvSiteContext site, Dictionary<string, int> seen)
        {
            var deviceType = row.Get("DeviceType");
            if (deviceType.Length == 0) deviceType = "Camera";
            if (!DeviceTypes.Contains(deviceType, StringComparer.OrdinalIgnoreCase))
                row.Errors.Add($"Invalid DeviceType '{deviceType}'. Supported values: {string.Join(", ", DeviceTypes)}.");
            else if (!string.Equals(deviceType, "Camera", StringComparison.OrdinalIgnoreCase))
                row.Warnings.Add($"{deviceType} rows are not configured by guided onboarding (the hardware is still added).");

            var address = row.Get("Address");
            Uri uri = null;
            if (address.Length == 0) row.Errors.Add("Address is required.");
            else if (!TryParseAddress(address, out uri)) row.Errors.Add($"Invalid Address '{address}'. Use an IP address, host name or http(s):// URL.");

            var channel = 0;
            var channelText = row.Get("Channel");
            if (channelText.Length > 0 && (!int.TryParse(channelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out channel) || channel < 0 || channel > 1023))
                row.Errors.Add($"Invalid Channel '{channelText}'. Use a whole number starting at 0.");

            var driverText = row.Get("DriverNumber");
            int driver = 0;
            if (driverText.Length > 0 && (!int.TryParse(driverText, NumberStyles.Integer, CultureInfo.InvariantCulture, out driver) || driver <= 0))
                row.Errors.Add($"Invalid DriverNumber '{driverText}'.");

            var enabled = row.Get("Enabled");
            if (enabled.Length > 0 && !TryParseBool(enabled, out _)) row.Errors.Add($"Invalid Enabled value '{enabled}'. Use True or False.");

            var coords = row.Get("Coordinates");
            if (coords.Length > 0 && !TryParseCoordinates(coords, out _, out _)) row.Errors.Add($"Invalid Coordinates '{coords}'. Use 'latitude, longitude' in decimal degrees.");

            var userIdx = table.IndexOf("UserName");
            var passIdx = table.IndexOf("Password");
            var hasUser = userIdx >= 0 && table.Rows[index][userIdx].Length > 0;
            var hasPass = passIdx >= 0 && table.Rows[index][passIdx].Length > 0;
            if (hasUser != hasPass) row.Warnings.Add("UserName and Password should be provided together; the common credential will be used instead.");

            var recorder = row.Get("RecordingServer");
            if (recorder.Length == 0) recorder = defaultRecorder ?? string.Empty;
            if (recorder.Length == 0) row.Errors.Add("RecordingServer is required (in the file or as the default in the wizard).");

            if (site != null && recorder.Length > 0)
            {
                if (!site.RecordingServers.Contains(recorder)) row.Errors.Add($"Recording server '{recorder}' does not exist on this site.");
                else
                {
                    if (driver > 0 && site.DriverNumbers.TryGetValue(recorder, out var drivers) && !drivers.Contains(driver))
                        row.Errors.Add($"Driver {driver} is not installed on recording server '{recorder}'. A newer device pack may be required.");
                    var storage = row.Get("StorageName");
                    if (storage.Length > 0 && site.StorageNames.TryGetValue(recorder, out var storages) && !storages.Contains(storage))
                        row.Errors.Add($"Storage '{storage}' does not exist on recording server '{recorder}'.");
                }

                if (uri != null && site.ExistingAddresses.Contains(NormalizeAddress(uri)))
                    row.Warnings.Add("Hardware with this address already exists on the site; it will be skipped.");
            }

            if (uri != null)
            {
                var key = $"{NormalizeAddress(uri)}|{deviceType}|{channel}";
                if (seen.TryGetValue(key, out var firstLine)) row.Errors.Add($"Duplicate of line {firstLine} (same address, device type and channel).");
                else seen[key] = row.LineNumber;
            }
        }

        private static void BuildHardware(HardwareCsvReport report, CsvTable table, string defaultRecorder, HardwareCsvSiteContext site)
        {
            var groups = new Dictionary<string, HardwareImportItem>(StringComparer.OrdinalIgnoreCase);
            var passIdx = table.IndexOf("Password");
            for (var i = 0; i < report.Rows.Count; i++)
            {
                var row = report.Rows[i];
                if (!row.IsValid) continue;
                TryParseAddress(row.Get("Address"), out var uri);
                var recorder = row.Get("RecordingServer");
                if (recorder.Length == 0) recorder = defaultRecorder;
                var key = recorder + "|" + NormalizeAddress(uri);
                if (!groups.TryGetValue(key, out var hw))
                {
                    hw = new HardwareImportItem
                    {
                        RecordingServer = recorder,
                        Address = uri,
                        HardwareName = NullIfEmpty(row.Get("HardwareName")),
                        UserName = NullIfEmpty(row.Get("UserName")),
                        AlreadyOnSite = site != null && site.ExistingAddresses.Contains(NormalizeAddress(uri))
                    };
                    if (int.TryParse(row.Get("DriverNumber"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) hw.DriverNumber = d;
                    hw.DriverGroups.AddRange(row.Get("DriverGroup").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0));
                    if (passIdx >= 0 && table.Rows[i][passIdx].Length > 0 && hw.UserName != null)
                    {
                        var ss = new SecureString();
                        foreach (var ch in table.Rows[i][passIdx]) ss.AppendChar(ch);
                        ss.MakeReadOnly();
                        hw.Password = ss;
                    }

                    groups[key] = hw;
                    report.Hardware.Add(hw);
                }
                else if (hw.HardwareName == null && row.Get("HardwareName").Length > 0)
                {
                    hw.HardwareName = row.Get("HardwareName");
                }

                hw.SourceLines.Add(row.LineNumber);
                var deviceType = row.Get("DeviceType");
                if (deviceType.Length == 0 || string.Equals(deviceType, "Camera", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(row.Get("Channel"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch);
                    bool? enabled = null;
                    if (TryParseBool(row.Get("Enabled"), out var en)) enabled = en;
                    hw.Channels.Add(new ChannelImportItem
                    {
                        Channel = ch,
                        Name = NullIfEmpty(row.Get("Name")),
                        Enabled = enabled,
                        StorageName = NullIfEmpty(row.Get("StorageName")),
                        Coordinates = NullIfEmpty(row.Get("Coordinates")),
                        SourceLine = row.LineNumber
                    });
                }
            }
        }

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        public static bool TryParseAddress(string text, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (!text.Contains("://")) text = "http://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
            if (u.HostNameType == UriHostNameType.Unknown || string.IsNullOrEmpty(u.Host)) return false;
            if (u.HostNameType == UriHostNameType.Dns)
            {
                // Reject things like "10.1.1" that parse as DNS names but are clearly malformed IPv4 addresses.
                var parts = u.Host.Split('.');
                if (parts.All(p => p.All(char.IsDigit)) && !IPAddress.TryParse(u.Host, out _)) return false;
                if (parts.All(p => p.All(char.IsDigit))) return false;
            }

            uri = new Uri(u.GetLeftPart(UriPartial.Authority) + "/");
            return true;
        }

        public static string NormalizeAddress(Uri uri) => uri == null ? string.Empty : (uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port).ToLowerInvariant();

        public static bool TryParseBool(string s, out bool value)
        {
            value = false;
            if (string.IsNullOrWhiteSpace(s)) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "true":
                case "1":
                case "yes":
                case "y":
                    value = true;
                    return true;
                case "false":
                case "0":
                case "no":
                case "n":
                    value = false;
                    return true;
                default:
                    return false;
            }
        }

        public static bool TryParseCoordinates(string s, out double latitude, out double longitude)
        {
            latitude = longitude = 0;
            var parts = s.Split(',');
            if (parts.Length != 2) return false;
            if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out latitude)) return false;
            if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out longitude)) return false;
            return latitude >= -90 && latitude <= 90 && longitude >= -180 && longitude <= 180;
        }
    }
}
