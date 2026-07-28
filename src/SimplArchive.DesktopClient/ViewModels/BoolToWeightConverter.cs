using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SimplArchive.DesktopClient.ViewModels;

// Bold when true, else normal — used to emphasise the selected search-facet button (ADR "Search facets").
public sealed class BoolToWeightConverter : IValueConverter
{
    public static readonly BoolToWeightConverter Instance = new();

    // ConverterParameter="invert" flips the meaning (e.g. an unread = !IsRead notification shows bold).
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var bold = value is true;
        if (parameter is "invert")
        {
            bold = !bold;
        }

        return bold ? FontWeight.Bold : FontWeight.Normal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
