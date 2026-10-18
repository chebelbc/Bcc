using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Application.Abstractions;
using MilestoneAdmin.Application.Actions;
using MilestoneAdmin.Application.Activity;
using MilestoneAdmin.Application.Execution;
using MilestoneAdmin.Application.Reports;
using MilestoneAdmin.Application.Services;
using MilestoneAdmin.Application.Session;
using MilestoneAdmin.Application.Workflows;
using MilestoneAdmin.Infrastructure;
using MilestoneAdmin.PSTools;
using MilestoneAdmin.PSTools.Execution;

namespace MilestoneAdmin.Tests.EndToEnd
{
    /// <summary>
    /// Composes the real application services exactly as the UI does, but with the worker's net8.0 test build and the
    /// mock module. Every call crosses the real process boundary and JSON-lines protocol.
    /// </summary>
    internal sealed class AppHarness : IAsyncDisposable
    {
        private sealed class TestExecutorFactory : IExecutorFactory
        {
            public IPowerShellExecutor Create() => new WorkerProcessExecutor();

            public WorkerStartOptions CreateOptions(AppSettings settings) => new WorkerStartOptions
            {
                FileName = "dotnet",
                Arguments = "\"" + WorkerPaths.WorkerDll + "\"",
                ModulePath = TestPaths.MockModule,
                DisableTelemetry = true,
                StartupTimeout = TimeSpan.FromSeconds(120),
                CancelGrace = TimeSpan.FromSeconds(20)
            };
        }

        public AppHarness()
        {
            Root = Path.Combine(Path.GetTempPath(), "xaa-tests-" + Guid.NewGuid().ToString("N"));
            Paths = new AppPaths(Path.Combine(Root, "roaming"), Path.Combine(Root, "local"));
            Audit = new JsonLinesAuditStore(Paths);
            var clock = new SystemClock();
            var user = new EnvironmentUserContext();
            Scripts = new ScriptService(Session, clock);
            Runner = new OperationRunner(Session, Activity, Audit, user, clock, Scripts);
            Bulk = new BulkOperationRunner(Runner, Activity);
            Catalogs = new CatalogService(new FileCatalogCache(Paths), Activity, TestPaths.StaticCatalog);
            Connection = new ConnectionService(new TestExecutorFactory(), Session, Activity, Catalogs, Audit, user, clock);
            Inventory = new InventoryService(Session, Activity);
            Reports = new ReportService(Session, Inventory);
            Cameras = new CameraEditWorkflow(Session, Runner, Bulk, Scripts);
            Actions = new QuickActionService(Session, Runner, Bulk, Scripts);
            Sites = new SiteService(Session, Runner, Activity);
            Workbooks = new WorkbookImportWorkflow(Session, Runner, Scripts);
            Settings = new AppSettings { MipSdkEulaAccepted = true };
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public SessionContext Session { get; } = new SessionContext();
        public ActivityLog Activity { get; } = new ActivityLog();
        public JsonLinesAuditStore Audit { get; }
        public ScriptService Scripts { get; }
        public OperationRunner Runner { get; }
        public BulkOperationRunner Bulk { get; }
        public CatalogService Catalogs { get; }
        public ConnectionService Connection { get; }
        public InventoryService Inventory { get; }
        public ReportService Reports { get; }
        public CameraEditWorkflow Cameras { get; }
        public QuickActionService Actions { get; }
        public SiteService Sites { get; }
        public WorkbookImportWorkflow Workbooks { get; }
        public AppSettings Settings { get; }

        public static SecureString Secure(string s)
        {
            var ss = new SecureString();
            foreach (var c in s) ss.AppendChar(c);
            ss.MakeReadOnly();
            return ss;
        }

        public async Task<ConnectOutcome> ConnectAsync(string password = "Corr3ct-Horse", ConnectionAuthMode mode = ConnectionAuthMode.BasicUser, string host = "mgmt.example.local")
        {
            return await Connection.ConnectAsync(new ConnectionRequest
            {
                ServerAddress = new Uri("http://" + host),
                AuthMode = mode,
                UserName = "admin",
                Password = Secure(password)
            }, Settings, CancellationToken.None);
        }

        public async Task<AppHarness> ConnectedWithInventoryAsync()
        {
            var c = await ConnectAsync();
            if (!c.Success) throw new InvalidOperationException("Connect failed: " + c.Error?.ToReport());
            var inv = await Inventory.RefreshAsync(CancellationToken.None);
            if (!inv.Success) throw new InvalidOperationException("Inventory failed: " + inv.Error?.ToReport());
            return this;
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisconnectAsync();
            try
            {
                Directory.Delete(Root, true);
            }
            catch (Exception)
            {
                // best effort
            }
        }
    }
}
