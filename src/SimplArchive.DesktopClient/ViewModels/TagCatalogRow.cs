using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SimplArchive.DesktopClient.ViewModels;

// A catalog tag row on the Tags admin tab (ADR "Tag controlled vocabulary") — editable name + colour, plus a
// merge-target selection. The parent VM owns the save / retire / merge commands (taking the row as a parameter).
public sealed partial class TagCatalogRow : ObservableObject
{
    public TagCatalogRow(Services.SimplArchiveApiClient.TagCatalogItem source)
    {
        Source = source;
        Id = source.Id;
        Name = source.Name;
        Color = source.Color;
    }

    // The api-client row this was built from — it carries the addresses the catalog advertised, and the
    // client's methods take it rather than an id (ADR 0555).
    public Services.SimplArchiveApiClient.TagCatalogItem Source { get; }

    public System.Guid Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string? _color;
    [ObservableProperty] private TagCatalogRow? _mergeTarget;

    // The colour swatch (transparent when no colour is set).
    public IBrush SwatchBrush
    {
        get
        {
            try { return string.IsNullOrEmpty(Color) ? Brushes.Transparent : new SolidColorBrush(Avalonia.Media.Color.Parse(Color)); }
            catch { return Brushes.Transparent; }
        }
    }

    partial void OnColorChanged(string? value) => OnPropertyChanged(nameof(SwatchBrush));
}
