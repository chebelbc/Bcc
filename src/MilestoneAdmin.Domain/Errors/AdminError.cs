using System.Collections.Generic;
using System.Linq;
using System.Text;
using MilestoneAdmin.Protocol;

namespace MilestoneAdmin.Domain.Errors
{
    public enum ErrorKind
    {
        Unknown,
        NotConnected,
        ServerUnreachable,
        AuthenticationFailed,
        PermissionDenied,
        FeatureNotLicensed,
        VersionNotSupported,
        ElevationRequired,
        InteractiveInputRequired,
        ObjectNotFound,
        InvalidInput,
        Timeout,
        Cancelled,
        PolicyBlocked,
        EulaNotAccepted,
        ModuleNotInstalled,
        ModuleLoadFailed,
        WorkerFailed,
        OperationFailed
    }

    /// <summary>
    /// An administrator-facing error: plain-language summary first, technical detail preserved underneath.
    /// </summary>
    public sealed class AdminError
    {
        public ErrorKind Kind { get; set; }
        public string Summary { get; set; }
        public string Operation { get; set; }
        public string Target { get; set; }
        public string Server { get; set; }
        public List<string> PossibleCauses { get; } = new List<string>();
        public List<string> SuggestedChecks { get; } = new List<string>();
        public ErrorInfo PowerShellError { get; set; }

        public string TechnicalDetails
        {
            get
            {
                var e = PowerShellError;
                if (e == null) return string.Empty;
                var sb = new StringBuilder();
                void Line(string label, string value)
                {
                    if (!string.IsNullOrWhiteSpace(value)) sb.Append(label).Append(": ").AppendLine(value);
                }

                Line("Command", e.CommandName);
                Line("Error ID", e.FullyQualifiedErrorId);
                Line("Category", e.Category);
                Line("Reason", e.Reason);
                Line("Target", e.TargetName);
                Line("Exception", e.ExceptionType);
                if (e.ExceptionTypeChain.Count > 1) Line("Exception chain", string.Join(" > ", e.ExceptionTypeChain));
                if (e.InnerMessages.Count > 0) Line("Inner messages", string.Join(" | ", e.InnerMessages));
                Line("Recommended action", e.RecommendedAction);
                return sb.ToString().TrimEnd();
            }
        }

        public string PowerShellMessage => PowerShellError?.Message;

        public string StackText => string.Join("\n\n", new[] { PowerShellError?.ScriptStackTrace, PowerShellError?.PositionMessage, PowerShellError?.StackTrace }.Where(s => !string.IsNullOrWhiteSpace(s)));

        /// <summary>Full text for clipboard / support tickets (never contains secrets: the worker scrubs them).</summary>
        public string ToReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Summary);
            if (!string.IsNullOrEmpty(Operation)) sb.AppendLine().Append("Operation: ").AppendLine(Operation);
            if (!string.IsNullOrEmpty(Target)) sb.Append("Target: ").AppendLine(Target);
            if (!string.IsNullOrEmpty(Server)) sb.Append("Server: ").AppendLine(Server);
            if (PossibleCauses.Count > 0)
            {
                sb.AppendLine().AppendLine("Possible causes:");
                foreach (var c in PossibleCauses) sb.Append("  • ").AppendLine(c);
            }

            if (SuggestedChecks.Count > 0)
            {
                sb.AppendLine().AppendLine("Suggested checks:");
                foreach (var c in SuggestedChecks) sb.Append("  • ").AppendLine(c);
            }

            if (PowerShellError != null)
            {
                sb.AppendLine().AppendLine("PowerShell error:").AppendLine(PowerShellMessage);
                sb.AppendLine().AppendLine("Technical details:").AppendLine(TechnicalDetails);
                if (!string.IsNullOrEmpty(StackText)) sb.AppendLine().AppendLine("Stack / position:").AppendLine(StackText);
            }

            return sb.ToString().TrimEnd();
        }
    }
}
