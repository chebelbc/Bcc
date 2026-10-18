using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using MilestoneAdmin.Domain.Errors;
using MilestoneAdmin.UI.Views;
using Microsoft.Win32;

namespace MilestoneAdmin.UI.Services
{
    public interface IDialogService
    {
        bool Confirm(string title, string message);

        /// <summary>Requires the user to type <paramref name="phrase"/> exactly (destructive operations).</summary>
        bool ConfirmStrong(string title, string message, string phrase);

        void ShowError(AdminError error);

        void ShowInfo(string title, string message);

        string OpenFile(string filter);

        string SaveFile(string filter, string defaultName);

        void SaveText(string filter, string defaultName, string content);

        void CopyToClipboard(string text);
    }

    public sealed class DialogService : IDialogService
    {
        private static Window Owner => System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? System.Windows.Application.Current?.MainWindow;

        public bool Confirm(string title, string message) =>
            MessageBox.Show(Owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

        public bool ConfirmStrong(string title, string message, string phrase)
        {
            var w = new StrongConfirmWindow(title, message, phrase) { Owner = Owner };
            return w.ShowDialog() == true;
        }

        public void ShowError(AdminError error)
        {
            if (error == null) return;
            var w = new ErrorWindow(error) { Owner = Owner };
            w.ShowDialog();
        }

        public void ShowInfo(string title, string message) => MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

        public string OpenFile(string filter)
        {
            var d = new OpenFileDialog { Filter = filter, CheckFileExists = true };
            return d.ShowDialog(Owner) == true ? d.FileName : null;
        }

        public string SaveFile(string filter, string defaultName)
        {
            var d = new SaveFileDialog { Filter = filter, FileName = defaultName, OverwritePrompt = true };
            return d.ShowDialog(Owner) == true ? d.FileName : null;
        }

        public void SaveText(string filter, string defaultName, string content)
        {
            var path = SaveFile(filter, defaultName);
            if (path == null) return;
            // UTF-8 with BOM so spreadsheet applications detect the encoding of exported CSV files.
            File.WriteAllText(path, content, new UTF8Encoding(true));
        }

        public void CopyToClipboard(string text)
        {
            try
            {
                Clipboard.SetText(text ?? string.Empty);
            }
            catch (Exception)
            {
                // The clipboard can be locked by another process; ignore.
            }
        }
    }
}
