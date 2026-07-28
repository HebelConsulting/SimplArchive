using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SimplArchive.DesktopClient;

// Draws the app icon — a filing cabinet on a rounded brand-purple tile — as an Avalonia visual, so it can be
// rendered to PNG headlessly (no external image tools, no third-party art). See ADR "Desktop app icon".
internal static class IconArt
{
    public static Control BuildTile()
    {
        const double size = 1024;
        var brand = Color.Parse("#5b4ee5");
        var brandLight = Color.Parse("#7d70f6");

        var tile = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(224),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(brandLight, 0),
                    new GradientStop(brand, 1),
                },
            },
        };

        var drawers = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 30,
            VerticalAlignment = VerticalAlignment.Center,
        };

        for (var i = 0; i < 3; i++)
        {
            drawers.Children.Add(new Border
            {
                Height = 150,
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(Color.Parse("#f3f2fb")),
                BorderBrush = new SolidColorBrush(Color.Parse("#e2e0f3")),
                BorderThickness = new Thickness(3),
                Child = new Border
                {
                    Width = 150,
                    Height = 30,
                    CornerRadius = new CornerRadius(15),
                    Background = new SolidColorBrush(brand),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }

        var body = new Border
        {
            Width = 560,
            Height = 660,
            CornerRadius = new CornerRadius(48),
            Background = Brushes.White,
            Padding = new Thickness(40),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 22, Blur = 55, Color = Color.FromArgb(70, 0, 0, 0) }),
            Child = drawers,
        };

        return new Grid { Width = size, Height = size, Children = { tile, body } };
    }
}
