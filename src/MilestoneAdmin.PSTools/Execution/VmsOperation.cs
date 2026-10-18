using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Domain.Scripting;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.PSTools.Execution
{
    /// <summary>
    /// A fully described MilestonePSTools invocation: what will run, with which typed parameters, and how risky it is.
    /// The same object drives execution, the Expert Mode script preview and the audit record.
    /// </summary>
    public sealed class VmsOperation
    {
        public string Title { get; set; }

        /// <summary>Human-readable target, e.g. "Lobby Camera 04" or "12 cameras". Used in activity and audit logs.</summary>
        public string Target { get; set; }

        public string TargetType { get; set; }

        public List<PipelineStep> Pipeline { get; } = new List<PipelineStep>();

        public Dictionary<string, SecureString> Secrets { get; } = new Dictionary<string, SecureString>(StringComparer.Ordinal);

        public Dictionary<string, string> SecretDescriptions { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public bool AllowStateChange { get; set; }

        public bool WhatIf { get; set; }

        public bool Verbose { get; set; }

        public int TimeoutSeconds { get; set; } = 600;

        public RiskLevel Risk { get; set; } = RiskLevel.ReadOnly;

        public OutputOptions Output { get; set; } = new OutputOptions();

        public string PrimaryCommand => Pipeline.LastOrDefault()?.Command;

        public bool IsAuditable => AllowStateChange || Risk == RiskLevel.Sensitive || Output.AllowSensitive;

        public string AddSecret(SecureString value, string description)
        {
            var id = "s" + (Secrets.Count + 1);
            Secrets[id] = value;
            SecretDescriptions[id] = description;
            return id;
        }

        public VmsOperation AsWhatIf()
        {
            var copy = (VmsOperation)MemberwiseClone();
            copy.WhatIf = true;
            return copy;
        }

        public ScriptInvocation ToScriptInvocation()
        {
            var s = new ScriptInvocation { Title = Title, WhatIf = WhatIf };
            s.Pipeline.AddRange(Pipeline);
            foreach (var kv in SecretDescriptions) s.SecretDescriptions[kv.Key] = kv.Value;
            return s;
        }

        /// <summary>
        /// Builds the wire request. Secret values exist as managed strings only for the duration of serialization
        /// (managed strings cannot be wiped reliably; see SECURITY.md).
        /// </summary>
        public InvokeRequest ToRequest(string id)
        {
            var r = new InvokeRequest
            {
                Id = id,
                Operation = Title,
                AllowStateChange = AllowStateChange,
                WhatIf = WhatIf,
                Verbose = Verbose,
                TimeoutSeconds = TimeoutSeconds,
                Output = Output
            };
            r.Pipeline.AddRange(Pipeline);
            foreach (var kv in Secrets) r.Secrets[kv.Key] = Unprotect(kv.Value);
            return r;
        }

        internal static string Unprotect(SecureString s)
        {
            if (s == null) return string.Empty;
            var ptr = IntPtr.Zero;
            try
            {
                ptr = Marshal.SecureStringToGlobalAllocUnicode(s);
                return Marshal.PtrToStringUni(ptr) ?? string.Empty;
            }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(ptr);
            }
        }
    }

    public enum OperationEventKind
    {
        Progress,
        Warning,
        Verbose,
        Information,
        Debug,
        Host,
        WhatIf,
        Error,
        Log
    }

    public sealed class OperationEvent
    {
        public OperationEventKind Kind { get; set; }
        public string Message { get; set; }
        public string Activity { get; set; }
        public int? Percent { get; set; }
        public bool Completed { get; set; }
        public ErrorInfo Error { get; set; }
    }

    public sealed class OperationResult
    {
        public bool Success { get; set; }
        public List<JsonNode> Items { get; } = new List<JsonNode>();
        public List<ErrorInfo> Errors { get; } = new List<ErrorInfo>();
        public ErrorInfo TerminatingError { get; set; }
        public bool Cancelled { get; set; }
        public bool TimedOut { get; set; }
        public bool WhatIf { get; set; }
        public bool Truncated { get; set; }
        public bool WorkerUnhealthy { get; set; }
        public TimeSpan Duration { get; set; }
        public List<string> WhatIfLines { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> HostLines { get; } = new List<string>();

        public bool HadErrors => Errors.Count > 0;

        /// <summary>The most relevant error for display: the terminating error, else the first non-terminating one.</summary>
        public ErrorInfo PrimaryError => TerminatingError ?? Errors.FirstOrDefault();

        public IEnumerable<JsonObject> Objects => Items.OfType<JsonObject>();

        public static OperationResult FromFailure(ErrorInfo error) => new OperationResult { Success = false, TerminatingError = error };

        public static OperationResult FromJson(JsonObject result)
        {
            var r = new OperationResult
            {
                Success = result.GetBool("success"),
                Cancelled = result.GetBool("cancelled"),
                TimedOut = result.GetBool("timedOut"),
                WhatIf = result.GetBool("whatIf"),
                Truncated = result.GetBool("truncated"),
                WorkerUnhealthy = result.GetBool("workerUnhealthy"),
                Duration = TimeSpan.FromMilliseconds(result.GetInt64("durationMs")),
                TerminatingError = ErrorInfo.FromJson(result.GetObject("terminatingError"))
            };
            foreach (var i in result.GetArray("items") ?? new JsonArray()) r.Items.Add(i);
            foreach (var e in result.GetArray("errors") ?? new JsonArray()) r.Errors.Add(ErrorInfo.FromJson(e.AsObject()));
            return r;
        }
    }
}
