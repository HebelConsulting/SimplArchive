using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SimplArchive.DesktopClient.ViewModels;

// Tints a control light green when the bound bool is true, else leaves the themed default (issue #270: a
// positive "reachable + this is our server" cue on the tenant-manager URL field). ConverterParameter picks which
// brush — "bg" a light-green fill, "fg" a dark-green text colour that stays readable on that fill (so it works in
// both light and dark themes). False → UnsetValue, so the control keeps its theme brush.
public sealed class GreenTintConverter : IValueConverter
{
    public static readonly GreenTintConverter Instance = new();

    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(0xC8, 0xE6, 0xC9)); // green 100
    private static readonly IBrush Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20)); // green 900

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true)
        {
            return AvaloniaProperty.UnsetValue;
        }

        return parameter is "fg" ? Foreground : Background;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
