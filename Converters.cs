using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SteamCardIdler.ViewModels;

namespace SteamCardIdler.Helpers
{
    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Returns Collapsed if string is null or empty, Visible otherwise.
    /// </summary>
    public class NullOrEmptyToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Highlights the active sort button based on CurrentSortMode.
    /// ConverterParameter: string name of the sort mode (e.g. "CardCountDesc")
    /// </summary>
    public class SortModeToBackgroundConverter : IValueConverter
    {
        private static readonly SolidColorBrush ActiveBrush   = new(Color.FromRgb(74, 144, 226));  // #4A90E2
        private static readonly SolidColorBrush InactiveBrush = new(Color.FromRgb(45, 50, 80));    // #2D3250

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is SortMode current && parameter is string paramStr)
            {
                if (Enum.TryParse<SortMode>(paramStr, out var target))
                    return current == target ? ActiveBrush : InactiveBrush;
            }
            return InactiveBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
