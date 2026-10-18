using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.PSTools.Execution
{
    public sealed class WorkerStartOptions
    {
        /// <summary>Full path of the worker executable (or of "dotnet" when running the test build).</summary>
        public string FileName { get; set; }

        /// <summary>Arguments placed before nothing else; only used for the test build ("path/MilestoneAdmin.Worker.dll").</summary>
        public string Arguments { get; set; }

        public string ModuleName { get; set; } = "MilestonePSTools";

        /// <summary>Optional explicit module path (side-by-side versions, tests).</summary>
        public string ModulePath { get; set; }

        public string RequiredVersion { get; set; }

        /// <summary>null = follow the module's own telemetry setting; true = disable for the worker process; false = enable.</summary>
        public bool? DisableTelemetry { get; set; }

        public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(90);

        /// <summary>Extra time after a cancel request before the worker is terminated.</summary>
        public TimeSpan CancelGrace { get; set; } = TimeSpan.FromSeconds(25);
    }

    public sealed class WorkerInfo
    {
        public string WorkerVersion { get; set; }
        public int ProcessId { get; set; }
        public string ModuleName { get; set; }
        public string ModuleVersion { get; set; }
        public string ModulePath { get; set; }
        public string PowerShellVersion { get; set; }
        public int CommandCount { get; set; }
        public List<string> Warnings { get; } = new List<string>();
        public List<string> HostOutput { get; } = new List<string>();
    }

    public sealed class WorkerExitedEventArgs : EventArgs
    {
        public WorkerExitedEventArgs(int? exitCode, bool expected)
        {
            ExitCode = exitCode;
            Expected = expected;
        }

        public int? ExitCode { get; }

        public bool Expected { get; }
    }

    public sealed class WorkerStartException : Exception
    {
        public WorkerStartException(string message, Exception inner = null)
            : base(message, inner)
        {
        }

        /// <summary>The raw worker error when the module itself failed to import.</summary>
        public Protocol.ErrorInfo ModuleError { get; set; }
    }

    /// <summary>
    /// Executes MilestonePSTools operations. The production implementation is <see cref="WorkerProcessExecutor"/>,
    /// which runs them in an isolated Windows PowerShell 5.1 worker process.
    /// </summary>
    public interface IPowerShellExecutor : IDisposable
    {
        bool IsRunning { get; }

        WorkerInfo Info { get; }

        event EventHandler<WorkerExitedEventArgs> Exited;

        /// <summary>Diagnostic lines from the worker (stderr and log events). Contain no secrets.</summary>
        event EventHandler<string> Diagnostic;

        Task<WorkerInfo> StartAsync(WorkerStartOptions options, CancellationToken cancellationToken);

        Task<JsonObject> GetEnvironmentAsync(CancellationToken cancellationToken);

        Task<JsonObject> DiscoverCatalogAsync(CancellationToken cancellationToken);

        Task<OperationResult> InvokeAsync(VmsOperation operation, IProgress<OperationEvent> progress, CancellationToken cancellationToken);

        Task ReleaseHandlesAsync(IEnumerable<string> handles);

        Task StopAsync();
    }

    /// <summary>Probes the environment without importing MilestonePSTools (used by the setup wizard before install).</summary>
    public interface IEnvironmentProbe
    {
        Task<JsonObject> ProbeAsync(WorkerStartOptions options, CancellationToken cancellationToken);
    }
}
