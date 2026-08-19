using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// The Contacts tab's view (#564). Its own UserControl rather than more of MainWindow.axaml, which is already
// over the 1000-line limit — the direction issue #519 wants, and the same shape the per-tab ribbons use.
public partial class ContactsTab : UserControl
{
    public ContactsTab() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Opens the structured editor for the selected contact (ADR 0631). The window lives here rather than in
    // the view-model so the load and the save stay testable without a display — the desktop suite drives them
    // at view-model level, and a VM that constructed a Window could not be exercised that way.
    private void OnEdit(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not ContactsTabViewModel tab
            || tab.Selected is not { } row
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        // The row the user clicked, not the pane's loaded state (ADR 0559).
        if (await tab.LoadCardAsync(row) is not { } loaded)
        {
            tab.StatusReporter?.Invoke(Strings.Get("ContactNotEditable"));
            return;
        }

        loaded.Value.CanEdit = loaded.CanEdit;
        if (await new ContactDialog(loaded.Value).ShowDialog<ContactEditViewModel?>(owner) is { } edited)
        {
            await tab.SaveCardAsync(loaded, edited);
        }
    });
}
