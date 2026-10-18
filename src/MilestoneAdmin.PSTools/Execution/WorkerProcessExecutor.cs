using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.PSTools.Execution
{
    /// <summary>
    /// Runs MilestonePSTools in a child MilestoneAdmin.Worker process and talks to it over its redirected
    /// stdin/stdout using the JSON-lines protocol. See docs/adr/0001-process-architecture.md.
    /// </summary>
    public sealed class WorkerProcessExecutor : IPowerShellExecutor
    {
        private readonly ConcurrentDictionary<string, Pending> _pending = new ConcurrentDictionary<string, Pending>(StringComparer.Ordinal);
        private readonly object _writeLock = new object();
        private readonly UTF8Encoding _utf8 = new UTF8Encoding(false);
        private Process _process;
        private Stream _stdin;
        private WorkerStartOptions _options;
        private TaskCompletionSource<JsonObject> _ready;
        private volatile bool _stopping;
        private long _nextId;

        public bool IsRunning => _process != null && !_process.HasExited;

        public WorkerInfo Info { get; private set; }

        public event EventHandler<WorkerExitedEventArgs> Exited;

        public event EventHandler<string> Diagnostic;

        private sealed class Pending
        {
            public TaskCompletionSource<JsonObject> Completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            public IProgress<OperationEvent> Progress;
            public readonly List<string> WhatIf = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> Host = new List<string>();
        }

        public Task<WorkerInfo> StartAsync(WorkerStartOptions options, CancellationToken cancellationToken) => StartCoreAsync(options, initialize: true, cancellationToken);

        /// <summary>
        /// Starts the worker without importing MilestonePSTools and returns the environment report (PowerShell version,
        /// .NET release, language mode, execution policy, installed module versions). Used by the setup wizard.
        /// </summary>
        public static async Task<JsonObject> ProbeEnvironmentAsync(WorkerStartOptions options, CancellationToken cancellationToken)
        {
            using (var e = new WorkerProcessExecutor())
            {
                await e.StartCoreAsync(options, initialize: false, cancellationToken).ConfigureAwait(false);
                try
                {
                    return await e.GetEnvironmentAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await e.StopAsync().ConfigureAwait(false);
                }
            }
        }

        private async Task<WorkerInfo> StartCoreAsync(WorkerStartOptions options, bool initialize, CancellationToken cancellationToken)
        {
            if (IsRunning) throw new InvalidOperationException("The worker is already running.");
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.FileName)) throw new ArgumentException("Worker path is required.", nameof(options));
            if (Path.IsPathRooted(options.FileName) && !File.Exists(options.FileName))
                throw new WorkerStartException($"The PowerShell worker was not found at '{options.FileName}'. Reinstall the application.");

            var psi = new ProcessStartInfo
            {
                FileName = options.FileName,
                Arguments = options.Arguments ?? string.Empty,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = _utf8,
                StandardErrorEncoding = _utf8,
                WorkingDirectory = Path.IsPathRooted(options.FileName) ? Path.GetDirectoryName(options.FileName) : Environment.CurrentDirectory
            };
            _ready = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopping = false;
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.Exited += OnProcessExited;
            try
            {
                if (!p.Start()) throw new WorkerStartException("The PowerShell worker process could not be started.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new WorkerStartException($"The PowerShell worker could not be started: {ex.Message}", ex);
            }

            _process = p;
            _stdin = p.StandardInput.BaseStream;
            StartReader(p.StandardOutput);
            StartErrorReader(p.StandardError);

            var timeout = Task.Delay(options.StartupTimeout, cancellationToken);
            if (await Task.WhenAny(_ready.Task, timeout).ConfigureAwait(false) != _ready.Task)
            {
                Kill();
                cancellationToken.ThrowIfCancellationRequested();
                throw new WorkerStartException("The PowerShell worker did not start within the expected time.");
            }

            var ready = await _ready.Task.ConfigureAwait(false);
            if (!initialize)
            {
                Info = new WorkerInfo { WorkerVersion = ready.GetString("workerVersion"), ProcessId = (int)ready.GetInt64("processId") };
                return Info;
            }

            var init = new JsonObject
            {
                { "type", MessageTypes.Initialize },
                { "id", NextId() },
                { "moduleName", options.ModuleName },
                { "modulePath", options.ModulePath },
                { "requiredVersion", options.RequiredVersion },
                { "disableTelemetry", options.DisableTelemetry.HasValue ? (JsonNode)options.DisableTelemetry.Value : JsonNull.Instance }
            };
            var result = await SendAsync(init, null, options.StartupTimeout, cancellationToken).ConfigureAwait(false);
            if (result.GetString("type") == MessageTypes.Failed)
            {
                var err = ErrorInfo.FromJson(result.GetObject("error"));
                Kill();
                throw new WorkerStartException("MilestonePSTools could not be loaded: " + err?.Message) { ModuleError = err };
            }

            var i = result.GetArray("items")?[0]?.AsObject() ?? new JsonObject();
            Info = new WorkerInfo
            {
                WorkerVersion = ready.GetString("workerVersion"),
                ProcessId = (int)ready.GetInt64("processId"),
                ModuleName = i.GetString("moduleName"),
                ModuleVersion = i.GetString("moduleVersion"),
                ModulePath = i.GetString("modulePath"),
                PowerShellVersion = i.GetString("psVersion"),
                CommandCount = (int)i.GetInt64("commandCount")
            };
            Info.Warnings.AddRange((i.GetArray("warnings") ?? new JsonArray()).Strings());
            Info.HostOutput.AddRange((i.GetArray("hostOutput") ?? new JsonArray()).Strings());
            return Info;
        }

        public async Task<JsonObject> GetEnvironmentAsync(CancellationToken cancellationToken)
        {
            var r = await SendAsync(new JsonObject { { "type", MessageTypes.Environment }, { "id", NextId() } }, null, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            return Unwrap(r);
        }

        public async Task<JsonObject> DiscoverCatalogAsync(CancellationToken cancellationToken)
        {
            var r = await SendAsync(new JsonObject { { "type", MessageTypes.Discover }, { "id", NextId() } }, null, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
            return Unwrap(r);
        }

        private static JsonObject Unwrap(JsonObject r)
        {
            if (r.GetString("type") == MessageTypes.Failed) throw new InvalidOperationException(ErrorInfo.FromJson(r.GetObject("error"))?.Message);
            return r.GetArray("items")?[0]?.AsObject() ?? throw new InvalidOperationException("The worker returned no data.");
        }

        public async Task<OperationResult> InvokeAsync(VmsOperation operation, IProgress<OperationEvent> progress, CancellationToken cancellationToken)
        {
            if (!IsRunning) return OperationResult.FromFailure(ErrorInfo.FromMessage("The PowerShell worker is not running. Reconnect to the management server.", "WorkerNotRunning"));
            var id = NextId();
            var request = operation.ToRequest(id).ToJson();
            var pending = new Pending { Progress = progress };
            var wait = operation.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(operation.TimeoutSeconds) + _options.CancelGrace : Timeout.InfiniteTimeSpan;
            JsonObject response;
            try
            {
                response = await SendAsync(request, pending, wait, cancellationToken, cancelOnToken: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new OperationResult { Success = false, Cancelled = true, TerminatingError = ErrorInfo.FromMessage("The operation was cancelled.", "OperationCanceled") };
            }
            catch (WorkerFailedException ex)
            {
                return OperationResult.FromFailure(ErrorInfo.FromMessage(ex.Message, "WorkerFailed"));
            }

            if (response.GetString("type") == MessageTypes.Failed)
            {
                var r0 = OperationResult.FromFailure(ErrorInfo.FromJson(response.GetObject("error")));
                return r0;
            }

            var result = OperationResult.FromJson(response);
            result.WhatIfLines.AddRange(pending.WhatIf);
            result.Warnings.AddRange(pending.Warnings);
            result.HostLines.AddRange(pending.Host);
            if (result.WorkerUnhealthy) Kill();
            return result;
        }

        public Task ReleaseHandlesAsync(IEnumerable<string> handles)
        {
            var list = handles?.Where(h => !string.IsNullOrEmpty(h)).Distinct().ToList();
            if (list == null || list.Count == 0 || !IsRunning) return Task.CompletedTask;
            return SendAsync(new JsonObject { { "type", MessageTypes.ReleaseHandles }, { "id", NextId() }, { "handles", new JsonArray(list.Select(h => (JsonNode)h)) } },
                null, TimeSpan.FromSeconds(30), CancellationToken.None);
        }

        public async Task StopAsync()
        {
            if (!IsRunning) return;
            _stopping = true;
            try
            {
                Write(new JsonObject { { "type", MessageTypes.Shutdown }, { "id", NextId() } });
                _stdin.Dispose();
            }
            catch (Exception)
            {
                // the process may already be gone
            }

            var p = _process;
            await Task.Run(() =>
            {
                if (!p.WaitForExit(10000)) Kill();
            }).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _stopping = true;
            try
            {
                if (IsRunning)
                {
                    Write(new JsonObject { { "type", MessageTypes.Shutdown }, { "id", NextId() } });
                    _stdin.Dispose();
                    if (!_process.WaitForExit(5000)) Kill();
                }
            }
            catch (Exception)
            {
                Kill();
            }

            _process?.Dispose();
        }

        private string NextId() => "r" + Interlocked.Increment(ref _nextId);

        private async Task<JsonObject> SendAsync(JsonObject request, Pending pending, TimeSpan timeout, CancellationToken token, bool cancelOnToken = false)
        {
            var id = request.GetString("id");
            pending ??= new Pending();
            _pending[id] = pending;
            CancellationTokenRegistration reg = default;
            try
            {
                Write(request);
                if (cancelOnToken)
                {
                    reg = token.Register(() =>
                    {
                        try
                        {
                            Write(new JsonObject { { "type", MessageTypes.Cancel }, { "id", NextId() }, { "target", id } });
                        }
                        catch (Exception)
                        {
                            // worker gone; the exit handler fails the request
                        }

                        // If the worker cannot stop the pipeline within the grace period, terminate it.
                        Task.Delay(_options?.CancelGrace ?? TimeSpan.FromSeconds(25)).ContinueWith(_ =>
                        {
                            if (_pending.ContainsKey(id))
                            {
                                Diagnostic?.Invoke(this, "Operation did not stop after cancellation; terminating the PowerShell worker.");
                                Kill();
                            }
                        }, TaskScheduler.Default);
                    });
                }
                else if (token.CanBeCanceled)
                {
                    reg = token.Register(() => pending.Completion.TrySetCanceled());
                }

                var completed = timeout == Timeout.InfiniteTimeSpan
                    ? await pending.Completion.Task.ConfigureAwait(false)
                    : await WithTimeout(pending.Completion.Task, timeout).ConfigureAwait(false);
                return completed;
            }
            finally
            {
                reg.Dispose();
                _pending.TryRemove(id, out _);
            }
        }

        private async Task<JsonObject> WithTimeout(Task<JsonObject> task, TimeSpan timeout)
        {
            if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) == task) return await task.ConfigureAwait(false);
            Diagnostic?.Invoke(this, "The PowerShell worker did not answer in time; terminating it.");
            Kill();
            throw new WorkerFailedException("The PowerShell worker did not respond in time and was stopped. Reconnect to continue.");
        }

        private void Write(JsonObject message)
        {
            var bytes = _utf8.GetBytes(message.ToJson() + "\n");
            lock (_writeLock)
            {
                _stdin.Write(bytes, 0, bytes.Length);
                _stdin.Flush();
            }
        }

        private void StartReader(StreamReader reader)
        {
            var t = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        JsonObject msg;
                        try
                        {
                            msg = JsonNode.Parse(line).AsObject();
                        }
                        catch (Exception)
                        {
                            Diagnostic?.Invoke(this, "Ignored malformed output from worker.");
                            continue;
                        }

                        if (msg != null) Dispatch(msg);
                    }
                }
                catch (Exception ex)
                {
                    Diagnostic?.Invoke(this, "Worker output reader stopped: " + ex.Message);
                }
            })
            { IsBackground = true, Name = "Worker stdout reader" };
            t.Start();
        }

        private void StartErrorReader(StreamReader reader)
        {
            var t = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length > 0) Diagnostic?.Invoke(this, "[worker] " + line);
                    }
                }
                catch (Exception)
                {
                    // process ended
                }
            })
            { IsBackground = true, Name = "Worker stderr reader" };
            t.Start();
        }

        private void Dispatch(JsonObject msg)
        {
            var type = msg.GetString("type");
            if (type == MessageTypes.Ready)
            {
                _ready?.TrySetResult(msg);
                return;
            }

            if (type == MessageTypes.Log)
            {
                Diagnostic?.Invoke(this, $"[worker {msg.GetString("level")}] {msg.GetString("message")}");
                return;
            }

            var id = msg.GetString("id");
            if (id == null || !_pending.TryGetValue(id, out var p))
            {
                if (type == MessageTypes.Failed) Diagnostic?.Invoke(this, "Worker reported: " + msg.GetObject("error")?.GetString("message"));
                return;
            }

            switch (type)
            {
                case MessageTypes.Result:
                case MessageTypes.Failed:
                    p.Completion.TrySetResult(msg);
                    break;
                case MessageTypes.Progress:
                    p.Progress?.Report(new OperationEvent
                    {
                        Kind = OperationEventKind.Progress,
                        Activity = msg.GetString("activity"),
                        Message = msg.GetString("status"),
                        Percent = (int)msg.GetInt64("percent", -1),
                        Completed = msg.GetBool("completed")
                    });
                    break;
                case MessageTypes.ErrorRecord:
                {
                    var err = ErrorInfo.FromJson(msg.GetObject("error"));
                    p.Progress?.Report(new OperationEvent { Kind = OperationEventKind.Error, Message = err?.Message, Error = err });
                    break;
                }

                case MessageTypes.Stream:
                {
                    var stream = msg.GetString("stream");
                    var text = msg.GetString("message");
                    OperationEventKind kind;
                    switch (stream)
                    {
                        case "warning":
                            kind = OperationEventKind.Warning;
                            lock (p.Warnings) p.Warnings.Add(text);
                            break;
                        case "verbose": kind = OperationEventKind.Verbose; break;
                        case "debug": kind = OperationEventKind.Debug; break;
                        case "information": kind = OperationEventKind.Information; break;
                        case "whatif":
                            kind = OperationEventKind.WhatIf;
                            lock (p.WhatIf) p.WhatIf.Add(text);
                            break;
                        default:
                            kind = OperationEventKind.Host;
                            lock (p.Host) p.Host.Add(text);
                            break;
                    }

                    p.Progress?.Report(new OperationEvent { Kind = kind, Message = text });
                    break;
                }
            }
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            int? code = null;
            try
            {
                code = ((Process)sender).ExitCode;
            }
            catch (Exception)
            {
                // unavailable
            }

            var expected = _stopping;
            foreach (var kv in _pending.ToArray())
            {
                kv.Value.Completion.TrySetException(new WorkerFailedException(
                    $"The PowerShell worker stopped unexpectedly (exit code {code?.ToString() ?? "unknown"}). The operation's outcome is unknown; refresh the data before retrying."));
            }

            _ready?.TrySetException(new WorkerStartException($"The PowerShell worker exited during start-up (exit code {code?.ToString() ?? "unknown"})."));
            Exited?.Invoke(this, new WorkerExitedEventArgs(code, expected));
        }

        private void Kill()
        {
            try
            {
                if (_process != null && !_process.HasExited) _process.Kill();
            }
            catch (Exception)
            {
                // already exited
            }
        }
    }

    public sealed class WorkerFailedException : Exception
    {
        public WorkerFailedException(string message)
            : base(message)
        {
        }
    }
}
