using System;
using System.Windows;
using MilestoneAdmin.Domain.Errors;

namespace MilestoneAdmin.UI.Views
{
    public partial class ErrorWindow : Window
    {
        private readonly AdminError _error;

        public ErrorWindow(AdminError error)
        {
            InitializeComponent();
            _error = error;
            DataContext = error;
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_error.ToReport());
            }
            catch (Exception)
            {
                // clipboard busy
            }
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }

    /// <summary>Destructive-operation confirmation: the user must type the given phrase exactly.</summary>
    public partial class StrongConfirmWindow : Window
    {
        private readonly string _phrase;

        public StrongConfirmWindow(string title, string message, string phrase)
        {
            InitializeComponent();
            _phrase = phrase;
            Title = title;
            Heading.Text = title;
            Message.Text = message;
            Prompt.Text = $"Type {phrase} to confirm:";
            Loaded += (s, e) => Answer.Focus();
        }

        private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
            ConfirmButton.IsEnabled = string.Equals(Answer.Text.Trim(), _phrase, StringComparison.Ordinal);

        private void OnConfirm(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
