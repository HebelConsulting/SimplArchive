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

    // New contact (#631): the SAME dialog, opened empty. Not a second, smaller create form — one whose fields
    // are a subset of the editor's silently discards whatever the user typed into the others, and every field
    // added later has to be remembered in two places.
    //
    // Nothing exists until Save: the document is created FROM the filled-in form, so cancelling leaves nothing
    // behind. SimplCalCon creates a stub first and then edits it (its ADR 0082 defers the rich create), which
    // would mean a cancelled dialog leaving an empty contact — and a DAV client syncing meanwhile would have
    // taken a copy of it.
    private void OnNew(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not ContactsTabViewModel tab || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        // The addresses the server advertised. Empty means no ticked addressbook accepts a create, which the
        // disabled button already said — re-checked here because the ticks can change while the tab is open.
        if (tab.CreateTargets() is not { Count: > 0 } targets)
        {
            return;
        }

        var form = new ContactEditViewModel();
        form.OpenForCreate(targets);

        if (await new ContactDialog(form).ShowDialog<ContactEditViewModel?>(owner) is { } filled
            && filled.SelectedTarget is { } target)
        {
            await tab.CreateContactAsync(target, filled);
        }
    });

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

        // The raw source is fetched only if the user opens the disclosure (#648) — the lambda is how this
        // window reaches the api client without owning one.
        var dialog = new ContactDialog(loaded.Value) { RawLoader = () => tab.LoadRawAsync(loaded, loaded.Value) };
        if (await dialog.ShowDialog<ContactEditViewModel?>(owner) is { } edited)
        {
            await tab.SaveCardAsync(loaded, edited);
        }
    });
}
