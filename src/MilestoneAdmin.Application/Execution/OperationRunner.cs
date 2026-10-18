using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Application.Abstractions;
using MilestoneAdmin.Application.Activity;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.Application.Session;
using MilestoneAdmin.Domain.Audit;
using MilestoneAdmin.Domain.Errors;
using MilestoneAdmin.Domain.Scripting;
using MilestoneAdmin.PSTools.Execution;

namespace MilestoneAdmin.Application.Execution
{
    public sealed class OperationOutcome
    {
        public OperationResult Result { get; set; }
        public AdminError Error { get; set; }
        public AuditRecord Audit { get; set; }

        public bool Success => Result != null && Result.Success && Error == null;

        public bool PartialFailure => Result != null && Result.Success && Result.HadErrors;
    }

    /// <summary>Generates the Expert Mode script for operations, resolving worker handles from the session.</summary>
    public sealed class ScriptService
    {
        private readonly SessionContext _session;
        private readonly IClock _clock;

        public ScriptService(SessionContext session, IClock clock)
        {
            _session = session;
            _clock = clock;
        }

        private ScriptGenerator Generator => new ScriptGenerator(_session.DescribeHandle);

        public string Fragment(VmsOperation op) => Generator.GenerateFragment(op.ToScriptInvocation());

        public string Script(IEnumerable<VmsOperation> ops) =>
            Generator.Generate(ops.Select(o => o.ToScriptInvocation()), _session.ScriptConnection, _session.ModuleVersion, _clock.Now.LocalDateTime);
    }

    /// <summary>
    /// The single entry point for executing VMS operations: activity feedback, PowerShell stream forwarding,
    /// audit records for every change or secret reveal, and error translation. UI code never calls the executor directly.
    /// </summary>
    public sealed class OperationRunner
    {
        private readonly SessionContext _session;
        private readonly ActivityLog _activity;
        private readonly IAuditStore _audit;
        private readonly IUserContext _user;
        private readonly IClock _clock;
        private readonly ScriptService _scripts;

        public OperationRunner(SessionContext session, ActivityLog activity, IAuditStore audit, IUserContext user, IClock clock, ScriptService scripts)
        {
            _session = session;
            _activity = activity;
            _audit = audit;
            _user = user;
            _clock = clock;
            _scripts = scripts;
        }

        public bool CaptureVerbose { get; set; }

        public int DefaultTimeoutSeconds { get; set; } = 600;

        public async Task<OperationOutcome> RunAsync(VmsOperation op, OperationContext ctx, IProgress<OperationEvent> progress = null, CancellationToken ct = default, bool quiet = false)
        {
            if (_session.Gateway == null)
            {
                var notConnected = ErrorTranslator.Translate(Protocol.ErrorInfo.FromMessage("Not connected to an XProtect management server. Connect first.", "PolicyViolationException"), ctx);
                notConnected.Kind = ErrorKind.NotConnected;
                return new OperationOutcome { Result = OperationResult.FromFailure(notConnected.PowerShellError), Error = notConnected };
            }

            if (op.TimeoutSeconds == 600 && DefaultTimeoutSeconds != 600) op.TimeoutSeconds = DefaultTimeoutSeconds;
            op.Verbose = op.Verbose || CaptureVerbose;
            var label = op.WhatIf ? $"{op.Title} (PowerShell -WhatIf preview)" : op.Title;
            if (!quiet) _activity.Info($"{label}{(op.Target != null ? ": " + op.Target : string.Empty)}...", op.Title);

            var forward = new Progress<OperationEvent>(e =>
            {
                switch (e.Kind)
                {
                    case OperationEventKind.Warning:
                        _activity.Warning(e.Message, op.Title);
                        break;
                    case OperationEventKind.WhatIf:
                        _activity.Detail(e.Message, op.Title);
                        break;
                    case OperationEventKind.Error:
                        _activity.Error(e.Message, op.Title);
                        break;
                    case OperationEventKind.Verbose:
                        if (CaptureVerbose) _activity.Detail(e.Message, op.Title);
                        break;
                    case OperationEventKind.Host:
                    case OperationEventKind.Information:
                        _activity.Detail(e.Message, op.Title);
                        break;
                }

                progress?.Report(e);
            });

            var started = _clock.Now;
            OperationResult result;
            try
            {
                result = await _session.Gateway.ExecuteAsync(op, forward, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = OperationResult.FromFailure(Protocol.ErrorInfo.FromMessage(ex.Message, ex.GetType().FullName));
            }

            var outcome = new OperationOutcome { Result = result };
            if (!result.Success) outcome.Error = ErrorTranslator.Translate(result, ctx);

            if (!quiet)
            {
                if (outcome.Error != null) _activity.Error(outcome.Error.Summary, op.Title);
                else if (result.HadErrors) _activity.Warning($"{op.Title}: completed with {result.Errors.Count} error(s).", op.Title);
                else _activity.Success($"{label}: done ({result.Items.Count} item(s), {result.Duration.TotalSeconds:0.0} s).", op.Title);
            }

            if (op.IsAuditable) outcome.Audit = WriteAudit(op, ctx, result, outcome.Error, started);
            if (result.WorkerUnhealthy) _activity.Error("The PowerShell worker had to be stopped. Reconnect to continue.");
            return outcome;
        }

        private AuditRecord WriteAudit(VmsOperation op, OperationContext ctx, OperationResult result, AdminError error, DateTimeOffset started)
        {
            AuditResult status;
            if (result.WhatIf && result.Success) status = AuditResult.WhatIf;
            else if (result.Cancelled) status = AuditResult.Cancelled;
            else if (error?.Kind == ErrorKind.PolicyBlocked) status = AuditResult.Blocked;
            else if (!result.Success) status = AuditResult.Failed;
            else if (result.HadErrors) status = AuditResult.PartialFailure;
            else status = AuditResult.Success;

            var record = new AuditRecord
            {
                Timestamp = started,
                WindowsUser = _user.WindowsUser,
                Workstation = _user.MachineName,
                XProtectSystem = _session.Connection?.ServerAddress?.ToString() ?? _session.SystemName,
                XProtectUser = _session.ConnectedAccount,
                Operation = op.WhatIf ? op.Title + " (WhatIf)" : op.Title,
                TargetType = op.TargetType,
                Target = op.Target ?? ctx.Target,
                SanitizedCommand = SafeFragment(op),
                DurationMs = (long)result.Duration.TotalMilliseconds,
                Result = status,
                Error = error?.Summary ?? (result.HadErrors ? string.Join(" | ", result.Errors.Select(e => e.Message).Take(5)) : null),
                ModuleVersion = _session.ModuleVersion,
                CorrelationId = Guid.NewGuid().ToString("N")
            };
            try
            {
                _audit.Append(record);
            }
            catch (Exception ex)
            {
                // Never hide this: an unwritable audit log is an operational problem the administrator must see.
                _activity.Error("Audit record could not be written: " + ex.Message);
            }

            return record;
        }

        private string SafeFragment(VmsOperation op)
        {
            try
            {
                return _scripts.Fragment(op);
            }
            catch (Exception)
            {
                return op.PrimaryCommand;
            }
        }
    }

    public enum BulkItemStatus
    {
        Pending,
        Running,
        Succeeded,
        SucceededWithWarnings,
        Failed,
        Skipped,
        Cancelled
    }

    public sealed class BulkItemResult<T>
    {
        public T Item { get; set; }
        public string Name { get; set; }
        public BulkItemStatus Status { get; set; }
        public AdminError Error { get; set; }
        public OperationOutcome Outcome { get; set; }
        public string Message { get; set; }
    }

    public sealed class BulkProgress
    {
        public int Completed { get; set; }
        public int Total { get; set; }
        public string Current { get; set; }
        public int Failed { get; set; }

        public double Percent => Total == 0 ? 100 : 100.0 * Completed / Total;
    }

    public sealed class BulkResult<T>
    {
        public List<BulkItemResult<T>> Items { get; } = new List<BulkItemResult<T>>();

        public int Succeeded => Items.Count(i => i.Status == BulkItemStatus.Succeeded || i.Status == BulkItemStatus.SucceededWithWarnings);

        public int Failed => Items.Count(i => i.Status == BulkItemStatus.Failed);

        public int Cancelled => Items.Count(i => i.Status == BulkItemStatus.Cancelled);

        public int Skipped => Items.Count(i => i.Status == BulkItemStatus.Skipped);

        public IEnumerable<T> FailedItems => Items.Where(i => i.Status == BulkItemStatus.Failed || i.Status == BulkItemStatus.Cancelled).Select(i => i.Item);

        public string Summary => $"{Succeeded} succeeded, {Failed} failed" + (Cancelled > 0 ? $", {Cancelled} cancelled" : string.Empty) + (Skipped > 0 ? $", {Skipped} skipped" : string.Empty);

        /// <summary>CSV of failed and cancelled items with their error summaries (for the "Export failed items" button).</summary>
        public string FailedItemsCsv() =>
            Domain.Csv.CsvParser.Write(new[] { "Item", "Status", "Error", "Details" },
                Items.Where(i => i.Status == BulkItemStatus.Failed || i.Status == BulkItemStatus.Cancelled)
                    .Select(i => (IReadOnlyList<string>)new[] { i.Name, i.Status.ToString(), i.Error?.Summary ?? i.Message ?? string.Empty, i.Error?.PowerShellMessage ?? string.Empty }));
    }

    /// <summary>
    /// Applies one operation per object, sequentially, reporting per-object results. Partial failures are never
    /// hidden; failed items can be retried. Cancellation stops after the current object (the current object is
    /// itself cancelled through the worker when possible).
    /// </summary>
    public sealed class BulkOperationRunner
    {
        private readonly OperationRunner _runner;
        private readonly ActivityLog _activity;

        public BulkOperationRunner(OperationRunner runner, ActivityLog activity)
        {
            _runner = runner;
            _activity = activity;
        }

        public async Task<BulkResult<T>> RunAsync<T>(string title, IReadOnlyList<T> items, Func<T, VmsOperation> build, Func<T, string> name,
            Func<T, OperationContext> context, IProgress<BulkProgress> progress, CancellationToken ct, Func<T, OperationOutcome, Task> afterEach = null)
        {
            var result = new BulkResult<T>();
            foreach (var i in items) result.Items.Add(new BulkItemResult<T> { Item = i, Name = name(i), Status = BulkItemStatus.Pending });
            _activity.Info($"{title}: {items.Count} object(s)...");
            var done = 0;
            var failed = 0;
            foreach (var r in result.Items)
            {
                if (ct.IsCancellationRequested)
                {
                    r.Status = BulkItemStatus.Cancelled;
                    r.Message = "Not started (cancelled).";
                    continue;
                }

                progress?.Report(new BulkProgress { Completed = done, Total = items.Count, Current = r.Name, Failed = failed });
                r.Status = BulkItemStatus.Running;
                VmsOperation op;
                try
                {
                    op = build(r.Item);
                }
                catch (Exception ex)
                {
                    r.Status = BulkItemStatus.Failed;
                    r.Error = ErrorTranslator.FromException(ex, context(r.Item));
                    failed++;
                    done++;
                    _activity.Error($"{r.Name} ✗ {r.Error.Summary}");
                    continue;
                }

                if (op == null)
                {
                    r.Status = BulkItemStatus.Skipped;
                    r.Message = "No change needed.";
                    done++;
                    continue;
                }

                var outcome = await _runner.RunAsync(op, context(r.Item), null, ct, quiet: true).ConfigureAwait(false);
                r.Outcome = outcome;
                if (outcome.Result.Cancelled)
                {
                    r.Status = BulkItemStatus.Cancelled;
                    r.Error = outcome.Error;
                }
                else if (!outcome.Success)
                {
                    r.Status = BulkItemStatus.Failed;
                    r.Error = outcome.Error ?? ErrorTranslator.Translate(outcome.Result.PrimaryError ?? Protocol.ErrorInfo.FromMessage("Failed"), context(r.Item));
                    failed++;
                    _activity.Error($"{r.Name} ✗ {r.Error.Summary}");
                }
                else if (outcome.PartialFailure)
                {
                    // A non-terminating error on a single-object operation means that object was not (fully) changed.
                    r.Status = BulkItemStatus.Failed;
                    r.Error = ErrorTranslator.Translate(outcome.Result.Errors[0], context(r.Item));
                    failed++;
                    _activity.Error($"{r.Name} ✗ {r.Error.Summary}");
                }
                else
                {
                    r.Status = outcome.Result.Warnings.Count > 0 ? BulkItemStatus.SucceededWithWarnings : BulkItemStatus.Succeeded;
                    _activity.Success($"{r.Name} ✓");
                }

                if (afterEach != null) await afterEach(r.Item, outcome).ConfigureAwait(false);
                done++;
            }

            progress?.Report(new BulkProgress { Completed = done, Total = items.Count, Current = null, Failed = failed });
            if (result.Failed > 0 || result.Cancelled > 0) _activity.Warning($"{title}: {result.Summary}.");
            else _activity.Success($"{title}: {result.Summary}.");
            return result;
        }
    }
}
