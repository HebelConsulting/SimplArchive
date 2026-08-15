using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// Create / rename / recolour a catalog tag (#530, tranche 6). ShowDialog<TagEditDialog.Result?> returns the
// entered name/colour (only the shown fields are meaningful), or null if cancelled. The caller does the API
// work via the view-model's existing row commands.
public partial class TagEditDialog : Window
{
    // Parameterless ctor so the Avalonia XAML runtime loader can reach this window (AVLN3001).
    public TagEditDialog() : this(Mode.Create) { }

    public enum Mode { Create, Rename, Recolour }

    public TagEditDialog(Mode mode, string? initialName = null, string? initialColor = null)
    {
        InitializeComponent();
        Title = Strings.Get(mode switch
        {
            Mode.Rename => "RibbonRename",
            Mode.Recolour => "TagsSetColour",
            _ => "TagsAdd",
        });
        NamePanel.IsVisible = mode is Mode.Create or Mode.Rename;
        ColorPanel.IsVisible = mode is Mode.Create or Mode.Recolour;
        NameBox.Text = initialName ?? string.Empty;
        ColorBox.Text = initialColor ?? string.Empty;
        _mode = mode;
        Opened += (_, _) => (NamePanel.IsVisible ? NameBox : (Control)ColorBox).Focus();
    }

    private readonly Mode _mode;

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? string.Empty).Trim();
        if (_mode is Mode.Create or Mode.Rename && name.Length == 0)
        {
            return;
        }

        Close(new Result(name, (ColorBox.Text ?? string.Empty).Trim()));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    public sealed record Result(string Name, string Color);
}
