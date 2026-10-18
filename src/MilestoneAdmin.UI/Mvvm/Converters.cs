using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MilestoneAdmin.Application.Execution;
using MilestoneAdmin.Application.Services;
using MilestoneAdmin.Domain.Classification;

namespace MilestoneAdmin.UI.Mvvm
{
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var b = value is bool v && v;
            return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility v && (v == Visibility.Visible) ^ Invert;
    }

    public sealed class NullToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var has = value != null && !(value is string s && s.Length == 0);
            return has ^ Invert ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public sealed class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
    }

    /// <summary>Displays nullable booleans from the VMS as Yes / No / blank (unknown).</summary>
    public sealed class YesNoConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? (b ? "Yes" : "No") : string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public sealed class RiskToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var r = value is RiskLevel rl ? rl : RiskLevel.ReadOnly;
            switch (r)
            {
                case RiskLevel.Delete:
                case RiskLevel.HighImpact:
                    return Brush("#B3261E");
                case RiskLevel.Sensitive:
                    return Brush("#8E24AA");
                case RiskLevel.Create:
                case RiskLevel.Modify:
                case RiskLevel.Operational:
                case RiskLevel.LocalState:
                    return Brush("#B26A00");
                default:
                    return Brush("#2E7D32");
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

        private static SolidColorBrush Brush(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }

    public sealed class StatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string hex;
            switch (value)
            {
                case BulkItemStatus s:
                    hex = s == BulkItemStatus.Failed ? "#B3261E" : s == BulkItemStatus.Succeeded ? "#2E7D32" : s == BulkItemStatus.SucceededWithWarnings ? "#B26A00" : "#5F6B7A";
                    break;
                case CheckStatus c:
                    hex = c == CheckStatus.Failed ? "#B3261E" : c == CheckStatus.Passed ? "#2E7D32" : c == CheckStatus.Warning ? "#B26A00" : "#5F6B7A";
                    break;
                case bool b:
                    hex = b ? "#2E7D32" : "#B3261E";
                    break;
                default:
                    hex = "#5F6B7A";
                    break;
            }

            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
