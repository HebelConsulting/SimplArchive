using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// The share-scope dialog for a saved search (ADR "Scoped saved-search sharing"). Mutates the passed VM in place;
// ShowDialog<bool> returns true on Save (the caller reads back VM.Scope + VM.SelectedPrincipals), false on Cancel.
public partial class ShareSavedSearchDialog : Window
{
    public ShareSavedSearchDialog() : this(new ShareSavedSearchViewModel("", 0, []))
    {
    }

    public ShareSavedSearchDialog(ShareSavedSearchViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Pre-check the radio matching the initial scope.
        (viewModel.Scope switch { 1 => EveryoneRadio, 2 => SpecificRadio, _ => PrivateRadio }).IsChecked = true;
    }

    private void OnScopeChecked(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && DataContext is ShareSavedSearchViewModel vm && int.TryParse(tag, out var scope))
        {
            vm.Scope = scope;
        }
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
