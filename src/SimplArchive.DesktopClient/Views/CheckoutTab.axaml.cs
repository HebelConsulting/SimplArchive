using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>The Check-out tab's body (#519 tranche 3), on the extracted <see cref="CheckoutTabViewModel"/>.</summary>
public partial class CheckoutTab : UserControl
{
    public CheckoutTab() => AvaloniaXamlLoader.Load(this);

    // The OCR-language picker (#999) — the DocumentDetailPane's OnEditOcrLanguages pattern, except the
    // picker's OK COMMITS immediately: this pane has no edit mode, so there is no staged save to ride.
    private void OnEditOcrLanguages(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not CheckoutTabViewModel vm || TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        var (catalog, selected) = vm.OcrPickerState();
        if (catalog.Count == 0)
        {
            return;
        }

        var picker = new OcrLanguagePickerViewModel(catalog, selected);
        var codes = await new OcrLanguagePickerDialog { DataContext = picker }.ShowDialog<System.Collections.Generic.List<string>?>(window);
        if (codes is not null)
        {
            await vm.SetOcrLanguagesAsync(codes);
        }
    });

    // Moved with the markup: the named ListBox is in THIS control's namescope now, so the window can no longer
    // reach it. Reads the tab's own DataContext rather than the window's.
    private void OnCheckoutSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is CheckoutTabViewModel vm && sender is ListBox list)
        {
            vm.SetSelection(list.SelectedItems?.OfType<CheckoutRowViewModel>().ToList() ?? []);
        }
    }
}
