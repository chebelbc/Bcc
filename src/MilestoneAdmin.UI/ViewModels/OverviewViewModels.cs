using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MilestoneAdmin.Application.Abstractions;
using MilestoneAdmin.Application.Navigation;
using MilestoneAdmin.Application.Services;
using MilestoneAdmin.Domain.Audit;
using MilestoneAdmin.Domain.Capabilities;
using MilestoneAdmin.Domain.Errors;
using MilestoneAdmin.PSTools;
using MilestoneAdmin.PSTools.Execution;
using MilestoneAdmin.UI.Mvvm;

namespace MilestoneAdmin.UI.ViewModels
{
    public sealed class WorkflowTile
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public bool Available { get; set; }
        public string Reason { get; set; }
        public bool IsFavorite { get; set; }
        public ICommand OpenCommand { get; set; }
        public ICommand ToggleFavoriteCommand { get; set; }
    }

    public sealed class DashboardViewModel : PageViewModel
    {
        private readonly ShellViewModel _shell;

        public DashboardViewModel(AppServices services, ShellViewModel shell)
            : base(services, "Dashboard")
        {
            _shell = shell;
            RefreshCommand = new AsyncCommand(RefreshAsync, () => IsConnected && !IsBusy);
            ConnectCommand = new RelayCommand(() => _shell.Navigate("Connect"));
        }

        public ICommand RefreshCommand { get; }
        public ICommand ConnectCommand { get; }

        public string ManagementServer => Services.Session.ManagementServer == null ? "—" : $"{Services.Session.ManagementServer.Name} ({Services.Session.ManagementServer.ComputerName})";
        public string XProtectVersion => Services.Session.ManagementServer?.Version is string v ? $"{v} ({VmsVersion.ToProductName(v)})" : "—";
        public string ModuleVersion => Services.Session.ModuleVersion ?? "—";
        public string Account => Services.Session.ConnectedAccount ?? "—";
        public string CatalogSource => Services.Session.Catalog == null ? "—" : $"{Services.Session.Catalog.Commands.Count} commands ({Services.Session.Catalog.Source})";
        public string RecordingServers => Count(Services.Session.Inventory.RecordingServers.Count);
        public string Hardware => Count(Services.Session.Inventory.Hardware.Count);
        public string Cameras => Count(Services.Session.Inventory.Cameras.Count);
        public string DisabledCameras => Count(Services.Session.Inventory.DisabledCameras);
        public string DisabledHardware => Count(Services.Session.Inventory.DisabledHardware);
        public string LoadedAt => Services.Session.Inventory.LoadedAt?.ToString("g", CultureInfo.CurrentCulture) ?? "not loaded";

        private string Count(int n) => Services.Session.Inventory.LoadedAt == null ? "—" : n.ToString(CultureInfo.CurrentCulture);

        public ObservableCollection<string> Warnings { get; } = new ObservableCollection<string>();
        public ObservableCollection<WorkflowTile> Favorites { get; } = new ObservableCollection<WorkflowTile>();
        public ObservableCollection<AuditRecord> RecentOperations { get; } = new ObservableCollection<AuditRecord>();

        public override async Task OnNavigatedToAsync()
        {
            Reload();
            if (IsConnected && Services.Session.Inventory.LoadedAt == null) await RefreshAsync();
        }

        public override void OnSessionChanged()
        {
            base.OnSessionChanged();
            Reload();
        }

        private async Task RefreshAsync()
        {
            await Busy("Retrieving the XProtect environment...", async () =>
            {
                var r = await Services.Inventory.RefreshAsync(CancellationToken.None);
                Error = r.Error;
            });
            Reload();
        }

        private void Reload()
        {
            RaiseAll();
            Warnings.Clear();
            var inv = Services.Session.Inventory;
            foreach (var r in inv.RecordingServers.Where(r => r.Enabled == false)) Warnings.Add($"Recording server {r.Name} is disabled.");
            if (inv.LoadedAt != null && inv.DisabledCameras > 0) Warnings.Add($"{inv.DisabledCameras} camera(s) are disabled.");
            foreach (var w in inv.Warnings.Take(10)) Warnings.Add(w);
            if (IsConnected)
                foreach (var c in Services.Session.Capabilities.Where(c => c.State == CapabilityState.Unavailable).Take(5)) Warnings.Add($"{c.Definition.Title}: {c.Reason}");
            if (!Services.Settings.MipSdkEulaAccepted) Warnings.Add("The Milestone MIP SDK license agreement has not been accepted in Settings; connecting will fail until it is.");

            Favorites.Clear();
            foreach (var id in Services.Settings.Favorites)
            {
                var w = WorkflowCatalog.Find(id);
                if (w == null) continue;
                var st = WorkflowCatalog.StatusFor(w, Services.Session);
                Favorites.Add(new WorkflowTile
                {
                    Id = w.Id,
                    Title = "★ " + w.Title,
                    Available = st.IsAvailable || !IsConnected,
                    Reason = st.IsAvailable ? w.Description : st.Reason,
                    OpenCommand = new RelayCommand(() => _shell.Navigate(w.Page))
                });
            }

            RecentOperations.Clear();
            try
            {
                foreach (var a in Services.Audit.ReadAll().Reverse().Take(8)) RecentOperations.Add(a);
            }
            catch (Exception)
            {
                // the audit view reports read problems
            }
        }
    }

    public sealed class ConnectViewModel : PageViewModel, ISecretSink
    {
        private readonly ShellViewModel _shell;
        private ConnectionProfile _selected;
        private string _profileName;
        private string _serverAddress = "http://";
        private ConnectionAuthMode _authMode = ConnectionAuthMode.CurrentWindowsUser;
        private string _userName;
        private bool _secureOnly;
        private bool _includeChildSites;
        private bool _rememberPassword;
        private string _status;

        public ConnectViewModel(AppServices services, ShellViewModel shell)
            : base(services, "Connect")
        {
            _shell = shell;
            foreach (var p in Services.Profiles.LoadAll()) Profiles.Add(p);
            ConnectCommand = new AsyncCommand(ConnectAsync, () => !IsBusy && Uri.TryCreate(ServerAddress, UriKind.Absolute, out _));
            DisconnectCommand = new AsyncCommand(async () => await Services.Connection.DisconnectAsync(), () => IsConnected && !IsBusy);
            SaveProfileCommand = new RelayCommand(SaveProfile, () => Uri.TryCreate(ServerAddress, UriKind.Absolute, out _));
            DeleteProfileCommand = new RelayCommand(DeleteProfile, () => Selected != null);
            NewProfileCommand = new RelayCommand(() => Selected = null);
            Selected = Profiles.FirstOrDefault(p => p.Id == Services.Settings.LastProfileId) ?? Profiles.FirstOrDefault();
        }

        public ObservableCollection<ConnectionProfile> Profiles { get; } = new ObservableCollection<ConnectionProfile>();

        public Array AuthModes => Enum.GetValues(typeof(ConnectionAuthMode));

        public ConnectionProfile Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                ProfileName = value?.Name;
                ServerAddress = value?.ServerAddress ?? "http://";
                AuthMode = value?.AuthMode ?? ConnectionAuthMode.CurrentWindowsUser;
                UserName = value?.UserName;
                SecureOnly = value?.SecureOnly ?? false;
                IncludeChildSites = value?.IncludeChildSites ?? false;
                RememberPassword = value?.RememberPassword ?? false;
                Password = null;
                PasswordStored = value?.RememberPassword == true;
                Raise(nameof(PasswordStored));
            }
        }

        public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }
        public string ServerAddress { get => _serverAddress; set => Set(ref _serverAddress, value); }

        public ConnectionAuthMode AuthMode
        {
            get => _authMode;
            set
            {
                if (Set(ref _authMode, value)) Raise(nameof(NeedsCredential));
            }
        }

        public bool NeedsCredential => AuthMode != ConnectionAuthMode.CurrentWindowsUser;
        public string UserName { get => _userName; set => Set(ref _userName, value); }
        public bool SecureOnly { get => _secureOnly; set => Set(ref _secureOnly, value); }
        public bool IncludeChildSites { get => _includeChildSites; set => Set(ref _includeChildSites, value); }
        public bool RememberPassword { get => _rememberPassword; set => Set(ref _rememberPassword, value); }
        public bool PasswordStored { get; private set; }

        /// <summary>Set from the PasswordBox (SecurePassword behavior) as a SecureString copy; never a plain string.</summary>
        public SecureString Password { get; set; }

        public void SetSecret(SecureString value) => Password = value;

        public string Status { get => _status; set => Set(ref _status, value); }

        public string EulaStatus => Services.Settings.MipSdkEulaAccepted
            ? "Milestone MIP SDK license agreement: accepted."
            : "Milestone MIP SDK license agreement: not accepted yet. Accept it in Settings before connecting.";

        public ICommand ConnectCommand { get; }
        public ICommand DisconnectCommand { get; }
        public ICommand SaveProfileCommand { get; }
        public ICommand DeleteProfileCommand { get; }
        public ICommand NewProfileCommand { get; }

        public override void OnSessionChanged()
        {
            base.OnSessionChanged();
            Raise(nameof(EulaStatus));
            Status = IsConnected ? $"Connected to {Services.Session.SystemName} (XProtect {Services.Session.ManagementServer?.Version})." : null;
        }

        private ConnectionProfile CurrentProfile()
        {
            var p = Selected ?? new ConnectionProfile();
            p.Name = string.IsNullOrWhiteSpace(ProfileName) ? new Uri(ServerAddress).Host : ProfileName.Trim();
            p.ServerAddress = ServerAddress.Trim();
            p.AuthMode = AuthMode;
            p.UserName = NeedsCredential ? UserName?.Trim() : null;
            p.SecureOnly = SecureOnly;
            p.IncludeChildSites = IncludeChildSites;
            p.RememberPassword = NeedsCredential && RememberPassword;
            return p;
        }

        private void SaveProfile()
        {
            var p = CurrentProfile();
            if (!Profiles.Contains(p)) Profiles.Add(p);
            if (p.RememberPassword && Password != null && Password.Length > 0) Services.Secrets.Save(p.SecretKey, p.UserName, Password);
            if (!p.RememberPassword) Services.Secrets.Delete(p.SecretKey);
            Services.Profiles.SaveAll(Profiles);
            Selected = p;
            Status = "Profile saved. Passwords are stored only in Windows Credential Manager.";
        }

        private void DeleteProfile()
        {
            var p = Selected;
            if (p == null || !Services.Dialogs.Confirm("Delete profile", $"Delete the connection profile '{p.Name}' and any password stored for it in Windows Credential Manager?")) return;
            Services.Secrets.Delete(p.SecretKey);
            Profiles.Remove(p);
            Services.Profiles.SaveAll(Profiles);
            Selected = Profiles.FirstOrDefault();
        }

        private async Task ConnectAsync()
        {
            Error = null;
            if (!Services.Settings.MipSdkEulaAccepted)
            {
                Services.Dialogs.ShowInfo("License agreement required", "MilestonePSTools requires accepting the Milestone MIP SDK end-user license agreement once per Windows user. Review and accept it in Settings > MilestonePSTools, then connect again.");
                _shell.Navigate("Settings");
                return;
            }

            var profile = CurrentProfile();
            var password = Password;
            if (NeedsCredential && (password == null || password.Length == 0) && profile.RememberPassword) password = Services.Secrets.Load(profile.SecretKey);
            if (NeedsCredential && (string.IsNullOrWhiteSpace(profile.UserName) || password == null || password.Length == 0))
            {
                Error = new AdminError { Kind = ErrorKind.InvalidInput, Summary = "Enter the user name and password for the selected authentication type." };
                return;
            }

            var request = new ConnectionRequest
            {
                ServerAddress = new Uri(profile.ServerAddress),
                AuthMode = profile.AuthMode,
                UserName = profile.UserName,
                Password = password,
                SecureOnly = profile.SecureOnly,
                IncludeChildSites = profile.IncludeChildSites
            };
            await Busy("Connecting...", async () =>
            {
                var r = await Services.Connection.ConnectAsync(request, Services.Settings, CancellationToken.None);
                if (!r.Success)
                {
                    Error = r.Error;
                    return;
                }

                Services.Settings.LastProfileId = profile.Id;
                if (Profiles.Contains(profile) || profile.RememberPassword)
                {
                    if (!Profiles.Contains(profile)) Profiles.Add(profile);
                    if (profile.RememberPassword && Password != null && Password.Length > 0) Services.Secrets.Save(profile.SecretKey, profile.UserName, Password);
                    Services.Profiles.SaveAll(Profiles);
                }

                Services.SaveSettings();
                Status = $"Connected to {r.Server?.Name}.";
                _shell.Navigate("Dashboard");
            });
        }
    }

    public sealed class SetupStep : ObservableObject
    {
        private bool _isCurrent;
        private bool _isDone;

        public int Number { get; set; }
        public string Title { get; set; }

        public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
        public bool IsDone { get => _isDone; set => Set(ref _isDone, value); }
    }

    /// <summary>
    /// First-run wizard. Checks the environment without loading the module, explains (but never silently performs)
    /// installation, captures telemetry and EULA decisions, then hands over to Connect.
    /// </summary>
    public sealed class SetupWizardViewModel : PageViewModel
    {
        public const string InstallCommand = "Install-Module -Name MilestonePSTools -Scope CurrentUser";
        public const string UpdateCommand = "Update-Module -Name MilestonePSTools";

        private readonly ShellViewModel _shell;
        private int _step = 1;
        private bool _eulaAccepted;
        private bool _disableTelemetry = true;

        public SetupWizardViewModel(AppServices services, ShellViewModel shell)
            : base(services, "Welcome")
        {
            _shell = shell;
            _eulaAccepted = services.Settings.MipSdkEulaAccepted;
            _disableTelemetry = services.Settings.Telemetry == TelemetryChoice.DisableForApplication;
            var titles = new[] { "Environment check", "Windows PowerShell", "MilestonePSTools", "Install or update", "Privacy and license", "Connect to XProtect", "Test authentication", "Retrieve environment" };
            for (var i = 0; i < titles.Length; i++) Steps.Add(new SetupStep { Number = i + 1, Title = titles[i] });
            RunChecksCommand = new AsyncCommand(RunChecksAsync, () => !IsBusy);
            NextCommand = new AsyncCommand(NextAsync, () => !IsBusy && CanGoNext);
            BackCommand = new RelayCommand(() => Step = Math.Max(1, Step - 1), () => Step > 1 && !IsBusy);
            CopyInstallCommand = new RelayCommand(() => Services.Dialogs.CopyToClipboard(InstallCommand));
            OpenPowerShellCommand = new RelayCommand(OpenPowerShell);
            SkipCommand = new RelayCommand(Finish);
            UpdateSteps();
        }

        public ObservableCollection<SetupStep> Steps { get; } = new ObservableCollection<SetupStep>();
        public ObservableCollection<PrecheckItem> Checks { get; } = new ObservableCollection<PrecheckItem>();

        public int Step
        {
            get => _step;
            set
            {
                if (Set(ref _step, value)) UpdateSteps();
            }
        }

        public bool ModuleInstalled { get; private set; }
        public string InstalledVersions { get; private set; }
        public string InstallCommandText => InstallCommand;
        public string UpdateCommandText => UpdateCommand;

        public bool EulaAccepted { get => _eulaAccepted; set => Set(ref _eulaAccepted, value); }
        public bool DisableTelemetry { get => _disableTelemetry; set => Set(ref _disableTelemetry, value); }

        public bool CanGoNext => Step != 3 || ModuleInstalled;

        public ICommand RunChecksCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand CopyInstallCommand { get; }
        public ICommand OpenPowerShellCommand { get; }
        public ICommand SkipCommand { get; }

        private void UpdateSteps()
        {
            foreach (var s in Steps)
            {
                s.IsCurrent = s.Number == Step;
                s.IsDone = s.Number < Step;
            }

            for (var i = 1; i <= 8; i++) Raise("IsStep" + i);
            Raise(nameof(CanGoNext));
        }

        public bool IsStep1 => Step == 1;
        public bool IsStep2 => Step == 2;
        public bool IsStep3 => Step == 3;
        public bool IsStep4 => Step == 4;
        public bool IsStep5 => Step == 5;
        public bool IsStep6 => Step == 6;
        public bool IsStep7 => Step == 7;
        public bool IsStep8 => Step == 8;

        public override Task OnNavigatedToAsync() => Checks.Count == 0 ? RunChecksAsync() : Task.CompletedTask;

        private async Task RunChecksAsync()
        {
            await Busy("Checking Windows PowerShell and MilestonePSTools (without loading the module)...", async () =>
            {
                Checks.Clear();
                try
                {
                    var opts = Services.ExecutorFactory.CreateOptions(Services.Settings);
                    var env = await WorkerProcessExecutor.ProbeEnvironmentAsync(opts, CancellationToken.None);
                    var report = Services.Environment.CheckEnvironment(env, null);
                    foreach (var c in report.Items) Checks.Add(c);
                    var versions = (env.GetArray("installedModuleVersions") ?? new Protocol.Json.JsonArray()).Select(v => v.AsObject()?.GetString("version")).Where(v => v != null).ToList();
                    ModuleInstalled = versions.Count > 0;
                    InstalledVersions = ModuleInstalled ? string.Join(", ", versions) : "not installed";
                }
                catch (Exception ex)
                {
                    Error = Application.Errors.ErrorTranslator.FromException(ex, new Application.Errors.OperationContext("check the environment"));
                    Checks.Add(PrecheckItem.Fail("Windows PowerShell worker", ex.Message, "Make sure Windows PowerShell 5.1 and .NET Framework 4.8 are installed, then reinstall the application."));
                }

                Raise(nameof(ModuleInstalled));
                Raise(nameof(InstalledVersions));
                Raise(nameof(CanGoNext));
            });
        }

        private void OpenPowerShell()
        {
            // Explicit, visible user action: after confirmation, opens a normal Windows PowerShell window that runs the
            // install command shown in the dialog. PowerShell itself asks about the PSGallery repository / NuGet provider
            // if needed; the user answers those prompts in that window.
            if (!Services.Dialogs.Confirm("Install MilestonePSTools",
                    "A Windows PowerShell window will open and run:\n\n" + InstallCommand + "\n\n" +
                    "PowerShell may ask you to install the NuGet provider and to trust the PowerShell Gallery repository. " +
                    "Read those prompts before answering; this application does not answer them for you. " +
                    "No administrator rights are needed for -Scope CurrentUser.")) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoExit -NoProfile -Command \"" + InstallCommand + "\"",
                UseShellExecute = true
            });
        }

        private async Task NextAsync()
        {
            switch (Step)
            {
                case 3 when !ModuleInstalled:
                    return;
                case 5:
                    Services.Settings.MipSdkEulaAccepted = EulaAccepted;
                    Services.Settings.Telemetry = DisableTelemetry ? TelemetryChoice.DisableForApplication : TelemetryChoice.FollowModuleSetting;
                    Services.SaveSettings();
                    break;
                case 6:
                    Services.Settings.SetupCompleted = true;
                    Services.SaveSettings();
                    _shell.Navigate("Connect");
                    return;
            }

            if (Step == 2 || Step == 3) await RunChecksAsync();
            Step = Math.Min(8, Step + 1);
        }

        private void Finish()
        {
            Services.Settings.SetupCompleted = true;
            Services.SaveSettings();
            _shell.Navigate(IsConnected ? "Dashboard" : "Connect");
        }
    }
}
