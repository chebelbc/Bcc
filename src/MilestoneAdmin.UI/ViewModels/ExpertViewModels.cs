using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using MilestoneAdmin.Application.Abstractions;
using MilestoneAdmin.Application.Activity;
using MilestoneAdmin.Application.CommandExplorer;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.Domain.Audit;
using MilestoneAdmin.Domain.Catalog;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Domain.Csv;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.Protocol.Json;
using MilestoneAdmin.PSTools.Mapping;
using MilestoneAdmin.UI.Mvvm;

namespace MilestoneAdmin.UI.ViewModels
{
    /// <summary>Receives SecureString values from a PasswordBox through <see cref="Views.SecurePassword"/>.</summary>
    public interface ISecretSink
    {
        void SetSecret(SecureString value);
    }

    public sealed class CommandRow
    {
        public CatalogCommand Command { get; set; }
        public string Name => Command.Name;
        public string Category => Command.CategoryLabel;
        public RiskLevel Risk => Command.Risk.Level;
        public string RiskText => $"{Command.Risk.Level} ({Command.Risk.Matrix})";
        public string Suitability => Command.GuiSuitability.ToString();
        public string Synopsis => Command.Synopsis;
        public bool WhatIf => Command.SupportsShouldProcess;
    }

    public sealed class ObjectOption : ObservableObject
    {
        private bool _isChecked;

        public VmsObject Object { get; set; }
        public string Label { get; set; }
        public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
    }

    /// <summary>
    /// View model for one generated form control. Object and Id parameters are pickers: their choices are loaded from the
    /// connected system (<see cref="LoadOptionsAsync"/>), so nobody types names or GUIDs.
    /// </summary>
    public sealed class FieldViewModel : ObservableObject, ISecretSink
    {
        private readonly bool _showSyntax;
        private string _optionFilter;
        private string _optionsStatus;

        public FieldViewModel(FormField field, bool showSyntax)
        {
            Field = field;
            _showSyntax = showSyntax;
            foreach (var v in field.AcceptedValues) Options.Add(new ObjectOption { Label = v });
            ObjectOptionsView = CollectionViewSource.GetDefaultView(ObjectOptions);
            ObjectOptionsView.Filter = o => string.IsNullOrWhiteSpace(OptionFilter) || ((ObjectOption)o).IsChecked ||
                                            ((ObjectOption)o).Label.IndexOf(OptionFilter, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        public FormField Field { get; }
        public string Label => Field.Label + (Field.Mandatory ? " *" : string.Empty);

        /// <summary>Plain description; the PowerShell parameter syntax is shown in Expert Mode only.</summary>
        public string Help
        {
            get
            {
                var desc = string.IsNullOrWhiteSpace(Field.Description) ? string.Empty : Field.Description.Replace("\n", " ");
                return _showSyntax ? $"-{Field.Name} <{Field.TypeName}>" + (desc.Length > 0 ? " · " + desc : string.Empty) : desc;
            }
        }

        public ControlKind Control => Field.Control;
        public ObservableCollection<ObjectOption> ObjectOptions { get; } = new ObservableCollection<ObjectOption>();
        public ICollectionView ObjectOptionsView { get; }
        public ObservableCollection<ObjectOption> Options { get; } = new ObservableCollection<ObjectOption>();
        public bool IsPicker => Field.ObjectTypes.Count > 0;

        public string ObjectTypesText => _optionsStatus ?? (Field.ObjectTypes.Count == 0 ? string.Empty : "Choose: " + string.Join(", ", Field.ObjectTypes.Select(Friendly)));

        /// <summary>Search text for long object lists (cameras, users...). Checked items stay visible.</summary>
        public string OptionFilter
        {
            get => _optionFilter;
            set
            {
                if (Set(ref _optionFilter, value)) ObjectOptionsView.Refresh();
            }
        }

        public string Text { get => Field.Text; set { Field.Text = value; Raise(); } }
        public bool? Flag { get => Field.Flag; set { Field.Flag = value; Raise(); } }
        public DateTime? Date { get => Field.Date; set { Field.Date = value; Raise(); } }
        public string UserName { get => Field.UserName; set { Field.UserName = value; Raise(); } }

        public ObjectOption SelectedObject
        {
            get => ObjectOptions.FirstOrDefault(o => o.IsChecked);
            set
            {
                foreach (var o in ObjectOptions) o.IsChecked = o == value;
                Raise();
            }
        }

        public void SetSecret(SecureString value) => Field.Secret = value;

        private static string Friendly(string type) =>
            ClassificationRules.Embedded.ObjectTypes.TryGetValue(type, out var d) && !string.IsNullOrEmpty(d.Label) ? d.Label : CommandFormBuilder.Humanize(type);

        private static string LabelFor(VmsObject o) =>
            o is CameraInfo c ? $"{c.Name} ({c.HardwareName})" : o is HardwareInfo h ? $"{h.Name} ({h.Address})" : o is StorageInfo st ? $"{st.Name} ({st.RecordingServerName})" : o.Name;

        /// <summary>Fills the picker with the objects of the field's types from the connected system.</summary>
        public async Task LoadOptionsAsync(Application.Services.ObjectLookupService lookup)
        {
            if (!IsPicker) return;
            _optionsStatus = "Loading choices...";
            Raise(nameof(ObjectTypesText));
            var problems = new List<string>();
            var keep = new HashSet<VmsObject>(Field.Objects);
            ObjectOptions.Clear();
            foreach (var t in Field.ObjectTypes)
            {
                var (items, problem) = await lookup.GetAsync(t, CancellationToken.None);
                if (problem != null) problems.Add(problem);
                foreach (var o in items) ObjectOptions.Add(new ObjectOption { Object = o, Label = LabelFor(o), IsChecked = keep.Contains(o) });
            }

            _optionsStatus = ObjectOptions.Count == 0
                ? (problems.Count > 0 ? string.Join(" ", problems.Distinct()) : "Nothing to choose from on this system.")
                : null;
            Raise(nameof(ObjectTypesText));
            Raise(nameof(SelectedObject));
        }

        /// <summary>Copy selections from the option lists into the form field before validation.</summary>
        public void Commit()
        {
            Field.Objects.Clear();
            Field.Objects.AddRange(ObjectOptions.Where(o => o.IsChecked).Select(o => o.Object));
            Field.SelectedValues.Clear();
            Field.SelectedValues.AddRange(Options.Where(o => o.IsChecked).Select(o => o.Label));
        }
    }

    public sealed class CommandExplorerViewModel : PageViewModel
    {
        private string _searchText;
        private string _category = "All categories";
        private string _riskFilter = "All";
        private CommandRow _selected;
        private CatalogParameterSet _selectedSet;
        private CommandForm _form;
        private string _script;
        private DataView _resultData;
        private string _resultText;
        private string _validation;

        public CommandExplorerViewModel(AppServices services)
            : base(services, "Command Explorer")
        {
            View = CollectionViewSource.GetDefaultView(Commands);
            View.Filter = o => Matches((CommandRow)o);
            PreviewScriptCommand = new RelayCommand(UpdateScript, () => Form != null);
            CopyScriptCommand = new RelayCommand(() => { UpdateScript(); Services.Dialogs.CopyToClipboard(Script); }, () => Form != null);
            ExportScriptCommand = new RelayCommand(ExportScript, () => Form != null);
            RunCommand = new AsyncCommand(() => RunAsync(false), () => Form != null && Form.ExecutionBlockedReason == null && IsConnected && !IsBusy);
            WhatIfCommand = new AsyncCommand(() => RunAsync(true), () => Form != null && Form.ExecutionBlockedReason == null && IsConnected && !IsBusy && Selected?.Command.SupportsShouldProcess == true);
            OpenHelpCommand = new RelayCommand(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Selected.Command.HelpUri) { UseShellExecute = true }),
                () => Uri.TryCreate(Selected?.Command.HelpUri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp));
        }

        public ObservableCollection<CommandRow> Commands { get; } = new ObservableCollection<CommandRow>();
        public ICollectionView View { get; }
        public ObservableCollection<string> Categories { get; } = new ObservableCollection<string>();
        public string[] RiskFilters { get; } = { "All", "Read", "Write", "Delete" };
        public ObservableCollection<FieldViewModel> Fields { get; } = new ObservableCollection<FieldViewModel>();

        public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) View.Refresh(); } }
        public string Category { get => _category; set { if (Set(ref _category, value)) View.Refresh(); } }
        public string RiskFilter { get => _riskFilter; set { if (Set(ref _riskFilter, value)) View.Refresh(); } }

        public string CatalogInfo => Services.Session.Catalog == null
            ? "No catalog loaded. Connect (runtime discovery) or the shipped catalog will be shown."
            : $"{Services.Session.Catalog.Commands.Count} commands · MilestonePSTools {Services.Session.Catalog.ModuleVersion} · {(Services.Session.Catalog.Source == CatalogSourceKind.Runtime ? "discovered from the installed module" : "catalog shipped with the application (not necessarily your installed version)")}";

        public CommandRow Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                Raise(nameof(Details));
                Raise(nameof(Requirements));
                Raise(nameof(Examples));
                Raise(nameof(ParameterSets));
                SelectedSet = value?.Command.DefaultParameterSet;
                if (value?.Command.ParameterSets.Count == 0) BuildForm();
            }
        }

        public IEnumerable<CatalogParameterSet> ParameterSets => Selected?.Command.ParameterSets ?? Enumerable.Empty<CatalogParameterSet>();

        public CatalogParameterSet SelectedSet
        {
            get => _selectedSet;
            set
            {
                Set(ref _selectedSet, value);
                BuildForm();
            }
        }

        public CommandForm Form { get => _form; private set => Set(ref _form, value); }

        public string Details
        {
            get
            {
                var c = Selected?.Command;
                if (c == null) return null;
                return $"{c.Synopsis}\n\n{c.Description}".Trim();
            }
        }

        public string Requirements
        {
            get
            {
                var c = Selected?.Command;
                if (c == null) return null;
                var lines = new List<string>
                {
                    $"Risk: {c.Risk.Level} ({c.Risk.Matrix}) - {c.Risk.Reason}",
                    $"Category: {c.CategoryLabel}",
                    $"PowerShell -WhatIf: {(c.SupportsShouldProcess ? "supported" : "not supported")}" + (c.ConfirmImpact != null ? $", ConfirmImpact {c.ConfirmImpact}" : string.Empty),
                    $"GUI: {c.GuiSuitability} - {string.Join("; ", c.GuiSuitabilityReasons)}"
                };
                if (c.Requirements.MinVmsVersion != null) lines.Add($"Minimum XProtect version: {c.Requirements.MinVmsVersion}");
                if (c.Requirements.VmsFeatures.Count > 0) lines.Add("Requires licensed feature(s): " + string.Join(", ", c.Requirements.VmsFeatures));
                if (c.Requirements.Elevation) lines.Add("Requires local Administrator elevation.");
                if (c.Aliases.Count > 0) lines.Add("Aliases: " + string.Join(", ", c.Aliases));
                if (c.Deprecated) lines.Add("Deprecated; use " + c.ReplacedBy);
                if (c.TypeDependencies.Count > 0) lines.Add("Objects come from: " + string.Join(", ", c.TypeDependencies.Select(d => $"-{d.Parameter} ← {d.PreferredProducer}").Distinct()));
                return string.Join("\n", lines);
            }
        }

        public IEnumerable<CommandExample> Examples => Selected?.Command.Examples ?? Enumerable.Empty<CommandExample>();

        public string Script { get => _script; private set => Set(ref _script, value); }
        public string Validation { get => _validation; private set => Set(ref _validation, value); }
        public DataView ResultData { get => _resultData; private set => Set(ref _resultData, value); }
        public string ResultText { get => _resultText; private set => Set(ref _resultText, value); }

        public ICommand PreviewScriptCommand { get; }
        public ICommand CopyScriptCommand { get; }
        public ICommand ExportScriptCommand { get; }
        public ICommand RunCommand { get; }
        public ICommand WhatIfCommand { get; }
        public ICommand OpenHelpCommand { get; }

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
            var catalog = Services.Session.Catalog ?? Services.Catalogs.LoadStatic();
            if (catalog == null || (Commands.Count > 0 && Commands[0].Command == catalog.Commands.FirstOrDefault())) return;
            Commands.Clear();
            foreach (var c in catalog.Commands) Commands.Add(new CommandRow { Command = c });
            Categories.Clear();
            Categories.Add("All categories");
            foreach (var cat in catalog.Commands.Select(c => c.CategoryLabel).Distinct().OrderBy(x => x)) Categories.Add(cat);
            Raise(nameof(CatalogInfo));
        }

        private bool Matches(CommandRow r) =>
            (Category == "All categories" || r.Category == Category) &&
            (RiskFilter == "All" || r.Command.Risk.Matrix == RiskFilter) &&
            (string.IsNullOrWhiteSpace(SearchText) || r.Name.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
             r.Command.Aliases.Any(a => a.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
             (SearchText.Length >= 4 && r.Synopsis.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0));

        private void BuildForm()
        {
            Fields.Clear();
            Script = null;
            Validation = null;
            if (Selected == null)
            {
                Form = null;
                return;
            }

            Form = CommandFormBuilder.Build(Selected.Command, SelectedSet, ExpertMode);
            foreach (var f in Form.Fields) Fields.Add(new FieldViewModel(f, ExpertMode));
            UpdateScript();
            foreach (var f in Fields) _ = f.LoadOptionsAsync(Services.Lookup);
        }

        private void Commit()
        {
            foreach (var f in Fields) f.Commit();
        }

        private void UpdateScript()
        {
            if (Form == null) return;
            Commit();
            try
            {
                Script = Services.Scripts.Fragment(CommandFormBuilder.BuildOperation(Form, whatIf: false));
            }
            catch (Exception ex)
            {
                Script = "# " + ex.Message;
            }
        }

        private void ExportScript()
        {
            Commit();
            var op = CommandFormBuilder.BuildOperation(Form, whatIf: false);
            Services.Dialogs.SaveText("PowerShell script (*.ps1)|*.ps1", Selected.Name + ".ps1", Services.Scripts.Script(new[] { op }));
        }

        private async Task RunAsync(bool whatIf)
        {
            Commit();
            var errors = CommandFormBuilder.Validate(Form);
            Validation = errors.Count == 0 ? null : string.Join("\n", errors);
            if (errors.Count > 0) return;
            var c = Selected.Command;
            var op = CommandFormBuilder.BuildOperation(Form, whatIf);
            var script = Services.Scripts.Fragment(op);
            if (!whatIf && !c.Risk.IsReadOnly)
            {
                var message = $"Risk: {c.Risk.Level} - {c.Risk.Definition?.Description}\n\nThe application will run:\n\n{script}";
                var confirmed = c.Risk.RequiresStrongConfirmation
                    ? Services.Dialogs.ConfirmStrong("Confirm " + c.Name, message, c.Name)
                    : Services.Dialogs.Confirm("Confirm " + c.Name, message);
                if (!confirmed) return;
            }

            await Busy($"Running {c.Name}{(whatIf ? " -WhatIf" : string.Empty)}...", async () =>
            {
                var outcome = await Services.Runner.RunAsync(op, new OperationContext("run " + c.Name, op.Target));
                Error = outcome.Error;
                var objects = outcome.Result.Objects.ToList();
                var lines = new List<string>();
                if (whatIf) lines.AddRange(outcome.Result.WhatIfLines.DefaultIfEmpty("(MilestonePSTools reported no actions.)"));
                lines.AddRange(outcome.Result.Items.Where(i => !(i is JsonObject)).Select(i => c.Risk.Level == RiskLevel.Sensitive ? "(value hidden in this list; see the dialog)" : i.AsString() ?? i.ToJson()));
                lines.AddRange(outcome.Result.Errors.Select(e => "Error: " + e.Message));
                ResultText = lines.Count == 0 ? $"{objects.Count} object(s) returned." : string.Join("\n", lines);
                ResultData = objects.Count == 0 ? null : GenericTable.ToDataView(objects.Select(o => DtoMapper.Generic(o, null)));
                if (c.Risk.Level == RiskLevel.Sensitive && outcome.Success)
                    Services.Dialogs.ShowInfo(c.Name, string.Join("\n", outcome.Result.Items.Where(i => !(i is JsonObject)).Select(i => i.AsString())));
                if (objects.Count > 0) Services.Session.Register(objects.Select(o => (VmsObject)DtoMapper.Generic(o, null)));
            });
        }
    }

    public sealed class ActivityViewModel : PageViewModel
    {
        public ActivityViewModel(AppServices services, ShellViewModel shell)
            : base(services, "Activity Log")
        {
            Entries = shell.Activity;
            ClearCommand = new RelayCommand(() =>
            {
                Services.Activity.Clear();
                Entries.Clear();
            });
            CopyCommand = new RelayCommand(() => Services.Dialogs.CopyToClipboard(string.Join(Environment.NewLine, Entries.Select(e => e.ToString()))));
        }

        public ObservableCollection<ActivityEntry> Entries { get; }
        public string DiagnosticsFile => Services.Diagnostics.File;
        public ICommand ClearCommand { get; }
        public ICommand CopyCommand { get; }
    }

    public sealed class AuditViewModel : PageViewModel
    {
        private string _integrity;

        public AuditViewModel(AppServices services)
            : base(services, "Audit Log")
        {
            RefreshCommand = new RelayCommand(Load);
            VerifyCommand = new RelayCommand(() =>
            {
                var broken = Services.Audit.Verify();
                Integrity = broken == null ? "✓ Hash chain intact: no record has been modified or removed." : $"✗ Hash chain broken at record {broken}. The audit file was modified outside the application.";
            });
            ExportCsvCommand = new RelayCommand(() => Services.Dialogs.SaveText("CSV file (*.csv)|*.csv", "xprotect-admin-audit.csv",
                CsvParser.Write(new[] { "Sequence", "Timestamp", "WindowsUser", "Workstation", "XProtectSystem", "XProtectUser", "Operation", "TargetType", "Target", "Result", "DurationMs", "Error", "Command", "ModuleVersion", "Hash" },
                    Records.Select(r => (IReadOnlyList<string>)new[] { r.Sequence.ToString(CultureInfo.InvariantCulture), r.Timestamp.ToString("o", CultureInfo.InvariantCulture), r.WindowsUser, r.Workstation, r.XProtectSystem, r.XProtectUser, r.Operation, r.TargetType, r.Target, r.Result.ToString(), r.DurationMs.ToString(CultureInfo.InvariantCulture), r.Error, r.SanitizedCommand, r.ModuleVersion, r.Hash }))));
            ExportJsonCommand = new RelayCommand(() => Services.Dialogs.SaveText("JSON file (*.json)|*.json", "xprotect-admin-audit.json", new JsonArray(Records.Select(r => (JsonNode)r.ToJson())).ToJson(true)));
            OpenFolderCommand = new RelayCommand(() => System.Diagnostics.Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(Services.Audit.Location) + "\""));
        }

        public ObservableCollection<AuditRecord> Records { get; } = new ObservableCollection<AuditRecord>();
        public string Location => Services.Audit.Location;
        public string Integrity { get => _integrity; set => Set(ref _integrity, value); }
        public ICommand RefreshCommand { get; }
        public ICommand VerifyCommand { get; }
        public ICommand ExportCsvCommand { get; }
        public ICommand ExportJsonCommand { get; }
        public ICommand OpenFolderCommand { get; }

        public override Task OnNavigatedToAsync()
        {
            Load();
            return Task.CompletedTask;
        }

        private void Load()
        {
            Records.Clear();
            try
            {
                foreach (var r in Services.Audit.ReadAll().Reverse()) Records.Add(r);
            }
            catch (Exception ex)
            {
                Integrity = "The audit log could not be read: " + ex.Message;
            }
        }
    }

    public sealed class SettingsViewModel : PageViewModel
    {
        private readonly ShellViewModel _shell;
        private string _message;

        public SettingsViewModel(AppServices services, ShellViewModel shell)
            : base(services, "Settings")
        {
            _shell = shell;
            SaveCommand = new RelayCommand(Save);
            SetupCommand = new RelayCommand(() => _shell.Navigate("Setup"));
            ClearCacheCommand = new RelayCommand(() =>
            {
                foreach (var f in Directory.GetFiles(Services.Paths.CatalogCacheDirectory, "catalog-*.json")) File.Delete(f);
                Message = "Command catalog cache cleared. Commands are rediscovered at the next connection.";
            });
            BrowseModuleCommand = new RelayCommand(() => ModulePath = Services.Dialogs.OpenFile("PowerShell module manifest (*.psd1)|*.psd1") ?? ModulePath);
        }

        public bool Expert { get => Services.Settings.ExpertMode; set { Services.Settings.ExpertMode = value; Raise(); } }
        public bool CaptureVerbose { get => Services.Settings.CaptureVerbose; set { Services.Settings.CaptureVerbose = value; Raise(); } }
        public int TimeoutSeconds { get => Services.Settings.OperationTimeoutSeconds; set { Services.Settings.OperationTimeoutSeconds = Math.Max(30, value); Raise(); } }
        public string ModulePath { get => Services.Settings.ModulePath; set { Services.Settings.ModulePath = value; Raise(); } }
        public bool DisableTelemetry { get => Services.Settings.Telemetry == TelemetryChoice.DisableForApplication; set { Services.Settings.Telemetry = value ? TelemetryChoice.DisableForApplication : TelemetryChoice.FollowModuleSetting; Raise(); } }
        public bool EulaAccepted { get => Services.Settings.MipSdkEulaAccepted; set { Services.Settings.MipSdkEulaAccepted = value; Raise(); } }
        public string Message { get => _message; set => Set(ref _message, value); }

        public string Paths => $"Settings and profiles: {Services.Paths.Roaming}\nAudit log, catalog cache, diagnostics: {Services.Paths.Local}\nPasswords: Windows Credential Manager (only when 'Remember password' is chosen).";

        public string About => $"XProtect Admin Assistant {typeof(SettingsViewModel).Assembly.GetName().Version} - an independent administration tool built on MilestonePSTools.\n" +
                               "Not an official Milestone Systems product. XProtect is a registered trademark of Milestone Systems A/S.\n" +
                               "MilestonePSTools is licensed by Milestone Systems under the Apache License 2.0 and is installed separately. " +
                               "The Milestone MIP SDK it loads is subject to the Milestone MIP SDK end-user license agreement.";

        public ICommand SaveCommand { get; }
        public ICommand SetupCommand { get; }
        public ICommand ClearCacheCommand { get; }
        public ICommand BrowseModuleCommand { get; }

        private void Save()
        {
            Services.SaveSettings();
            _shell.ExpertMode = Services.Settings.ExpertMode;
            Message = "Settings saved. Telemetry, module path and license changes apply to the next connection.";
        }
    }
}
