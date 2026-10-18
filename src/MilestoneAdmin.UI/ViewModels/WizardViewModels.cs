using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.Application.Execution;
using MilestoneAdmin.Application.Services;
using MilestoneAdmin.Application.Workflows;
using MilestoneAdmin.Domain.Csv;
using MilestoneAdmin.Domain.Errors;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.PSTools.Execution;
using MilestoneAdmin.UI.Mvvm;

namespace MilestoneAdmin.UI.ViewModels
{
    /// <summary>
    /// Add Hardware wizard: recording server -> discovery method and addresses -> credentials -> driver -> discover ->
    /// select devices and names -> review -> add -> configure channels (names, enabled, storage) -> review -> results.
    /// </summary>
    public sealed class AddHardwareViewModel : PageViewModel, ISecretSink
    {
        private readonly ShellViewModel _shell;
        private AddHardwareWorkflow _wf;
        private int _step = 1;
        private RecordingServerInfo _recorder;
        private DiscoveryMode _mode = DiscoveryMode.Addresses;
        private string _addressText;
        private string _addressErrors;
        private string _userName = "root";
        private HardwareDriverInfo _driver;
        private bool _useDefaultCredentials = true;
        private bool _useHttps;
        private string _scanStatus;

        public AddHardwareViewModel(AppServices services, ShellViewModel shell)
            : base(services, "Add hardware")
        {
            _shell = shell;
            NextCommand = new AsyncCommand(NextAsync, () => !IsBusy && CanNext());
            BackCommand = new RelayCommand(() => Step--, () => !IsBusy && Step > 1 && Step < 6);
            RestartCommand = new RelayCommand(Restart);
            PrecheckCommand = new AsyncCommand(PrecheckAsync, () => Recorder != null && !IsBusy);
        }

        /// <summary>Step 1 of the workflow: the site the hardware is added to (changed on the System page).</summary>
        public string SiteText => $"Site: {Services.Session.SelectedSite ?? Services.Session.ManagementServer?.Name ?? "(not connected)"}. To add hardware to another site of a Milestone Federated Architecture, switch the site on the System page first.";

        public ObservableCollection<RecordingServerInfo> Recorders { get; } = new ObservableCollection<RecordingServerInfo>();
        public ObservableCollection<HardwareDriverInfo> Drivers { get; } = new ObservableCollection<HardwareDriverInfo>();
        public ObservableCollection<AddHardwareWorkflow.AddCandidate> Candidates { get; } = new ObservableCollection<AddHardwareWorkflow.AddCandidate>();
        public ObservableCollection<AddHardwareWorkflow.ChannelSetting> Channels { get; } = new ObservableCollection<AddHardwareWorkflow.ChannelSetting>();
        public ObservableCollection<string> StorageNames { get; } = new ObservableCollection<string>();

        /// <summary>Existing camera groups, offered in the device group picker (a new path can still be typed).</summary>
        public ObservableCollection<string> DeviceGroupPaths { get; } = new ObservableCollection<string>();
        public ObservableCollection<PrecheckItem> Checks { get; } = new ObservableCollection<PrecheckItem>();
        public Array Modes => Enum.GetValues(typeof(DiscoveryMode));

        public int Step
        {
            get => _step;
            set
            {
                if (!Set(ref _step, value)) return;
                for (var i = 1; i <= 6; i++) Raise("IsStep" + i);
                Raise(nameof(StepTitle));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsStep1 => Step == 1;
        public bool IsStep2 => Step == 2;
        public bool IsStep3 => Step == 3;
        public bool IsStep4 => Step == 4;
        public bool IsStep5 => Step == 5;
        public bool IsStep6 => Step == 6;

        public string StepTitle
        {
            get
            {
                switch (Step)
                {
                    case 1: return "Step 1 of 6 · Recording server and discovery";
                    case 2: return "Step 2 of 6 · Device credentials and driver";
                    case 3: return "Step 3 of 6 · Select devices";
                    case 4: return "Step 4 of 6 · Review and add";
                    case 5: return "Step 5 of 6 · Configure cameras";
                    default: return "Step 6 of 6 · Done";
                }
            }
        }

        public RecordingServerInfo Recorder { get => _recorder; set => Set(ref _recorder, value); }

        public DiscoveryMode Mode
        {
            get => _mode;
            set
            {
                if (!Set(ref _mode, value)) return;
                Raise(nameof(NeedsAddresses));
                Raise(nameof(NeedsDriver));
            }
        }

        public bool NeedsAddresses => Mode != DiscoveryMode.Express;
        public bool NeedsDriver => Mode == DiscoveryMode.KnownDriver;
        public string AddressText { get => _addressText; set => Set(ref _addressText, value); }
        public string AddressErrors { get => _addressErrors; set => Set(ref _addressErrors, value); }
        public string UserName { get => _userName; set => Set(ref _userName, value); }

        /// <summary>Device password from the PasswordBox (SecureString copy).</summary>
        public SecureString Password { get; set; }

        public void SetSecret(SecureString value)
        {
            Password = value;
            CommandManager.InvalidateRequerySuggested();
        }

        public HardwareDriverInfo Driver { get => _driver; set => Set(ref _driver, value); }
        public bool UseDefaultCredentials { get => _useDefaultCredentials; set => Set(ref _useDefaultCredentials, value); }
        public bool UseHttps { get => _useHttps; set => Set(ref _useHttps, value); }
        public string ScanStatus { get => _scanStatus; set => Set(ref _scanStatus, value); }

        public ICommand NextCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand RestartCommand { get; }
        public ICommand PrecheckCommand { get; }

        public void PreselectRecorder(RecordingServerInfo rec)
        {
            Restart();
            Recorder = Recorders.FirstOrDefault(r => r.Id == rec?.Id) ?? rec;
        }

        public override Task OnNavigatedToAsync()
        {
            if (Recorders.Count == 0) LoadRecorders();
            return Task.CompletedTask;
        }

        public override void OnSessionChanged()
        {
            base.OnSessionChanged();
            Raise(nameof(SiteText));
            LoadRecorders();
        }

        private void LoadRecorders()
        {
            var keep = Recorder?.Id;
            Recorders.Clear();
            foreach (var r in Services.Session.Inventory.RecordingServers) Recorders.Add(r);
            Recorder = Recorders.FirstOrDefault(r => r.Id == keep) ?? Recorders.FirstOrDefault();
        }

        private void Restart()
        {
            _wf = null;
            Candidates.Clear();
            Channels.Clear();
            Checks.Clear();
            ScanStatus = null;
            Error = null;
            Review = null;
            Step = 1;
        }

        private bool CanNext()
        {
            switch (Step)
            {
                case 1: return IsConnected && Recorder != null && (Mode == DiscoveryMode.Express || !string.IsNullOrWhiteSpace(AddressText));
                case 2: return Mode != DiscoveryMode.KnownDriver || (Driver != null && !string.IsNullOrWhiteSpace(UserName) && Password != null && Password.Length > 0);
                case 3: return Candidates.Any(c => c.Selected);
                default: return false;
            }
        }

        private async Task PrecheckAsync()
        {
            await Busy("Running pre-checks...", async () =>
            {
                Checks.Clear();
                var cmds = Mode == DiscoveryMode.KnownDriver ? new[] { "Add-VmsHardware", "Get-VmsHardwareDriver" } : new[] { "Start-VmsHardwareScan", "Add-VmsHardware", "Get-VmsHardwareDriver" };
                var r = await Services.Environment.CheckOperationAsync(Services.Session, cmds, new[] { Recorder }, CancellationToken.None);
                foreach (var c in r.Items) Checks.Add(c);
            });
        }

        private async Task NextAsync()
        {
            Error = null;
            switch (Step)
            {
                case 1:
                {
                    _wf = new AddHardwareWorkflow(Services.Session, Services.Runner, Services.Bulk, Services.Scripts) { Recorder = Recorder, Mode = Mode };
                    if (NeedsAddresses)
                    {
                        var parsed = AddHardwareWorkflow.ParseAddresses(AddressText, UseHttps);
                        AddressErrors = parsed.Errors.Count == 0 ? null : string.Join("\n", parsed.Errors);
                        if (parsed.Errors.Count > 0 || parsed.Addresses.Count == 0) return;
                        _wf.Addresses.AddRange(parsed.Addresses);
                    }

                    await Busy($"Reading hardware drivers on {Recorder.Name}...", async () =>
                    {
                        Error = await _wf.LoadDriversAsync(CancellationToken.None);
                        Drivers.Clear();
                        foreach (var d in _wf.AvailableDrivers) Drivers.Add(d);
                    });
                    if (Error == null) Step = 2;
                    break;
                }

                case 2:
                {
                    _wf.Credentials.Clear();
                    if (!string.IsNullOrWhiteSpace(UserName) && Password != null && Password.Length > 0) _wf.Credentials.Add(new DeviceCredential { UserName = UserName.Trim(), Password = Password.Copy() });
                    _wf.UseDefaultCredentials = UseDefaultCredentials;
                    _wf.UseHttps = UseHttps;
                    _wf.SelectedDriver = Driver;
                    _wf.LimitToDrivers.Clear();
                    if (Mode == DiscoveryMode.Addresses && Driver?.Number != null) _wf.LimitToDrivers.Add(Driver.Number.Value);
                    await Busy(Mode == DiscoveryMode.KnownDriver ? "Preparing..." : "Discovering devices (this can take a few minutes)...", async () =>
                    {
                        var progress = new Progress<OperationEvent>(e =>
                        {
                            if (e.Kind == OperationEventKind.Progress) ScanStatus = $"{e.Activity}: {e.Message} {(e.Percent >= 0 ? e.Percent + "%" : string.Empty)}";
                        });
                        Error = await _wf.ScanAsync(progress, CancellationToken.None);
                    });
                    Candidates.Clear();
                    foreach (var c in _wf.Candidates) Candidates.Add(c);
                    ScanStatus = Mode == DiscoveryMode.KnownDriver ? null : $"{_wf.ScanResults.Count} device(s) responded, {_wf.Candidates.Count(c => c.Selected)} ready to add.";
                    if (Error == null || Candidates.Count > 0) Step = 3;
                    break;
                }

                case 3:
                {
                    var plan = _wf.BuildAddPlan();
                    Step = 4;
                    Review = new ReviewViewModel(Services, plan, null,
                        async (p, ct) => ReviewViewModel.Rows(await _wf.AddAsync(p, ct)),
                        async () =>
                        {
                            Review = null;
                            await AfterAddAsync();
                        });
                    break;
                }
            }
        }

        private async Task AfterAddAsync()
        {
            if (_wf.AddedHardware.Count == 0)
            {
                Step = 3;
                return;
            }

            await Busy("Reading the new cameras...", async () => Error = await _wf.LoadChannelsAsync(CancellationToken.None));
            Channels.Clear();
            foreach (var c in _wf.Channels) Channels.Add(c);
            StorageNames.Clear();
            StorageNames.Add(string.Empty);
            foreach (var s in Services.Session.Inventory.Storage.Where(s => s.RecordingServerName == Recorder.Name)) StorageNames.Add(s.Name);
            Step = 5;
            _ = LoadDeviceGroupPathsAsync();
            ConfigureChannelsCommand = new RelayCommand(ReviewChannels);
            SkipChannelsCommand = new AsyncCommand(FinishAsync);
            Raise(nameof(ConfigureChannelsCommand));
            Raise(nameof(SkipChannelsCommand));
        }

        private async Task LoadDeviceGroupPathsAsync()
        {
            DeviceGroupPaths.Clear();
            var gateway = Services.Session.Gateway;
            if (gateway == null) return;
            try
            {
                foreach (var path in await gateway.GetCameraGroupPathsAsync(CancellationToken.None)) DeviceGroupPaths.Add(path);
            }
            catch (Exception ex)
            {
                // The list is only a convenience: the path can still be typed, so a failure is logged, not shown as an error.
                Services.Activity.Warning("Could not list the existing camera groups: " + ex.Message);
            }
        }

        /// <summary>Optional camera group for the new cameras (Add-VmsHardware does not assign device groups).</summary>
        public string DeviceGroupPath
        {
            get => _wf?.DeviceGroupPath;
            set
            {
                if (_wf == null) return;
                _wf.DeviceGroupPath = value;
                Raise();
            }
        }

        public ICommand ConfigureChannelsCommand { get; private set; }
        public ICommand SkipChannelsCommand { get; private set; }

        private void ReviewChannels()
        {
            var (plan, ops) = _wf.BuildChannelPlan();
            if (ops.Count == 0)
            {
                _ = FinishAsync();
                return;
            }

            Review = new ReviewViewModel(Services, plan, null,
                async (p, ct) => ReviewViewModel.Rows(await _wf.ApplyChannelsAsync(ops, p, ct)),
                async () =>
                {
                    Review = null;
                    await FinishAsync();
                });
        }

        private async Task FinishAsync()
        {
            Step = 6;
            await Busy("Refreshing inventory...", async () => Error = (await Services.Inventory.RefreshAsync(CancellationToken.None)).Error);
        }
    }

    public sealed class CsvResultRow
    {
        public string Address { get; set; }
        public string RecordingServer { get; set; }
        public string Status { get; set; }
        public string Details { get; set; }
        public AdminError Error { get; set; }
    }

    public sealed class CsvImportViewModel : PageViewModel, ISecretSink
    {
        private CsvOnboardingWorkflow _wf;
        private string _path;
        private string _summary;
        private string _defaultRecorder;
        private string _userName = "root";
        private bool _showInvalidOnly;
        private bool _useDefaultCredentials = true;
        private List<CsvImportItemResult> _results;
        private double _progress;
        private string _progressText;

        private WorkbookPreview _workbook;
        private bool _updateExisting;

        public CsvImportViewModel(AppServices services)
            : base(services, "Bulk onboarding")
        {
            BrowseCommand = new AsyncCommand(BrowseAsync, () => IsConnected && !IsBusy);
            RevalidateCommand = new AsyncCommand(ValidateAsync, () => _wf != null && !IsWorkbookMode && !IsBusy);
            ApplyCommand = new AsyncCommand(ApplyAsync, () => _wf?.Report?.CanApply == true && !IsWorkbookMode && !IsBusy);
            ExportFailedCommand = new RelayCommand(ExportFailed, () => !IsWorkbookMode && _wf?.Report != null && (_wf.Report.InvalidRows > 0 || _results?.Any(r => r.Error != null) == true));
            ShowResultErrorCommand = new RelayCommand(o => Services.Dialogs.ShowError((o as CsvResultRow)?.Error), o => (o as CsvResultRow)?.Error != null);
            TemplateExcelCommand = new RelayCommand(() => SaveTemplate(excel: true));
            TemplateCsvCommand = new RelayCommand(() => SaveTemplate(excel: false));
            ReviewWorkbookCommand = new RelayCommand(ReviewWorkbook, () => IsWorkbookMode && IsConnected && !IsBusy && Services.Session.Catalog?.Contains("Import-VmsHardware") == true);
        }

        /// <summary>The opened file is a MilestonePSTools Export-VmsHardware workbook, imported by Import-VmsHardware itself.</summary>
        public bool IsWorkbookMode => _workbook != null;

        public bool IsTableMode => _workbook == null;
        public ObservableCollection<WorkbookHardwareRow> WorkbookRows { get; } = new ObservableCollection<WorkbookHardwareRow>();
        public string WorkbookSummary => _workbook?.SummaryText;
        public bool UpdateExisting { get => _updateExisting; set => Set(ref _updateExisting, value); }
        public ICommand TemplateExcelCommand { get; }
        public ICommand TemplateCsvCommand { get; }
        public ICommand ReviewWorkbookCommand { get; }

        public ObservableCollection<CsvRowResult> Rows { get; } = new ObservableCollection<CsvRowResult>();
        public ObservableCollection<string> FileMessages { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> Recorders { get; } = new ObservableCollection<string>();
        public ObservableCollection<CsvResultRow> Results { get; } = new ObservableCollection<CsvResultRow>();

        public string Path { get => _path; set => Set(ref _path, value); }
        public string Summary { get => _summary; set => Set(ref _summary, value); }
        public string DefaultRecorder { get => _defaultRecorder; set => Set(ref _defaultRecorder, value); }
        public string UserName { get => _userName; set => Set(ref _userName, value); }
        public SecureString Password { get; set; }
        public void SetSecret(SecureString value) => Password = value;
        public bool UseDefaultCredentials { get => _useDefaultCredentials; set => Set(ref _useDefaultCredentials, value); }
        public double Progress { get => _progress; set => Set(ref _progress, value); }
        public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

        public bool ShowInvalidOnly
        {
            get => _showInvalidOnly;
            set
            {
                if (Set(ref _showInvalidOnly, value)) Fill();
            }
        }

        public string FormatHelp => "CSV file or Excel workbook (.xlsx) with the columns documented for Import-VmsHardware: Address (required), RecordingServer, HardwareName, Name, Channel, DeviceType, " +
                                    "DriverNumber, DriverGroup, UserName, Password, Enabled, StorageName, Coordinates, DeviceGroups (camera group paths such as /Imported cameras;/Lobby). " +
                                    "One row per camera channel; rows with the same address are one hardware unit. In Excel, the first worksheet with an Address column is used. " +
                                    "A workbook exported by MilestonePSTools (Export-VmsHardware) is recognized and imported by MilestonePSTools itself.";

        public ICommand BrowseCommand { get; }
        public ICommand RevalidateCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand ExportFailedCommand { get; }
        public ICommand ShowResultErrorCommand { get; }

        public override Task OnNavigatedToAsync()
        {
            Recorders.Clear();
            Recorders.Add(string.Empty);
            foreach (var r in Services.Session.Inventory.RecordingServers) Recorders.Add(r.Name);
            return Task.CompletedTask;
        }

        private async Task BrowseAsync()
        {
            var p = Services.Dialogs.OpenFile("Hardware file (*.csv;*.xlsx)|*.csv;*.xlsx|CSV file (*.csv)|*.csv|Excel workbook (*.xlsx)|*.xlsx");
            if (p == null) return;
            Path = p;
            _wf = new CsvOnboardingWorkflow(Services.Session, Services.Runner, Services.Activity);
            Results.Clear();
            Rows.Clear();
            FileMessages.Clear();
            Summary = null;
            _results = null;
            SetWorkbook(null);
            bool isWorkbook;
            try
            {
                isWorkbook = WorkbookImportWorkflow.IsMilestonePSToolsWorkbook(p);
            }
            catch (CsvFormatException ex)
            {
                Error = new AdminError { Kind = ErrorKind.InvalidInput, Summary = "The file could not be read: " + ex.Message };
                return;
            }

            if (isWorkbook)
            {
                if (Services.Session.Inventory.LoadedAt == null) await Busy("Reading the inventory...", async () => Error = (await Services.Inventory.RefreshAsync(CancellationToken.None)).Error);
                SetWorkbook(Services.Workbooks.Preview(p));
                return;
            }

            await ValidateAsync();
        }

        private async Task ValidateAsync()
        {
            Error = null;
            await Busy("Validating the file against the connected site...", async () =>
            {
                CsvTable table;
                try
                {
                    table = HardwareImportFile.Read(Path);
                }
                catch (Exception ex) when (ex is FormatException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
                {
                    Error = new AdminError { Kind = ErrorKind.InvalidInput, Summary = "The file could not be read: " + ex.Message };
                    return;
                }

                _wf.DefaultRecordingServer = string.IsNullOrEmpty(DefaultRecorder) ? null : DefaultRecorder;
                if (Services.Session.Inventory.LoadedAt == null) await Services.Inventory.RefreshAsync(CancellationToken.None);
                var (site, err) = await _wf.BuildSiteContextAsync(table, CancellationToken.None);
                if (err != null) Error = err;
                var report = _wf.Load(table, err == null ? site : null);
                Summary = report.SummaryText + (report.ValidatedOnline ? " (validated against the connected site)" : " (offline validation only)") +
                          $" · {report.Hardware.Count} hardware unit(s) to add, {report.Hardware.Count(h => h.AlreadyOnSite)} already on the site.";
                FileMessages.Clear();
                foreach (var m in report.FileErrors) FileMessages.Add("✗ " + m);
                foreach (var m in report.FileWarnings) FileMessages.Add("! " + m);
                Fill();
            });
        }

        private void Fill()
        {
            Rows.Clear();
            if (_wf?.Report == null) return;
            foreach (var r in _wf.Report.Rows.Where(r => !ShowInvalidOnly || !r.IsValid)) Rows.Add(r);
        }

        private async Task ApplyAsync()
        {
            var report = _wf.Report;
            var toAdd = report.Hardware.Where(h => !h.AlreadyOnSite).ToList();
            var needsCommon = toAdd.Any(h => !h.HasCsvCredential);
            if (needsCommon && (Password == null || Password.Length == 0) && toAdd.Any(h => h.DriverNumber.HasValue && !h.HasCsvCredential))
            {
                Error = new AdminError { Kind = ErrorKind.InvalidInput, Summary = "Rows with a DriverNumber and no credential in the file need the common device credential." };
                return;
            }

            if (!Services.Dialogs.Confirm("Apply bulk onboarding", $"Add {toAdd.Count} hardware unit(s) from {report.ValidRows} valid row(s)? Invalid rows ({report.InvalidRows}) are skipped.")) return;
            _wf.CommonCredential = Password != null && Password.Length > 0 ? new DeviceCredential { UserName = UserName, Password = Password.Copy() } : null;
            _wf.UseDefaultCredentials = UseDefaultCredentials;
            await Busy("Adding hardware...", async () =>
            {
                var progress = new Progress<BulkProgress>(p =>
                {
                    Progress = p.Percent;
                    ProgressText = $"{p.Completed} of {p.Total} · {p.Current}";
                });
                _results = await _wf.ApplyAsync(report.Hardware, progress, CancellationToken.None);
                Results.Clear();
                foreach (var r in _results)
                    Results.Add(new CsvResultRow { Address = r.Item.DisplayAddress, RecordingServer = r.Item.RecordingServer, Status = r.Status, Details = r.Error?.Summary ?? string.Join(" ", r.Messages), Error = r.Error });
                await Services.Inventory.RefreshAsync(CancellationToken.None);
            });
        }

        private void SetWorkbook(WorkbookPreview preview)
        {
            _workbook = preview;
            WorkbookRows.Clear();
            if (preview != null)
            {
                foreach (var r in preview.Hardware) WorkbookRows.Add(r);
                foreach (var m in preview.Problems) FileMessages.Add("! " + m);
                if (preview.ContainsPasswords) FileMessages.Add("! The workbook contains plain-text device passwords. Protect or delete it after use.");
            }

            Raise(nameof(IsWorkbookMode));
            Raise(nameof(IsTableMode));
            Raise(nameof(WorkbookSummary));
            CommandManager.InvalidateRequerySuggested();
        }

        private void SaveTemplate(bool excel)
        {
            var recorder = Services.Session.Inventory.RecordingServers.FirstOrDefault()?.Name ?? "Recording server name";
            var rows = HardwareImportTemplate.ExampleRows(recorder);
            if (excel)
            {
                var path = Services.Dialogs.SaveFile("Excel workbook (*.xlsx)|*.xlsx", "hardware-onboarding-template.xlsx");
                if (path != null) XlsxWriter.WriteFile(path, "Hardware import", HardwareImportTemplate.Headers, rows);
            }
            else
            {
                Services.Dialogs.SaveText("CSV file (*.csv)|*.csv", "hardware-onboarding-template.csv", CsvParser.Write(HardwareImportTemplate.Headers, rows));
            }
        }

        private void ExportFailed()
        {
            var path = Services.Dialogs.SaveFile("Excel workbook (*.xlsx)|*.xlsx|CSV file (*.csv)|*.csv", "onboarding-rows-to-fix.xlsx");
            if (path == null) return;
            if (HardwareImportFile.IsExcel(path)) _wf.WriteFailedRowsExcel(_results, path);
            else System.IO.File.WriteAllText(path, _wf.FailedRowsCsv(_results), new System.Text.UTF8Encoding(true));
        }

        private void ReviewWorkbook()
        {
            var recorder = string.IsNullOrEmpty(DefaultRecorder) ? null : Services.Session.Inventory.RecordingServers.FirstOrDefault(r => r.Name == DefaultRecorder);
            var user = Password != null && Password.Length > 0 ? UserName : null;
            var pwd = user != null ? Password.Copy() : null;
            var wf = Services.Workbooks;
            var preview = _workbook;
            var update = UpdateExisting;
            var plan = wf.BuildPlan(preview, recorder, user, pwd, update);
            Review = new ReviewViewModel(Services, plan, null,
                async (p, ct) =>
                {
                    var progress = new Progress<OperationEvent>(e =>
                    {
                        if (e.Percent.HasValue) p.Report(new BulkProgress { Completed = e.Percent.Value, Total = 100, Current = e.Activity ?? e.Message });
                    });
                    var (outcome, imported) = await wf.ApplyAsync(preview, recorder, user, pwd, update, progress, ct);
                    var rows = new List<BulkResultRow>
                    {
                        new BulkResultRow
                        {
                            Name = System.IO.Path.GetFileName(preview.Path),
                            Status = outcome.Success && !outcome.PartialFailure ? BulkItemStatus.Succeeded : BulkItemStatus.Failed,
                            Error = outcome.Error ?? (outcome.PartialFailure ? ErrorTranslator.Translate(outcome.Result.Errors[0], new OperationContext("import hardware", preview.Path)) : null),
                            Message = outcome.Success
                                ? $"MilestonePSTools finished the import ({outcome.Result.Errors.Count} error(s), {outcome.Result.Warnings.Count} warning(s)). Check the Activity panel for details."
                                : outcome.Error?.Summary
                        }
                    };
                    foreach (var w in outcome.Result?.Warnings ?? new List<string>()) rows.Add(new BulkResultRow { Name = "Warning", Status = BulkItemStatus.SucceededWithWarnings, Message = w });
                    foreach (var e in (outcome.Result?.Errors ?? new List<Protocol.ErrorInfo>()).Skip(outcome.PartialFailure ? 1 : 0))
                        rows.Add(new BulkResultRow { Name = "Error", Status = BulkItemStatus.Failed, Message = e.Message, Error = ErrorTranslator.Translate(e, new OperationContext("import hardware", preview.Path)) });
                    await Services.Inventory.RefreshAsync(CancellationToken.None);
                    return rows;
                },
                () => Review = null);
        }
    }
}
