using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MilestoneAdmin.Application.Automation;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.Application.Reports;
using MilestoneAdmin.Application.Services;
using MilestoneAdmin.Domain.Csv;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.UI.Mvvm;

namespace MilestoneAdmin.UI.ViewModels
{
    public sealed class MaintenanceViewModel : PageViewModel
    {
        private bool _problemsOnly = true;
        private List<DeviceStatusInfo> _all = new List<DeviceStatusInfo>();

        public MaintenanceViewModel(AppServices services)
            : base(services, "Maintenance")
        {
            StatusCommand = new AsyncCommand(LoadStatusAsync, () => IsConnected && !IsBusy && Services.Session.Catalog?.Contains("Get-VmsDeviceStatus") == true);
            EnvironmentCommand = new AsyncCommand(CheckEnvironmentAsync, () => !IsBusy && IsConnected);
            OpenLogsCommand = new RelayCommand(() => System.Diagnostics.Process.Start("explorer.exe", "\"" + Services.Paths.DiagnosticsDirectory + "\""));
        }

        public ObservableCollection<DeviceStatusInfo> Status { get; } = new ObservableCollection<DeviceStatusInfo>();
        public ObservableCollection<PrecheckItem> Checks { get; } = new ObservableCollection<PrecheckItem>();
        public string StatusSummary { get; private set; }

        public bool ProblemsOnly
        {
            get => _problemsOnly;
            set
            {
                if (Set(ref _problemsOnly, value)) Fill();
            }
        }

        public ICommand StatusCommand { get; }
        public ICommand EnvironmentCommand { get; }
        public ICommand OpenLogsCommand { get; }

        private async Task LoadStatusAsync()
        {
            await Busy("Asking recording servers for live device status...", async () =>
            {
                var r = await Services.Session.Gateway.GetDeviceStatusAsync(null, CancellationToken.None);
                if (!r.Success) Error = ErrorTranslator.Translate(r.Raw, new OperationContext("read device status", null, Services.Session.SystemName));
                _all = r.Items;
                StatusSummary = $"{_all.Count} devices · {_all.Count(s => s.HasProblem)} with problems · {_all.Count(s => s.Enabled == false)} disabled" +
                                (r.Raw.Errors.Count > 0 ? $" · {r.Raw.Errors.Count} recording server(s) did not answer" : string.Empty);
                Raise(nameof(StatusSummary));
                Fill();
            });
        }

        private void Fill()
        {
            Status.Clear();
            foreach (var s in _all.Where(s => !ProblemsOnly || s.HasProblem).OrderBy(s => s.RecorderName).ThenBy(s => s.DeviceName)) Status.Add(s);
        }

        private async Task CheckEnvironmentAsync()
        {
            await Busy("Checking the environment...", async () =>
            {
                Checks.Clear();
                var env = Services.Session.EnvironmentReport;
                if (Services.Session.Gateway != null) env = await Services.Session.Gateway.Executor.GetEnvironmentAsync(CancellationToken.None);
                foreach (var c in Services.Environment.CheckEnvironment(env, Services.Session.Catalog).Items) Checks.Add(c);
                foreach (var r in Services.Session.Inventory.RecordingServers) Checks.Add(await EnvironmentService.CheckRecorderAsync(r, CancellationToken.None));
            });
        }
    }

    public sealed class ReportItem
    {
        public ReportDefinition Definition { get; set; }
        public bool Available { get; set; }
        public string Reason { get; set; }
        public string Title => Definition.Title;
        public string Description => Available ? Definition.Description : Reason;
    }

    public sealed class ReportsViewModel : PageViewModel
    {
        private ReportItem _selected;
        private ReportResult _result;

        public ReportsViewModel(AppServices services)
            : base(services, "Reports")
        {
            RunCommand = new AsyncCommand(RunAsync, () => Selected?.Available == true && IsConnected && !IsBusy);
            ExportCsvCommand = new RelayCommand(() => Services.Dialogs.SaveText("CSV file (*.csv)|*.csv", Slug() + ".csv", Result.ToCsv()), () => Result != null && Result.Error == null);
            ExportJsonCommand = new RelayCommand(() => Services.Dialogs.SaveText("JSON file (*.json)|*.json", Slug() + ".json", Result.ToJson()), () => Result != null && Result.Error == null);
        }

        public ObservableCollection<ReportItem> Reports { get; } = new ObservableCollection<ReportItem>();
        public ReportItem Selected { get => _selected; set => Set(ref _selected, value); }
        public ReportResult Result { get => _result; private set => Set(ref _result, value); }
        public DataView Data { get; private set; }
        public ObservableCollection<string> Notes { get; } = new ObservableCollection<string>();
        public ICommand RunCommand { get; }
        public ICommand ExportCsvCommand { get; }
        public ICommand ExportJsonCommand { get; }

        private string Slug() => Result.Title.ToLowerInvariant().Replace(' ', '-').Replace("(", string.Empty).Replace(")", string.Empty);

        public override Task OnNavigatedToAsync()
        {
            Load();
            return Task.CompletedTask;
        }

        public override void OnSessionChanged()
        {
            base.OnSessionChanged();
            Load();
        }

        private void Load()
        {
            var keep = Selected?.Definition.Id;
            Reports.Clear();
            foreach (var d in ReportService.Definitions)
                Reports.Add(new ReportItem { Definition = d, Available = Services.Reports.IsAvailable(d), Reason = Services.Reports.UnavailableReason(d) ?? (IsConnected ? null : "Connect to run reports.") });
            Selected = Reports.FirstOrDefault(r => r.Definition.Id == keep) ?? Reports.FirstOrDefault();
        }

        private async Task RunAsync()
        {
            await Busy($"Running '{Selected.Title}'...", async () =>
            {
                var r = await Services.Reports.RunAsync(Selected.Definition, CancellationToken.None);
                Result = r;
                Error = r.Error;
                var t = new DataTable();
                foreach (var c in r.Columns) t.Columns.Add(c, typeof(string));
                foreach (var row in r.Rows) t.Rows.Add(row.Cast<object>().ToArray());
                Data = t.DefaultView;
                Raise(nameof(Data));
                Notes.Clear();
                Notes.Add($"Generated {r.GeneratedAt:g} from {r.System} · {r.Source} · {r.Rows.Count} row(s)");
                foreach (var n in r.Notes) Notes.Add(n);
            });
        }
    }

    public sealed class AutomationViewModel : PageViewModel
    {
        private JobDefinition _selected;
        private string _preview;

        public AutomationViewModel(AppServices services)
            : base(services, "Automation · Job Builder")
        {
            foreach (var j in Services.Jobs.LoadAll()) Jobs.Add(j);
            NewCommand = new RelayCommand(() =>
            {
                var j = new JobDefinition { Name = "Job " + (Jobs.Count + 1) };
                Jobs.Add(j);
                Selected = j;
            });
            DeleteCommand = new RelayCommand(() =>
            {
                if (Selected == null || !Services.Dialogs.Confirm("Delete job", $"Delete '{Selected.Name}'?")) return;
                Jobs.Remove(Selected);
                Save();
                Selected = Jobs.FirstOrDefault();
            }, () => Selected != null);
            SaveCommand = new RelayCommand(Save, () => Selected != null);
            AddFilterCommand = new RelayCommand(() =>
            {
                Selected.Filters.Add(new JobFilter { Property = "Name", Operator = FilterOperator.Contains, Value = string.Empty });
                RefreshFilters();
            }, () => Selected != null);
            RemoveFilterCommand = new RelayCommand(o =>
            {
                Selected.Filters.Remove(o as JobFilter);
                RefreshFilters();
            });
            PreviewCommand = new AsyncCommand(PreviewAsync, () => Selected != null && IsConnected && !IsBusy);
            RunCommand = new AsyncCommand(RunAsync, () => Selected != null && IsConnected && !IsBusy);
            Selected = Jobs.FirstOrDefault();
        }

        public ObservableCollection<JobDefinition> Jobs { get; } = new ObservableCollection<JobDefinition>();
        public ObservableCollection<JobFilter> Filters { get; } = new ObservableCollection<JobFilter>();
        public ObservableCollection<string> Steps { get; } = new ObservableCollection<string>();
        public Array Sources => Enum.GetValues(typeof(CameraSource));
        public Array Operators => Enum.GetValues(typeof(FilterOperator));
        public Array Actions => Enum.GetValues(typeof(JobActionKind));
        public string[] Properties => JobDefinition.FilterableProperties;
        public IEnumerable<string> SettableProperties => PSTools.Commands.PsCommands.CameraSettableProperties.Keys;

        public JobDefinition Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                RefreshFilters();
                Raise(nameof(Name));
                Raise(nameof(Source));
                Raise(nameof(SourceName));
                Raise(nameof(Action));
                Raise(nameof(ActionProperty));
                Raise(nameof(ActionValue));
            }
        }

        public string Name { get => Selected?.Name; set { if (Selected != null) { Selected.Name = value; Raise(); } } }
        public CameraSource Source { get => Selected?.Source ?? CameraSource.AllCameras; set { if (Selected != null) { Selected.Source = value; Raise(); RefreshSteps(); } } }
        public string SourceName { get => Selected?.SourceName; set { if (Selected != null) { Selected.SourceName = value; Raise(); RefreshSteps(); } } }
        public JobActionKind Action { get => Selected?.Action ?? JobActionKind.ExportInventory; set { if (Selected != null) { Selected.Action = value; Raise(); RefreshSteps(); } } }
        public string ActionProperty { get => Selected?.ActionProperty; set { if (Selected != null) { Selected.ActionProperty = value; Raise(); RefreshSteps(); } } }
        public string ActionValue { get => Selected?.ActionValue; set { if (Selected != null) { Selected.ActionValue = value; Raise(); RefreshSteps(); } } }
        public string Preview { get => _preview; set => Set(ref _preview, value); }

        public ICommand NewCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand AddFilterCommand { get; }
        public ICommand RemoveFilterCommand { get; }
        public ICommand PreviewCommand { get; }
        public ICommand RunCommand { get; }

        private void RefreshFilters()
        {
            Filters.Clear();
            if (Selected != null)
                foreach (var f in Selected.Filters) Filters.Add(f);
            RefreshSteps();
        }

        private void RefreshSteps()
        {
            Steps.Clear();
            if (Selected == null) return;
            var i = 1;
            foreach (var s in Selected.Describe()) Steps.Add($"{i++}. {s}");
        }

        private void Save()
        {
            Services.Jobs.SaveAll(Jobs);
            Services.Activity.Info($"Job '{Selected?.Name}' saved.");
        }

        private async Task<List<CameraInfo>> SelectCamerasAsync()
        {
            if (Services.Session.Inventory.LoadedAt == null) await Services.Inventory.RefreshAsync(CancellationToken.None);
            return Selected.Select(Services.Session.Inventory.Cameras);
        }

        private async Task PreviewAsync()
        {
            await Busy("Evaluating job...", async () =>
            {
                var cams = await SelectCamerasAsync();
                Preview = $"{cams.Count} camera(s) match: " + string.Join(", ", cams.Take(20).Select(c => c.Name)) + (cams.Count > 20 ? ", ..." : string.Empty);
            });
        }

        private async Task RunAsync()
        {
            var cams = await SelectCamerasAsync();
            if (cams.Count == 0)
            {
                Services.Dialogs.ShowInfo(Selected.Name, "No camera matches the job's source and filters.");
                return;
            }

            if (Selected.Action == JobActionKind.ExportInventory)
            {
                var csv = CsvParser.Write(new[] { "Name", "Enabled", "Hardware", "Address", "RecordingServer", "RecordingEnabled", "RecordingFramerate", "Storage", "Id" },
                    cams.Select(c => (IReadOnlyList<string>)new[] { c.Name, c.Enabled?.ToString(), c.HardwareName, c.HardwareAddress, c.RecordingServerName, c.RecordingEnabled?.ToString(), c.RecordingFramerate?.ToString(System.Globalization.CultureInfo.InvariantCulture), c.RecordingStorageName, c.Id.ToString() }));
                Services.Dialogs.SaveText("CSV file (*.csv)|*.csv", "job-camera-inventory.csv", csv);
                return;
            }

            object value;
            try
            {
                value = Selected.ConvertActionValue();
            }
            catch (FormatException ex)
            {
                Services.Dialogs.ShowInfo(Selected.Name, ex.Message);
                return;
            }

            var wf = Services.CameraEdits;
            var prop = Selected.ActionProperty;
            var plan = wf.BuildPlan($"{Selected.Name}: set {prop}", cams, c => new Dictionary<string, object> { { prop, value } });
            if (plan.ChangedItemCount == 0)
            {
                Services.Dialogs.ShowInfo(Selected.Name, "All matching cameras already have this value.");
                return;
            }

            Review = new ReviewViewModel(Services, plan, ct => wf.RunWhatIfAsync(plan, cams, ct), async (p, ct) => ReviewViewModel.Rows(await wf.ApplyAsync(plan, cams, p, ct)), () => Review = null);
        }
    }
}
