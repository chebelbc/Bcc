using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.UI.Mvvm;
using MilestoneAdmin.UI.ViewModels;

namespace MilestoneAdmin.UI
{
    public partial class App : System.Windows.Application
    {
        private AppServices _services;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            _services = new AppServices();
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += (s, a) =>
            {
                _services.Diagnostics.Write("Unobserved task exception: " + a.Exception);
                a.SetObserved();
            };
            AsyncCommand.UnhandledError = ShowUnexpected;
            var window = new MainWindow { DataContext = new ShellViewModel(_services) };
            MainWindow = window;
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // Stop the worker cleanly (bounded wait); the worker also exits on its own when its stdin closes.
                Task.Run(() => _services.Connection.DisconnectAsync()).Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                _services.Diagnostics.Write("Shutdown: " + ex.Message);
            }

            base.OnExit(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            ShowUnexpected(e.Exception);
            e.Handled = true;
        }

        private void ShowUnexpected(Exception ex)
        {
            _services?.Diagnostics.Write("Unexpected error: " + ex);
            _services?.Dialogs.ShowError(ErrorTranslator.FromException(ex, new OperationContext("complete the action")));
        }
    }
}
