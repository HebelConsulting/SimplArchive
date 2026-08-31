using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>The Search tab's body (#519 tranche 4), on the extracted <see cref="SearchTabViewModel"/>.</summary>
/// <remarks>
/// The four handlers moved here with the markup that raises them. They read the TAB's DataContext now, and
/// "open a result" goes through the view-model's command rather than the window: opening switches to the
/// Repositories tab, which is the shell's act, so the view-model asks for it via OpenResultRequested
/// (#517 tranche 2) and this control never reaches the window at all.
/// </remarks>
public partial class SearchTab : UserControl
{
    public SearchTab() => AvaloniaXamlLoader.Load(this);

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is SearchTabViewModel vm && vm.SearchCommand.CanExecute(null))
        {
            e.Handled = true;
            vm.SearchCommand.Execute(null);
        }
    }

    // Double-click a search result: switch to the Repositories tab and navigate to it.
    private void OnSearchResultDoubleTapped(object? sender, TappedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is SearchTabViewModel vm && vm.SelectedSearchResult is { } result)
        {
            await vm.OpenSearchResultCommand.ExecuteAsync(result);
        }
    });

    internal void OnSearchResultPreview(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SearchTabViewModel vm && (sender as Control)?.DataContext is SearchResultViewModel row)
        {
            vm.SelectedSearchResult = row;
        }
    }

    internal void OnSearchResultGoTo(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is SearchTabViewModel vm && (sender as Control)?.DataContext is SearchResultViewModel row)
        {
            await vm.OpenSearchResultCommand.ExecuteAsync(row);
        }
    });
}
