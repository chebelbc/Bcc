using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.Application.Execution;
using MilestoneAdmin.Application.Services;
using MilestoneAdmin.Application.Workflows;
using MilestoneAdmin.Domain.Capabilities;
using MilestoneAdmin.Domain.Csv;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.PSTools;
using MilestoneAdmin.PSTools.Execution;
using MilestoneAdmin.PSTools.Commands;
using MilestoneAdmin.UI.Mvvm;

namespace MilestoneAdmin.UI.ViewModels
{
    public sealed class SystemViewModel : PageViewModel
    {
        private GenericItem _site;
        private string _siteStatus;

        public SystemViewModel(AppServices services)
            : base(services, "System")
        {
            RefreshCommand = new AsyncCommand(RefreshAsync, () => IsConnected && !IsBusy);
            LoadSitesCommand = new AsyncCommand(LoadSitesAsync, () => IsConnected && !IsBusy && Services.Sites.IsAvailable);
            SelectSiteCommand = new AsyncCommand(SelectSiteAsync, () => IsConnected && !IsBusy && SelectedSite != null && SelectedSite.Name != Services.Session.SelectedSite);
        }

        public ICommand RefreshCommand { get; }
        public ICommand LoadSitesCommand { get; }
        public ICommand SelectSiteCommand { get; }

        /// <summary>Sites of a Milestone Federated Architecture (Get-VmsSite -ListAvailable).</summary>
        public ObservableCollection<GenericItem> Sites { get; } = new ObservableCollection<GenericItem>();
        public GenericItem SelectedSite { get => _site; set => Set(ref _site, value); }
        public string SiteStatus { get => _siteStatus; set => Set(ref _siteStatus, value); }

        private async Task LoadSitesAsync()
        {
            await Busy("Reading sites...", async () =>
            {
                var list = await Services.Sites.LoadAsync(CancellationToken.None);
                Error = list.Error;
                Sites.Clear();
                foreach (var site in list.Sites) Sites.Add(site);
                SelectedSite = Sites.FirstOrDefault(x => x.Name == (Services.Session.SelectedSite ?? list.Selected));
                SiteStatus = list.Error != null ? null
                    : list.IsMultiSite ? $"{list.Sites.Count} sites available. Commands target: {list.Selected}."
                    : "Only one site is available (connect with 'Include child sites' to see the sites of a Milestone Federated Architecture).";
            });
        }

        private async Task SelectSiteAsync()
        {
            var site = SelectedSite;
            if (!Services.Dialogs.Confirm("Switch site",
                    $"All pages, reports and changes will target the site '{site.Name}' until you switch again. The loaded inventory is cleared and read again from that site. Continue?")) return;
            await Busy($"Selecting site '{site.Name}'...", async () =>
            {
                Error = await Services.Sites.SelectAsync(site, CancellationToken.None);
                if (Error == null)
                {
                    SiteStatus = $"Commands target: {site.Name}.";
                    Error = (await Services.Inventory.RefreshAsync(CancellationToken.None)).Error;
                }
            });
            Load();
        }
        public ObservableCollection<KeyValuePair<string, string>> Facts { get; } = new ObservableCollection<KeyValuePair<string, string>>();
        public ObservableCollection<CapabilityStatus> Capabilities { get; } = new ObservableCollection<CapabilityStatus>();
        public DataView License { get; private set; }

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
            Facts.Clear();
            var s = Services.Session;
            void Add(string k, string v) => Facts.Add(new KeyValuePair<string, string>(k, string.IsNullOrEmpty(v) ? "—" : v));
            Add("Connected", s.IsConnected ? "Yes" : "No");
            Add("Server address", s.Connection?.ServerAddress?.ToString());
            Add("Management server", s.ManagementServer?.Name);
            Add("Selected site", s.SelectedSite ?? s.ManagementServer?.Name);
            Add("Computer name", s.ManagementServer?.ComputerName);
            Add("XProtect version", s.ManagementServer?.Version == null ? null : $"{s.ManagementServer.Version} ({VmsVersion.ToProductName(s.ManagementServer.Version)})");
            Add("Signed in as", s.ConnectedAccount);
            Add("MilestonePSTools", s.ModuleVersion);
            Add("Worker PowerShell", s.Gateway?.Executor.Info?.PowerShellVersion);
            Add("Command catalog", s.Catalog == null ? null : $"{s.Catalog.Commands.Count} commands, {s.Catalog.Source}");
            Capabilities.Clear();
            var caps = s.Capabilities.Count > 0 ? s.Capabilities : new CapabilityEvaluator().Evaluate(s.Catalog);
            foreach (var c in caps) Capabilities.Add(c);
        }

        private async Task RefreshAsync()
        {
            Load();
            if (Services.Session.Catalog?.Contains("Get-VmsLicenseOverview") != true) return;
            await Busy("Reading license overview...", async () =>
            {
                var q = new GenericQuery { Title = "Read license overview", ObjectType = "License", Step = PsCommands.GetVmsLicenseOverview };
                var r = await Services.Session.Gateway.QueryAsync(q, CancellationToken.None);
                if (!r.Success) Error = ErrorTranslator.Translate(r.Raw, new OperationContext("read the license overview"));
                License = GenericTable.ToDataView(r.Items);
                Raise(nameof(License));
            });
        }
    }

    public static class GenericTable
    {
        public static DataView ToDataView(IEnumerable<GenericItem> items)
        {
            var list = items.ToList();
            var t = new DataTable();
            var cols = list.SelectMany(i => i.Properties.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var c in cols) t.Columns.Add(c, typeof(string));
            foreach (var i in list) t.Rows.Add(cols.Select(c => Convert.ToString(i[c], CultureInfo.CurrentCulture)).Cast<object>().ToArray());
            return t.DefaultView;
        }
    }

    public sealed class RecordingServersViewModel : PageViewModel
    {
        private readonly ShellViewModel _shell;
        private RecordingServerInfo _selected;

        public RecordingServersViewModel(AppServices services, ShellViewModel shell)
            : base(services, "Recording Servers")
        {
            _shell = shell;
            RefreshCommand = new AsyncCommand(RefreshAsync, () => IsConnected && !IsBusy);
            CheckCommand = new AsyncCommand(CheckAsync, () => Selected != null && !IsBusy);
            AddHardwareCommand = new RelayCommand(() =>
            {
                var page = _shell.Page<AddHardwareViewModel>("AddHardware");
                page.PreselectRecorder(Selected);
            }, () => Selected != null && _shell.Capability("hardware.add").IsAvailable);
        }

        public ObservableCollection<RecordingServerInfo> Items { get; } = new ObservableCollection<RecordingServerInfo>();
        public ObservableCollection<PrecheckItem> Checks { get; } = new ObservableCollection<PrecheckItem>();
        public RecordingServerInfo Selected { get => _selected; set => Set(ref _selected, value); }
        public ICommand RefreshCommand { get; }
        public ICommand CheckCommand { get; }
        public ICommand AddHardwareCommand { get; }

        public override async Task OnNavigatedToAsync()
        {
            if (IsConnected && Services.Session.Inventory.LoadedAt == null) await RefreshAsync();
            else Load();
        }

        public override void OnSessionChanged()
        {
            base.OnSessionChanged();
            Load();
        }

        private void Load()
        {
            Items.Clear();
            foreach (var r in Services.Session.Inventory.RecordingServers) Items.Add(r);
        }

        private async Task RefreshAsync()
        {
            await Busy("Retrieving recording servers, hardware and cameras...", async () =>
            {
                var r = await Services.Inventory.RefreshAsync(CancellationToken.None);
                Error = r.Error;
            });
            Load();
        }

        private async Task CheckAsync()
        {
            var rec = Selected;
            await Busy($"Checking {rec.Name}...", async () =>
            {
                Checks.Clear();
                var report = await Services.Environment.CheckOperationAsync(Services.Session, new[] { "Get-VmsRecordingServer" }, new[] { rec }, CancellationToken.None);
                foreach (var c in report.Items) Checks.Add(c);
            });
        }
    }

    public sealed class HardwareViewModel : PageViewModel
    {
        private readonly ShellViewModel _shell;
        private string _filter;
        private string _recorderFilter = AllRecorders;
        public const string AllRecorders = "(all recording servers)";

        public HardwareViewModel(AppServices services, ShellViewModel shell)
            : base(services, "Hardware")
        {
            _shell = shell;
            View = CollectionViewSource.GetDefaultView(Items);
            View.Filter = o => Matches((HardwareInfo)o);
            RefreshCommand = new AsyncCommand(RefreshAsync, () => IsConnected && !IsBusy);
            AddCommand = new RelayCommand(() => _shell.Navigate("AddHardware"), () => IsConnected && _shell.Capability("hardware.add").IsAvailable);
            CsvCommand = new RelayCommand(() => _shell.Navigate("CsvImport"), () => IsConnected && _shell.Capability("hardware.csv").IsAvailable);
            EnableCommand = new RelayCommand(() => ReviewEnable(true), () => SelectedItems.Count > 0 && _shell.Capability("hardware.update").IsAvailable);
            DisableCommand = new RelayCommand(() => ReviewEnable(false), () => SelectedItems.Count > 0 && _shell.Capability("hardware.update").IsAvailable);
            RemoveCommand = new RelayCommand(ReviewRemove, () => SelectedItems.Count > 0 && _shell.Capability("hardware.remove").IsAvailable);
            RevealPasswordCommand = new AsyncCommand(RevealPasswordAsync, () => ExpertMode && SelectedItems.Count == 1 && Services.Session.Catalog?.Contains("Get-VmsHardwarePassword") == true);
            ExportCommand = new RelayCommand(Export, () => Items.Count > 0);
            QuickActions = new QuickActionsViewModel(services, this, "Hardware", () => SelectedItems, RefreshAsync);
        }

        /// <summary>Edit hardware (name, description, address) and update device credentials.</summary>
        public QuickActionsViewModel QuickActions { get; }

        public ObservableCollection<HardwareInfo> Items { get; } = new ObservableCollection<HardwareInfo>();
        public ICollectionView View { get; }
        public List<HardwareInfo> SelectedItems { get; } = new List<HardwareInfo>();
        public ObservableCollection<string> Recorders { get; } = new ObservableCollection<string>();

        public string Filter
        {
            get => _filter;
            set
            {
                if (Set(ref _filter, value)) View.Refresh();
            }
        }

        public string RecorderFilter
        {
            get => _recorderFilter;
            set
            {
                if (Set(ref _recorderFilter, value)) View.Refresh();
            }
        }

        public string Summary => $"{Items.Count} hardware, {Items.Count(i => i.Enabled == false)} disabled";

        public ICommand RefreshCommand { get; }
        public ICommand AddCommand { get; }
        public ICommand CsvCommand { get; }
        public ICommand EnableCommand { get; }
        public ICommand DisableCommand { get; }
        public ICommand RemoveCommand { get; }
        public ICommand RevealPasswordCommand { get; }
        public ICommand ExportCommand { get; }

        private bool Matches(HardwareInfo h) =>
            (RecorderFilter == AllRecorders || RecorderFilter == null || h.RecordingServerName == RecorderFilter) &&
            (string.IsNullOrWhiteSpace(Filter) || (h.Name ?? string.Empty).IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0 || (h.Address ?? string.Empty).IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0 || (h.Model ?? string.Empty).IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0);

        public void SetSelection(IList items)
        {
            SelectedItems.Clear();
            SelectedItems.AddRange(items.OfType<HardwareInfo>());
            QuickActions.Refresh();
            CommandManager.InvalidateRequerySuggested();
        }

        public override async Task OnNavigatedToAsync()
        {
            if (IsConnected && Services.Session.Inventory.LoadedAt == null) await RefreshAsync();
            else Load();
        }

        public override void OnSessionChanged()
        {
            base.OnSessionChanged();
            Load();
            QuickActions.Refresh();
        }

        private void Load()
        {
            Items.Clear();
            foreach (var h in Services.Session.Inventory.Hardware.OrderBy(h => h.RecordingServerName).ThenBy(h => h.Name)) Items.Add(h);
            Recorders.Clear();
            Recorders.Add(AllRecorders);
            foreach (var r in Services.Session.Inventory.RecordingServers) Recorders.Add(r.Name);
            Raise(nameof(Summary));
        }

        private async Task RefreshAsync()
        {
            await Busy("Retrieving hardware...", async () =>
            {
                var r = await Services.Inventory.RefreshAsync(CancellationToken.None);
                Error = r.Error;
            });
            Load();
        }

        private void ReviewEnable(bool enable)
        {
            var items = SelectedItems.ToList();
            var wf = Services.HardwareEdits;
            var plan = wf.BuildEnablePlan(items, enable);
            Review = new ReviewViewModel(Services, plan,
                ct => wf.RunEnableWhatIfAsync(plan, items, enable, ct),
                async (p, ct) => ReviewViewModel.Rows(await wf.ApplyEnableAsync(items, enable, p, ct)),
                () =>
                {
                    Review = null;
                    View.Refresh();
                    Raise(nameof(Summary));
                });
        }

        private void ReviewRemove()
        {
            var items = SelectedItems.ToList();
            var wf = Services.HardwareRemoval;
            var plan = wf.BuildPlan(items);
            Review = new ReviewViewModel(Services, plan,
                ct => wf.RunWhatIfAsync(plan, items, ct),
                async (p, ct) => ReviewViewModel.Rows(await wf.ApplyAsync(items, p, ct)),
                async () =>
                {
                    Review = null;
                    await RefreshAsync();
                });
        }

        private async Task RevealPasswordAsync()
        {
            var hw = SelectedItems[0];
            if (!Services.Dialogs.ConfirmStrong("Reveal hardware password",
                    $"This shows the password XProtect stores for '{hw.Name}'. The action is recorded in the audit log (without the password). Make sure nobody else can see your screen.",
                    hw.Name)) return;
            var outcome = await Services.Runner.RunAsync(Services.Session.Gateway.PlanRevealHardwarePassword(hw), new OperationContext("reveal the hardware password", hw.Name, hw.RecordingServerName));
            if (!outcome.Success)
            {
                Error = outcome.Error;
                return;
            }

            var pwd = outcome.Result.Items.FirstOrDefault()?.AsString() ?? string.Empty;
            Services.Dialogs.ShowInfo($"Password for {hw.Name}", $"User name: {hw.UserName}\nPassword: {pwd}");
        }

        private void Export()
        {
            var csv = CsvParser.Write(new[] { "Name", "Address", "Model", "Enabled", "RecordingServer", "Cameras", "UserName", "Id" },
                Items.Select(h => (IReadOnlyList<string>)new[] { h.Name, h.Address, h.Model, h.Enabled?.ToString(), h.RecordingServerName, h.CameraCount.ToString(CultureInfo.InvariantCulture), h.UserName, h.Id.ToString() }));
            Services.Dialogs.SaveText("CSV file (*.csv)|*.csv", "hardware-inventory.csv", csv);
        }
    }

    public enum CameraEditKind
    {
        Enable,
        Disable,
        RenamePattern,
        RenameCsv,
        Recording,
        Storage
    }

    public sealed class CamerasViewModel : PageViewModel
    {
        private string _filter;
        private string _recorderFilter = HardwareViewModel.AllRecorders;
        private string _stateFilter = "All";
        private bool _editorOpen;
        private CameraEditKind _editKind;
        private string _namePattern = "{Hardware} - Camera {Channel1}";
        private bool? _recordingEnabled;
        private string _framerate;
        private bool? _prebufferEnabled;
        private string _prebufferSeconds;
        private string _storageName;
        private string _renameCsvPath;
        private string _editorMessage;

        public CamerasViewModel(AppServices services)
            : base(services, "Cameras & Devices")
        {
            View = CollectionViewSource.GetDefaultView(Items);
            View.Filter = o => Matches((CameraInfo)o);
            RefreshCommand = new AsyncCommand(RefreshAsync, () => IsConnected && !IsBusy);
            EnableCommand = new RelayCommand(() => Start(CameraEditKind.Enable), CanEdit);
            DisableCommand = new RelayCommand(() => Start(CameraEditKind.Disable), CanEdit);
            RenameCommand = new RelayCommand(() => OpenEditor(CameraEditKind.RenamePattern), CanEdit);
            RenameCsvCommand = new RelayCommand(() => OpenEditor(CameraEditKind.RenameCsv), () => IsConnected && Capable("cameras.update"));
            RecordingCommand = new RelayCommand(() => OpenEditor(CameraEditKind.Recording), CanEdit);
            StorageCommand = new RelayCommand(() => OpenEditor(CameraEditKind.Storage), () => CanEdit() && Capable("cameras.storage"));
            BrowseCsvCommand = new RelayCommand(() => RenameCsvPath = Services.Dialogs.OpenFile("CSV file (*.csv)|*.csv") ?? RenameCsvPath);
            ContinueCommand = new RelayCommand(Continue);
            CloseEditorCommand = new RelayCommand(() => EditorOpen = false);
            ExportCommand = new RelayCommand(Export, () => Items.Count > 0);
        }

        public ObservableCollection<CameraInfo> Items { get; } = new ObservableCollection<CameraInfo>();
        public ICollectionView View { get; }
        public List<CameraInfo> SelectedItems { get; } = new List<CameraInfo>();
        public ObservableCollection<string> Recorders { get; } = new ObservableCollection<string>();
        public string[] StateFilters { get; } = { "All", "Enabled", "Disabled" };
        public ObservableCollection<string> StorageNames { get; } = new ObservableCollection<string>();

        public string Filter { get => _filter; set { if (Set(ref _filter, value)) View.Refresh(); } }
        public string RecorderFilter { get => _recorderFilter; set { if (Set(ref _recorderFilter, value)) View.Refresh(); } }
        public string StateFilter { get => _stateFilter; set { if (Set(ref _stateFilter, value)) View.Refresh(); } }

        public string Summary => $"{Items.Count} cameras, {Items.Count(c => c.Enabled == false)} disabled · {SelectedItems.Count} selected";

        public bool EditorOpen { get => _editorOpen; set => Set(ref _editorOpen, value); }

        public CameraEditKind EditKind
        {
            get => _editKind;
            set
            {
                if (!Set(ref _editKind, value)) return;
                Raise(nameof(IsRenamePattern));
                Raise(nameof(IsRenameCsv));
                Raise(nameof(IsRecording));
                Raise(nameof(IsStorage));
                Raise(nameof(EditorTitle));
            }
        }

        public bool IsRenamePattern => EditKind == CameraEditKind.RenamePattern;
        public bool IsRenameCsv => EditKind == CameraEditKind.RenameCsv;
        public bool IsRecording => EditKind == CameraEditKind.Recording;
        public bool IsStorage => EditKind == CameraEditKind.Storage;

        public string EditorTitle
        {
            get
            {
                switch (EditKind)
                {
                    case CameraEditKind.RenamePattern: return "Rename cameras with a pattern";
                    case CameraEditKind.RenameCsv: return "Rename cameras from a CSV file";
                    case CameraEditKind.Recording: return "Change recording settings";
                    case CameraEditKind.Storage: return "Move cameras to another storage";
                    default: return string.Empty;
                }
            }
        }

        public string NamePattern { get => _namePattern; set { if (Set(ref _namePattern, value)) Raise(nameof(PatternExample)); } }

        public string PatternExample => SelectedItems.Count == 0 ? string.Empty : "Example: " + CameraEditWorkflow.ExpandPattern(NamePattern, SelectedItems[0], 1);

        public bool? RecordingEnabled { get => _recordingEnabled; set => Set(ref _recordingEnabled, value); }
        public string Framerate { get => _framerate; set => Set(ref _framerate, value); }
        public bool? PrebufferEnabled { get => _prebufferEnabled; set => Set(ref _prebufferEnabled, value); }
        public string PrebufferSeconds { get => _prebufferSeconds; set => Set(ref _prebufferSeconds, value); }
        public string StorageName { get => _storageName; set => Set(ref _storageName, value); }
        public string RenameCsvPath { get => _renameCsvPath; set => Set(ref _renameCsvPath, value); }
        public string EditorMessage { get => _editorMessage; set => Set(ref _editorMessage, value); }

        public ICommand RefreshCommand { get; }
        public ICommand EnableCommand { get; }
        public ICommand DisableCommand { get; }
        public ICommand RenameCommand { get; }
        public ICommand RenameCsvCommand { get; }
        public ICommand RecordingCommand { get; }
        public ICommand StorageCommand { get; }
        public ICommand BrowseCsvCommand { get; }
        public ICommand ContinueCommand { get; }
        public ICommand CloseEditorCommand { get; }
        public ICommand ExportCommand { get; }

        private bool Capable(string id) => Services.Session.Capabilities.Any(c => c.Definition.Id == id && c.IsAvailable);

        private bool CanEdit() => IsConnected && SelectedItems.Count > 0 && Capable("cameras.update");

        private bool Matches(CameraInfo c) =>
            (RecorderFilter == HardwareViewModel.AllRecorders || RecorderFilter == null || c.RecordingServerName == RecorderFilter) &&
            (StateFilter == "All" || (StateFilter == "Enabled" ? c.Enabled == true : c.Enabled == false)) &&
            (string.IsNullOrWhiteSpace(Filter) || (c.Name ?? string.Empty).IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0 || (c.HardwareName ?? string.Empty).IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0 || (c.HardwareAddress ?? string.Empty).IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0);

        public void SetSelection(IList items)
        {
            SelectedItems.Clear();
            SelectedItems.AddRange(items.OfType<CameraInfo>());
            Raise(nameof(Summary));
            Raise(nameof(PatternExample));
            CommandManager.InvalidateRequerySuggested();
        }

        public override async Task OnNavigatedToAsync()
        {
            if (IsConnected && Services.Session.Inventory.LoadedAt == null) await RefreshAsync();
            else Load();
        }

        public override void OnSessionChanged()
        {
            base.OnSessionChanged();
            Load();
        }

        private void Load()
        {
            Items.Clear();
            foreach (var c in Services.Session.Inventory.Cameras.OrderBy(c => c.RecordingServerName).ThenBy(c => c.HardwareName).ThenBy(c => c.Channel)) Items.Add(c);
            Recorders.Clear();
            Recorders.Add(HardwareViewModel.AllRecorders);
            foreach (var r in Services.Session.Inventory.RecordingServers) Recorders.Add(r.Name);
            Raise(nameof(Summary));
        }

        private async Task RefreshAsync()
        {
            await Busy("Retrieving cameras...", async () =>
            {
                var r = await Services.Inventory.RefreshAsync(CancellationToken.None);
                Error = r.Error;
            });
            Load();
        }

        private void OpenEditor(CameraEditKind kind)
        {
            EditKind = kind;
            EditorMessage = null;
            if (kind == CameraEditKind.Storage)
            {
                StorageNames.Clear();
                var recorders = SelectedItems.Select(c => c.RecordingServerName).Distinct().ToList();
                // Only storages that exist on every selected camera's recording server can be chosen.
                var names = Services.Session.Inventory.Storage.Where(s => recorders.Contains(s.RecordingServerName)).GroupBy(s => s.Name)
                    .Where(g => recorders.All(r => g.Any(s => s.RecordingServerName == r))).Select(g => g.Key).OrderBy(n => n);
                foreach (var n in names) StorageNames.Add(n);
                if (StorageNames.Count == 0) EditorMessage = "No storage with the same name exists on all recording servers of the selected cameras.";
            }

            EditorOpen = true;
        }

        private void Start(CameraEditKind kind)
        {
            EditKind = kind;
            Continue();
        }

        private void Continue()
        {
            var wf = Services.CameraEdits;
            var cams = SelectedItems.ToList();
            Func<CameraInfo, IReadOnlyDictionary<string, object>> desired;
            string title;
            switch (EditKind)
            {
                case CameraEditKind.Enable:
                    title = "Enable cameras";
                    desired = c => new Dictionary<string, object> { { "Enabled", true } };
                    break;
                case CameraEditKind.Disable:
                    title = "Disable cameras";
                    desired = c => new Dictionary<string, object> { { "Enabled", false } };
                    break;
                case CameraEditKind.RenamePattern:
                {
                    title = "Rename cameras";
                    var index = cams.Select((c, i) => new { c, i }).ToDictionary(x => x.c, x => x.i + 1);
                    desired = c => new Dictionary<string, object> { { "Name", CameraEditWorkflow.ExpandPattern(NamePattern, c, index[c]) } };
                    break;
                }

                case CameraEditKind.RenameCsv:
                {
                    if (string.IsNullOrEmpty(RenameCsvPath))
                    {
                        EditorMessage = "Choose a CSV file with columns NewName and Id (or Name).";
                        return;
                    }

                    CameraEditWorkflow.RenameMapping map;
                    try
                    {
                        map = CameraEditWorkflow.ParseRenameCsv(CsvParser.ParseFile(RenameCsvPath));
                    }
                    catch (Exception ex)
                    {
                        EditorMessage = "The file could not be read: " + ex.Message;
                        return;
                    }

                    if (map.Invalid > 0)
                    {
                        EditorMessage = $"{map.Rows.Count} rows detected, {map.Valid} valid, {map.Invalid} invalid. First problem (line {map.Rows.First(r => !r.IsValid).LineNumber}): {map.Rows.First(r => !r.IsValid).Messages}. Fix the file and try again.";
                        return;
                    }

                    title = "Rename cameras from CSV";
                    cams = Services.Session.Inventory.Cameras.Where(c => map.ById.ContainsKey(c.Id) || (c.Name != null && map.ByName.ContainsKey(c.Name))).ToList();
                    desired = c => new Dictionary<string, object> { { "Name", map.ById.TryGetValue(c.Id, out var n) ? n : map.ByName[c.Name] } };
                    var unmatched = map.Valid - cams.Count;
                    if (cams.Count == 0)
                    {
                        EditorMessage = "No camera in the file matches a camera on this site.";
                        return;
                    }

                    if (unmatched > 0) EditorMessage = $"{unmatched} row(s) did not match any camera and are ignored.";
                    break;
                }

                case CameraEditKind.Recording:
                {
                    title = "Change recording settings";
                    var d = new Dictionary<string, object>();
                    if (RecordingEnabled.HasValue) d["RecordingEnabled"] = RecordingEnabled.Value;
                    if (PrebufferEnabled.HasValue) d["PrebufferEnabled"] = PrebufferEnabled.Value;
                    if (!string.IsNullOrWhiteSpace(Framerate))
                    {
                        if (!double.TryParse(Framerate, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) || fps <= 0)
                        {
                            EditorMessage = "Frame rate must be a positive number (use '.' as decimal separator).";
                            return;
                        }

                        d["RecordingFramerate"] = fps;
                    }

                    if (!string.IsNullOrWhiteSpace(PrebufferSeconds))
                    {
                        if (!int.TryParse(PrebufferSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pb) || pb < 0)
                        {
                            EditorMessage = "Pre-buffer seconds must be a whole number.";
                            return;
                        }

                        d["PrebufferSeconds"] = pb;
                    }

                    if (d.Count == 0)
                    {
                        EditorMessage = "Choose at least one setting to change.";
                        return;
                    }

                    desired = c => d;
                    break;
                }

                case CameraEditKind.Storage:
                {
                    if (string.IsNullOrEmpty(StorageName))
                    {
                        EditorMessage = "Choose a storage.";
                        return;
                    }

                    StartStorageReview(cams, StorageName);
                    return;
                }

                default:
                    return;
            }

            EditorOpen = false;
            var plan = wf.BuildPlan(title, cams, desired);
            if (plan.ChangedItemCount == 0)
            {
                Services.Dialogs.ShowInfo(title, "The selected cameras already have these values. Nothing to change.");
                return;
            }

            Review = new ReviewViewModel(Services, plan,
                ct => wf.RunWhatIfAsync(plan, cams, ct),
                async (p, ct) => ReviewViewModel.Rows(await wf.ApplyAsync(plan, cams, p, ct)),
                () =>
                {
                    Review = null;
                    View.Refresh();
                    Raise(nameof(Summary));
                },
                failed =>
                {
                    var retry = failed.OfType<CameraInfo>().ToList();
                    Review = null;
                    SelectedItems.Clear();
                    SelectedItems.AddRange(retry);
                    Continue();
                });
        }

        private void StartStorageReview(List<CameraInfo> cams, string storage)
        {
            EditorOpen = false;
            var plan = new Domain.Changes.ChangePlan { Title = $"Move recording storage to '{storage}'", OperationId = "cameras.storage", Risk = Domain.Classification.RiskLevel.Modify, WhatIfAvailable = true };
            var targets = cams.Where(c => !string.Equals(c.RecordingStorageName, storage, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var c in cams)
            {
                var item = new Domain.Changes.PlannedChange { TargetType = "Camera", TargetId = c.Id, TargetName = c.Name, Operation = "Update" };
                item.Properties.Add(new Domain.Changes.PropertyChange { Property = "Storage", DisplayName = "Recording storage", Current = c.RecordingStorageName, New = storage });
                plan.Items.Add(item);
            }

            plan.Warnings.Add("Set-VmsDeviceStorage sets the target storage for each camera. MilestonePSTools does not document what happens to recordings already stored; check the XProtect documentation for your version before moving many cameras.");
            plan.Summary.Add(new KeyValuePair<string, string>("Cameras to move", targets.Count.ToString(CultureInfo.InvariantCulture)));
            plan.ScriptPreview = Services.Scripts.Script(targets.Select(c => Services.Session.Gateway.PlanSetDeviceStorage(c, storage)));
            Review = new ReviewViewModel(Services, plan,
                async ct =>
                {
                    var lines = new List<string>();
                    foreach (var c in targets)
                    {
                        var o = await Services.Runner.RunAsync(Services.Session.Gateway.PlanSetDeviceStorage(c, storage).AsWhatIf(), new OperationContext("preview storage change", c.Name), null, ct, quiet: true);
                        lines.AddRange(o.Success ? o.Result.WhatIfLines : new List<string> { $"{c.Name}: {o.Error?.Summary}" });
                    }

                    return lines;
                },
                async (p, ct) =>
                {
                    var r = await Services.Bulk.RunAsync("Move recording storage", targets, c => Services.Session.Gateway.PlanSetDeviceStorage(c, storage), c => c.Name,
                        c => new OperationContext("move recording storage", c.Name, c.RecordingServerName), p, ct,
                        (c, o) =>
                        {
                            if (o.Success && !o.PartialFailure) c.RecordingStorageName = storage;
                            return Task.CompletedTask;
                        });
                    return ReviewViewModel.Rows(r);
                },
                () =>
                {
                    Review = null;
                    View.Refresh();
                });
        }

        private void Export()
        {
            var csv = CsvParser.Write(new[] { "Name", "Enabled", "Channel", "Hardware", "Address", "RecordingServer", "RecordingEnabled", "RecordingFramerate", "Storage", "Id" },
                Items.Select(c => (IReadOnlyList<string>)new[] { c.Name, c.Enabled?.ToString(), c.Channel?.ToString(CultureInfo.InvariantCulture), c.HardwareName, c.HardwareAddress, c.RecordingServerName, c.RecordingEnabled?.ToString(), c.RecordingFramerate?.ToString(CultureInfo.InvariantCulture), c.RecordingStorageName, c.Id.ToString() }));
            Services.Dialogs.SaveText("CSV file (*.csv)|*.csv", "camera-inventory.csv", csv);
        }
    }

    public sealed class StorageViewModel : PageViewModel
    {
        public StorageViewModel(AppServices services)
            : base(services, "Storage")
        {
            RefreshCommand = new AsyncCommand(RefreshAsync, () => IsConnected && !IsBusy);
            QuickActions = new QuickActionsViewModel(services, this, "Storage", () => SelectedItems, RefreshAsync);
        }

        public ObservableCollection<StorageInfo> Items { get; } = new ObservableCollection<StorageInfo>();
        public List<StorageInfo> SelectedItems { get; } = new List<StorageInfo>();
        public ICommand RefreshCommand { get; }

        /// <summary>Add a recording storage, or remove storages that are not the default and not in use.</summary>
        public QuickActionsViewModel QuickActions { get; }

        public string Note => "Retention is shown in days, computed from the storage's RetainMinutes property. Archive storages can be inspected with Get-VmsArchiveStorage in Command Explorer.";

        public void SetSelection(IList items)
        {
            SelectedItems.Clear();
            SelectedItems.AddRange(items.OfType<StorageInfo>());
            QuickActions.Refresh();
        }

        public override async Task OnNavigatedToAsync()
        {
            if (IsConnected && Services.Session.Inventory.LoadedAt == null) await RefreshAsync();
            else Load();
        }

        public override void OnSessionChanged()
        {
            base.OnSessionChanged();
            Load();
            QuickActions.Refresh();
        }

        private async Task RefreshAsync()
        {
            await Busy("Retrieving storage...", async () => Error = (await Services.Inventory.RefreshAsync(CancellationToken.None)).Error);
            Load();
        }

        private void Load()
        {
            Items.Clear();
            foreach (var s in Services.Session.Inventory.Storage.OrderBy(s => s.RecordingServerName).ThenBy(s => s.Name)) Items.Add(s);
        }
    }

    public sealed class InventoryTab : ObservableObject
    {
        private DataView _data;
        private string _status;

        public string Title { get; set; }
        public GenericQuery Query { get; set; }

        /// <summary>Custom loader (e.g. members of one role); null = run <see cref="Query"/>.</summary>
        public Func<CancellationToken, Task<QueryResult<GenericItem>>> Loader { get; set; }

        /// <summary>Object the tab's items belong to (the role whose members are listed).</summary>
        public VmsObject Parent { get; set; }

        public string ObjectType => Query.ObjectType;
        public List<GenericItem> Items { get; } = new List<GenericItem>();
        public DataView Data { get => _data; set => Set(ref _data, value); }
        public string Status { get => _status; set => Set(ref _status, value); }
    }

    /// <summary>
    /// Inventory pages for roles, rules and Smart Client, driven by query definitions; unavailable commands are explained,
    /// not hidden. Guided changes for the selected rows come from the quick action catalog.
    /// </summary>
    public sealed class GenericInventoryViewModel : PageViewModel
    {
        private InventoryTab _selected;

        private GenericInventoryViewModel(AppServices services, string title, string note, params InventoryTab[] tabs)
            : base(services, title)
        {
            Note = note;
            foreach (var t in tabs) Tabs.Add(t);
            QuickActions = new QuickActionsViewModel(services, this, null, () => SelectedItems, () => LoadAsync(), () => Selected?.Parent);
            Selected = Tabs.FirstOrDefault();
            LoadCommand = new AsyncCommand(() => LoadAsync(), () => IsConnected && !IsBusy && Selected != null);
            ShowMembersCommand = new AsyncCommand(ShowMembersAsync, () => IsConnected && !IsBusy && Selected?.ObjectType == "Role" && SelectedItems.Count == 1 && Services.Session.Catalog?.Contains("Get-VmsRoleMember") == true);
        }

        /// <summary>Columns are the simple-typed properties the objects actually have; nothing is assumed.</summary>
        public string Note { get; }
        public ObservableCollection<InventoryTab> Tabs { get; } = new ObservableCollection<InventoryTab>();
        public List<GenericItem> SelectedItems { get; } = new List<GenericItem>();
        public QuickActionsViewModel QuickActions { get; }
        public bool CanShowMembers => Tabs.Any(t => t.ObjectType == "Role");

        public InventoryTab Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                SelectedItems.Clear();
                QuickActions.ObjectType = value?.ObjectType;
                QuickActions.Refresh();
            }
        }

        public ICommand LoadCommand { get; }
        public ICommand ShowMembersCommand { get; }

        private static InventoryTab Tab(string title, string type, Func<Protocol.PipelineStep> step, string command, params string[] props) =>
            new InventoryTab { Title = title, Query = new GenericQuery { Title = "Retrieve " + title.ToLowerInvariant(), ObjectType = type, Step = step, Properties = props, RequiredCommand = command } };

        public static GenericInventoryViewModel Security(AppServices s) => new GenericInventoryViewModel(s, "Users & Roles",
            "Load a list, select rows and choose an action. Role permissions are configured in Management Client; MilestonePSTools role permission commands are available in Command Explorer (Expert Mode).",
            Tab("Roles", "Role", PsCommands.GetVmsRole, "Get-VmsRole"),
            Tab("Basic users", "BasicUser", PsCommands.GetVmsBasicUser, "Get-VmsBasicUser"));

        public static GenericInventoryViewModel Rules(AppServices s) => new GenericInventoryViewModel(s, "Rules & Events",
            "Rules can be enabled, disabled and renamed here. Alarm definitions are listed read-only: Set-VmsAlarmDefinition has no Enabled parameter. Rule import/export is available as Import-VmsRule / Export-VmsRule in Command Explorer.",
            Tab("Rules", "Rule", PsCommands.GetVmsRule, "Get-VmsRule"),
            Tab("Alarm definitions", "AlarmDefinition", PsCommands.GetVmsAlarmDefinition, "Get-VmsAlarmDefinition"));

        public static GenericInventoryViewModel SmartClient(AppServices s) => new GenericInventoryViewModel(s, "Smart Client",
            "View groups can be created, renamed and removed here. Smart Client profiles are listed read-only.",
            Tab("View groups", "ViewGroup", () => PsCommands.GetVmsViewGroup(), "Get-VmsViewGroup"),
            Tab("Smart Client profiles", "ClientProfile", PsCommands.GetVmsClientProfile, "Get-VmsClientProfile"));

        /// <summary>Maps the grid's selected rows back to the objects (rows keep their original index in the table).</summary>
        public void SetSelection(IList rows)
        {
            SelectedItems.Clear();
            var tab = Selected;
            if (tab?.Data != null)
            {
                foreach (var r in rows.OfType<DataRowView>())
                {
                    var i = tab.Data.Table.Rows.IndexOf(r.Row);
                    if (i >= 0 && i < tab.Items.Count) SelectedItems.Add(tab.Items[i]);
                }
            }

            QuickActions.Refresh();
            CommandManager.InvalidateRequerySuggested();
        }

        public override void OnSessionChanged()
        {
            base.OnSessionChanged();
            QuickActions.Refresh();
        }

        private async Task LoadAsync(InventoryTab tab = null)
        {
            tab = tab ?? Selected;
            if (tab.Loader == null && Services.Session.Catalog?.Contains(tab.Query.RequiredCommand) != true)
            {
                tab.Status = $"Not supported by the currently installed MilestonePSTools version: {tab.Query.RequiredCommand} is not available.";
                return;
            }

            await Busy("Loading " + tab.Title.ToLowerInvariant() + "...", async () =>
            {
                var r = tab.Loader != null ? await tab.Loader(CancellationToken.None) : await Services.Session.Gateway.QueryAsync(tab.Query, CancellationToken.None);
                if (!r.Success)
                {
                    Error = ErrorTranslator.Translate(r.Raw, new OperationContext(tab.Query.Title));
                    return;
                }

                Services.Session.Register(r.Items);
                tab.Items.Clear();
                tab.Items.AddRange(r.Items);
                tab.Data = GenericTable.ToDataView(r.Items);
                tab.Status = $"{r.Items.Count} item(s) · source: {tab.Query.RequiredCommand}";
            });
            if (tab == Selected)
            {
                SelectedItems.Clear();
                QuickActions.Refresh();
            }
        }

        private async Task ShowMembersAsync()
        {
            var role = SelectedItems[0];
            var existing = Tabs.FirstOrDefault(t => t.ObjectType == "User");
            if (existing != null) Tabs.Remove(existing);
            var tab = new InventoryTab
            {
                Title = $"Members of {role.Name}",
                Parent = role,
                Query = new GenericQuery { Title = $"Retrieve members of {role.Name}", ObjectType = "User", RequiredCommand = "Get-VmsRoleMember" },
                Loader = ct => Services.Session.Gateway.GetRoleMembersAsync(role, ct)
            };
            Tabs.Add(tab);
            Selected = tab;
            await LoadAsync(tab);
        }
    }
}
