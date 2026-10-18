using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using MilestoneAdmin.Application.Activity;
using MilestoneAdmin.Application.Navigation;
using MilestoneAdmin.Domain.Capabilities;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.UI.Mvvm;

namespace MilestoneAdmin.UI.ViewModels
{
    public sealed class NavItem : ObservableObject
    {
        private bool _isSelected;

        public string Key { get; set; }
        public string Title { get; set; }
        public string Icon { get; set; }
        public string Group { get; set; }
        public bool ExpertOnly { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }
    }

    public sealed class ShellViewModel : ObservableObject
    {
        private readonly Dictionary<string, PageViewModel> _pages = new Dictionary<string, PageViewModel>();
        private readonly AppServices _services;
        private PageViewModel _current;
        private string _searchText;
        private bool _activityExpanded = true;
        private SearchResult _selectedResult;

        public ShellViewModel(AppServices services)
        {
            _services = services;
            Nav = new ObservableCollection<NavItem>
            {
                new NavItem { Key = "Dashboard", Title = "Dashboard", Icon = "⌂", Group = "Overview" },
                new NavItem { Key = "Connect", Title = "Connect", Icon = "⇄", Group = "Overview" },
                new NavItem { Key = "System", Title = "System", Icon = "▣", Group = "Configuration" },
                new NavItem { Key = "RecordingServers", Title = "Recording Servers", Icon = "▤", Group = "Configuration" },
                new NavItem { Key = "Hardware", Title = "Hardware", Icon = "⛭", Group = "Configuration" },
                new NavItem { Key = "Cameras", Title = "Cameras & Devices", Icon = "◉", Group = "Configuration" },
                new NavItem { Key = "Storage", Title = "Storage", Icon = "▦", Group = "Configuration" },
                new NavItem { Key = "Security", Title = "Users & Roles", Icon = "☺", Group = "Configuration" },
                new NavItem { Key = "Rules", Title = "Rules & Events", Icon = "⚑", Group = "Configuration" },
                new NavItem { Key = "SmartClient", Title = "Smart Client", Icon = "▭", Group = "Configuration" },
                new NavItem { Key = "Maintenance", Title = "Maintenance", Icon = "✚", Group = "Operations" },
                new NavItem { Key = "Reports", Title = "Reports", Icon = "≣", Group = "Operations" },
                new NavItem { Key = "Automation", Title = "Automation", Icon = "↻", Group = "Operations" },
                new NavItem { Key = "CommandExplorer", Title = "Command Explorer", Icon = "›_", Group = "Expert", ExpertOnly = false },
                new NavItem { Key = "Activity", Title = "Activity Log", Icon = "☰", Group = "Expert" },
                new NavItem { Key = "Audit", Title = "Audit Log", Icon = "✓", Group = "Expert" },
                new NavItem { Key = "Settings", Title = "Settings", Icon = "⚙", Group = "Expert" }
            };
            NavigateCommand = new RelayCommand(o => Navigate(o as string ?? (o as NavItem)?.Key));
            ToggleActivityCommand = new RelayCommand(() => ActivityExpanded = !ActivityExpanded);
            ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
            DisconnectCommand = new AsyncCommand(async () => await _services.Connection.DisconnectAsync(), () => _services.Session.IsConnected);
            OpenResultCommand = new RelayCommand(o => OpenResult(o as SearchResult));

            foreach (var e in _services.Activity.Entries.Skip(Math.Max(0, _services.Activity.Entries.Count - 500))) Activity.Add(e);
            _services.Activity.EntryAdded += (s, e) => Ui.Post(() =>
            {
                Activity.Add(e);
                while (Activity.Count > 1000) Activity.RemoveAt(0);
                LastActivity = e;
                Raise(nameof(LastActivity));
            });
            _services.Session.Changed += (s, e) => Ui.Post(OnSessionChanged);
            Navigate(_services.Settings.SetupCompleted ? "Dashboard" : "Setup");
        }

        public ObservableCollection<NavItem> Nav { get; }

        public ObservableCollection<ActivityEntry> Activity { get; } = new ObservableCollection<ActivityEntry>();

        public ObservableCollection<SearchResult> SearchResults { get; } = new ObservableCollection<SearchResult>();

        public ActivityEntry LastActivity { get; private set; }

        public PageViewModel Current
        {
            get => _current;
            private set => Set(ref _current, value);
        }

        public bool IsConnected => _services.Session.IsConnected;

        public string ConnectionText => _services.Session.IsConnected
            ? $"Connected: {_services.Session.SystemName} · XProtect {_services.Session.ManagementServer?.Version} · {_services.Session.ConnectedAccount}"
            : "Not connected";

        public string ModuleText => _services.Session.ModuleVersion != null ? "MilestonePSTools " + _services.Session.ModuleVersion : "MilestonePSTools not loaded";

        public bool ExpertMode
        {
            get => _services.Settings.ExpertMode;
            set
            {
                if (_services.Settings.ExpertMode == value) return;
                _services.Settings.ExpertMode = value;
                _services.SaveSettings();
                Raise();
                foreach (var p in _pages.Values) p.OnSessionChanged();
            }
        }

        public bool ActivityExpanded
        {
            get => _activityExpanded;
            set => Set(ref _activityExpanded, value);
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (!Set(ref _searchText, value)) return;
                SearchResults.Clear();
                foreach (var r in _services.Search.Search(value, ExpertMode, 30)) SearchResults.Add(r);
                Raise(nameof(HasSearchResults));
            }
        }

        public bool HasSearchResults => SearchResults.Count > 0;

        public SearchResult SelectedResult
        {
            get => _selectedResult;
            set
            {
                if (Set(ref _selectedResult, value) && value != null) OpenResult(value);
            }
        }

        public ICommand NavigateCommand { get; }
        public ICommand ToggleActivityCommand { get; }
        public ICommand ClearSearchCommand { get; }
        public ICommand DisconnectCommand { get; }
        public ICommand OpenResultCommand { get; }

        private void OnSessionChanged()
        {
            Raise(nameof(IsConnected));
            Raise(nameof(ConnectionText));
            Raise(nameof(ModuleText));
            foreach (var p in _pages.Values) p.OnSessionChanged();
            CommandManager.InvalidateRequerySuggested();
        }

        public void Navigate(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!_pages.TryGetValue(key, out var page))
            {
                page = Create(key);
                if (page == null) return;
                _pages[key] = page;
            }

            foreach (var n in Nav) n.IsSelected = n.Key == key;
            Current = page;
            _ = page.OnNavigatedToAsync();
        }

        public T Page<T>(string key) where T : PageViewModel
        {
            Navigate(key);
            return Current as T;
        }

        private PageViewModel Create(string key)
        {
            switch (key)
            {
                case "Setup": return new SetupWizardViewModel(_services, this);
                case "Dashboard": return new DashboardViewModel(_services, this);
                case "Connect": return new ConnectViewModel(_services, this);
                case "System": return new SystemViewModel(_services);
                case "RecordingServers": return new RecordingServersViewModel(_services, this);
                case "Hardware": return new HardwareViewModel(_services, this);
                case "Cameras": return new CamerasViewModel(_services);
                case "Storage": return new StorageViewModel(_services);
                case "Security": return GenericInventoryViewModel.Security(_services);
                case "Rules": return GenericInventoryViewModel.Rules(_services);
                case "SmartClient": return GenericInventoryViewModel.SmartClient(_services);
                case "Maintenance": return new MaintenanceViewModel(_services);
                case "Reports": return new ReportsViewModel(_services);
                case "Automation": return new AutomationViewModel(_services);
                case "CommandExplorer": return new CommandExplorerViewModel(_services);
                case "Activity": return new ActivityViewModel(_services, this);
                case "Audit": return new AuditViewModel(_services);
                case "Settings": return new SettingsViewModel(_services, this);
                case "AddHardware": return new AddHardwareViewModel(_services, this);
                case "CsvImport": return new CsvImportViewModel(_services);
                default: return null;
            }
        }

        private void OpenResult(SearchResult r)
        {
            if (r == null) return;
            Navigate(r.Page);
            switch (r.Target)
            {
                case CameraInfo c when Current is CamerasViewModel cv:
                    cv.Filter = c.Name;
                    break;
                case HardwareInfo h when Current is HardwareViewModel hv:
                    hv.Filter = h.Name;
                    break;
                case Domain.Catalog.CatalogCommand cmd when Current is CommandExplorerViewModel ev:
                    ev.SearchText = cmd.Name;
                    ev.Selected = ev.Commands.FirstOrDefault(x => x.Command == cmd);
                    break;
            }

            _searchText = string.Empty;
            Raise(nameof(SearchText));
            SearchResults.Clear();
            Raise(nameof(HasSearchResults));
        }

        public CapabilityStatus Capability(string id) =>
            _services.Session.Capabilities.FirstOrDefault(c => c.Definition.Id == id) ?? new CapabilityStatus { State = CapabilityState.Unavailable, Reason = "MilestonePSTools has not been detected yet." };
    }
}
