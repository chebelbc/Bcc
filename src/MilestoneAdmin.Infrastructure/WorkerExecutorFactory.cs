using System;
using System.IO;
using MilestoneAdmin.Application.Abstractions;
using MilestoneAdmin.Application.Services;
using MilestoneAdmin.PSTools.Execution;

namespace MilestoneAdmin.Infrastructure
{
    /// <summary>Creates worker processes from the application's own installation folder (never from PATH).</summary>
    public sealed class WorkerExecutorFactory : IExecutorFactory
    {
        private readonly string _workerPath;
        private readonly string _arguments;

        public WorkerExecutorFactory(string workerPath = null, string arguments = null)
        {
            _workerPath = workerPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "worker", "MilestoneAdmin.Worker.exe");
            _arguments = arguments;
        }

        public string WorkerPath => _workerPath;

        public IPowerShellExecutor Create() => new WorkerProcessExecutor();

        public WorkerStartOptions CreateOptions(AppSettings settings) => new WorkerStartOptions
        {
            FileName = _workerPath,
            Arguments = _arguments,
            ModulePath = string.IsNullOrWhiteSpace(settings.ModulePath) ? null : settings.ModulePath,
            DisableTelemetry = settings.Telemetry == TelemetryChoice.DisableForApplication ? true : (bool?)null
        };
    }
}
