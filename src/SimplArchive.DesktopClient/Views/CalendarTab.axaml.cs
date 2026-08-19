using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// The Calendar tab's view (#564). Its own UserControl, like ContactsTab, rather than more of MainWindow.axaml.
public partial class CalendarTab : UserControl
{
    public CalendarTab() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Opens the structured editor for the selected appointment (ADR 0631). The window lives here rather than
    // in the view-model so the load and the save stay testable without a display.
    private void OnEdit(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not CalendarTabViewModel tab
            || tab.Selected is not { } row
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        // The row the user clicked, not the pane's loaded state (ADR 0559).
        if (await tab.LoadEntryAsync(row) is not { } loaded)
        {
            tab.StatusReporter?.Invoke(Strings.Get("ApptNotEditable"));
            return;
        }

        loaded.Value.CanEdit = loaded.CanEdit;
        if (await new AppointmentDialog(loaded.Value).ShowDialog<AppointmentEditViewModel?>(owner) is { } edited)
        {
            await tab.SaveEntryAsync(loaded, edited);
        }
    });
}
