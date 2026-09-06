using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// The Repositories tab's preview-pane collapse (its own partial — the shell file is an over-limit debt file,
// so new concerns get a home of their own). Same shape as the tree/list/chat collapses beside it: a saved
// width, a caret, a toggle that hands the row to the chat pane (both columns are star-sized, so a 0-width
// preview lets chat absorb the space with no extra logic), persisted through LayoutSettings.
public partial class MainWindowViewModel
{
    [ObservableProperty] private GridLength _previewWidth = new(3, GridUnitType.Star);
    [ObservableProperty] private bool _previewCollapsed;

    private GridLength _previewSaved = new(3, GridUnitType.Star);
    private const double DefaultPreview = 3;

    // A LEFT pane in its row (chat sits to its right): mirror TreeCaret, not the Checkout up/down glyph.
    public string PreviewPaneCaret => PreviewCollapsed ? "mdi-chevron-right" : "mdi-chevron-left";

    partial void OnPreviewCollapsedChanged(bool value) => OnPropertyChanged(nameof(PreviewPaneCaret));

    [RelayCommand]
    private void TogglePreviewPane()
    {
        if (PreviewCollapsed) { PreviewWidth = _previewSaved; PreviewCollapsed = false; }
        else { _previewSaved = PreviewWidth; PreviewWidth = new GridLength(0); PreviewCollapsed = true; }
        SaveLayout();
    }

    private void LoadPreviewLayout(LayoutSettings settings)
    {
        _previewSaved = GridLengths.ParseOrStar(settings.PreviewWidth, DefaultPreview);
        PreviewCollapsed = settings.PreviewCollapsed;
        PreviewWidth = PreviewCollapsed ? new GridLength(0) : _previewSaved;
    }

    private void WritePreviewLayout(LayoutSettings settings)
    {
        settings.PreviewWidth = (PreviewCollapsed ? _previewSaved : PreviewWidth).ToString();
        settings.PreviewCollapsed = PreviewCollapsed;
    }

    private void ResetPreviewLayout()
    {
        _previewSaved = new GridLength(DefaultPreview, GridUnitType.Star);
        PreviewCollapsed = false;
        PreviewWidth = _previewSaved;
    }
}
