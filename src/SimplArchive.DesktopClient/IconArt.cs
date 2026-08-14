using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SimplArchive.Theming;

namespace SimplArchive.DesktopClient;

// Draws the app icon — a filing cabinet on a rounded brand tile — as an Avalonia visual, so it can be
// rendered to PNG headlessly (no external image tools, no third-party art). See ADR "Desktop app icon".
//
// The colours come from the design tokens (ADR 0578) rather than being written here, so the launcher icon
// follows the brand instead of contradicting it. That mattered the moment a custom accent became possible:
// an application whose window is one colour and whose Dock icon is another looks unfinished, and the icon is
// the half nobody remembers to change.
internal static class IconArt
{
    /// <param name="accent">
    /// Which accent to draw in. Defaults to the shipped one; passing another is how a favicon is produced for
    /// every bundled style, so an operator who sets custom/theme.json to indigo can drop a matching tab icon
    /// beside it rather than keeping a teal one.
    /// </param>
    public static Control BuildTile(AccentTokens? accent = null)
    {
        const double size = 1024;
        accent ??= ThemeTokensReader.Shipped.Light.Accent;
        var brand = Color.Parse(accent.Primary);
        var brandLight = Color.Parse(AccentDerivation.Shade(accent.Primary, 0.10));

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
                Background = new SolidColorBrush(Color.Parse(AccentDerivation.Shade(accent.Tint, 0.02))),
                BorderBrush = new SolidColorBrush(Color.Parse(AccentDerivation.Shade(accent.Tint, -0.04))),
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
