using System;
using System.Linq;
using MilestoneAdmin.Domain.Errors;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.PSTools.Execution;

namespace MilestoneAdmin.Application.Errors
{
    /// <summary>Context that turns a low-level failure into an administrator message.</summary>
    public sealed class OperationContext
    {
        public OperationContext(string operation, string target = null, string server = null)
        {
            Operation = operation;
            Target = target;
            Server = server;
        }

        /// <summary>Verb phrase, e.g. "add hardware", "connect to the management server".</summary>
        public string Operation { get; }

        public string Target { get; }

        /// <summary>The server involved (recording server name or management server address), when known.</summary>
        public string Server { get; }
    }

    /// <summary>
    /// Translates PowerShell / MilestonePSTools / worker errors into plain-language messages while preserving all
    /// technical details. Rules key on exception types and error ids taken from the MilestonePSTools source
    /// (src/MilestonePSTools/Attributes.cs) and standard .NET/WCF exceptions.
    /// </summary>
    public static class ErrorTranslator
    {
        public static AdminError Translate(OperationResult result, OperationContext ctx)
        {
            if (result.Cancelled) return Build(ErrorKind.Cancelled, ctx, result.PrimaryError, $"The operation \"{Cap(ctx.Operation)}\" was cancelled.",
                new[] { "Cancellation stops the current object; objects already processed keep their new configuration." }, new[] { "Refresh the view to see the current state." });
            return Translate(result.PrimaryError ?? ErrorInfo.FromMessage("The operation failed without an error message."), ctx, result.TimedOut);
        }

        public static AdminError Translate(ErrorInfo e, OperationContext ctx, bool timedOut = false)
        {
            var text = string.Join(" | ", new[] { e.Message, e.ExceptionMessage, e.FullyQualifiedErrorId, e.Reason }.Concat(e.InnerMessages).Where(s => !string.IsNullOrEmpty(s)));
            var types = string.Join(" ", e.ExceptionTypeChain.Concat(new[] { e.ExceptionType ?? string.Empty }));
            bool Has(string s) => text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
            bool Type(string s) => types.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
            var op = Cap(ctx.Operation);
            var where = ctx.Server != null ? $" on {ctx.Server}" : string.Empty;
            var isConnect = ctx.Operation != null && ctx.Operation.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0;

            if (Type("PolicyViolationException") && Has("Not connected") || Type("VmsNotConnectedException") || Has("Not connected to an XProtect"))
                return Build(ErrorKind.NotConnected, ctx, e, $"Unable to {Lower(op)} because the application is not connected to an XProtect management server.",
                    new[] { "The connection was closed or lost.", "The PowerShell worker was restarted." },
                    new[] { "Open Connect and sign in again." });

            if (Has("AcceptEula") || Has("license agreement"))
                return Build(ErrorKind.EulaNotAccepted, ctx, e, "The Milestone MIP SDK end-user license agreement has not been accepted for this Windows user.",
                    new[] { "MilestonePSTools requires the MIP SDK EULA to be accepted once per Windows user." },
                    new[] { "Accept the license agreement in Settings > MilestonePSTools (or the setup wizard), then connect again." });

            if (Type("VmsFeatureNotAvailableException"))
                return Build(ErrorKind.FeatureNotLicensed, ctx, e, $"Unable to {Lower(op)}: the required feature is not available on this XProtect system.",
                    new[] { "The XProtect product edition or license does not include this feature." },
                    new[] { "Check the product edition and license in System > License.", "See the command's requirements in Command Explorer." });

            if (Type("VmsVersionValidationException"))
                return Build(ErrorKind.VersionNotSupported, ctx, e, $"Unable to {Lower(op)}: this operation requires a newer XProtect version than the connected system.",
                    new[] { "The management server version is older than the minimum required by MilestonePSTools for this command." },
                    new[] { "Compare the XProtect version on the Dashboard with the command requirements in Command Explorer." });

            if (Type("AdminElevationRequiredException"))
                return Build(ErrorKind.ElevationRequired, ctx, e, $"Unable to {Lower(op)}: this operation must be run with local Administrator privileges.",
                    new[] { "The command changes local system configuration (services, certificates or drivers)." },
                    new[] { "Run this operation from an elevated session on the server concerned. The application does not elevate itself." });

            if (Type("InteractiveSessionRequiredException") || Type("InteractiveInputNotSupportedException") || Has("interactive input") || Has("interactive dialog"))
                return Build(ErrorKind.InteractiveInputRequired, ctx, e, $"Unable to {Lower(op)}: the command asked for interactive input, which the application cannot provide.",
                    new[] { "A required value was not supplied.", "The command opens its own dialog." },
                    new[] { "Provide all required values in the form.", "Use the generated PowerShell in an interactive PowerShell window instead." });

            if (Type("PolicyViolationException"))
                return Build(ErrorKind.PolicyBlocked, ctx, e, e.Message, new[] { "The request was blocked by the application's safety rules before anything was executed." }, new string[0]);

            if (timedOut || Type("TimeoutException"))
            {
                if (isConnect)
                    return Build(ErrorKind.ServerUnreachable, ctx, e, $"Unable to connect to {ctx.Target ?? ctx.Server ?? "the management server"}: the server did not respond in time.",
                        new[] { "The management server address or port is wrong.", "A firewall blocks HTTP (80) / HTTPS (443) to the management server.", "The Management Server service is stopped." },
                        new[] { "Check the address in a browser from this workstation.", "Verify the Milestone XProtect Management Server service is running." });
                return Build(ErrorKind.Timeout, ctx, e, $"Unable to {Lower(op)}{where}: the operation did not complete in time and was stopped.",
                    new[] { "The server is slow or unreachable.", "The operation processes a large number of objects." },
                    new[] { "Retry the failed items.", "Increase the operation timeout in Settings." });
            }

            if (Type("EndpointNotFoundException") || Type("CommunicationException") || Type("SocketException") || Type("WebException") || Has("could not be reached") ||
                Has("No connection could be made") || Has("actively refused") || Has("No such host") || Has("name could not be resolved"))
                return Build(ErrorKind.ServerUnreachable, ctx, e,
                    isConnect
                        ? $"Unable to connect because the management server {ctx.Target ?? string.Empty} could not be reached."
                        : $"Unable to {Lower(op)} because {(ctx.Server != null ? "recording server " + ctx.Server : "the server")} could not be reached.",
                    isConnect
                        ? new[] { "Wrong server address or port.", "Network connectivity or DNS problem.", "Firewall blocks TCP 80/443.", "Management Server service unavailable." }
                        : new[] { "Recording Server service unavailable.", "Network connectivity.", "Firewall blocks the recording server port (default 7563).", "Insufficient permissions." },
                    new[] { "Check that the server name resolves and responds from this workstation.", "Check the Windows service status on the server." });

            if (Type("SecurityNegotiationException") || Type("MessageSecurityException") || Type("UnauthorizedAccessException") || Type("InvalidCredentialException") ||
                Has("Login failed") || Has("invalid credentials") || Has("(401)") || Has("401 Unauthorized") || Has("authentication failed") || Has("access is denied") || Has("not authorized"))
            {
                if (isConnect)
                    return Build(ErrorKind.AuthenticationFailed, ctx, e, $"Sign-in to {ctx.Target ?? "the management server"} failed.",
                        new[] { "Wrong user name or password.", "Wrong authentication type (Windows vs. basic user).", "The account is locked or disabled.", "The account is not a member of an XProtect role." },
                        new[] { "Verify the credentials with XProtect Management Client.", "Basic users require 'Basic user' authentication and usually HTTPS." });
                return Build(ErrorKind.PermissionDenied, ctx, e, $"Unable to {Lower(op)}{where}: the XProtect account does not have permission.",
                    new[] { "The role of the connected user does not grant this permission." },
                    new[] { "Check the user's role permissions in Management Client, or connect with an administrator account." });
            }

            if (Type("ItemNotFoundException") || e.Category == "ObjectNotFound" || Has("not found") || Has("no longer available"))
                return Build(ErrorKind.ObjectNotFound, ctx, e, $"Unable to {Lower(op)}: {(ctx.Target != null ? "'" + ctx.Target + "'" : "the object")} was not found. It may have been changed or removed by someone else.",
                    new[] { "The configuration changed since the data was loaded.", "The PowerShell worker was restarted." },
                    new[] { "Refresh the view and try again." });

            if (Type("ParameterBindingException") || Type("PSInvalidCastException") || Type("ArgumentException") || e.Category == "InvalidArgument" || e.Category == "InvalidData")
                return Build(ErrorKind.InvalidInput, ctx, e, $"Unable to {Lower(op)}: a value was not accepted by MilestonePSTools.",
                    new[] { "A value has the wrong format or is out of range." }, new[] { "Review the values entered and the technical details below." });

            if (Type("CommandNotFoundException"))
                return Build(ErrorKind.ModuleLoadFailed, ctx, e, $"Unable to {Lower(op)}: the command is not available in the installed MilestonePSTools version.",
                    new[] { "The installed module is older or newer than the version this feature was validated with." }, new[] { "Check Settings > Environment for the installed version." });

            if (e.ExceptionType == "WorkerFailed" || e.ExceptionType == "WorkerNotRunning" || e.FullyQualifiedErrorId == "WorkerFailed")
                return Build(ErrorKind.WorkerFailed, ctx, e, e.Message,
                    new[] { "The PowerShell worker process stopped (crash, kill or unresponsive operation)." },
                    new[] { "Reconnect. If the problem repeats, check the diagnostics log in Activity." });

            return Build(ErrorKind.OperationFailed, ctx, e, $"Unable to {Lower(op)}{(ctx.Target != null ? " for '" + ctx.Target + "'" : string.Empty)}{where}.",
                new[] { "See the PowerShell error and technical details below." }, new[] { "Retry the operation. If it fails again, copy the details for support." });
        }

        public static AdminError FromException(Exception ex, OperationContext ctx)
        {
            if (ex is PSTools.VmsOperationException voe) return Translate(voe.Result, ctx);
            if (ex is WorkerStartException wse && wse.ModuleError != null)
            {
                var a = Translate(wse.ModuleError, ctx);
                a.Kind = ErrorKind.ModuleLoadFailed;
                a.Summary = "MilestonePSTools could not be loaded: " + wse.ModuleError.Message;
                a.PossibleCauses.Clear();
                a.PossibleCauses.AddRange(new[] { "MilestonePSTools is not installed for this user or for all users.", "The execution policy prevents loading the module.", "Files were downloaded from the Internet and are blocked (Unblock-File)." });
                a.SuggestedChecks.Clear();
                a.SuggestedChecks.AddRange(new[] { "Open the setup wizard's environment check for details.", "Run 'Get-Module -ListAvailable MilestonePSTools' in Windows PowerShell 5.1." });
                return a;
            }

            var info = ErrorInfo.FromMessage(ex.Message, ex.GetType().FullName);
            info.ExceptionTypeChain.Add(ex.GetType().FullName);
            if (ex is WorkerStartException) info.ExceptionType = "WorkerFailed";
            return Translate(info, ctx);
        }

        private static AdminError Build(ErrorKind kind, OperationContext ctx, ErrorInfo e, string summary, string[] causes, string[] checks)
        {
            var a = new AdminError { Kind = kind, Summary = summary, Operation = Cap(ctx.Operation), Target = ctx.Target, Server = ctx.Server, PowerShellError = e };
            a.PossibleCauses.AddRange(causes);
            a.SuggestedChecks.AddRange(checks);
            return a;
        }

        private static string Cap(string s) => string.IsNullOrEmpty(s) ? "Operation" : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string Lower(string s) => string.IsNullOrEmpty(s) ? "complete the operation" : char.ToLowerInvariant(s[0]) + s.Substring(1);
    }
}
