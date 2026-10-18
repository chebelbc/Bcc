using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.Domain.Audit
{
    public enum AuditResult
    {
        Success,
        Failed,
        PartialFailure,
        Cancelled,
        WhatIf,
        Blocked
    }

    /// <summary>
    /// One audit entry for a configuration-changing (or secret-revealing) operation. Contains no secrets: the command
    /// text is the generated script fragment, in which passwords are always prompts.
    /// </summary>
    public sealed class AuditRecord
    {
        public long Sequence { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public string WindowsUser { get; set; }
        public string Workstation { get; set; }
        public string XProtectSystem { get; set; }
        public string XProtectUser { get; set; }
        public string Operation { get; set; }
        public string TargetType { get; set; }
        public string Target { get; set; }
        public string SanitizedCommand { get; set; }
        public long DurationMs { get; set; }
        public AuditResult Result { get; set; }
        public string Error { get; set; }
        public string CorrelationId { get; set; }
        public string ModuleVersion { get; set; }
        public string PreviousHash { get; set; }
        public string Hash { get; set; }

        public JsonObject ToJson(bool includeHash = true)
        {
            var o = new JsonObject
            {
                { "sequence", Sequence },
                { "timestamp", Timestamp.ToString("o", CultureInfo.InvariantCulture) },
                { "windowsUser", WindowsUser },
                { "workstation", Workstation },
                { "xprotectSystem", XProtectSystem },
                { "xprotectUser", XProtectUser },
                { "operation", Operation },
                { "targetType", TargetType },
                { "target", Target },
                { "sanitizedCommand", SanitizedCommand },
                { "durationMs", DurationMs },
                { "result", Result.ToString() },
                { "error", Error },
                { "correlationId", CorrelationId },
                { "moduleVersion", ModuleVersion },
                { "previousHash", PreviousHash }
            };
            if (includeHash) o.Set("hash", Hash);
            return o;
        }

        public static AuditRecord FromJson(JsonObject o)
        {
            return new AuditRecord
            {
                Sequence = o.GetInt64("sequence"),
                Timestamp = DateTimeOffset.Parse(o.GetString("timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                WindowsUser = o.GetString("windowsUser"),
                Workstation = o.GetString("workstation"),
                XProtectSystem = o.GetString("xprotectSystem"),
                XProtectUser = o.GetString("xprotectUser"),
                Operation = o.GetString("operation"),
                TargetType = o.GetString("targetType"),
                Target = o.GetString("target"),
                SanitizedCommand = o.GetString("sanitizedCommand"),
                DurationMs = o.GetInt64("durationMs"),
                Result = Enum.TryParse<AuditResult>(o.GetString("result"), out var r) ? r : AuditResult.Failed,
                Error = o.GetString("error"),
                CorrelationId = o.GetString("correlationId"),
                ModuleVersion = o.GetString("moduleVersion"),
                PreviousHash = o.GetString("previousHash"),
                Hash = o.GetString("hash")
            };
        }

        /// <summary>SHA-256 over the canonical JSON of the record (without its own hash) chained to the previous hash.</summary>
        public string ComputeHash()
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(ToJson(includeHash: false).ToJson()));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
