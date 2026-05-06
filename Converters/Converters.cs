using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ProcareDownloader.ViewModels;

namespace ProcareDownloader.Converters;

public class StateToVisibilityConverter : IValueConverter
{
    public AppState TargetState { get; set; }
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool match = value is AppState s && s == TargetState;
        if (Invert) match = !match;
        return match ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool bv && bv;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class SelectedBorderConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool selected = value is bool b && b;
        return selected
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(13, 148, 136))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class MultiStateVisibilityConverter : IValueConverter
{
    /// Comma-separated list of AppState names that should show Visible
    public string? States { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (States == null || value is not AppState current) return Visibility.Collapsed;
        var targets = States.Split(',').Select(s => Enum.Parse<AppState>(s.Trim()));
        return targets.Contains(current) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
