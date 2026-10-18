using System.Collections.Specialized;
using System.Windows;

namespace MilestoneAdmin.UI
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            // Keep the newest activity line visible.
            ((INotifyCollectionChanged)ActivityList.Items).CollectionChanged += (s, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add && ActivityList.Items.Count > 0) ActivityList.ScrollIntoView(ActivityList.Items[ActivityList.Items.Count - 1]);
            };
        }
    }
}
