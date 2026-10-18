using System;
using System.Windows;
using System.Windows.Controls;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.UI.ViewModels;

namespace MilestoneAdmin.UI.Views
{
    public partial class ErrorBanner : UserControl
    {
        public ErrorBanner()
        {
            InitializeComponent();
        }
    }

    public partial class BusyOverlay : UserControl
    {
        public BusyOverlay()
        {
            InitializeComponent();
        }
    }

    public partial class ReviewPanel : UserControl
    {
        public ReviewPanel()
        {
            InitializeComponent();
        }
    }

    /// <summary>
    /// Passes PasswordBox contents to the view model as a SecureString (PasswordBox.SecurePassword returns a copy).
    /// The password is never exposed as a bindable string.
    /// </summary>
    public static class SecurePassword
    {
        public static readonly DependencyProperty SinkProperty =
            DependencyProperty.RegisterAttached("Sink", typeof(ISecretSink), typeof(SecurePassword), new PropertyMetadata(null, OnSinkChanged));

        public static ISecretSink GetSink(DependencyObject d) => (ISecretSink)d.GetValue(SinkProperty);

        public static void SetSink(DependencyObject d, ISecretSink value) => d.SetValue(SinkProperty, value);

        private static void OnSinkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is PasswordBox box)) return;
            box.PasswordChanged -= OnPasswordChanged;
            if (e.NewValue != null) box.PasswordChanged += OnPasswordChanged;
        }

        private static void OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            var box = (PasswordBox)sender;
            GetSink(box)?.SetSecret(box.SecurePassword);
        }
    }

    /// <summary>Chooses the input template for a generated Command Explorer field from its control kind.</summary>
    public sealed class FieldTemplateSelector : DataTemplateSelector
    {
        public DataTemplate Text { get; set; }
        public DataTemplate Checkbox { get; set; }
        public DataTemplate Toggle { get; set; }
        public DataTemplate Dropdown { get; set; }
        public DataTemplate MultiSelect { get; set; }
        public DataTemplate ObjectSelector { get; set; }
        public DataTemplate ObjectMultiSelector { get; set; }
        public DataTemplate Credential { get; set; }
        public DataTemplate Secret { get; set; }
        public DataTemplate Date { get; set; }
        public DataTemplate List { get; set; }
        public DataTemplate Unsupported { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (!(item is FieldViewModel f)) return base.SelectTemplate(item, container);
            switch (f.Control)
            {
                case ControlKind.Checkbox: return Checkbox;
                case ControlKind.Toggle: return Toggle;
                case ControlKind.Dropdown: return Dropdown;
                case ControlKind.MultiSelect: return MultiSelect;
                case ControlKind.ObjectSelector: return ObjectSelector;
                case ControlKind.ObjectMultiSelector: return ObjectMultiSelector;
                case ControlKind.Credential: return Credential;
                case ControlKind.SecureText: return Secret;
                case ControlKind.DateTime: return Date;
                case ControlKind.TextList:
                case ControlKind.IntegerList:
                case ControlKind.GuidList:
                    return List;
                case ControlKind.Unsupported:
                case ControlKind.KeyValue:
                    return Unsupported;
                default:
                    return Text;
            }
        }
    }
}
