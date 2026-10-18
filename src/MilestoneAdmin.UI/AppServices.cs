using System;
using System.IO;
using MilestoneAdmin.Application.Abstractions;
using MilestoneAdmin.Application.Activity;
using MilestoneAdmin.Application.Execution;
using MilestoneAdmin.Application.Navigation;
using MilestoneAdmin.Application.Reports;
using MilestoneAdmin.Application.Services;
using MilestoneAdmin.Application.Session;
using MilestoneAdmin.Application.Workflows;
using MilestoneAdmin.Infrastructure;
using MilestoneAdmin.UI.Services;

namespace MilestoneAdmin.UI
{
    /// <summary>Composition root: one instance of every service, created at start-up.</summary>
    public sealed class AppServices
    {
        public AppServices()
        {
            Paths = new AppPaths();
            Diagnostics = new DiagnosticLog(Paths);
            SettingsStore = new JsonSettingsStore(Paths);
            Settings = SettingsStore.Load();
            Profiles = new JsonProfileStore(Paths);
            Jobs = new JsonJobStore(Paths);
            Secrets = CredentialManagerSecretStore.IsSupported ? (ISecretStore)new CredentialManagerSecretStore() : new InMemorySecretStore();
            Audit = new JsonLinesAuditStore(Paths);
            Clock = new SystemClock();
            User = new EnvironmentUserContext();
            Scripts = new ScriptService(Session, Clock);
            Runner = new OperationRunner(Session, Activity, Audit, User, Clock, Scripts)
            {
                CaptureVerbose = Settings.CaptureVerbose,
                DefaultTimeoutSeconds = Settings.OperationTimeoutSeconds
            };
            Bulk = new BulkOperationRunner(Runner, Activity);
            ExecutorFactory = new WorkerExecutorFactory();
            StaticCatalogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "catalog", "milestonepstools-catalog.json");
            Catalogs = new CatalogService(new FileCatalogCache(Paths), Activity, StaticCatalogPath);
            Connection = new ConnectionService(ExecutorFactory, Session, Activity, Catalogs, Audit, User, Clock);
            Connection.Diagnostic += (s, e) => Diagnostics.Write(e);
            Inventory = new InventoryService(Session, Activity);
            Environment = new EnvironmentService();
            Reports = new ReportService(Session, Inventory);
            Search = new SearchService(Session);
            Dialogs = new DialogService();
            Activity.EntryAdded += (s, e) => Diagnostics.Write($"[activity {e.Level}] {e.Message}");
        }

        public AppPaths Paths { get; }
        public DiagnosticLog Diagnostics { get; }
        public JsonSettingsStore SettingsStore { get; }
        public AppSettings Settings { get; }
        public JsonProfileStore Profiles { get; }
        public JsonJobStore Jobs { get; }
        public ISecretStore Secrets { get; }
        public JsonLinesAuditStore Audit { get; }
        public IClock Clock { get; }
        public IUserContext User { get; }
        public SessionContext Session { get; } = new SessionContext();
        public ActivityLog Activity { get; } = new ActivityLog();
        public ScriptService Scripts { get; }
        public OperationRunner Runner { get; }
        public BulkOperationRunner Bulk { get; }
        public WorkerExecutorFactory ExecutorFactory { get; }
        public string StaticCatalogPath { get; }
        public CatalogService Catalogs { get; }
        public ConnectionService Connection { get; }
        public InventoryService Inventory { get; }
        public EnvironmentService Environment { get; }
        public ReportService Reports { get; }
        public SearchService Search { get; }
        public IDialogService Dialogs { get; }

        public CameraEditWorkflow CameraEdits => new CameraEditWorkflow(Session, Runner, Bulk, Scripts);

        public HardwareEditWorkflow HardwareEdits => new HardwareEditWorkflow(Session, Runner, Bulk, Scripts);

        public RemoveHardwareWorkflow HardwareRemoval => new RemoveHardwareWorkflow(Session, Runner, Bulk, Scripts);

        public void SaveSettings()
        {
            Runner.CaptureVerbose = Settings.CaptureVerbose;
            Runner.DefaultTimeoutSeconds = Settings.OperationTimeoutSeconds;
            SettingsStore.Save(Settings);
        }
    }
}
