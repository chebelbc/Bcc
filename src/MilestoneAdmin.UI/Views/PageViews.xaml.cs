using System.Windows.Controls;
using MilestoneAdmin.UI.ViewModels;

namespace MilestoneAdmin.UI.Views
{
    // Page views contain no logic: everything is bound to their view models. Multi-select grids forward their
    // selection because DataGrid.SelectedItems is not bindable.

    public partial class SetupWizardView : UserControl { public SetupWizardView() { InitializeComponent(); } }

    public partial class DashboardView : UserControl { public DashboardView() { InitializeComponent(); } }

    public partial class ConnectView : UserControl { public ConnectView() { InitializeComponent(); } }

    public partial class SystemView : UserControl { public SystemView() { InitializeComponent(); } }

    public partial class RecordingServersView : UserControl { public RecordingServersView() { InitializeComponent(); } }

    public partial class QuickActionsPanel : UserControl { public QuickActionsPanel() { InitializeComponent(); } }

    public partial class StorageView : UserControl
    {
        public StorageView()
        {
            InitializeComponent();
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => (DataContext as StorageViewModel)?.SetSelection(ItemsGrid.SelectedItems);
    }

    public partial class GenericInventoryView : UserControl
    {
        public GenericInventoryView()
        {
            InitializeComponent();
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => (DataContext as GenericInventoryViewModel)?.SetSelection(ItemsGrid.SelectedItems);
    }

    public partial class MaintenanceView : UserControl { public MaintenanceView() { InitializeComponent(); } }

    public partial class ReportsView : UserControl { public ReportsView() { InitializeComponent(); } }

    public partial class AutomationView : UserControl { public AutomationView() { InitializeComponent(); } }

    public partial class CommandExplorerView : UserControl { public CommandExplorerView() { InitializeComponent(); } }

    public partial class ActivityView : UserControl { public ActivityView() { InitializeComponent(); } }

    public partial class AuditView : UserControl { public AuditView() { InitializeComponent(); } }

    public partial class SettingsView : UserControl { public SettingsView() { InitializeComponent(); } }

    public partial class AddHardwareView : UserControl { public AddHardwareView() { InitializeComponent(); } }

    public partial class CsvImportView : UserControl { public CsvImportView() { InitializeComponent(); } }

    public partial class HardwareView : UserControl
    {
        public HardwareView()
        {
            InitializeComponent();
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => (DataContext as HardwareViewModel)?.SetSelection(ItemsGrid.SelectedItems);
    }

    public partial class CamerasView : UserControl
    {
        public CamerasView()
        {
            InitializeComponent();
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => (DataContext as CamerasViewModel)?.SetSelection(ItemsGrid.SelectedItems);
    }
}
