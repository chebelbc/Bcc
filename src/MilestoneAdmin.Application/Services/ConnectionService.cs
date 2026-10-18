using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Application.Abstractions;
using MilestoneAdmin.Application.Activity;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.Application.Execution;
using MilestoneAdmin.Application.Session;
using MilestoneAdmin.Domain.Capabilities;
using MilestoneAdmin.Domain.Catalog;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Domain.Errors;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.Protocol.Json;
using MilestoneAdmin.PSTools;
using MilestoneAdmin.PSTools.Execution;

namespace MilestoneAdmin.Application.Services
{
    public interface IExecutorFactory
    {
        IPowerShellExecutor Create();

        WorkerStartOptions CreateOptions(AppSettings settings);
    }

    public sealed class ConnectOutcome
    {
        public bool Success { get; set; }
        public AdminError Error { get; set; }
        public ManagementServerInfo Server { get; set; }
    }

    /// <summary>Starts the worker, loads the catalog, connects, and keeps <see cref="SessionContext"/> current.</summary>
    public sealed class ConnectionService
    {
        private readonly IExecutorFactory _factory;
        private readonly SessionContext _session;
        private readonly ActivityLog _activity;
        private readonly CatalogService _catalogs;
        private readonly IAuditStore _audit;
        private readonly IUserContext _user;
        private readonly IClock _clock;
        private IPowerShellExecutor _executor;

        public ConnectionService(IExecutorFactory factory, SessionContext session, ActivityLog activity, CatalogService catalogs, IAuditStore audit, IUserContext user, IClock clock)
        {
            _factory = factory;
            _session = session;
            _activity = activity;
            _catalogs = catalogs;
            _audit = audit;
            _user = user;
            _clock = clock;
        }

        public event EventHandler<string> Diagnostic;

        /// <summary>Starts a worker (if needed) so the environment and catalog are available before connecting.</summary>
        public async Task<IPowerShellExecutor> EnsureWorkerAsync(AppSettings settings, CancellationToken ct)
        {
            if (_executor != null && _executor.IsRunning) return _executor;
            _executor?.Dispose();
            _executor = _factory.Create();
            _executor.Diagnostic += (s, e) => Diagnostic?.Invoke(this, e);
            _executor.Exited += OnWorkerExited;
            _activity.Info("Starting Windows PowerShell worker and loading MilestonePSTools...");
            var info = await _executor.StartAsync(_factory.CreateOptions(settings), ct).ConfigureAwait(false);
            _activity.Success($"MilestonePSTools {info.ModuleVersion} loaded ({info.CommandCount} commands, PowerShell {info.PowerShellVersion}).");
            foreach (var w in info.Warnings) _activity.Warning(w);

            _session.EnvironmentReport = await _executor.GetEnvironmentAsync(ct).ConfigureAwait(false);
            _session.Catalog = await _catalogs.GetCatalogAsync(_executor, info, ct).ConfigureAwait(false);
            _session.Capabilities = new CapabilityEvaluator().Evaluate(_session.Catalog);
            _session.NotifyChanged();
            return _executor;
        }

        public async Task<ConnectOutcome> ConnectAsync(ConnectionRequest request, AppSettings settings, CancellationToken ct)
        {
            var ctx = new OperationContext("connect to the management server", request.ServerAddress?.ToString());
            try
            {
                if (_session.IsConnected) await DisconnectAsync().ConfigureAwait(false);
                var executor = await EnsureWorkerAsync(settings, ct).ConfigureAwait(false);
                var gateway = new VmsGateway(executor);
                request.AcceptEula = settings.MipSdkEulaAccepted;
                var op = gateway.PlanConnect(request);
                _activity.Info($"Connecting to {request.ServerAddress}...");
                var started = _clock.Now;
                var result = await executor.InvokeAsync(op, null, ct).ConfigureAwait(false);
                foreach (var w in result.Warnings) _activity.Warning(w);
                if (!result.Success)
                {
                    var err = ErrorTranslator.Translate(result, ctx);
                    _activity.Error(err.Summary);
                    AuditConnect(request, false, err.Summary, started);
                    return new ConnectOutcome { Error = err };
                }

                var server = await gateway.GetManagementServerAsync(ct).ConfigureAwait(false);
                var account = request.AuthMode == ConnectionAuthMode.CurrentWindowsUser ? _user.WindowsUser : request.UserName;
                _session.SetConnected(gateway, request, server, account);
                _activity.Success($"Connected to {server?.Name} ({server?.ComputerName}), XProtect {server?.Version} as {account}.");
                AuditConnect(request, true, null, started);
                return new ConnectOutcome { Success = true, Server = server };
            }
            catch (Exception ex)
            {
                var err = ErrorTranslator.FromException(ex, ctx);
                _activity.Error(err.Summary);
                return new ConnectOutcome { Error = err };
            }
        }

        private void AuditConnect(ConnectionRequest request, bool success, string error, DateTimeOffset started)
        {
            try
            {
                _audit.Append(new Domain.Audit.AuditRecord
                {
                    Timestamp = started,
                    WindowsUser = _user.WindowsUser,
                    Workstation = _user.MachineName,
                    XProtectSystem = request.ServerAddress?.ToString(),
                    XProtectUser = request.AuthMode == ConnectionAuthMode.CurrentWindowsUser ? _user.WindowsUser : request.UserName,
                    Operation = "Connect",
                    TargetType = "ManagementServer",
                    Target = request.ServerAddress?.ToString(),
                    SanitizedCommand = $"Connect-Vms -ServerAddress '{request.ServerAddress}'" + (request.AuthMode == ConnectionAuthMode.CurrentWindowsUser ? string.Empty : " -Credential <prompt>") + (request.AuthMode == ConnectionAuthMode.BasicUser ? " -BasicUser" : string.Empty),
                    DurationMs = (long)(_clock.Now - started).TotalMilliseconds,
                    Result = success ? Domain.Audit.AuditResult.Success : Domain.Audit.AuditResult.Failed,
                    Error = error,
                    ModuleVersion = _session.ModuleVersion,
                    CorrelationId = Guid.NewGuid().ToString("N")
                });
            }
            catch (Exception ex)
            {
                _activity.Error("Audit record could not be written: " + ex.Message);
            }
        }

        public async Task DisconnectAsync()
        {
            if (_executor == null) return;
            if (_session.IsConnected)
            {
                try
                {
                    var op = new VmsOperation { Title = "Disconnect", Risk = RiskLevel.Session };
                    op.Pipeline.Add(PSTools.Commands.PsCommands.DisconnectVms());
                    await _executor.InvokeAsync(op, null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // the worker is stopped below anyway
                }

                _activity.Info($"Disconnected from {_session.SystemName}.");
            }

            _session.SetDisconnected();
            // A fresh worker per connection guarantees a clean MIP SDK state (process-global connection state).
            await _executor.StopAsync().ConfigureAwait(false);
            _executor.Dispose();
            _executor = null;
        }

        private void OnWorkerExited(object sender, WorkerExitedEventArgs e)
        {
            if (e.Expected) return;
            _activity.Error($"The PowerShell worker stopped unexpectedly (exit code {e.ExitCode?.ToString() ?? "unknown"}). Reconnect to continue.");
            _session.SetDisconnected();
        }
    }

    /// <summary>Provides the command catalog: runtime discovery (cached per module version) with the shipped static catalog as fallback.</summary>
    public sealed class CatalogService
    {
        private readonly ICatalogCache _cache;
        private readonly ActivityLog _activity;
        private readonly string _staticCatalogPath;

        public CatalogService(ICatalogCache cache, ActivityLog activity, string staticCatalogPath)
        {
            _cache = cache;
            _activity = activity;
            _staticCatalogPath = staticCatalogPath;
        }

        public CommandCatalog LoadStatic()
        {
            if (string.IsNullOrEmpty(_staticCatalogPath) || !File.Exists(_staticCatalogPath)) return null;
            return CommandCatalog.Load(_staticCatalogPath, ClassificationRules.Embedded);
        }

        public async Task<CommandCatalog> GetCatalogAsync(IPowerShellExecutor executor, WorkerInfo info, CancellationToken ct)
        {
            var cached = _cache?.TryLoad(info.ModuleVersion, info.ModulePath);
            if (cached != null)
            {
                _activity.Detail($"Using cached command catalog for MilestonePSTools {info.ModuleVersion}.");
                return CommandCatalog.FromJson(cached, ClassificationRules.Embedded);
            }

            try
            {
                _activity.Info("Discovering MilestonePSTools commands (first use of this module version)...");
                var json = await executor.DiscoverCatalogAsync(ct).ConfigureAwait(false);
                _cache?.Save(info.ModuleVersion, info.ModulePath, json);
                var catalog = CommandCatalog.FromJson(json, ClassificationRules.Embedded);
                _activity.Success($"Discovered {catalog.Commands.Count} commands.");
                return catalog;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _activity.Warning("Command discovery failed (" + ex.Message + "). Falling back to the catalog shipped with the application; " +
                                  "features are still checked against the commands actually exported by the installed module.");
                var fallback = LoadStatic();
                return fallback ?? new CommandCatalog(info.ModuleName, info.ModuleVersion, CatalogSourceKind.StaticRepository, Enumerable.Empty<CatalogCommand>());
            }
        }
    }
}
