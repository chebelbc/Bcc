using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.Worker.Engine
{
    public sealed class EngineOptions
    {
        public string ModuleName { get; set; } = "MilestonePSTools";

        /// <summary>Import from this path instead of by name (tests, side-by-side module versions).</summary>
        public string ModulePath { get; set; }

        public string RequiredVersion { get; set; }

        /// <summary>null = leave MilestonePSTools telemetry as configured by the user; true/false = force for this process.</summary>
        public bool? DisableTelemetry { get; set; }

        /// <summary>Seconds to wait for a stopped pipeline before declaring the worker unhealthy.</summary>
        public int StopGraceSeconds { get; set; } = 15;
    }

    /// <summary>
    /// Hosts one Windows PowerShell runspace with MilestonePSTools and executes protocol requests one at a time.
    /// Thread-safety: <see cref="Handle"/> must be called sequentially; <see cref="Cancel"/> may be called from any thread.
    /// </summary>
    public sealed class WorkerEngine : IDisposable
    {
        private readonly HandleTable _handles = new HandleTable();
        private readonly object _currentLock = new object();
        private Runspace _runspace;
        private CommandPolicy _policy;
        private EngineOptions _options = new EngineOptions();
        private string _currentRequestId;
        private CancellationTokenSource _currentCts;
        private Action<JsonObject> _hostSink;
        private SecretScrubber _hostScrubber = SecretScrubber.None;
        private PSModuleInfo _module;
        private WorkerHost _host;

        public bool IsInitialized => _policy != null;

        public bool IsUnhealthy { get; private set; }

        public HandleTable Handles => _handles;

        public void Dispose()
        {
            try
            {
                _runspace?.Close();
            }
            catch (Exception)
            {
                // ignore on shutdown
            }

            _runspace?.Dispose();
        }

        public void Cancel(string requestId)
        {
            lock (_currentLock)
            {
                if (_currentCts != null && (requestId == null || requestId == _currentRequestId)) _currentCts.Cancel();
            }
        }

        /// <summary>Process one request. Events (progress, streams, final result) are delivered through <paramref name="emit"/>.</summary>
        public void Handle(JsonObject request, Action<JsonObject> emit)
        {
            var id = request.GetString("id");
            var type = request.GetString("type");
            try
            {
                switch (type)
                {
                    case MessageTypes.Initialize:
                        emit(Result(id, true, new JsonArray { Initialize(ReadOptions(request)) }));
                        break;
                    case MessageTypes.Environment:
                        EnsureRunspace();
                        emit(Result(id, true, new JsonArray { EnvironmentProbe.Collect(_runspace, _options, _module) }));
                        break;
                    case MessageTypes.Discover:
                        RequireInitialized();
                        emit(Result(id, true, new JsonArray { CatalogBuilder.Build(_runspace, _policy, _module, _options) }));
                        break;
                    case MessageTypes.ReleaseHandles:
                        _handles.Release((request.GetArray("handles") ?? new JsonArray()).Strings());
                        emit(Result(id, true, new JsonArray()));
                        break;
                    case MessageTypes.Invoke:
                        RequireInitialized();
                        Invoke(InvokeRequest.FromJson(request), emit);
                        break;
                    default:
                        throw new ProtocolException($"Unknown request type '{type}'.");
                }
            }
            catch (Exception ex) when (ex is ProtocolException || ex is PolicyViolationException)
            {
                emit(Failed(id, ErrorInfo.FromMessage(ex.Message, ex.GetType().FullName)));
            }
            catch (Exception ex)
            {
                emit(Failed(id, ErrorSerializer.FromException(ex, SecretScrubber.None, true)));
            }
        }

        private static EngineOptions ReadOptions(JsonObject request)
        {
            var o = new EngineOptions
            {
                ModuleName = request.GetString("moduleName", "MilestonePSTools"),
                ModulePath = request.GetString("modulePath"),
                RequiredVersion = request.GetString("requiredVersion"),
                StopGraceSeconds = (int)request.GetInt64("stopGraceSeconds", 15)
            };
            var t = request["disableTelemetry"];
            if (t != null && !t.IsNull) o.DisableTelemetry = t.AsBool();
            return o;
        }

        private void RequireInitialized()
        {
            if (!IsInitialized) throw new ProtocolException("The worker has not been initialized. Send 'initialize' first.");
        }

        private void EnsureRunspace()
        {
            if (_runspace != null) return;
            var iss = InitialSessionState.CreateDefault2();
            // The host's execution policy is respected: the worker never changes or bypasses it.
            _host = new WorkerHost(OnHostWrite);
            var host = _host;
            _runspace = RunspaceFactory.CreateRunspace(host, iss);
#if NETFRAMEWORK
            // Windows PowerShell console defaults to STA; some MIP SDK components expect it.
            _runspace.ApartmentState = ApartmentState.STA;
#endif
            _runspace.ThreadOptions = PSThreadOptions.ReuseThread;
            _runspace.Open();
        }

        private JsonObject Initialize(EngineOptions options)
        {
            if (IsInitialized) throw new ProtocolException("The worker is already initialized.");
            _options = options;
            if (options.DisableTelemetry.HasValue)
            {
                // Must be set before the module is imported (docs/commands/en-US/about_Telemetry.md). Process scope only.
                Environment.SetEnvironmentVariable("VMS_ApplicationInsights__Enabled", options.DisableTelemetry.Value ? "false" : "true");
            }

            EnsureRunspace();
            var warnings = new List<string>();
            var hostLines = new List<string>();
            _hostSink = e => hostLines.Add(e.GetString("message"));
            try
            {
                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = _runspace;
                    ps.AddCommand("Microsoft.PowerShell.Core\\Import-Module")
                        .AddParameter("Name", string.IsNullOrEmpty(options.ModulePath) ? options.ModuleName : options.ModulePath)
                        .AddParameter("PassThru")
                        .AddParameter("ErrorAction", ActionPreference.Stop);
                    if (!string.IsNullOrEmpty(options.RequiredVersion) && string.IsNullOrEmpty(options.ModulePath))
                        ps.AddParameter("RequiredVersion", new Version(options.RequiredVersion));
                    var result = ps.Invoke();
                    warnings.AddRange(ps.Streams.Warning.Select(w => w.Message));
                    // -PassThru can also return nested modules and ScriptsToProcess scripts; pick the module itself.
                    var expected = string.IsNullOrEmpty(options.ModulePath) ? options.ModuleName : System.IO.Path.GetFileNameWithoutExtension(options.ModulePath);
                    var modules = result.Select(r => r.BaseObject).OfType<PSModuleInfo>().ToList();
                    _module = modules.FirstOrDefault(m => string.Equals(m.Name, expected, StringComparison.OrdinalIgnoreCase))
                              ?? throw new InvalidOperationException($"Import-Module did not return module information for {expected}.");
                }

                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = _runspace;
                    ps.AddCommand("Microsoft.PowerShell.Core\\Get-Command").AddParameter("Module", _module.Name);
                    var commands = ps.Invoke().Select(o => o.BaseObject).OfType<CommandInfo>().ToList();
                    _policy = new CommandPolicy(_module.Name, commands);
                }
            }
            finally
            {
                _host?.Flush();
                _hostSink = null;
            }

            return new JsonObject
            {
                { "moduleName", _module.Name },
                { "moduleVersion", _module.Version.ToString() },
                { "modulePath", _module.ModuleBase },
                { "commandCount", _policy.Commands.Count() },
                { "psVersion", _runspace.Version?.ToString() },
                { "warnings", new JsonArray(warnings.Select(w => (JsonNode)w)) },
                { "hostOutput", new JsonArray(hostLines.Select(w => (JsonNode)w)) },
                { "telemetryOverride", options.DisableTelemetry.HasValue ? (JsonNode)(options.DisableTelemetry.Value ? "disabled" : "enabled") : JsonNull.Instance }
            };
        }

        private void OnHostWrite(string stream, string message)
        {
            var sink = _hostSink;
            if (sink == null) return;
            sink(new JsonObject { { "type", MessageTypes.Stream }, { "id", _currentRequestId }, { "stream", stream }, { "message", _hostScrubber.Scrub(message) } });
        }

        private void Invoke(InvokeRequest request, Action<JsonObject> emit)
        {
            var scrubber = new SecretScrubber(request.Secrets.Values);
            var steps = new List<(CommandInfo Command, PipelineStep Step)>();
            foreach (var s in request.Pipeline) steps.Add((_policy.Resolve(s.Command), s));

            // ---- policy checks (before anything executes)
            foreach (var (cmd, step) in steps)
            {
                if (CommandPolicy.RequiresInteractiveSession(cmd))
                    throw new PolicyViolationException($"{cmd.Name} opens its own interactive dialog and cannot be run by the application.");
                if (CommandPolicy.IsMutating(cmd) && !request.AllowStateChange)
                    throw new PolicyViolationException($"{cmd.Name} can change the system and was requested without state-change approval.");
                if (CommandPolicy.SensitiveCommands.Contains(cmd.Name) && !request.Output.AllowSensitive)
                    throw new PolicyViolationException($"{cmd.Name} returns secrets and requires explicit approval to reveal them.");
                if (CommandPolicy.SensitiveParameters.TryGetValue(cmd.Name, out var sp) &&
                    step.Parameters.Any(p => sp.Contains(p.Key, StringComparer.OrdinalIgnoreCase)) && !request.Output.AllowSensitive)
                    throw new PolicyViolationException($"The selected options make {cmd.Name} return secrets and require explicit approval.");
            }

            var last = steps[steps.Count - 1].Command;
            if (request.WhatIf && !CommandPolicy.SupportsWhatIf(last))
                throw new PolicyViolationException($"{last.Name} does not support -WhatIf. Use the application's validated preview instead.");
            var referenced = request.Pipeline.SelectMany(p => p.Parameters).SelectMany(p => p.Value.ReferencedSecrets());
            foreach (var r in referenced)
            {
                if (!request.Secrets.ContainsKey(r)) throw new PolicyViolationException($"Secret '{r}' was referenced but not supplied.");
            }

            var userCts = new CancellationTokenSource();
            var timeoutCts = request.TimeoutSeconds > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds)) : new CancellationTokenSource();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(userCts.Token, timeoutCts.Token);
            if (steps.Any(s => CommandPolicy.RequiresConnection(s.Command)) && !IsConnected())
            {
                cts.Dispose();
                userCts.Dispose();
                timeoutCts.Dispose();
                throw new PolicyViolationException(NotConnectedMessage);
            }

            lock (_currentLock)
            {
                _currentRequestId = request.Id;
                _currentCts = userCts;
            }

            _hostScrubber = scrubber;
            _hostSink = emit;
            var sw = Stopwatch.StartNew();
            var errors = new List<ErrorInfo>();
            var items = new JsonArray();
            var truncated = false;
            ErrorInfo terminating = null;
            var cancelled = false;
            try
            {
                var resolver = new ReferenceResolver(_runspace, _policy);
                var binder = new ParameterBinder(request.Secrets, _handles, resolver.Resolve);
                var bound = steps.Select(s => (s.Command, Params: binder.Bind(s.Command, s.Step))).ToList();

                SetPreference("ConfirmPreference", request.AllowStateChange ? "None" : "High");
                SetPreference("VerbosePreference", request.Verbose ? "Continue" : "SilentlyContinue");
                SetPreference("ProgressPreference", "Continue");
                SetPreference("ErrorActionPreference", "Continue");

                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = _runspace;
                    for (var i = 0; i < bound.Count; i++)
                    {
                        // Module-qualified names prevent another module's same-named command from being invoked.
                        ps.AddCommand(QualifiedName(bound[i].Command));
                        foreach (var p in bound[i].Params) ps.AddParameter(p.Key, p.Value);
                        if (i == bound.Count - 1 && request.WhatIf) ps.AddParameter("WhatIf", new SwitchParameter(true));
                    }

                    var id = request.Id;
                    ps.Streams.Error.DataAdded += (s, e) =>
                    {
                        var rec = ((PSDataCollection<ErrorRecord>)s)[e.Index];
                        var info = ErrorSerializer.FromErrorRecord(rec, scrubber, false);
                        lock (errors) errors.Add(info);
                        emit(new JsonObject { { "type", MessageTypes.ErrorRecord }, { "id", id }, { "error", info.ToJson() } });
                    };
                    ps.Streams.Warning.DataAdded += (s, e) => EmitStream(emit, id, "warning", ((PSDataCollection<WarningRecord>)s)[e.Index].Message, scrubber);
                    ps.Streams.Verbose.DataAdded += (s, e) => EmitStream(emit, id, "verbose", ((PSDataCollection<VerboseRecord>)s)[e.Index].Message, scrubber);
                    ps.Streams.Debug.DataAdded += (s, e) => EmitStream(emit, id, "debug", ((PSDataCollection<DebugRecord>)s)[e.Index].Message, scrubber);
                    ps.Streams.Information.DataAdded += (s, e) =>
                    {
                        var rec = ((PSDataCollection<InformationRecord>)s)[e.Index];
                        // Write-Host records are also delivered to the host UI; avoid duplicates.
                        if (rec.Tags != null && rec.Tags.Contains("PSHOST")) return;
                        EmitStream(emit, id, "information", Convert.ToString(rec.MessageData), scrubber);
                    };
                    ps.Streams.Progress.DataAdded += (s, e) =>
                    {
                        var p = ((PSDataCollection<ProgressRecord>)s)[e.Index];
                        emit(new JsonObject
                        {
                            { "type", MessageTypes.Progress }, { "id", id },
                            { "activity", scrubber.Scrub(p.Activity) }, { "status", scrubber.Scrub(p.StatusDescription) },
                            { "currentOperation", scrubber.Scrub(p.CurrentOperation) }, { "percent", p.PercentComplete },
                            { "activityId", p.ActivityId }, { "parentActivityId", p.ParentActivityId },
                            { "completed", p.RecordType == ProgressRecordType.Completed }
                        });
                    };

                    var output = new PSDataCollection<PSObject>();
                    var serializer = new ObjectSerializer(request.Output, _handles, scrubber);
                    output.DataAdded += (s, e) =>
                    {
                        var o = ((PSDataCollection<PSObject>)s)[e.Index];
                        if (items.Count >= request.Output.MaxItems)
                        {
                            truncated = true;
                            return;
                        }

                        items.Add(serializer.SerializeTopLevel(o));
                    };

                    var async = ps.BeginInvoke<PSObject, PSObject>(null, output);
                    var signaled = WaitHandle.WaitAny(new[] { async.AsyncWaitHandle, cts.Token.WaitHandle });
                    if (signaled == 1 && !async.IsCompleted)
                    {
                        cancelled = true;
                        var stop = ps.BeginStop(null, null);
                        if (!stop.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(_options.StopGraceSeconds)))
                        {
                            // A blocking SDK call ignored the stop request; the GUI must restart this worker.
                            IsUnhealthy = true;
                            terminating = ErrorInfo.FromMessage("The operation did not stop within the grace period. The PowerShell worker will be restarted.", "WorkerStopTimeout");
                        }
                    }

                    try
                    {
                        if (!IsUnhealthy) ps.EndInvoke(async);
                    }
                    catch (PipelineStoppedException)
                    {
                        cancelled = true;
                    }
                    catch (Exception ex)
                    {
                        terminating = ErrorSerializer.FromException(ex, scrubber, true);
                    }

                    if (ps.InvocationStateInfo.State == PSInvocationState.Failed && terminating == null && ps.InvocationStateInfo.Reason != null)
                        terminating = ErrorSerializer.FromException(ps.InvocationStateInfo.Reason, scrubber, true);
                }
            }
            catch (PolicyViolationException ex)
            {
                terminating = ErrorInfo.FromMessage(ex.Message, ex.GetType().FullName);
            }
            catch (Exception ex)
            {
                terminating = ErrorSerializer.FromException(ex, scrubber, true);
            }
            finally
            {
                _host?.Flush();
                _hostSink = null;
                _hostScrubber = SecretScrubber.None;
                lock (_currentLock)
                {
                    _currentRequestId = null;
                    _currentCts = null;
                }

                SafeSetPreference("ConfirmPreference", "High");
            }

            var timedOut = cancelled && timeoutCts.IsCancellationRequested && !userCts.IsCancellationRequested;
            if (timedOut && terminating == null)
                terminating = ErrorInfo.FromMessage($"The operation did not complete within {request.TimeoutSeconds} seconds and was stopped.", "System.TimeoutException");
            cts.Dispose();
            userCts.Dispose();
            timeoutCts.Dispose();
            var result = Result(request.Id, terminating == null && !cancelled, items);
            result.Set("hadErrors", errors.Count > 0);
            result.Set("errors", new JsonArray(errors.Select(e => (JsonNode)e.ToJson())));
            result.Set("terminatingError", terminating?.ToJson() ?? (JsonNode)JsonNull.Instance);
            result.Set("cancelled", cancelled && !timedOut);
            result.Set("timedOut", timedOut);
            result.Set("truncated", truncated);
            result.Set("whatIf", request.WhatIf);
            result.Set("durationMs", (long)sw.Elapsed.TotalMilliseconds);
            result.Set("workerUnhealthy", IsUnhealthy);
            emit(result);
        }

        public const string NotConnectedMessage =
            "Not connected to an XProtect management server. Connect first. (The application blocks MilestonePSTools' automatic connection so it never connects with a saved default profile or opens a login dialog in the background.)";

        private bool IsConnected()
        {
            CommandInfo test;
            try
            {
                test = _policy.Resolve("Test-VmsConnection");
            }
            catch (PolicyViolationException)
            {
                return true; // cannot check; the module's own behaviour applies
            }

            using (var ps = PowerShell.Create())
            {
                ps.Runspace = _runspace;
                ps.AddCommand(QualifiedName(test));
                var r = ps.Invoke().FirstOrDefault();
                return r?.BaseObject is bool b && b;
            }
        }

        private static void EmitStream(Action<JsonObject> emit, string id, string stream, string message, SecretScrubber scrubber)
        {
            emit(new JsonObject { { "type", MessageTypes.Stream }, { "id", id }, { "stream", stream }, { "message", scrubber.Scrub(message) } });
        }

        private string QualifiedName(CommandInfo cmd) => string.IsNullOrEmpty(cmd.ModuleName) ? cmd.Name : cmd.ModuleName + "\\" + cmd.Name;

        private void SetPreference(string name, string value)
        {
            _runspace.SessionStateProxy.SetVariable(name, Enum.Parse(name == "ConfirmPreference" ? typeof(ConfirmImpact) : typeof(ActionPreference), value));
        }

        private void SafeSetPreference(string name, string value)
        {
            try
            {
                if (_runspace?.RunspaceAvailability == RunspaceAvailability.Available) SetPreference(name, value);
            }
            catch (Exception)
            {
                // best effort
            }
        }

        internal static JsonObject Result(string id, bool success, JsonArray items) =>
            new JsonObject { { "type", MessageTypes.Result }, { "id", id }, { "success", success }, { "items", items } };

        internal static JsonObject Failed(string id, ErrorInfo error) =>
            new JsonObject { { "type", MessageTypes.Failed }, { "id", id }, { "error", error.ToJson() } };
    }

    /// <summary>
    /// Re-resolves {"$ref": {type, id}} values with the type's producer command. Only types whose producer accepts
    /// -Id (or -ItemPath for storage) are supported; this list is verified against the command catalog by tests.
    /// </summary>
    internal sealed class ReferenceResolver
    {
        public static readonly Dictionary<string, (string Command, string Parameter, bool Path)> Producers =
            new Dictionary<string, (string, string, bool)>(StringComparer.OrdinalIgnoreCase)
            {
                { "RecordingServer", ("Get-VmsRecordingServer", "Id", false) },
                { "Hardware", ("Get-VmsHardware", "Id", false) },
                { "Camera", ("Get-VmsCamera", "Id", false) },
                { "Microphone", ("Get-VmsMicrophone", "Id", false) },
                { "Speaker", ("Get-VmsSpeaker", "Id", false) },
                { "Metadata", ("Get-VmsMetadata", "Id", false) },
                { "InputEvent", ("Get-VmsInput", "Id", false) },
                { "Output", ("Get-VmsOutput", "Id", false) },
                { "Role", ("Get-VmsRole", "Id", false) },
                { "View", ("Get-VmsView", "Id", false) },
                { "ViewGroup", ("Get-VmsViewGroup", "Id", false) },
                { "ClientProfile", ("Get-VmsClientProfile", "Id", false) },
                { "FailoverGroup", ("Get-VmsFailoverGroup", "Id", false) },
                { "Rule", ("Get-VmsRule", "Id", false) },
                { "Storage", ("Get-VmsStorage", "ItemPath", true) }
            };

        private readonly Runspace _runspace;
        private readonly CommandPolicy _policy;
        private readonly Dictionary<string, object> _cache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public ReferenceResolver(Runspace runspace, CommandPolicy policy)
        {
            _runspace = runspace;
            _policy = policy;
        }

        public object Resolve(string type, string id)
        {
            var key = type + "|" + id;
            if (_cache.TryGetValue(key, out var cached)) return cached;
            if (!Producers.TryGetValue(type, out var p)) throw new PolicyViolationException($"Objects of type '{type}' cannot be referenced by Id. Refresh the data and try again.");
            object idValue = id;
            if (!p.Path)
            {
                if (!Guid.TryParse(id, out var g)) throw new PolicyViolationException($"'{id}' is not a valid {type} Id.");
                idValue = g;
            }

            var cmd = _policy.Resolve(p.Command);
            using (var ps = PowerShell.Create())
            {
                ps.Runspace = _runspace;
                ps.AddCommand(string.IsNullOrEmpty(cmd.ModuleName) ? cmd.Name : cmd.ModuleName + "\\" + cmd.Name).AddParameter(p.Parameter, idValue).AddParameter("ErrorAction", ActionPreference.Stop);
                var r = ps.Invoke();
                var obj = r.FirstOrDefault();
                if (obj == null) throw new PolicyViolationException($"{type} with Id '{id}' was not found. It may have been removed.");
                _cache[key] = obj;
                return obj;
            }
        }
    }
}
